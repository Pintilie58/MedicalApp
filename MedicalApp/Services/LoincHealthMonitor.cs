using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Options;

namespace MedicalApp.Services
{
    /// <summary>
    /// Background service that periodically probes the Python LOINC microservice
    /// at <c>{LoincMatcher.BaseUrl}/ready</c>, keeps a cheap in-memory snapshot
    /// (<see cref="ILoincHealthState"/>) that the admin dashboard reads with
    /// zero HTTP overhead, and — optionally, only on Windows dev boxes — auto-
    /// restarts uvicorn when the microservice has been down for
    /// <see cref="LoincAutoStartSettings.FailuresBeforeRestart"/> consecutive
    /// probes.
    ///
    /// AZURE / PRODUCTION SAFETY
    /// -------------------------
    /// The restart logic is triple-gated:
    ///   1. <c>LoincAutoStart.Enabled == true</c> (default false in appsettings.json)
    ///   2. <c>RuntimeInformation.IsOSPlatform(OSPlatform.Windows)</c>
    ///   3. cooldown timer (<c>RestartCooldownSeconds</c>) has elapsed
    /// Even if points 1 and 3 line up, point 2 guarantees the code path is
    /// completely dead on Linux App Service / Container Apps / Kubernetes.
    ///
    /// KILL SWITCH
    /// -----------
    /// Flip <c>LoincAutoStart.Enabled</c> to <c>false</c> and restart the app.
    /// The monitor still runs and updates the cache (good — the admin widget
    /// remains functional), but never spawns a process.
    ///
    /// FAILURE ISOLATION
    /// -----------------
    /// Every probe and restart attempt is wrapped in try/catch. A crash inside
    /// this service never crashes the host — worst case the cache stays stale
    /// and the admin widget shows "timeout" until the next tick.
    /// </summary>
    public sealed class LoincHealthMonitor : BackgroundService
    {
        private readonly IHttpClientFactory _httpFactory;
        private readonly ILoincHealthState _state;
        private readonly IOptionsMonitor<LoincMatcherSettings> _matcherOpts;
        private readonly IOptionsMonitor<LoincAutoStartSettings> _autoStartOpts;
        private readonly ILogger<LoincHealthMonitor> _logger;

        private int _consecutiveFailures;
        private DateTime _lastRestartAttemptUtc = DateTime.MinValue;

        public LoincHealthMonitor(
            IHttpClientFactory httpFactory,
            ILoincHealthState state,
            IOptionsMonitor<LoincMatcherSettings> matcherOpts,
            IOptionsMonitor<LoincAutoStartSettings> autoStartOpts,
            ILogger<LoincHealthMonitor> logger)
        {
            _httpFactory = httpFactory;
            _state = state;
            _matcherOpts = matcherOpts;
            _autoStartOpts = autoStartOpts;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // Small initial delay so we don't collide with app startup work
            // (EF migrations, LOINC dictionary seed, etc.).
            try { await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken); }
            catch (TaskCanceledException) { return; }

            _logger.LogInformation("LoincHealthMonitor started. Polling every {Sec}s.",
                _autoStartOpts.CurrentValue.PollIntervalSeconds);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await ProbeOnceAsync(stoppingToken);
                }
                catch (Exception ex)
                {
                    // Absolute safety net — nothing in the probe path should
                    // ever kill the host. Log and keep looping.
                    _logger.LogError(ex, "LoincHealthMonitor: unexpected error in probe loop.");
                }

