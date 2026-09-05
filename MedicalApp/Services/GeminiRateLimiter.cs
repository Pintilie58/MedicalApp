using System.Diagnostics;
using Microsoft.Extensions.Options;

namespace MedicalApp.Services
{
    /// <summary>
    /// Keeps the app inside the Gemini quota INSTEAD of discovering it through
    /// HTTP 429 errors.
    ///
    /// Google limits how many requests per minute a project may send. Until now
    /// nothing in the app knew that number: raising the interpretation
    /// concurrency from 3 to 15 would have produced failed interpretations, not
    /// faster ones. This limiter does three things, all of them cheap:
    ///
    ///   1. a sliding one-minute window — the caller waits instead of being
    ///      refused ("queue at the counter" rather than "door in the face");
    ///   2. a ceiling on simultaneous calls in flight;
    ///   3. a shared cool-down when Google DOES answer 429: every caller pauses
    ///      for the duration Google asked for (Retry-After), so we stop
    ///      stampeding a quota that is already exhausted.
    ///
    /// Defaults are deliberately generous (60 requests/minute, 6 in flight) so
    /// current behaviour is unchanged; set them to your real Google quota.
    /// KILL SWITCH: "Gemini:RateLimit:Enabled" = false.
    /// </summary>
    public sealed class GeminiRateLimiter
    {
        private readonly IOptionsMonitor<GeminiSettings> _options;
        private readonly ILogger<GeminiRateLimiter> _logger;

        private readonly object _sync = new();
        private readonly Queue<long> _recentCalls = new();   // Stopwatch ticks
        private SemaphoreSlim? _inFlight;
        private int _inFlightCeiling;
        private long _cooldownUntilTicks;

        private long _totalCalls;
        private long _throttledCalls;
        private long _totalWaitMs;
        private long _rejections;

        public GeminiRateLimiter(IOptionsMonitor<GeminiSettings> options, ILogger<GeminiRateLimiter> logger)
        {
            _options = options;
            _logger = logger;
        }

        private GeminiRateLimitSettings Config => _options.CurrentValue.RateLimit ?? new GeminiRateLimitSettings();

        /// <summary>Snapshot for logs and the admin page.</summary>
        public (long Calls, long Throttled, long WaitMs, long Rejections, int InLastMinute, bool CoolingDown) Stats()
        {
            lock (_sync)
            {
                Trim();
                return (_totalCalls, _throttledCalls, _totalWaitMs, _rejections,
                        _recentCalls.Count, Stopwatch.GetTimestamp() < _cooldownUntilTicks);
            }
        }

        /// <summary>
        /// Call right before sending a request. Returns a lease that MUST be
        /// disposed when the call finishes, so the in-flight slot is freed.
        /// </summary>
        public async Task<IDisposable> AcquireAsync(CancellationToken ct)
        {
            var cfg = Config;
            if (!cfg.Enabled) return NullLease.Instance;

            var ceiling = Math.Max(1, cfg.MaxConcurrentCalls);
            var gate = EnsureGate(ceiling);
            await gate.WaitAsync(ct);

            var waited = 0L;
            try
            {
                while (true)
                {
                    TimeSpan wait;
                    lock (_sync)
                    {
                        wait = ComputeWait(cfg);
                        if (wait <= TimeSpan.Zero)
                        {
                            _recentCalls.Enqueue(Stopwatch.GetTimestamp());
                            _totalCalls++;
                            if (waited > 0)
                            {
                                _throttledCalls++;
                                _totalWaitMs += waited;
                            }
                            break;
                        }
                    }

                    if (waited == 0)
                        _logger.LogInformation(
                            "Gemini rate limit: waiting {Wait:F1}s before the next call (quota {Rpm}/min).",
                            wait.TotalSeconds, cfg.RequestsPerMinute);

                    await Task.Delay(wait, ct);
                    waited += (long)wait.TotalMilliseconds;
                }
            }
            catch
            {
                gate.Release();
                throw;
            }

            return new Lease(gate);
        }

