using System.Diagnostics;
using System.Text.Json;

namespace MedicalApp.Services
{
    /// <summary>
    /// Per-request stopwatch for the interpretation pipeline. Answers the only
    /// question that matters when an interpretation takes minutes: WHICH stage
    /// took them. Timings are logged and persisted on the history row so the
    /// Admin panel can show them without any extra plumbing.
    ///
    /// Not thread-safe on purpose: one instance per request.
    /// </summary>
    public sealed class StageTimer
    {
        private readonly Stopwatch _total = Stopwatch.StartNew();
        private readonly Dictionary<string, long> _stages = new();

        /// <summary>Times an async stage and returns its result.</summary>
        public async Task<T> MeasureAsync<T>(string stage, Func<Task<T>> action)
        {
            var sw = Stopwatch.StartNew();
            try { return await action(); }
            finally { Add(stage, sw.ElapsedMilliseconds); }
        }

        /// <summary>Times an async stage with no result.</summary>
        public async Task MeasureAsync(string stage, Func<Task> action)
        {
            var sw = Stopwatch.StartNew();
            try { await action(); }
            finally { Add(stage, sw.ElapsedMilliseconds); }
        }

        /// <summary>Times a synchronous stage and returns its result.</summary>
        public T Measure<T>(string stage, Func<T> action)
        {
            var sw = Stopwatch.StartNew();
            try { return action(); }
            finally { Add(stage, sw.ElapsedMilliseconds); }
        }

        /// <summary>Adds elapsed milliseconds to a stage (repeat calls accumulate,
        /// so Gemini retries show up as total time spent, not just the last try).</summary>
        public void Add(string stage, long ms)
        {
            _stages[stage] = _stages.TryGetValue(stage, out var prev) ? prev + ms : ms;
        }

        public long TotalMs => _total.ElapsedMilliseconds;

        /// <summary>Compact JSON persisted on InterpretationHistory.StageTimingsJson.</summary>
        public string ToJson()
        {
            var payload = new Dictionary<string, long>(_stages) { ["total"] = TotalMs };
            return JsonSerializer.Serialize(payload);
        }

        /// <summary>Human-readable one-liner for the log: "pdf_extract=1204ms gemini=78210ms ...".</summary>
        public override string ToString() =>
            string.Join(" ", _stages.OrderByDescending(k => k.Value).Select(k => $"{k.Key}={k.Value}ms"))
            + $" TOTAL={TotalMs}ms";

        /// <summary>Parses a persisted timings JSON back into a dictionary (Admin view).</summary>
        public static Dictionary<string, long> Parse(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return new();
            try
            {
                return JsonSerializer.Deserialize<Dictionary<string, long>>(json!) ?? new();
            }
            catch
            {
                return new();
            }
        }
    }
}
