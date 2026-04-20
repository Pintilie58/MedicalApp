using MedicalApp.Data;
using MedicalApp.Models;
using MedicalApp.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Globalization;

namespace MedicalApp.Controllers
{
    public class InterpretationController : Controller
    {
        private readonly AppDbContext _db;
        private readonly IMedicalInterpretationService _ai;
        private readonly IEmailService _emailService;
        private readonly PdfReportGenerator _pdfGenerator;
        private readonly ILogger<InterpretationController> _logger;

        private const long MaxFileSize = 10 * 1024 * 1024; // 10 MB

        public InterpretationController(
            AppDbContext db,
            IMedicalInterpretationService ai,
            IEmailService emailService,
            PdfReportGenerator pdfGenerator,
            ILogger<InterpretationController> logger)
        {
            _db = db;
            _ai = ai;
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

            if (user.CreditRest <= 0)
            {
                TempData["ErrorMessage"] = Loc.T("NoCreditsBody");
                return RedirectToAction("Buy", "Credits");
            }

            ViewBag.CreditRest = user.CreditRest;
            return View(new InterpretationUploadViewModel());
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

            if (user.CreditRest <= 0)
            {
                TempData["ErrorMessage"] = Loc.T("NoCreditsBody");
                return RedirectToAction("Buy", "Credits");
            }

            if (model.PdfFile == null || model.PdfFile.Length == 0)
            {
                ModelState.AddModelError(nameof(model.PdfFile), Loc.T("PdfFileRequired"));
                ViewBag.CreditRest = user.CreditRest;
                return View(model);
            }

            if (model.PdfFile.Length > MaxFileSize)
            {
                ModelState.AddModelError(nameof(model.PdfFile), Loc.T("FileTooLarge"));
                ViewBag.CreditRest = user.CreditRest;
                return View(model);
            }

            if (!model.PdfFile.ContentType.Contains("pdf", StringComparison.OrdinalIgnoreCase)
                && !model.PdfFile.FileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
            {
                ModelState.AddModelError(nameof(model.PdfFile), Loc.T("OnlyPdfAllowed"));
                ViewBag.CreditRest = user.CreditRest;
                return View(model);
            }

            var languageCode = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
            var originalFileName = Path.GetFileName(model.PdfFile.FileName);

            // 1) Extract text
            string extractedText;
            try
            {
                using var stream = model.PdfFile.OpenReadStream();
                extractedText = PdfTextExtractor.Extract(stream);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to extract text from PDF");
                await SaveHistory(user.Email, originalFileName, languageCode, "error", ex.Message, 0, null, null);
                TempData["ErrorMessage"] = Loc.T("PdfExtractFailed");
                return RedirectToAction(nameof(Upload));
            }

            if (string.IsNullOrWhiteSpace(extractedText) || extractedText.Length < 50)
            {
                await SaveHistory(user.Email, originalFileName, languageCode, "rejected", "Empty or too short", 0, null, null);
                TempData["ErrorMessage"] = Loc.T("PdfEmptyText");
                return RedirectToAction(nameof(Upload));
            }

            // 2) Call OpenAI for interpretation
            InterpretationResult result;
            int inputTokens, outputTokens;
            try
            {
                (result, inputTokens, outputTokens) = await _ai.InterpretAsync(extractedText, languageCode);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "OpenAI interpretation failed");
                await SaveHistory(user.Email, originalFileName, languageCode, "error", ex.Message, 0, null, null);
                TempData["ErrorMessage"] = Loc.T("InterpretationFailed");
                return RedirectToAction(nameof(Upload));
            }

            // 3) If non-medical, reject without consuming credit
            if (!result.IsMedicalAnalysis)
            {
                await SaveHistory(user.Email, originalFileName, languageCode, "rejected", result.RejectionReason, 0, inputTokens, outputTokens);
                TempData["ErrorMessage"] = string.Format(Loc.T("NotMedicalAnalysisMessage"),
                    result.RejectionReason ?? Loc.T("UnknownReason"));
                return RedirectToAction(nameof(Upload));
            }

            // 4) Generate PDF report
            byte[] pdfBytes;
            try
            {
                pdfBytes = _pdfGenerator.Generate(result, BuildLabels(languageCode));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "PDF generation failed");
                await SaveHistory(user.Email, originalFileName, languageCode, "error", ex.Message, 0, inputTokens, outputTokens);
                TempData["ErrorMessage"] = Loc.T("PdfGenerationFailed");
                return RedirectToAction(nameof(Upload));
            }

