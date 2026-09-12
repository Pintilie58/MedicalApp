using System.Collections.Concurrent;
using System.Text.Json;
using MedicalApp.Models;
using Microsoft.Extensions.Caching.Distributed;

namespace MedicalApp.Services
{
    /// <summary>
    /// Live progress of an interpretation, so the upload screen can fill itself
    /// in section by section as the pipeline produces results, instead of
    /// staring at a spinner for ~150 s:
    ///
    ///   stage A done (~40 s)  → patient data + the analyte table + what is out of range
    ///   stage C done (~70 s)  → summary and recommendations
    ///   local passes done     → the table and the findings are refreshed with
    ///                           the corrections made by StatusValidator /
    ///                           AbnormalFindingsCompleter
    ///   done                  → the browser is redirected to the full report
    ///
    /// Throw-away UI state, tied to one upload, with a short TTL. Nothing here
    /// is ever the source of truth for a report (that is the archive row).
    ///
    /// SaaS / scale-out note: the state is kept in memory for the instance that
    /// runs the job AND written through to <see cref="IDistributedCache"/>, which
    /// in production is the SQL Server cache table. Without that, a poll that the
    /// load balancer sends to another instance would answer "no progress" and the
    /// overlay would look frozen. No new table and no schema change: it reuses
    /// the cache table the scale-out configuration already creates.
    /// </summary>
    public class InterpretationProgressTracker
    {
        private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(20);
        private const string CachePrefix = "iprog:";

        private readonly ConcurrentDictionary<string, ProgressState> _states = new();
        private readonly IDistributedCache _cache;
        private readonly ILogger<InterpretationProgressTracker> _logger;

        public InterpretationProgressTracker(
            IDistributedCache cache,
            ILogger<InterpretationProgressTracker> logger)
        {
            _cache = cache;
            _logger = logger;
        }

        public sealed class PartialAnalyte
        {
            public string Parameter { get; set; } = "";
            public string? Value { get; set; }
            public string? Unit { get; set; }
            public string? ReferenceRange { get; set; }
            public string? Status { get; set; }
        }

        /// <summary>Who the report belongs to, as read from the PDF itself.</summary>
        public sealed class PartialPatient
        {
            public string? Name { get; set; }
            public string? Age { get; set; }
            public string? Sex { get; set; }
            public string? DateTaken { get; set; }
            public string? Laboratory { get; set; }
        }

        public sealed class PartialFinding
        {
            public string Parameter { get; set; } = "";
            public string? Severity { get; set; }
        }

        public sealed class ProgressState
        {
            /// <summary>Stage key: upload | pdf_extract | ai_extract | ai_explain | loinc_match | pdf_report | done | error.</summary>
            public string Stage { get; set; } = "upload";
            public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
            public List<PartialAnalyte>? Table { get; set; }
            public int OutOfRangeCount { get; set; }
            public string? Error { get; set; }

            // Progressive sections, each one published the moment it exists.
            public PartialPatient? Patient { get; set; }
            public string? Summary { get; set; }
            public string? Recommendations { get; set; }
            public List<PartialFinding>? Findings { get; set; }

            /// <summary>Where the browser should go once the background job is
            /// finished. Set only on the "done" stage.</summary>
            public string? RedirectUrl { get; set; }
            public int? HistoryId { get; set; }
        }

        /// <summary>Job finished successfully — hands the browser its destination.</summary>
        public void Done(string? token, string redirectUrl, int historyId)
        {
            if (string.IsNullOrWhiteSpace(token)) return;
            var state = _states.GetOrAdd(token!, _ => new ProgressState());
            state.Stage = "done";
            state.RedirectUrl = redirectUrl;
            state.HistoryId = historyId;
            Touch(token!, state);
        }

        public void SetStage(string? token, string stage)
        {
            if (string.IsNullOrWhiteSpace(token)) return;
            Cleanup();
            var state = _states.GetOrAdd(token!, _ => new ProgressState());
            state.Stage = stage;
            Touch(token!, state);
        }

        /// <summary>
        /// Publishes the extracted table so the browser can render it immediately
        /// and moves the pipeline indicator to the explanation stage.
        /// </summary>
        public void SetTable(string? token, IEnumerable<KeyResult> analytes)
        {
            if (string.IsNullOrWhiteSpace(token)) return;
            Cleanup();
            var state = _states.GetOrAdd(token!, _ => new ProgressState());
            FillTable(state, analytes);
            state.Stage = "ai_explain";
            Touch(token!, state);
        }

