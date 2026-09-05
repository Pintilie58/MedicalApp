using MedicalApp.Data;
using MedicalApp.Models;
using Microsoft.EntityFrameworkCore;

namespace MedicalApp.Services
{
    /// <summary>
    /// Drains <see cref="InterpretationJobQueue"/> and runs each B2C
    /// interpretation on a background thread, at most
    /// <see cref="InterpretationJobQueue.MaxConcurrent"/> at a time.
    ///
    /// The user's browser is NOT waiting on this: it polls
    /// <c>/Interpretation/Progress</c>. Closing the page does not stop the job.
    /// </summary>
    public class InterpretationQueueWorker : BackgroundService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly InterpretationJobQueue _queue;
        private readonly ILogger<InterpretationQueueWorker> _logger;

        public InterpretationQueueWorker(
            IServiceScopeFactory scopeFactory,
            InterpretationJobQueue queue,
            ILogger<InterpretationQueueWorker> logger)
        {
            _scopeFactory = scopeFactory;
            _queue = queue;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // Read once at startup: SemaphoreSlim cannot be resized, and changing
            // the ceiling mid-flight would be a nasty source of surprises. Editing
            // appsettings and restarting is the intended way to raise it.
            var maxConcurrent = _queue.MaxConcurrent;
            using var gate = new SemaphoreSlim(maxConcurrent);
            _logger.LogInformation(
                "Interpretation worker started: {Max} concurrent job(s), {PerUser} per user.",
                maxConcurrent, _queue.MaxPerUser);

            try
            {
                await foreach (var job in _queue.Reader.ReadAllAsync(stoppingToken))
                {
                    await gate.WaitAsync(stoppingToken);
                    _queue.MarkStarted(job.HistoryId);

                    _ = Task.Run(async () =>
                    {
                        using var heartbeat = new CancellationTokenSource();
                        try
                        {
                            using var scope = _scopeFactory.CreateScope();
                            var store = scope.ServiceProvider
                                .GetRequiredService<InterpretationJobStore>();
                            // Durable queue: take ownership + start the lease, so
                            // a sibling instance does not pick the job up too.
                            await store.MarkRunningAsync(job.HistoryId);

                            // Heartbeat: keeps the (deliberately short) lease
                            // alive while we work. If this process dies, the
                            // lease lapses in ~2 minutes and the job is picked
                            // up again — instead of being frozen for as long as
                            // the longest imaginable interpretation.
                            _ = KeepLeaseAliveAsync(job.HistoryId, heartbeat.Token);

                            var runner = scope.ServiceProvider
                                .GetRequiredService<B2cInterpretationRunner>();
                            await runner.RunAsync(job, stoppingToken);

                            // Finished (success OR handled failure): the durable
                            // row has done its job and must not be replayed.
                            await store.RemoveAsync(job.HistoryId);
                        }
                        catch (Exception ex)
                        {
                            // The runner is written to never throw; this is the
                            // last net so a "processing" row can never be left
                            // dangling with the credit taken.
                            _logger.LogError(ex,
                                "Interpretation job {HistoryId} crashed outside the runner.",
                                job.HistoryId);
                            await SalvageAsync(job);
                        }
                        finally
                        {
                            heartbeat.Cancel();
                            _queue.ReleaseUser(job.UserEmail);
                            gate.Release();
                        }
                    }, CancellationToken.None);
                }
            }
            catch (OperationCanceledException)
            {
                // App is shutting down. Jobs still in flight are recovered on
                // the next start by StartupSeed.FailOrphanedInterpretationsAsync.
            }
        }

        /// <summary>
        /// Renews the durable job's lease while the interpretation runs, in its
        /// own scope so it never touches the runner's DbContext.
        /// </summary>
        private async Task KeepLeaseAliveAsync(int historyId, CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    await Task.Delay(InterpretationJobStore.RenewInterval, ct);

                    using var scope = _scopeFactory.CreateScope();
                    var store = scope.ServiceProvider.GetRequiredService<InterpretationJobStore>();
                    if (!await store.RenewLeaseAsync(historyId, ct)) return; // row already gone
                }
            }
            catch (OperationCanceledException) { /* job finished */ }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not renew the lease for job {HistoryId}.", historyId);
            }
        }

        private async Task SalvageAsync(InterpretationJob job)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var row = await db.InterpretationHistories
                    .FirstOrDefaultAsync(h => h.Id == job.HistoryId);
                if (row == null || row.Status != "processing") return;

                row.Status = "error";
                row.ErrorMessage = "Interpretation crashed unexpectedly.";
                row.CreditsConsumed = 0;

                var user = await db.Users.FirstOrDefaultAsync(u => u.Email == job.UserEmail);
                if (user != null) CreditLedger.RefundOne(user);

                var durable = await db.InterpretationJobs
                    .FirstOrDefaultAsync(j => j.HistoryId == job.HistoryId);
                if (durable != null) db.InterpretationJobs.Remove(durable);

                await db.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Could not salvage interpretation job {Id}.", job.HistoryId);
            }
        }
    }
}
