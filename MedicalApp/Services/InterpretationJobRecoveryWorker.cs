using Microsoft.Extensions.Options;

namespace MedicalApp.Services
{
    /// <summary>
    /// Picks up interpretations nobody is working on and pushes them back into
    /// the in-memory queue. This is what makes the queue durable in practice:
    ///
    ///   * jobs still "queued" when the app was restarted or deployed;
    ///   * jobs left "running" by an instance that died mid-flight (their lease
    ///     expires after <see cref="InterpretationJobStore.LeaseDuration"/>);
    ///   * with several instances, work waiting on a busy sibling.
    ///
    /// The first pass runs a few seconds after start-up (so the app is warm),
    /// then every <see cref="InterpretationQueueSettings.RecoveryIntervalSeconds"/>.
    /// Claiming is optimistic, so running this on every instance is safe.
    /// </summary>
    public class InterpretationJobRecoveryWorker : BackgroundService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly InterpretationJobQueue _queue;
        private readonly IOptionsMonitor<InterpretationQueueSettings> _options;
        private readonly ILogger<InterpretationJobRecoveryWorker> _logger;

        public InterpretationJobRecoveryWorker(
            IServiceScopeFactory scopeFactory,
            InterpretationJobQueue queue,
            IOptionsMonitor<InterpretationQueueSettings> options,
            ILogger<InterpretationJobRecoveryWorker> logger)
        {
            _scopeFactory = scopeFactory;
            _queue = queue;
            _options = options;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await RecoverOnceAsync(stoppingToken);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Interpretation queue recovery pass failed.");
                }

                var seconds = Math.Max(15, _options.CurrentValue.RecoveryIntervalSeconds);
                try { await Task.Delay(TimeSpan.FromSeconds(seconds), stoppingToken); }
                catch (OperationCanceledException) { break; }
            }
        }

        /// <summary>One recovery pass. Public so it can be exercised directly in tests.</summary>
        public async Task<int> RecoverOnceAsync(CancellationToken ct)
        {
            // Never take more than this instance can actually work on, otherwise
            // one instance would hoard the whole queue and starve its siblings.
            var budget = Math.Max(1, _queue.MaxConcurrent - _queue.ActiveCount);
            if (budget <= 0) return 0;

            using var scope = _scopeFactory.CreateScope();
            var store = scope.ServiceProvider.GetRequiredService<InterpretationJobStore>();

            var abandoned = await store.FindAbandonedAsync(budget, ct);
            if (abandoned.Count == 0) return 0;

            var recovered = 0;
            foreach (var row in abandoned)
            {
                var job = await store.TryClaimAsync(row, ct);
                if (job == null) continue;

                if (_queue.TryRequeue(job))
                {
                    recovered++;
                    _logger.LogWarning(
                        "Recovered interpretation for history {Id} (user {User}, attempt {N}).",
                        job.HistoryId, job.UserEmail, row.Attempts);
                }
            }

            return recovered;
        }
    }
}
