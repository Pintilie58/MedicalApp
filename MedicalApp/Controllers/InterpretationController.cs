using MedicalApp.Data;
using MedicalApp.Models;
using MedicalApp.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using System.Globalization;
using System.Net.Http;

namespace MedicalApp.Controllers
{
    public class InterpretationController : Controller
    {
        private readonly AppDbContext _db;
        private readonly IMedicalInterpretationProvider _ai;
        private readonly InterpretationSettings _interpretationSettings;
        private readonly IEmailService _emailService;
        private readonly PdfReportGenerator _pdfGenerator;
        private readonly ILogger<InterpretationController> _logger;

        private const long MaxFileSize = 10 * 1024 * 1024; // 10 MB

        public InterpretationController(
            AppDbContext db,
            IMedicalInterpretationProvider ai,
            IOptions<InterpretationSettings> interpretationOptions,
            IEmailService emailService,
            PdfReportGenerator pdfGenerator,
            ILogger<InterpretationController> logger)
        {
            _db = db;
            _ai = ai;
            _interpretationSettings = interpretationOptions.Value;
            _emailService = emailService;
            _pdfGenerator = pdfGenerator;
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
        public async Task<IActionResult> Upload(InterpretationUploadViewModel model)
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

            var languageCode = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
            var originalFileName = Path.GetFileName(model.PdfFile.FileName);
            var providerName = (_interpretationSettings.Provider ?? "Gemini").Trim();
            var useGemini = !string.Equals(providerName, "OpenAI", StringComparison.OrdinalIgnoreCase);

            // 1) Read the PDF stream into memory once - we need it twice
            //    (a) for the AI provider, (b) for OpenAI's text-extraction path,
            //    plus we keep it as DEBUG attachment when using Gemini.
            byte[] pdfBytes;
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
                await SaveHistory(user.Email, originalFileName, languageCode, "error", ex.Message, 0, null, null, profile.Id);
                TempData["ErrorMessage"] = Loc.T("PdfExtractFailed");
                return RedirectToAction(nameof(Upload));
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

            const int maxAttempts = 3;
            int attempt = 0;
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
                            await _ai.InterpretPdfAsync(pdfMs, originalFileName, languageCode);
                    }
                    else
                    {
                        (result, inputTokens, outputTokens, rawGptResponse) =
                            await _ai.InterpretAsync(extractedText, languageCode);
                    }
                    break; // success
                }
                catch (OperationCanceledException ex) when (attempt < maxAttempts)
                {
                    _logger.LogWarning(ex,
                        "{Provider} call timed out (attempt {Attempt}/{Max}). Retrying...",
                        providerName, attempt, maxAttempts);
                    lastException = ex;
                    await Task.Delay(2000 * attempt); // 2s, 4s
                }
                catch (HttpRequestException ex) when (attempt < maxAttempts)
                {
                    _logger.LogWarning(ex,
                        "{Provider} HTTP error (attempt {Attempt}/{Max}). Retrying...",
                        providerName, attempt, maxAttempts);
                    lastException = ex;
                    await Task.Delay(2000 * attempt);
                }
                catch (InvalidOperationException ex) when (attempt < maxAttempts)
                {
                    // Transient model-side issues: malformed JSON, truncated output (MAX_TOKENS),
                    // empty response, etc. Retrying often succeeds because the model produces a
                    // different output on the next call.
                    _logger.LogWarning(ex,
                        "{Provider} produced an invalid/truncated response (attempt {Attempt}/{Max}). Retrying... Reason: {Reason}",
                        providerName, attempt, maxAttempts, ex.Message);
                    lastException = ex;
                    await Task.Delay(1500 * attempt); // 1.5s, 3s
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "{Provider} interpretation failed after {Attempt} attempt(s)",
                        providerName, attempt);
                    await SaveHistory(user.Email, originalFileName, languageCode, "error", ex.Message, 0, null, null, profile.Id);
                    var msgKey = (ex is OperationCanceledException || ex is HttpRequestException)
                        ? "InterpretationTimeout"
                        : "InterpretationFailed";
                    TempData["ErrorMessage"] = Loc.T(msgKey);
                    return RedirectToAction(nameof(Upload));
                }
            }

            // 3) If non-medical, reject without consuming credit
            if (!result.IsMedicalAnalysis)
            {
                await SaveHistory(user.Email, originalFileName, languageCode, "rejected", result.RejectionReason, 0, inputTokens, outputTokens, profile.Id, rawGptResponse);
                TempData["ErrorMessage"] = string.Format(Loc.T("NotMedicalAnalysisMessage"),
                    result.RejectionReason ?? Loc.T("UnknownReason"));
                return RedirectToAction(nameof(Upload));
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

            await SaveHistory(user.Email, originalFileName, languageCode, "success", null, 1, inputTokens, outputTokens, profile.Id, rawGptResponse);

            TempData["SuccessMessage"] = Loc.T("InterpretationEmailedSuccess");
            return RedirectToAction("Dashboard", "Account");
        }

        private async Task SaveHistory(string email, string? file, string lang, string status,
            string? errorMsg, int credits, int? inTok, int? outTok, int? profileId = null,
            string? rawJson = null)
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
                CreatedAt = DateTime.UtcNow
            });
            await _db.SaveChangesAsync();
        }

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
