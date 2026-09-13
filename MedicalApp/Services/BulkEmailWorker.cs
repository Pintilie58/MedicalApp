using MedicalApp.Data;
using MedicalApp.Models;
using Microsoft.EntityFrameworkCore;

namespace MedicalApp.Services
{
    /// <summary>
    /// Sends the admin's bulk emails OUTSIDE the request that started them
    /// (Azure hosting, June 2026). Recipients are resolved once, at claim time,
    /// exactly like the old in-request loop did, and the row keeps the progress
    /// so the admin page can show "217 / 480 sent".
    ///
    /// Resumable: NextIndex advances as mails go out, so an instance recycle
    /// costs at most the recipient in flight — never the whole list, and never
    /// a second copy for people who already received it.
    /// </summary>
    public class BulkEmailWorker : BackgroundService
    {
        private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(3);
        private static readonly TimeSpan RenewInterval = TimeSpan.FromSeconds(30);

        /// <summary>Small pause between mails so the SMTP provider stays happy.</summary>
        private static readonly TimeSpan SendDelay = TimeSpan.FromMilliseconds(250);

        private static readonly string InstanceId =
            $"{Environment.MachineName}/{Environment.ProcessId}";

        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<BulkEmailWorker> _logger;

        public BulkEmailWorker(IServiceScopeFactory scopeFactory, ILogger<BulkEmailWorker> logger)
        {
            _scopeFactory = scopeFactory;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(12), stoppingToken); }
            catch (OperationCanceledException) { return; }

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    if (await RunOnceAsync(stoppingToken)) continue;
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Bulk email worker pass failed.");
                }

                try { await Task.Delay(PollInterval, stoppingToken); }
                catch (OperationCanceledException) { break; }
            }
        }

        /// <summary>
        /// One pass: claim a job and send what is left of it. Public so the
        /// probe can drive it deterministically instead of waiting on timers.
        /// Returns false when there was nothing to do.
        /// </summary>
        public async Task<bool> RunOnceAsync(CancellationToken ct)
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var email = scope.ServiceProvider.GetRequiredService<IEmailService>();

            var now = DateTime.UtcNow;
            var job = await db.BulkEmailJobs
                .Where(j => j.FinishedAt == null
                            && (j.Status == "queued"
                                || (j.Status == "running" && (j.LeaseUntil == null || j.LeaseUntil < now))))
                .OrderBy(j => j.CreatedAt)
                .FirstOrDefaultAsync(ct);
            if (job == null) return false;

            job.Status = "running";
            job.OwnerInstance = InstanceId;
            job.LeaseUntil = now.Add(LeaseDuration);
            job.Attempts++;
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateConcurrencyException) { return false; }   // another instance won

            var recipients = await ResolveRecipientsAsync(db, job.Filter, ct);
            job.Total = recipients.Count;
            await db.SaveChangesAsync(ct);

            _logger.LogInformation(
                "Bulk email job {Id}: sending to {Count} recipient(s) from index {Index}.",
                job.Id, recipients.Count, job.NextIndex);

            var body = WrapHtml(job.HtmlBody);
            var lastRenew = DateTime.UtcNow;

            for (var i = job.NextIndex; i < recipients.Count; i++)
            {
                if (ct.IsCancellationRequested)
                {
                    // Graceful shutdown: leave the row claimable again so another
                    // instance (or this one after restart) continues from here.
                    job.Status = "queued";
                    job.OwnerInstance = null;
                    job.LeaseUntil = null;
                    await db.SaveChangesAsync(CancellationToken.None);
                    return false;
                }

                try
                {
                    await email.SendEmailAsync(recipients[i], job.Subject, body);
                    job.Sent++;
                }
                catch (Exception ex)
                {
                    job.Failed++;
                    job.LastError = ex.Message.Length > 500 ? ex.Message[..500] : ex.Message;
                    _logger.LogError(ex, "Bulk email job {Id}: failed for {Email}.", job.Id, recipients[i]);
                }

                job.NextIndex = i + 1;

                // Persist progress (and renew the lease) regularly, not on every
                // single mail: the admin page polls, it does not need each row.
                if (DateTime.UtcNow - lastRenew >= RenewInterval || i == recipients.Count - 1)
                {
                    lastRenew = DateTime.UtcNow;
                    job.LeaseUntil = DateTime.UtcNow.Add(LeaseDuration);
                    await db.SaveChangesAsync(CancellationToken.None);
                }

                try { await Task.Delay(SendDelay, ct); }
                catch (OperationCanceledException) { /* handled at the top of the loop */ }
            }

            job.Status = job.Failed > 0 && job.Sent == 0 ? "failed" : "done";
            job.FinishedAt = DateTime.UtcNow;
            job.OwnerInstance = null;
            job.LeaseUntil = null;
            await db.SaveChangesAsync(CancellationToken.None);

            _logger.LogInformation("Bulk email job {Id}: finished ({Sent} sent, {Failed} failed).",
                job.Id, job.Sent, job.Failed);
            return true;
        }

        /// <summary>Same filters as the admin screen. Kept here so the worker is self-contained.</summary>
        public static async Task<List<string>> ResolveRecipientsAsync(
            AppDbContext db, string filter, CancellationToken ct = default)
        {
            var now = DateTime.UtcNow;
            IQueryable<User> q = db.Users.Where(u => !u.IsBlocked);

            q = filter switch
            {
                "paying" => q.Where(u => u.TotalPaid > 0),
                "with_credits" => q.Where(u => u.CreditRest > 0),
                "registered_last_30_days" => q.Where(u => u.DataC >= now.AddDays(-30)),
                "blocked" => db.Users.Where(u => u.IsBlocked),
                _ => q
            };

            return await q.OrderBy(u => u.Email).Select(u => u.Email).ToListAsync(ct);
        }

        /// <summary>Branded wrapper — identical to the one the admin screen used.</summary>
        public static string WrapHtml(string innerHtml) => $@"
<div style=""font-family:Arial,Helvetica,sans-serif;max-width:640px;margin:0 auto;padding:0;background:#ffffff;"">
  <div style=""background:#0d47a1;color:#ffffff;padding:20px 24px;border-radius:10px 10px 0 0;"">
    <h2 style=""margin:0;font-size:20px;font-weight:700;letter-spacing:0.3px;"">MyMedicalApp.NET</h2>
    <div style=""font-size:13px;opacity:0.9;margin-top:4px;"">Intelligent interpretation of medical analyses</div>
  </div>
  <div style=""padding:24px;color:#212529;font-size:15px;line-height:1.55;border:1px solid #e9ecef;border-top:0;"">
    {innerHtml}
  </div>
  <div style=""background:#f1f5fb;color:#0d47a1;padding:16px 24px;border-radius:0 0 10px 10px;text-align:center;font-size:13px;font-weight:600;border:1px solid #e9ecef;border-top:0;"">
    Be smart, take care of your health!
  </div>
</div>";
    }
}
