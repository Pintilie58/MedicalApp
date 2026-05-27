using MedicalApp.Data;
using MedicalApp.Models;
using MedicalApp.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MedicalApp.Controllers
{
    public class AccountController : Controller
    {
        private readonly AppDbContext _db;
        private readonly IEmailService _emailService;
        private readonly PendingRegistrationStore _pendingStore;
        private readonly AdminSettings _adminSettings;
        private readonly ILogger<AccountController> _logger;

        public AccountController(
            AppDbContext db,
            IEmailService emailService,
            PendingRegistrationStore pendingStore,
            Microsoft.Extensions.Options.IOptions<AdminSettings> adminOptions,
            ILogger<AccountController> logger)
        {
            _db = db;
            _emailService = emailService;
            _pendingStore = pendingStore;
            _adminSettings = adminOptions.Value;
            _logger = logger;
        }

        // =====================================================================
        //  AJAX endpoint: validate promo code in real time on the register form.
        //  Returns JSON: { valid: bool, message: string, credits: int }
        // =====================================================================
        [HttpGet]
        public async Task<IActionResult> CheckPromoCode(string code, string? lang = null)
        {
            // Force the requested language on this thread so Loc.T returns the right strings
            if (!string.IsNullOrWhiteSpace(lang))
            {
                try
                {
                    var ci = new System.Globalization.CultureInfo(lang);
                    System.Globalization.CultureInfo.CurrentCulture = ci;
                    System.Globalization.CultureInfo.CurrentUICulture = ci;
                }
                catch { /* invalid lang -> keep request culture */ }
            }

            if (string.IsNullOrWhiteSpace(code))
                return Json(new { valid = false, message = string.Empty, credits = 0 });

            var codeNorm = code.Trim().ToLower();
            var promo = await _db.PromoCodes
                .AsNoTracking()
                .FirstOrDefaultAsync(p => p.Code.ToLower() == codeNorm);

            if (promo == null || !promo.IsCurrentlyValid())
            {
                return Json(new
                {
                    valid = false,
                    message = Loc.T("PromoCodeInvalid"),
                    credits = 0
                });
            }

            return Json(new
            {
                valid = true,
                message = string.Format(Loc.T("PromoCodeValidOffer"), promo.CreditsToAdd),
                credits = promo.CreditsToAdd
            });
        }

        // ---------- Register (Step 1: request verification code) ----------

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Register(RegisterViewModel model)
        {
            // CAM: dacă userul a ales "Clinic", numele clinicii / localitatea /
            // adresa devin obligatorii. Validăm aici pentru că [Required] pe
            // ViewModel ar bloca și fluxul "Individual".
            if (string.Equals(model.UserType, "Clinic", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(model.ClinicName))
                    ModelState.AddModelError(nameof(model.ClinicName), Loc.T("ClinicNameRequired"));
                if (string.IsNullOrWhiteSpace(model.ClinicCity))
                    ModelState.AddModelError(nameof(model.ClinicCity), Loc.T("ClinicCityRequired"));
                if (string.IsNullOrWhiteSpace(model.ClinicAddress))
                    ModelState.AddModelError(nameof(model.ClinicAddress), Loc.T("ClinicAddressRequired"));
            }
            else
            {
                // Forțează "Individual" pentru orice valoare neașteptată.
                model.UserType = "Individual";
            }

            if (!ModelState.IsValid)
            {
                ViewData["LoginModel"] = new LoginViewModel();
                ViewData["RegisterModel"] = model;
                ViewData["ActiveTab"] = "register";
                return View("~/Views/Home/Index.cshtml");
            }

            var email = model.Email.Trim().ToLowerInvariant();

            var exists = await _db.Users.AnyAsync(u => u.Email == email);
            if (exists)
            {
                ModelState.AddModelError(string.Empty, Loc.T("EmailAlreadyExists"));
                ViewData["LoginModel"] = new LoginViewModel();
                ViewData["RegisterModel"] = model;
                ViewData["ActiveTab"] = "register";
                return View("~/Views/Home/Index.cshtml");
            }

            // Store pending registration and send verification code
            var code = PasswordGenerator.GenerateNumericCode(4);
            _pendingStore.Save(new PendingRegistration
            {
                Email = email,
                HashedPassword = BCrypt.Net.BCrypt.HashPassword(model.Parola),
                VerificationCode = code,
                ExpiresAt = DateTime.UtcNow.AddMinutes(10),
                AttemptsLeft = 5,
                // Persist the promo code typed at register, so it can be applied
                // on successful email verification (VerifyEmail action).
                PromoCode = string.IsNullOrWhiteSpace(model.PromoCode)
                    ? null
                    : model.PromoCode.Trim(),
                // CAM: carry the clinic-specific fields through email verification
                // so we can create the matching Clinic row right after the user
                // confirms their email.
                UserType = string.Equals(model.UserType, "Clinic", StringComparison.OrdinalIgnoreCase)
                    ? "Clinic" : "Individual",
                ClinicName = model.ClinicName?.Trim(),
                ClinicCity = model.ClinicCity?.Trim(),
                ClinicAddress = model.ClinicAddress?.Trim()
            });

            try
            {
                await _emailService.SendEmailAsync(
                    email,
                    Loc.T("VerifyEmailSubject"),
                    BuildVerificationEmailBody(code));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending verification email to {Email}", email);
                _pendingStore.Remove(email);
                ModelState.AddModelError(string.Empty, Loc.T("EmailSendFailed"));
                ViewData["LoginModel"] = new LoginViewModel();
                ViewData["RegisterModel"] = model;
                ViewData["ActiveTab"] = "register";
                return View("~/Views/Home/Index.cshtml");
            }

            return RedirectToAction("VerifyEmail", new { email });
        }

        // ---------- Register (Step 2: verify email with 4-digit code) ----------

        [HttpGet]
        public IActionResult VerifyEmail(string? email)
        {
            if (string.IsNullOrEmpty(email))
                return RedirectToAction("Index", "Home");

            var normalized = email.Trim().ToLowerInvariant();
            var pending = _pendingStore.Get(normalized);
            if (pending == null)
            {
                TempData["ErrorMessage"] = Loc.T("VerificationExpired");
                return RedirectToAction("Index", "Home");
            }

            return View(new VerifyEmailViewModel { Email = normalized });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> VerifyEmail(VerifyEmailViewModel model)
        {
            if (!ModelState.IsValid)
                return View(model);

            var email = model.Email.Trim().ToLowerInvariant();
            var pending = _pendingStore.Get(email);

            if (pending == null)
            {
                TempData["ErrorMessage"] = Loc.T("VerificationExpired");
                return RedirectToAction("Index", "Home");
            }

            if (pending.VerificationCode != model.Code)
            {
                pending.AttemptsLeft--;
                if (pending.AttemptsLeft <= 0)
                {
                    _pendingStore.Remove(email);
                    TempData["ErrorMessage"] = Loc.T("TooManyAttempts");
                    return RedirectToAction("Index", "Home");
                }
                _pendingStore.Save(pending);
                ModelState.AddModelError(string.Empty,
                    string.Format(Loc.T("InvalidCodeTriesLeft"), pending.AttemptsLeft));
                return View(model);
            }

            // Code matches → create user
            var user = new User
            {
                Email = pending.Email,
                Parola = pending.HashedPassword,
                Credite = 0,
                DataC = DateTime.UtcNow,
                CreditConsum = 0,
                CreditRest = 0,
                FreeArchiveUntil = DateTime.UtcNow.Add(MedicalApp.Services.ArchiveAccessService.FreePeriod),
                IsAdmin = _adminSettings.IsAdminEmail(pending.Email),
                UserType = string.Equals(pending.UserType, "Clinic", StringComparison.OrdinalIgnoreCase)
                    ? "Clinic" : "Individual"
            };

            // Apply promo code (if any and valid) - case-insensitive lookup.
            // Promo credits go to the SEPARATE Bonus pool, NOT to user.Credite (paid).
            int bonusGranted = 0;
            string? appliedCode = null;
            if (!string.IsNullOrWhiteSpace(pending.PromoCode))
            {
                var codeNorm = pending.PromoCode.Trim().ToLower();
                var promo = await _db.PromoCodes
                    .FirstOrDefaultAsync(p => p.Code.ToLower() == codeNorm);

                if (promo != null && promo.IsCurrentlyValid())
                {
                    user.BonusCredits = promo.CreditsToAdd;
                    user.BonusCreditsConsumed = 0;
                    bonusGranted = promo.CreditsToAdd;
                    appliedCode = promo.Code;
                    promo.TimesUsed++;
                    _logger.LogInformation(
                        "Promo code {Code} redeemed by {Email} for {Credits} bonus credits.",
                        promo.Code, pending.Email, promo.CreditsToAdd);
                }
                else
                {
                    _logger.LogInformation(
                        "Promo code {Code} entered by {Email} at register is invalid/expired - ignored.",
                        codeNorm, pending.Email);
                }
            }

            _db.Users.Add(user);

            // CAM: dacă userul s-a înregistrat ca Clinică, creăm imediat și rândul
            // Clinic asociat (1:1 cu User.Email). Folderele pe disk NU se creează
            // acum — abia după PRIMA cumpărare de credite (vezi CreditsController).
            if (user.UserType == "Clinic" &&
                !string.IsNullOrWhiteSpace(pending.ClinicName) &&
                !string.IsNullOrWhiteSpace(pending.ClinicCity) &&
                !string.IsNullOrWhiteSpace(pending.ClinicAddress))
            {
                _db.Clinics.Add(new Clinic
                {
                    UserEmail = pending.Email,
                    Name = pending.ClinicName!.Trim(),
                    City = pending.ClinicCity!.Trim(),
                    Address = pending.ClinicAddress!.Trim(),
                    CreatedAt = DateTime.UtcNow
                });
            }

            await _db.SaveChangesAsync();
            _pendingStore.Remove(email);

            // Build success message with promo info, so the user clearly sees the bonus
            if (bonusGranted > 0)
            {
                TempData["SuccessMessage"] = string.Format(
                    Loc.T("RegistrationSuccessWithPromo"),
                    bonusGranted, appliedCode);
            }
            else if (!string.IsNullOrWhiteSpace(pending.PromoCode))
            {
                TempData["SuccessMessage"] = Loc.T("RegistrationSuccessPromoInvalid");
            }
            else
            {
                TempData["SuccessMessage"] = Loc.T("RegistrationSuccess");
            }
            TempData["ActiveTab"] = "login";
            return RedirectToAction("Index", "Home");
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ResendCode(string email)
        {
            if (string.IsNullOrEmpty(email))
                return RedirectToAction("Index", "Home");

            var normalized = email.Trim().ToLowerInvariant();
            var pending = _pendingStore.Get(normalized);
            if (pending == null)
            {
                TempData["ErrorMessage"] = Loc.T("VerificationExpired");
                return RedirectToAction("Index", "Home");
            }

            // Generate new code, extend expiry, reset attempts
            pending.VerificationCode = PasswordGenerator.GenerateNumericCode(4);
            pending.ExpiresAt = DateTime.UtcNow.AddMinutes(10);
            pending.AttemptsLeft = 5;
            _pendingStore.Save(pending);

            try
            {
                await _emailService.SendEmailAsync(
                    normalized,
                    Loc.T("VerifyEmailSubject"),
                    BuildVerificationEmailBody(pending.VerificationCode));
                TempData["SuccessMessage"] = Loc.T("CodeResent");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error resending verification email to {Email}", normalized);
                TempData["ErrorMessage"] = Loc.T("EmailSendFailed");
            }

            return RedirectToAction("VerifyEmail", new { email = normalized });
        }

        // ---------- Login ----------

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Login(LoginViewModel model)
        {
            if (!ModelState.IsValid)
            {
                ViewData["LoginModel"] = model;
                ViewData["RegisterModel"] = new RegisterViewModel();
                ViewData["ActiveTab"] = "login";
                return View("~/Views/Home/Index.cshtml");
            }

            var email = model.Email.Trim().ToLowerInvariant();
            var user = await _db.Users.FirstOrDefaultAsync(u => u.Email == email);

            if (user == null || !BCrypt.Net.BCrypt.Verify(model.Parola, user.Parola))
            {
                ModelState.AddModelError(string.Empty, Loc.T("InvalidCredentials"));
                ViewData["LoginModel"] = model;
                ViewData["RegisterModel"] = new RegisterViewModel();
                ViewData["ActiveTab"] = "login";
                return View("~/Views/Home/Index.cshtml");
            }

            if (user.IsBlocked)
            {
                ModelState.AddModelError(string.Empty, Loc.T("AccountBlocked"));
                ViewData["LoginModel"] = model;
                ViewData["RegisterModel"] = new RegisterViewModel();
                ViewData["ActiveTab"] = "login";
                return View("~/Views/Home/Index.cshtml");
            }

            // Auto-promote to admin if configured email
            if (!user.IsAdmin && _adminSettings.IsAdminEmail(user.Email))
                user.IsAdmin = true;

            user.LastLoginAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();

            HttpContext.Session.SetString("UserEmail", user.Email);
            HttpContext.Session.SetString("JustLoggedIn", "1");
            return RedirectToAction("Dashboard", "Account");
        }

        public IActionResult Dashboard()
        {
            var email = HttpContext.Session.GetString("UserEmail");
            if (string.IsNullOrEmpty(email))
                return RedirectToAction("Index", "Home");

            var user = _db.Users.FirstOrDefault(u => u.Email == email);
            if (user == null)
            {
                HttpContext.Session.Clear();
                return RedirectToAction("Index", "Home");
            }

            // CAM: doar dacă utilizatorul tocmai s-a logat (sau a fost redirectat
            // după login). Folosim un flag de sesiune ca să nu blocăm utilizatorii
            // Clinic să-și acceseze panoul personal manual din meniu.
            if (string.Equals(user.UserType, "Clinic", StringComparison.OrdinalIgnoreCase) &&
                HttpContext.Session.GetString("JustLoggedIn") == "1")
            {
                HttpContext.Session.Remove("JustLoggedIn");
                return RedirectToAction("Index", "Dashboard", new { area = "CAM" });
            }

            return View(user);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult Logout()
        {
            HttpContext.Session.Clear();
            return RedirectToAction("Index", "Home");
        }

        // ---------- Forgot Password (step 1: request reset link) ----------

        [HttpGet]
        public IActionResult ForgotPassword()
        {
            return View(new ForgotPasswordViewModel());
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ForgotPassword(ForgotPasswordViewModel model)
        {
            if (!ModelState.IsValid)
                return View(model);

            var email = model.Email.Trim().ToLowerInvariant();
            var user = await _db.Users.FirstOrDefaultAsync(u => u.Email == email);

            if (user == null)
            {
                ModelState.AddModelError(string.Empty, Loc.T("EmailNotRegistered"));
                return View(model);
            }

            // Generate secure token valid for 30 minutes
            user.PasswordResetToken = PasswordGenerator.GenerateToken(48);
            user.PasswordResetTokenExpiry = DateTime.UtcNow.AddMinutes(30);
            await _db.SaveChangesAsync();

            var resetLink = Url.Action(
                action: "ResetPassword",
                controller: "Account",
                values: new { token = user.PasswordResetToken, email = user.Email },
                protocol: Request.Scheme,
                host: Request.Host.Value);

            try
            {
                await _emailService.SendEmailAsync(email, Loc.T("EmailSubject"),
                    BuildResetEmailBody(resetLink ?? string.Empty));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending password reset email to {Email}", email);
                ModelState.AddModelError(string.Empty, Loc.T("EmailSendFailed"));
                return View(model);
            }

            TempData["SuccessMessage"] = Loc.T("ResetLinkSent");
            TempData["ActiveTab"] = "login";
            return RedirectToAction("Index", "Home");
        }

        // ---------- Reset Password (step 2: set new password via link) ----------

        [HttpGet]
        public async Task<IActionResult> ResetPassword(string? token, string? email)
        {
            if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(email))
                return View("ResetPasswordInvalid");

            var normalizedEmail = email.Trim().ToLowerInvariant();
            var user = await _db.Users.FirstOrDefaultAsync(u => u.Email == normalizedEmail);

            if (user == null
                || user.PasswordResetToken != token
                || user.PasswordResetTokenExpiry == null
                || user.PasswordResetTokenExpiry < DateTime.UtcNow)
            {
                return View("ResetPasswordInvalid");
            }

            return View(new ResetPasswordViewModel { Token = token, Email = normalizedEmail });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ResetPassword(ResetPasswordViewModel model)
        {
            if (!ModelState.IsValid)
                return View(model);

            var normalizedEmail = model.Email.Trim().ToLowerInvariant();
            var user = await _db.Users.FirstOrDefaultAsync(u => u.Email == normalizedEmail);

            if (user == null
                || user.PasswordResetToken != model.Token
                || user.PasswordResetTokenExpiry == null
                || user.PasswordResetTokenExpiry < DateTime.UtcNow)
            {
                return View("ResetPasswordInvalid");
            }

            user.Parola = BCrypt.Net.BCrypt.HashPassword(model.Parola);
            user.PasswordResetToken = null;
            user.PasswordResetTokenExpiry = null;
            await _db.SaveChangesAsync();

            TempData["SuccessMessage"] = Loc.T("ResetPasswordSuccess");
            TempData["ActiveTab"] = "login";
            return RedirectToAction("Index", "Home");
        }

        // ---------- Email body builders ----------

        private static string BuildVerificationEmailBody(string code)
        {
            var greeting = Loc.T("EmailGreeting");
            var intro = Loc.T("VerifyEmailIntro");
            var expiry = Loc.T("VerifyEmailExpiry");
            var ignore = Loc.T("EmailIgnoreIfNotRequested");
            var regards = Loc.T("EmailRegards");

            return $@"
<div style='font-family: Arial, sans-serif; max-width: 600px; margin: 0 auto; padding: 20px;'>
    <h2 style='color: #0d6efd;'>MedicalApp</h2>
    <p>{greeting}</p>
    <p>{intro}</p>
    <div style='background: #f8f9fa; border: 2px solid #0d6efd; border-radius: 10px; padding: 28px; text-align: center; margin: 24px 0;'>
        <span style='font-family: monospace; font-size: 42px; font-weight: bold; color: #0d6efd; letter-spacing: 12px;'>{System.Net.WebUtility.HtmlEncode(code)}</span>
    </div>
    <p style='color: #6c757d; font-size: 0.9em;'>{expiry}</p>
    <p style='color: #6c757d; font-size: 0.9em;'>{ignore}</p>
    <hr style='border: none; border-top: 1px solid #dee2e6; margin: 20px 0;' />
    <p style='color: #6c757d; font-size: 0.9em;'>{regards}</p>
</div>";
        }

        private static string BuildResetEmailBody(string resetLink)
        {
            var greeting = Loc.T("EmailGreeting");
            var intro = Loc.T("EmailResetLinkIntro");
            var buttonText = Loc.T("EmailResetLinkButton");
            var expiry = Loc.T("EmailLinkExpiryNote");
            var ignore = Loc.T("EmailIgnoreIfNotRequested");
            var regards = Loc.T("EmailRegards");

            return $@"
<div style='font-family: Arial, sans-serif; max-width: 600px; margin: 0 auto; padding: 20px;'>
    <h2 style='color: #0d6efd;'>MedicalApp</h2>
    <p>{greeting}</p>
    <p>{intro}</p>
    <div style='text-align: center; margin: 30px 0;'>
        <a href='{resetLink}' style='display: inline-block; background: #0d6efd; color: #ffffff; text-decoration: none; padding: 12px 24px; border-radius: 8px; font-weight: bold;'>
            {buttonText}
        </a>
    </div>
    <p style='color: #6c757d; font-size: 0.9em;'>{expiry}</p>
    <p style='color: #6c757d; font-size: 0.9em;'>{ignore}</p>
    <hr style='border: none; border-top: 1px solid #dee2e6; margin: 20px 0;' />
    <p style='color: #6c757d; font-size: 0.85em; word-break: break-all;'>{System.Net.WebUtility.HtmlEncode(resetLink)}</p>
    <p style='color: #6c757d; font-size: 0.9em;'>{regards}</p>
</div>";
        }
    }
}
