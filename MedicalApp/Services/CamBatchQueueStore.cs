using MedicalApp.Data;
using MedicalApp.Models;
using Microsoft.EntityFrameworkCore;

namespace MedicalApp.Services
{
    /// <summary>
    /// The durable queue of CAM batches (June 2026, Azure hosting).
    ///
    /// A batch used to be started with a fire-and-forget Task.Run inside the
    /// request that pressed the button, so an instance recycle killed it.
    /// Now the ClinicBatchRun row itself is the queue entry:
    ///   Queued  -> nobody works on it, any instance may claim it
    ///   Running -> one instance holds a CLAIM (see ClinicBatchClaim)
    ///   Completed / Failed / Cancelled -> finished
    ///
    /// The lock lives in a SEPARATE row (ClinicBatchClaim), not in a
    /// concurrency token on the batch row. That is deliberate: the batch row is
    /// written continuously by the runner (per-file counters) from a different
    /// DbContext, so a [Timestamp] column on it made every counter save throw
    /// DbUpdateConcurrencyException as soon as the first heartbeat renewed the
    /// lease. Claiming = INSERT of the claim row: the primary key makes it
    /// atomic, and a second instance simply loses the insert.
    /// </summary>
    public class CamBatchQueueStore
    {
        /// <summary>How long a claim is trusted before the batch counts as abandoned.</summary>
        public static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(3);

        /// <summary>How often the owner pushes its claim forward while working.</summary>
        public static readonly TimeSpan RenewInterval = TimeSpan.FromSeconds(30);

        /// <summary>Name of this process, written into the claim it holds.</summary>
        public static readonly string Instance =
            $"{Environment.MachineName}/{Environment.ProcessId}";

        private readonly AppDbContext _db;
        private readonly ILogger<CamBatchQueueStore> _logger;

        public CamBatchQueueStore(AppDbContext db, ILogger<CamBatchQueueStore> logger)
        {
            _db = db;
            _logger = logger;
        }

        /// <summary>
        /// True when the clinic already has a batch waiting or running. Checked
        /// in the database, so the guard holds across Azure instances (the old
        /// in-memory registry only knew about the current process).
        /// </summary>
        public Task<bool> HasActiveForClinicAsync(int clinicId, CancellationToken ct = default) =>
            _db.ClinicBatchRuns.AsNoTracking().AnyAsync(
                b => b.ClinicId == clinicId
                     && b.FinishedAt == null
                     && (b.Status == "Queued" || b.Status == "Running"), ct);

        /// <summary>
        /// Closes batches whose owner died: the row says Running but no live
        /// claim exists. Business decision (CAM phase 3): such a batch is NOT
        /// resumed, because the patients already e-mailed would be e-mailed
        /// twice. It is marked Failed and the operator relaunches it.
        /// </summary>
        public async Task<int> FailAbandonedAsync(CancellationToken ct = default)
        {
            var now = DateTime.UtcNow;

            // Stale claims first, so the batch below is seen as unclaimed.
            var staleClaims = await _db.ClinicBatchClaims
                .Where(c => c.LeaseUntil < now)
                .ToListAsync(ct);
            if (staleClaims.Count > 0)
            {
                _db.ClinicBatchClaims.RemoveRange(staleClaims);
                await _db.SaveChangesAsync(ct);
            }

            var liveClaims = await _db.ClinicBatchClaims.AsNoTracking()
                .Where(c => c.LeaseUntil >= now)
                .Select(c => c.BatchRunId)
                .ToListAsync(ct);

            var abandoned = await _db.ClinicBatchRuns
                .Where(b => b.Status == "Running"
                            && b.FinishedAt == null
                            && !liveClaims.Contains(b.Id))
                .ToListAsync(ct);
            if (abandoned.Count == 0) return 0;

            foreach (var b in abandoned)
            {
                b.Status = "Failed";
                b.FinishedAt = now;
                b.OwnerInstance = null;
            }
            await _db.SaveChangesAsync(ct);

            _logger.LogWarning(
                "CAM queue: closed {Count} abandoned batch(es) (owner instance gone).",
                abandoned.Count);
            return abandoned.Count;
        }