            // 5) Send email with attachment
            try
            {
                var subject = Loc.T("ResultEmailSubject");
                var htmlBody = BuildEmailBody(originalFileName);
                await _emailService.SendEmailWithAttachmentAsync(
                    user.Email, subject, htmlBody, pdfBytes,
                    $"MedicalApp_Interpretation_{DateTime.Now:yyyyMMdd_HHmmss}.pdf");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Sending result email failed");
                await SaveHistory(user.Email, originalFileName, languageCode, "error", ex.Message, 0, inputTokens, outputTokens);
                TempData["ErrorMessage"] = Loc.T("EmailSendFailed");
                return RedirectToAction(nameof(Upload));
            }

            // 6) Consume 1 credit (SUCCESS)
            user.CreditConsum += 1;
            user.CreditRest = user.Credite - user.CreditConsum;
            await _db.SaveChangesAsync();

            await SaveHistory(user.Email, originalFileName, languageCode, "success", null, 1, inputTokens, outputTokens);

            TempData["SuccessMessage"] = Loc.T("InterpretationEmailedSuccess");
            return RedirectToAction("Dashboard", "Account");
        }

        private async Task SaveHistory(string email, string? file, string lang, string status,
            string? errorMsg, int credits, int? inTok, int? outTok)
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
                CreatedAt = DateTime.UtcNow
            });
            await _db.SaveChangesAsync();
        }

        private static string BuildEmailBody(string? originalFileName)
        {
            var greeting = Loc.T("EmailGreeting");
            var intro = Loc.T("ResultEmailIntro");
            var attached = Loc.T("ResultEmailAttachedNote");
            var tagline = Loc.T("Tagline");
            var regards = Loc.T("EmailRegards");
            return $@"
<div style='font-family: Arial, sans-serif; max-width: 600px; margin: 0 auto; padding: 20px;'>
    <h2 style='color: #0d47a1;'>MedicalApp</h2>
    <p>{greeting}</p>
    <p>{intro}</p>
    <p style='color: #6c757d; font-size: 0.9em;'>{attached}</p>
    <p style='font-style: italic; color: #0d47a1;'>{tagline}</p>
    <hr style='border: none; border-top: 1px solid #dee2e6; margin: 20px 0;' />
    <p style='color: #6c757d; font-size: 0.9em;'>{regards}</p>
    <p style='color: #0d47a1; font-weight: bold;'>www.MedicalApp.com</p>
</div>";
        }

        private static LocalizedLabels BuildLabels(string _)
            => new()
            {
                BrandTitle = "MedicalApp",
                BrandSubtitle = Loc.T("BrandSubtitle"),
                Tagline = Loc.T("Tagline"),
                PatientInfo = Loc.T("PatientInfoSection"),
                Name = Loc.T("PatientName"),
                Age = Loc.T("PatientAge"),
                Sex = Loc.T("PatientSex"),
                DateTaken = Loc.T("DateTaken"),
                Laboratory = Loc.T("Laboratory"),
                DoctorRequesting = Loc.T("DoctorRequesting"),
                Summary = Loc.T("SummarySection"),
                KeyResults = Loc.T("KeyResultsSection"),
                Parameter = Loc.T("Parameter"),
                Value = Loc.T("ValueLabel"),
                Reference = Loc.T("ReferenceRange"),
                Status = Loc.T("Status"),
                AbnormalFindings = Loc.T("AbnormalFindingsSection"),
                Correlations = Loc.T("CorrelationsSection"),
                Recommendations = Loc.T("RecommendationsSection"),
                Disclaimer = Loc.T("DisclaimerSection"),
                GeneratedOn = Loc.T("GeneratedOn"),
                Page = Loc.T("Page")
            };
    }
}
