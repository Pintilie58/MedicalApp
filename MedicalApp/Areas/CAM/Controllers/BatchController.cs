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
        private readonly ILogger<BatchController> _logger;

        public BatchController(
            AppDbContext db,
            ICamFileStore files,
            CamBatchRegistry registry,
            CamBatchService runner,
            ILogger<BatchController> logger)
        {
            _db = db;
            _files = files;
            _registry = registry;
            _runner = runner;
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
            var seeded = _registry.GetOrCreate(batch.Id, clinic.Id, total: 0);
            seeded.Log("Lot inițializat — pregătesc procesarea…");

            // Fire & forget. The runner uses its own DI scope, so it survives
            // the disposal of THIS controller's scope when the request returns.
            _ = Task.Run(() => _runner.RunAsync(batch.Id));

            TempData["SuccessMessage"] = "Lotul a pornit. Te poți întoarce oricând la această pagină.";
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
        [HttpGet]
        [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
        public async Task<IActionResult> Status(int id)
        {
            Response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate, max-age=0";
            Response.Headers["Pragma"] = "no-cache";
            Response.Headers["Expires"] = "0";

            if (string.IsNullOrEmpty(CurrentEmail))
                return Unauthorized();

            var clinic = await _db.Clinics.AsNoTracking()
                .FirstOrDefaultAsync(c => c.UserEmail == CurrentEmail);
            if (clinic == null) return NotFound();

            var batch = await _db.ClinicBatchRuns.AsNoTracking()
                .FirstOrDefaultAsync(b => b.Id == id && b.ClinicId == clinic.Id);
            if (batch == null) return NotFound();

            var p = _registry.Get(id);

            return Json(new
            {
                status = p?.Status ?? batch.Status,
                processed = p?.Processed ?? 0,
                total = p?.Total ?? batch.TotalFiles,
                sent = p?.Sent ?? batch.FilesSent,
                compared = p?.Compared ?? batch.FilesCompared,
                notSends = p?.NotSends ?? batch.NotSends,
                currentFile = p?.CurrentFile ?? string.Empty,
                log = p?.LogSnapshot() ?? new List<string>(),
                finished = batch.FinishedAt != null || p?.Status != "Running"
            });
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
