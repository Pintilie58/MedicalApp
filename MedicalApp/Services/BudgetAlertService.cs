using MedicalApp.Data;
using MedicalApp.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MedicalApp.Services
{
    /// <summary>
    /// Background service that watches the CURRENT calendar month's Gemini cost
    /// (computed from AiUsageLogs × GeminiPricing) and emails every admin in
    /// <c>AdminSettings.Emails</c> when the configured budget is exceeded.
    ///
    /// Design choices (intentional, do not change without thinking):
    ///   • Reads the SAME AiUsageLogs table the dashboard reads — so the email
    ///     and the dashboard always agree.
    ///   • Tracks last-sent timestamp in a marker file under <see cref="Path.GetTempPath"/>
    ///     (same pattern as DailySummaryService) so we survive process restarts
    ///     without spamming. No EF migration needed.
    ///   • Cooldown is applied AFTER the first alert in a month, so the admin
    ///     gets nudged at most once per CooldownHours (default 24h). When a
    ///     new month starts the cost calculation naturally drops below the
    ///     threshold so the alert auto-resets without touching the marker.
    ///   • Failures are logged and swallowed — this service must NEVER take
    ///     down the app.
    /// </summary>
    public class BudgetAlertService : BackgroundService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly BudgetAlertSettings _settings;
        private readonly ILogger<BudgetAlertService> _logger;

        public BudgetAlertService(
            IServiceScopeFactory scopeFactory,
            IOptions<BudgetAlertSettings> settings,
            ILogger<BudgetAlertService> logger)
        {
            _scopeFactory = scopeFactory;
            _settings = settings.Value;
            _logger = logger;
        }

        // -------------------------------------------------------------------
        // Last-alert marker (single line: ISO timestamp). Surviving restarts
        // matters because admins should never get duplicate nudges just
        // because the dev rebuilt the app.
        // -------------------------------------------------------------------
        private static string MarkerFilePath =>
            Path.Combine(Path.GetTempPath(), "MedicalApp_BudgetAlert_LastSent.txt");

        private static DateTime? ReadLastSentUtc()
        {
            try
            {
                if (!File.Exists(MarkerFilePath)) return null;
                var raw = File.ReadAllText(MarkerFilePath).Trim();
                return DateTime.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                    out var dt) ? dt : null;
            }
            catch { return null; }
        }

        private static void WriteLastSentUtc(DateTime utc)
        {
            try
            {
                File.WriteAllText(MarkerFilePath,
                    utc.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
            }
            catch { /* non-critical */ }
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (!_settings.Enabled)
            {
                _logger.LogInformation("BudgetAlertService is disabled via settings. Exiting.");
                return;
            }

            var interval = TimeSpan.FromMinutes(Math.Clamp(_settings.CheckIntervalMinutes, 5, 24 * 60));
            _logger.LogInformation(
                "BudgetAlertService started. Threshold ${Threshold:F2} USD, check every {Mins} min, cooldown {Hours}h.",
                _settings.MonthlyBudgetUsd, (int)interval.TotalMinutes, _settings.CooldownHours);

            // Wait a short while on boot so we don't compete with EF migrations
            // / DB warm-up if the service stack just started.
            try { await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken); }
            catch (OperationCanceledException) { return; }

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await CheckOnceAsync(stoppingToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "BudgetAlertService check failed.");
                }

                try { await Task.Delay(interval, stoppingToken); }
                catch (OperationCanceledException) { break; }
            }
        }

        /// <summary>
        /// Public entry-point so a future Admin "Test now" button can trigger
        /// an immediate evaluation. Not wired to a UI yet but cheap to expose.
        /// </summary>
        public Task RunNowAsync(CancellationToken ct = default) => CheckOnceAsync(ct);

        private async Task CheckOnceAsync(CancellationToken ct)
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var pricing = scope.ServiceProvider.GetRequiredService<IOptions<GeminiPricing>>().Value;
            var emailService = scope.ServiceProvider.GetRequiredService<IEmailService>();
            var adminSettings = scope.ServiceProvider.GetRequiredService<IOptions<AdminSettings>>().Value;

            // Month-to-date cost — same window the user thinks of when they
            // ask "how much have I spent this month?".
            var now = DateTime.UtcNow;
            var monthStart = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);

            var rows = await db.AiUsageLogs
                .AsNoTracking()
                .Where(h => h.CreatedAt >= monthStart
                         // Skip transient retry rows — they have 0 tokens and aren't
                         // billable; including them in the "calls" column of the
                         // alert email would mislead the admin about traffic volume.
                         && h.Status != "transient_error")
                .GroupBy(h => h.ModelUsed)
                .Select(g => new
                {
                    ModelUsed = g.Key,
                    InputTokens = g.Sum(h => (long?)h.InputTokens) ?? 0L,
                    OutputTokens = g.Sum(h => (long?)h.OutputTokens) ?? 0L,
                    Count = g.Count(),
                })
                .ToListAsync(ct);

            decimal totalCostUsd = 0m;
            var perModel = new List<(string ModelId, int Count, long In, long Out, decimal Cost)>();
            foreach (var r in rows)
            {
                var price = pricing.Resolve(r.ModelUsed);
                var cost = price.ComputeCost((int)r.InputTokens, (int)r.OutputTokens);
                totalCostUsd += cost;
                perModel.Add((r.ModelUsed ?? "(unknown)", r.Count, r.InputTokens, r.OutputTokens, cost));
            }

            if (totalCostUsd <= _settings.MonthlyBudgetUsd)
            {
                _logger.LogDebug(
                    "BudgetAlert: month-to-date ${Spent:F2} ≤ threshold ${Threshold:F2}. No alert.",
                    totalCostUsd, _settings.MonthlyBudgetUsd);
                return;
            }

            // Cooldown gate. We compare against the marker file so restarts
            // never re-trigger an alert that was already sent.
            var lastSent = ReadLastSentUtc();
            if (lastSent.HasValue && (now - lastSent.Value).TotalHours < _settings.CooldownHours)
            {
                _logger.LogDebug(
                    "BudgetAlert: budget exceeded but inside cooldown window (last sent {Sent:O}, cooldown {Hours}h).",
                    lastSent.Value, _settings.CooldownHours);
                return;
            }

            var recipients = (adminSettings.Emails ?? new List<string>())
                .Where(e => !string.IsNullOrWhiteSpace(e))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (recipients.Count == 0)
            {
                _logger.LogWarning("BudgetAlert: threshold exceeded but no admin emails configured.");
                return;
            }

            // Compose plain-but-clear HTML email. Mirrors the dashboard wording.
            var monthName = now.ToString("MMMM yyyy", System.Globalization.CultureInfo.GetCultureInfo("en-US"));
            var approxEur = Math.Round(totalCostUsd / 1.10m, 2);
            var thresholdEur = Math.Round(_settings.MonthlyBudgetUsd / 1.10m, 2);
            var body = new System.Text.StringBuilder();
            body.Append("<h2 style=\"font-family:Arial,sans-serif;color:#b02a37\">⚠️ MedicalApp — Gemini budget alert</h2>");
            body.Append("<p style=\"font-family:Arial,sans-serif\">The month-to-date Gemini cost for <strong>").Append(monthName).Append("</strong> has crossed your configured budget.</p>");
            body.Append("<table style=\"font-family:Arial,sans-serif;border-collapse:collapse;\">");
            body.Append("<tr><td style=\"padding:4px 12px 4px 0;color:#555\">Spent so far:</td><td style=\"padding:4px 0\"><strong>$").Append(totalCostUsd.ToString("F2")).Append(" USD</strong> &nbsp; (~€").Append(approxEur.ToString("F2")).Append(")</td></tr>");
            body.Append("<tr><td style=\"padding:4px 12px 4px 0;color:#555\">Configured budget:</td><td style=\"padding:4px 0\">$").Append(_settings.MonthlyBudgetUsd.ToString("F2")).Append(" USD &nbsp; (~€").Append(thresholdEur.ToString("F2")).Append(")</td></tr>");
            body.Append("<tr><td style=\"padding:4px 12px 4px 0;color:#555\">Over budget by:</td><td style=\"padding:4px 0;color:#b02a37\"><strong>$").Append((totalCostUsd - _settings.MonthlyBudgetUsd).ToString("F2")).Append(" USD</strong></td></tr>");
            body.Append("</table>");

            if (perModel.Count > 0)
            {
                body.Append("<h3 style=\"font-family:Arial,sans-serif;margin-top:24px\">Breakdown by model</h3>");
                body.Append("<table style=\"font-family:Arial,sans-serif;border-collapse:collapse;font-size:14px\">");
                body.Append("<thead><tr style=\"background:#f1f3f5\"><th style=\"padding:6px 12px;border:1px solid #dee2e6;text-align:left\">Model</th><th style=\"padding:6px 12px;border:1px solid #dee2e6;text-align:right\">Calls</th><th style=\"padding:6px 12px;border:1px solid #dee2e6;text-align:right\">Input tokens</th><th style=\"padding:6px 12px;border:1px solid #dee2e6;text-align:right\">Output tokens</th><th style=\"padding:6px 12px;border:1px solid #dee2e6;text-align:right\">Cost</th></tr></thead><tbody>");
                foreach (var m in perModel.OrderByDescending(m => m.Cost))
                {
                    body.Append("<tr>")
                        .Append("<td style=\"padding:6px 12px;border:1px solid #dee2e6\"><code>").Append(System.Net.WebUtility.HtmlEncode(m.ModelId)).Append("</code></td>")
                        .Append("<td style=\"padding:6px 12px;border:1px solid #dee2e6;text-align:right\">").Append(m.Count).Append("</td>")
                        .Append("<td style=\"padding:6px 12px;border:1px solid #dee2e6;text-align:right\">").Append(m.In.ToString("N0")).Append("</td>")
                        .Append("<td style=\"padding:6px 12px;border:1px solid #dee2e6;text-align:right\">").Append(m.Out.ToString("N0")).Append("</td>")
                        .Append("<td style=\"padding:6px 12px;border:1px solid #dee2e6;text-align:right\"><strong>$").Append(m.Cost.ToString("F2")).Append("</strong></td>")
                        .Append("</tr>");
                }
                body.Append("</tbody></table>");
            }

            body.Append("<h3 style=\"font-family:Arial,sans-serif;margin-top:24px\">What you can do</h3>");
            body.Append("<ul style=\"font-family:Arial,sans-serif;font-size:14px\">");
            body.Append("<li>Open the <strong>Admin Dashboard</strong> &rarr; AI usage card for a full breakdown.</li>");
            body.Append("<li>Check the <strong>Reliability widget</strong> on the dashboard — if Flash error rate is high, fallback to Pro is pulling cost up.</li>");
            body.Append("<li>Adjust the budget in <code>appsettings.json</code> → <code>BudgetAlert.MonthlyBudgetUsd</code> if your usage just grew naturally.</li>");
            body.Append("<li>Lower the budget alert cooldown if you want the next reminder sooner: <code>BudgetAlert.CooldownHours</code>.</li>");
            body.Append("</ul>");
            body.Append("<p style=\"font-family:Arial,sans-serif;font-size:12px;color:#888;margin-top:24px\">This is an automated message from MedicalApp. You will not receive another budget alert for at least ")
                .Append(_settings.CooldownHours).Append(" hours, or until ").Append(monthName).Append(" rolls over.</p>");

            var subject = $"⚠️ MedicalApp — Gemini budget exceeded (${totalCostUsd:F2} / ${_settings.MonthlyBudgetUsd:F2} USD)";
            int sent = 0;
            foreach (var to in recipients)
            {
                try
                {
                    await emailService.SendEmailAsync(to, subject, body.ToString());
                    sent++;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "BudgetAlert: failed to send email to {Recipient}.", to);
                }
            }

            if (sent > 0)
            {
                WriteLastSentUtc(now);
                _logger.LogWarning(
                    "BudgetAlert: sent {Count} email(s). Month-to-date ${Spent:F2} vs threshold ${Threshold:F2}.",
                    sent, totalCostUsd, _settings.MonthlyBudgetUsd);
            }
        }
    }
}
