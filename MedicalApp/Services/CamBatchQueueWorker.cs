using System.Text.Json;
using MedicalApp.Models;
using Microsoft.Extensions.Caching.Distributed;

namespace MedicalApp.Services
{
    /// <summary>
    /// Runs CAM batches OUTSIDE any HTTP request (Azure hosting, June 2026).
    ///
    /// The operator's click only writes a "Queued" row; this worker claims it
    /// with a lease, runs it in its own DI scope, renews the lease every 30 s
    /// and publishes a progress snapshot to the distributed cache so the
    /// progress page keeps working even when the poll lands on another
    /// instance. It also watches the durable cancel flag.
    ///
    /// One batch at a time per instance: a batch is dozens of Gemini calls and
    /// PDF renders, and the per-instance Gemini budget is shared with B2C.
    /// </summary>
    public class CamBatchQueueWorker : BackgroundService
    {
        /// <summary>Cache key of the shared progress snapshot.</summary>
        public static string SnapshotKey(int batchId) => $"cambatch:{batchId}";

        private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan SnapshotInterval = TimeSpan.FromSeconds(3);
        private static readonly DistributedCacheEntryOptions SnapshotTtl = new()
        {
            AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(6)
        };

        private readonly IServiceScopeFactory _scopeFactory;
        private readonly CamBatchRegistry _registry;
        private readonly IDistributedCache _cache;
        private readonly ILogger<CamBatchQueueWorker> _logger;

        public CamBatchQueueWorker(
            IServiceScopeFactory scopeFactory,
            CamBatchRegistry registry,
            IDistributedCache cache,
            ILogger<CamBatchQueueWorker> logger)
        {
            _scopeFactory = scopeFactory;
            _registry = registry;
            _cache = cache;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // Let the app warm up before touching the database.
            try { await Task.Delay(TimeSpan.FromSeconds(8), stoppingToken); }
            catch (OperationCanceledException) { return; }

            _logger.LogInformation("CAM batch worker started on {Instance}.", CamBatchQueueStore.Instance);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var claimed = await ClaimAndRunAsync(stoppingToken);
                    if (claimed) continue;   // maybe another one is waiting
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "CAM batch worker pass failed.");
                }

                try { await Task.Delay(PollInterval, stoppingToken); }
                catch (OperationCanceledException) { break; }
            }
        }

        private async Task<bool> ClaimAndRunAsync(CancellationToken stoppingToken)
        {
            ClinicBatchRun? batch;
            using (var claimScope = _scopeFactory.CreateScope())
            {
                var store = claimScope.ServiceProvider.GetRequiredService<CamBatchQueueStore>();
                batch = await store.TryClaimNextAsync(stoppingToken);
            }
            if (batch == null) return false;

            _logger.LogInformation("CAM batch {Id} claimed (attempt {Attempt}).", batch.Id, batch.Attempts);

            using var heartbeat = new CancellationTokenSource();
            try
            {
                var progress = _registry.GetOrCreate(batch.Id, batch.ClinicId, total: 0);
                progress.Status = "Running";

                var keepAlive = KeepAliveAsync(batch.Id, progress, heartbeat.Token);

                using var runScope = _scopeFactory.CreateScope();
                var runner = runScope.ServiceProvider.GetRequiredService<CamBatchService>();
                await runner.RunAsync(batch.Id, batch.LanguageCode ?? "ro");

                heartbeat.Cancel();
                await keepAlive;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "CAM batch {Id} crashed outside the runner.", batch.Id);
            }
            finally
            {
                heartbeat.Cancel();
                using var scope = _scopeFactory.CreateScope();
                var store = scope.ServiceProvider.GetRequiredService<CamBatchQueueStore>();
                await store.ReleaseAsync(batch.Id, CancellationToken.None);
                await PublishSnapshotAsync(batch.Id, CancellationToken.None);
            }

            return true;
        }

        /// <summary>
        /// While the batch runs: renews the lease, mirrors the progress into the
        /// distributed cache and honours a cancel pressed on another instance.
        /// </summary>
        private async Task KeepAliveAsync(int batchId, CamBatchProgress progress, CancellationToken ct)
        {
            var lastRenew = DateTime.UtcNow;
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    await Task.Delay(SnapshotInterval, ct);
                    await PublishSnapshotAsync(batchId, ct);

                    if (DateTime.UtcNow - lastRenew < CamBatchQueueStore.RenewInterval) continue;
                    lastRenew = DateTime.UtcNow;

                    using var scope = _scopeFactory.CreateScope();
                    var store = scope.ServiceProvider.GetRequiredService<CamBatchQueueStore>();
                    await store.RenewLeaseAsync(batchId, ct);

                    if (await store.IsCancelRequestedAsync(batchId, ct) && !progress.Cts.IsCancellationRequested)
                    {
                        _logger.LogInformation("CAM batch {Id}: cancel requested by the operator.", batchId);
                        progress.Cts.Cancel();
                    }
                }
            }
            catch (OperationCanceledException) { /* batch finished */ }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "CAM batch {Id}: keep-alive loop failed.", batchId);
            }
        }

        private async Task PublishSnapshotAsync(int batchId, CancellationToken ct)
        {
            var p = _registry.Get(batchId);
            if (p == null) return;

            try
            {
                var json = JsonSerializer.Serialize(new CamBatchSnapshot
                {
                    ClinicId = p.ClinicId,
                    Status = p.Status,
                    Processed = p.Processed,
                    Total = p.Total,
                    Sent = p.Sent,
                    Compared = p.Compared,
                    NotSends = p.NotSends,
                    CurrentFile = p.CurrentFile ?? string.Empty,
                    Log = p.LogSnapshot(),
                    FinishedAt = p.FinishedAt
                });
                await _cache.SetStringAsync(SnapshotKey(batchId), json, SnapshotTtl, ct);
            }
            catch (Exception ex)
            {
                // Progress sharing is a comfort, never a reason to lose a batch.
                _logger.LogDebug(ex, "CAM batch {Id}: could not publish the progress snapshot.", batchId);
            }
        }
    }

    /// <summary>
    /// What the progress page needs, as published by the instance doing the
    /// work and read by whichever instance answers the poll.
    /// </summary>
    public class CamBatchSnapshot
    {
        public int ClinicId { get; set; }
        public string Status { get; set; } = "Running";
        public int Processed { get; set; }
        public int Total { get; set; }
        public int Sent { get; set; }
        public int Compared { get; set; }
        public int NotSends { get; set; }
        public string CurrentFile { get; set; } = string.Empty;
        public List<string> Log { get; set; } = new();
        public DateTime? FinishedAt { get; set; }
    }
}
