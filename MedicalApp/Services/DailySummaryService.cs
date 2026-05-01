using MedicalApp.Data;
using MedicalApp.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MedicalApp.Services
{
    /// <summary>
    /// Background service that sends a daily summary email to every configured admin
    /// at a fixed local hour (default 09:00). The summary covers activity in the
    /// last 24 hours (purchases, revenue, new users, interpretations).
    ///
    /// Behavior:
    ///  - On start, waits until the next configured hour (never sends immediately).
    ///  - Fails silently (with log) if SMTP errors occur, then waits for the next day.
    ///  - Exits immediately if disabled via settings.
    /// </summary>
    public class DailySummaryService : BackgroundService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly DailySummarySettings _settings;
        private readonly ILogger<DailySummaryService> _logger;

        public DailySummaryService(
            IServiceScopeFactory scopeFactory,
            IOptions<DailySummarySettings> settings,
            ILogger<DailySummaryService> logger)
        {
            _scopeFactory = scopeFactory;
            _settings = settings.Value;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (!_settings.Enabled)
            {
                _logger.LogInformation("Daily summary service is disabled via settings. Exiting.");
                return;
            }

            var hour = Math.Clamp(_settings.HourOfDayLocal, 0, 23);
            _logger.LogInformation("Daily summary service started. Will run every day at {Hour:00}:00 local time.", hour);

            // ---- CATCH-UP on startup ----
            // Local apps are not always running - if the app was off at 09:00, we miss
            // the trigger. To compensate, on startup we check whether today's summary
            // has already been sent. If not, AND we're already past the configured
            // hour for today, we send it immediately.
            try
            {
                if (DateTime.Now.Hour >= hour && !HasSummaryBeenSentToday())
                {
                    _logger.LogInformation(
                        "Catch-up: today's daily summary was not sent yet. Sending now.");
                    await SendSummaryAsync(stoppingToken);
                }
                else
                {
                    _logger.LogInformation(
                        "Catch-up: no action needed (already sent today or before scheduled hour).");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Catch-up daily summary failed. Will try at scheduled time.");
            }

            while (!stoppingToken.IsCancellationRequested)
            {
                var nextRun = ComputeNextRun(hour);
                var delay = nextRun - DateTime.Now;
                if (delay < TimeSpan.Zero) delay = TimeSpan.FromMinutes(1);

                _logger.LogInformation("Next daily summary scheduled for {Next} (in {Hours:F1}h)",
                    nextRun.ToString("dd/MM/yyyy HH:mm"), delay.TotalHours);

                try
                {
                    await Task.Delay(delay, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break; // app shutting down
                }

                try
                {
                    if (HasSummaryBeenSentToday())
                    {
                        _logger.LogInformation("Daily summary already sent today (by catch-up). Skipping scheduled run.");
                    }
                    else
                    {
                        await SendSummaryAsync(stoppingToken);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Daily summary send failed. Will try again tomorrow.");
                }
            }
        }

        /// <summary>Returns the next DateTime at the given hour (today if still in the future, otherwise tomorrow).</summary>
        private static DateTime ComputeNextRun(int hour)
        {
            var now = DateTime.Now;
            var target = now.Date.AddHours(hour);
            if (target <= now) target = target.AddDays(1);
            return target;
        }

        // -------------------------------------------------------------------
        // "Already sent today?" tracking - simple flat file in TEMP folder.
        // -------------------------------------------------------------------
        private static string MarkerFilePath =>
            Path.Combine(Path.GetTempPath(), "MedicalApp_DailySummary_LastSent.txt");

        private static bool HasSummaryBeenSentToday()
        {
            try
            {
                if (!File.Exists(MarkerFilePath)) return false;
                var content = File.ReadAllText(MarkerFilePath).Trim();
                return DateTime.TryParse(content, out var dt) && dt.Date == DateTime.Now.Date;
            }
            catch { return false; }
        }

        private static void MarkSummaryAsSentToday()
        {
            try
            {
                File.WriteAllText(MarkerFilePath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            }
            catch { /* swallow - non-critical */ }
        }

        /// <summary>Public entry point so the Admin "Send now" button can trigger a send manually.</summary>
        public Task RunNowAsync(CancellationToken ct = default) => SendSummaryAsync(ct);
        }

        private async Task SendSummaryAsync(CancellationToken ct)
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var emailService = scope.ServiceProvider.GetRequiredService<IEmailService>();
            var adminSettings = scope.ServiceProvider
                .GetRequiredService<IOptions<AdminSettings>>().Value;

            var admins = adminSettings.Emails?
                .Where(e => !string.IsNullOrWhiteSpace(e))
                .Select(e => e.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList() ?? new List<string>();

            if (admins.Count == 0)
            {
                _logger.LogInformation("No admin emails configured - skipping daily summary.");
                return;
            }

            // ----- collect stats over the last 24 hours -----
            var cutoffUtc = DateTime.UtcNow.AddDays(-1);

            var newUsers = await db.Users
                .Where(u => u.DataC >= cutoffUtc)
                .Select(u => u.Email)
                .ToListAsync(ct);

            var purchases = await db.Purchases
                .Where(p => p.PurchasedAt >= cutoffUtc)
                .ToListAsync(ct);

            var interpretations = await db.InterpretationHistories
                .Where(h => h.CreatedAt >= cutoffUtc)
                .ToListAsync(ct);

            // ----- aggregates -----
            var purchaseCount = purchases.Count;
            var revenue = purchases.Sum(p => p.AmountEur);
            var creditsSold = purchases.Sum(p => p.CreditsAdded);

            var interpSuccess = interpretations.Count(h => h.Status == "success");
            var interpRejected = interpretations.Count(h => h.Status == "rejected");
            var interpError = interpretations.Count(h => h.Status == "error");

            var topBuyerYesterday = purchases
                .GroupBy(p => p.UserEmail)
                .Select(g => new { Email = g.Key, Amount = g.Sum(x => x.AmountEur), Count = g.Count() })
                .OrderByDescending(x => x.Amount)
                .FirstOrDefault();

            // ----- running totals -----
            var totalUsers = await db.Users.CountAsync(ct);
            var lifetimeRevenue = await db.Purchases.SumAsync(p => (decimal?)p.AmountEur, ct) ?? 0m;
            var activePromoCodes = await db.PromoCodes
                .CountAsync(p => p.IsActive && p.ValidFrom <= DateTime.UtcNow && p.ValidUntil >= DateTime.UtcNow, ct);

            var summary = new DailySummaryData
            {
                NewUsers = newUsers,
                PurchaseCount = purchaseCount,
                Revenue = revenue,
                CreditsSold = creditsSold,
                InterpretationsSuccess = interpSuccess,
                InterpretationsRejected = interpRejected,
                InterpretationsError = interpError,
                TopBuyerEmail = topBuyerYesterday?.Email,
                TopBuyerAmount = topBuyerYesterday?.Amount ?? 0m,
                TotalUsers = totalUsers,
                LifetimeRevenue = lifetimeRevenue,
                ActivePromoCodes = activePromoCodes
            };

            var subject = $"[MedicalApp] Rezumat zilnic - {DateTime.Now:dd/MM/yyyy}";
            var body = BuildEmailBody(summary);

            int sentCount = 0;
            foreach (var adminEmail in admins)
            {
                try
                {
                    await emailService.SendEmailAsync(adminEmail, subject, body);
                    sentCount++;
                    _logger.LogInformation("Daily summary sent to admin {Admin}", adminEmail);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to send daily summary to admin {Admin}", adminEmail);
                }
            }

            // Only mark as sent if at least one admin received it - otherwise we want
            // the next startup catch-up to retry.
            if (sentCount > 0)
            {
                MarkSummaryAsSentToday();
            }
        }

        private static string BuildEmailBody(DailySummaryData s)
        {
            var hasActivity = s.NewUsers.Count > 0
                || s.PurchaseCount > 0
                || s.InterpretationsSuccess + s.InterpretationsRejected + s.InterpretationsError > 0;

            var newUsersList = s.NewUsers.Count == 0
                ? "<em style='color:#6c757d;'>Niciun utilizator nou.</em>"
                : "<ul style='margin:8px 0 0 0;padding-left:20px;'>"
                  + string.Join("", s.NewUsers.Take(20).Select(e =>
                      $"<li style='padding:2px 0;'>{System.Net.WebUtility.HtmlEncode(e)}</li>"))
                  + (s.NewUsers.Count > 20 ? $"<li style='color:#6c757d;'><em>... si inca {s.NewUsers.Count - 20}</em></li>" : "")
                  + "</ul>";

            var topBuyerRow = string.IsNullOrEmpty(s.TopBuyerEmail)
                ? ""
                : $@"<tr style='background:#fff9e6;'>
                      <td style='padding:10px 12px;border:1px solid #e9ecef;font-weight:600;'>&#127942; Top cumparator</td>
                      <td style='padding:10px 12px;border:1px solid #e9ecef;'>
                        <strong>{System.Net.WebUtility.HtmlEncode(s.TopBuyerEmail)}</strong> — {s.TopBuyerAmount:F2} EUR
                      </td>
                    </tr>";

            var activityBadge = hasActivity
                ? "<span style='background:#198754;color:#fff;padding:4px 10px;border-radius:12px;font-size:12px;'>ACTIVITATE</span>"
                : "<span style='background:#6c757d;color:#fff;padding:4px 10px;border-radius:12px;font-size:12px;'>ZI LINISTITA</span>";

            return $@"
<div style=""font-family:Arial,Helvetica,sans-serif;max-width:680px;margin:0 auto;padding:0;background:#ffffff;"">
  <div style=""background:#0d47a1;color:#ffffff;padding:20px 24px;border-radius:10px 10px 0 0;"">
    <h2 style=""margin:0;font-size:20px;font-weight:700;"">&#128202; Rezumat zilnic MedicalApp</h2>
    <div style=""font-size:13px;opacity:0.9;margin-top:4px;"">
      Ultimele 24 ore &nbsp;|&nbsp; Generat la {DateTime.Now:dd/MM/yyyy HH:mm} &nbsp; {activityBadge}
    </div>
  </div>

  <div style=""padding:24px;color:#212529;font-size:15px;line-height:1.6;border:1px solid #e9ecef;border-top:0;"">

    <!-- Financial -->
    <h3 style=""margin:0 0 10px 0;color:#0d47a1;font-size:16px;"">&#128181; Vanzari</h3>
    <table style=""width:100%;border-collapse:collapse;margin:0 0 24px 0;font-size:14px;"">
      <tr>
        <td style=""padding:10px 12px;border:1px solid #e9ecef;font-weight:600;width:260px;"">Numar achizitii</td>
        <td style=""padding:10px 12px;border:1px solid #e9ecef;font-size:16px;font-weight:700;color:#0d6efd;"">{s.PurchaseCount}</td>
      </tr>
      <tr style=""background:#f8f9fa;"">
        <td style=""padding:10px 12px;border:1px solid #e9ecef;font-weight:600;"">Incasari (EUR)</td>
        <td style=""padding:10px 12px;border:1px solid #e9ecef;font-size:16px;font-weight:700;color:#198754;"">{s.Revenue:F2} EUR</td>
      </tr>
      <tr>
        <td style=""padding:10px 12px;border:1px solid #e9ecef;font-weight:600;"">Credite vandute</td>
        <td style=""padding:10px 12px;border:1px solid #e9ecef;"">+{s.CreditsSold}</td>
      </tr>
      {topBuyerRow}
    </table>

    <!-- Users -->
    <h3 style=""margin:0 0 10px 0;color:#0d47a1;font-size:16px;"">&#128100; Utilizatori noi ({s.NewUsers.Count})</h3>
    <div style=""background:#f8f9fa;border:1px solid #e9ecef;padding:14px 18px;border-radius:6px;margin-bottom:24px;font-size:14px;"">
      {newUsersList}
    </div>

    <!-- Interpretations -->
    <h3 style=""margin:0 0 10px 0;color:#0d47a1;font-size:16px;"">&#129514; Interpretari analize medicale</h3>
    <table style=""width:100%;border-collapse:collapse;margin:0 0 24px 0;font-size:14px;"">
      <tr>
        <td style=""padding:10px 12px;border:1px solid #e9ecef;font-weight:600;width:260px;color:#198754;"">&#10004; Reusite</td>
        <td style=""padding:10px 12px;border:1px solid #e9ecef;font-weight:700;"">{s.InterpretationsSuccess}</td>
      </tr>
      <tr style=""background:#f8f9fa;"">
        <td style=""padding:10px 12px;border:1px solid #e9ecef;font-weight:600;color:#6c757d;"">&#9888;&#65039; Respinse (non-medical)</td>
        <td style=""padding:10px 12px;border:1px solid #e9ecef;"">{s.InterpretationsRejected}</td>
      </tr>
      <tr>
        <td style=""padding:10px 12px;border:1px solid #e9ecef;font-weight:600;color:#dc3545;"">&#10006; Erori</td>
        <td style=""padding:10px 12px;border:1px solid #e9ecef;"">{s.InterpretationsError}</td>
      </tr>
    </table>

    <!-- Lifetime -->
    <div style=""background:#eef5ff;border-left:4px solid #0d47a1;padding:14px 18px;border-radius:6px;"">
      <div style=""font-weight:600;color:#0d47a1;margin-bottom:8px;"">&#128200; Situatie generala</div>
      <div style=""font-size:14px;color:#495057;"">
        &#8226; Utilizatori total: <strong>{s.TotalUsers}</strong><br/>
        &#8226; Incasari totale de la lansare: <strong>{s.LifetimeRevenue:F2} EUR</strong><br/>
        &#8226; Coduri promo active: <strong>{s.ActivePromoCodes}</strong>
      </div>
    </div>

    <p style=""margin:24px 0 0 0;color:#6c757d;font-size:13px;text-align:center;"">
      Acest email este generat automat de MedicalApp in fiecare zi la ora programata.
    </p>
  </div>

  <div style=""background:#f1f5fb;color:#0d47a1;padding:14px 24px;border-radius:0 0 10px 10px;text-align:center;font-size:13px;font-weight:600;border:1px solid #e9ecef;border-top:0;"">
    MedicalApp &mdash; Panou administrator
  </div>
</div>";
        }

        private sealed class DailySummaryData
        {
            public List<string> NewUsers { get; set; } = new();
            public int PurchaseCount { get; set; }
            public decimal Revenue { get; set; }
            public int CreditsSold { get; set; }
            public int InterpretationsSuccess { get; set; }
            public int InterpretationsRejected { get; set; }
            public int InterpretationsError { get; set; }
            public string? TopBuyerEmail { get; set; }
            public decimal TopBuyerAmount { get; set; }
            public int TotalUsers { get; set; }
            public decimal LifetimeRevenue { get; set; }
            public int ActivePromoCodes { get; set; }
        }
    }
}
