namespace MedicalApp.Services
{
    /// <summary>
    /// Singleton, in-memory snapshot of the LOINC Python microservice health,
    /// refreshed periodically by <see cref="LoincHealthMonitor"/>.
    ///
    /// Why a cache? Every user request that touches the admin dashboard used to
    /// issue a live HTTP GET http://localhost:8000/ready with a 2s timeout. When
    /// the Python process was down, that 2s wait blocked the UI thread. Now the
    /// controller reads this cached snapshot instead — 0 ms, non-blocking.
    ///
    /// Thread safety: implementations MUST be safe for concurrent readers and a
    /// single writer (the background monitor). Fields should be volatile or set
    /// atomically.
    /// </summary>
    public interface ILoincHealthState
    {
        /// <summary>true when the last background probe succeeded within timeout.</summary>
        bool IsUp { get; }

        /// <summary>Short status label: "ok", "timeout", "down", "starting", "disabled", "unknown".</summary>
        string Status { get; }

        /// <summary>Optional human-readable error message from the last failed probe.</summary>
        string? Message { get; }

        /// <summary>Latency of the last probe in milliseconds (0 if none yet).</summary>
        long LatencyMs { get; }

        /// <summary>loinc_count parsed from the /ready payload, if present.</summary>
        int? LoincCount { get; }

        /// <summary>UTC timestamp of the last completed probe (null before the first run).</summary>
        DateTime? LastProbeUtc { get; }

        /// <summary>Base URL that was probed (echoed to callers so the UI stays informative).</summary>
        string BaseUrl { get; }

        /// <summary>Atomic snapshot update, called by the monitor after each probe.</summary>
        void Update(bool isUp, string status, string? message, long latencyMs, int? loincCount, string baseUrl);
    }

    /// <summary>Default in-memory implementation. Registered as Singleton in Program.cs.</summary>
    public sealed class LoincHealthState : ILoincHealthState
    {
        private readonly object _lock = new();
        private volatile bool _isUp;
        private string _status = "unknown";
        private string? _message;
        private long _latencyMs;
        private int? _loincCount;
        private DateTime? _lastProbeUtc;
        private string _baseUrl = string.Empty;

        public bool IsUp => _isUp;
        public string Status { get { lock (_lock) return _status; } }
        public string? Message { get { lock (_lock) return _message; } }
        public long LatencyMs { get { lock (_lock) return _latencyMs; } }
        public int? LoincCount { get { lock (_lock) return _loincCount; } }
        public DateTime? LastProbeUtc { get { lock (_lock) return _lastProbeUtc; } }
        public string BaseUrl { get { lock (_lock) return _baseUrl; } }

        public void Update(bool isUp, string status, string? message, long latencyMs, int? loincCount, string baseUrl)
        {
            lock (_lock)
            {
                _isUp = isUp;
                _status = status;
                _message = message;
                _latencyMs = latencyMs;
                _loincCount = loincCount;
                _lastProbeUtc = DateTime.UtcNow;
                _baseUrl = baseUrl;
            }
        }
    }
}
