using MedicalApp.Data;
using MedicalApp.Models;
using MedicalApp.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace MedicalApp.Controllers
{
    /// <summary>
    /// Manages the current user's health profiles (Eu, Mama, Tata, etc.).
    /// All actions require an authenticated user (session "UserEmail" set).
    /// </summary>
    public class ProfilesController : Controller
    {
        private readonly AppDbContext _db;
        private readonly PdfReportGenerator _pdfGenerator;
        private readonly EvolutionPdfGenerator _evolutionPdf;
        private readonly ProfileComparePdfGenerator _comparePdf;
        private readonly MedicalDossierPdfGenerator _dossierPdf;
        private readonly ArchiveAccessService _archiveAccess;
        private readonly IEmailService _emailService;
        private readonly ILogger<ProfilesController> _logger;

        public ProfilesController(
            AppDbContext db,
            PdfReportGenerator pdfGenerator,
            EvolutionPdfGenerator evolutionPdf,
            ProfileComparePdfGenerator comparePdf,
            MedicalDossierPdfGenerator dossierPdf,
            ArchiveAccessService archiveAccess,
            IEmailService emailService,
            ILogger<ProfilesController> logger)
        {
            _db = db;
            _pdfGenerator = pdfGenerator;
            _evolutionPdf = evolutionPdf;
            _comparePdf = comparePdf;
            _dossierPdf = dossierPdf;
            _archiveAccess = archiveAccess;
            _emailService = emailService;
            _logger = logger;
        }

        private string? CurrentEmail => HttpContext.Session.GetString("UserEmail");

        // ====================================================================
        // LIST
        // ====================================================================
        [HttpGet]
        public async Task<IActionResult> Index()
        {
            if (string.IsNullOrEmpty(CurrentEmail))
                return RedirectToAction("Index", "Home");

            var profiles = await _db.Profiles
                .AsNoTracking()
                .Where(p => p.UserEmail == CurrentEmail)
                .OrderByDescending(p => p.IsDefault)
                .ThenBy(p => p.Name)
                .ToListAsync();

            // Interpretation counts per profile (successful ones only).
            var profileIds = profiles.Select(p => p.Id).ToList();
            var counts = await _db.InterpretationHistories
                .AsNoTracking()
                .Where(h => h.ProfileId.HasValue
                            && profileIds.Contains(h.ProfileId.Value)
                            && h.Status == "success")
                .GroupBy(h => h.ProfileId!.Value)
                .Select(g => new { ProfileId = g.Key, Count = g.Count() })
                .ToDictionaryAsync(x => x.ProfileId, x => x.Count);

            var vm = new ProfilesIndexViewModel
            {
                Profiles = profiles.Select(p => new ProfilesIndexViewModel.ProfileRow
                {
                    Id = p.Id,
                    Name = p.Name,
                    Relationship = p.Relationship,
                    Gender = p.Gender,
                    BirthYear = p.BirthYear,
                    Notes = p.Notes,
                    IsDefault = p.IsDefault,
                    CreatedAt = p.CreatedAt,
                    InterpretationsCount = counts.TryGetValue(p.Id, out var c) ? c : 0
                }).ToList()
            };

            // "+ Profil nou" gate (Feb 2026 anti-abuse). B2C users must have at
            // least 1 PAID credit to add extra family profiles; bonus credits
            // don't count. See ProfileGateService for the full rationale.
            var user = await _db.Users.AsNoTracking()
                .FirstOrDefaultAsync(u => u.Email == CurrentEmail);
            ViewBag.CanCreateProfile =
                ProfileGateService.CanCreateAdditionalProfile(user, profiles.Count);

            return View(vm);
        }

        // ====================================================================
        // HISTORY (archive) - list interpretations for a specific profile
        // ====================================================================
        [HttpGet]
        public async Task<IActionResult> History(int id)
        {
            if (string.IsNullOrEmpty(CurrentEmail))
                return RedirectToAction("Index", "Home");

            var profile = await _db.Profiles.AsNoTracking()
                .FirstOrDefaultAsync(p => p.Id == id && p.UserEmail == CurrentEmail);
            if (profile == null)
            {
                TempData["ErrorMessage"] = Loc.T("ErrProfileNotFound");
                return RedirectToAction(nameof(Index));
            }

            var rows = await _db.InterpretationHistories
                .AsNoTracking()
                .Where(h => h.UserEmail == CurrentEmail
                            && h.ProfileId == profile.Id
                            && h.Status == "success")
                .OrderByDescending(h => h.CreatedAt)
                .Select(h => new
                {
                    h.Id,
                    h.CreatedAt,
                    h.OriginalFileName,
                    h.Language,
                    h.RawJsonResult
                })
                .ToListAsync();

            var items = new List<ProfileHistoryViewModel.HistoryRow>(rows.Count);
            foreach (var r in rows)
            {
                var row = new ProfileHistoryViewModel.HistoryRow
                {
                    Id = r.Id,
                    CreatedAt = r.CreatedAt,
                    OriginalFileName = r.OriginalFileName,
                    Language = r.Language,
                    HasRawJson = !string.IsNullOrWhiteSpace(r.RawJsonResult)
                };

                // Lightweight parse only to show counts in the table - never block the page if parsing fails.
                if (row.HasRawJson)
                {
                    try
                    {
                        var parsed = JsonSerializer.Deserialize<InterpretationResult>(r.RawJsonResult!,
                            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                        row.KeyResultsCount = parsed?.KeyResults?.Count;
                        row.AbnormalFindingsCount = parsed?.AbnormalFindings?.Count;
                        row.PatientName = parsed?.PatientInfo?.Name;
                        row.DateTaken = parsed?.PatientInfo?.DateTaken;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Could not parse stored RawJsonResult for history id={Id}", r.Id);
                    }
                }

                items.Add(row);
            }

            // Sort by patient's sampling date (newest sampling first), with a tolerant
            // parser - falls back to CreatedAt when DateTaken is missing or unparsable.
            items = items
                .OrderByDescending(r => ParseSamplingDate(r.DateTaken) ?? r.CreatedAt)
                .ThenByDescending(r => r.CreatedAt)
                .ToList();

            var vm = new ProfileHistoryViewModel
            {
                ProfileId = profile.Id,
                ProfileName = profile.Name,
                Relationship = profile.Relationship,
                Items = items
            };

            // Fetch the user to know their free-period state (for the UI hint only;
            // nothing is charged on this page).
            var user = await _db.Users.AsNoTracking()
                .FirstOrDefaultAsync(u => u.Email == CurrentEmail);
            if (user != null)
            {
                vm.IsInFreePeriod = ArchiveAccessService.IsInFreePeriod(user);
                vm.FreeUntil = user.FreeArchiveUntil ?? user.DataC.Add(ArchiveAccessService.FreePeriod);
                vm.FreeUsesLeftInBundle = ArchiveAccessService.FreeUsesLeftInBundle(user);
                vm.IsFreemium = user.Credite == 0;
            }

            return View(vm);
        }

        // ====================================================================
        // VIEW REPORT ON SCREEN — the in-app twin of the PDF. For freemium users
        // it is the DEMO report (server-side redacted + unlock CTAs) and keeps
        // them inside the app, one click from the paywall. For paying users it
        // renders the COMPLETE report with no CTAs — they already paid.
        // ====================================================================
        [HttpGet]
        public async Task<IActionResult> ViewReport(int id)
        {
            if (string.IsNullOrEmpty(CurrentEmail))
                return RedirectToAction("Index", "Home");

            var paidCredits = await _db.Users.AsNoTracking()
                .Where(u => u.Email == CurrentEmail)
                .Select(u => u.Credite)
                .FirstOrDefaultAsync();
            bool isFreemium = paidCredits == 0;

            var history = await _db.InterpretationHistories.AsNoTracking()
                .FirstOrDefaultAsync(h => h.Id == id && h.UserEmail == CurrentEmail);

            if (history == null || string.IsNullOrWhiteSpace(history.RawJsonResult))
            {
                TempData["ErrorMessage"] = Loc.T("ErrReportCannotBeReconstructed");
                return RedirectToAction(nameof(Index));
            }

            InterpretationResult? result;
            try
            {
                result = JsonSerializer.Deserialize<InterpretationResult>(history.RawJsonResult!,
                    new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true,
                        AllowTrailingCommas = true,
                        ReadCommentHandling = JsonCommentHandling.Skip
                    });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ViewReport: failed to deserialize RawJsonResult for history id={Id}", id);
                TempData["ErrorMessage"] = Loc.T("ErrReportCannotBeReconstructed");
                return RedirectToAction(nameof(Index));
            }

            if (result == null)
            {
                TempData["ErrorMessage"] = Loc.T("ErrReportCannotBeReconstructed");
                return RedirectToAction(nameof(Index));
            }

            var profileName = history.ProfileId.HasValue
                ? await _db.Profiles.AsNoTracking()
                    .Where(p => p.Id == history.ProfileId.Value && p.UserEmail == CurrentEmail)
                    .Select(p => p.Name)
                    .FirstOrDefaultAsync()
                : null;

            return View(BuildReportScreen(history, result, profileName, isFreemium));
        }

        /// <summary>
        /// Maps the stored interpretation onto the on-screen ViewModel, dropping the
        /// text of every redacted item (see ReportScreenViewModel security note).
        /// Nothing is redacted when <paramref name="isFreemium"/> is false.
        /// </summary>
        private static ReportScreenViewModel BuildReportScreen(
            InterpretationHistory history, InterpretationResult r, string? profileName,
            bool isFreemium = true)
        {
            var vm = new ReportScreenViewModel
            {
                HistoryId = history.Id,
                ProfileId = history.ProfileId,
                ProfileName = profileName,
                CreatedAt = history.CreatedAt,
                PatientInfo = r.PatientInfo,
                IsFreemium = isFreemium,
                Summary = r.Summary,
                Disclaimer = r.Disclaimer
            };

            int locked = 0;

            List<ReportScreenViewModel.LockableText> MapTexts(IEnumerable<string>? items)
            {
                var list = new List<ReportScreenViewModel.LockableText>();
                if (items == null) return list;
                int i = 0;
                foreach (var raw in items.Where(x => !string.IsNullOrWhiteSpace(x)))
                {
                    bool isLocked = isFreemium && PdfReportGenerator.IsRedactedAt(i++);
                    if (isLocked) locked++;
                    list.Add(new ReportScreenViewModel.LockableText
                    {
                        Locked = isLocked,
                        Text = isLocked ? null : raw
                    });
                }
                return list;
            }

            vm.RiskFactors = MapTexts(r.RiskFactors);
            vm.DoctorQuestions = MapTexts(r.DoctorQuestions);
            vm.Correlations = MapTexts(SplitSentences(r.Correlations));
            vm.Recommendations = MapTexts(SplitSentences(r.Recommendations));

            if (r.KeyResults != null)
            {
                for (int i = 0; i < r.KeyResults.Count; i++)
                {
                    var k = r.KeyResults[i];
                    bool isLocked = isFreemium && PdfReportGenerator.IsRedactedAt(i);
                    if (isLocked) locked++;
                    vm.KeyResults.Add(new ReportScreenViewModel.LockableRow
                    {
                        Locked = isLocked,
                        // Panel headers are lab metadata (not PHI, no interpretive
                        // value) — kept visible so the report keeps its structure.
                        PanelHeader = string.IsNullOrWhiteSpace(k.PanelHeaderRaw) ? null : k.PanelHeaderRaw!.Trim(),
                        Parameter = isLocked ? null : k.Parameter,
                        Value = isLocked ? null : k.Value,
                        Unit = isLocked ? null : k.Unit,
                        ReferenceRange = isLocked ? null : k.ReferenceRange,
                        Status = isLocked ? null : k.Status,
                        Explanation = isLocked ? null : k.Explanation,
                        AnalyteLine = isLocked || string.IsNullOrWhiteSpace(k.AnalyteLineRaw)
                            ? null : k.AnalyteLineRaw!.Trim(),
                        LoincCode = isLocked ? null : k.LoincCode,
                        LoincLongName = isLocked ? null : k.LoincLongName,
                        LoincSource = isLocked ? null : k.LoincSource,
                        LoincScore = isLocked ? null : k.LoincScore
                    });
                }

                vm.TotalResultsCount = r.KeyResults.Count;
                vm.VisibleResultsCount = vm.KeyResults.Count(x => !x.Locked);
                vm.HasLoincCodes = vm.KeyResults.Any(x => !string.IsNullOrWhiteSpace(x.LoincCode));
            }

            if (r.AbnormalFindings != null)
            {
                for (int i = 0; i < r.AbnormalFindings.Count; i++)
                {
                    var f = r.AbnormalFindings[i];
                    bool isLocked = isFreemium && PdfReportGenerator.IsRedactedAt(i);
                    if (isLocked) locked++;
                    // The finding is colored by the VALUE of the analyte it refers
                    // to (red = high, blue = low, mustard = borderline), so the
                    // section reads the same way as the results table.
                    var src = isLocked ? null : AbnormalFindingsCompleter.FindKeyResult(r, f.Parameter);
                    vm.AbnormalFindings.Add(new ReportScreenViewModel.LockableFinding
                    {
                        Locked = isLocked,
                        Parameter = isLocked ? null : f.Parameter,
                        Explanation = isLocked ? null : f.Explanation,
                        Severity = isLocked ? null : f.Severity,
                        Status = src?.Status,
                        Value = src?.Value,
                        Unit = src?.Unit
                    });
                }
            }

            vm.LockedCount = locked;
            return vm;
        }

        /// <summary>Splits a paragraph into sentences so they can be redacted one by
        /// one — the same intercalated pattern the PDF uses for free text.</summary>
        private static List<string> SplitSentences(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return new List<string>();
            var parts = System.Text.RegularExpressions.Regex
                .Split(text, @"(?<=[\.\!\?])\s+")
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .ToList();
            return parts.Count == 0 ? new List<string> { text } : parts;
        }

        // ====================================================================
        // DOWNLOAD REPORT - regenerate PDF from stored JSON on the fly
        // ====================================================================
        [HttpGet]
        public async Task<IActionResult> DownloadReport(int id)
        {
            if (string.IsNullOrEmpty(CurrentEmail))
                return RedirectToAction("Index", "Home");

            var (pdfBytes, fileName, errorResult) = await TryRegenerateReportPdfAsync(id);
            if (errorResult != null) return errorResult;

            // `File(bytes, contentType, fileDownloadName)` sets
            // Content-Disposition: attachment — the browser saves the PDF to
            // the Downloads folder instead of trying to hand it off to
            // Adobe Acrobat / Reader (per user request Feb 2026: users who
            // don't have a PDF viewer installed were left stuck otherwise).
            return File(pdfBytes!, "application/pdf", fileName!);
        }

        // ====================================================================
        // EMAIL REPORT - regenerate PDF and email it back to the current user
        // as an alternative to downloading. Introduced Feb 2026 alongside the
        // "Duplicate detected" page where the single "Open existing report"
        // button was split into two: "Download" (DownloadReport) and "Send
        // via email" (this action).
        // ====================================================================
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> EmailReport(int id, int? profileId = null)
        {
            if (string.IsNullOrEmpty(CurrentEmail))
                return RedirectToAction("Index", "Home");

            var (pdfBytes, fileName, errorResult) = await TryRegenerateReportPdfAsync(id);
            if (errorResult != null) return errorResult;

            // Capture UI culture up-front so awaited operations further down
            // can't drift the language of the email (same pattern used by
            // CompareExport at line ~474).
            var lang = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
            var subject = Loc.T("ResultEmailSubject", lang);
            var greeting = Loc.T("EmailGreeting", lang);
            var intro = Loc.T("ResultEmailIntro", lang);
            var attached = Loc.T("ResultEmailAttachedNote", lang);
            var tagline = Loc.T("Tagline", lang);
            var regards = Loc.T("EmailRegards", lang);
            var htmlBody = $@"
<div style='font-family: Arial, sans-serif; max-width: 600px; margin: 0 auto; padding: 20px;'>
    <h2 style='color: #0d47a1;'>MyMedicalApp.NET</h2>
    <p>{greeting}</p>
    <p>{intro}</p>
    <p style='color: #6c757d; font-size: 0.9em;'>{attached}</p>
    <p style='font-style: italic; color: #0d47a1;'>{tagline}</p>
    <hr style='border: none; border-top: 1px solid #dee2e6; margin: 20px 0;' />
    <p style='color: #6c757d; font-size: 0.9em;'>{regards}</p>
    <p style='color: #0d47a1; font-weight: bold;'>www.mymedicalapp.net</p>
</div>";

            try
            {
                await _emailService.SendEmailWithAttachmentAsync(
                    CurrentEmail, subject, htmlBody, pdfBytes!, fileName!);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "EmailReport: failed to send report id={Id} to {Email}", id, CurrentEmail);
                TempData["ErrorMessage"] = Loc.T("EmailSendFailedTryDownload", lang);
                // Fallback: the user was on DuplicateDetected, so bounce back
                // to Interpretation/Upload where they can retry.
                return profileId.HasValue
                    ? RedirectToAction(nameof(History), new { id = profileId.Value })
                    : RedirectToAction("Upload", "Interpretation");
            }

            TempData["SuccessMessage"] = string.Format(Loc.T("DupEmailSentFmt", lang), CurrentEmail);
            return profileId.HasValue
                ? RedirectToAction(nameof(History), new { id = profileId.Value })
                : RedirectToAction("Upload", "Interpretation");
        }

        /// <summary>
        /// Shared helper used by <see cref="DownloadReport"/> and
        /// <see cref="EmailReport"/>: regenerates the branded PDF from the
        /// stored <c>RawJsonResult</c> and returns the bytes + filename.
        /// Returns an <see cref="IActionResult"/> in <c>errorResult</c> when
        /// the caller must short-circuit (missing history, deserialize fail,
        /// PDF regeneration fail); in that case <c>pdfBytes</c>/<c>fileName</c>
        /// are null and the redirect target is already prepared with a
        /// TempData error message.
        /// </summary>
        private async Task<(byte[]? pdfBytes, string? fileName, IActionResult? errorResult)>
            TryRegenerateReportPdfAsync(int id)
        {
            var history = await _db.InterpretationHistories
                .AsNoTracking()
                .FirstOrDefaultAsync(h => h.Id == id
                                          && h.UserEmail == CurrentEmail
                                          && h.Status == "success");
            if (history == null || string.IsNullOrWhiteSpace(history.RawJsonResult))
            {
                TempData["ErrorMessage"] = Loc.T("ErrReportNotFound");
                return (null, null, RedirectToAction(nameof(Index)));
            }

            InterpretationResult? result;
            try
            {
                result = JsonSerializer.Deserialize<InterpretationResult>(history.RawJsonResult,
                    new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true,
                        AllowTrailingCommas = true,
                        ReadCommentHandling = JsonCommentHandling.Skip
                    });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to deserialize RawJsonResult for history id={Id}", id);
                TempData["ErrorMessage"] = Loc.T("ErrReportCannotBeReconstructed");
                return (null, null, RedirectToAction(nameof(History),
                    new { id = history.ProfileId ?? 0 }));
            }

            if (result == null)
            {
                TempData["ErrorMessage"] = Loc.T("ErrReportCannotBeReconstructed");
                return (null, null, RedirectToAction(nameof(History),
                    new { id = history.ProfileId ?? 0 }));
            }

            byte[] pdfBytes;
            try
            {
                // Freemium gating also applies to re-downloads of past reports —
                // otherwise users could bypass the blur by re-pulling old history.
                var paidStatus = await _db.Users.AsNoTracking()
                    .Where(u => u.Email == CurrentEmail)
                    .Select(u => u.Credite)
                    .FirstOrDefaultAsync();
                bool isFreemium = paidStatus == 0;
                pdfBytes = _pdfGenerator.Generate(result, LocalizedLabels.ForCurrentUi(), isFreemium);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "PDF regeneration failed for history id={Id}", id);
                TempData["ErrorMessage"] = Loc.T("ErrPdfGenerationFailed");
                return (null, null, RedirectToAction(nameof(History),
                    new { id = history.ProfileId ?? 0 }));
            }

            var fileName = $"MedicalApp_{history.CreatedAt:yyyyMMdd_HHmmss}_report.pdf";
            return (pdfBytes, fileName, null);
        }

        // ====================================================================
        // DELETE one interpretation from the archive (with explicit user confirmation
        // submitted from the History page).
        // ====================================================================
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteHistory(int id, int profileId)
        {
            if (string.IsNullOrEmpty(CurrentEmail))
                return RedirectToAction("Index", "Home");

            var history = await _db.InterpretationHistories
                .FirstOrDefaultAsync(h => h.Id == id && h.UserEmail == CurrentEmail);
            if (history == null)
            {
                TempData["ErrorMessage"] = Loc.T("ErrInterpretationNotFound");
                return RedirectToAction(nameof(History), new { id = profileId });
            }

            _db.InterpretationHistories.Remove(history);
            await _db.SaveChangesAsync();

            _logger.LogInformation(
                "User {Email} deleted interpretation history id={Id} (profile={Pid}, file={File}).",
                CurrentEmail, id, history.ProfileId, history.OriginalFileName);

            TempData["SuccessMessage"] = Loc.T("OkInterpretationDeletedFromArchive");
            return RedirectToAction(nameof(History), new { id = profileId });
        }

        // ====================================================================
        // COMPARE 2 to 4 interpretations side-by-side (P1.5.5, premium feature).
        // Columns are ordered oldest → newest by patient's sampling date
        // (PatientInfo.DateTaken in the stored JSON, with a tolerant parser),
        // falling back to CreatedAt when the date cannot be parsed.
        // ====================================================================
        [HttpGet]
        public async Task<IActionResult> Compare(int profileId, int[]? ids)
        {
            if (string.IsNullOrEmpty(CurrentEmail))
                return RedirectToAction("Index", "Home");

            // Sanitize: distinct, non-zero ids, max 4.
            var distinctIds = (ids ?? Array.Empty<int>())
                .Where(i => i > 0)
                .Distinct()
                .Take(CompareInterpretationsViewModel.MaxSelections)
                .ToArray();

            if (distinctIds.Length < CompareInterpretationsViewModel.MinSelections)
            {
                TempData["ErrorMessage"] = string.Format(
                    Loc.T("ErrSelectBetweenForCompare"),
                    CompareInterpretationsViewModel.MinSelections,
                    CompareInterpretationsViewModel.MaxSelections);
                return RedirectToAction(nameof(History), new { id = profileId });
            }

            var user = await _db.Users.FirstOrDefaultAsync(u => u.Email == CurrentEmail);
            if (user == null)
            {
                HttpContext.Session.Clear();
                return RedirectToAction("Index", "Home");
            }

            var profile = await _db.Profiles.AsNoTracking()
                .FirstOrDefaultAsync(p => p.Id == profileId && p.UserEmail == CurrentEmail);
            if (profile == null)
            {
                TempData["ErrorMessage"] = Loc.T("ErrProfileNotFound");
                return RedirectToAction(nameof(Index));
            }

            var items = await _db.InterpretationHistories
                .AsNoTracking()
                .Where(h => distinctIds.Contains(h.Id)
                            && h.UserEmail == CurrentEmail
                            && h.ProfileId == profile.Id
                            && h.Status == "success"
                            && h.RawJsonResult != null)
                .ToListAsync();

            if (items.Count != distinctIds.Length)
            {
                TempData["ErrorMessage"] = Loc.T("ErrOneOrMoreInterpretationsNotFoundSelected");
                return RedirectToAction(nameof(History), new { id = profileId });
            }

            // Archive premium billing: 1 use regardless of how many columns are compared.
            var check = _archiveAccess.TryConsume(user, "compare");
            if (!check.Allowed)
            {
                TempData["ErrorMessage"] = Loc.T("ErrNoCreditsForCompare");
                return RedirectToAction("Buy", "Credits");
            }
            await _db.SaveChangesAsync();

            // Deserialize each JSON; drop any that fail to parse.
            var parsed = new List<(InterpretationHistory h, InterpretationResult r)>();
            foreach (var h in items)
            {
                var r = DeserializeSafe(h.RawJsonResult);
                if (r != null) parsed.Add((h, r));
            }
            if (parsed.Count < CompareInterpretationsViewModel.MinSelections)
            {
                TempData["ErrorMessage"] = Loc.T("ErrCompareGenerationFailed");
                return RedirectToAction(nameof(History), new { id = profileId });
            }

            // Sort oldest → newest by patient's SAMPLING date (PatientInfo.DateTaken).
            // Fallback to CreatedAt when DateTaken is missing or unparsable.
            parsed = parsed
                .Select(t => (t.h, t.r,
                              eff: ParseSamplingDate(t.r.PatientInfo?.DateTaken) ?? t.h.CreatedAt))
                .OrderBy(t => t.eff)
                .Select(t => (t.h, t.r))
                .ToList();

            var vm = BuildComparison(profile, parsed);
            vm.CreditConsumed = check.CreditConsumed;
            return View(vm);
        }

        // ====================================================================
        // COMPARE EXPORT — generate a PDF of the same comparison and either
        // stream it as a download or send it by email to the logged-in user.
        // Does NOT consume an archive credit (the user already paid when they
        // opened the comparison view). Mirrors EvolutionExport's UX.
        // ====================================================================
        public class CompareExportRequest
        {
            public int ProfileId { get; set; }
            /// <summary>Same interpretation IDs as the Compare view (max 4).</summary>
            public int[] Ids { get; set; } = Array.Empty<int>();
            /// <summary>"download" or "email".</summary>
            public string Mode { get; set; } = "download";
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> CompareExport([FromForm] CompareExportRequest req)
        {
            if (string.IsNullOrEmpty(CurrentEmail))
                return Unauthorized();

            // Same sanitation as Compare(): distinct, positive, capped at MaxSelections.
            var distinctIds = (req.Ids ?? Array.Empty<int>())
                .Where(i => i > 0)
                .Distinct()
                .Take(CompareInterpretationsViewModel.MaxSelections)
                .ToArray();

            if (distinctIds.Length < CompareInterpretationsViewModel.MinSelections)
            {
                TempData["ErrorMessage"] = string.Format(
                    Loc.T("ErrSelectBetween"),
                    CompareInterpretationsViewModel.MinSelections,
                    CompareInterpretationsViewModel.MaxSelections);
                return RedirectToAction(nameof(History), new { id = req.ProfileId });
            }

            var profile = await _db.Profiles.AsNoTracking()
                .FirstOrDefaultAsync(p => p.Id == req.ProfileId && p.UserEmail == CurrentEmail);
            if (profile == null)
                return NotFound("Profil inexistent.");

            var items = await _db.InterpretationHistories
                .AsNoTracking()
                .Where(h => distinctIds.Contains(h.Id)
                            && h.UserEmail == CurrentEmail
                            && h.ProfileId == profile.Id
                            && h.Status == "success"
                            && h.RawJsonResult != null)
                .ToListAsync();

            if (items.Count != distinctIds.Length)
            {
                TempData["ErrorMessage"] = Loc.T("ErrOneOrMoreInterpretationsNotFound");
                return RedirectToAction(nameof(History), new { id = req.ProfileId });
            }

            // Rebuild VM exactly as Compare() does, but skip credit consumption.
            var parsed = new List<(InterpretationHistory h, InterpretationResult r)>();
            foreach (var h in items)
            {
                var r = DeserializeSafe(h.RawJsonResult);
                if (r != null) parsed.Add((h, r));
            }
            if (parsed.Count < CompareInterpretationsViewModel.MinSelections)
            {
                TempData["ErrorMessage"] = Loc.T("ErrCompareGenerationFailed");
                return RedirectToAction(nameof(History), new { id = req.ProfileId });
            }

            parsed = parsed
                .Select(t => (t.h, t.r,
                              eff: ParseSamplingDate(t.r.PatientInfo?.DateTaken) ?? t.h.CreatedAt))
                .OrderBy(t => t.eff)
                .Select(t => (t.h, t.r))
                .ToList();

            var vm = BuildComparison(profile, parsed);

            byte[] pdfBytes;
            try
            {
                pdfBytes = _comparePdf.Generate(profile, vm);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "CompareExport: PDF generation failed.");
                return StatusCode(500, Loc.T("ErrPdfGenerationFailedSeeLog"));
            }

            var fileName = $"Comparatie_{Sanitize(profile.Name)}_{DateTime.Now:yyyyMMdd_HHmm}.pdf";

            if (string.Equals(req.Mode, "email", StringComparison.OrdinalIgnoreCase))
            {
                // Capture culture at request entry so the email body can't drift
                // to a different language if any awaited call below offloads
                // work to the thread pool (same pattern as InterpretationController.SaveHistory).
                var lang = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
                var safeName = System.Net.WebUtility.HtmlEncode(profile.Name);
                var html =
                    $"<p>{Loc.T("EmailGreeting", lang)}</p>" +
                    $"<p>{string.Format(Loc.T("EmailCompareBodyFmt", lang), safeName, vm.Columns.Count, vm.Rows.Count)}</p>" +
                    $"<p>{Loc.T("EmailGoodDay", lang)}<br/>— MyMedicalApp.NET</p>";
                try
                {
                    await _emailService.SendEmailWithAttachmentAsync(
                        CurrentEmail,
                        string.Format(Loc.T("EmailCompareSubjectFmt", lang), profile.Name),
                        html,
                        pdfBytes,
                        fileName);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "CompareExport: email send failed to {Email}.", CurrentEmail);
                    TempData["ErrorMessage"] = Loc.T("EmailSendFailedTryDownload", lang);
                    return RedirectToAction(nameof(Compare),
                        new { profileId = req.ProfileId, ids = distinctIds });
                }

                TempData["SuccessMessage"] = $"Raportul a fost trimis pe email la {CurrentEmail}.";
                return RedirectToAction(nameof(Compare),
                    new { profileId = req.ProfileId, ids = distinctIds });
            }

            return File(pdfBytes, "application/pdf", fileName);
        }

        private static InterpretationResult? DeserializeSafe(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            try
            {
                return JsonSerializer.Deserialize<InterpretationResult>(raw,
                    new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true,
                        AllowTrailingCommas = true,
                        ReadCommentHandling = JsonCommentHandling.Skip
                    });
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Tolerantly parses the various date formats labs print on PDFs. Examples we want
        /// to handle: "27/01/2014", "27.01.2014", "27-01-2014", "2014-01-27", "01/27/2014",
        /// "27/01/2014 14:30", "27 Jan 2014" etc. Returns null when no parse succeeds.
        /// </summary>
        private static DateTime? ParseSamplingDate(string? raw)
            => SamplingDateParser.TryParse(raw);

        public static CompareInterpretationsViewModel BuildComparison(
            Profile profile,
            List<(InterpretationHistory h, InterpretationResult r)> sortedOldestFirst)
        {
            // ----------------------------------------------------------------
            // Pas 4: grouping by LOINC code (with parameter-name fallback)
            // ----------------------------------------------------------------
            // Historically the Compare view grouped rows by lowercase parameter
            // name. That broke whenever two lab reports for the same analyte
            // used different wording — "VSH" vs "ESR", "Glicemie" vs "Glucose",
            // "Procent protrombina" vs "Quick %" — even though they are the
            // same test (and now we have an authoritative LOINC code proving
            // they are the same test).
            //
            // New algorithm:
            //   * If a KeyResult has a non-empty LoincCode (post-validator),
            //     the row key is "loinc:<code>". All cells with the same
            //     code line up on ONE row, even if their parameter labels
            //     disagree.
            //   * If a KeyResult has no LoincCode (legacy rows pre-Pas 2, or
            //     parameters with no LOINC counterpart like custom indices),
            //     it falls back to the OLD behaviour: row key is
            //     "name:<normalized parameter>".
            //   * Cross-grouping is allowed: a parameter that was coded in
            //     one report and not coded in another will appear on TWO
            //     separate rows. That is intentional and HONEST — we don't
            //     pretend the link is solid. The user will see this and can
            //     re-interpret the older report to get LOINC coverage.
            // ----------------------------------------------------------------

            static string NameKey(string param) =>
                "name:" + (param ?? string.Empty).Trim().ToLowerInvariant();

            // ----------------------------------------------------------------
            // Retroactive LOINC unification (display-only, no DB write).
            // Same name + same unit + same reference range => same analyte, so
            // the codes Gemini's wording variability split apart collapse onto
            // ONE row here. Incomplete signatures are never merged; they get a
            // discreet "!" instead (MissingAxis below).
            // ----------------------------------------------------------------
            var uni = Services.LoincUnifier.Analyze(
                sortedOldestFirst.SelectMany(t => t.r.KeyResults ?? new()));

            string? UnifiedCode(KeyResult kr) =>
                Services.LoincUnifier.Unify(kr.LoincCode, uni.CodeMap)?.Trim();

            // Same code, different units (Fibrinogen g/L vs mg/dL) must NOT
            // share a row — the values live on different scales.
            var unitScope = Services.LoincUnifier.UnitScope.Build(
                sortedOldestFirst.SelectMany(t => t.r.KeyResults ?? new()), uni.CodeMap);

            string KeyFor(KeyResult kr) =>
                !string.IsNullOrWhiteSpace(kr.LoincCode)
                    ? "loinc:" + UnifiedCode(kr) + unitScope.Suffix(UnifiedCode(kr), kr.Unit)
                    : NameKey(kr.Parameter);

            // Representative KeyResult per SURVIVING code, so the row shows the
            // long name / source / score of the winning code, not of a loser.
            var identityByCode = new Dictionary<string, KeyResult>(StringComparer.OrdinalIgnoreCase);
            foreach (var (_, r) in sortedOldestFirst) // oldest first => newest wins
                foreach (var kr in r.KeyResults ?? new())
                    if (!string.IsNullOrWhiteSpace(kr.LoincCode) &&
                        string.Equals(kr.LoincCode.Trim(), UnifiedCode(kr), StringComparison.OrdinalIgnoreCase))
                        identityByCode[kr.LoincCode.Trim()] = kr;

            int n = sortedOldestFirst.Count;

            // Build per-column key→KeyResult dictionaries.
            var keyMaps = sortedOldestFirst
                .Select(t => (t.r.KeyResults ?? new())
                    .Where(k => !string.IsNullOrWhiteSpace(k.Parameter))
                    .GroupBy(KeyFor)
                    .ToDictionary(g => g.Key, g => g.First()))
                .ToList();

            // ----------------------------------------------------------------
            // Pas 5: ordering by LOINC CLASS (medical specialty)
            // ----------------------------------------------------------------
            // Now that every matched KeyResult carries an authoritative
            // LoincClass (HEM, CHEM, SERO, ENDO, COAG, UA, ...) we can group
            // the Compare table by medical specialty exactly like a real
            // lab report PDF does: Hematology first, then Coagulation,
            // Biochimie serică, Endocrinologie, Serologie, Urinalysis, etc.
            //
            // For each row key we pick the LATEST non-null LoincClass we
            // see across all columns (the newer interpretation is the most
            // likely to have been processed with the CLASS-aware seeder).
            // Rows without any class fall into the "Alte analize" bucket
            // and appear at the very end so they remain visible.
            // ----------------------------------------------------------------
            string? PickClassFor(string rowKey)
            {
                for (int i = n - 1; i >= 0; i--)
                {
                    if (keyMaps[i].TryGetValue(rowKey, out var kr) &&
                        !string.IsNullOrWhiteSpace(kr.LoincClass))
                    {
                        return kr.LoincClass;
                    }
                }
                return null;
            }

            // Build a one-shot "what class does each row belong to" map so
            // we sort by it without re-computing the value four times.
            var classByKey = new Dictionary<string, string?>(StringComparer.Ordinal);
            foreach (var k in keyMaps.SelectMany(m => m.Keys).Distinct())
                classByKey[k] = PickClassFor(k);

            // Union of all row keys, sorted by:
            //   1. LOINC CLASS priority (Hematology -> Coagulation -> Chemistry -> ...)
            //   2. parameter display name, case-insensitive
            // We DELIBERATELY no longer split "loinc:" vs "name:" prefixes
            // first — class-based grouping is a more meaningful organization
            // for the user. Rows without a class go last (priority 999).
            var allKeys = keyMaps
                .SelectMany(m => m.Keys)
                .Distinct()
                .OrderBy(k => Services.LoincClassDisplay.GetPriority(classByKey[k]))
                .ThenBy(k =>
                {
                    // representative parameter name for alphabetic sub-ordering
                    for (int i = n - 1; i >= 0; i--)
                        if (keyMaps[i].TryGetValue(k, out var kr))
                            return kr.Parameter ?? k;
                    return k;
                }, StringComparer.OrdinalIgnoreCase)
                .ToList();

            // ----------------------------------------------------------------
            // LOINC drift detection (option b — conservative)
            // ----------------------------------------------------------------
            // Build a map: normalized parameter name -> set of distinct
            // LOINC codes assigned to that name across ALL columns.
            // When the SAME name received >=2 different LOINC codes, every
            // row that carries one of those codes gets HasLoincDrift=true
            // and a tooltip listing the other codes seen under the same
            // wording. This warns the user about Gemini's text-extraction
            // variability without false-alarming on every minor difference.
            // ----------------------------------------------------------------
            var codesByNormName = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            foreach (var (_, r) in sortedOldestFirst)
            {
                foreach (var kr in r.KeyResults ?? new())
                {
                    if (string.IsNullOrWhiteSpace(kr.Parameter) ||
                        string.IsNullOrWhiteSpace(kr.LoincCode)) continue;
                    var nname = kr.Parameter.Trim().ToLowerInvariant();
                    if (!codesByNormName.TryGetValue(nname, out var set))
                    {
                        set = new HashSet<string>(StringComparer.Ordinal);
                        codesByNormName[nname] = set;
                    }
                    // Post-unification codes only — a drift that we already
                    // resolved must not raise a warning any more.
                    set.Add(UnifiedCode(kr)!);
                }
            }

            int risen = 0, fallen = 0, unchanged = 0, partial = 0;

            var rows = new List<CompareInterpretationsViewModel.ComparisonRow>(allKeys.Count);
            string? previousClassLabel = null;
            foreach (var k in allKeys)
            {
                // Find a representative parameter object for the row's metadata
                // (latest column wins, falls back through earlier columns).
                KeyResult? meta = null;
                for (int i = n - 1; i >= 0 && meta == null; i--)
                    keyMaps[i].TryGetValue(k, out meta);

                var rowClass = classByKey[k];
                var classLabel = Services.LoincClassDisplay.GetLabel(rowClass);

                // Identity of the row: the unified code (from the row key) plus
                // the metadata of the KeyResult that actually carries it.
                var unifiedCode = k.StartsWith("loinc:")
                    ? k.Substring("loinc:".Length).Split('|')[0]
                    : null;
                var identity = unifiedCode != null && identityByCode.TryGetValue(unifiedCode, out var idn)
                    ? idn
                    : meta;

                var row = new CompareInterpretationsViewModel.ComparisonRow
                {
                    Parameter = meta?.Parameter ?? k,
                    Unit = meta?.Unit,
                    ReferenceRange = meta?.ReferenceRange,
                    // Surface the LOINC identity on LOINC-grouped rows so the
                    // view can show a tooltip / badge. Null on name-fallback rows.
                    LoincCode = unifiedCode,
                    LoincLongName = unifiedCode != null ? identity?.LoincLongName : null,
                    LoincSource = unifiedCode != null ? identity?.LoincSource : null,
                    LoincScore = unifiedCode != null ? identity?.LoincScore : null,
                    LoincClass = rowClass,
                    ClassDisplayLabel = classLabel,
                    // First row in each class group triggers a section header
                    // in the view. We compare against the previous row's label
                    // (not class code) so "HEM" and "HEM/BC" merge cleanly into
                    // a single "Hematologie" header.
                    IsFirstInClass = !string.Equals(classLabel, previousClassLabel, StringComparison.Ordinal),
                };
                previousClassLabel = classLabel;

                // PRUDENT unification: the same name still carries two codes
                // because a report did not print its unit / reference range.
                row.MissingAxis = uni.MissingAxisFor(meta?.Parameter);

                // Apply LOINC-drift warning when this row's parameter name
                // (case-insensitive) was mapped to MORE than one LOINC code
                // across the compared interpretations. The other codes go in
                // DriftLoincCodes for the tooltip.
                if (!string.IsNullOrWhiteSpace(row.LoincCode) && meta != null &&
                    !string.IsNullOrWhiteSpace(meta.Parameter))
                {
                    var nname = meta.Parameter.Trim().ToLowerInvariant();
                    if (codesByNormName.TryGetValue(nname, out var allCodes) && allCodes.Count > 1)
                    {
                        row.HasLoincDrift = true;
                        row.DriftLoincCodes = allCodes
                            .Where(c => !string.Equals(c, row.LoincCode, StringComparison.Ordinal))
                            .OrderBy(c => c, StringComparer.Ordinal)
                            .ToList();
                    }
                }

                // First numeric value index (used as the baseline for "risen/fallen").
                int? baseIdx = null;
                double baseValue = 0;
                int presentCount = 0;
                int numericCount = 0;

                for (int i = 0; i < n; i++)
                {
                    var cell = new CompareInterpretationsViewModel.Cell();
                    if (keyMaps[i].TryGetValue(k, out var kr))
                    {
                        presentCount++;
                        cell.Value = kr.Value;
                        cell.Status = kr.Status;
                        cell.CellDirection = "unchanged"; // refined below
                        var (v, ok) = ParseNumeric(kr.Value);
                        if (ok)
                        {
                            numericCount++;
                            if (baseIdx == null)
                            {
                                baseIdx = i;
                                baseValue = v;
                                cell.CellDirection = "first";
                            }
                            else
                            {
                                if (Math.Abs(v - baseValue) < 1e-9) cell.CellDirection = "unchanged";
                                else if (v > baseValue) cell.CellDirection = "risen";
                                else cell.CellDirection = "fallen";
                            }
                        }
                        else
                        {
                            cell.CellDirection = baseIdx == null ? "first" : "unchanged";
                        }
                    }
                    else
                    {
                        cell.CellDirection = "absent";
                    }
                    row.Cells.Add(cell);
                }

                // Aggregate row-level direction.
                if (presentCount < n)
                {
                    row.Direction = "partial";
                    partial++;
                }
                else if (numericCount == n && baseIdx != null)
                {
                    // Compare LAST numeric vs the baseline (first numeric).
                    var lastNumeric = row.Cells
                        .Select((c, idx) => (c, idx))
                        .Where(t => t.c.CellDirection != "absent" && ParseNumeric(t.c.Value).ok)
                        .Select(t => ParseNumeric(t.c.Value).value)
                        .Last();
                    if (Math.Abs(lastNumeric - baseValue) < 1e-9) { row.Direction = "unchanged"; unchanged++; }
                    else if (lastNumeric > baseValue) { row.Direction = "risen"; risen++; }
                    else { row.Direction = "fallen"; fallen++; }
                }
                else
                {
                    // All cells present but at least one non-numeric: compare strings.
                    var first = row.Cells[0].Value?.Trim();
                    bool allEqual = row.Cells.All(c =>
                        string.Equals(c.Value?.Trim(), first, StringComparison.OrdinalIgnoreCase));
                    if (allEqual) { row.Direction = "unchanged"; unchanged++; }
                    else { row.Direction = "unparsable"; }
                }

                rows.Add(row);
            }

            var columns = sortedOldestFirst.Select(t =>
            {
                var eff = ParseSamplingDate(t.r.PatientInfo?.DateTaken) ?? t.h.CreatedAt;
                return new CompareInterpretationsViewModel.Column
                {
                    HistoryId = t.h.Id,
                    CreatedAt = t.h.CreatedAt,
                    OriginalFileName = t.h.OriginalFileName,
                    DateTaken = t.r.PatientInfo?.DateTaken,
                    EffectiveDate = eff,
                    KeyResultsCount = t.r.KeyResults?.Count ?? 0,
                    AbnormalFindingsCount = t.r.AbnormalFindings?.Count ?? 0,
                    PatientName = t.r.PatientInfo?.Name
                };
            }).ToList();

            return new CompareInterpretationsViewModel
            {
                ProfileId = profile.Id,
                ProfileName = profile.Name,
                Columns = columns,
                Rows = rows,
                RisenCount = risen,
                FallenCount = fallen,
                UnchangedCount = unchanged,
                PartialCount = partial
            };
        }

        /// <summary>
        /// Tries to extract a numeric value from labels like "4.6", "4,6", "12.3 x10^9/L",
        /// "&lt;0.5", "&gt;200". Returns (0, false) when no parse is possible.
        /// </summary>
        private static (double value, bool ok) ParseNumeric(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return (0, false);
            var s = raw.Trim().TrimStart('<', '>', '=', '~', '≤', '≥', ' ').Replace(',', '.');
            // Take the first contiguous number-ish token.
            var buf = new System.Text.StringBuilder();
            bool seenDigit = false;
            foreach (var c in s)
            {
                if (char.IsDigit(c) || c == '.' || (c == '-' && buf.Length == 0))
                {
                    buf.Append(c);
                    if (char.IsDigit(c)) seenDigit = true;
                }
                else if (seenDigit) break;
            }
            if (buf.Length == 0 || !seenDigit) return (0, false);
            return double.TryParse(buf.ToString(),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out var v)
                ? (v, true)
                : (0, false);
        }

        // ====================================================================
        // MEDICAL DOSSIER (B2C) — every out-of-range result the profile ever had,
        // pulled from ALL archived interpretations, grouped by medical specialty
        // then by analyte, each analyte showing its own timeline. This is the
        // page the patient prints for the doctor.
        // Billing: 1 archive-premium use, exactly like Compare and Evolution.
        // ====================================================================
        public async Task<IActionResult> Dossier(int profileId)
        {
            if (string.IsNullOrEmpty(CurrentEmail))
                return RedirectToAction("Index", "Home");

            var user = await _db.Users.FirstOrDefaultAsync(u => u.Email == CurrentEmail);
            if (user == null)
            {
                HttpContext.Session.Clear();
                return RedirectToAction("Index", "Home");
            }

            var profile = await _db.Profiles.AsNoTracking()
                .FirstOrDefaultAsync(p => p.Id == profileId && p.UserEmail == CurrentEmail);
            if (profile == null)
            {
                TempData["ErrorMessage"] = Loc.T("ErrProfileNotFound");
                return RedirectToAction(nameof(Index));
            }

            var check = _archiveAccess.TryConsume(user, "dossier");
            if (!check.Allowed)
            {
                TempData["ErrorMessage"] = Loc.T("ErrNoCreditsForCompare");
                return RedirectToAction("Buy", "Credits");
            }
            await _db.SaveChangesAsync();

            var vm = await BuildDossierAsync(profile);
            vm.CreditConsumed = check.CreditConsumed;
            return View(vm);
        }

        public class DossierExportRequest
        {
            public int ProfileId { get; set; }
            /// <summary>"download" or "email".</summary>
            public string Mode { get; set; } = "download";
        }

        // ====================================================================
        // DOSSIER EXPORT — same dossier as a PDF, streamed or emailed. Does NOT
        // consume a credit: the user already paid when opening the view.
        // ====================================================================
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DossierExport([FromForm] DossierExportRequest req)
        {
            if (string.IsNullOrEmpty(CurrentEmail))
                return Unauthorized();

            var profile = await _db.Profiles.AsNoTracking()
                .FirstOrDefaultAsync(p => p.Id == req.ProfileId && p.UserEmail == CurrentEmail);
            if (profile == null)
                return NotFound(Loc.T("ErrProfileNotFound"));

            var vm = await BuildDossierAsync(profile);

            byte[] pdfBytes;
            try
            {
                pdfBytes = _dossierPdf.Generate(vm);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "DossierExport: PDF generation failed for profile {ProfileId}.", profile.Id);
                return StatusCode(500, Loc.T("ErrPdfGenerationFailedSeeLog"));
            }

            var fileName = $"DosarMedical_{Sanitize(profile.Name)}_{DateTime.Now:yyyyMMdd_HHmm}.pdf";

            if (string.Equals(req.Mode, "email", StringComparison.OrdinalIgnoreCase))
            {
                var lang = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
                var safeName = System.Net.WebUtility.HtmlEncode(profile.Name);
                var html =
                    $"<p>{Loc.T("EmailGreeting", lang)}</p>" +
                    $"<p>{string.Format(Loc.T("DossierEmailBodyFmt", lang), safeName, vm.AbnormalAnalyteCount, vm.SourceReportsCount)}</p>" +
                    $"<p>{Loc.T("EmailGoodDay", lang)}<br/>— MyMedicalApp.NET</p>";
                try
                {
                    await _emailService.SendEmailWithAttachmentAsync(
                        CurrentEmail,
                        string.Format(Loc.T("DossierEmailSubjectFmt", lang), profile.Name),
                        html,
                        pdfBytes,
                        fileName);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "DossierExport: email send failed.");
                    TempData["ErrorMessage"] = Loc.T("EmailSendFailedTryDownload");
                    return RedirectToAction(nameof(Dossier), new { profileId = profile.Id });
                }

                TempData["SuccessMessage"] = string.Format(Loc.T("DossierEmailSentFmt"), CurrentEmail);
                return RedirectToAction(nameof(Dossier), new { profileId = profile.Id });
            }

            return File(pdfBytes, "application/pdf", fileName);
        }

        /// <summary>
        /// Collects every abnormal result across the profile's archive and shapes it
        /// into the dossier. Reads only stored RawJsonResult — no LLM call, no cost.
        /// </summary>
        private async Task<MedicalDossierViewModel> BuildDossierAsync(Profile profile)
        {
            var histories = await _db.InterpretationHistories.AsNoTracking()
                .Where(h => h.UserEmail == CurrentEmail
                            && h.ProfileId == profile.Id
                            && h.Status == "success"
                            && h.RawJsonResult != null)
                .ToListAsync();

            return BuildDossier(profile, histories);
        }

        /// <summary>Pure aggregation step, split out so it can be exercised without a database.</summary>
        private static MedicalDossierViewModel BuildDossier(Profile profile, List<InterpretationHistory> histories)
        {            var vm = new MedicalDossierViewModel
            {
                ProfileId = profile.Id,
                ProfileName = profile.Name,
                Gender = profile.Gender,
                BirthYear = profile.BirthYear,
                Age = profile.BirthYear.HasValue && profile.BirthYear > 1900
                    ? DateTime.UtcNow.Year - profile.BirthYear.Value
                    : null,
                MedicalHistory = string.IsNullOrWhiteSpace(profile.Notes) ? null : profile.Notes!.Trim(),
                SourceReportsCount = histories.Count
            };

            // groupKey -> analyte bucket (+ the class code we will sort it by)
            var buckets = new Dictionary<string, (MedicalDossierViewModel.AnalyteGroup Group, string? ClassCode)>(
                StringComparer.OrdinalIgnoreCase);
            // Guards against the same result being listed twice when the user
            // uploaded the same lab report more than once.
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Parse once — the unifier needs the whole archive up front.
            var parsed = histories
                .Select(h => (h, r: DeserializeSafe(h.RawJsonResult)))
                .Where(t => t.r?.KeyResults != null)
                .ToList();

            // Retroactive LOINC unification (display-only): identical name +
            // unit + reference range means one analyte, one timeline.
            var uni = Services.LoincUnifier.Analyze(parsed.SelectMany(t => t.r!.KeyResults!));
            var unitScope = Services.LoincUnifier.UnitScope.Build(
                parsed.SelectMany(t => t.r!.KeyResults!), uni.CodeMap);

            foreach (var (h, rr) in parsed)
            {
                var r = rr!;

                var effDate = ParseSamplingDate(r.PatientInfo?.DateTaken);
                var lab = r.PatientInfo?.Laboratory;

                foreach (var kr in r.KeyResults!)
                {
                    if (string.IsNullOrWhiteSpace(kr.Parameter)) continue;

                    var status = ClassifyAbnormal(kr);
                    if (status == null) continue;

                    var unifiedCode = Services.LoincUnifier.Unify(kr.LoincCode, uni.CodeMap)?.Trim();

                    var key = !string.IsNullOrWhiteSpace(unifiedCode)
                        ? "loinc:" + unifiedCode + unitScope.Suffix(unifiedCode, kr.Unit)
                        : "name:" + kr.Parameter.Trim().ToLowerInvariant();

                    var date = effDate ?? h.CreatedAt;

                    // Same analyte, same day, same value => duplicate upload.
                    var dedupKey = $"{key}|{date:yyyyMMdd}|{kr.Value?.Trim()}";
                    if (!seen.Add(dedupKey)) continue;

                    if (!buckets.TryGetValue(key, out var bucket))
                    {
                        bucket = (new MedicalDossierViewModel.AnalyteGroup(), null);
                        buckets[key] = bucket;
                    }

                    var entry = new MedicalDossierViewModel.Entry
                    {
                        HistoryId = h.Id,
                        Date = date,
                        DateIsSampling = effDate.HasValue,
                        InterpretedAt = h.CreatedAt,
                        Laboratory = string.IsNullOrWhiteSpace(lab) ? null : lab!.Trim(),
                        Value = kr.Value,
                        Unit = kr.Unit,
                        ReferenceRange = kr.ReferenceRange,
                        Status = status
                    };
                    bucket.Group.Entries.Add(entry);

                    // Newest report wins for the display name / LOINC identity.
                    var isNewest = bucket.Group.Entries.Count == 1 ||
                                   date >= bucket.Group.Entries.Max(e => e.Date);
                    if (isNewest)
                    {
                        bucket.Group.Parameter = kr.Parameter.Trim();
                        bucket.Group.MissingAxis = uni.MissingAxisFor(kr.Parameter);
                        if (!string.IsNullOrWhiteSpace(unifiedCode))
                        {
                            bucket.Group.LoincCode = unifiedCode;
                            // Only trust the long name when it belongs to the
                            // surviving code (it can come from a merged loser).
                            if (string.Equals(kr.LoincCode?.Trim(), unifiedCode, StringComparison.OrdinalIgnoreCase))
                            {
                                bucket.Group.LoincLongName = kr.LoincLongName;
                                bucket.Group.LoincSource = kr.LoincSource;
                            }
                        }
                    }

                    // Keep the first class we can determine — from the official
                    // LOINC CLASS when present, otherwise inferred from the lab's
                    // own panel header so code-less analytes still land in their
                    // specialty instead of a generic bucket.
                    if (string.IsNullOrWhiteSpace(bucket.ClassCode))
                    {
                        var cls = !string.IsNullOrWhiteSpace(kr.LoincClass)
                            ? kr.LoincClass
                            : InferClassFromPanel(kr.PanelHeaderRaw, kr.AnalyteLineRaw);
                        buckets[key] = (bucket.Group, cls);
                    }
                }
            }

            // Timeline order + trend versus the previous entry.
            foreach (var (_, bucket) in buckets)
            {
                var ordered = bucket.Group.Entries.OrderBy(e => e.Date).ToList();
                for (int i = 1; i < ordered.Count; i++)
                {
                    var (prev, prevOk) = ParseNumeric(ordered[i - 1].Value);
                    var (cur, curOk) = ParseNumeric(ordered[i].Value);
                    if (!prevOk || !curOk) continue;
                    ordered[i].Trend = cur > prev ? "up" : cur < prev ? "down" : "same";
                }
                bucket.Group.Entries = ordered;
            }

            vm.Groups = buckets.Values
                .GroupBy(b => Services.LoincClassDisplay.GetLabel(b.ClassCode), StringComparer.Ordinal)
                .Select(g => new MedicalDossierViewModel.ClassGroup
                {
                    Label = g.Key,
                    Priority = g.Min(b => Services.LoincClassDisplay.GetPriority(b.ClassCode)),
                    Analytes = g.Select(b => b.Group)
                        .OrderBy(a => a.LoincCode ?? "zzz", StringComparer.Ordinal)
                        .ThenBy(a => a.Parameter, StringComparer.OrdinalIgnoreCase)
                        .ToList()
                })
                .OrderBy(g => g.Priority)
                .ThenBy(g => g.Label, StringComparer.OrdinalIgnoreCase)
                .ToList();

            vm.AbnormalAnalyteCount = buckets.Count;
            vm.AbnormalEntryCount = buckets.Values.Sum(b => b.Group.Entries.Count);
            var allDates = buckets.Values.SelectMany(b => b.Group.Entries).Select(e => e.Date).ToList();
            if (allDates.Count > 0)
            {
                vm.FirstDate = allDates.Min();
                vm.LastDate = allDates.Max();
            }

            return vm;
        }

        /// <summary>
        /// Returns "high" / "low" / "borderline" / "positive" when the result belongs
        /// in the dossier, or null when it is a normal finding. Text-positive results
        /// (Ag HBs, antibodies) often arrive with no numeric status, so the value text
        /// is inspected too — negations are checked FIRST so "nereactiv" is not read
        /// as "reactiv".
        /// </summary>
        private static string? ClassifyAbnormal(KeyResult kr)
        {
            var status = (kr.Status ?? "").Trim().ToLowerInvariant();
            if (status is "high" or "low" or "borderline") return status;

            var value = (kr.Value ?? "").Trim().ToLowerInvariant();
            if (value.Length == 0) return null;

            string[] negations =
            {
                "negativ", "negative", "negativo", "nereactiv", "non-reactiv", "non reactiv",
                "nonreactive", "non-reactive", "nedetectabil", "not detected", "undetectable",
                "absent", "abwesend", "ausente", "assente"
            };
            if (negations.Any(n => value.Contains(n, StringComparison.Ordinal))) return null;

            string[] positives =
            {
                "pozitiv", "positive", "positiv", "positivo", "positif", "positivo",
                "reactiv", "reactive", "reagent", "detectabil", "detected", "detectat",
                "prezent", "present", "presente", "vorhanden"
            };
            return positives.Any(p => value.Contains(p, StringComparison.Ordinal)) ? "positive" : null;
        }

        /// <summary>
        /// Best-effort specialty for analytes the matcher could not code: reads the
        /// lab's own panel header / method line and maps it to a LOINC CLASS code.
        /// </summary>
        private static string? InferClassFromPanel(string? panelHeader, string? analyteLine)
        {
            var text = ((panelHeader ?? "") + " " + (analyteLine ?? "")).ToLowerInvariant();
            if (text.Trim().Length == 0) return null;

            if (text.Contains("hematolog") || text.Contains("hemoleucogram") ||
                text.Contains("hemogram") || text.Contains("haematolog")) return "HEM";
            if (text.Contains("coagul") || text.Contains("hemostaz")) return "COAG";
            if (text.Contains("hormon") || text.Contains("endocrin") || text.Contains("tiroid")) return "HORMONE";
            if (text.Contains("marker tumoral") || text.Contains("tumor")) return "TUMOR MARKERS";
            if (text.Contains("serolog") || text.Contains("imunolog") || text.Contains("immunolog") ||
                text.Contains("anticorp") || text.Contains("antibod")) return "SERO";
            if (text.Contains("alergolog") || text.Contains("allerg")) return "ALLERGY";
            if (text.Contains("urin") || text.Contains("sumar de urina")) return "UA";
            if (text.Contains("microbiolog") || text.Contains("cultur") || text.Contains("bacterio")) return "MICRO";
            if (text.Contains("parazit") || text.Contains("parasit")) return "PARASITE";
            if (text.Contains("toxicolog") || text.Contains("drog")) return "TOX";
            if (text.Contains("biochim") || text.Contains("chemistry") || text.Contains("chimie")) return "CHEM";
            return null;
        }

        // ====================================================================
        // EVOLUTION (P1.8): time-series chart for 1..5 LOINC codes across ALL
        // of the profile's interpretations. No credit cost — we only aggregate
        // data that is already stored in InterpretationHistories.RawJsonResult.
        // ====================================================================
        [HttpGet]
        public async Task<IActionResult> Evolution(int profileId, string? codes)
        {
            if (string.IsNullOrEmpty(CurrentEmail))
                return RedirectToAction("Index", "Home");

            var profile = await _db.Profiles.AsNoTracking()
                .FirstOrDefaultAsync(p => p.Id == profileId && p.UserEmail == CurrentEmail);
            if (profile == null)
            {
                TempData["ErrorMessage"] = Loc.T("ErrProfileNotFound");
                return RedirectToAction(nameof(Index));
            }

            // Parse and sanitize the user-pasted LOINC codes.
            // Accept comma, semicolon, whitespace and newline as separators so
            // the user can paste a quick list like "718-7, 4548-4 2160-0".
            var codeList = (codes ?? string.Empty)
                .Split(new[] { ',', ';', '\n', '\r', '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim())
                .Where(s => s.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(EvolutionViewModel.MaxSelections)
                .ToList();

            if (codeList.Count < EvolutionViewModel.MinSelections)
            {
                TempData["ErrorMessage"] = string.Format(
                    Loc.T("ErrEnterBetweenLoincCodes"),
                    EvolutionViewModel.MinSelections,
                    EvolutionViewModel.MaxSelections);
                return RedirectToAction(nameof(History), new { id = profileId });
            }

            var vm = await BuildEvolutionAsync(profile, codeList);
            return View(vm);
        }

        // ====================================================================
        // EVOLUTION EXPORT — generate PDF on the server (embedding the Chart.js
        // PNG produced client-side via canvas.toDataURL) and either DOWNLOAD it
        // or EMAIL it to the logged-in user. No credit cost.
        // ====================================================================
        public class EvolutionExportRequest
        {
            public int ProfileId { get; set; }
            /// <summary>Comma/space-separated LOINC codes (same payload as the view query).</summary>
            public string Codes { get; set; } = string.Empty;
            /// <summary>
            /// PNG data URL produced by the canvas. Example:
            /// <c>"data:image/png;base64,iVBORw0KGgoAAAA..."</c>. May be empty
            /// if the user didn't wait for the chart to render — the PDF then
            /// contains only the tables.
            /// </summary>
            public string? ChartPngDataUrl { get; set; }
            /// <summary>"download" or "email".</summary>
            public string Mode { get; set; } = "download";
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> EvolutionExport([FromForm] EvolutionExportRequest req)
        {
            if (string.IsNullOrEmpty(CurrentEmail))
                return Unauthorized();

            var profile = await _db.Profiles.AsNoTracking()
                .FirstOrDefaultAsync(p => p.Id == req.ProfileId && p.UserEmail == CurrentEmail);
            if (profile == null)
                return NotFound("Profil inexistent.");

            var codeList = (req.Codes ?? string.Empty)
                .Split(new[] { ',', ';', '\n', '\r', '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim())
                .Where(s => s.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(EvolutionViewModel.MaxSelections)
                .ToList();

            if (codeList.Count < EvolutionViewModel.MinSelections)
                return BadRequest(Loc.T("ErrAtLeastOneLoinc"));

            var vm = await BuildEvolutionAsync(profile, codeList);

            // Decode the chart PNG from the data URL (best effort — pass null
            // to the PDF generator if it's missing/malformed).
            byte[]? png = null;
            if (!string.IsNullOrWhiteSpace(req.ChartPngDataUrl))
            {
                var commaIdx = req.ChartPngDataUrl.IndexOf(',');
                if (commaIdx > 0)
                {
                    try
                    {
                        png = Convert.FromBase64String(req.ChartPngDataUrl[(commaIdx + 1)..]);
                    }
                    catch (FormatException ex)
                    {
                        _logger.LogWarning(ex, "EvolutionExport: invalid base64 in ChartPngDataUrl, dropping image.");
                    }
                }
            }

            byte[] pdfBytes;
            try
            {
                pdfBytes = _evolutionPdf.Generate(vm, png);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "EvolutionExport: PDF generation failed.");
                return StatusCode(500, Loc.T("ErrPdfGenerationFailedSeeLog"));
            }

            var fileName = $"Evolutie_{Sanitize(profile.Name)}_{DateTime.Now:yyyyMMdd_HHmm}.pdf";

            if (string.Equals(req.Mode, "email", StringComparison.OrdinalIgnoreCase))
            {
                var lang = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
                var safeName = System.Net.WebUtility.HtmlEncode(profile.Name);
                var codesJoined = System.Net.WebUtility.HtmlEncode(string.Join(", ", codeList));
                var measurementsTotal = vm.Series.Sum(s => s.Points.Count);
                var html =
                    $"<p>{Loc.T("EmailGreeting", lang)}</p>" +
                    $"<p>{string.Format(Loc.T("EmailEvolutionBodyFmt", lang), safeName, vm.Series.Count, measurementsTotal)}</p>" +
                    $"<p>{string.Format(Loc.T("EmailEvolutionCodesFmt", lang), $"<code>{codesJoined}</code>")}</p>" +
                    $"<p>{Loc.T("EmailGoodDay", lang)}<br/>— MyMedicalApp.NET</p>";
                try
                {
                    await _emailService.SendEmailWithAttachmentAsync(
                        CurrentEmail,
                        string.Format(Loc.T("EmailEvolutionSubjectFmt", lang), profile.Name),
                        html,
                        pdfBytes,
                        fileName);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "EvolutionExport: email send failed to {Email}.", CurrentEmail);
                    TempData["ErrorMessage"] = Loc.T("EmailSendFailedTryDownload", lang);
                    return RedirectToAction(nameof(Evolution),
                        new { profileId = req.ProfileId, codes = string.Join(",", codeList) });
                }

                TempData["SuccessMessage"] = $"Raportul a fost trimis pe email la {CurrentEmail}.";
                return RedirectToAction(nameof(Evolution),
                    new { profileId = req.ProfileId, codes = string.Join(",", codeList) });
            }

            // Default: stream the file back as a download.
            return File(pdfBytes, "application/pdf", fileName);
        }

        private static string Sanitize(string s)
        {
            var chars = s.Select(ch => (char.IsLetterOrDigit(ch) || ch == '-' || ch == '_') ? ch : '_').ToArray();
            return new string(chars);
        }

        private async Task<EvolutionViewModel> BuildEvolutionAsync(Profile profile, List<string> codes)
        {
            var vm = new EvolutionViewModel
            {
                ProfileId = profile.Id,
                ProfileName = profile.Name,
                RequestedCodes = codes,
            };

            // Load every successful interpretation for the profile.
            var histories = await _db.InterpretationHistories
                .AsNoTracking()
                .Where(h => h.UserEmail == CurrentEmail
                            && h.ProfileId == profile.Id
                            && h.Status == "success"
                            && h.RawJsonResult != null)
                .OrderBy(h => h.CreatedAt)
                .ToListAsync();

            // Distinct color palette (up to 5 series). Picked for high contrast
            // against white background and against each other.
            var palette = new[] { "#0d6efd", "#dc3545", "#198754", "#fd7e14", "#6f42c1" };

            // Retroactive LOINC unification: parse once, collapse the codes that
            // Gemini's wording variability split, then resolve the codes the
            // user pasted through the same map so an old code still charts.
            var parsed = histories
                .Select(h => (h, r: DeserializeSafe(h.RawJsonResult)))
                .Where(t => t.r?.KeyResults != null)
                .ToList();

            var uni = Services.LoincUnifier.Analyze(parsed.SelectMany(t => t.r!.KeyResults!));

            codes = codes
                .Select(c => Services.LoincUnifier.Unify(c, uni.CodeMap)!.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            vm.RequestedCodes = codes;

            // Build one series per requested code — but split by unit when the
            // same code was reported on different scales (Fibrinogen g/L vs
            // mg/dL), otherwise the chart would mix incomparable values.
            var codeSet = new HashSet<string>(codes, StringComparer.OrdinalIgnoreCase);
            var unitScope = Services.LoincUnifier.UnitScope.Build(
                parsed.SelectMany(t => t.r!.KeyResults!), uni.CodeMap);
            var seriesByCode = new Dictionary<string, EvolutionViewModel.EvolutionSeries>(StringComparer.OrdinalIgnoreCase);

            foreach (var (h, rr) in parsed)
            {
                var r = rr!;

                var eff = ParseSamplingDate(r.PatientInfo?.DateTaken) ?? h.CreatedAt;

                foreach (var kr in r.KeyResults!)
                {
                    if (string.IsNullOrWhiteSpace(kr.LoincCode)) continue;

                    var code = Services.LoincUnifier.Unify(kr.LoincCode, uni.CodeMap)!.Trim();
                    if (!codeSet.Contains(code)) continue;

                    var seriesKey = code + unitScope.Suffix(code, kr.Unit);
                    if (!seriesByCode.TryGetValue(seriesKey, out var s))
                    {
                        s = new EvolutionViewModel.EvolutionSeries
                        {
                            LoincCode = code,
                            ColorHex = palette[seriesByCode.Count % palette.Length],
                        };
                        seriesByCode[seriesKey] = s;
                    }

                    var (val, ok) = ParseNumeric(kr.Value);
                    var point = new EvolutionViewModel.EvolutionPoint
                    {
                        EffectiveDate = eff,
                        DateLabel = eff.ToLocalTime().ToString("yyyy-MM-dd"),
                        Value = kr.Value,
                        NumericValue = ok ? val : (double?)null,
                        Status = kr.Status,
                        PatientName = r.PatientInfo?.Name,
                        Laboratory = r.PatientInfo?.Laboratory,
                        Unit = kr.Unit,
                        ReferenceRange = kr.ReferenceRange,
                    };
                    s.Points.Add(point);

                    // Always refresh the series's "latest seen" metadata from
                    // the newest point so the table shows the freshest unit
                    // and reference range (which can change between labs).
                    s.DisplayParameter = kr.Parameter ?? code;
                    s.LoincLongName = kr.LoincLongName ?? s.LoincLongName;
                    s.LoincSource = kr.LoincSource ?? s.LoincSource;
                    if (kr.LoincScore.HasValue) s.LoincScore = kr.LoincScore;
                    s.ClassDisplayLabel = Services.LoincClassDisplay.GetLabel(kr.LoincClass);
                    s.Unit = kr.Unit ?? s.Unit;
                    s.ReferenceRange = kr.ReferenceRange ?? s.ReferenceRange;
                    var (lo, hi) = ParseReferenceRange(kr.ReferenceRange);
                    if (lo.HasValue) s.RefLow = lo;
                    if (hi.HasValue) s.RefHigh = hi;
                }
            }

            // Order each series' points chronologically.
            foreach (var s in seriesByCode.Values)
                s.Points = s.Points.OrderBy(p => p.EffectiveDate).ToList();

            // Codes the user asked for but never appeared in any interpretation.
            var foundCodes = seriesByCode.Values
                .Select(s => s.LoincCode)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            vm.CodesNotFound = codes.Where(c => !foundCodes.Contains(c)).ToList();

            // Series ordering: in the order the user typed the codes (so the
            // first one keeps its primary color and appears first in the legend).
            // A code split by unit yields several series, the most reported first.
            vm.Series = codes
                .SelectMany(c => seriesByCode.Values
                    .Where(s => string.Equals(s.LoincCode, c, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(s => s.Points.Count)
                    .ThenBy(s => s.Unit, StringComparer.Ordinal))
                .ToList();

            // Reassign palette colors in the user-typed order (palette index
            // can drift if some codes weren't found).
            for (int i = 0; i < vm.Series.Count; i++)
                vm.Series[i].ColorHex = palette[i % palette.Length];

            return vm;
        }

        /// <summary>
        /// Parses a reference-range string like "12 - 18", "&lt;100", "&gt;70",
        /// "0.5 - 1.2 mg/dL" into low/high numeric bounds. Either side may be
        /// null when the range is one-sided or completely unparsable.
        /// </summary>
        private static (double? low, double? high) ParseReferenceRange(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return (null, null);
            var s = raw.Replace(',', '.').Replace('–', '-').Replace('—', '-');

            // One-sided: "<X" / "≤X" -> upper bound; ">X" / "≥X" -> lower bound.
            var trim = s.TrimStart();
            if (trim.StartsWith("<") || trim.StartsWith("≤"))
            {
                var (v, ok) = ParseNumeric(trim.TrimStart('<', '≤'));
                return ok ? ((double?)null, v) : (null, null);
            }
            if (trim.StartsWith(">") || trim.StartsWith("≥"))
            {
                var (v, ok) = ParseNumeric(trim.TrimStart('>', '≥'));
                return ok ? (v, (double?)null) : (null, null);
            }

            // Range "X - Y" — find first '-' between digits.
            // We collect the FIRST two numbers in the string and treat them
            // as [low, high]. That handles "12 - 18", "12-18 mg/dL",
            // "0.5 - 1.2 / mmol/L", etc.
            var nums = System.Text.RegularExpressions.Regex.Matches(s,
                @"-?\d+(?:\.\d+)?");
            if (nums.Count >= 2)
            {
                if (double.TryParse(nums[0].Value,
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out var lo)
                    && double.TryParse(nums[1].Value,
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out var hi))
                {
                    return (lo, hi);
                }
            }
            return (null, null);
        }

        // ====================================================================
        // CREATE
        // ====================================================================
        [HttpGet]
        public async Task<IActionResult> Create()
        {
            if (string.IsNullOrEmpty(CurrentEmail))
                return RedirectToAction("Index", "Home");

            // Anti-abuse gate (Feb 2026): B2C users need paid credits to add
            // extra profiles. Bypassed by direct URL navigation without this
            // check. See ProfileGateService for details.
            if (!await UserCanCreateAnotherProfileAsync())
            {
                TempData["ErrorMessage"] = Loc.T("ProfileLockRequirePaidCredits");
                return RedirectToAction(nameof(Index));
            }

            return View("Form", new ProfileFormViewModel());
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create(ProfileFormViewModel model)
        {
            if (string.IsNullOrEmpty(CurrentEmail))
                return RedirectToAction("Index", "Home");

            // Server-side enforcement of the anti-abuse gate — mirrors the GET
            // guard so an attacker cannot bypass the disabled button by
            // POSTing directly (curl/Postman/DevTools). Must never be removed
            // even if the view-level check is refactored away.
            if (!await UserCanCreateAnotherProfileAsync())
            {
                TempData["ErrorMessage"] = Loc.T("ProfileLockRequirePaidCredits");
                return RedirectToAction(nameof(Index));
            }

            if (!ModelState.IsValid) return View("Form", model);

            var trimmedName = (model.Name ?? "").Trim();

            // Case-insensitive duplicate check
            var nameExists = await _db.Profiles
                .AnyAsync(p => p.UserEmail == CurrentEmail &&
                               p.Name.ToLower() == trimmedName.ToLower());
            if (nameExists)
            {
                ModelState.AddModelError(nameof(model.Name),
                    "Ai deja un profil cu acest nume. Alege altul.");
                return View("Form", model);
            }

            _db.Profiles.Add(new Profile
            {
                UserEmail = CurrentEmail,
                Name = trimmedName,
                Relationship = string.IsNullOrWhiteSpace(model.Relationship) ? null : model.Relationship.Trim(),
                Gender = string.IsNullOrWhiteSpace(model.Gender) ? null : model.Gender.Trim(),
                BirthYear = model.BirthYear,
                Notes = string.IsNullOrWhiteSpace(model.Notes) ? null : model.Notes.Trim(),
                CardiovascularRisk = NormalizeCvRisk(model.CardiovascularRisk),
                IsDefault = false,
                CreatedAt = DateTime.UtcNow
            });
            await _db.SaveChangesAsync();

            TempData["SuccessMessage"] = $"Profilul \"{trimmedName}\" a fost creat.";
            return RedirectToAction(nameof(Index));
        }

        // ====================================================================
        // EDIT
        // ====================================================================
        [HttpGet]
        public async Task<IActionResult> Edit(int id)
        {
            if (string.IsNullOrEmpty(CurrentEmail))
                return RedirectToAction("Index", "Home");

            var profile = await _db.Profiles
                .FirstOrDefaultAsync(p => p.Id == id && p.UserEmail == CurrentEmail);
            if (profile == null) return RedirectToAction(nameof(Index));

            var vm = new ProfileFormViewModel
            {
                Id = profile.Id,
                Name = profile.Name,
                Relationship = profile.Relationship,
                Gender = profile.Gender,
                BirthYear = profile.BirthYear,
                Notes = profile.Notes,
                CardiovascularRisk = profile.CardiovascularRisk,
                IsDefault = profile.IsDefault
            };
            return View("Form", vm);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(ProfileFormViewModel model)
        {
            if (string.IsNullOrEmpty(CurrentEmail))
                return RedirectToAction("Index", "Home");

            if (!ModelState.IsValid) return View("Form", model);

            var profile = await _db.Profiles
                .FirstOrDefaultAsync(p => p.Id == model.Id && p.UserEmail == CurrentEmail);
            if (profile == null) return RedirectToAction(nameof(Index));

            var trimmedName = (model.Name ?? "").Trim();

            var nameExists = await _db.Profiles
                .AnyAsync(p => p.UserEmail == CurrentEmail &&
                               p.Id != profile.Id &&
                               p.Name.ToLower() == trimmedName.ToLower());
            if (nameExists)
            {
                ModelState.AddModelError(nameof(model.Name),
                    "Ai deja un profil cu acest nume. Alege altul.");
                return View("Form", model);
            }

            profile.Name = trimmedName;
            profile.Relationship = string.IsNullOrWhiteSpace(model.Relationship) ? null : model.Relationship.Trim();
            profile.Gender = string.IsNullOrWhiteSpace(model.Gender) ? null : model.Gender.Trim();
            profile.BirthYear = model.BirthYear;
            profile.Notes = string.IsNullOrWhiteSpace(model.Notes) ? null : model.Notes.Trim();
            profile.CardiovascularRisk = NormalizeCvRisk(model.CardiovascularRisk);

            await _db.SaveChangesAsync();
            TempData["SuccessMessage"] = $"Profilul \"{trimmedName}\" a fost actualizat.";
            return RedirectToAction(nameof(Index));
        }

        // ====================================================================
        // DELETE
        // ====================================================================
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Delete(int id)
        {
            if (string.IsNullOrEmpty(CurrentEmail))
                return RedirectToAction("Index", "Home");

            var profile = await _db.Profiles
                .FirstOrDefaultAsync(p => p.Id == id && p.UserEmail == CurrentEmail);
            if (profile == null) return RedirectToAction(nameof(Index));

            if (profile.IsDefault)
            {
                TempData["ErrorMessage"] = Loc.T("ErrDefaultProfileCannotDelete");
                return RedirectToAction(nameof(Index));
            }

            _db.Profiles.Remove(profile);
            await _db.SaveChangesAsync();

            TempData["SuccessMessage"] = string.Format(Loc.T("OkProfileDeleted"), profile.Name);
            return RedirectToAction(nameof(Index));
        }

        /// <summary>
        /// Validates and normalizes the cardiovascular-risk dropdown value.
        /// Accepts only the three known categories; everything else (including the
        /// "unknown" placeholder) is mapped to null so the AI prompt can fall back
        /// to its multi-threshold rule.
        /// </summary>
        private static string? NormalizeCvRisk(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            var v = raw.Trim().ToLowerInvariant();
            return v switch
            {
                "low_moderate" => "low_moderate",
                "high"         => "high",
                "very_high"    => "very_high",
                _              => null
            };
        }

        /// <summary>
        /// Anti-abuse gate helper (Feb 2026). Encapsulates the two DB reads
        /// required to consult <see cref="ProfileGateService"/>: the current
        /// user (for UserType + paid credit balance) and the count of profiles
        /// they already own. Returns false when the current user context is
        /// broken (session cleared, user row deleted) — safest default.
        /// </summary>
        private async Task<bool> UserCanCreateAnotherProfileAsync()
        {
            if (string.IsNullOrEmpty(CurrentEmail)) return false;
            var user = await _db.Users.AsNoTracking()
                .FirstOrDefaultAsync(u => u.Email == CurrentEmail);
            if (user == null) return false;
            var profileCount = await _db.Profiles
                .CountAsync(p => p.UserEmail == CurrentEmail);
            return ProfileGateService.CanCreateAdditionalProfile(user, profileCount);
        }
    }
}
