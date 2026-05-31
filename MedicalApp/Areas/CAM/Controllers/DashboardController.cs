using MedicalApp.Data;
using MedicalApp.Models;
using MedicalApp.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MedicalApp.Areas.CAM.Controllers
{
    /// <summary>
    /// Dashboard pentru utilizatorii de tip "Clinic" (Clinici de Analize Medicale).
    /// Faza 4: KPI lifetime, istoric loturi, top pacienți, activitate 30 zile +
    /// export Sumar PDF per lot.
    /// </summary>
    [Area("CAM")]
    public class DashboardController : Controller
    {
        private readonly AppDbContext _db;
        private readonly ICamFileStore _files;
        private readonly CamBatchSumarPdfGenerator _sumarPdfGen;
        private readonly ILogger<DashboardController> _logger;

        public DashboardController(
            AppDbContext db,
            ICamFileStore files,
            CamBatchSumarPdfGenerator sumarPdfGen,
            ILogger<DashboardController> logger)
        {
            _db = db;
            _files = files;
            _sumarPdfGen = sumarPdfGen;
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

            await PopulateStatsAsync(vm, clinic.Id);
            return View(vm);
        }

        /// <summary>
        /// Computes lifetime KPIs, last-30-day activity bars, top patients and
        /// the recent-batches table for the CAM dashboard. All EF queries run
        /// AsNoTracking to keep this read-only path fast.
        /// </summary>
        private async Task PopulateStatsAsync(Models.CamDashboardViewModel vm, int clinicId)
        {
            // ----- KPI loturi (aggregations) -----
            var batchStats = await _db.ClinicBatchRuns.AsNoTracking()
                .Where(b => b.ClinicId == clinicId)
                .GroupBy(_ => 1)
                .Select(g => new
                {
                    Total = g.Count(),
                    Completed = g.Count(b => b.Status == "Completed"),
                    Failed = g.Count(b => b.Status == "Failed"),
                    Cancelled = g.Count(b => b.Status == "Cancelled"),
                    Interpreted = g.Sum(b => (int?)b.FilesInterpreted) ?? 0,
                    Sent = g.Sum(b => (int?)b.FilesSent) ?? 0,
                    Compared = g.Sum(b => (int?)b.FilesCompared) ?? 0,
                    NotSends = g.Sum(b => (int?)b.NotSends) ?? 0
                })
                .FirstOrDefaultAsync();

            if (batchStats != null)
            {
                vm.TotalBatches = batchStats.Total;
                vm.CompletedBatches = batchStats.Completed;
                vm.FailedBatches = batchStats.Failed;
                vm.CancelledBatches = batchStats.Cancelled;
                vm.LifetimeFilesInterpreted = batchStats.Interpreted;
                vm.LifetimeFilesSent = batchStats.Sent;
                vm.LifetimeFilesCompared = batchStats.Compared;
                vm.LifetimeNotSends = batchStats.NotSends;
            }

            // ----- Pacienți unici cu cel puțin o analiză -----
            vm.TotalPatients = await _db.ClinicAnalyses.AsNoTracking()
                .Where(a => a.ClinicId == clinicId)
                .Select(a => a.PatientId)
                .Distinct()
                .CountAsync();

            // ----- Loturi pe anul curent / luna curentă (UTC) -----
            var nowUtc = DateTime.UtcNow;
            var startOfYear = new DateTime(nowUtc.Year, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var startOfMonth = new DateTime(nowUtc.Year, nowUtc.Month, 1, 0, 0, 0, DateTimeKind.Utc);

            vm.BatchesThisYear = await _db.ClinicBatchRuns.AsNoTracking()
                .Where(b => b.ClinicId == clinicId && b.StartedAt >= startOfYear)
                .CountAsync();
            vm.BatchesThisMonth = await _db.ClinicBatchRuns.AsNoTracking()
                .Where(b => b.ClinicId == clinicId && b.StartedAt >= startOfMonth)
                .CountAsync();
            vm.CurrentYear = nowUtc.Year;
            // Format "Mai-2026" — ro-RO month name, capitalized, then dash + year.
            var ro = new System.Globalization.CultureInfo("ro-RO");
            var monthName = ro.DateTimeFormat.GetMonthName(nowUtc.Month);
            if (monthName.Length > 0)
                monthName = char.ToUpper(monthName[0], ro) + monthName.Substring(1);
            vm.CurrentMonthLabel = $"{monthName}-{nowUtc.Year}";

            // ----- Ultimele 20 loturi -----
            var recent = await _db.ClinicBatchRuns.AsNoTracking()
                .Where(b => b.ClinicId == clinicId)
                .OrderByDescending(b => b.StartedAt)
                .Take(20)
                .ToListAsync();

            vm.RecentBatches = recent
                .Select(b => new Models.CamDashboardViewModel.BatchHistoryRow
                {
                    BatchRunId = b.Id,
                    StartedAt = b.StartedAt,
                    FinishedAt = b.FinishedAt,
                    Status = b.Status,
                    TotalFiles = b.TotalFiles,
                    FilesSent = b.FilesSent,
                    FilesCompared = b.FilesCompared,
                    NotSends = b.NotSends,
                    DurationDisplay = (b.FinishedAt - b.StartedAt)?.ToString(@"hh\:mm\:ss") ?? "-"
                })
                .ToList();
        }

        // -------------------------------------------------------------
        // Sumar PDF download for a finished batch run. Generates the PDF
        // on demand (always fresh) and also persists a copy in the clinic's
        // Sumar/ folder so the operator has a local audit trail.
        // -------------------------------------------------------------
        [HttpGet]
        public async Task<IActionResult> SumarPdf(int id)
        {
            if (string.IsNullOrEmpty(CurrentEmail))
                return RedirectToAction("Index", "Home", new { area = "" });

            var clinic = await _db.Clinics.AsNoTracking()
                .FirstOrDefaultAsync(c => c.UserEmail == CurrentEmail);
            if (clinic == null) return NotFound();

            var batch = await _db.ClinicBatchRuns.AsNoTracking()
                .FirstOrDefaultAsync(b => b.Id == id && b.ClinicId == clinic.Id);
            if (batch == null) return NotFound();

            var errors = await _db.ClinicBatchErrors.AsNoTracking()
                .Where(e => e.BatchRunId == id)
                .OrderBy(e => e.OccurredAt)
                .ToListAsync();

            byte[] pdfBytes;
            try
            {
                pdfBytes = _sumarPdfGen.Generate(clinic, batch, errors);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "CAM SumarPdf: generation failed for batch {Id}", id);
                TempData["ErrorMessage"] = "Generarea PDF Sumar a eșuat: " + ex.Message;
                return RedirectToAction(nameof(Index));
            }

            // Best-effort persist to Sumar/ — failures are non-fatal (the
            // user already gets the PDF via the response).
            try
            {
                var sumarFolder = _files.GetSumarFolder(clinic);
                Directory.CreateDirectory(sumarFolder);
                var fileName = $"Sumar_Lot_{batch.Id}_{batch.StartedAt.ToLocalTime():yyyyMMdd_HHmm}.pdf";
                System.IO.File.WriteAllBytes(Path.Combine(sumarFolder, fileName), pdfBytes);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "CAM SumarPdf: could not write Sumar PDF to disk for batch {Id}", id);
            }

            var downloadName = $"Sumar_Lot_{batch.Id}_{batch.StartedAt.ToLocalTime():yyyyMMdd_HHmm}.pdf";
            return File(pdfBytes, "application/pdf", downloadName);
        }
    }
}
