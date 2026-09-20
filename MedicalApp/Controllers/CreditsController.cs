using MedicalApp.Data;
using MedicalApp.Models;
using MedicalApp.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using System.Globalization;
using System.Text.Json;

namespace MedicalApp.Controllers
{
    public class CreditsController : Controller
    {
        private readonly AppDbContext _db;
        private readonly IEmailService _emailService;
        private readonly AdminSettings _adminSettings;
        private readonly ICamFileStore _camFileStore;
        private readonly PdfReportGenerator _pdfGenerator;
        private readonly StripePaymentService _stripe;
        private readonly PaymentSettings _payments;
        private readonly ILogger<CreditsController> _logger;

        public CreditsController(
            AppDbContext db,
            IEmailService emailService,
            IOptions<AdminSettings> adminOptions,
            ICamFileStore camFileStore,
            PdfReportGenerator pdfGenerator,
            StripePaymentService stripe,
            IOptions<PaymentSettings> payments,
            ILogger<CreditsController> logger)
        {
            _stripe = stripe;
            _payments = payments.Value;
            _db = db;
            _emailService = emailService;
            _adminSettings = adminOptions.Value;
            _camFileStore = camFileStore;
            _pdfGenerator = pdfGenerator;
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

            // Each account type sees only its own offer: CAM packages for
            // clinics, the single cabinet package for a practice, the four B2C
            // tiers for everyone else.
            var audience = AccountTypes.Normalize(user?.UserType);
            ViewBag.Audience = audience;
            return View(CreditPackages.ForAudience(audience).ToList());
        }

        // ---------- Checkout: show simulated card form ----------

        [HttpGet]
        public async Task<IActionResult> Checkout(string? package)
        {
            var email = HttpContext.Session.GetString("UserEmail");
            if (string.IsNullOrEmpty(email))
                return RedirectToAction("Index", "Home");

            if (string.IsNullOrEmpty(package))
                return RedirectToAction(nameof(Buy));

            var selected = CreditPackages.GetByKey(package);
            if (selected == null)
                return RedirectToAction(nameof(Buy));

            // A package belongs to ONE audience: a cabinet must not buy the B2C
            // tiers by typing a URL, and vice versa.
            var userType = await _db.Users.AsNoTracking()
                .Where(u => u.Email == email)
                .Select(u => u.UserType)
                .FirstOrDefaultAsync();
            if (!PackageMatchesAccount(selected, userType))
                return RedirectToAction(nameof(Buy));

            ViewBag.Package = selected;
            ViewBag.UseStripe = _payments.UseStripe;
            return View(new CheckoutViewModel { PackageKey = selected.Key });
        }

        private static bool PackageMatchesAccount(CreditPackage package, string? userType) =>
            string.Equals(package.Audience, AccountTypes.Normalize(userType),
                          StringComparison.OrdinalIgnoreCase);

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

            // Same guard as the GET: money is only taken for a package this
            // account type is actually allowed to buy.
            if (!PackageMatchesAccount(selected, user.UserType))
                return RedirectToAction(nameof(Buy));

            // The simulated provider is only for local development; when Stripe is
            // active the card form no longer exists, so refuse a stray POST.
            if (_payments.UseStripe)
                return RedirectToAction(nameof(Checkout), new { package = selected.Key });

            var outcome = await FulfillPurchaseAsync(user, selected, paymentMethod: "simulated", providerReference: null);
            ApplyDemoUnlockTempData(outcome);

            return RedirectAfterPurchase(user, selected);
        }


