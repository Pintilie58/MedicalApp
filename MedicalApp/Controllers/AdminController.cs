using MedicalApp.Attributes;
using MedicalApp.Data;
using MedicalApp.Models;
using MedicalApp.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MedicalApp.Controllers
{
    [AdminAuthorize]
    public class AdminController : Controller
    {
        private readonly AppDbContext _db;
        private readonly IEmailService _emailService;
        private readonly ILogger<AdminController> _logger;

        public AdminController(
            AppDbContext db,
            IEmailService emailService,
            ILogger<AdminController> logger)
        {
            _db = db;
            _emailService = emailService;
            _logger = logger;
        }

        // =====================================================================
        //  Dashboard  (stats overview)
        // =====================================================================
        public async Task<IActionResult> Index()
        {
            var now = DateTime.UtcNow;
            var cutoff7 = now.AddDays(-7);
            var cutoff30 = now.AddDays(-30);

            var totalUsers = await _db.Users.CountAsync();
            var payingUsers = await _db.Users.CountAsync(u => u.TotalPaid > 0);
            var activeUsers = await _db.Users.CountAsync(u => u.CreditConsum > 0);
            var blockedUsers = await _db.Users.CountAsync(u => u.IsBlocked);
            var newUsers7 = await _db.Users.CountAsync(u => u.DataC >= cutoff7);

            var totalBought = await _db.Users.SumAsync(u => (int?)u.Credite) ?? 0;
            var totalConsumed = await _db.Users.SumAsync(u => (int?)u.CreditConsum) ?? 0;
            var totalRemaining = await _db.Users.SumAsync(u => (int?)u.CreditRest) ?? 0;

            var totalRevenue = await _db.Purchases.SumAsync(p => (decimal?)p.AmountEur) ?? 0m;
            var revenue30 = await _db.Purchases
                .Where(p => p.PurchasedAt >= cutoff30)
                .SumAsync(p => (decimal?)p.AmountEur) ?? 0m;
            var purchases30 = await _db.Purchases.CountAsync(p => p.PurchasedAt >= cutoff30);

            var activePromos = await _db.PromoCodes.CountAsync(p =>
                p.IsActive && p.ValidFrom <= now && p.ValidUntil >= now);

            var topSpenders = await _db.Users
                .Where(u => u.TotalPaid > 0)
                .OrderByDescending(u => u.TotalPaid)
                .Take(10)
                .Select(u => new TopSpender
                {
                    Email = u.Email,
                    TotalPaid = u.TotalPaid,
                    Credite = u.Credite,
                    CreditConsum = u.CreditConsum
                })
                .ToListAsync();

            var dailyRaw = await _db.Purchases
                .Where(p => p.PurchasedAt >= cutoff30)
                .GroupBy(p => p.PurchasedAt.Date)
                .Select(g => new DailyRevenue
                {
                    Date = g.Key,
                    Amount = g.Sum(x => x.AmountEur),
                    Count = g.Count()
                })
                .ToListAsync();

            // Fill missing days with 0 for a continuous chart
            var daily = Enumerable.Range(0, 30)
                .Select(i => cutoff30.Date.AddDays(i))
                .Select(d => dailyRaw.FirstOrDefault(x => x.Date == d) ?? new DailyRevenue { Date = d })
                .OrderBy(x => x.Date)
                .ToList();

            var vm = new AdminDashboardViewModel
            {
                TotalUsers = totalUsers,
                PayingUsers = payingUsers,
                ActiveUsers = activeUsers,
                BlockedUsers = blockedUsers,
                NewUsersLast7Days = newUsers7,
                TotalCreditsPurchased = totalBought,
                TotalCreditsConsumed = totalConsumed,
                TotalCreditsRemaining = totalRemaining,
                TotalRevenueEur = totalRevenue,
                RevenueLast30DaysEur = revenue30,
                PurchasesLast30Days = purchases30,
                ActivePromoCodes = activePromos,
                TopSpenders = topSpenders,
                RevenueChart = daily
            };

            return View(vm);
        }

        // =====================================================================
        //  Users list + search
        // =====================================================================
        [HttpGet]
        public async Task<IActionResult> Users(string? q = null, int page = 1)
        {
            const int pageSize = 25;
            page = Math.Max(1, page);

            IQueryable<User> query = _db.Users.OrderByDescending(u => u.DataC);
            if (!string.IsNullOrWhiteSpace(q))
            {
                var needle = q.Trim().ToLower();
                query = query.Where(u => u.Email.ToLower().Contains(needle));
            }

            var total = await query.CountAsync();
            var list = await query
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync();

            ViewBag.Query = q;
            ViewBag.Page = page;
            ViewBag.PageSize = pageSize;
            ViewBag.Total = total;
            ViewBag.TotalPages = (int)Math.Ceiling((double)total / pageSize);

            return View(list);
        }

        // =====================================================================
        //  User detail
        // =====================================================================
        [HttpGet]
        public async Task<IActionResult> UserDetail(string email)
        {
            if (string.IsNullOrWhiteSpace(email))
                return RedirectToAction(nameof(Users));

            var e = email.Trim().ToLowerInvariant();
            var user = await _db.Users.FirstOrDefaultAsync(u => u.Email == e);
            if (user == null)
            {
                TempData["ErrorMessage"] = "User not found.";
                return RedirectToAction(nameof(Users));
            }

            var purchases = await _db.Purchases
                .Where(p => p.UserEmail == e)
                .OrderByDescending(p => p.PurchasedAt)
                .ToListAsync();

            var history = await _db.InterpretationHistories
                .Where(h => h.UserEmail == e)
                .OrderByDescending(h => h.CreatedAt)
                .Take(50)
                .ToListAsync();

            ViewBag.Purchases = purchases;
            ViewBag.History = history;
            return View(user);
        }

        // =====================================================================
        //  Give free credits (manual)
        // =====================================================================
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> GiveCredits(string email, int credits, string? reason)
        {
            if (credits <= 0 || credits > 1000)
            {
                TempData["ErrorMessage"] = "Credits must be between 1 and 1000.";
                return RedirectToAction(nameof(UserDetail), new { email });
            }

            var e = (email ?? string.Empty).Trim().ToLowerInvariant();
            var user = await _db.Users.FirstOrDefaultAsync(u => u.Email == e);
            if (user == null)
            {
                TempData["ErrorMessage"] = "User not found.";
                return RedirectToAction(nameof(Users));
            }

            user.Credite += credits;
            user.CreditRest = user.Credite - user.CreditConsum;
            await _db.SaveChangesAsync();

            _logger.LogInformation("Admin gave {Credits} free credits to {Email}. Reason: {Reason}",
                credits, e, reason ?? "(none)");
            TempData["SuccessMessage"] = $"Added {credits} credits to {e}.";
            return RedirectToAction(nameof(UserDetail), new { email = e });
        }

        // =====================================================================
        //  Reset password for a user (sends new random password by email)
        // =====================================================================
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ResetUserPassword(string email)
        {
            var e = (email ?? string.Empty).Trim().ToLowerInvariant();
            var user = await _db.Users.FirstOrDefaultAsync(u => u.Email == e);
            if (user == null)
            {
                TempData["ErrorMessage"] = "User not found.";
                return RedirectToAction(nameof(Users));
            }

            var newPassword = PasswordGenerator.GenerateStrong(12);
            user.Parola = BCrypt.Net.BCrypt.HashPassword(newPassword);
            user.PasswordResetToken = null;
            user.PasswordResetTokenExpiry = null;
            await _db.SaveChangesAsync();

            try
            {
                var subject = "MedicalApp - Your password has been reset by an administrator";
                var body = $@"<div style='font-family:Arial;max-width:600px;margin:0 auto;padding:20px;'>
<h2 style='color:#0d47a1;'>MedicalApp</h2>
<p>Hello,</p>
<p>Your password has been reset by an administrator.</p>
<p>Your new temporary password is:</p>
<div style='background:#f8f9fa;border:2px solid #0d47a1;border-radius:10px;padding:20px;text-align:center;margin:24px 0;'>
  <span style='font-family:monospace;font-size:24px;font-weight:bold;color:#0d47a1;letter-spacing:2px;'>
    {System.Net.WebUtility.HtmlEncode(newPassword)}
  </span>
</div>
<p>Please log in with this password and change it immediately from your account settings.</p>
<hr/><p style='color:#6c757d;font-size:0.9em;'>MedicalApp - your medical analysis interpreter.</p>
</div>";
                await _emailService.SendEmailAsync(e, subject, body);
                TempData["SuccessMessage"] = $"New password emailed to {e}.";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to email new password to {Email}", e);
                TempData["ErrorMessage"] = $"Password was reset but email failed: {ex.Message}";
            }

            return RedirectToAction(nameof(UserDetail), new { email = e });
        }

        // =====================================================================
        //  Block / Unblock user
        // =====================================================================
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ToggleBlock(string email)
        {
            var e = (email ?? string.Empty).Trim().ToLowerInvariant();
            var user = await _db.Users.FirstOrDefaultAsync(u => u.Email == e);
            if (user == null)
            {
                TempData["ErrorMessage"] = "User not found.";
                return RedirectToAction(nameof(Users));
            }

            // Don't allow blocking an admin (safety)
            if (user.IsAdmin && !user.IsBlocked)
            {
                TempData["ErrorMessage"] = "Cannot block an admin account.";
                return RedirectToAction(nameof(UserDetail), new { email = e });
            }

            user.IsBlocked = !user.IsBlocked;
            await _db.SaveChangesAsync();

            TempData["SuccessMessage"] = user.IsBlocked
                ? $"{e} is now blocked."
                : $"{e} is unblocked.";
            return RedirectToAction(nameof(UserDetail), new { email = e });
        }

        // =====================================================================
        //  Bulk email  (with filter)
        // =====================================================================
        [HttpGet]
        public async Task<IActionResult> SendEmail(string filter = "all")
        {
            var recipients = await ResolveRecipients(filter);
            return View(new BulkEmailViewModel
            {
                Filter = filter,
                RecipientsCount = recipients.Count
            });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SendEmail(BulkEmailViewModel model)
        {
            if (!ModelState.IsValid)
            {
                model.RecipientsCount = (await ResolveRecipients(model.Filter)).Count;
                return View(model);
            }

            var recipients = await ResolveRecipients(model.Filter);
            model.RecipientsCount = recipients.Count;

            if (recipients.Count == 0)
            {
                TempData["ErrorMessage"] = "No recipients match the selected filter.";
                return View(model);
            }

            int sent = 0, failed = 0;
            foreach (var email in recipients)
            {
                try
                {
                    await _emailService.SendEmailAsync(email, model.Subject, model.HtmlBody);
                    sent++;
                }
                catch (Exception ex)
                {
                    failed++;
                    _logger.LogError(ex, "Bulk email failed for {Email}", email);
                }
            }

            TempData["SuccessMessage"] = $"Email sent to {sent} users (failed: {failed}).";
            return RedirectToAction(nameof(SendEmail), new { filter = model.Filter });
        }

        private async Task<List<string>> ResolveRecipients(string filter)
        {
            var now = DateTime.UtcNow;
            IQueryable<User> q = _db.Users.Where(u => !u.IsBlocked);

            q = filter switch
            {
                "paying"          => q.Where(u => u.TotalPaid > 0),
                "with_credits"    => q.Where(u => u.CreditRest > 0),
                "registered_last_30_days" => q.Where(u => u.DataC >= now.AddDays(-30)),
                "blocked"         => _db.Users.Where(u => u.IsBlocked),
                _                 => q
            };

            return await q.Select(u => u.Email).ToListAsync();
        }

        // =====================================================================
        //  Promo codes
        // =====================================================================
        [HttpGet]
        public async Task<IActionResult> PromoCodes()
        {
            var list = await _db.PromoCodes
                .OrderByDescending(p => p.CreatedAt)
                .ToListAsync();
            return View(list);
        }

        [HttpGet]
        public IActionResult NewPromoCode()
            => View("PromoCodeForm", new PromoCodeViewModel
            {
                ValidFrom = DateTime.UtcNow,
                ValidUntil = DateTime.UtcNow.AddMonths(1)
            });

        [HttpGet]
        public async Task<IActionResult> EditPromoCode(int id)
        {
            var p = await _db.PromoCodes.FindAsync(id);
            if (p == null) return RedirectToAction(nameof(PromoCodes));

            return View("PromoCodeForm", new PromoCodeViewModel
            {
                Id = p.Id,
                Code = p.Code,
                CreditsToAdd = p.CreditsToAdd,
                ValidFrom = p.ValidFrom,
                ValidUntil = p.ValidUntil,
                MaxUses = p.MaxUses,
                IsActive = p.IsActive
            });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SavePromoCode(PromoCodeViewModel model)
        {
            if (!ModelState.IsValid) return View("PromoCodeForm", model);

            if (model.ValidUntil <= model.ValidFrom)
            {
                ModelState.AddModelError(nameof(model.ValidUntil), "End date must be after start date.");
                return View("PromoCodeForm", model);
            }

            var codeNorm = model.Code.Trim();

            if (model.Id == 0)
            {
                // create
                var exists = await _db.PromoCodes.AnyAsync(p => p.Code == codeNorm);
                if (exists)
                {
                    ModelState.AddModelError(nameof(model.Code), "A promo code with this name already exists.");
                    return View("PromoCodeForm", model);
                }
                _db.PromoCodes.Add(new PromoCode
                {
                    Code = codeNorm,
                    CreditsToAdd = model.CreditsToAdd,
                    ValidFrom = model.ValidFrom,
                    ValidUntil = model.ValidUntil,
                    MaxUses = model.MaxUses,
                    IsActive = model.IsActive,
                    CreatedAt = DateTime.UtcNow
                });
                TempData["SuccessMessage"] = $"Promo code {codeNorm} created.";
            }
            else
            {
                var p = await _db.PromoCodes.FindAsync(model.Id);
                if (p == null) return RedirectToAction(nameof(PromoCodes));

                p.Code = codeNorm;
                p.CreditsToAdd = model.CreditsToAdd;
                p.ValidFrom = model.ValidFrom;
                p.ValidUntil = model.ValidUntil;
                p.MaxUses = model.MaxUses;
                p.IsActive = model.IsActive;
                TempData["SuccessMessage"] = $"Promo code {codeNorm} updated.";
            }

            await _db.SaveChangesAsync();
            return RedirectToAction(nameof(PromoCodes));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeletePromoCode(int id)
        {
            var p = await _db.PromoCodes.FindAsync(id);
            if (p == null) return RedirectToAction(nameof(PromoCodes));

            _db.PromoCodes.Remove(p);
            await _db.SaveChangesAsync();
            TempData["SuccessMessage"] = $"Promo code {p.Code} deleted.";
            return RedirectToAction(nameof(PromoCodes));
        }
    }
}
