using MedicalApp.Data;
using MedicalApp.Models;
using Microsoft.EntityFrameworkCore;

namespace MedicalApp.Services
{
    /// <summary>
    /// Durable side of the CAM batch queue. The ClinicBatchRuns row IS the
    /// queue entry: no second table, no payload to serialize (the batch reads
    /// its files from the clinic's folder anyway).
    ///
    /// Claiming is optimistic (RowVersion): if two instances try to take the
    /// same batch, exactly one wins and the other moves on. No locks.
    /// </summary>
    public class CamBatchQueueStore
    {
        /// <summary>How long a claim is trusted without a heartbeat.</summary>
        public static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(3);

        /// <summary>Heartbeat period — comfortably shorter than the lease.</summary>
        public static readonly TimeSpan RenewInterval = TimeSpan.FromSeconds(30);

        private static readonly string InstanceId =
            $"{Environment.MachineName}/{Environment.ProcessId}";

        private readonly AppDbContext _db;
        private readonly ILogger<CamBatchQueueStore> _logger;

        public CamBatchQueueStore(AppDbContext db, ILogger<CamBatchQueueStore> logger)
        {
            _db = db;
            _logger = logger;
        }

        public static string Instance => InstanceId;

        /// <summary>
        /// True when the clinic already has a batch queued or running. Replaces
        /// the old in-memory check, which only knew about THIS instance.
        /// </summary>
        public Task<bool> HasActiveForClinicAsync(int clinicId, CancellationToken ct = default) =>
            _db.ClinicBatchRuns.AsNoTracking().AnyAsync(
                b => b.ClinicId == clinicId
                     && (b.Status == "Queued" || b.Status == "Running")
                     && b.FinishedAt == null, ct);

        /// <summary>
        /// Takes the next batch this instance may work on: one that is still
        /// queued, or one whose owner died (expired lease). Returns null when
        /// there is nothing to do.
        /// </summary>
        public async Task<ClinicBatchRun?> TryClaimNextAsync(CancellationToken ct = default)
        {
            var now = DateTime.UtcNow;

            var candidates = await _db.ClinicBatchRuns
                .Where(b => b.FinishedAt == null
                            && (b.Status == "Queued"
                                || (b.Status == "Running"
                                    && (b.LeaseUntil == null || b.LeaseUntil < now))))
                .OrderBy(b => b.StartedAt)
                .Take(5)
                .ToListAsync(ct);

            foreach (var batch in candidates)
            {
                // A batch interrupted mid-flight is NOT resumed: the files it
                // already e-mailed would be sent twice. This is the owner's
                // explicit decision (CAM phase 3, d)i) — we only make sure the
                // operator sees the truth quickly instead of after a restart.
                if (batch.Status == "Running")
                {
                    batch.Status = "Failed";
                    batch.FinishedAt = now;
                    batch.OwnerInstance = null;
                    batch.LeaseUntil = null;
                    if (await SaveClaimAsync(batch, ct))
                        _logger.LogWarning(
                            "CAM batch {Id}: lease expired (instance died) → marked Failed.", batch.Id);
                    continue;
                }

                batch.Status = "Running";
                batch.OwnerInstance = InstanceId;
                batch.LeaseUntil = now.Add(LeaseDuration);
                batch.Attempts++;
                if (await SaveClaimAsync(batch, ct)) return batch;
            }

            return null;
        }

        /// <summary>Extends the lease. False ⇒ we no longer own the batch (or it is gone).</summary>
        public async Task<bool> RenewLeaseAsync(int batchId, CancellationToken ct = default)
        {
            var batch = await _db.ClinicBatchRuns.FirstOrDefaultAsync(b => b.Id == batchId, ct);
            if (batch == null || batch.OwnerInstance != InstanceId) return false;

            batch.LeaseUntil = DateTime.UtcNow.Add(LeaseDuration);
            try
            {
                await _db.SaveChangesAsync(ct);
                return true;
            }
            catch (DbUpdateConcurrencyException)
            {
                // Someone else touched the row (cancel, or a claim race).
                _db.ChangeTracker.Clear();
                return true;
            }
        }

        /// <summary>True when the operator pressed Cancel, possibly on another instance.</summary>
        public Task<bool> IsCancelRequestedAsync(int batchId, CancellationToken ct = default) =>
            _db.ClinicBatchRuns.AsNoTracking()
                .AnyAsync(b => b.Id == batchId && b.CancelRequested, ct);

        /// <summary>
        /// Drops the lease after the runner returned. The runner itself already
        /// wrote the final Status/FinishedAt; if it failed to (crash), we make
        /// sure the row does not stay "Running" forever.
        /// </summary>
        public async Task ReleaseAsync(int batchId, CancellationToken ct = default)
        {
            var batch = await _db.ClinicBatchRuns.FirstOrDefaultAsync(b => b.Id == batchId, ct);
            if (batch == null) return;

            batch.OwnerInstance = null;
            batch.LeaseUntil = null;
            if (batch.Status == "Running")
            {
                batch.Status = "Failed";
                batch.FinishedAt ??= DateTime.UtcNow;
            }

            try { await _db.SaveChangesAsync(ct); }
            catch (DbUpdateConcurrencyException) { _db.ChangeTracker.Clear(); }
        }

        private async Task<bool> SaveClaimAsync(ClinicBatchRun batch, CancellationToken ct)
        {
            try
            {
                await _db.SaveChangesAsync(ct);
                return true;
            }
            catch (DbUpdateConcurrencyException)
            {
                // Another instance won the race. Forget everything and move on.
                _db.ChangeTracker.Clear();
                return false;
            }
        }
    }
}
