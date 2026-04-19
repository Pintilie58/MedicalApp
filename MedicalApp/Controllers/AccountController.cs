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
        private readonly ILogger<AccountController> _logger;

        public AccountController(AppDbContext db, IEmailService emailService, ILogger<AccountController> logger)
        {
            _db = db;
            _emailService = emailService;
            _logger = logger;
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Register(RegisterViewModel model)
        {
            if (!ModelState.IsValid)
            {
                ViewData["LoginModel"] = new LoginViewModel();
                ViewData["RegisterModel"] = model;
                ViewData["ActiveTab"] = "register";
                return View("~/Views/Home/Index.cshtml");
            }

            var exists = await _db.Users.AnyAsync(u => u.Email == model.Email);
            if (exists)
            {
                ModelState.AddModelError(string.Empty, Loc.T("EmailAlreadyExists"));
                ViewData["LoginModel"] = new LoginViewModel();
                ViewData["RegisterModel"] = model;
                ViewData["ActiveTab"] = "register";
                return View("~/Views/Home/Index.cshtml");
            }

            var user = new User
            {
                Email = model.Email.Trim().ToLowerInvariant(),
                Parola = BCrypt.Net.BCrypt.HashPassword(model.Parola),
                Credite = 0,
                DataC = DateTime.UtcNow,
                CreditConsum = 0,
                CreditRest = 0
            };

            _db.Users.Add(user);
            await _db.SaveChangesAsync();

            TempData["SuccessMessage"] = Loc.T("RegistrationSuccess");
            TempData["ActiveTab"] = "login";
            return RedirectToAction("Index", "Home");
        }

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

            HttpContext.Session.SetString("UserEmail", user.Email);
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

            return View(user);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult Logout()
        {
            HttpContext.Session.Clear();
            return RedirectToAction("Index", "Home");
        }

        // ---------- Forgot Password ----------

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

            // Generate and persist new password (hashed)
            var newPassword = PasswordGenerator.Generate(10);
            user.Parola = BCrypt.Net.BCrypt.HashPassword(newPassword);
            await _db.SaveChangesAsync();

            // Send email with plaintext password
            try
            {
                var subject = Loc.T("EmailSubject");
                var htmlBody = BuildEmailBody(newPassword);
                await _emailService.SendEmailAsync(email, subject, htmlBody);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending password recovery email to {Email}", email);
                ModelState.AddModelError(string.Empty, Loc.T("EmailSendFailed"));
                return View(model);
            }

            TempData["SuccessMessage"] = Loc.T("NewPasswordSent");
            TempData["ActiveTab"] = "login";
            return RedirectToAction("Index", "Home");
        }

        private static string BuildEmailBody(string newPassword)
        {
            var greeting = Loc.T("EmailGreeting");
            var intro = Loc.T("EmailNewPasswordIntro");
            var advice = Loc.T("EmailChangeAdvice");
            var regards = Loc.T("EmailRegards");

            return $@"
<div style='font-family: Arial, sans-serif; max-width: 600px; margin: 0 auto; padding: 20px;'>
    <h2 style='color: #0d6efd;'>MedicalApp</h2>
    <p>{greeting}</p>
    <p>{intro}</p>
    <div style='background: #f8f9fa; border: 1px solid #dee2e6; border-radius: 8px; padding: 20px; text-align: center; margin: 20px 0;'>
        <span style='font-family: monospace; font-size: 24px; font-weight: bold; color: #0d6efd; letter-spacing: 2px;'>{System.Net.WebUtility.HtmlEncode(newPassword)}</span>
    </div>
    <p style='color: #dc3545;'><strong>{advice}</strong></p>
    <hr style='border: none; border-top: 1px solid #dee2e6; margin: 20px 0;' />
    <p style='color: #6c757d; font-size: 0.9em;'>{regards}</p>
</div>";
        }
    }
}
