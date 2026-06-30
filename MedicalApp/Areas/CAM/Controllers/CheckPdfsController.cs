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
        private readonly EmailDeliverabilityChecker _emailChecker;
        private readonly ILogger<CheckPdfsController> _logger;

        public CheckPdfsController(
            AppDbContext db,
            ICamFileStore files,
            CamPdfMetadataExtractor extractor,
            EmailDeliverabilityChecker emailChecker,
            ILogger<CheckPdfsController> logger)
        {
            _db = db;
            _files = files;
            _extractor = extractor;
            _emailChecker = emailChecker;
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
                            row.Reason = Loc.T("CamCheckRowReasonNoBlock");
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

            // ----------------------------------------------------------------
            // A2 — Email deliverability check.
            // For every row that already passed identity validation (gold path
            // [MedicalApp] block OR manual override), classify the email syntax
            // + DNS resolvability. Results are cached per-domain, so when many
            // PDFs go to the same gmail.com address only one DNS hop happens.
            // Parallelized with a small fan-out to keep the page snappy.
            // ----------------------------------------------------------------
            using (var ctsAll = CancellationTokenSource.CreateLinkedTokenSource(HttpContext.RequestAborted))
            {
                ctsAll.CancelAfter(TimeSpan.FromSeconds(6));
                var deliverabilityTasks = vm.Items
                    .Where(r => r.IsValid && !string.IsNullOrWhiteSpace(r.PatientEmail))
                    .Select(async r =>
                    {
                        try
                        {
                            var res = await _emailChecker.ValidateAsync(r.PatientEmail, ctsAll.Token);
                            r.EmailValidity = res.Validity;
                            r.EmailValidityMessage = res.FriendlyMessage;
                            r.EmailDomainSuggestion = res.DomainSuggestion;
                        }
                        catch (Exception ex)
                        {
                            // Never let a deliverability check break the page render.
                            _logger.LogDebug(ex, "EmailDeliverabilityChecker failed for {Email}", r.PatientEmail);
                            r.EmailValidity = EmailValidity.DnsUnknown;
                            r.EmailValidityMessage = Loc.T("CamEmailValidationDnsUnavailable");
                        }
                    })
                    .ToList();
                try { await Task.WhenAll(deliverabilityTasks); }
                catch (OperationCanceledException)
                {
                    // 6-second global cap reached — rows that didn't finish stay
                    // at their default (Empty / DnsUnknown). Better to show the
                    // page than block for slow DNS.
                }
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
                TempData["ErrorMessage"] = Loc.T("ErrAllFieldsRequired");
                return RedirectToAction(nameof(Index));
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
            // Stay on the edited row instead of jumping back to the top of
            // the list — picked up by the view's scroll-to-row script.
            TempData["ScrollToFile"] = fileName;
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
            TempData["SuccessMessage"] = Loc.T("OkOverrideCleared");
            // Keep the operator on the same row after the override is cleared.
            TempData["ScrollToFile"] = fileName;
            return RedirectToAction(nameof(Index));
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
                return RedirectToAction(nameof(Index));
            }

            // Defensive: refuse path traversal — accept only a bare file name.
            var safeName = Path.GetFileName(fileName);
            if (!string.Equals(safeName, fileName, StringComparison.Ordinal))
            {
                TempData["ErrorMessage"] = Loc.T("ErrFileNameInvalid");
                return RedirectToAction(nameof(Index));
            }

            var originalFolder = _files.GetOriginalFolder(clinic);
            var path = Path.Combine(originalFolder, safeName);

            if (!System.IO.File.Exists(path))
            {
                TempData["ErrorMessage"] = string.Format(Loc.T("ErrFileNoLongerExists"), safeName);
                return RedirectToAction(nameof(Index));
            }

            try
            {
                System.IO.File.Delete(path);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "DeletePdf: failed to delete {Path}", path);
                TempData["ErrorMessage"] = string.Format(Loc.T("ErrDeleteFailed"), ex.Message);
                return RedirectToAction(nameof(Index));
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
            TempData["SuccessMessage"] = Loc.T("OkIgnoredDomainsSaved");
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
                TempData["ErrorMessage"] = Loc.T("ErrNoFileSelected");
                return RedirectToAction(nameof(Index));
            }

            var originalFolder = _files.GetOriginalFolder(clinic);
            if (!Directory.Exists(originalFolder))
            {
                TempData["ErrorMessage"] = Loc.T("ErrOriginalFolderMissing");
                return RedirectToAction(nameof(Index));
            }

            int copied = 0, skipped = 0, rejected = 0;
            string? firstUploadedName = null;
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
                    baseName = $"{stem}_{DateTime.Now:yyyyMMdd_HHmmss}{ext}";
                    dest = Path.Combine(originalFolder, baseName);
                }

                try
                {
                    using var stream = new FileStream(dest, FileMode.CreateNew, FileAccess.Write);
                    await f.CopyToAsync(stream);
                    copied++;
                    // Remember the FIRST file successfully copied — the view
                    // will scroll the operator directly to its row so they
                    // don't have to manually find it in long batches.
                    if (firstUploadedName == null) firstUploadedName = baseName;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "UploadFiles: failed to write {Dest}", dest);
                    skipped++;
                }
            }

            TempData["SuccessMessage"] =
                string.Format(Loc.T("CamUploadDoneMain"), copied) +
                (rejected > 0 ? string.Format(Loc.T("CamUploadRejectedSuffix"), rejected) : "") +
                (skipped > 0 ? string.Format(Loc.T("CamUploadSkippedSuffix"), skipped) : "") + ".";
            if (firstUploadedName != null) TempData["ScrollToFile"] = firstUploadedName;
            return RedirectToAction(nameof(Index));
        }
    }
}
