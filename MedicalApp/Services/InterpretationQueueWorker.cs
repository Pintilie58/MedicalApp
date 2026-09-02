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
            using var gate = new SemaphoreSlim(InterpretationJobQueue.MaxConcurrent);

            try
            {
                await foreach (var job in _queue.Reader.ReadAllAsync(stoppingToken))
                {
                    await gate.WaitAsync(stoppingToken);

                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            using var scope = _scopeFactory.CreateScope();
                            var runner = scope.ServiceProvider
                                .GetRequiredService<B2cInterpretationRunner>();
                            await runner.RunAsync(job, stoppingToken);
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

                await db.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Could not salvage interpretation job {Id}.", job.HistoryId);
            }
        }
    }
}
