using MedicalApp.Data;
using MedicalApp.Models;
using MedicalApp.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MedicalApp.Areas.CAM.Controllers
{
    /// <summary>
    /// Pagina /CAM/CheckPdfs — Strategia C (safety net manual).
    /// Operatorul vede pentru fiecare PDF din folderul <c>Original</c>:
    ///   * ce extrage automat extractor-ul (Strategy 0 [MedicalApp] → fallback),
    ///   * statusul (verde gold path / galben fallback / roșu invalid),
    ///   * butoane: Edit (override manual), Upload (copiere fișiere noi din alt loc).
    /// La lansare lot, BatchService preferă override-ul dacă există.
    /// </summary>
    [Area("CAM")]
    public class CheckPdfsController : Controller
    {
        private readonly AppDbContext _db;
        private readonly ICamFileStore _files;
        private readonly CamPdfMetadataExtractor _extractor;
        private readonly ILogger<CheckPdfsController> _logger;

        public CheckPdfsController(
            AppDbContext db,
            ICamFileStore files,
            CamPdfMetadataExtractor extractor,
            ILogger<CheckPdfsController> logger)
        {
            _db = db;
            _files = files;
            _extractor = extractor;
            _logger = logger;
        }

        private string? CurrentEmail => HttpContext.Session.GetString("UserEmail");

        [HttpGet]
        public async Task<IActionResult> Index()
        {
            if (string.IsNullOrEmpty(CurrentEmail))
                return RedirectToAction("Index", "Home", new { area = "" });

            var clinic = await _db.Clinics.AsNoTracking()
                .FirstOrDefaultAsync(c => c.UserEmail == CurrentEmail);
            if (clinic == null)
                return RedirectToAction("Index", "Dashboard", new { area = "CAM" });

            var vm = new Models.CamCheckPdfsViewModel
            {
                ClinicName = clinic.Name,
                OriginalFolder = _files.GetOriginalFolder(clinic)
            };

            if (!Directory.Exists(vm.OriginalFolder))
            {
                vm.FolderMissing = true;
                return View(vm);
            }

            var pdfs = Directory.GetFiles(vm.OriginalFolder, "*.pdf", SearchOption.TopDirectoryOnly)
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList();

            // Preload all overrides for this clinic in ONE query.
            var fileNames = pdfs
                .Select(Path.GetFileName)
                .Where(n => !string.IsNullOrEmpty(n))
                .Select(n => n!)
                .ToList();
            var overrides = await _db.ClinicPdfOverrides.AsNoTracking()
                .Where(o => o.ClinicId == clinic.Id && fileNames.Contains(o.FileName))
                .ToDictionaryAsync(o => o.FileName, o => o);

            foreach (var path in pdfs)
            {
                var row = new Models.CamCheckPdfsViewModel.Row
                {
                    FileName = Path.GetFileName(path),
                    SizeKb = (int)Math.Round(new FileInfo(path).Length / 1024.0)
                };

                // Check for an operator override FIRST.
                if (overrides.TryGetValue(row.FileName, out var ov))
                {
                    row.PatientName = ov.OverrideName;
                    row.PatientEmail = ov.OverrideEmail;
                    row.IsValid = true;
                    row.IsManualOverride = true;
                }
                else
                {
                    try
                    {
                        var bytes = await System.IO.File.ReadAllBytesAsync(path);
                        // No domain blacklist anymore — per user's Feb 2026 decision,
                        // identification relies on either the [MedicalApp] block
                        // (gold path) or Gemini's PatientInfo from the actual batch
                        // run. CheckPdfs preview just hints at what auto-extractor
                        // would see without Gemini help.
                        var meta = _extractor.Extract(bytes, row.FileName, clinicDomainBlacklist: null);

                        // ----------------------------------------------------------
                        // Reality check: in CAM we ONLY trust 3 sources of patient
                        // identity — operator override, explicit [MedicalApp] block,
                        // or Gemini's PatientInfo at batch time. Heuristic name +
                        // first-email guesses (label scan / near-email / capitalized
                        // line) are NOT reliable — they often pick up the document
                        // title ("ANALIZE MEDICALE") or the clinic header email.
                        // So in preview we DELIBERATELY hide those guesses and
                        // tell the operator "AI will resolve this at batch time".
                        // ----------------------------------------------------------
                        if (meta.MatchedExplicitBlock && meta.IsValid)
                        {
                            row.PatientName = meta.PatientName;
                            row.PatientEmail = meta.PatientEmail;
                            row.IsValid = true;
                            row.MatchedExplicitBlock = true;
                        }
                        else if (!meta.IsMedicalLabReport)
                        {
                            // Sanity check failed — this PDF is not a lab report at all.
                            row.IsValid = false;
                            row.Reason = meta.Reason ?? "PDF does not look like a medical lab report.";
                        }
                        else
                        {
                            // No [MedicalApp] block → operator must use "Editează"
                            // to set a manual override. AI fallback is disabled by policy.
                            row.IsValid = false;
                            row.Reason = "PDF fără bloc [MedicalApp]. Apasă „Editează" pentru a introduce manual numele și emailul.";
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "CheckPdfs: failed reading {File}", path);
                        row.IsValid = false;
                        row.Reason = "I/O error: " + ex.Message;
                    }
                }
                vm.Items.Add(row);
            }

            return View(vm);
        }

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
                TempData["ErrorMessage"] = "Toate câmpurile sunt obligatorii.";
                return RedirectToAction(nameof(Index));
            }

            // Trivial sanity check — extractor will validate again at batch time.
            if (!overrideEmail.Contains('@') || !overrideEmail.Contains('.'))
            {
                TempData["ErrorMessage"] = "Adresa de email pare invalidă.";
                return RedirectToAction(nameof(Index));
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
            return RedirectToAction(nameof(Index));
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
            TempData["SuccessMessage"] = "Override șters. PDF-ul va fi re-analizat automat.";
            return RedirectToAction(nameof(Index));
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
            TempData["SuccessMessage"] = "Lista de domenii ignorate a fost salvată.";
            return RedirectToAction(nameof(Index));
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
                TempData["ErrorMessage"] = "Niciun fișier selectat.";
                return RedirectToAction(nameof(Index));
            }

            var originalFolder = _files.GetOriginalFolder(clinic);
            if (!Directory.Exists(originalFolder))
            {
                TempData["ErrorMessage"] = "Folderul Original nu există încă. Cumpără primul pachet de credite.";
                return RedirectToAction(nameof(Index));
            }

            int copied = 0, skipped = 0, rejected = 0;
            foreach (var f in files)
            {
                if (f == null || f.Length == 0) { skipped++; continue; }
                if (!f.FileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) { rejected++; continue; }

                // Sanitize file name (drop path components, keep base + ext).
                var baseName = Path.GetFileName(f.FileName);
                var dest = Path.Combine(originalFolder, baseName);
                if (System.IO.File.Exists(dest))
                {
                    // Disambiguate to avoid silently overwriting an existing file.
                    var stem = Path.GetFileNameWithoutExtension(baseName);
                    var ext = Path.GetExtension(baseName);
                    dest = Path.Combine(originalFolder, $"{stem}_{DateTime.Now:yyyyMMdd_HHmmss}{ext}");
                }

                try
                {
                    using var stream = new FileStream(dest, FileMode.CreateNew, FileAccess.Write);
                    await f.CopyToAsync(stream);
                    copied++;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "UploadFiles: failed to write {Dest}", dest);
                    skipped++;
                }
            }

            TempData["SuccessMessage"] =
                $"Upload finalizat: {copied} copiate" +
                (rejected > 0 ? $", {rejected} respinse (nu sunt PDF)" : "") +
                (skipped > 0 ? $", {skipped} sărite (erori I/O)" : "") + ".";
            return RedirectToAction(nameof(Index));
        }
    }
}