        /// <summary>
        /// Same table, WITHOUT touching the stage — used after the local
        /// verification passes corrected some statuses, so the preliminary rows
        /// stop contradicting the final report.
        /// </summary>
        public void UpdateTable(string? token, IEnumerable<KeyResult> analytes)
        {
            if (string.IsNullOrWhiteSpace(token)) return;
            var state = _states.GetOrAdd(token!, _ => new ProgressState());
            FillTable(state, analytes);
            Touch(token!, state);
        }

        /// <summary>Who the analyses belong to, as extracted from the PDF.</summary>
        public void SetPatient(string? token, PatientInfo? info)
        {
            if (string.IsNullOrWhiteSpace(token) || info == null) return;
            var state = _states.GetOrAdd(token!, _ => new ProgressState());
            state.Patient = new PartialPatient
            {
                Name = Clean(info.Name),
                Age = Clean(info.Age),
                Sex = Clean(info.Sex),
                DateTaken = Clean(info.DateTaken),
                Laboratory = Clean(info.Laboratory)
            };
            Touch(token!, state);
        }

        /// <summary>
        /// The narrative part: summary, recommendations and the list of abnormal
        /// findings. Published as soon as the narrative stage answers, which is
        /// well before the PDF and the email are done.
        /// </summary>
        public void SetNarrative(string? token, InterpretationResult? result)
        {
            if (string.IsNullOrWhiteSpace(token) || result == null) return;
            var state = _states.GetOrAdd(token!, _ => new ProgressState());

            if (!string.IsNullOrWhiteSpace(result.Summary)) state.Summary = result.Summary!.Trim();
            if (!string.IsNullOrWhiteSpace(result.Recommendations))
                state.Recommendations = result.Recommendations!.Trim();

            if (result.AbnormalFindings is { Count: > 0 })
                state.Findings = result.AbnormalFindings
                    .Where(f => !string.IsNullOrWhiteSpace(f.Parameter))
                    .Select(f => new PartialFinding
                    {
                        Parameter = f.Parameter.Trim(),
                        Severity = (f.Severity ?? "").Trim().ToLowerInvariant()
                    })
                    .ToList();

            Touch(token!, state);
        }

        public void Fail(string? token, string message)
        {
            if (string.IsNullOrWhiteSpace(token)) return;
            var state = _states.GetOrAdd(token!, _ => new ProgressState());
            state.Stage = "error";
            state.Error = message;
            Touch(token!, state);
        }

        /// <summary>
        /// Local state wins (it is the live object). If this instance never saw
        /// the token, the job belongs to another instance — read the shared copy
        /// every time instead of caching a snapshot that would freeze the UI.
        /// </summary>
        public ProgressState? Get(string? token)
        {
            if (string.IsNullOrWhiteSpace(token)) return null;
            if (_states.TryGetValue(token!, out var local)) return local;

            try
            {
                var json = _cache.GetString(CachePrefix + token);
                return string.IsNullOrEmpty(json)
                    ? null
                    : JsonSerializer.Deserialize<ProgressState>(json);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Progress tracker: shared read failed for token {Token}.", token);
                return null;
            }
        }

        private static void FillTable(ProgressState state, IEnumerable<KeyResult> analytes)
        {
            state.Table = analytes
                .Where(k => !string.IsNullOrWhiteSpace(k.Parameter))
                .Select(k => new PartialAnalyte
                {
                    Parameter = k.Parameter.Trim(),
                    Value = k.Value,
                    Unit = k.Unit,
                    ReferenceRange = k.ReferenceRange,
                    Status = (k.Status ?? "").Trim().ToLowerInvariant()
                })
                .ToList();

            state.OutOfRangeCount = state.Table.Count(t => t.Status is "high" or "low" or "borderline");
        }

        private static string? Clean(string? s) =>
            string.IsNullOrWhiteSpace(s) ? null : s.Trim();

        /// <summary>
        /// Stamps the state and shares it with the other instances. Failing to
        /// share must never break an interpretation — the local copy still works
        /// for the instance that runs the job.
        /// </summary>
        private void Touch(string token, ProgressState state)
        {
            state.UpdatedUtc = DateTime.UtcNow;
            try
            {
                _cache.SetString(CachePrefix + token,
                    JsonSerializer.Serialize(state),
                    new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = Ttl });
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Progress tracker: shared write failed for token {Token}.", token);
            }
        }

        private void Cleanup()
        {
            if (_states.Count < 50) return;
            var cutoff = DateTime.UtcNow - Ttl;
            foreach (var kv in _states)
                if (kv.Value.UpdatedUtc < cutoff)
                    _states.TryRemove(kv.Key, out _);
        }
    }
}