        /// <summary>
        /// Google answered 429/503. Everyone pauses for the requested delay
        /// (or the configured default), instead of hammering the quota.
        /// </summary>
        public void NoteRejected(int statusCode, TimeSpan? retryAfter)
        {
            var cfg = Config;
            if (!cfg.Enabled) return;

            var pause = retryAfter is { TotalSeconds: > 0 }
                ? retryAfter.Value
                : TimeSpan.FromSeconds(Math.Max(1, cfg.CooldownSecondsOnReject));
            if (pause > TimeSpan.FromMinutes(5)) pause = TimeSpan.FromMinutes(5);

            lock (_sync)
            {
                _rejections++;
                var until = Stopwatch.GetTimestamp() + (long)(pause.TotalSeconds * Stopwatch.Frequency);
                if (until > _cooldownUntilTicks) _cooldownUntilTicks = until;
            }

            _logger.LogWarning(
                "Gemini answered {Status}; pausing all Gemini calls for {Pause:F0}s (Retry-After: {Given}).",
                statusCode, pause.TotalSeconds, retryAfter?.ToString() ?? "not sent");
        }

        // ---------------------------------------------------------------
        private TimeSpan ComputeWait(GeminiRateLimitSettings cfg)
        {
            var now = Stopwatch.GetTimestamp();

            if (now < _cooldownUntilTicks)
                return TimeSpan.FromSeconds((_cooldownUntilTicks - now) / (double)Stopwatch.Frequency);

            Trim();

            var rpm = cfg.RequestsPerMinute;
            if (rpm <= 0 || _recentCalls.Count < rpm) return TimeSpan.Zero;

            var oldest = _recentCalls.Peek();
            var freeAt = oldest + (long)(Stopwatch.Frequency * WindowSeconds(cfg));
            var seconds = (freeAt - now) / (double)Stopwatch.Frequency;
            return seconds <= 0 ? TimeSpan.Zero : TimeSpan.FromSeconds(seconds);
        }

        private static double WindowSeconds(GeminiRateLimitSettings cfg) =>
            cfg.WindowSeconds > 0 ? cfg.WindowSeconds : 60;

        private void Trim()
        {
            var cutoff = Stopwatch.GetTimestamp() - (long)(Stopwatch.Frequency * WindowSeconds(Config));
            while (_recentCalls.Count > 0 && _recentCalls.Peek() < cutoff) _recentCalls.Dequeue();
        }

        private SemaphoreSlim EnsureGate(int ceiling)
        {
            lock (_sync)
            {
                if (_inFlight == null || _inFlightCeiling != ceiling)
                {
                    // Config changed at runtime: a SemaphoreSlim cannot be
                    // resized, so we swap it. Calls already in flight simply
                    // release the old one, which is then collected.
                    _inFlight = new SemaphoreSlim(ceiling, ceiling);
                    _inFlightCeiling = ceiling;
                }
                return _inFlight;
            }
        }

        private sealed class Lease : IDisposable
        {
            private SemaphoreSlim? _gate;
            public Lease(SemaphoreSlim gate) => _gate = gate;
            public void Dispose() => Interlocked.Exchange(ref _gate, null)?.Release();
        }

        private sealed class NullLease : IDisposable
        {
            public static readonly NullLease Instance = new();
            public void Dispose() { }
        }
    }

    /// <summary>Quota settings ("Gemini:RateLimit").</summary>
    public class GeminiRateLimitSettings
    {
        /// <summary>False ⇒ no throttling at all (previous behaviour).</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>Your Google quota, in requests per minute. 0 = no window check.</summary>
        public int RequestsPerMinute { get; set; } = 60;

        /// <summary>Length of the sliding window in seconds. 60 = "per minute" (only changed in tests).</summary>
        public int WindowSeconds { get; set; } = 60;

        /// <summary>How many Gemini calls may be in flight at once.</summary>
        public int MaxConcurrentCalls { get; set; } = 6;

        /// <summary>Pause applied to ALL callers after a 429/503 that carried no Retry-After.</summary>
        public int CooldownSecondsOnReject { get; set; } = 20;
    }
}
