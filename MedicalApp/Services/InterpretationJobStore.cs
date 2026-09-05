using MedicalApp.Data;
using MedicalApp.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MedicalApp.Services
{
    /// <summary>
    /// Durable side of the B2C interpretation queue (see
    /// <see cref="InterpretationJobRecord"/>). Every job is written to SQL
    /// before it is dispatched, so nothing is lost on a restart and a sibling
    /// instance can take over abandoned work.
    ///
    /// Claiming is optimistic: the row carries a RowVersion, so if two
    /// instances try to take the same job exactly one wins and the other simply
    /// moves on. No locks, no distributed coordination.
    /// </summary>
    public class InterpretationJobStore
    {
        /// <summary>
        /// How long a claim is trusted without a heartbeat. Short on purpose:
        /// this is exactly how long a dead instance's work stays frozen. The
        /// running job renews it every <see cref="RenewInterval"/>.
        /// </summary>
        public static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(2);

        /// <summary>Heartbeat period — comfortably shorter than the lease.</summary>
        public static readonly TimeSpan RenewInterval = TimeSpan.FromSeconds(30);

        /// <summary>How many times a job is retried after a crash before giving up.</summary>
        public const int MaxAttempts = 3;

        private static readonly string InstanceId =
            $"{Environment.MachineName}/{Environment.ProcessId}";

        private readonly AppDbContext _db;
        private readonly ScaleOutSettings _scaleOut;
        private readonly ILogger<InterpretationJobStore> _logger;

        public InterpretationJobStore(
            AppDbContext db,
            IOptions<ScaleOutSettings> scaleOut,
            ILogger<InterpretationJobStore> logger)
        {
            _db = db;
            _scaleOut = scaleOut.Value;
            _logger = logger;
        }

        public static string Instance => InstanceId;

        /// <summary>Writes the job. Called inside the same request that reserved the credit.</summary>
        public async Task AddAsync(InterpretationJob job, CancellationToken ct = default)
        {
            _db.InterpretationJobs.Add(new InterpretationJobRecord
            {
                HistoryId = job.HistoryId,
                UserEmail = job.UserEmail,
                ProfileId = job.ProfileId,
                ProfileName = job.ProfileName ?? string.Empty,
                PdfBytes = job.PdfBytes,
                OriginalFileName = job.OriginalFileName ?? string.Empty,
                PdfHash = job.PdfHash,
                LanguageCode = job.LanguageCode ?? "ro",
                Force = job.Force,
                ProgressToken = job.ProgressToken,
                Status = "queued",
                Attempts = 0,
                EnqueuedAt = DateTime.UtcNow
            });
            await _db.SaveChangesAsync(ct);
        }

        /// <summary>Marks the job as running on this instance and extends the lease.</summary>
        public async Task MarkRunningAsync(int historyId, CancellationToken ct = default)
        {
            var row = await _db.InterpretationJobs.FirstOrDefaultAsync(j => j.HistoryId == historyId, ct);
            if (row == null) return;

            row.Status = "running";
            row.Attempts++;
            row.StartedAt = DateTime.UtcNow;
            row.Owner = InstanceId;
            row.LeaseUntil = DateTime.UtcNow.Add(LeaseDuration);
            await _db.SaveChangesAsync(ct);
        }

        /// <summary>The interpretation ended (success or failure): the job is done.</summary>
        public async Task RemoveAsync(int historyId, CancellationToken ct = default)
        {
            var row = await _db.InterpretationJobs.FirstOrDefaultAsync(j => j.HistoryId == historyId, ct);
            if (row == null) return;

            _db.InterpretationJobs.Remove(row);
            try
            {
                await _db.SaveChangesAsync(ct);
            }
            catch (DbUpdateConcurrencyException)
            {
                // Someone touched the row between our read and our delete (the
                // heartbeat renewing the lease, typically). Deleting a finished
                // job must never fail: reload and try once more.
                _db.ChangeTracker.Clear();
                var again = await _db.InterpretationJobs
                    .FirstOrDefaultAsync(j => j.HistoryId == historyId, ct);
                if (again == null) return;   // already gone — that is the goal

                _db.InterpretationJobs.Remove(again);
                await _db.SaveChangesAsync(ct);
            }
        }

        /// <summary>
        /// Heartbeat: the job is alive, push the lease forward. Without this a
        /// long interpretation would look abandoned after two minutes.
        /// </summary>
        public async Task<bool> RenewLeaseAsync(int historyId, CancellationToken ct = default)
        {
            var row = await _db.InterpretationJobs.FirstOrDefaultAsync(j => j.HistoryId == historyId, ct);
            if (row == null) return false;

            row.LeaseUntil = DateTime.UtcNow.Add(LeaseDuration);
            try
            {
                await _db.SaveChangesAsync(ct);
                return true;
            }
            catch (DbUpdateConcurrencyException)
            {
                // The interpretation finished (or was taken over) between our
                // read and our write, so the row is already gone. Nothing to
                // renew and nothing to report — stop the heartbeat quietly.
                return false;
            }
        }

        /// <summary>
        /// Jobs nobody is working on: queued, or running with an expired lease
        /// (the instance died mid-flight). Oldest first, so nobody starves.
        ///
        /// With a SINGLE instance (ScaleOut disabled — local development and
        /// today's hosting) there is nobody else who could be running a job, so
        /// every "running" row found after a start-up is abandoned by
        /// definition and is picked up immediately instead of waiting for the
        /// lease to expire.
        /// </summary>
        public async Task<List<InterpretationJobRecord>> FindAbandonedAsync(
            int take, CancellationToken ct = default)
        {
            var now = DateTime.UtcNow;
            var singleInstance = !_scaleOut.Enabled;

            return await _db.InterpretationJobs
                .Where(j => j.Status == "queued"
                            || (j.Status == "running"
                                // Never steal a job THIS process is running.
                                && j.Owner != InstanceId
                                && (singleInstance || j.LeaseUntil == null || j.LeaseUntil < now)))
                .OrderBy(j => j.EnqueuedAt)
                .Take(take)
                .ToListAsync(ct);
        }

        /// <summary>
        /// Tries to take ownership of an abandoned job. Returns the rebuilt job
        /// when this instance won the race, null otherwise (another instance
        /// got it first, or the job died of too many attempts).
        /// </summary>
        public async Task<InterpretationJob?> TryClaimAsync(
            InterpretationJobRecord row, CancellationToken ct = default)
        {
            if (row.Attempts >= MaxAttempts)
            {
                await AbandonAsync(row, ct);
                return null;
            }

            try
            {
                row.Status = "running";
                row.Attempts++;
                row.StartedAt = DateTime.UtcNow;
                row.Owner = InstanceId;
                row.LeaseUntil = DateTime.UtcNow.Add(LeaseDuration);
                await _db.SaveChangesAsync(ct);
            }
            catch (DbUpdateConcurrencyException)
            {
                _logger.LogInformation(
                    "Interpretation job for history {Id} was claimed by another instance.", row.HistoryId);
                return null;
            }

            return new InterpretationJob(
                HistoryId: row.HistoryId,
                UserEmail: row.UserEmail,
                ProfileId: row.ProfileId,
                ProfileName: row.ProfileName,
                PdfBytes: row.PdfBytes,
                OriginalFileName: row.OriginalFileName,
                PdfHash: row.PdfHash ?? string.Empty,
                LanguageCode: row.LanguageCode,
                Force: row.Force,
                ProgressToken: row.ProgressToken);
        }

        /// <summary>
        /// A job that crashed too many times: stop retrying, refund the credit
        /// and tell the user, instead of looping forever.
        /// </summary>
        private async Task AbandonAsync(InterpretationJobRecord row, CancellationToken ct)
        {
            _logger.LogError(
                "Interpretation job for history {Id} failed {Attempts} times; giving up and refunding.",
                row.HistoryId, row.Attempts);

            var history = await _db.InterpretationHistories
                .FirstOrDefaultAsync(h => h.Id == row.HistoryId, ct);
            if (history != null && history.Status == "processing")
            {
                history.Status = "error";
                history.ErrorMessage = "Interpretation could not be completed after several attempts. Credit refunded.";
                if (history.CreditsConsumed > 0)
                {
                    history.CreditsConsumed = 0;
                    var user = await _db.Users.FirstOrDefaultAsync(u => u.Email == row.UserEmail, ct);
                    if (user != null) CreditLedger.RefundOne(user);
                }
            }

            _db.InterpretationJobs.Remove(row);
            await _db.SaveChangesAsync(ct);
        }
    }
}
