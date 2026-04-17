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

        public AccountController(AppDbContext db)
        {
            _db = db;
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
    }
}
