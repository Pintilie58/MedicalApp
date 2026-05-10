using MedicalApp.Data;
using MedicalApp.Models;
using MedicalApp.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using System.Globalization;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace MedicalApp.Controllers
{
    public class InterpretationController : Controller
    {
        private readonly AppDbContext _db;
        private readonly IMedicalInterpretationProvider _ai;
        private readonly InterpretationSettings _interpretationSettings;
        private readonly IEmailService _emailService;
        private readonly PdfReportGenerator _pdfGenerator;
        private readonly IMemoryCache _cache;
        private readonly ILogger<InterpretationController> _logger;

        private const long MaxFileSize = 10 * 1024 * 1024; // 10 MB
        private const string DupCacheKeyPrefix = "dup_pdf:";
        private static readonly TimeSpan DupCacheLifetime = TimeSpan.FromMinutes(15);

        public InterpretationController(
            AppDbContext db,
            IMedicalInterpretationProvider ai,
            IOptions<InterpretationSettings> interpretationOptions,
            IEmailService emailService,
            PdfReportGenerator pdfGenerator,
            IMemoryCache cache,
            ILogger<InterpretationController> logger)
        {
            _db = db;
            _ai = ai;
            _interpretationSettings = interpretationOptions.Value;
            _emailService = emailService;
            _pdfGenerator = pdfGenerator;
            _cache = cache;
            _logger = logger;
        }

        private string? CurrentEmail => HttpContext.Session.GetString("UserEmail");

        [HttpGet]
        public async Task<IActionResult> Upload()
        {
            if (string.IsNullOrEmpty(CurrentEmail))
                return RedirectToAction("Index", "Home");

            var user = await _db.Users.FirstOrDefaultAsync(u => u.Email == CurrentEmail);
            if (user == null)
            {
                HttpContext.Session.Clear();
                return RedirectToAction("Index", "Home");
            }

            if (user.CreditRest <= 0 && user.BonusCreditsRemaining <= 0)
            {
                TempData["ErrorMessage"] = Loc.T("NoCreditsBody");
                return RedirectToAction("Buy", "Credits");
            }

            ViewBag.CreditRest = user.CreditRest;
            ViewBag.BonusCreditsRemaining = user.BonusCreditsRemaining;
            ViewBag.TotalAvailableCredits = user.TotalAvailableCredits;

            // Load user's profiles for the dropdown.
            var profiles = await _db.Profiles
                .AsNoTracking()
                .Where(p => p.UserEmail == user.Email)
                .OrderByDescending(p => p.IsDefault)
                .ThenBy(p => p.Name)
                .Select(p => new InterpretationUploadViewModel.ProfileOption
                {
                    Id = p.Id,
                    Name = p.Name,
                    IsDefault = p.IsDefault
                })
                .ToListAsync();

            var defaultId = profiles.FirstOrDefault(p => p.IsDefault)?.Id
                         ?? profiles.FirstOrDefault()?.Id;

            return View(new InterpretationUploadViewModel
            {
                AvailableProfiles = profiles,
                ProfileId = defaultId
            });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        [RequestSizeLimit(MaxFileSize)]
        public async Task<IActionResult> Upload(InterpretationUploadViewModel model, bool force = false, string? reuploadToken = null)
        {
            if (string.IsNullOrEmpty(CurrentEmail))
                return RedirectToAction("Index", "Home");

            var user = await _db.Users.FirstOrDefaultAsync(u => u.Email == CurrentEmail);
            if (user == null)
            {
                HttpContext.Session.Clear();
                return RedirectToAction("Index", "Home");
            }

            if (user.CreditRest <= 0 && user.BonusCreditsRemaining <= 0)
            {
                TempData["ErrorMessage"] = Loc.T("NoCreditsBody");
                return RedirectToAction("Buy", "Credits");
            }

            // Validate ProfileId - must exist and belong to the current user.
            var profile = model.ProfileId.HasValue
                ? await _db.Profiles.FirstOrDefaultAsync(p =>
                    p.Id == model.ProfileId.Value && p.UserEmail == user.Email)
                : null;

            if (profile == null)
            {
                ModelState.AddModelError(nameof(model.ProfileId),
                    "Te rugăm să selectezi un profil valid.");
                await RepopulateFormViewBags(user, model);
                return View(model);
            }

            // Obtain the PDF bytes. Two sources:
            //   1) Normal upload path: bytes come from model.PdfFile.
            //   2) "Force re-interpret" path (user clicked the button on the
            //      duplicate-detected page): bytes were cached under reuploadToken.
            byte[] pdfBytes;
            string originalFileName;

            if (!string.IsNullOrWhiteSpace(reuploadToken)
                && _cache.TryGetValue<CachedUpload>(DupCacheKeyPrefix + reuploadToken, out var cached)
                && cached != null
                && cached.UserEmail == user.Email
                && cached.ProfileId == profile.Id)
            {
                pdfBytes = cached.PdfBytes;
                originalFileName = cached.FileName;
                // One-shot: consume so the token cannot be reused.
                _cache.Remove(DupCacheKeyPrefix + reuploadToken);
            }
            else
            {
                if (model.PdfFile == null || model.PdfFile.Length == 0)
                {
                    ModelState.AddModelError(nameof(model.PdfFile), Loc.T("PdfFileRequired"));
                    await RepopulateFormViewBags(user, model);
                    return View(model);
                }

                if (model.PdfFile.Length > MaxFileSize)
                {
                    ModelState.AddModelError(nameof(model.PdfFile), Loc.T("FileTooLarge"));
                    await RepopulateFormViewBags(user, model);
                    return View(model);
                }

                if (!model.PdfFile.ContentType.Contains("pdf", StringComparison.OrdinalIgnoreCase)
                    && !model.PdfFile.FileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
                {
                    ModelState.AddModelError(nameof(model.PdfFile), Loc.T("OnlyPdfAllowed"));
                    await RepopulateFormViewBags(user, model);
                    return View(model);
                }

                originalFileName = Path.GetFileName(model.PdfFile.FileName);

                try
                {
                    using var stream = model.PdfFile.OpenReadStream();
                    using var ms = new MemoryStream();
                    await stream.CopyToAsync(ms);
                    pdfBytes = ms.ToArray();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to read uploaded PDF");
                    await SaveHistory(user.Email, null, null, "error", ex.Message, 0, null, null, profile.Id, null, null);
                    TempData["ErrorMessage"] = Loc.T("PdfExtractFailed");
                    return RedirectToAction(nameof(Upload));
                }
            }

            var languageCode = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
            var providerName = (_interpretationSettings.Provider ?? "Gemini").Trim();
            var useGemini = !string.Equals(providerName, "OpenAI", StringComparison.OrdinalIgnoreCase);

            // Compute SHA-256 hash of the uploaded PDF for duplicate detection.
            string pdfHash = ComputeSha256(pdfBytes);

            // If the user did not explicitly force a re-interpretation, check
            // whether the exact same PDF (by hash) was already interpreted
            // successfully for this SAME profile.
            if (!force)
            {
                var dup = await _db.InterpretationHistories
                    .AsNoTracking()
                    .Where(h => h.UserEmail == user.Email
                                && h.ProfileId == profile.Id
                                && h.Status == "success"
                                && h.PdfSha256 == pdfHash
                                && h.RawJsonResult != null)
                    .OrderByDescending(h => h.CreatedAt)
                    .Select(h => new { h.Id, h.CreatedAt, h.OriginalFileName })
                    .FirstOrDefaultAsync();

                if (dup != null)
                {
                    // Cache the PDF bytes under a short-lived token so the user can
                    // force a re-interpretation with a single click, without being
                    // asked to re-select the file.
                    var token = Guid.NewGuid().ToString("N");
                    _cache.Set(DupCacheKeyPrefix + token,
                        new CachedUpload(user.Email, profile.Id, pdfBytes, originalFileName),
                        DupCacheLifetime);

                    var dupVm = new DuplicateDetectedViewModel
                    {
                        ExistingHistoryId = dup.Id,
                        ExistingCreatedAt = dup.CreatedAt,
                        ExistingFileName = dup.OriginalFileName,
                        ProfileId = profile.Id,
                        ProfileName = profile.Name,
                        OriginalFileName = originalFileName,
                        ReuploadToken = token
                    };
                    return View("DuplicateDetected", dupVm);
                }
            }

            // For the OpenAI path we also need a text extraction.
            // For the Gemini path we still extract text - purely as a DEBUG attachment.
            string extractedText;
            try
            {
                using var ms = new MemoryStream(pdfBytes);
                extractedText = PdfTextExtractor.Extract(ms);
            }
            catch (Exception ex)
            {
                if (!useGemini)
                {
                    // OpenAI path needs the text - hard fail
                    _logger.LogError(ex, "Failed to extract text from PDF (OpenAI path)");
                    await SaveHistory(user.Email, originalFileName, languageCode, "error", ex.Message, 0, null, null, profile.Id);
                    TempData["ErrorMessage"] = Loc.T("PdfExtractFailed");
                    return RedirectToAction(nameof(Upload));
                }
                // Gemini path - text is only for debug, swallow the error
                _logger.LogWarning(ex, "PdfTextExtractor failed (Gemini path - non-fatal). Continuing without DEBUG text.");
                extractedText = "(text extraction failed - Gemini reads the PDF directly)";
            }

            if (!useGemini && (string.IsNullOrWhiteSpace(extractedText) || extractedText.Length < 50))
            {
                await SaveHistory(user.Email, originalFileName, languageCode, "rejected", "Empty or too short", 0, null, null, profile.Id);
                TempData["ErrorMessage"] = Loc.T("PdfEmptyText");
                return RedirectToAction(nameof(Upload));
            }

            // 2) Call AI provider for interpretation - with auto-retry on transient errors
            InterpretationResult result;
            int inputTokens, outputTokens;
            string rawGptResponse;

            // Build the patient context the AI uses to pick risk-tiered lipid targets.
            int? ageYears = profile.BirthYear.HasValue
                ? Math.Max(0, DateTime.UtcNow.Year - profile.BirthYear.Value)
                : (int?)null;
            var patientCtx = new PatientContext(
                CardiovascularRisk: profile.CardiovascularRisk,
                AgeYears: ageYears,
                Gender: profile.Gender);

            // Two distinct retry budgets:
            //  * maxAttemptsTransient: for upstream overload / rate-limit (HTTP 429/503).
            //    These need long backoffs (Google needs time to free capacity) so we
            //    retry up to 5 times with 5s, 15s, 30s, 60s pauses (~ 110s total).
            //  * maxAttemptsModel: for model-side issues (malformed JSON, truncated
            //    output, audit mismatch). 3 attempts is plenty - retrying does not
            //    help if the model genuinely cannot produce a valid output.
            const int maxAttemptsTransient = 5;
            const int maxAttemptsModel = 3;
            int attempt = 0;
            int transientAttempts = 0;
            int modelAttempts = 0;
            Exception? lastException = null;

            while (true)
            {
                attempt++;
                try
                {
                    if (useGemini)
                    {
                        using var pdfMs = new MemoryStream(pdfBytes);
                        (result, inputTokens, outputTokens, rawGptResponse) =
                            await _ai.InterpretPdfAsync(pdfMs, originalFileName, languageCode, patientCtx);
                    }
                    else
                    {
                        (result, inputTokens, outputTokens, rawGptResponse) =
                            await _ai.InterpretAsync(extractedText, languageCode);
                    }
                    break; // success
                }
                catch (GeminiTransientException ex) when (transientAttempts + 1 < maxAttemptsTransient)
                {
                    transientAttempts++;
                    // Progressive backoff for upstream overload: 5s, 15s, 30s, 60s
                    int[] delaysMs = { 5_000, 15_000, 30_000, 60_000 };
                    int wait = delaysMs[Math.Min(transientAttempts - 1, delaysMs.Length - 1)];
                    _logger.LogWarning(
                        "Gemini upstream transient {Status} (try {N}/{Max}). Backing off {Wait} ms.",
                        ex.HttpStatusCode, transientAttempts, maxAttemptsTransient, wait);
                    lastException = ex;
                    await Task.Delay(wait);
                }
                catch (OperationCanceledException ex) when (transientAttempts + 1 < maxAttemptsTransient)
                {
                    transientAttempts++;
                    _logger.LogWarning(ex,
                        "{Provider} call timed out (transient try {N}/{Max}). Retrying...",
                        providerName, transientAttempts, maxAttemptsTransient);
                    lastException = ex;
                    await Task.Delay(5_000 * transientAttempts); // 5s, 10s, 15s, 20s
                }
                catch (HttpRequestException ex) when (transientAttempts + 1 < maxAttemptsTransient)
                {
                    transientAttempts++;
                    _logger.LogWarning(ex,
                        "{Provider} HTTP error (transient try {N}/{Max}). Retrying...",
                        providerName, transientAttempts, maxAttemptsTransient);
                    lastException = ex;
                    await Task.Delay(5_000 * transientAttempts);
                }
                catch (InvalidOperationException ex) when (modelAttempts + 1 < maxAttemptsModel)
                {
                    modelAttempts++;
                    // Model-side issues: malformed JSON, truncated output (MAX_TOKENS),
                    // audit mismatch, empty response. These often succeed on a fresh call.
                    _logger.LogWarning(ex,
                        "{Provider} produced an invalid response (model try {N}/{Max}). Retrying... Reason: {Reason}",
                        providerName, modelAttempts, maxAttemptsModel, ex.Message);
                    lastException = ex;
                    await Task.Delay(1500 * modelAttempts); // 1.5s, 3s
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "{Provider} interpretation failed (transient={T}/{TMax}, model={M}/{MMax})",
                        providerName, transientAttempts, maxAttemptsTransient, modelAttempts, maxAttemptsModel);

                    bool isTransient = ex is GeminiTransientException
                                    || ex is OperationCanceledException
                                    || ex is HttpRequestException;

                    // For transient upstream failures (Google overloaded, network issues),
                    // do NOT save a noisy "error" row in the user's archive. The user did
                    // not produce a bad PDF - Google was busy. Logged in the file only.
                    if (!isTransient)
                    {
                        await SaveHistory(user.Email, originalFileName, languageCode, "error",
                            ex.Message, 0, null, null, profile.Id);
                    }

                    string msgKey;
                    if (ex is GeminiTransientException gex)
                    {
                        msgKey = gex.HttpStatusCode == 429
                            ? "AiRateLimited"
                            : "AiOverloaded";
                    }
                    else if (ex is OperationCanceledException || ex is HttpRequestException)
                    {
                        msgKey = "InterpretationTimeout";
                    }
                    else
                    {
                        msgKey = "InterpretationFailed";
                    }
                    TempData["ErrorMessage"] = Loc.T(msgKey);
                    return RedirectToAction(nameof(Upload));
                }
            }

            // 3) If non-medical, reject without consuming credit
            if (!result.IsMedicalAnalysis)
            {
                await SaveHistory(user.Email, originalFileName, languageCode, "rejected", result.RejectionReason, 0, inputTokens, outputTokens, profile.Id, rawGptResponse, pdfHash);
                TempData["ErrorMessage"] = string.Format(Loc.T("NotMedicalAnalysisMessage"),
                    result.RejectionReason ?? Loc.T("UnknownReason"));
                return RedirectToAction(nameof(Upload));
            }

            // 3.5) POST-LLM mathematical validator.
            // Gemini occasionally labels a value that is inside its reference range
            // as "high"/"low" even when it has correctly extracted both the value
            // and the range (LLM math hallucination). The validator re-computes
            // every status from the value+range in plain C#. Parameters whose
            // value or range cannot be parsed (e.g. "negative", "see comment",
            // or multi-tier text ranges like "Desirable: <200 Borderline 200-239")
            // are SKIPPED - their status from the model is preserved untouched.
            var stats = StatusValidator.Validate(result, _logger);
            _logger.LogInformation(
                "StatusValidator: parsed {Total} parameter(s), corrected {Corrected}, skipped (unparseable value/range) {Skipped}.",
                stats.Total, stats.Corrected, stats.Skipped);

            // Re-serialize the corrected InterpretationResult so the JSON we
            // persist in the DB (RawJsonResult) and ship as DEBUG attachment
            // reflects the mathematically-corrected statuses. This is critical
            // for the upcoming P1.6 denormalization (AnalysisResults table) and
            // P1.8 evolution charts, which both read from RawJsonResult.
            if (stats.Corrected > 0)
            {
                rawGptResponse = JsonSerializer.Serialize(result, new JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                    DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
                });
            }

            // 4) Generate PDF report
            byte[] reportPdfBytes;
            try
            {
                reportPdfBytes = _pdfGenerator.Generate(result, BuildLabels(languageCode));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "PDF generation failed");
                await SaveHistory(user.Email, originalFileName, languageCode, "error", ex.Message, 0, inputTokens, outputTokens, profile.Id);
                TempData["ErrorMessage"] = Loc.T("PdfGenerationFailed");
                return RedirectToAction(nameof(Upload));
            }

            // 5) Send email with attachment (+ debug attachments: extracted text and raw GPT JSON)
            try
            {
                // Prefix subject with the profile name so inbox is easier to scan when user has many profiles.
                var subject = $"[{profile.Name}] " + Loc.T("ResultEmailSubject");
                var htmlBody = BuildEmailBody(originalFileName, profile.Name);
                var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");

                var attachments = new List<(byte[] Bytes, string FileName, string MimeType)>
                {
                    (reportPdfBytes,
                        $"MedicalApp_Interpretation_{timestamp}.pdf",
                        "application/pdf"),

                    // DEBUG #1 – exactly what we extracted from the user's PDF BEFORE sending to GPT.
                    (System.Text.Encoding.UTF8.GetBytes(extractedText ?? string.Empty),
                        $"DEBUG_01_extracted_text_{timestamp}.txt",
                        "text/plain"),

                    // DEBUG #2 – raw JSON returned by the AI provider, exactly as received (before deserialization).
                    (System.Text.Encoding.UTF8.GetBytes(rawGptResponse ?? string.Empty),
                        $"DEBUG_02_{providerName}_raw_response_{timestamp}.json",
                        "application/json"),
                };

                await _emailService.SendEmailWithAttachmentsAsync(
                    user.Email, subject, htmlBody, attachments);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Sending result email failed");
                await SaveHistory(user.Email, originalFileName, languageCode, "error", ex.Message, 0, inputTokens, outputTokens, profile.Id);
                TempData["ErrorMessage"] = Loc.T("EmailSendFailed");
                return RedirectToAction(nameof(Upload));
            }

            // 6) Consume 1 credit (SUCCESS) - bonus first, then paid
            if (user.BonusCreditsRemaining > 0)
            {
                user.BonusCreditsConsumed += 1;
            }
            else
            {
                user.CreditConsum += 1;
                user.CreditRest = user.Credite - user.CreditConsum;
            }
            await _db.SaveChangesAsync();

            await SaveHistory(user.Email, originalFileName, languageCode, "success", null, 1, inputTokens, outputTokens, profile.Id, rawGptResponse, pdfHash);

            // OVERRIDE on force re-interpret: the user explicitly paid for a fresh
            // run, and we want a single canonical row per (user, profile, pdfHash)
            // so charts and aggregations are not polluted with duplicates.
            if (force)
            {
                // Find all earlier success rows with the same hash for this user+profile
                // (excluding the row we just inserted, which is the most recent).
                var stale = await _db.InterpretationHistories
                    .Where(h => h.UserEmail == user.Email
                                && h.ProfileId == profile.Id
                                && h.Status == "success"
                                && h.PdfSha256 == pdfHash)
                    .OrderByDescending(h => h.CreatedAt)
                    .Skip(1) // keep the newest, drop the rest
                    .ToListAsync();

                if (stale.Count > 0)
                {
                    _db.InterpretationHistories.RemoveRange(stale);
                    await _db.SaveChangesAsync();
                    _logger.LogInformation(
                        "Force re-interpret OVERRIDE: removed {Count} stale row(s) with matching hash for {Email}/profile={Pid}.",
                        stale.Count, user.Email, profile.Id);
                }
            }

            TempData["SuccessMessage"] = Loc.T("InterpretationEmailedSuccess");
            return RedirectToAction("Dashboard", "Account");
        }

        private async Task SaveHistory(string email, string? file, string? lang, string status,
            string? errorMsg, int credits, int? inTok, int? outTok, int? profileId = null,
            string? rawJson = null, string? pdfSha256 = null)
        {
            _db.InterpretationHistories.Add(new InterpretationHistory
            {
                UserEmail = email,
                OriginalFileName = file,
                Language = lang,
                Status = status,
                ErrorMessage = errorMsg?.Length > 500 ? errorMsg[..500] : errorMsg,
                CreditsConsumed = credits,
                InputTokens = inTok,
                OutputTokens = outTok,
                ProfileId = profileId,
                RawJsonResult = rawJson,
                PdfSha256 = pdfSha256,
                CreatedAt = DateTime.UtcNow
            });
            await _db.SaveChangesAsync();
        }

        /// <summary>Returns the hex SHA-256 (64 lowercase chars) of the PDF bytes.</summary>
        private static string ComputeSha256(byte[] bytes)
        {
            var hash = SHA256.HashData(bytes);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }

        /// <summary>
        /// Holds the uploaded PDF bytes in memory so the user can trigger a
        /// "force re-interpret" without being asked to re-select the file.
        /// Short-lived (see DupCacheLifetime) and one-shot.
        /// </summary>
        private sealed record CachedUpload(string UserEmail, int ProfileId, byte[] PdfBytes, string FileName);

        /// <summary>Reload dropdown profile list + credit ViewBags when returning View(model) after a validation error.</summary>
        private async Task RepopulateFormViewBags(User user, InterpretationUploadViewModel model)
        {
            ViewBag.CreditRest = user.CreditRest;
            ViewBag.BonusCreditsRemaining = user.BonusCreditsRemaining;
            ViewBag.TotalAvailableCredits = user.TotalAvailableCredits;
            model.AvailableProfiles = await _db.Profiles
                .AsNoTracking()
                .Where(p => p.UserEmail == user.Email)
                .OrderByDescending(p => p.IsDefault)
                .ThenBy(p => p.Name)
                .Select(p => new InterpretationUploadViewModel.ProfileOption
                {
                    Id = p.Id, Name = p.Name, IsDefault = p.IsDefault
                })
                .ToListAsync();
        }

        private static string BuildEmailBody(string? originalFileName, string? profileName = null)
        {
            var greeting = Loc.T("EmailGreeting");
            var intro = Loc.T("ResultEmailIntro");
            var attached = Loc.T("ResultEmailAttachedNote");
            var tagline = Loc.T("Tagline");
            var regards = Loc.T("EmailRegards");
            var profileLine = string.IsNullOrWhiteSpace(profileName)
                ? string.Empty
                : $"<p style='background:#eef5ff;border-left:4px solid #0d47a1;padding:10px 14px;border-radius:6px;margin:16px 0;'>Interpretare pentru profilul: <strong>{System.Net.WebUtility.HtmlEncode(profileName)}</strong></p>";
            return $@"
<div style='font-family: Arial, sans-serif; max-width: 600px; margin: 0 auto; padding: 20px;'>
    <h2 style='color: #0d47a1;'>MedicalApp</h2>
    <p>{greeting}</p>
    {profileLine}
    <p>{intro}</p>
    <p style='color: #6c757d; font-size: 0.9em;'>{attached}</p>
    <p style='font-style: italic; color: #0d47a1;'>{tagline}</p>
    <hr style='border: none; border-top: 1px solid #dee2e6; margin: 20px 0;' />
    <p style='color: #6c757d; font-size: 0.9em;'>{regards}</p>
    <p style='color: #0d47a1; font-weight: bold;'>www.MedicalApp.com</p>
</div>";
        }

        private static LocalizedLabels BuildLabels(string _) => LocalizedLabels.ForCurrentUi();
    }
}
