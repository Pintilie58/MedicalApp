using MedicalApp.Data;
using MedicalApp.Models;
using MedicalApp.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MedicalApp.Controllers
{
    public class CreditsController : Controller
    {
        private readonly AppDbContext _db;
        private readonly ILogger<CreditsController> _logger;

        public CreditsController(AppDbContext db, ILogger<CreditsController> logger)
        {
            _db = db;
            _logger = logger;
        }

        // ---------- Buy: show package selection ----------

        [HttpGet]
        public IActionResult Buy()
        {
            if (string.IsNullOrEmpty(HttpContext.Session.GetString("UserEmail")))
                return RedirectToAction("Index", "Home");

            return View(CreditPackages.All);
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
            await _db.SaveChangesAsync();

            _logger.LogInformation(
                "Simulated payment: {Email} bought {Credits} credits for {Price} EUR ({Package}).",
                email, selected.Credits, selected.PriceEur, selected.Key);

            TempData["SuccessMessage"] = string.Format(
                Loc.T("PaymentSuccessMessage"), selected.Credits);
            return RedirectToAction("Dashboard", "Account");
        }
    }
}
