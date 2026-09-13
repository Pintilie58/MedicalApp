using MedicalApp.Data;
using MedicalApp.Models;
using MedicalApp.Services;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;

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
        private readonly CamBatchQueueStore _queue;
        private readonly IDistributedCache _cache;
        private readonly CamRetentionService _retention;
        private readonly ILogger<BatchController> _logger;

        public BatchController(
            AppDbContext db,
            ICamFileStore files,
            CamBatchRegistry registry,
            CamBatchQueueStore queue,
            IDistributedCache cache,
            CamRetentionService retention,
            ILogger<BatchController> logger)
        {
            _db = db;
            _files = files;
            _registry = registry;
            _queue = queue;
            _cache = cache;
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

            var pdfs = (await _files.ListAsync(clinic, CamFolder.Original, ".pdf"))
                .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            // Durable check (June 2026): the in-memory registry only knew about
            // THIS instance, so on Azure a second instance happily started a
            // parallel batch for the same clinic.
            bool alreadyRunning = await _queue.HasActiveForClinicAsync(clinic.Id);
            int? runningId = null;
            if (alreadyRunning)
            {
                runningId = (await _db.ClinicBatchRuns.AsNoTracking()
                    .Where(b => b.ClinicId == clinic.Id
                                && (b.Status == "Running" || b.Status == "Queued")
                                && b.FinishedAt == null)
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
                    SizeKb = (int)Math.Round(f.SizeBytes / 1024.0)
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

            // Concurrency guard: only one active batch per clinic at a time,
            // checked in the DATABASE so it holds across Azure instances.
            if (await _queue.HasActiveForClinicAsync(clinic.Id))
            {
                TempData["ErrorMessage"] = Loc.T("ErrBatchAlreadyRunning");
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
            // The row IS the queue entry. "Queued" means: nobody is working on
            // it yet, any instance may claim it. Writing it here (instead of
            // starting a Task.Run) is what stops a batch from dying together
            // with the instance that served this request.
            var lang0 = System.Globalization.CultureInfo.CurrentUICulture.Name;
            var batch = new ClinicBatchRun
            {
                ClinicId = clinic.Id,
                StartedAt = DateTime.UtcNow,
                Status = "Queued",
                TotalFiles = 0,
                LanguageCode = string.IsNullOrEmpty(lang0) ? "ro" : lang0.Split('-')[0].ToLowerInvariant()
            };
            _db.ClinicBatchRuns.Add(batch);
            await _db.SaveChangesAsync();

            // CamBatchQueueWorker (this instance or another one) picks it up
            // within a few seconds, claims a lease and runs it. Until then the
            // progress page shows "waiting to start" instead of a frozen 0/0.
            var langShort = batch.LanguageCode ?? "ro";

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

            // SLOW PATH: the batch is finished, has not started yet, or is
            // running on ANOTHER instance (Azure). Authorize against the DB,
            // then prefer the snapshot published by the instance doing the work.
            var clinic = await _db.Clinics.AsNoTracking()
                .FirstOrDefaultAsync(c => c.UserEmail == CurrentEmail);
            if (clinic == null) return NotFound();

            var batch = await _db.ClinicBatchRuns.AsNoTracking()
                .FirstOrDefaultAsync(b => b.Id == id && b.ClinicId == clinic.Id);
            if (batch == null) return NotFound();

            CamBatchSnapshot? snap = null;
            if (p == null && batch.FinishedAt == null)
            {
                try
                {
                    var json = await _cache.GetStringAsync(CamBatchQueueWorker.SnapshotKey(id));
                    if (!string.IsNullOrEmpty(json))
                        snap = JsonSerializer.Deserialize<CamBatchSnapshot>(json);
                    if (snap != null && snap.ClinicId != clinic.Id) snap = null;
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "CAM batch {Id}: shared progress snapshot unavailable.", id);
                }
            }

            // A queued batch is NOT finished — it is waiting for a worker.
            var stillActive = batch.FinishedAt == null
                              && (batch.Status == "Queued" || batch.Status == "Running");

            return Json(new
            {
                status = p?.Status ?? snap?.Status ?? batch.Status,
                processed = p?.Processed ?? snap?.Processed
                            ?? (stillActive ? batch.FilesInterpreted + batch.NotSends : batch.TotalFiles),
                total = p?.Total ?? snap?.Total ?? batch.TotalFiles,
                sent = p?.Sent ?? snap?.Sent ?? batch.FilesSent,
                compared = p?.Compared ?? snap?.Compared ?? batch.FilesCompared,
                notSends = p?.NotSends ?? snap?.NotSends ?? batch.NotSends,
                currentFile = p?.CurrentFile ?? snap?.CurrentFile ?? string.Empty,
                log = p?.LogSnapshot() ?? snap?.Log ?? new List<string>(),
                finished = !stillActive
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

            // Durable cancel: the batch may be running on another instance, so
            // the flag goes into the row and the worker's heartbeat picks it up
            // within a few seconds. The local cancel below stays as the instant
            // path for the (usual) case where we ARE the owner.
            var row = await _db.ClinicBatchRuns
                .FirstOrDefaultAsync(b => b.Id == id && b.ClinicId == clinic.Id);
            if (row == null) return RedirectToAction(nameof(Progress), new { id });

            if (row.FinishedAt == null && (row.Status == "Running" || row.Status == "Queued"))
            {
                row.CancelRequested = true;
                // Never claimed by anyone: cancel it right here, nothing to stop.
                if (row.Status == "Queued")
                {
                    row.Status = "Cancelled";
                    row.FinishedAt = DateTime.UtcNow;
                }
                await _db.SaveChangesAsync();
                TempData["SuccessMessage"] = Loc.T("OkBatchCancelRequested");
            }

            var p = _registry.Get(id);
            if (p != null && p.ClinicId == clinic.Id && p.Status == "Running")
            {
                p.Cts.Cancel();
                p.Log(Loc.T("CamBatchLogCancelRequested"));
            }
            return RedirectToAction(nameof(Progress), new { id });
        }
    }
}
