using MedicalApp.Models;
using MedicalApp.Services;
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.Mvc;

namespace MedicalApp.Controllers
{
    public class HomeController : Controller
    {
        public IActionResult Index()
        {
            // If already logged in, go straight to Dashboard (skip login form).
            if (!string.IsNullOrEmpty(HttpContext.Session.GetString("UserEmail")))
                return RedirectToAction("Dashboard", "Account");

            // NOT logged in → marketing landing page (new) instead of the
            // old login form. The old login form is still reachable directly
            // via /Home/Auth (route unchanged behavior, just different name).
            return View("Landing");
        }

        /// <summary>
        /// Renders the legacy login + register form. This was previously the
        /// default <c>/</c> page; landing now lives there. All "Sign in" and
        /// "Register" links from the landing point here. Re-uses the existing
        /// <c>Index.cshtml</c> view unchanged.
        /// </summary>
        [HttpGet]
        public IActionResult Auth(string? tab = null)
        {
            if (!string.IsNullOrEmpty(HttpContext.Session.GetString("UserEmail")))
                return RedirectToAction("Dashboard", "Account");

            ViewData["LoginModel"] = new LoginViewModel();
            ViewData["RegisterModel"] = new RegisterViewModel();
            if (!string.IsNullOrEmpty(tab))
                ViewData["ActiveTab"] = tab; // "login" or "register"
            return View("Index");
        }

        [HttpPost]
        public IActionResult SetLanguage(string culture, string? returnUrl = null)
        {
            if (!string.IsNullOrEmpty(culture))
            {
                Response.Cookies.Append(
                    CookieRequestCultureProvider.DefaultCookieName,
                    CookieRequestCultureProvider.MakeCookieValue(new RequestCulture(culture)),
                    new CookieOptions { Expires = DateTimeOffset.UtcNow.AddYears(1), IsEssential = true }
                );
            }
            return LocalRedirect(string.IsNullOrEmpty(returnUrl) ? "/" : returnUrl);
        }

        public IActionResult Error()
        {
            return View();
        }
    }
}