        /// <summary>
        /// Takes the oldest queued batch, if any. Returns null when there is
        /// nothing to do or another instance won the race.
        /// </summary>
        public async Task<ClinicBatchRun?> TryClaimNextAsync(CancellationToken ct = default)
        {
            await FailAbandonedAsync(ct);

            var candidates = await _db.ClinicBatchRuns
                .Where(b => b.Status == "Queued" && b.FinishedAt == null)
                .OrderBy(b => b.StartedAt)
                .Take(5)
                .ToListAsync(ct);

            foreach (var batch in candidates)
            {
                if (batch.CancelRequested)
                {
                    batch.Status = "Cancelled";
                    batch.FinishedAt = DateTime.UtcNow;
                    await _db.SaveChangesAsync(ct);
                    continue;
                }

                // The INSERT *is* the lock: BatchRunId is the primary key, so a
                // second instance trying the same batch fails right here.
                _db.ClinicBatchClaims.Add(new ClinicBatchClaim
                {
                    BatchRunId = batch.Id,
                    OwnerInstance = Instance,
                    LeaseUntil = DateTime.UtcNow.Add(LeaseDuration),
                    ClaimedAt = DateTime.UtcNow
                });

                try
                {
                    await _db.SaveChangesAsync(ct);
                }
                catch (Exception ex) when (ex is DbUpdateException or InvalidOperationException or ArgumentException)
                {
                    // Someone else holds the claim. Forget it and try the next.
                    _logger.LogDebug(ex, "CAM batch {Id}: claim lost to another instance.", batch.Id);
                    _db.ChangeTracker.Clear();
                    continue;
                }

                batch.Status = "Running";
                batch.OwnerInstance = Instance;
                batch.Attempts += 1;
                await _db.SaveChangesAsync(ct);

                _logger.LogInformation("CAM batch {Id} claimed by {Instance}.", batch.Id, Instance);
                return batch;
            }

            return null;
        }

        /// <summary>
        /// Pushes the lease forward while the batch is being processed. Returns
        /// false when we are no longer the owner (claim deleted or taken over),
        /// which tells the worker to stop.
        /// </summary>
        public async Task<bool> RenewLeaseAsync(int batchRunId, CancellationToken ct = default)
        {
            var claim = await _db.ClinicBatchClaims
                .FirstOrDefaultAsync(c => c.BatchRunId == batchRunId, ct);
            if (claim == null || claim.OwnerInstance != Instance) return false;

            claim.LeaseUntil = DateTime.UtcNow.Add(LeaseDuration);
            await _db.SaveChangesAsync(ct);
            return true;
        }

        /// <summary>
        /// Reads the Cancel flag written by the operator's request, possibly on
        /// another instance. The worker polls it while the batch runs.
        /// </summary>
        public Task<bool> IsCancelRequestedAsync(int batchRunId, CancellationToken ct = default) =>
            _db.ClinicBatchRuns.AsNoTracking()
                .Where(b => b.Id == batchRunId)
                .Select(b => b.CancelRequested)
                .FirstOrDefaultAsync(ct);

        /// <summary>
        /// Drops the claim once the runner returned. If the runner left the row
        /// as Running (it crashed), the batch is closed as Failed so nothing
        /// stays stuck in the operator's history.
        /// </summary>
        public async Task ReleaseAsync(int batchRunId, CancellationToken ct = default)
        {
            try
            {
                var claim = await _db.ClinicBatchClaims
                    .FirstOrDefaultAsync(c => c.BatchRunId == batchRunId, ct);
                if (claim != null) _db.ClinicBatchClaims.Remove(claim);

                var batch = await _db.ClinicBatchRuns
                    .FirstOrDefaultAsync(b => b.Id == batchRunId, ct);
                if (batch != null)
                {
                    batch.OwnerInstance = null;
                    if (batch.Status == "Running" && batch.FinishedAt == null)
                    {
                        batch.Status = "Failed";
                        batch.FinishedAt = DateTime.UtcNow;
                    }
                }
                await _db.SaveChangesAsync(ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "CAM batch {Id}: could not release the claim.", batchRunId);
            }
        }
    }
}
