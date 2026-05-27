using MedicalApp.Data;
using MedicalApp.Models;
using MedicalApp.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MedicalApp.Areas.CAM.Controllers
{
    /// <summary>
    /// Dashboard pentru utilizatorii de tip "Clinic" (Clinici de Analize Medicale).
    /// În Faza 1 conține doar landing page-ul + verificarea că folderele
    /// există pe disk. Următoarele faze vor adăuga listă pacienți,
    /// "Lansează Lot", sumar batch-uri etc.
    /// </summary>
    [Area("CAM")]
    public class DashboardController : Controller
    {
        private readonly AppDbContext _db;
        private readonly ICamFileStore _files;
        private readonly ILogger<DashboardController> _logger;

        public DashboardController(
            AppDbContext db,
            ICamFileStore files,
            ILogger<DashboardController> logger)
        {
            _db = db;
            _files = files;
            _logger = logger;
        }

        private string? CurrentEmail => HttpContext.Session.GetString("UserEmail");

        [HttpGet]
        public async Task<IActionResult> Index()
        {
            if (string.IsNullOrEmpty(CurrentEmail))
                return RedirectToAction("Index", "Home", new { area = "" });

            var user = await _db.Users.AsNoTracking()
                .FirstOrDefaultAsync(u => u.Email == CurrentEmail);
            if (user == null)
            {
                HttpContext.Session.Clear();
                return RedirectToAction("Index", "Home", new { area = "" });
            }

            // Doar conturile "Clinic" au acces la /CAM/*. Restul sunt redirectați
            // către dashboard-ul B2C clasic.
            if (!string.Equals(user.UserType, "Clinic", StringComparison.OrdinalIgnoreCase))
            {
                return RedirectToAction("Dashboard", "Account", new { area = "" });
            }

            var clinic = await _db.Clinics.AsNoTracking()
                .FirstOrDefaultAsync(c => c.UserEmail == CurrentEmail);

            // Edge case: user marcat "Clinic" dar fără rând în Clinics (date legacy).
            // Cere completarea datelor mai târziu — pentru moment redirect la Buy.
            if (clinic == null)
            {
                TempData["ErrorMessage"] = "Contul tău e marcat ca \"Clinică\" dar nu are date de clinică asociate. Contactează administratorul.";
                return RedirectToAction("Buy", "Credits", new { area = "" });
            }

            var vm = new Models.CamDashboardViewModel
            {
                ClinicName = clinic.Name,
                ClinicCity = clinic.City,
                ClinicAddress = clinic.Address,
                CreditsAvailable = user.TotalAvailableCredits,
                FoldersCreated = clinic.FoldersCreatedAt.HasValue,
                FoldersCreatedAt = clinic.FoldersCreatedAt,
                ClinicFolderRoot = _files.GetClinicRoot(clinic),
                OriginalFolder = _files.GetOriginalFolder(clinic),
                SendsFolder = _files.GetSendsFolder(clinic),
                SumarFolder = _files.GetSumarFolder(clinic),
                ErrorsFolder = _files.GetErrorsFolder(clinic)
            };

            return View(vm);
        }
    }
}
