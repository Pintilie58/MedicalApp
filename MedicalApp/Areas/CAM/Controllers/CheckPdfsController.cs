using MedicalApp.Data;
using MedicalApp.Models;
using MedicalApp.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MedicalApp.Areas.CAM.Controllers
{
    /// <summary>
    /// POST actions of the "Original" workbench (override save/clear, delete,
    /// blacklist, legacy multi-file upload). The table itself is built by
    /// <see cref="CamCheckPdfsBuilder"/> and rendered in /CAM/Files → "De trimis";
    /// GET Index only redirects there. La lansare lot, BatchService preferă
    /// override-ul dacă există.
    /// </summary>
    [Area("CAM")]
    public class CheckPdfsController : Controller
    {
        private readonly AppDbContext _db;
        private readonly ICamFileStore _files;
        private readonly ILogger<CheckPdfsController> _logger;

        public CheckPdfsController(
            AppDbContext db,
            ICamFileStore files,
            ILogger<CheckPdfsController> logger)
        {
            _db = db;
            _files = files;
            _logger = logger;
        }

        private string? CurrentEmail => HttpContext.Session.GetString("UserEmail");

        /// <summary>
        /// The workbench now lives in /CAM/Files → "De trimis" (single page for
        /// the operator). Old bookmarks and links keep working through this redirect.
        /// </summary>
        [HttpGet]
        public IActionResult Index() => BackToWorkbench();

        private IActionResult BackToWorkbench() =>
            RedirectToAction("Index", "Files", new { area = "CAM", folder = "Original" });

        // ----- POST: salvează un override manual nume+email per PDF -----
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SaveOverride(string fileName, string overrideName, string overrideEmail)
        {
            if (string.IsNullOrEmpty(CurrentEmail))
                return RedirectToAction("Index", "Home", new { area = "" });

            var clinic = await _db.Clinics.FirstOrDefaultAsync(c => c.UserEmail == CurrentEmail);
            if (clinic == null) return RedirectToAction("Index", "Dashboard", new { area = "CAM" });

            if (string.IsNullOrWhiteSpace(fileName) ||
                string.IsNullOrWhiteSpace(overrideName) ||
                string.IsNullOrWhiteSpace(overrideEmail))
            {
                TempData["ErrorMessage"] = Loc.T("ErrAllFieldsRequired");
                return BackToWorkbench();
            }

            // RFC-syntactic check via System.Net.Mail.MailAddress. We don't run
            // the DNS check here because the view's Index() does it on render —
            // doing it again here would just add latency without giving the
            // operator any new information. If the domain is broken the row
            // will be flagged red on next page load and Run-batch stays disabled.
            try { _ = new System.Net.Mail.MailAddress(overrideEmail.Trim()); }
            catch
            {
                TempData["ErrorMessage"] = Loc.T("ErrEmailLooksInvalid");
                return BackToWorkbench();
            }

            var existing = await _db.ClinicPdfOverrides
                .FirstOrDefaultAsync(o => o.ClinicId == clinic.Id && o.FileName == fileName);
            if (existing == null)
            {
                _db.ClinicPdfOverrides.Add(new ClinicPdfOverride
                {
                    ClinicId = clinic.Id,
                    FileName = fileName,
                    OverrideName = overrideName.Trim(),
                    OverrideEmail = overrideEmail.Trim(),
                    CreatedAt = DateTime.UtcNow
                });
            }
            else
            {
                existing.OverrideName = overrideName.Trim();
                existing.OverrideEmail = overrideEmail.Trim();
                existing.CreatedAt = DateTime.UtcNow;
            }
            await _db.SaveChangesAsync();
            TempData["SuccessMessage"] = $"Override salvat pentru {fileName}.";
            // Stay on the edited row instead of jumping back to the top of
            // the list — picked up by the view's scroll-to-row script.
            TempData["ScrollToFile"] = fileName;
            return BackToWorkbench();
        }

        // ----- POST: clear override (revine la auto-extracție) -----
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ClearOverride(string fileName)
        {
            if (string.IsNullOrEmpty(CurrentEmail))
                return RedirectToAction("Index", "Home", new { area = "" });

            var clinic = await _db.Clinics.FirstOrDefaultAsync(c => c.UserEmail == CurrentEmail);
            if (clinic == null) return RedirectToAction("Index", "Dashboard", new { area = "CAM" });

            var ov = await _db.ClinicPdfOverrides
                .FirstOrDefaultAsync(o => o.ClinicId == clinic.Id && o.FileName == fileName);
            if (ov != null)
            {
                _db.ClinicPdfOverrides.Remove(ov);
                await _db.SaveChangesAsync();
            }
            TempData["SuccessMessage"] = Loc.T("OkOverrideCleared");
            // Keep the operator on the same row after the override is cleared.
            TempData["ScrollToFile"] = fileName;
            return BackToWorkbench();
        }

        // ----- POST: șterge un PDF din folderul Original + override-ul asociat -----
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeletePdf(string fileName)
        {
            if (string.IsNullOrEmpty(CurrentEmail))
                return RedirectToAction("Index", "Home", new { area = "" });

            var clinic = await _db.Clinics.FirstOrDefaultAsync(c => c.UserEmail == CurrentEmail);
            if (clinic == null) return RedirectToAction("Index", "Dashboard", new { area = "CAM" });

            if (string.IsNullOrWhiteSpace(fileName))
            {
                TempData["ErrorMessage"] = Loc.T("ErrFileNameMissing");
                return BackToWorkbench();
            }

            // Defensive: refuse path traversal — accept only a bare file name.
            var safeName = Path.GetFileName(fileName);
            if (!string.Equals(safeName, fileName, StringComparison.Ordinal))
            {
                TempData["ErrorMessage"] = Loc.T("ErrFileNameInvalid");
                return BackToWorkbench();
            }

            if (!await _files.ExistsAsync(clinic, CamFolder.Original, safeName))
            {
                TempData["ErrorMessage"] = string.Format(Loc.T("ErrFileNoLongerExists"), safeName);
                return BackToWorkbench();
            }

            try
            {
                await _files.DeleteAsync(clinic, CamFolder.Original, safeName);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "DeletePdf: failed to delete {File}", safeName);
                TempData["ErrorMessage"] = string.Format(Loc.T("ErrDeleteFailed"), ex.Message);
                return BackToWorkbench();
            }

            // Drop any override row tied to this file name (no orphans).
            var ov = await _db.ClinicPdfOverrides
                .FirstOrDefaultAsync(o => o.ClinicId == clinic.Id && o.FileName == safeName);
            if (ov != null)
            {
                _db.ClinicPdfOverrides.Remove(ov);
                await _db.SaveChangesAsync();
            }

            TempData["SuccessMessage"] = string.Format(Loc.T("OkFileDeleted"), safeName);
            return BackToWorkbench();
        }

        // ----- POST: salvează blacklist-ul de domenii al clinicii -----
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SaveBlacklist(string emailDomainBlacklist)
        {
            if (string.IsNullOrEmpty(CurrentEmail))
                return RedirectToAction("Index", "Home", new { area = "" });

            var clinic = await _db.Clinics.FirstOrDefaultAsync(c => c.UserEmail == CurrentEmail);
            if (clinic == null) return RedirectToAction("Index", "Dashboard", new { area = "CAM" });

            clinic.EmailDomainBlacklist = (emailDomainBlacklist ?? string.Empty).Trim();
            if (clinic.EmailDomainBlacklist.Length > 500)
                clinic.EmailDomainBlacklist = clinic.EmailDomainBlacklist[..500];
            await _db.SaveChangesAsync();
            TempData["SuccessMessage"] = Loc.T("OkIgnoredDomainsSaved");
            return BackToWorkbench();
        }

        // ----- POST: upload manual PDF-uri din alt loc de pe disk (copiere) -----
        [HttpPost]
        [ValidateAntiForgeryToken]
        [RequestSizeLimit(200_000_000)] // 200 MB total per request
        public async Task<IActionResult> UploadFiles(List<IFormFile> files)
        {
            if (string.IsNullOrEmpty(CurrentEmail))
                return RedirectToAction("Index", "Home", new { area = "" });

            var clinic = await _db.Clinics.FirstOrDefaultAsync(c => c.UserEmail == CurrentEmail);
            if (clinic == null) return RedirectToAction("Index", "Dashboard", new { area = "CAM" });

            if (files == null || files.Count == 0)
            {
                TempData["ErrorMessage"] = Loc.T("ErrNoFileSelected");
                return BackToWorkbench();
            }

            // Makes sure the destination exists (folders on disk, container in
            // the cloud) instead of refusing the upload.
            await _files.EnsureClinicFoldersAsync(clinic);

            int copied = 0, skipped = 0, rejected = 0;
            string? firstUploadedName = null;
            foreach (var f in files)
            {
                if (f == null || f.Length == 0) { skipped++; continue; }
                if (!f.FileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) { rejected++; continue; }

                // Sanitize file name (drop path components, keep base + ext).
                var baseName = Path.GetFileName(f.FileName);

                try
                {
                    using var ms = new MemoryStream();
                    await f.CopyToAsync(ms);
                    // The store disambiguates instead of overwriting an existing file.
                    baseName = await _files.WriteAsync(clinic, CamFolder.Original, baseName,
                        ms.ToArray());
                    copied++;
                    // Remember the FIRST file successfully copied — the view
                    // will scroll the operator directly to its row so they
                    // don't have to manually find it in long batches.
                    if (firstUploadedName == null) firstUploadedName = baseName;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "UploadFiles: failed to write {Dest}", baseName);
                    skipped++;
                }
            }

            TempData["SuccessMessage"] =
                string.Format(Loc.T("CamUploadDoneMain"), copied) +
                (rejected > 0 ? string.Format(Loc.T("CamUploadRejectedSuffix"), rejected) : "") +
                (skipped > 0 ? string.Format(Loc.T("CamUploadSkippedSuffix"), skipped) : "") + ".";
            if (firstUploadedName != null) TempData["ScrollToFile"] = firstUploadedName;
            return BackToWorkbench();
        }
    }
}
