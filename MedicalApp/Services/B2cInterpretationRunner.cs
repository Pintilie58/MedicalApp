using MedicalApp.Data;
using MedicalApp.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using System.Globalization;
using System.Net.Http;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MedicalApp.Services
{
    /// <summary>
    /// The whole B2C interpretation pipeline, running OUTSIDE the HTTP request:
    /// AI call (with the tiered retry/fallback chain) → status validator →
    /// abnormal-findings completer → LOINC matching → PDF → email → archive row.
    ///
    /// Moved here from InterpretationController (June 2026) so the user is not
    /// held hostage by a 2-4 minute request. Consequences that shaped this code:
    ///   * no HttpContext / session / TempData — everything comes from the job;
    ///   * the cancellation token is the APPLICATION's, never the browser's
    ///     (closing the tab used to abort Gemini mid-flight);
    ///   * the credit was already reserved by the controller, so every failure
    ///     path must refund it;
    ///   * the UI language is set on this thread, otherwise the PDF/email come
    ///     out in the server's default language (same trick as CamBatchService).
    ///
    /// NEVER throws: the history row must always leave "processing".
    /// </summary>
    public class B2cInterpretationRunner
    {
        private readonly AppDbContext _db;
        private readonly IMedicalInterpretationProvider _ai;
        private readonly InterpretationSettings _interpretationSettings;
        private readonly GeminiSettings _geminiSettings;
        private readonly IEmailService _emailService;
        private readonly PdfReportGenerator _pdfGenerator;
        private readonly LoincMatcherClient _loincMatcher;
        private readonly IAiUsageLogger _aiUsage;
        private readonly InterpretationProgressTracker _progress;
        private readonly ILogger<B2cInterpretationRunner> _logger;

        public B2cInterpretationRunner(
            AppDbContext db,
            IMedicalInterpretationProvider ai,
            IOptions<InterpretationSettings> interpretationOptions,
            IOptions<GeminiSettings> geminiOptions,
            IEmailService emailService,
            PdfReportGenerator pdfGenerator,
            LoincMatcherClient loincMatcher,
            IAiUsageLogger aiUsage,
            InterpretationProgressTracker progress,
            ILogger<B2cInterpretationRunner> logger)
        {
            _db = db;
            _ai = ai;
            _interpretationSettings = interpretationOptions.Value;
            _geminiSettings = geminiOptions.Value;
            _emailService = emailService;
            _pdfGenerator = pdfGenerator;
            _loincMatcher = loincMatcher;
            _aiUsage = aiUsage;
            _progress = progress;
            _logger = logger;
        }

        public async Task RunAsync(InterpretationJob job, CancellationToken ct)
        {
            ApplyCulture(job.LanguageCode);

            var history = await _db.InterpretationHistories
                .FirstOrDefaultAsync(h => h.Id == job.HistoryId, CancellationToken.None);
            if (history == null)
            {
                _logger.LogError("Interpretation job: history row {Id} not found.", job.HistoryId);
                return;
            }

            var user = await _db.Users
                .FirstOrDefaultAsync(u => u.Email == job.UserEmail, CancellationToken.None);
            if (user == null)
            {
                await FinishAsync(history, "error", "User no longer exists.", 0, null, null, timer: null);
                return;
            }

            try
            {
                await ExecuteAsync(job, history, user, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Interpretation job {Id} failed unexpectedly.", job.HistoryId);
                await FailAsync(job, history, user, ex.Message, Loc.T("InterpretationFailed", job.LanguageCode));
            }
        }

        // ===================================================================
        //  The pipeline
        // ===================================================================
        private async Task ExecuteAsync(InterpretationJob job, InterpretationHistory history,
                                        User user, CancellationToken ct)
        {
            var timer = new StageTimer();
            var token = job.ProgressToken;
            var languageCode = job.LanguageCode;
            var pdfBytes = job.PdfBytes;
            var originalFileName = job.OriginalFileName;

            var profile = await _db.Profiles
                .FirstOrDefaultAsync(p => p.Id == job.ProfileId, CancellationToken.None);
            if (profile == null)
            {
                await FailAsync(job, history, user, "Profile deleted before the job ran.",
                    Loc.T("InterpretationFailed", languageCode));
                return;
            }

            var providerName = (_interpretationSettings.Provider ?? "Gemini").Trim();
            var useGemini = !string.Equals(providerName, "OpenAI", StringComparison.OrdinalIgnoreCase);

            // 1) Text layer of the PDF: the primary input for Gemini's TEXT mode
            // and a debug aid on the vision path.
            string extractedText;
            var extractSw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                using var ms = new MemoryStream(pdfBytes);
                extractedText = PdfTextExtractor.Extract(ms);
            }
            catch (Exception ex)
            {
                if (!useGemini)
                {
                    _logger.LogError(ex, "Failed to extract text from PDF (OpenAI path)");
                    await FailAsync(job, history, user, ex.Message,
                        Loc.T("PdfExtractFailed", languageCode));
                    return;
                }
                _logger.LogWarning(ex, "PdfTextExtractor failed (Gemini path - non-fatal).");
                extractedText = "(text extraction failed - Gemini reads the PDF directly)";
            }
            timer.Add("pdf_extract", extractSw.ElapsedMilliseconds);
            _progress.SetStage(token, "ai_extract");

            if (!useGemini && (string.IsNullOrWhiteSpace(extractedText) || extractedText.Length < 50))
            {
                await FailAsync(job, history, user, "Empty or too short",
                    Loc.T("PdfEmptyText", languageCode), status: "rejected");
                return;
            }

            InterpretationResult result;
            int inputTokens, outputTokens;
            string rawGptResponse;

            int? ageYears = profile.BirthYear.HasValue
                ? Math.Max(0, DateTime.UtcNow.Year - profile.BirthYear.Value)
                : (int?)null;
            var patientCtx = new PatientContext(
                CardiovascularRisk: profile.CardiovascularRisk,
                AgeYears: ageYears,
                Gender: profile.Gender);

            // TEXT mode when the PDF has a real text layer (literal digits, no OCR
            // hallucinations); VISION mode for scans / Word-rasterized pages.
            bool extractedTextLooksMedical =
                !string.IsNullOrWhiteSpace(extractedText)
                && extractedText.Length >= 200
                && !extractedText.StartsWith("(text extraction failed")
                && LooksLikeMedicalData(extractedText);

            bool geminiUseTextMode = useGemini && extractedTextLooksMedical;

            if (useGemini && !geminiUseTextMode)
            {
                _logger.LogInformation(
                    "B2C interpretation: switching to VISION mode (too few medical value+unit " +
                    "patterns in the text layer). File: {File}", originalFileName);
            }

            const int maxAttemptsTransient = 5;
            const int maxAttemptsModel = 3;
            const int transientFallbackThreshold = 2;
            int transientAttempts = 0;
            int modelAttempts = 0;

            if (_ai is GeminiMedicalInterpretationService progressAware)
                progressAware.OnStage = (stage, analytes) =>
                {
                    if (stage == "ai_extract_done" && analytes != null)
                        _progress.SetTable(token, analytes);
                    else
                        _progress.SetStage(token, stage);
                };

            string? currentModelOverride = null;
            string? modelsUsedLabel = null;

            while (true)
            {
                var aiSw = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    if (useGemini)
                    {
                        if (geminiUseTextMode)
                        {
                            (result, inputTokens, outputTokens, rawGptResponse) =
                                await _ai.InterpretTextAsync(extractedText, originalFileName,
                                    languageCode, patientCtx, ct, currentModelOverride);
                        }
                        else
                        {
                            using var pdfMs = new MemoryStream(pdfBytes);
                            (result, inputTokens, outputTokens, rawGptResponse) =
                                await _ai.InterpretPdfAsync(pdfMs, originalFileName,
                                    languageCode, patientCtx, ct, currentModelOverride);
                        }
                    }
                    else
                    {
                        (result, inputTokens, outputTokens, rawGptResponse) =
                            await _ai.InterpretAsync(extractedText, languageCode);
                    }
                    timer.Add("ai_calls", aiSw.ElapsedMilliseconds);
                    timer.Add("ai_attempts", 1);
                    if (_ai is GeminiMedicalInterpretationService gem)
                    {
                        timer.Add("ai_thinking_tokens", gem.LastThoughtsTokenCount);
                        if (!string.IsNullOrWhiteSpace(gem.LastModelsUsed))
                            modelsUsedLabel = gem.LastModelsUsed;
                        foreach (var kv in gem.LastStageTimings)
                            timer.Add(kv.Key, kv.Value);
                    }
                    break; // success
                }
                catch (GeminiTransientException ex) when (transientAttempts + 1 < maxAttemptsTransient)
                {
                    transientAttempts++;
                    timer.Add("ai_calls", aiSw.ElapsedMilliseconds);
                    timer.Add("ai_attempts", 1);

                    await LogTransientAsync(user.Email, currentModelOverride,
                        $"HTTP {ex.HttpStatusCode}: {ex.Message}");

                    if (currentModelOverride == null
                        && transientAttempts >= transientFallbackThreshold
                        && !string.IsNullOrWhiteSpace(_geminiSettings.FallbackModel)
                        && !string.Equals(_geminiSettings.FallbackModel, _geminiSettings.Model,
                                          StringComparison.OrdinalIgnoreCase))
                    {
                        currentModelOverride = _geminiSettings.FallbackModel;
                        _logger.LogWarning(
                            "Gemini primary {Primary} hit {Count} consecutive transient {Status}. " +
                            "Switching to FALLBACK {Fallback}.",
                            _geminiSettings.Model, transientAttempts, ex.HttpStatusCode, currentModelOverride);
                    }
                    else if (string.Equals(currentModelOverride, _geminiSettings.FallbackModel,
                                           StringComparison.OrdinalIgnoreCase)
                             && !string.IsNullOrWhiteSpace(_geminiSettings.SecondaryFallbackModel)
                             && !string.Equals(_geminiSettings.SecondaryFallbackModel,
                                               _geminiSettings.FallbackModel, StringComparison.OrdinalIgnoreCase)
                             && !string.Equals(_geminiSettings.SecondaryFallbackModel,
                                               _geminiSettings.Model, StringComparison.OrdinalIgnoreCase))
                    {
                        currentModelOverride = _geminiSettings.SecondaryFallbackModel;
                        _logger.LogWarning(
                            "Gemini FALLBACK also hit transient {Status}. Switching to SECONDARY {Sec}.",
                            ex.HttpStatusCode, currentModelOverride);
                    }

                    int[] delaysMs = { 5_000, 15_000, 30_000, 60_000 };
                    int wait = delaysMs[Math.Min(transientAttempts - 1, delaysMs.Length - 1)];

                    // Google sometimes says exactly how long to wait. Obey it
                    // (never less than our own backoff, never more than 2 min).
                    if (ex.RetryAfter is { TotalMilliseconds: > 0 } advised)
                        wait = (int)Math.Min(Math.Max(wait, advised.TotalMilliseconds), 120_000);

                    _logger.LogWarning(
                        "Gemini transient {Status} (try {N}/{Max}). Backing off {Wait} ms. Model: {Model}.",
                        ex.HttpStatusCode, transientAttempts, maxAttemptsTransient, wait,
                        currentModelOverride ?? _geminiSettings.Model);
                    await Task.Delay(wait, ct);
                }
                catch (GeminiModelRetiredException ex)
                {
                    string? nextModel = null;
                    if (currentModelOverride == null)
                        nextModel = _geminiSettings.FallbackModel;
                    else if (string.Equals(currentModelOverride, _geminiSettings.FallbackModel,
                                           StringComparison.OrdinalIgnoreCase))
                        nextModel = _geminiSettings.SecondaryFallbackModel;

                    if (!string.IsNullOrWhiteSpace(nextModel)
                        && !string.Equals(nextModel, ex.RetiredModelId, StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(nextModel, _geminiSettings.Model, StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(nextModel, currentModelOverride, StringComparison.OrdinalIgnoreCase))
                    {
                        var prev = currentModelOverride ?? _geminiSettings.Model;
                        currentModelOverride = nextModel;
                        _logger.LogWarning(
                            "Gemini model '{Retired}' retired. Promoting {Prev} → {Next} and retrying.",
                            ex.RetiredModelId, prev, currentModelOverride);
                        continue; // does not consume an attempt slot
                    }
                    _logger.LogError(
                        "Gemini model '{Retired}' retired and no usable fallback is configured.",
                        ex.RetiredModelId);
                    throw;
                }
                catch (OperationCanceledException ex) when (!ct.IsCancellationRequested
                                                            && transientAttempts + 1 < maxAttemptsTransient)
                {
                    transientAttempts++;
                    _logger.LogWarning(ex, "{Provider} call timed out (try {N}/{Max}). Retrying...",
                        providerName, transientAttempts, maxAttemptsTransient);
                    await LogTransientAsync(user.Email, currentModelOverride,
                        "Timeout: " + Trim200(ex.Message));
                    await Task.Delay(5_000 * transientAttempts, ct);
                }
                catch (HttpRequestException ex) when (transientAttempts + 1 < maxAttemptsTransient)
                {
                    transientAttempts++;
                    _logger.LogWarning(ex, "{Provider} HTTP error (try {N}/{Max}). Retrying...",
                        providerName, transientAttempts, maxAttemptsTransient);
                    await LogTransientAsync(user.Email, currentModelOverride,
                        "HttpRequestException: " + Trim200(ex.Message));
                    await Task.Delay(5_000 * transientAttempts, ct);
                }
                catch (InvalidOperationException ex) when (
                    ex.Message.Contains("MaxOutputTokens", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(currentModelOverride, _geminiSettings.SecondaryFallbackModel,
                                      StringComparison.OrdinalIgnoreCase))
                {
                    // Truncated output — jump to a model with a bigger output
                    // budget WITHOUT consuming a retry slot.
                    string? nextModel = null;
                    if (currentModelOverride == null)
                        nextModel = _geminiSettings.FallbackModel;
                    else if (string.Equals(currentModelOverride, _geminiSettings.FallbackModel,
                                           StringComparison.OrdinalIgnoreCase))
                        nextModel = _geminiSettings.SecondaryFallbackModel;

                    if (!string.IsNullOrWhiteSpace(nextModel)
                        && !string.Equals(nextModel, currentModelOverride, StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(nextModel, _geminiSettings.Model, StringComparison.OrdinalIgnoreCase))
                    {
                        var prev = currentModelOverride ?? _geminiSettings.Model;
                        currentModelOverride = nextModel;
                        _logger.LogWarning(
                            "Gemini hit MaxOutputTokens on {Prev}; switching to {Next}. Detail: {Detail}",
                            prev, nextModel, ex.Message);
                        continue;
                    }
                    throw;
                }
                catch (InvalidOperationException ex) when (modelAttempts + 1 < maxAttemptsModel)
                {
                    modelAttempts++;
                    _logger.LogWarning(ex,
                        "{Provider} produced an invalid response (model try {N}/{Max}). Reason: {Reason}",
                        providerName, modelAttempts, maxAttemptsModel, ex.Message);

                    // Retrying the SAME model on a dense report repeats the same
                    // JSON error — escalate a tier instead.
                    string? nextModel = null;
                    if (currentModelOverride == null)
                        nextModel = _geminiSettings.FallbackModel;
                    else if (string.Equals(currentModelOverride, _geminiSettings.FallbackModel,
                                           StringComparison.OrdinalIgnoreCase))
                        nextModel = _geminiSettings.SecondaryFallbackModel;

                    if (!string.IsNullOrWhiteSpace(nextModel)
                        && !string.Equals(nextModel, currentModelOverride, StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(nextModel, _geminiSettings.Model, StringComparison.OrdinalIgnoreCase))
                    {
                        var prev = currentModelOverride ?? _geminiSettings.Model;
                        currentModelOverride = nextModel;
                        _logger.LogWarning(
                            "{Provider} JSON/audit failure on {Prev}. Escalating to {Next}.",
                            providerName, prev, currentModelOverride);
                    }

                    await Task.Delay(1500 * modelAttempts, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "{Provider} interpretation failed (transient={T}/{TMax}, model={M}/{MMax})",
                        providerName, transientAttempts, maxAttemptsTransient, modelAttempts, maxAttemptsModel);

                    string msgKey = ex switch
                    {
                        GeminiTransientException gex => gex.HttpStatusCode == 429
                            ? "AiRateLimited" : "AiOverloaded",
                        OperationCanceledException => "InterpretationTimeout",
                        HttpRequestException => "InterpretationTimeout",
                        _ => "InterpretationFailed"
                    };

                    await FailAsync(job, history, user, ex.Message, Loc.T(msgKey, languageCode));
                    return;
                }
            }

            // 2) Non-medical PDF → refund, no charge.
            if (!result.IsMedicalAnalysis)
            {
                history.InputTokens = inputTokens;
                history.OutputTokens = outputTokens;
                history.RawJsonResult = rawGptResponse;
                history.ModelUsed = useGemini
                    ? Trim40(modelsUsedLabel ?? currentModelOverride ?? _geminiSettings.Model)
                    : null;
                await FailAsync(job, history, user, result.RejectionReason,
                    string.Format(Loc.T("NotMedicalAnalysisMessage", languageCode),
                        result.RejectionReason ?? Loc.T("UnknownReason", languageCode)),
                    status: "rejected");
                return;
            }

            bool resultMutated = false;

            // 3) Local clean-up / verification passes. Each one is optional and
            // must never break the flow.
            try { if (LabMarkerSanitizer.Clean(result) > 0) resultMutated = true; }
            catch { /* cosmetic */ }

            try
            {
                var stats = StatusValidator.Validate(result, _logger);
                _logger.LogInformation(
                    "StatusValidator: parsed {Total}, corrected {Corrected}, skipped {Skipped}.",
                    stats.Total, stats.Corrected, stats.Skipped);
                if (stats.Corrected > 0) resultMutated = true;
            }
            catch (Exception valEx)
            {
                _logger.LogWarning(valEx, "StatusValidator threw. Keeping the model's statuses.");
            }

            try
            {
                var addedFindings = AbnormalFindingsCompleter.Complete(result);
                if (addedFindings > 0)
                {
                    resultMutated = true;
                    _logger.LogInformation(
                        "AbnormalFindingsCompleter: added {Added} omitted out-of-range analyte(s).",
                        addedFindings);
                }
            }
            catch (Exception afEx)
            {
                _logger.LogWarning(afEx, "AbnormalFindingsCompleter threw. Keeping the model's list.");
            }

            try
            {
                var rebuilt = RawLineReconstructor.Fill(result, extractedText);
                if (rebuilt > 0)
                    _logger.LogInformation(
                        "RawLineReconstructor: rebuilt {Count} raw analyte line(s) locally.", rebuilt);
            }
            catch (Exception rlEx)
            {
                _logger.LogWarning(rlEx, "RawLineReconstructor threw. Continuing without raw lines.");
            }

            _progress.SetStage(token, "loinc_match");

            // 4) Deterministic LOINC mapping (Python FastAPI microservice).
            try
            {
                var loincSw = System.Diagnostics.Stopwatch.StartNew();
                var matcherStats = await _loincMatcher.MatchAllAsync(result, ct);
                timer.Add("loinc_match", loincSw.ElapsedMilliseconds);
                if (matcherStats.Matched > 0) resultMutated = true;
            }
            catch (Exception lmEx)
            {
                _logger.LogWarning(lmEx,
                    "LoincMatcherClient threw. Continuing without LOINC codes for this interpretation.");
            }

            if (resultMutated)
            {
                rawGptResponse = JsonSerializer.Serialize(result, new JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                    DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
                });
            }

            _progress.SetStage(token, "pdf_report");

            // 5) PDF report.
            bool isFreemium = user.Credite == 0;
            byte[] reportPdfBytes;
            try
            {
                var labels = LocalizedLabels.ForCurrentUi();
                labels.ProcessingMode = useGemini
                    ? Loc.T(geminiUseTextMode ? "ProcessingModeText" : "ProcessingModeVision", languageCode)
                    : "";
                var pdfSw = System.Diagnostics.Stopwatch.StartNew();
                reportPdfBytes = _pdfGenerator.Generate(result, labels, isFreemium);
                timer.Add("pdf_report", pdfSw.ElapsedMilliseconds);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "PDF generation failed");
                history.InputTokens = inputTokens;
                history.OutputTokens = outputTokens;
                await FailAsync(job, history, user, ex.Message,
                    Loc.T("PdfGenerationFailed", languageCode));
                return;
            }

            // 6) Email with the PDF attached.
            try
            {
                var subject = $"[{job.ProfileName}] " + Loc.T("ResultEmailSubject", languageCode);
                var htmlBody = BuildEmailBody(job.ProfileName, languageCode);
                var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                var emailSw = System.Diagnostics.Stopwatch.StartNew();

                var attachments = new List<(byte[] Bytes, string FileName, string MimeType)>
                {
                    (reportPdfBytes, $"MedicalApp_Interpretation_{timestamp}.pdf", "application/pdf")
                };

                await _emailService.SendEmailWithAttachmentsAsync(
                    user.Email, subject, htmlBody, attachments);
                timer.Add("email", emailSw.ElapsedMilliseconds);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Sending result email failed");
                history.InputTokens = inputTokens;
                history.OutputTokens = outputTokens;
                await FailAsync(job, history, user, ex.Message, Loc.T("EmailSendFailed", languageCode));
                return;
            }

            // 7) Success — the credit reserved at launch is now definitively spent.
            history.RawJsonResult = rawGptResponse;
            history.ModelUsed = useGemini
                ? Trim40(modelsUsedLabel ?? currentModelOverride ?? _geminiSettings.Model)
                : null;
            await FinishAsync(history, "success", null, 1, inputTokens, outputTokens, timer);

            _logger.LogInformation(
                "Interpretation TIMING (history {Id}, file {File}): {Timings}",
                history.Id, originalFileName, timer.ToString());

            // Force re-interpret keeps ONE canonical row per (user, profile, hash)
            // so charts are not polluted with duplicates.
            if (job.Force)
            {
                var stale = await _db.InterpretationHistories
                    .Where(h => h.UserEmail == user.Email
                                && h.ProfileId == job.ProfileId
                                && h.Status == "success"
                                && h.PdfSha256 == job.PdfHash
                                && h.Id != history.Id)
                    .ToListAsync(CancellationToken.None);

                if (stale.Count > 0)
                {
                    _db.InterpretationHistories.RemoveRange(stale);
                    await _db.SaveChangesAsync(CancellationToken.None);
                    _logger.LogInformation(
                        "Force re-interpret OVERRIDE: removed {Count} stale row(s) for {Email}/profile={Pid}.",
                        stale.Count, user.Email, job.ProfileId);
                }
            }

            // Where the browser should go when it sees stage = "done".
            var redirectUrl = isFreemium
                ? $"/Profiles/ViewReport/{history.Id}"
                : "/Account/Dashboard";
            _progress.Done(token, redirectUrl, history.Id);
        }

        // ===================================================================
        //  Terminal states
        // ===================================================================
        private async Task FailAsync(InterpretationJob job, InterpretationHistory history,
            User user, string? technicalError, string userMessage, string status = "error")
        {
            CreditLedger.RefundOne(user);
            await FinishAsync(history, status, technicalError, 0,
                history.InputTokens, history.OutputTokens, timer: null);
            _progress.Fail(job.ProgressToken, userMessage);
        }

        private async Task FinishAsync(InterpretationHistory history, string status,
            string? errorMsg, int credits, int? inTok, int? outTok, StageTimer? timer)
        {
            history.Status = status;
            history.ErrorMessage = errorMsg?.Length > 500 ? errorMsg[..500] : errorMsg;
            history.CreditsConsumed = credits;
            history.InputTokens = inTok;
            history.OutputTokens = outTok;
            if (timer != null)
            {
                history.DurationMs = (int)Math.Min(int.MaxValue, timer.TotalMs);
                history.StageTimingsJson = timer.ToJson();
            }
            await _db.SaveChangesAsync(CancellationToken.None);

            bool geminiWasCalled = inTok.HasValue || outTok.HasValue
                                   || !string.IsNullOrWhiteSpace(history.ModelUsed);
            if (geminiWasCalled)
            {
                await _aiUsage.LogAsync(
                    source: "B2C",
                    userEmail: history.UserEmail,
                    clinicId: null,
                    modelUsed: history.ModelUsed ?? _geminiSettings.Model ?? "(unknown)",
                    inputTokens: inTok ?? 0,
                    outputTokens: outTok ?? 0,
                    status: status,
                    errorMessage: errorMsg);
            }
        }

        private Task LogTransientAsync(string email, string? modelOverride, string message) =>
            _aiUsage.LogAsync(
                source: "B2C",
                userEmail: email,
                clinicId: null,
                modelUsed: modelOverride ?? _geminiSettings.Model ?? "(unknown)",
                inputTokens: 0,
                outputTokens: 0,
                status: "transient_error",
                errorMessage: message);

        private static string Trim200(string s) => s.Length > 200 ? s[..200] : s;
        private static string? Trim40(string? s) =>
            string.IsNullOrEmpty(s) ? s : (s.Length > 40 ? s[..40] : s);

        /// <summary>
        /// The worker thread has no request culture, so PDF labels and email
        /// copy would fall back to the server default. Same fix as CamBatchService.
        /// </summary>
        private static void ApplyCulture(string? languageCode)
        {
            var lang = string.IsNullOrWhiteSpace(languageCode)
                ? "ro"
                : languageCode.Split('-')[0].ToLowerInvariant();
            try
            {
                var culture = new CultureInfo(SupportedLanguagesConfig.GetCultureCode(lang));
                CultureInfo.CurrentUICulture = culture;
                CultureInfo.CurrentCulture = culture;
            }
            catch (CultureNotFoundException)
            {
                // Loc.T falls back to English — acceptable safety net.
            }
        }

        private static readonly Regex s_medicalValueUnit = new Regex(
            @"\d+(?:[.,]\d+)?\s*(?:g\s*/\s*d[lL]"
            + @"|mg\s*/\s*d[lL]"
            + @"|µ?g\s*/\s*[lL]"
            + @"|n?g\s*/\s*m[lL]"
            + @"|mmol\s*/\s*[lL]"
            + @"|mIU\s*/\s*m[lL]"
            + @"|U\s*/\s*[lL]"
            + @"|mm\s*/\s*h"
            + @"|10\^?[36]\s*/\s*u?[lL]"
            + @"|fl\b"
            + @"|pg\b"
            + @"|%)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>
        /// Does the extracted text contain real lab measurements, or only
        /// administrative metadata (Word-rasterized body pages / scans)?
        /// Three value+unit matches cleanly separate the two cases.
        /// </summary>
        private static bool LooksLikeMedicalData(string text) =>
            !string.IsNullOrWhiteSpace(text) && s_medicalValueUnit.Matches(text).Count >= 3;

        private static string BuildEmailBody(string? profileName, string? languageCode)
        {
            var greeting = Loc.T("EmailGreeting", languageCode);
            var intro = Loc.T("ResultEmailIntro", languageCode);
            var attached = Loc.T("ResultEmailAttachedNote", languageCode);
            var tagline = Loc.T("Tagline", languageCode);
            var regards = Loc.T("EmailRegards", languageCode);
            var profileLine = string.IsNullOrWhiteSpace(profileName)
                ? string.Empty
                : $"<p style='background:#eef5ff;border-left:4px solid #0d47a1;padding:10px 14px;border-radius:6px;margin:16px 0;'>{string.Format(Loc.T("EmailInterpretForProfileFmt", languageCode), $"<strong>{System.Net.WebUtility.HtmlEncode(profileName)}</strong>")}</p>";
            return $@"
<div style='font-family: Arial, sans-serif; max-width: 600px; margin: 0 auto; padding: 20px;'>
    <h2 style='color: #0d47a1;'>MyMedicalApp.NET</h2>
    <p>{greeting}</p>
    {profileLine}
    <p>{intro}</p>
    <p style='color: #6c757d; font-size: 0.9em;'>{attached}</p>
    <p style='font-style: italic; color: #0d47a1;'>{tagline}</p>
    <hr style='border: none; border-top: 1px solid #dee2e6; margin: 20px 0;' />
    <p style='color: #6c757d; font-size: 0.9em;'>{regards}</p>
    <p style='color: #0d47a1; font-weight: bold;'>www.mymedicalapp.net</p>
</div>";
        }
    }
}
