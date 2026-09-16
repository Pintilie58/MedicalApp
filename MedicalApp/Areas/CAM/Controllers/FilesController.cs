using System.IO.Compression;
using MedicalApp.Data;
using MedicalApp.Models;
using MedicalApp.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MedicalApp.Areas.CAM.Controllers
{
    /// <summary>
    /// /CAM/Files — the web "counter" over the clinic's four CAM buckets
    /// (Original / Sends / Sumar / Errors). Replaces what the operator used to
    /// do with Windows Explorer on the server disk, so it works identically on
    /// local disk and on Azure Blob Storage (everything goes through
    /// <see cref="ICamFileStore"/>; nothing here touches the processing code).
    /// </summary>
    [Area("CAM")]
    public class FilesController : Controller
    {
        private const string ReasonsSuffix = ".reasons.txt";

        private readonly AppDbContext _db;
        private readonly ICamFileStore _files;
        private readonly CamCheckPdfsBuilder _workbench;
        private readonly ILogger<FilesController> _logger;

        public FilesController(AppDbContext db, ICamFileStore files, CamCheckPdfsBuilder workbench,
            ILogger<FilesController> logger)
        {
            _db = db;
            _files = files;
            _workbench = workbench;
            _logger = logger;
        }

        private string? CurrentEmail => HttpContext.Session.GetString("UserEmail");

        private async Task<Clinic?> CurrentClinicAsync() =>
            string.IsNullOrEmpty(CurrentEmail)
                ? null
                : await _db.Clinics.AsNoTracking().FirstOrDefaultAsync(c => c.UserEmail == CurrentEmail);

        private static bool TryParseFolder(string? raw, out CamFolder folder) =>
            Enum.TryParse(raw, ignoreCase: true, out folder) && Enum.IsDefined(folder);

        /// <summary>Accepts only a bare file name — refuses anything with path components.</summary>
        private static string? SafeName(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            var name = Path.GetFileName(raw);
            return string.Equals(name, raw, StringComparison.Ordinal) ? name : null;
        }

        [HttpGet]
        public async Task<IActionResult> Index(string? folder)
        {
            if (string.IsNullOrEmpty(CurrentEmail))
                return RedirectToAction("Index", "Home", new { area = "" });
            var clinic = await CurrentClinicAsync();
            if (clinic == null)
                return RedirectToAction("Index", "Dashboard", new { area = "CAM" });

            if (!TryParseFolder(folder, out var current)) current = CamFolder.Original;

            var vm = new Models.CamFilesViewModel
            {
                ClinicName = clinic.Name,
                Folder = current,
                DisplayLocation = _files.GetDisplayLocation(clinic, current)
            };

            foreach (CamFolder f in Enum.GetValues<CamFolder>())
            {
                var entries = await _files.ListAsync(clinic, f);
                var visible = entries.Where(e => !IsReasonsFile(e.Name)).ToList();
                vm.Counts[f] = visible.Count;
                if (f == current)
                {
                    vm.Items = visible
                        .Select(e => new Models.CamFilesViewModel.Row
                        {
                            FileName = e.Name,
                            SizeBytes = e.SizeBytes,
                            LastModifiedUtc = e.LastModifiedUtc
                        })
                        .ToList();
                }
            }

            if (current == CamFolder.Errors && vm.Items.Count > 0)
                await FillErrorReasonsAsync(clinic, vm.Items);

            // "De trimis" is the operator's workbench: identity preview + override
            // editing, exactly what /CAM/CheckPdfs used to render.
            if (current == CamFolder.Original)
                vm.Workbench = await _workbench.BuildAsync(clinic, HttpContext.RequestAborted);

            return View(vm);
        }

        private static bool IsReasonsFile(string name) =>
            name.EndsWith(ReasonsSuffix, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Latest recorded reason per file. Files land in Errors with a stamped
        /// name ("20260614_101010_Popescu.pdf") while ClinicBatchErrors keeps the
        /// original name, so we match on the DB name being a suffix of the stored
        /// name; the .reasons.txt written next to the file is the fallback.
        /// </summary>
        private async Task FillErrorReasonsAsync(Clinic clinic, List<Models.CamFilesViewModel.Row> rows)
        {
            var reasons = await _db.ClinicBatchErrors.AsNoTracking()
                .Join(_db.ClinicBatchRuns, e => e.BatchRunId, b => b.Id, (e, b) => new { e, b.ClinicId })
                .Where(x => x.ClinicId == clinic.Id)
                .OrderByDescending(x => x.e.OccurredAt)
                .Select(x => new { x.e.FileName, x.e.Reason })
                .Take(2000)
                .ToListAsync();

            foreach (var row in rows)
            {
                var hit = reasons.FirstOrDefault(r =>
                    row.FileName.EndsWith(r.FileName, StringComparison.OrdinalIgnoreCase));
                if (hit != null) { row.Reason = hit.Reason; continue; }

                try
                {
                    var txt = await _files.ReadAsync(clinic, CamFolder.Errors, row.FileName + ReasonsSuffix);
                    if (txt != null)
                    {
                        var lines = System.Text.Encoding.UTF8.GetString(txt)
                            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                        row.Reason = lines.LastOrDefault()?.TrimStart('•', ' ');
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Files: could not read reasons for {File}", row.FileName);
                }
            }
        }

        // ----- GET: download one file -----
        [HttpGet]
        public async Task<IActionResult> Download(string folder, string name)
        {
            var clinic = await CurrentClinicAsync();
            if (clinic == null) return RedirectToAction("Index", "Home", new { area = "" });
            if (!TryParseFolder(folder, out var f)) return NotFound();
            var safe = SafeName(name);
            if (safe == null) return BadRequest();

            var bytes = await _files.ReadAsync(clinic, f, safe);
            if (bytes == null)
            {
                TempData["ErrorMessage"] = string.Format(Loc.T("ErrFileNoLongerExists"), safe);
                return RedirectToAction(nameof(Index), new { folder = f });
            }

            var contentType = safe.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)
                ? "application/pdf"
                : safe.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)
                    ? "text/plain; charset=utf-8"
                    : "application/octet-stream";
            return File(bytes, contentType, safe);
        }

        // ----- GET: whole bucket as a ZIP, streamed file by file -----
        [HttpGet]
        public async Task<IActionResult> DownloadZip(string folder)
        {
            var clinic = await CurrentClinicAsync();
            if (clinic == null) return RedirectToAction("Index", "Home", new { area = "" });
            if (!TryParseFolder(folder, out var f)) return NotFound();

            var entries = await _files.ListAsync(clinic, f);
            if (entries.Count == 0)
            {
                TempData["ErrorMessage"] = Loc.T("CamFilesEmpty");
                return RedirectToAction(nameof(Index), new { folder = f });
            }

            var clinicPart = string.Concat(clinic.Name.Select(ch =>
                char.IsLetterOrDigit(ch) ? ch : '_'));
            var zipName = $"{clinicPart}_{f}_{DateTime.Now:yyyyMMdd_HHmm}.zip";
            Response.ContentType = "application/zip";
            Response.Headers.ContentDisposition = $"attachment; filename=\"{zipName}\"";

            using (var zip = new ZipArchive(Response.Body, ZipArchiveMode.Create, leaveOpen: true))
            {
                foreach (var e in entries)
                {
                    var bytes = await _files.ReadAsync(clinic, f, e.Name, HttpContext.RequestAborted);
                    if (bytes == null) continue;
                    var entry = zip.CreateEntry(e.Name, CompressionLevel.Fastest);
                    entry.LastWriteTime = e.LastModifiedUtc;
                    using var s = entry.Open();
                    await s.WriteAsync(bytes, HttpContext.RequestAborted);
                }
            }
            return new EmptyResult();
        }

        // ----- POST: one file per request (the JS uploader shows per-file progress) -----
        [HttpPost]
        [ValidateAntiForgeryToken]
        [RequestSizeLimit(50_000_000)] // 50 MB — a single lab report is < 5 MB
        public async Task<IActionResult> Upload(IFormFile? file)
        {
            var clinic = await CurrentClinicAsync();
            if (clinic == null) return Unauthorized();

            if (file == null || file.Length == 0)
                return BadRequest(new { ok = false, error = Loc.T("ErrNoFileSelected") });
            if (!file.FileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
                return BadRequest(new { ok = false, error = Loc.T("CamFilesOnlyPdf") });

            await _files.EnsureClinicFoldersAsync(clinic);
            try
            {
                using var ms = new MemoryStream();
                await file.CopyToAsync(ms, HttpContext.RequestAborted);
                var stored = await _files.WriteAsync(clinic, CamFolder.Original,
                    Path.GetFileName(file.FileName), ms.ToArray(), overwrite: false, HttpContext.RequestAborted);
                return Json(new { ok = true, name = stored, size = file.Length });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Files.Upload failed for {File}", file.FileName);
                return StatusCode(500, new { ok = false, error = ex.Message });
            }
        }

        // ----- POST: delete (Original + Errors only) -----
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Delete(string folder, string name)
        {
            var clinic = await CurrentClinicAsync();
            if (clinic == null) return RedirectToAction("Index", "Home", new { area = "" });
            if (!TryParseFolder(folder, out var f)) return NotFound();
            if (f != CamFolder.Original && f != CamFolder.Errors) return Forbid();

            var safe = SafeName(name);
            if (safe == null)
            {
                TempData["ErrorMessage"] = Loc.T("ErrFileNameInvalid");
                return RedirectToAction(nameof(Index), new { folder = f });
            }

            try
            {
                if (!await _files.DeleteAsync(clinic, f, safe))
                {
                    TempData["ErrorMessage"] = string.Format(Loc.T("ErrFileNoLongerExists"), safe);
                    return RedirectToAction(nameof(Index), new { folder = f });
                }
                if (f == CamFolder.Errors)
                    await _files.DeleteAsync(clinic, f, safe + ReasonsSuffix);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Files.Delete failed for {File}", safe);
                TempData["ErrorMessage"] = string.Format(Loc.T("ErrDeleteFailed"), ex.Message);
                return RedirectToAction(nameof(Index), new { folder = f });
            }

            if (f == CamFolder.Original)
            {
                // Same rule as CheckPdfs.DeletePdf: no orphan override rows.
                var ov = await _db.ClinicPdfOverrides
                    .FirstOrDefaultAsync(o => o.ClinicId == clinic.Id && o.FileName == safe);
                if (ov != null)
                {
                    _db.ClinicPdfOverrides.Remove(ov);
                    await _db.SaveChangesAsync();
                }
            }

            TempData["SuccessMessage"] = string.Format(Loc.T("OkFileDeleted"), safe);
            return RedirectToAction(nameof(Index), new { folder = f });
        }

        // ----- POST: Errors → Original, so a fixed file can be retried without Explorer -----
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Restore(string name)
        {
            var clinic = await CurrentClinicAsync();
            if (clinic == null) return RedirectToAction("Index", "Home", new { area = "" });

            var safe = SafeName(name);
            if (safe == null)
            {
                TempData["ErrorMessage"] = Loc.T("ErrFileNameInvalid");
                return RedirectToAction(nameof(Index), new { folder = CamFolder.Errors });
            }

            try
            {
                var moved = await _files.MoveAsync(clinic, CamFolder.Errors, CamFolder.Original, safe);
                if (moved == null)
                {
                    TempData["ErrorMessage"] = string.Format(Loc.T("ErrFileNoLongerExists"), safe);
                    return RedirectToAction(nameof(Index), new { folder = CamFolder.Errors });
                }
                await _files.DeleteAsync(clinic, CamFolder.Errors, safe + ReasonsSuffix);
                TempData["SuccessMessage"] = string.Format(Loc.T("CamFilesRestoreDone"), moved);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Files.Restore failed for {File}", safe);
                TempData["ErrorMessage"] = ex.Message;
                return RedirectToAction(nameof(Index), new { folder = CamFolder.Errors });
            }

            return RedirectToAction(nameof(Index), new { folder = CamFolder.Original });
        }
    }
}
