using System.Collections.Concurrent;
using MedicalApp.Models;

namespace MedicalApp.Services
{
    /// <summary>
    /// Live progress of an interpretation, so the upload screen can show what is
    /// happening instead of a blank spinner — and, most importantly, show the
    /// TABLE OF RESULTS as soon as the extraction stage finishes (~40s) rather
    /// than after the whole pipeline (~150s).
    ///
    /// In-memory on purpose: it is throw-away UI state, tied to one upload, with
    /// a short TTL. Nothing here is ever the source of truth for a report.
    /// </summary>
    public class InterpretationProgressTracker
    {
        private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(20);
        private readonly ConcurrentDictionary<string, ProgressState> _states = new();

        public sealed class PartialAnalyte
        {
            public string Parameter { get; set; } = "";
            public string? Value { get; set; }
            public string? Unit { get; set; }
            public string? ReferenceRange { get; set; }
            public string? Status { get; set; }
        }

        public sealed class ProgressState
        {
            /// <summary>Stage key: upload | pdf_extract | ai_extract | ai_explain | loinc_match | pdf_report | done | error.</summary>
            public string Stage { get; set; } = "upload";
            public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
            public List<PartialAnalyte>? Table { get; set; }
            public int OutOfRangeCount { get; set; }
            public string? Error { get; set; }

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
            state.UpdatedUtc = DateTime.UtcNow;
        }

        public void SetStage(string? token, string stage)
        {
            if (string.IsNullOrWhiteSpace(token)) return;
            Cleanup();
            var state = _states.GetOrAdd(token!, _ => new ProgressState());
            state.Stage = stage;
            state.UpdatedUtc = DateTime.UtcNow;
        }

        /// <summary>Publishes the extracted table so the browser can render it immediately.</summary>
        public void SetTable(string? token, IEnumerable<KeyResult> analytes)
        {
            if (string.IsNullOrWhiteSpace(token)) return;
            Cleanup();
            var state = _states.GetOrAdd(token!, _ => new ProgressState());

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
            state.Stage = "ai_explain";
            state.UpdatedUtc = DateTime.UtcNow;
        }

        public void Fail(string? token, string message)
        {
            if (string.IsNullOrWhiteSpace(token)) return;
            var state = _states.GetOrAdd(token!, _ => new ProgressState());
            state.Stage = "error";
            state.Error = message;
            state.UpdatedUtc = DateTime.UtcNow;
        }

        public ProgressState? Get(string? token) =>
            !string.IsNullOrWhiteSpace(token) && _states.TryGetValue(token!, out var s) ? s : null;

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
