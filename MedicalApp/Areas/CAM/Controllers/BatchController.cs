using MedicalApp.Data;
using MedicalApp.Models;
using MedicalApp.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MedicalApp.Areas.CAM.Controllers
{
    /// <summary>
    /// Lansare lot + monitorizare progres în timp real + anulare.
    /// Faza 3 a CAM Module.
    /// </summary>
    [Area("CAM")]
    public class BatchController : Controller
    {
        private readonly AppDbContext _db;
        private readonly ICamFileStore _files;
        private readonly CamBatchRegistry _registry;
        private readonly CamBatchService _runner;
        private readonly CamRetentionService _retention;
        private readonly ILogger<BatchController> _logger;

        public BatchController(
            AppDbContext db,
            ICamFileStore files,
            CamBatchRegistry registry,
            CamBatchService runner,
            CamRetentionService retention,
            ILogger<BatchController> logger)
        {
            _db = db;
            _files = files;
            _registry = registry;
            _runner = runner;
            _retention = retention;
            _logger = logger;
        }

        private string? CurrentEmail => HttpContext.Session.GetString("UserEmail");

        // ----- Preview: arată ce va procesa lotul -----
        [HttpGet]
        public async Task<IActionResult> Start()
        {
            if (string.IsNullOrEmpty(CurrentEmail))
                return RedirectToAction("Index", "Home", new { area = "" });

            var clinic = await _db.Clinics.AsNoTracking()
                .FirstOrDefaultAsync(c => c.UserEmail == CurrentEmail);
            if (clinic == null)
                return RedirectToAction("Index", "Dashboard", new { area = "CAM" });

            var user = await _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Email == CurrentEmail);

            var originalFolder = _files.GetOriginalFolder(clinic);
            var pdfs = Directory.Exists(originalFolder)
                ? Directory.GetFiles(originalFolder, "*.pdf", SearchOption.TopDirectoryOnly)
                    .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                    .Select(p => new FileInfo(p))
                    .ToList()
                : new List<FileInfo>();

            bool alreadyRunning = _registry.HasRunningForClinic(clinic.Id);
            int? runningId = null;
            if (alreadyRunning)
            {
                runningId = (await _db.ClinicBatchRuns
                    .Where(b => b.ClinicId == clinic.Id && b.Status == "Running")
                    .OrderByDescending(b => b.StartedAt)
                    .FirstOrDefaultAsync())?.Id;
            }

            var vm = new Models.CamBatchStartViewModel
            {
                ClinicName = clinic.Name,
                FileCount = pdfs.Count,
                Files = pdfs.Take(50).Select(f => new Models.CamBatchStartViewModel.FileRow
                {
                    FileName = f.Name,
                    SizeKb = (int)Math.Round(f.Length / 1024.0)
                }).ToList(),
                CreditsAvailable = user?.TotalAvailableCredits ?? 0,
                AlreadyRunning = alreadyRunning,
                RunningBatchId = runningId
            };
            return View(vm);
        }

        // ----- POST: pornește lotul în background -----
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Start(int confirm)
        {
            if (string.IsNullOrEmpty(CurrentEmail))
                return RedirectToAction("Index", "Home", new { area = "" });

            var clinic = await _db.Clinics.FirstOrDefaultAsync(c => c.UserEmail == CurrentEmail);
            if (clinic == null) return RedirectToAction("Index", "Dashboard", new { area = "CAM" });

            // Concurrency guard: only one Running batch per clinic at a time.
            if (_registry.HasRunningForClinic(clinic.Id))
            {
                TempData["ErrorMessage"] = "Există deja un lot în execuție pentru această clinică.";
                return RedirectToAction(nameof(Start));
            }

            // Auto-cleanup of Sends/Sumar/Errors before launching the batch.
            // Original is never touched. Files from the LAST completed batch
            // are protected regardless of age. Never throws.
            try
            {
                var cleanup = await _retention.CleanupAsync(clinic);
                if (cleanup.TotalDeleted > 0)
                {
                    _logger.LogInformation(
                        "Auto-cleanup before batch start for clinic {Email}: {Total} files deleted, {Bytes} freed.",
                        clinic.UserEmail, cleanup.TotalDeleted, cleanup.HumanSize);
                }
            }
            catch (Exception ex)
            {
                // Never let cleanup failures block a batch launch — log and move on.
                _logger.LogWarning(ex, "Auto-cleanup failed for clinic {Email}; continuing with batch start.",
                    clinic.UserEmail);
            }

            // Create the ClinicBatchRun row up-front so we have a stable id
            // to share with the background task and the progress UI.
            var batch = new ClinicBatchRun
            {
                ClinicId = clinic.Id,
                StartedAt = DateTime.UtcNow,
                Status = "Running",
                TotalFiles = 0
            };
            _db.ClinicBatchRuns.Add(batch);
            await _db.SaveChangesAsync();

            // SYNC pre-populate the in-memory progress entry BEFORE we kick
            // off the background Task.Run. Otherwise the first few polls from
            // /Progress/{id} race the background task and find _registry.Get
            // returning null — UI freezes on "0 / 0 fișiere" until the runner
            // gets past its DI scope setup (~200-500ms, can be longer on a
            // cold first request after IIS recycle). Seeding it here makes
            // the UI live from the very first poll. The runner then re-uses
            // the same entry via GetOrCreate (idempotent).
            // Capture the current UI language so the background batch can
            // localize its progress log AND the redirect flash message using
            // the operator's preferred language. The batch runs in a fresh
            // DI scope without HttpContext, so we must pass it explicitly.
            var lang = System.Globalization.CultureInfo.CurrentUICulture.Name;
            var langShort = string.IsNullOrEmpty(lang) ? "ro" : lang.Split('-')[0].ToLowerInvariant();

            var seeded = _registry.GetOrCreate(batch.Id, clinic.Id, total: 0);
            seeded.Log(Loc.T("CamBatchLogInitialized", langShort));

            // Fire & forget. The runner uses its own DI scope, so it survives
            // the disposal of THIS controller's scope when the request returns.
            _ = Task.Run(() => _runner.RunAsync(batch.Id, langShort));

            TempData["SuccessMessage"] = Loc.T("CamBatchStartFlash", langShort);
            return RedirectToAction(nameof(Progress), new { id = batch.Id });
        }

        // ----- Pagina de progres live -----
        [HttpGet]
        public async Task<IActionResult> Progress(int id)
        {
            if (string.IsNullOrEmpty(CurrentEmail))
                return RedirectToAction("Index", "Home", new { area = "" });

            var clinic = await _db.Clinics.AsNoTracking()
                .FirstOrDefaultAsync(c => c.UserEmail == CurrentEmail);
            if (clinic == null) return RedirectToAction("Index", "Dashboard", new { area = "CAM" });

            var batch = await _db.ClinicBatchRuns.AsNoTracking()
                .FirstOrDefaultAsync(b => b.Id == id && b.ClinicId == clinic.Id);
            if (batch == null) return NotFound();

            var vm = new Models.CamBatchProgressPageViewModel
            {
                BatchRunId = id,
                ClinicName = clinic.Name,
                StartedAt = batch.StartedAt
            };
            return View(vm);
        }

        // ----- JSON status (polled every 3s by the Progress page) -----
        // No-store cache headers + cache-busting query param on the client side
        // protect against the occasional intermediate proxy / IIS / browser
        // serving a stale 200 from cache, which would freeze the live UI.
        //
        // Performance: while the lot is RUNNING we serve the in-memory registry
        // entry directly without hitting the DB. The runner updates the entry
        // synchronously after every per-file step, so the data is always fresh.
        // Before this short-circuit, a 5-minute batch polled at 3s = ~100 polls,
        // each issuing 2 SQL queries (Clinic + ClinicBatchRun) for zero useful
        // delta. The DB read is preserved as a fallback for finished batches
        // (registry entry is removed) and for AuthZ enforcement (clinic match).
        [HttpGet]
        [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
        public async Task<IActionResult> Status(int id)
        {
            Response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate, max-age=0";
            Response.Headers["Pragma"] = "no-cache";
            Response.Headers["Expires"] = "0";

            if (string.IsNullOrEmpty(CurrentEmail))
                return Unauthorized();

            // FAST PATH: in-memory registry has the live state. AuthZ via ClinicId
            // cached in Session at login. ZERO DB queries on the hot polling path.
            var p = _registry.Get(id);
            if (p != null && p.Status == "Running")
            {
                var sessionClinicId = HttpContext.Session.GetInt32("ClinicId");
                if (sessionClinicId.HasValue)
                {
                    if (p.ClinicId != sessionClinicId.Value) return NotFound();
                }
                else
                {
                    // Session cache missing (legacy session pre-fix, or non-Clinic user).
                    // One-time DB lookup + memoize back in Session so next polls are free.
                    var clinicId = await GetClinicIdAsync();
                    if (clinicId == 0) return NotFound();
                    HttpContext.Session.SetInt32("ClinicId", clinicId);
                    if (p.ClinicId != clinicId) return NotFound();
                }

                return Json(new
                {
                    status = p.Status,
                    processed = p.Processed,
                    total = p.Total,
                    sent = p.Sent,
                    compared = p.Compared,
                    notSends = p.NotSends,
                    currentFile = p.CurrentFile ?? string.Empty,
                    log = p.LogSnapshot(),
                    finished = false
                });
            }

            // SLOW PATH: batch is finished (registry entry purged) or never started
            // — fall back to DB for the persisted final counts.
            var clinic = await _db.Clinics.AsNoTracking()
                .FirstOrDefaultAsync(c => c.UserEmail == CurrentEmail);
            if (clinic == null) return NotFound();

            var batch = await _db.ClinicBatchRuns.AsNoTracking()
                .FirstOrDefaultAsync(b => b.Id == id && b.ClinicId == clinic.Id);
            if (batch == null) return NotFound();

            return Json(new
            {
                status = p?.Status ?? batch.Status,
                processed = p?.Processed ?? batch.TotalFiles,
                total = p?.Total ?? batch.TotalFiles,
                sent = p?.Sent ?? batch.FilesSent,
                compared = p?.Compared ?? batch.FilesCompared,
                notSends = p?.NotSends ?? batch.NotSends,
                currentFile = p?.CurrentFile ?? string.Empty,
                log = p?.LogSnapshot() ?? new List<string>(),
                finished = batch.FinishedAt != null || p?.Status != "Running"
            });
        }

        // Returns the current user's ClinicId, or 0 when no clinic matches.
        // Used by the Status fast path to enforce per-clinic isolation without
        // loading the full Clinic entity.
        private async Task<int> GetClinicIdAsync()
        {
            return await _db.Clinics.AsNoTracking()
                .Where(c => c.UserEmail == CurrentEmail)
                .Select(c => c.Id)
                .FirstOrDefaultAsync();
        }

        // ----- POST: anulează lotul -----
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Cancel(int id)
        {
            if (string.IsNullOrEmpty(CurrentEmail))
                return RedirectToAction("Index", "Home", new { area = "" });

            var clinic = await _db.Clinics.AsNoTracking()
                .FirstOrDefaultAsync(c => c.UserEmail == CurrentEmail);
            if (clinic == null) return RedirectToAction("Index", "Dashboard", new { area = "CAM" });

            var p = _registry.Get(id);
            if (p != null && p.ClinicId == clinic.Id && p.Status == "Running")
            {
                p.Cts.Cancel();
                p.Log("⚠ Anulare cerută de operator — opresc după fișierul curent.");
                TempData["SuccessMessage"] = "Anulare cerută. Lotul se va opri după fișierul curent.";
            }
            return RedirectToAction(nameof(Progress), new { id });
        }
    }
}
