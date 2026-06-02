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
        private readonly CamRetentionService _retention;
        private readonly ILogger<DashboardController> _logger;

        public DashboardController(
            AppDbContext db,
            ICamFileStore files,
            CamBatchSumarPdfGenerator sumarPdfGen,
            CamRetentionService retention,
            ILogger<DashboardController> logger)
        {
            _db = db;
            _files = files;
            _sumarPdfGen = sumarPdfGen;
            _retention = retention;
            _logger = logger;
        }

        private string? CurrentEmail => HttpContext.Session.GetString("UserEmail");

        [HttpGet]
        public async Task<IActionResult> Index(int? year, int? month)
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

            await PopulateStatsAsync(vm, clinic.Id, year, month);

            // Disk usage snapshot — read-only, runs after stats so this never
            // breaks the dashboard if the disk is unreachable.
            try
            {
                var usage = _retention.MeasureUsage(clinic);
                vm.DiskBytesTotal = usage.BytesTotal;
                vm.DiskFilesTotal = usage.FilesTotal;
                vm.DiskBytesSends = usage.BytesSends;
                vm.DiskBytesSumar = usage.BytesSumar;
                vm.DiskBytesErrors = usage.BytesErrors;
                vm.DiskBytesOriginal = usage.BytesOriginal;
                vm.RetentionDaysDefault =
                    HttpContext.RequestServices.GetService<Microsoft.Extensions.Options.IOptions<CamSettings>>()?
                        .Value.RetentionDays ?? 30;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Disk usage measurement failed for clinic {Id}", clinic.Id);
            }

            return View(vm);
        }

        // -------------------------------------------------------------
        // Manual cleanup — operator-initiated retention sweep from the
        // dashboard "Curăță fișiere vechi" button. Auto-cleanup also runs
        // automatically before every "Lansează lot" in BatchController.
        // -------------------------------------------------------------
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Cleanup(int? days)
        {
            if (string.IsNullOrEmpty(CurrentEmail))
                return RedirectToAction("Index", "Home", new { area = "" });

            var clinic = await _db.Clinics
                .FirstOrDefaultAsync(c => c.UserEmail == CurrentEmail);
            if (clinic == null)
                return RedirectToAction(nameof(Index));

            try
            {
                var result = await _retention.CleanupAsync(clinic, days);
                if (result.TotalDeleted == 0)
                {
                    TempData["SuccessMessage"] =
                        $"Niciun fișier nu era mai vechi de {result.RetentionDaysUsed} zile " +
                        $"(protejate de ultimul lot: {result.FilesProtectedByLastBatch}).";
                }
                else
                {
                    TempData["SuccessMessage"] =
                        $"Au fost șterse {result.TotalDeleted} fișiere ({result.HumanSize} eliberat) " +
                        $"— retenție {result.RetentionDaysUsed} zile. " +
                        $"Sends: {result.FilesDeletedSends} · Sumar: {result.FilesDeletedSumar} · " +
                        $"Errors: {result.FilesDeletedErrors}. " +
                        $"Protejate de ultimul lot: {result.FilesProtectedByLastBatch}.";
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Manual cleanup failed for clinic {Email}", CurrentEmail);
                TempData["ErrorMessage"] = "Curățarea a eșuat: " + ex.Message;
            }

            return RedirectToAction(nameof(Index));
        }

        /// <summary>
        /// Computes lifetime KPIs, last-30-day activity bars, top patients and
        /// the recent-batches table for the CAM dashboard. All EF queries run
        /// AsNoTracking to keep this read-only path fast.
        /// </summary>
        private async Task PopulateStatsAsync(Models.CamDashboardViewModel vm, int clinicId,
            int? selectedYear, int? selectedMonth)
        {
            // ----- KPI lifetime (sume cumulate pe fișiere — păstrate pentru cardurile sus) -----
            var lifetimeStats = await _db.ClinicBatchRuns.AsNoTracking()
                .Where(b => b.ClinicId == clinicId)
                .GroupBy(_ => 1)
                .Select(g => new
                {
                    Interpreted = g.Sum(b => (int?)b.FilesInterpreted) ?? 0,
                    Sent = g.Sum(b => (int?)b.FilesSent) ?? 0,
                    Compared = g.Sum(b => (int?)b.FilesCompared) ?? 0,
                    NotSends = g.Sum(b => (int?)b.NotSends) ?? 0
                })
                .FirstOrDefaultAsync();

            if (lifetimeStats != null)
            {
                vm.LifetimeFilesInterpreted = lifetimeStats.Interpreted;
                vm.LifetimeFilesSent = lifetimeStats.Sent;
                vm.LifetimeFilesCompared = lifetimeStats.Compared;
                vm.LifetimeNotSends = lifetimeStats.NotSends;
            }

            // ----- Available years (descending) — for the year dropdown.
            //       Query StartedAt years that actually have batches. -----
            var availableYears = await _db.ClinicBatchRuns.AsNoTracking()
                .Where(b => b.ClinicId == clinicId)
                .Select(b => b.StartedAt.Year)
                .Distinct()
                .ToListAsync();
            availableYears = availableYears.OrderByDescending(y => y).ToList();

            var nowUtc = DateTime.UtcNow;
            // Ensure current year is always pickable even before any batch is launched.
            if (!availableYears.Contains(nowUtc.Year))
                availableYears.Insert(0, nowUtc.Year);
            vm.AvailableYears = availableYears;

            // ----- Resolve selected year/month (defaults = now) -----
            int year = selectedYear ?? nowUtc.Year;
            // Clamp month to 1..12; default to current month when no year override is given,
            // otherwise default to month=1 when navigating to a past year.
            int month;
            if (selectedMonth.HasValue && selectedMonth.Value >= 1 && selectedMonth.Value <= 12)
                month = selectedMonth.Value;
            else if (selectedYear.HasValue && selectedYear.Value != nowUtc.Year)
                month = 1;
            else
                month = nowUtc.Month;

            // ----- Stats for the selected year (1 Jan .. 31 Dec inclusive) -----
            var startOfYear = new DateTime(year, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var endOfYear = startOfYear.AddYears(1);
            vm.YearStats = await ComputeBatchPeriodRangeAsync(clinicId, startOfYear, endOfYear);

            // ----- Stats for the selected month (1st .. last day inclusive) -----
            var startOfMonth = new DateTime(year, month, 1, 0, 0, 0, DateTimeKind.Utc);
            var endOfMonth = startOfMonth.AddMonths(1);
            vm.MonthStats = await ComputeBatchPeriodRangeAsync(clinicId, startOfMonth, endOfMonth);

            vm.SelectedYear = year;
            vm.SelectedMonth = month;
            vm.CurrentYear = year;
            var ro = new System.Globalization.CultureInfo("ro-RO");
            var monthName = ro.DateTimeFormat.GetMonthName(month);
            if (monthName.Length > 0)
                monthName = char.ToUpper(monthName[0], ro) + monthName.Substring(1);
            vm.CurrentMonthLabel = $"{monthName}-{year}";

            // ----- Ultimele 10 loturi -----
            var recent = await _db.ClinicBatchRuns.AsNoTracking()
                .Where(b => b.ClinicId == clinicId)
                .OrderByDescending(b => b.StartedAt)
                .Take(10)
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

        /// <summary>
        /// Computes batch counts + distinct patients for an explicit
        /// [startUtc, endUtc) range. Replaces the older open-ended variant
        /// so we can scope a stat to one specific year or month.
        /// </summary>
        private async Task<Models.CamDashboardViewModel.BatchPeriodStats> ComputeBatchPeriodRangeAsync(
            int clinicId, DateTime startUtc, DateTime endUtc)
        {
            var batchAgg = await _db.ClinicBatchRuns.AsNoTracking()
                .Where(b => b.ClinicId == clinicId
                            && b.StartedAt >= startUtc
                            && b.StartedAt < endUtc)
                .GroupBy(_ => 1)
                .Select(g => new
                {
                    Total = g.Count(),
                    Completed = g.Count(b => b.Status == "Completed"),
                    Failed = g.Count(b => b.Status == "Failed"),
                    Cancelled = g.Count(b => b.Status == "Cancelled")
                })
                .FirstOrDefaultAsync();

            var patients = await _db.ClinicAnalyses.AsNoTracking()
                .Where(a => a.ClinicId == clinicId
                            && a.ProcessedAt >= startUtc
                            && a.ProcessedAt < endUtc)
                .Select(a => a.PatientId)
                .Distinct()
                .CountAsync();

            return new Models.CamDashboardViewModel.BatchPeriodStats
            {
                Total = batchAgg?.Total ?? 0,
                Completed = batchAgg?.Completed ?? 0,
                Failed = batchAgg?.Failed ?? 0,
                Cancelled = batchAgg?.Cancelled ?? 0,
                Patients = patients
            };
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
