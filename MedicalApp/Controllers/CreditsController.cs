using MedicalApp.Data;
using MedicalApp.Models;
using MedicalApp.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MedicalApp.Controllers
{
    public class CreditsController : Controller
    {
        private readonly AppDbContext _db;
        private readonly IEmailService _emailService;
        private readonly AdminSettings _adminSettings;
        private readonly ICamFileStore _camFileStore;
        private readonly ILogger<CreditsController> _logger;

        public CreditsController(
            AppDbContext db,
            IEmailService emailService,
            IOptions<AdminSettings> adminOptions,
            ICamFileStore camFileStore,
            ILogger<CreditsController> logger)
        {
            _db = db;
            _emailService = emailService;
            _adminSettings = adminOptions.Value;
            _camFileStore = camFileStore;
            _logger = logger;
        }

        // ---------- Buy: show package selection ----------

        [HttpGet]
        public async Task<IActionResult> Buy()
        {
            var email = HttpContext.Session.GetString("UserEmail");
            if (string.IsNullOrEmpty(email))
                return RedirectToAction("Index", "Home");

            var user = await _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Email == email);
            ViewBag.PaidRemaining = user?.CreditRest ?? 0;
            ViewBag.BonusRemaining = user?.BonusCreditsRemaining ?? 0;
            ViewBag.TotalAvailable = user?.TotalAvailableCredits ?? 0;

            // CAM users see CAM packages, Individual users see B2C packages.
            // Defaults to "Individual" if the field is missing for any reason.
            var audience = string.Equals(user?.UserType, "Clinic", StringComparison.OrdinalIgnoreCase)
                ? "Clinic" : "Individual";
            ViewBag.Audience = audience;
            return View(CreditPackages.ForAudience(audience).ToList());
        }

        // ---------- Checkout: show simulated card form ----------

        [HttpGet]
        public IActionResult Checkout(string? package)
        {
            if (string.IsNullOrEmpty(HttpContext.Session.GetString("UserEmail")))
                return RedirectToAction("Index", "Home");

            if (string.IsNullOrEmpty(package))
                return RedirectToAction(nameof(Buy));

            var selected = CreditPackages.GetByKey(package);
            if (selected == null)
                return RedirectToAction(nameof(Buy));

            ViewBag.Package = selected;
            return View(new CheckoutViewModel { PackageKey = selected.Key });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Checkout(CheckoutViewModel model)
        {
            var email = HttpContext.Session.GetString("UserEmail");
            if (string.IsNullOrEmpty(email))
                return RedirectToAction("Index", "Home");

            var selected = CreditPackages.GetByKey(model.PackageKey);
            if (selected == null)
                return RedirectToAction(nameof(Buy));

            if (!ModelState.IsValid)
            {
                ViewBag.Package = selected;
                return View(model);
            }

            var user = await _db.Users.FirstOrDefaultAsync(u => u.Email == email);
            if (user == null)
            {
                HttpContext.Session.Clear();
                return RedirectToAction("Index", "Home");
            }

            // ---- SIMULATED PAYMENT (always succeeds) ----
            // TODO: replace with real payment provider (Netopia/Stripe/PayPal).
            user.Credite += selected.Credits;
            user.CreditRest = user.Credite - user.CreditConsum;
            user.TotalPaid += selected.PriceEur;

            _db.Purchases.Add(new Purchase
            {
                UserEmail = user.Email,
                PurchasedAt = DateTime.UtcNow,
                AmountEur = selected.PriceEur,
                CreditsAdded = selected.Credits,
                PaymentMethod = "simulated",
                PackageKey = selected.Key
            });

            await _db.SaveChangesAsync();

            _logger.LogInformation(
                "Simulated payment: {Email} bought {Credits} credits for {Price} EUR ({Package}).",
                email, selected.Credits, selected.PriceEur, selected.Key);

            // CAM: la PRIMA cumpărare de credite a unei clinici, creează folderele
            // locale (Original, Sends, Sumar, Errors) pe C:\MedicalApp_files\.
            // Idempotent — dacă rulează a doua oară, FoldersCreatedAt fiind setat,
            // nu mai facem nimic.
            if (string.Equals(user.UserType, "Clinic", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var clinic = await _db.Clinics.FirstOrDefaultAsync(c => c.UserEmail == user.Email);
                    if (clinic != null && clinic.FoldersCreatedAt == null)
                    {
                        await _camFileStore.EnsureClinicFoldersAsync(clinic);
                        clinic.FoldersCreatedAt = DateTime.UtcNow;
                        await _db.SaveChangesAsync();
                        _logger.LogInformation(
                            "CAM: created on-disk folder structure for clinic {Email} after first purchase.",
                            user.Email);
                    }
                }
                catch (Exception ex)
                {
                    // Folder creation issues should NOT roll back the user's payment.
                    // We log and continue; operator can retry from the dashboard later.
                    _logger.LogError(ex,
                        "CAM: failed to create folder structure for clinic {Email} on first purchase. " +
                        "Payment is safe; operator can retry from the CAM dashboard.",
                        user.Email);
                }
            }

            // ---- Notify all admins by email (non-blocking: failure does NOT break the purchase) ----
            try
            {
                await SendAdminPurchaseNotificationAsync(user, selected);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Admin notification email failed after successful purchase by {Email}. Purchase is safe.",
                    email);
            }

            TempData["SuccessMessage"] = string.Format(
                Loc.T("PaymentSuccessMessage"), selected.Credits);

            // Clinic users land back on the CAM dashboard after a successful
            // top-up so the navbar mode doesn't flip to "personal" — Individual
            // users keep the original B2C dashboard destination.
            if (string.Equals(user.UserType, "Clinic", StringComparison.OrdinalIgnoreCase))
                return RedirectToAction("Index", "Dashboard", new { area = "CAM" });

            return RedirectToAction("Dashboard", "Account");
        }

        /// <summary>
        /// Sends a notification email to every address configured in AdminSettings.Emails,
        /// announcing a new credit purchase. Failures are swallowed by the caller so a broken
        /// SMTP config never blocks the user from completing their purchase.
        /// </summary>
        private async Task SendAdminPurchaseNotificationAsync(User user, CreditPackage package)
        {
            var admins = _adminSettings.Emails?
                .Where(e => !string.IsNullOrWhiteSpace(e))
                .Select(e => e.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList() ?? new List<string>();

            if (admins.Count == 0)
            {
                _logger.LogInformation("No admin emails configured - skipping purchase notification.");
                return;
            }

            var subject = $"[MedicalApp] Achizitie noua - {user.Email} - {package.Credits} credite - {package.PriceEur:F2} EUR";
            var body = BuildAdminPurchaseEmailBody(user, package);

            foreach (var adminEmail in admins)
            {
                try
                {
                    await _emailService.SendEmailAsync(adminEmail, subject, body);
                    _logger.LogInformation("Purchase notification sent to admin {Admin}", adminEmail);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "Failed to send purchase notification to admin {Admin}", adminEmail);
                }
            }
        }

        private static string BuildAdminPurchaseEmailBody(User user, CreditPackage package)
        {
            var now = DateTime.Now.ToString("dd/MM/yyyy HH:mm");
            var safeEmail = System.Net.WebUtility.HtmlEncode(user.Email);
            var safePackageKey = System.Net.WebUtility.HtmlEncode(package.Key ?? "-");

            return $@"
<div style=""font-family:Arial,Helvetica,sans-serif;max-width:640px;margin:0 auto;padding:0;background:#ffffff;"">
  <div style=""background:#0d47a1;color:#ffffff;padding:20px 24px;border-radius:10px 10px 0 0;"">
    <h2 style=""margin:0;font-size:20px;font-weight:700;"">&#128176; MedicalApp &mdash; Achizitie noua</h2>
    <div style=""font-size:13px;opacity:0.9;margin-top:4px;"">Notificare automata catre administrator</div>
  </div>

  <div style=""padding:24px;color:#212529;font-size:15px;line-height:1.6;border:1px solid #e9ecef;border-top:0;"">
    <p style=""margin:0 0 16px 0;"">Un utilizator tocmai a finalizat o achizitie de credite:</p>

    <table style=""width:100%;border-collapse:collapse;margin:0 0 20px 0;font-size:14px;"">
      <tr style=""background:#f8f9fa;"">
        <td style=""padding:10px 12px;border:1px solid #e9ecef;font-weight:600;width:200px;"">Data si ora</td>
        <td style=""padding:10px 12px;border:1px solid #e9ecef;"">{now}</td>
      </tr>
      <tr>
        <td style=""padding:10px 12px;border:1px solid #e9ecef;font-weight:600;"">Email utilizator</td>
        <td style=""padding:10px 12px;border:1px solid #e9ecef;""><strong>{safeEmail}</strong></td>
      </tr>
      <tr style=""background:#f8f9fa;"">
        <td style=""padding:10px 12px;border:1px solid #e9ecef;font-weight:600;"">Pachet</td>
        <td style=""padding:10px 12px;border:1px solid #e9ecef;"">{safePackageKey}</td>
      </tr>
      <tr>
        <td style=""padding:10px 12px;border:1px solid #e9ecef;font-weight:600;"">Credite adaugate</td>
        <td style=""padding:10px 12px;border:1px solid #e9ecef;color:#0d6efd;font-weight:700;font-size:16px;"">+{package.Credits}</td>
      </tr>
      <tr style=""background:#f8f9fa;"">
        <td style=""padding:10px 12px;border:1px solid #e9ecef;font-weight:600;"">Suma platita</td>
        <td style=""padding:10px 12px;border:1px solid #e9ecef;color:#198754;font-weight:700;font-size:16px;"">{package.PriceEur:F2} EUR</td>
      </tr>
      <tr>
        <td style=""padding:10px 12px;border:1px solid #e9ecef;font-weight:600;"">Metoda plata</td>
        <td style=""padding:10px 12px;border:1px solid #e9ecef;"">simulated</td>
      </tr>
    </table>

    <div style=""background:#eef5ff;border-left:4px solid #0d47a1;padding:14px 18px;border-radius:6px;margin:20px 0;"">
      <div style=""font-weight:600;color:#0d47a1;margin-bottom:6px;"">Situatie utilizator dupa achizitie</div>
      <div style=""font-size:14px;color:#495057;"">
        &#8226; Credite platite cumparate in total: <strong>{user.Credite}</strong><br/>
        &#8226; Credite platite ramase: <strong>{user.CreditRest}</strong><br/>
        &#8226; Credite bonus ramase: <strong>{user.BonusCreditsRemaining}</strong><br/>
        &#8226; Total disponibil acum: <strong>{user.TotalAvailableCredits}</strong><br/>
        &#8226; Total incasat de la acest utilizator: <strong>{user.TotalPaid:F2} EUR</strong>
      </div>
    </div>

    <p style=""margin:20px 0 0 0;color:#6c757d;font-size:13px;"">
      Acest email a fost trimis automat de MedicalApp. Nu raspunde la acest mesaj.
    </p>
  </div>

  <div style=""background:#f1f5fb;color:#0d47a1;padding:14px 24px;border-radius:0 0 10px 10px;text-align:center;font-size:13px;font-weight:600;border:1px solid #e9ecef;border-top:0;"">
    MedicalApp &mdash; Panou administrator
  </div>
</div>";
        }
    }
}