        /// <summary>
        /// Everything that happens once a payment is KNOWN to be settled: credits,
        /// Purchase row, CAM folders on first clinic purchase, freemium unlock email,
        /// admin notification. Shared by the simulated provider, the Stripe return
        /// page and the Stripe webhook — one code path, one behaviour.
        /// </summary>
        private async Task<PurchaseOutcome> FulfillPurchaseAsync(User user, CreditPackage selected,
            string paymentMethod, string? providerReference)
        {
            string email = user.Email;
            // Evaluated BEFORE the Purchase row below is inserted: the freemium
            // ("DEMO") report is unlocked+emailed only on the very FIRST purchase.
            bool isFirstPurchase = !await _db.Purchases.AnyAsync(p => p.UserEmail == user.Email);
            (int historyId, int otherUnlockedCount)? demoUnlocked = null;

            user.Credite += selected.Credits;
            user.CreditRest = user.Credite - user.CreditConsum;
            user.TotalPaid += selected.PriceEur;

            _db.Purchases.Add(new Purchase
            {
                UserEmail = user.Email,
                PurchasedAt = DateTime.UtcNow,
                AmountEur = selected.PriceEur,
                CreditsAdded = selected.Credits,
                PaymentMethod = paymentMethod,
                ProviderReference = providerReference,
                PackageKey = selected.Key
            });

            await _db.SaveChangesAsync();

            _logger.LogInformation(
                "Payment ({Method}): {Email} bought {Credits} credits for {Price} EUR ({Package}).",
                paymentMethod, email, selected.Credits, selected.PriceEur, selected.Key);

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

            // ---- FIRST-PURCHASE PERK: unlock the DEMO report and email it in full ----
            // B2C only: CAM clinics never receive freemium reports, so there is
            // nothing to unlock for them. Never blocks the purchase on failure.
            if (isFirstPurchase &&
                !string.Equals(user.UserType, "Clinic", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var unlocked = await TrySendUnlockedDemoReportAsync(user);
                    if (unlocked != null)
                    {
                        demoUnlocked = unlocked;
                        _logger.LogInformation(
                            "Demo unlock: emailed full report id={Id} to {Email} after first purchase ({Others} other reports unlocked).",
                            unlocked.Value.historyId, user.Email, unlocked.Value.otherUnlockedCount);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "Demo unlock email failed after first purchase by {Email}. Purchase is safe; " +
                        "the report is still downloadable unblurred from the archive.",
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

            return new PurchaseOutcome(selected, demoUnlocked);
        }

        private sealed record PurchaseOutcome(CreditPackage Package, (int historyId, int otherUnlockedCount)? DemoUnlocked);

        private void ApplyDemoUnlockTempData(PurchaseOutcome outcome)
        {
            if (outcome.DemoUnlocked == null) return;
            TempData["DemoUnlockedHistoryId"] = outcome.DemoUnlocked.Value.historyId.ToString();
            TempData["DemoUnlockedOtherCount"] = outcome.DemoUnlocked.Value.otherUnlockedCount.ToString();
        }

        private IActionResult RedirectAfterPurchase(User user, CreditPackage selected)
        {
            TempData["SuccessMessage"] = string.Format(
                Loc.T("PaymentSuccessMessage"), selected.Credits);
            if (string.Equals(user.UserType, "Clinic", StringComparison.OrdinalIgnoreCase))
                return RedirectToAction("Index", "Dashboard", new { area = "CAM" });
            return RedirectToAction("Dashboard", "Account");
        }

        // ===================================================================
        //  Stripe Checkout (Payments:Provider = "Stripe")
        // ===================================================================

        /// <summary>Creates the Stripe Checkout Session and sends the browser to Stripe's hosted page.</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> StartStripe(string packageKey)
        {
            var email = HttpContext.Session.GetString("UserEmail");
            if (string.IsNullOrEmpty(email))
                return RedirectToAction("Index", "Home");
            if (!_payments.UseStripe)
                return RedirectToAction(nameof(Checkout), new { package = packageKey });

            var selected = CreditPackages.GetByKey(packageKey ?? "");
            if (selected == null)
                return RedirectToAction(nameof(Buy));

            var user = await _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Email == email);
            if (user == null)
            {
                HttpContext.Session.Clear();
                return RedirectToAction("Index", "Home");
            }
            if (!PackageMatchesAccount(selected, user.UserType))
                return RedirectToAction(nameof(Buy));

            var baseUrl = $"{Request.Scheme}://{Request.Host}";
            var successUrl = baseUrl + Url.Action(nameof(StripeSuccess)) + "?session_id={CHECKOUT_SESSION_ID}";
            var cancelUrl = baseUrl + Url.Action(nameof(StripeCancel), new { package = selected.Key });

            try
            {
                var (checkoutUrl, _) = await _stripe.CreateCheckoutAsync(
                    user, selected, Loc.T(selected.NameKey), successUrl, cancelUrl, HttpContext.RequestAborted);
                return Redirect(checkoutUrl);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Stripe: could not create Checkout Session for {Email}/{Package}.", email, selected.Key);
                TempData["ErrorMessage"] = Loc.T("PaymentStripeUnavailable");
                return RedirectToAction(nameof(Checkout), new { package = selected.Key });
            }
        }

        /// <summary>Stripe sends the browser here after payment. Verifies with Stripe, then fulfils (idempotent).</summary>
        [HttpGet]
        public async Task<IActionResult> StripeSuccess(string? session_id)
        {
            var email = HttpContext.Session.GetString("UserEmail");
            if (string.IsNullOrEmpty(email))
                return RedirectToAction("Index", "Home");
            if (string.IsNullOrWhiteSpace(session_id))
                return RedirectToAction(nameof(Buy));

            var tx = await _stripe.FindTransactionAsync(session_id, HttpContext.RequestAborted);
            if (tx == null || !string.Equals(tx.UserEmail, email, StringComparison.OrdinalIgnoreCase))
                return RedirectToAction(nameof(Buy));

            var selected = CreditPackages.GetByKey(tx.PackageKey);
            var user = await _db.Users.FirstOrDefaultAsync(u => u.Email == email);
            if (selected == null || user == null)
                return RedirectToAction(nameof(Buy));

            if (tx.Status != PaymentTransaction.StatusPaid)
            {
                var (paid, paymentIntentId) = await _stripe.IsPaidAtStripeAsync(session_id, HttpContext.RequestAborted);
                if (!paid)
                {
                    TempData["ErrorMessage"] = Loc.T("PaymentStripePending");
                    return RedirectToAction(nameof(Buy));
                }
                var claimed = await _stripe.TryMarkPaidAsync(tx, paymentIntentId, HttpContext.RequestAborted);
                if (claimed)
                {
                    var outcome = await FulfillPurchaseAsync(user, selected, "stripe", paymentIntentId ?? session_id);
                    ApplyDemoUnlockTempData(outcome);
                }
            }

            return RedirectAfterPurchase(user, selected);
        }

        [HttpGet]
        public IActionResult StripeCancel(string? package)
        {
            TempData["ErrorMessage"] = Loc.T("PaymentStripeCancelled");
            return string.IsNullOrEmpty(package)
                ? RedirectToAction(nameof(Buy))
                : RedirectToAction(nameof(Checkout), new { package });
        }

        /// <summary>
        /// Stripe → server notification (register this URL in the Stripe Dashboard →
        /// Developers → Webhooks, event <c>checkout.session.completed</c>). Fulfils the
        /// purchase even if the customer never came back to StripeSuccess. Idempotent.
        /// </summary>
        [HttpPost]
        [IgnoreAntiforgeryToken]
        public async Task<IActionResult> StripeWebhook()
        {
            if (!_payments.UseStripe) return NotFound();

            string json;
            using (var reader = new StreamReader(Request.Body))
                json = await reader.ReadToEndAsync();

            Stripe.Event stripeEvent;
            try
            {
                stripeEvent = _stripe.ParseWebhook(json, Request.Headers["Stripe-Signature"]);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Stripe webhook rejected (bad signature or payload).");
                return BadRequest();
            }

            if (stripeEvent.Data.Object is not Stripe.Checkout.Session session)
                return Ok();

            var tx = await _stripe.FindTransactionAsync(session.Id);
            if (tx == null) return Ok(); // not ours (e.g. another environment sharing the account)

            switch (stripeEvent.Type)
            {
                case "checkout.session.completed":
                case "checkout.session.async_payment_succeeded":
                    if (session.PaymentStatus != "paid") break;
                    var user = await _db.Users.FirstOrDefaultAsync(u => u.Email == tx.UserEmail);
                    var selected = CreditPackages.GetByKey(tx.PackageKey);
                    if (user == null || selected == null) break;
                    if (await _stripe.TryMarkPaidAsync(tx, session.PaymentIntentId))
                        await FulfillPurchaseAsync(user, selected, "stripe", session.PaymentIntentId ?? session.Id);
                    break;

                case "checkout.session.expired":
                case "checkout.session.async_payment_failed":
                    await _stripe.MarkClosedAsync(tx,
                        stripeEvent.Type == "checkout.session.expired" ? PaymentTransaction.StatusExpired : PaymentTransaction.StatusFailed);
                    break;
            }
            return Ok();
        }

        /// <summary>
        /// First-purchase perk: regenerates the user's most recent freemium ("DEMO")
        /// report WITHOUT blur and emails it as an attachment, so paying delivers the
        /// full report immediately instead of making the user hunt for it. Older demo
        /// reports are only mentioned (with an Archive link) — they are already
        /// unblurred on download because the gate is <c>user.Credite == 0</c>.
        /// Returns the unlocked report id + how many other reports got unlocked,
        /// or <c>null</c> when there was nothing to unlock.
        /// </summary>
        private async Task<(int historyId, int otherUnlockedCount)?> TrySendUnlockedDemoReportAsync(User user)
        {
            var demoReports = await _db.InterpretationHistories.AsNoTracking()
                .Where(h => h.UserEmail == user.Email
                            && h.Status == "success"
                            && h.RawJsonResult != null)
                .OrderByDescending(h => h.CreatedAt)
                .Select(h => new { h.Id, h.CreatedAt, h.Language, h.RawJsonResult })
                .ToListAsync();

            if (demoReports.Count == 0) return null;

            var latest = demoReports[0];
            var result = JsonSerializer.Deserialize<InterpretationResult>(latest.RawJsonResult!,
                new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    AllowTrailingCommas = true,
                    ReadCommentHandling = JsonCommentHandling.Skip
                });
            if (result == null) return null;

            var lang = string.IsNullOrWhiteSpace(latest.Language)
                ? CultureInfo.CurrentUICulture.TwoLetterISOLanguageName
                : latest.Language.Split('-')[0].ToLowerInvariant();

            // The report body was written in `lang`, so its PDF labels must match.
            // ForCurrentUi() reads the ambient culture — swap it for this call only
            // (same pattern as CamBatchService.RunAsync).
            var previousUiCulture = CultureInfo.CurrentUICulture;
            byte[] pdfBytes;
            try
            {
                try
                {
                    CultureInfo.CurrentUICulture =
                        new CultureInfo(SupportedLanguagesConfig.GetCultureCode(lang));
                }
                catch (CultureNotFoundException)
                {
                    // Culture not installed on this machine — Loc.T falls back to English.
                }

                pdfBytes = _pdfGenerator.Generate(result, LocalizedLabels.ForCurrentUi(), isFreemium: false);
            }
            finally
            {
                CultureInfo.CurrentUICulture = previousUiCulture;
            }

            var otherCount = demoReports.Count - 1;
            var archiveUrl = Url.Action("Index", "Profiles", null, Request.Scheme) ?? "";
            var intro = string.Format(Loc.T("CreditsDemoUnlockedEmailIntro", lang),
                latest.CreatedAt.ToLocalTime().ToString("dd.MM.yyyy HH:mm"));
            var archiveNote = otherCount > 0
                ? $"<p>{string.Format(Loc.T("CreditsDemoUnlockedEmailArchiveFmt", lang), otherCount, archiveUrl)}</p>"
                : "";

            var htmlBody = $@"
<div style='font-family: Arial, sans-serif; max-width: 600px; margin: 0 auto; padding: 20px;'>
    <h2 style='color: #0d47a1;'>MyMedicalApp.NET</h2>
    <p>{Loc.T("EmailGreeting", lang)}</p>
    <p style='font-size: 1.05em;'>{intro}</p>
    {archiveNote}
    <p style='font-style: italic; color: #0d47a1;'>{Loc.T("Tagline", lang)}</p>
    <hr style='border: none; border-top: 1px solid #dee2e6; margin: 20px 0;' />
    <p style='color: #6c757d; font-size: 0.9em;'>{Loc.T("EmailRegards", lang)}</p>
    <p style='color: #0d47a1; font-weight: bold;'>www.mymedicalapp.net</p>
</div>";

            await _emailService.SendEmailWithAttachmentAsync(
                user.Email,
                Loc.T("CreditsDemoUnlockedEmailSubject", lang),
                htmlBody,
                pdfBytes,
                $"MedicalApp_{latest.CreatedAt:yyyyMMdd_HHmmss}_report.pdf");

            return (latest.Id, otherCount);
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

            var subject = $"[MyMedicalApp.NET] Achizitie noua - {user.Email} - {package.Credits} credite - {package.PriceEur:F2} EUR";
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
    <h2 style=""margin:0;font-size:20px;font-weight:700;"">&#128176; MyMedicalApp.NET &mdash; Achizitie noua</h2>
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
      Acest email a fost trimis automat de MyMedicalApp.NET. Nu raspunde la acest mesaj.
    </p>
  </div>

  <div style=""background:#f1f5fb;color:#0d47a1;padding:14px 24px;border-radius:0 0 10px 10px;text-align:center;font-size:13px;font-weight:600;border:1px solid #e9ecef;border-top:0;"">
    MyMedicalApp.NET &mdash; Panou administrator
  </div>
</div>";
        }
    }
}
