using System.Collections.Concurrent;

namespace MedicalApp.Services
{
    /// <summary>
    /// In-memory progress tracker for the CAM batch runner. Lives only in
    /// process memory — if the app restarts, any "Running" batch in DB is
    /// flipped to "Failed" on startup (see d)i decision: no auto-resume).
    ///
    /// The Progress page polls <see cref="CamBatchService.GetProgress"/> via
    /// AJAX every 3s and reads the snapshot stored here.
    /// </summary>
    public class CamBatchProgress
    {
        public int BatchRunId { get; set; }
        public int ClinicId { get; set; }
        public int Total { get; set; }
        public int Processed { get; set; }
        public int Sent { get; set; }
        public int Compared { get; set; }
        public int NotSends { get; set; }
        public string CurrentFile { get; set; } = string.Empty;
        public string Status { get; set; } = "Running"; // Running / Completed / Cancelled / Failed
        public DateTime StartedAt { get; set; } = DateTime.UtcNow;
        public DateTime? FinishedAt { get; set; }
        public List<string> RecentLog { get; set; } = new();
        public CancellationTokenSource Cts { get; set; } = new();

        public void Log(string message)
        {
            lock (RecentLog)
            {
                var stamped = $"{DateTime.Now:HH:mm:ss}  {message}";
                RecentLog.Add(stamped);
                // Keep only the last 30 lines in memory.
                if (RecentLog.Count > 30)
                    RecentLog.RemoveRange(0, RecentLog.Count - 30);
            }
        }

        public List<string> LogSnapshot()
        {
            lock (RecentLog) return RecentLog.ToList();
        }
    }

    /// <summary>
    /// Process-wide registry of active batch runs, keyed by batch run id.
    /// Singleton — survives between requests within the same process.
    /// </summary>
    public class CamBatchRegistry
    {
        private readonly ConcurrentDictionary<int, CamBatchProgress> _store = new();

        public CamBatchProgress GetOrCreate(int batchRunId, int clinicId, int total)
        {
            return _store.GetOrAdd(batchRunId, _ => new CamBatchProgress
            {
                BatchRunId = batchRunId,
                ClinicId = clinicId,
                Total = total
            });
        }

        public CamBatchProgress? Get(int batchRunId) =>
            _store.TryGetValue(batchRunId, out var p) ? p : null;

        public bool HasRunningForClinic(int clinicId) =>
            _store.Values.Any(p => p.ClinicId == clinicId && p.Status == "Running");

        public void Remove(int batchRunId) => _store.TryRemove(batchRunId, out _);
    }
}
