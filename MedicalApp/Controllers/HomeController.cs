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
            ViewData["LoginModel"] = new LoginViewModel();
            ViewData["RegisterModel"] = new RegisterViewModel();
            return View();
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