                var interval = Math.Max(3, _autoStartOpts.CurrentValue.PollIntervalSeconds);
                try { await Task.Delay(TimeSpan.FromSeconds(interval), stoppingToken); }
                catch (TaskCanceledException) { break; }
            }

            _logger.LogInformation("LoincHealthMonitor stopped.");
        }

        private async Task ProbeOnceAsync(CancellationToken ct)
        {
            var matcher = _matcherOpts.CurrentValue;
            var auto = _autoStartOpts.CurrentValue;
            var baseUrl = (matcher?.BaseUrl ?? string.Empty).TrimEnd('/');

            if (string.IsNullOrWhiteSpace(baseUrl) || matcher?.Enabled != true)
            {
                _state.Update(false, "disabled",
                    "LoincMatcher is disabled in appsettings.json (LoincMatcher.Enabled=false).",
                    0, null, baseUrl);
                return;
            }

            var sw = Stopwatch.StartNew();
            var timeoutMs = Math.Max(200, auto.ProbeTimeoutMs);
            try
            {
                var client = _httpFactory.CreateClient();
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromMilliseconds(timeoutMs));

                using var resp = await client.GetAsync(baseUrl + "/ready", cts.Token);
                sw.Stop();

                if (!resp.IsSuccessStatusCode)
                {
                    await OnProbeFailedAsync("error",
                        $"HTTP {(int)resp.StatusCode} from {baseUrl}/ready",
                        sw.ElapsedMilliseconds, baseUrl);
                    return;
                }

                int? loincCount = null;
                try
                {
                    var body = await resp.Content.ReadAsStringAsync(cts.Token);
                    using var doc = System.Text.Json.JsonDocument.Parse(body);
                    // The FastAPI service answers {"status":"ready","entries":N};
                    // older builds used "loinc_count". Accept both.
                    if ((doc.RootElement.TryGetProperty("loinc_count", out var lc)
                            || doc.RootElement.TryGetProperty("entries", out lc))
                        && lc.ValueKind == System.Text.Json.JsonValueKind.Number)
                    {
                        loincCount = lc.GetInt32();
                    }
                }
                catch { /* payload not JSON or different schema — leave null */ }

                _state.Update(true, "ok", null, sw.ElapsedMilliseconds, loincCount, baseUrl);
                _consecutiveFailures = 0;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                sw.Stop();
                await OnProbeFailedAsync("timeout",
                    $"No response from {baseUrl}/ready within {timeoutMs} ms.",
                    sw.ElapsedMilliseconds, baseUrl);
            }
            catch (Exception ex)
            {
                sw.Stop();
                await OnProbeFailedAsync("down",
                    ex.GetBaseException().Message,
                    sw.ElapsedMilliseconds, baseUrl);
            }
        }

        private async Task OnProbeFailedAsync(string status, string? message, long latencyMs, string baseUrl)
        {
            _consecutiveFailures++;

            // A single missed probe is NOT an outage: the microservice is
            // single-worker and cannot answer while it is matching a batch. Keep
            // the previous snapshot until a SECOND probe fails, otherwise the
            // admin widget and the upload warning flip red on a healthy service.
            if (_consecutiveFailures >= 2)
                _state.Update(false, status, message, latencyMs, null, baseUrl);

            var auto = _autoStartOpts.CurrentValue;

            if (!auto.Enabled)
            {
                // gate #1 — log ONCE per outage so a silent config issue
                // (e.g. app accidentally running in Production env) is visible.
                if (_consecutiveFailures == auto.FailuresBeforeRestart)
                {
                    _logger.LogWarning(
                        "LoincHealthMonitor: microservice down ({Count} consecutive failures), " +
                        "but auto-restart is DISABLED (LoincAutoStart.Enabled=false — check " +
                        "ASPNETCORE_ENVIRONMENT / appsettings.{Env}.json).",
                        _consecutiveFailures,
                        Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Production");
                }
                return;
            }

            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                // gate #2 — Azure Linux / any non-Windows host
                if (_consecutiveFailures == auto.FailuresBeforeRestart)
                {
                    _logger.LogWarning(
                        "LoincHealthMonitor: microservice down, but auto-restart is skipped on non-Windows host.");
                }
                return;
            }

            if (_consecutiveFailures < auto.FailuresBeforeRestart)
                return;

            var sinceLast = DateTime.UtcNow - _lastRestartAttemptUtc;
            if (sinceLast < TimeSpan.FromSeconds(auto.RestartCooldownSeconds))
            {
                _logger.LogDebug(
                    "LoincHealthMonitor: restart suppressed by cooldown ({Sec}s remaining).",
                    (int)(TimeSpan.FromSeconds(auto.RestartCooldownSeconds) - sinceLast).TotalSeconds);
                return;                                          // gate #3
            }

            _lastRestartAttemptUtc = DateTime.UtcNow;

            // gate #4 — SECOND OPINION: one last probe with a long (8s) timeout.
            // A busy-but-alive service (CPU saturated by batch matching) will
            // answer within 8s; a truly dead one will not. Prevents spawning a
            // doomed duplicate instance that dies on "port already in use".
            if (!await ConfirmDownAsync())
                return;

            TryStartUvicorn(auto);
        }

        /// <summary>Returns true when the service is REALLY down (confirmation
        /// probe with generous timeout also failed).</summary>
        private async Task<bool> ConfirmDownAsync()
        {
            try
            {
                var baseUrl = (_matcherOpts.CurrentValue?.BaseUrl ?? string.Empty).TrimEnd('/');
                if (string.IsNullOrWhiteSpace(baseUrl))
                    return true;

                var client = _httpFactory.CreateClient();
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                using var resp = await client.GetAsync(baseUrl + "/ready", cts.Token);
                if (resp.IsSuccessStatusCode)
                {
                    _logger.LogInformation(
                        "LoincHealthMonitor: confirmation probe succeeded — service is alive but busy. Restart cancelled.");
                    _consecutiveFailures = 0;
                    return false;
                }
                return true;
            }
            catch
            {
                return true;
            }
        }

        private void TryStartUvicorn(LoincAutoStartSettings auto)
        {
            try
            {
                if (!Directory.Exists(auto.WorkingDirectory))
                {
                    _logger.LogError(
                        "LoincHealthMonitor: cannot auto-start — WorkingDirectory not found: {Dir}",
                        auto.WorkingDirectory);
                    return;
                }

                var psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoExit -ExecutionPolicy Bypass -Command \"{auto.StartCommand}\"",
                    WorkingDirectory = auto.WorkingDirectory,
                    UseShellExecute = true,                       // required for CreateNoWindow=false + separate window
                    CreateNoWindow = !auto.ShowWindow,
                    WindowStyle = auto.ShowWindow ? ProcessWindowStyle.Normal : ProcessWindowStyle.Hidden,
                };

                var proc = Process.Start(psi);
                if (proc == null)
                {
                    _logger.LogError("LoincHealthMonitor: Process.Start returned null for uvicorn.");
                    return;
                }

                // Update cache to "starting" so the admin widget shows the
                // transition state until the next successful probe.
                _state.Update(false, "starting",
                    "Auto-start triggered — waiting for uvicorn to accept connections.",
                    0, null, _state.BaseUrl);

                _logger.LogWarning(
                    "LoincHealthMonitor: uvicorn auto-start triggered (PID {Pid}). Next probe in {Sec}s.",
                    proc.Id, _autoStartOpts.CurrentValue.PollIntervalSeconds);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "LoincHealthMonitor: failed to auto-start uvicorn.");
            }
        }
    }
}
