using System.Net.Http;
using System.Text.Json;
using MedicalApp.Data;
using MedicalApp.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MedicalApp.Services
{
    /// <summary>
    /// Orchestrates a CAM batch run end-to-end. Designed to be invoked from a
    /// fire-and-forget <c>Task.Run</c> by the controller right after the user
    /// presses "Lansează Lot". Tracks live progress in the
    /// <see cref="CamBatchRegistry"/> singleton so the AJAX status endpoint
    /// can render a progress bar.
    ///
    /// Decisions baked in (Faza 3, confirmed with the user a)i / b)i / c)i / d)i):
    ///   * Compare PDF attached when patient has ≥1 prior analysis (kept ≤4).
    ///   * No file count limit per batch — operator sees live progress.
    ///   * Cancellable via CancellationToken — stops after current file finishes.
    ///   * No auto-resume on app restart: "Running" batches are flipped to
    ///     "Failed" on startup; the operator re-launches manually.
    ///   * Processing is SEQUENTIAL (1 file at a time) — easier on Gemini's
    ///     rate limit, easier to log, and reliable on a single-machine setup.
    /// </summary>
    public class CamBatchService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly CamBatchRegistry _registry;
        private readonly ILogger<CamBatchService> _logger;

        public CamBatchService(
            IServiceScopeFactory scopeFactory,
            CamBatchRegistry registry,
            ILogger<CamBatchService> logger)
        {
            _scopeFactory = scopeFactory;
            _registry = registry;
            _logger = logger;
        }

        /// <summary>
        /// Long-running entry point. Each call processes EXACTLY ONE batch
        /// (already inserted in <c>ClinicBatchRuns</c> with Status="Running").
        /// Never throws — captures all exceptions and persists the final
        /// state to DB + .txt sumar.
        ///
        /// <paramref name="languageCode"/> is the short ISO code (ro/en/fr/es/de)
        /// captured by BatchController at the time the operator clicked "Start".
        /// Used to localize the progress.Log() messages displayed in the live
        /// log UI. The default "ro" keeps existing callers working unchanged
        /// (e.g. tests, future re-launch endpoints).
        /// </summary>
        public async Task RunAsync(int batchRunId, string languageCode = "ro")
        {
            // Normalize and guard so a future caller that accidentally passes
            // "" or "en-US" still reaches Loc.T with a value it understands.
            var lang = string.IsNullOrWhiteSpace(languageCode)
                ? "ro"
                : languageCode.Split('-')[0].ToLowerInvariant();

            // Propagate the operator's UI language into the background batch
            // thread so every Loc.T(key) call without an explicit `lang` argument
            // (notably LocalizedLabels.ForCurrentUi() used by PdfReportGenerator
            // and CamComparePdfGenerator) resolves to the operator's language
            // instead of the OS default. CurrentUICulture flows through awaits
            // inside the same async state machine, so setting it once here
            // covers the entire batch lifetime.
            try
            {
                var cultureName = lang switch
                {
                    "ro" => "ro-RO",
                    "fr" => "fr-FR",
                    "es" => "es-ES",
                    "de" => "de-DE",
                    _    => "en-US"
                };
                var culture = new System.Globalization.CultureInfo(cultureName);
                System.Globalization.CultureInfo.CurrentUICulture = culture;
                System.Globalization.CultureInfo.CurrentCulture = culture;
            }
            catch (System.Globalization.CultureNotFoundException)
            {
                // Older Windows installations may not have every culture
                // registered. The batch can still run — Loc.T then falls
                // back to English via Resolve(), which is the documented
                // safety net.
            }
            CamBatchProgress? progress = null;
            try
            {
                // Each batch gets its OWN DI scope so the DbContext + scoped
                // services are not shared with the HTTP request that started it.
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var files = scope.ServiceProvider.GetRequiredService<ICamFileStore>();
                var extractor = scope.ServiceProvider.GetRequiredService<CamPdfMetadataExtractor>();
                var gemini = scope.ServiceProvider.GetRequiredService<IMedicalInterpretationProvider>();
                var pdfGen = scope.ServiceProvider.GetRequiredService<PdfReportGenerator>();
                var compareGen = scope.ServiceProvider.GetRequiredService<CamComparePdfGenerator>();
                var loincMatcher = scope.ServiceProvider.GetRequiredService<LoincMatcherClient>();
                var email = scope.ServiceProvider.GetRequiredService<IEmailService>();

                var batch = await db.ClinicBatchRuns.FirstOrDefaultAsync(b => b.Id == batchRunId);
                if (batch == null)
                {
                    _logger.LogError("CAM batch {Id}: not found in DB, aborting.", batchRunId);
                    return;
                }

                var clinic = await db.Clinics.FirstOrDefaultAsync(c => c.Id == batch.ClinicId);
                if (clinic == null)
                {
                    _logger.LogError("CAM batch {Id}: clinic {ClinicId} not found, aborting.", batchRunId, batch.ClinicId);
                    batch.Status = "Failed";
                    batch.FinishedAt = DateTime.UtcNow;
                    await db.SaveChangesAsync();
                    return;
                }

                var user = await db.Users.FirstOrDefaultAsync(u => u.Email == clinic.UserEmail);

                var originalFolder = files.GetOriginalFolder(clinic);
                var sendsFolder = files.GetSendsFolder(clinic);
                var sumarFolder = files.GetSumarFolder(clinic);
                var errorsFolder = files.GetErrorsFolder(clinic);

                var pdfPaths = Directory.Exists(originalFolder)
                    ? Directory.GetFiles(originalFolder, "*.pdf", SearchOption.TopDirectoryOnly)
                        .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                        .ToList()
                    : new List<string>();

                batch.TotalFiles = pdfPaths.Count;
                await db.SaveChangesAsync();

                progress = _registry.GetOrCreate(batchRunId, batch.ClinicId, pdfPaths.Count);
                progress.Log(string.Format(Loc.T("CamBatchLogStarted", lang), pdfPaths.Count));

                if (pdfPaths.Count == 0)
                {
                    progress.Log(Loc.T("CamBatchLogEmpty", lang));
                    batch.Status = "Completed";
                    batch.FinishedAt = DateTime.UtcNow;
                    await db.SaveChangesAsync();
                    progress.Status = "Completed";
                    progress.FinishedAt = batch.FinishedAt;
                    WriteSumar(db, batch, clinic, sumarFolder);
                    return;
                }

                var ct = progress.Cts.Token;

                foreach (var path in pdfPaths)
                {
                    if (ct.IsCancellationRequested)
                    {
                        progress.Log(Loc.T("CamBatchLogCancelledStopping", lang));
                        batch.Status = "Cancelled";
                        break;
                    }

                    progress.CurrentFile = Path.GetFileName(path);
                    progress.Log(string.Format(Loc.T("CamBatchLogProcessingFile", lang), progress.CurrentFile));

                    try
                    {
                        await ProcessOneFileAsync(
                            db, files, extractor, gemini, pdfGen, compareGen, loincMatcher, email,
                            clinic, user, batch, progress, path, sendsFolder, errorsFolder, lang, ct);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "CAM batch {Id}: uncaught error for {File}", batchRunId, path);
                        progress.Log(string.Format(Loc.T("CamBatchLogUnexpectedError", lang), ex.Message));
                        await RecordErrorAsync(db, batch, path, null, "Unexpected: " + ex.Message);
                        batch.NotSends++;
                        progress.NotSends++;
                    }

                    progress.Processed++;
                    await db.SaveChangesAsync();
                }

                if (batch.Status == "Running") batch.Status = "Completed";
                batch.FinishedAt = DateTime.UtcNow;
                await db.SaveChangesAsync();
                progress.Status = batch.Status;
                progress.FinishedAt = batch.FinishedAt;
                progress.Log(string.Format(Loc.T("CamBatchLogFinalized", lang), batch.Status));

                WriteSumar(db, batch, clinic, sumarFolder);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "CAM batch {Id}: fatal error", batchRunId);
                if (progress != null)
                {
                    progress.Status = "Failed";
                    progress.FinishedAt = DateTime.UtcNow;
                    progress.Log(string.Format(Loc.T("CamBatchLogFatalError", lang), ex.Message));
                }
                try
                {
                    using var scope = _scopeFactory.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                    var b = await db.ClinicBatchRuns.FirstOrDefaultAsync(x => x.Id == batchRunId);
                    if (b != null)
                    {
                        b.Status = "Failed";
                        b.FinishedAt = DateTime.UtcNow;
                        await db.SaveChangesAsync();
                    }
                }
                catch { /* swallow secondary errors */ }
            }
        }

        // -------------------------------------------------------------------
        // Per-file pipeline. Each file is INDEPENDENT — a failure here never
        // breaks the batch, it just increments NotSends.
        // -------------------------------------------------------------------
        private async Task ProcessOneFileAsync(
            AppDbContext db,
            ICamFileStore files,
            CamPdfMetadataExtractor extractor,
            IMedicalInterpretationProvider gemini,
            PdfReportGenerator pdfGen,
            CamComparePdfGenerator compareGen,
            LoincMatcherClient loincMatcher,
            IEmailService email,
            Clinic clinic,
            User? user,
            ClinicBatchRun batch,
            CamBatchProgress progress,
            string path,
            string sendsFolder,
            string errorsFolder,
            string lang,
            CancellationToken ct)
        {
            var fileName = Path.GetFileName(path);
            byte[] bytes;
            try { bytes = await File.ReadAllBytesAsync(path, ct); }
            catch (Exception ex)
            {
                progress.Log(string.Format(Loc.T("CamBatchLogReadFailed", lang), ex.Message));
                await RecordErrorAsync(db, batch, path, null, "Read error: " + ex.Message);
                batch.NotSends++; progress.NotSends++;
                await MoveToErrorsIfRetriesExhaustedAsync(db, batch, path, errorsFolder);
                return;
            }

            // 1. Decide patient identification path (NO AI fallback, per user policy):
            //    (a) operator override → use as-is, skip auto-extraction
            //    (b) explicit [MedicalApp] block in PDF → use directly
            //    (c) otherwise → reject; operator must press "Editează" in the
            //        Verificare PDF-uri page to set a manual override.
            CamPdfMetadata? meta = null;

            var overrideRow = await db.ClinicPdfOverrides
                .FirstOrDefaultAsync(o => o.ClinicId == clinic.Id && o.FileName == fileName);
            if (overrideRow != null)
            {
                meta = new CamPdfMetadata
                {
                    PatientName = overrideRow.OverrideName,
                    PatientEmail = overrideRow.OverrideEmail,
                    IsValid = true,
                    IsMedicalLabReport = true
                };
                progress.Log(string.Format(Loc.T("CamBatchLogUseOverride", lang), overrideRow.OverrideName));
            }
            else
            {
                // Try the explicit [MedicalApp] block first (gold path, free).
                var probe = extractor.Extract(bytes, fileName, clinicDomainBlacklist: null);
                if (probe.MatchedExplicitBlock && probe.IsValid)
                {
                    meta = probe;
                    progress.Log(string.Format(Loc.T("CamBatchLogBlockMedicalApp", lang), probe.PatientName, probe.PatientEmail));
                }
                else if (!probe.IsMedicalLabReport)
                {
                    // Pre-filter: refuse non-medical PDFs immediately (no AI cost).
                    // Live log gets the localized message; DB keeps the English
                    // Reason for stable traceability across UI languages.
                    var localizedReason = !string.IsNullOrEmpty(probe.ReasonKey)
                        ? Loc.T(probe.ReasonKey, lang)
                        : probe.Reason ?? "Not a medical lab PDF";
                    progress.Log($"   ✘ {localizedReason}");
                    await RecordErrorAsync(db, batch, path, null, probe.Reason ?? "Not a medical lab PDF");
                    batch.NotSends++; progress.NotSends++;
                    await MoveToErrorsIfRetriesExhaustedAsync(db, batch, path, errorsFolder);
                    return;
                }
                else
                {
                    // Per user policy: NO AI fallback for patient identification.
                    // Operator must add [MedicalApp] block in PDF OR set a manual
                    // override via "Editează" on the Verificare PDF-uri page.
                    progress.Log(Loc.T("CamBatchLogNoBlockNoOverride", lang));
                    await RecordErrorAsync(db, batch, path, null,
                        "PDF fără bloc [MedicalApp] și fără override manual. Apasă „Editează” în pagina Verificare PDF-uri.");
                    batch.NotSends++; progress.NotSends++;
                    await MoveToErrorsIfRetriesExhaustedAsync(db, batch, path, errorsFolder);
                    return;
                }
            }

            // 2. Check credit budget BEFORE we spend AI tokens
            if (user == null || user.TotalAvailableCredits <= 0)
            {
                progress.Log(Loc.T("CamBatchLogNoCredits", lang));
                await RecordErrorAsync(db, batch, path, meta!.PatientName, "Out of credits");
                batch.NotSends++; progress.NotSends++;
                return;
            }

            // 3. Call Gemini with retry + Flash→Pro fallback (mirrors InterpretationController logic).
            InterpretationResult? result = await CallGeminiWithRetryAsync(gemini, bytes, fileName, clinic, user, progress, lang, ct);
            if (result == null)
            {
                await RecordErrorAsync(db, batch, path, meta!.PatientName, "AI exhausted retries (incl. fallback model)");
                batch.NotSends++; progress.NotSends++;
                // 3-strikes-out: after 3 failed attempts across batches, move
                // the file to Errors/ so the next "Lansează lot" doesn't keep
                // burning credits on the same broken PDF. (Was missing here;
                // present on all the other NotSends paths.)
                await MoveToErrorsIfRetriesExhaustedAsync(db, batch, path, errorsFolder);
                return;
            }

            // 3c. CAM now uses the SAME LOINC matcher as the B2C interpretation
            // path (Python service: 128 canonical anchors + semantic embeddings).
            // Without this step the Compare PDF cannot group rows by LOINC class
            // because Gemini alone rarely emits LOINC codes for every parameter.
            try
            {
                var matchStats = await loincMatcher.MatchAllAsync(result, ct);
                if (matchStats.Matched > 0)
                {
                    progress.Log(string.Format(Loc.T("CamBatchLogLoincMatcher", lang), matchStats.Matched, matchStats.Total));
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "CAM batch {Id}: LoincMatcherClient failed (continuing without LOINC codes)", batch.Id);
                progress.Log(Loc.T("CamBatchLogLoincUnavailable", lang));
            }

            // 3d. Re-compute every parameter's status from its value + reference
            // range, overriding whatever Gemini emitted. Gemini occasionally
            // labels a perfectly in-range value as "high"/"low" (e.g. Densitate
            // urinară 1.024 inside [1.005-1.03] flagged ↑). This deterministic
            // post-correction is the safety net — exact same call the B2C
            // InterpretationController makes before generating the PDF.
            try
            {
                var v = StatusValidator.Validate(result, _logger);
                if (v.Corrected > 0)
                    progress.Log(string.Format(Loc.T("CamBatchLogStatusValidator", lang), v.Corrected, v.Total));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "CAM batch {Id}: StatusValidator failed (continuing with raw AI statuses)", batch.Id);
            }

            // 4. Find or create patient (lookup by NameKey + Email)
            var nameKey = CamPatientKey.Normalize(meta!.PatientName!);
            var patient = await db.ClinicPatients.FirstOrDefaultAsync(p =>
                p.ClinicId == clinic.Id &&
                p.NameKey == nameKey &&
                p.Email == meta.PatientEmail);
            if (patient == null)
            {
                patient = new ClinicPatient
                {
                    ClinicId = clinic.Id,
                    Name = meta.PatientName!.Trim(),
                    NameKey = nameKey,
                    Email = meta.PatientEmail!.Trim(),
                    CreatedAt = DateTime.UtcNow
                };
                db.ClinicPatients.Add(patient);
                await db.SaveChangesAsync();
                progress.Log(string.Format(Loc.T("CamBatchLogNewPatient", lang), patient.Name, patient.Email));
            }

            // 5. Persist this analysis + keep only last 4 per patient
            var rawJson = JsonSerializer.Serialize(result, new JsonSerializerOptions
            {
                WriteIndented = true,
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
            });
            var newAnalysis = new ClinicAnalysis
            {
                ClinicId = clinic.Id,
                PatientId = patient.Id,
                OriginalFileName = fileName,
                RawJsonResult = rawJson,
                SamplingDate = TryParseDate(result.PatientInfo?.DateTaken),
                ProcessedAt = DateTime.UtcNow
            };
            db.ClinicAnalyses.Add(newAnalysis);
            await db.SaveChangesAsync();

            var allForPatient = await db.ClinicAnalyses
                .Where(a => a.PatientId == patient.Id)
                .OrderByDescending(a => a.SamplingDate ?? a.ProcessedAt)
                .ToListAsync();
            if (allForPatient.Count > 4)
            {
                var toRemove = allForPatient.Skip(4).ToList();
                db.ClinicAnalyses.RemoveRange(toRemove);
                await db.SaveChangesAsync();
                allForPatient = allForPatient.Take(4).ToList();
            }

            // 6. Build interpretation PDF
            byte[] interpretationPdf;
            try
            {
                interpretationPdf = pdfGen.Generate(result, LocalizedLabels.ForCurrentUi());
            }
            catch (Exception ex)
            {
                progress.Log(string.Format(Loc.T("CamBatchLogPdfGenFailed", lang), ex.Message));
                await RecordErrorAsync(db, batch, path, patient.Name, "PDF gen failure: " + ex.Message);
                batch.NotSends++; progress.NotSends++;
                return;
            }

            // 7. Compare PDF if patient has ≥2 analyses (option a)i)
            byte[]? comparePdf = null;
            if (allForPatient.Count >= 2)
            {
                try
                {
                    comparePdf = compareGen.GenerateIfPossible(clinic, patient, allForPatient);
                }
                catch (Exception ex)
                {
                    progress.Log(string.Format(Loc.T("CamBatchLogComparePdfFailed", lang), ex.Message));
                    comparePdf = null;
                }
            }

            // 8. Send email
            try
            {
                var subject = CamPatientEmailBuilder.BuildSubject(clinic);
                var html = CamPatientEmailBuilder.BuildHtml(
                    clinic, patient,
                    hasInterpretation: true,
                    hasCompareReport: comparePdf != null,
                    originalFileName: fileName);

                var attachments = new List<(byte[] Bytes, string FileName, string MimeType)>
                {
                    (bytes, fileName, "application/pdf"),
                    (interpretationPdf, "Raport_Interpretare.pdf", "application/pdf")
                };
                if (comparePdf != null)
                    attachments.Add((comparePdf, "Raport_Comparatie.pdf", "application/pdf"));

                await email.SendEmailWithAttachmentsAsync(patient.Email, subject, html, attachments);
                batch.FilesSent++; progress.Sent++;
                if (comparePdf != null) { batch.FilesCompared++; progress.Compared++; }
            }
            catch (Exception ex)
            {
                // A4 — translate the raw SMTP exception into a clinician-friendly
                // sentence so the operator immediately knows whether to retry, edit
                // the address, or call the IT person. Original message stays in
                // batch.Errors for traceability; only the progress log is humanized.
                var friendly = ClassifyEmailFailure(ex, patient.Email);
                progress.Log($"   ✘ {friendly}");
                await RecordErrorAsync(db, batch, path, patient.Name,
                    $"Email failure: [{friendly}] — Raw: {ex.Message}");
                batch.NotSends++; progress.NotSends++;
                return;
            }

            // 9. Move original PDF to Sends/ and consume 1 credit
            try
            {
                Directory.CreateDirectory(sendsFolder);
                var dest = Path.Combine(sendsFolder, fileName);
                if (File.Exists(dest)) dest = Path.Combine(sendsFolder, $"{DateTime.Now:yyyyMMdd_HHmmss}_{fileName}");
                File.Move(path, dest);
            }
            catch (Exception ex)
            {
                progress.Log(string.Format(Loc.T("CamBatchLogMoveToSendsFailed", lang), ex.Message));
            }

            // Cleanup: override-ul manual nu mai are sens după ce fișierul
            // a părăsit folderul Original.
            var ovToDelete = await db.ClinicPdfOverrides
                .FirstOrDefaultAsync(o => o.ClinicId == clinic.Id && o.FileName == fileName);
            if (ovToDelete != null)
            {
                db.ClinicPdfOverrides.Remove(ovToDelete);
            }

            if (user != null)
            {
                // Bonus credits consumed FIRST, then paid.
                if (user.BonusCreditsRemaining > 0)
                    user.BonusCreditsConsumed++;
                else
                {
                    user.CreditConsum++;
                    user.CreditRest = user.Credite - user.CreditConsum;
                }
            }

            batch.FilesInterpreted++; progress.Sent = batch.FilesSent;
            await db.SaveChangesAsync();

            progress.Log(string.Format(
                comparePdf != null
                    ? Loc.T("CamBatchLogSentWithCompare", lang)
                    : Loc.T("CamBatchLogSent", lang),
                patient.Email));
        }

        private async Task RecordErrorAsync(AppDbContext db, ClinicBatchRun batch, string filePath, string? patientName, string reason)
        {
            var fileName = Path.GetFileName(filePath);
            // RetryCount = how many times THIS filename has failed for this clinic across batches.
            var prior = await db.ClinicBatchErrors
                .Where(e => e.FileName == fileName)
                .Join(db.ClinicBatchRuns, e => e.BatchRunId, b => b.Id, (e, b) => new { e, b.ClinicId })
                .Where(x => x.ClinicId == batch.ClinicId)
                .CountAsync();

            db.ClinicBatchErrors.Add(new ClinicBatchError
            {
                BatchRunId = batch.Id,
                FileName = fileName,
                PatientName = patientName,
                Reason = reason,
                OccurredAt = DateTime.UtcNow,
                RetryCount = prior + 1
            });
            await db.SaveChangesAsync();
        }

        /// <summary>
        /// Translates a raw SMTP / network exception into a single English sentence the
        /// clinic operator can act on immediately. The original <c>ex.Message</c> is still
        /// recorded verbatim in <c>ClinicBatchError.Reason</c> for traceability — this is
        /// only the human-friendly line that hits the live progress log.
        /// English-only by design: this is a technical/log message, not a UI string.
        /// </summary>
        private static string ClassifyEmailFailure(Exception ex, string? patientEmail)
        {
            // Walk the inner-exception chain so we catch SmtpFailedRecipientException
            // nested inside Exception or AggregateException wrappers.
            var msgs = new List<string>();
            for (var cur = ex; cur != null; cur = cur.InnerException)
                msgs.Add(cur.Message ?? string.Empty);
            var combined = string.Join(" || ", msgs).ToLowerInvariant();

            string emailLabel = string.IsNullOrWhiteSpace(patientEmail) ? "(unknown)" : patientEmail!;

            // ---- Mailbox doesn't exist on the destination server ----
            if (combined.Contains("550")
                || combined.Contains("mailbox unavailable")
                || combined.Contains("user unknown")
                || combined.Contains("recipient address rejected")
                || combined.Contains("no such user")
                || combined.Contains("does not exist"))
                return $"Address {emailLabel} was rejected by the server (mailbox does not exist). " +
                       "Check the spelling via the Edit button.";

            // ---- Domain not found ----
            if (combined.Contains("no such host")
                || combined.Contains("hostnotfound")
                || combined.Contains("name or service not known")
                || combined.Contains("could not be resolved"))
                return $"The domain of {emailLabel} does not exist. Most likely a spelling mistake " +
                       "(e.g. gmial.com instead of gmail.com). Use Edit to fix it.";

            // ---- Greylisting / temporary rejection ----
            if (combined.Contains("450")
                || combined.Contains("451")
                || combined.Contains("try again later")
                || combined.Contains("temporarily")
                || combined.Contains("temporary failure"))
                return $"Destination server temporarily busy for {emailLabel}. " +
                       "We will retry on the next batch.";

            // ---- Anti-spam reject (DKIM / SPF / blacklist) ----
            if (combined.Contains("spam")
                || combined.Contains("dkim")
                || combined.Contains("spf")
                || combined.Contains("blacklist")
                || combined.Contains("policy")
                || combined.Contains("reputation"))
                return $"Message to {emailLabel} was rejected as possible spam by the destination server. " +
                       "Likely needs DKIM/SPF configuration on the clinic's domain — contact your administrator.";

            // ---- Auth failure between us and Gmail/SMTP relay ----
            if (combined.Contains("authentication") || combined.Contains("auth fail")
                || combined.Contains("535") || combined.Contains("password"))
                return "Authentication to the SMTP server failed (Gmail app password). " +
                       "Contact your administrator — this error is not caused by the patient's address.";

            // ---- Network timeout / TLS issue ----
            if (combined.Contains("timeout") || combined.Contains("timed out")
                || combined.Contains("connection") || combined.Contains("network is unreachable"))
                return $"Connection to the SMTP server was interrupted while sending to {emailLabel}. " +
                       "Check the internet connection and try again.";

            // ---- Default: surface the first message verbatim, truncated ----
            var first = msgs.FirstOrDefault() ?? "Unknown error";
            if (first.Length > 220) first = first.Substring(0, 220) + "…";
            return $"Email failed for {emailLabel}: {first}";
        }

        private async Task MoveToErrorsIfRetriesExhaustedAsync(AppDbContext db, ClinicBatchRun batch, string filePath, string errorsFolder)
        {
            var fileName = Path.GetFileName(filePath);
            var attempts = await db.ClinicBatchErrors
                .Where(e => e.FileName == fileName)
                .Join(db.ClinicBatchRuns, e => e.BatchRunId, b => b.Id, (e, b) => new { e, b.ClinicId })
                .Where(x => x.ClinicId == batch.ClinicId)
                .CountAsync();
            if (attempts < 3) return;
            try
            {
                Directory.CreateDirectory(errorsFolder);
                var destPdf = Path.Combine(errorsFolder, fileName);
                if (File.Exists(destPdf)) destPdf = Path.Combine(errorsFolder, $"{DateTime.Now:yyyyMMdd_HHmmss}_{fileName}");
                File.Move(filePath, destPdf);

                var reasons = await db.ClinicBatchErrors
                    .Where(e => e.FileName == fileName)
                    .Join(db.ClinicBatchRuns, e => e.BatchRunId, b => b.Id, (e, b) => new { e, b.ClinicId })
                    .Where(x => x.ClinicId == batch.ClinicId)
                    .OrderByDescending(x => x.e.OccurredAt)
                    .Take(3)
                    .Select(x => x.e.Reason)
                    .ToListAsync();

                File.WriteAllText(destPdf + ".reasons.txt",
                    $"Acest fișier a eșuat de 3 ori în loturile clinicii. Motive:\n" +
                    string.Join("\n", reasons.Select(r => "  • " + r)),
                    System.Text.Encoding.UTF8);

                // Cleanup: override-ul nu mai are sens — fișierul a părăsit Original.
                var ovToDelete = await db.ClinicPdfOverrides
                    .FirstOrDefaultAsync(o => o.ClinicId == batch.ClinicId && o.FileName == fileName);
                if (ovToDelete != null)
                {
                    db.ClinicPdfOverrides.Remove(ovToDelete);
                    await db.SaveChangesAsync();
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to move {File} to errors folder", filePath);
            }
        }

        private static void WriteSumar(AppDbContext db, ClinicBatchRun batch, Clinic clinic, string sumarFolder)
        {
            try
            {
                var errors = db.ClinicBatchErrors.Where(e => e.BatchRunId == batch.Id)
                    .OrderBy(e => e.OccurredAt).ToList();
                CamBatchSumarWriter.Write(batch, clinic, errors, sumarFolder);
            }
            catch { /* never break the batch on a sumar I/O failure */ }
        }

        /// <summary>
        /// Calls Gemini for a single CAM PDF with the SAME retry strategy used
        /// by <c>InterpretationController</c> for B2C interpretations:
        ///   * Up to 5 attempts on transient 429/503.
        ///   * After 2 consecutive transients on the primary model, switch to
        ///     <c>GeminiSettings.FallbackModel</c> (typically gemini-2.5-pro)
        ///     for the remaining retries — less congested, more reliable.
        ///   * Progressive backoff: 5s, 15s, 30s, 60s.
        /// Returns null when all attempts are exhausted; never throws.
        /// </summary>
        /// <summary>
        /// Robust Gemini call with 3-tier fallback + transient retry.
        ///
        /// Attempts (max 7 total):
        ///   • Tries 1-2 : primary model       (Flash)            → "motor MedicalApp"
        ///   • Tries 3-5 : first  fallback     (Pro)              → "motor MedicalApp+"
        ///   • Tries 6-7 : second fallback     (next-gen Pro)     → "motor MedicalApp Plus"
        ///
        /// Treated as TRANSIENT (eligible for retry + tier promotion):
        ///   • GeminiTransientException (HTTP 429/503/5xx)
        ///   • HttpClient.Timeout → TaskCanceledException / OperationCanceledException
        ///     when the user has NOT requested cancellation
        ///   • HttpRequestException with "timeout" in the message
        ///
        /// MaxOutputTokens truncation triggers an IMMEDIATE tier promotion
        /// without consuming a retry attempt (output-size issue, not a
        /// transient upstream problem).
        ///
        /// All operator-facing log lines mask the underlying model name to
        /// "motor MedicalApp" / "motor MedicalApp+" / "motor MedicalApp Plus"
        /// so the public log doesn't disclose which AI engine is in use.
        /// </summary>
        private async Task<InterpretationResult?> CallGeminiWithRetryAsync(
            IMedicalInterpretationProvider gemini,
            byte[] pdfBytes,
            string fileName,
            Clinic clinic,
            User? user,
            CamBatchProgress progress,
            string lang,
            CancellationToken ct)
        {
            const int maxAttempts = 7;
            // Note: tier promotion thresholds (Flash→Pro at attempt 2,
            // Pro→Plus at attempt 5) are encoded directly in TryPromoteTier
            // below — kept as magic numbers there for locality.

            using var settingsScope = _scopeFactory.CreateScope();
            var settings = settingsScope.ServiceProvider
                .GetRequiredService<IOptions<GeminiSettings>>().Value;
            // Resolved here (in the SAME scope as `settings`) so we can record
            // every real Gemini call into AiUsageLogs — both successes (with
            // actual tokens + effective model) and final failures (model that
            // was being attempted when we ran out of retries). Fail-safe by
            // design: the logger swallows its own exceptions.
            var aiUsage = settingsScope.ServiceProvider
                .GetRequiredService<IAiUsageLogger>();

            string? modelOverride = null;     // null = use primary (Flash)
            int currentTier = 1;              // 1 = primary, 2 = first fallback, 3 = second fallback

            // The model id that was actually attempted on the LAST iteration —
            // used for accurate AI-usage logging (especially on terminal
            // failures, where we still want to attribute the cost).
            string EffectiveModelId() => modelOverride ?? settings.Model ?? "(unknown)";

            // Resolve display labels once (operator-facing, anonymized).
            // The localized prefix ("motor"/"engine"/"moteur"/"motor"/"Engine")
            // comes from Loc.cs; the brand suffix stays verbatim.
            string LabelFor(int tier)
            {
                var prefix = Loc.T("CamBatchEnginePrefix", lang);
                return tier switch
                {
                    2 => $"{prefix} MedicalApp+",
                    3 => $"{prefix} MedicalApp Plus",
                    _ => $"{prefix} MedicalApp"
                };
            }

            int attempts = 0;
            Exception? lastEx = null;

            while (attempts < maxAttempts)
            {
                attempts++;
                try
                {
                    using var ms = new MemoryStream(pdfBytes);
                    var resp = await gemini.InterpretPdfAsync(
                        ms, fileName, lang,
                        patientContext: null,
                        ct: ct,
                        modelOverride: modelOverride);
                    // Record the successful Gemini call (real tokens, effective model).
                    await aiUsage.LogAsync(
                        source: "CAM",
                        userEmail: user?.Email,
                        clinicId: clinic.Id,
                        modelUsed: EffectiveModelId(),
                        inputTokens: resp.InputTokens,
                        outputTokens: resp.OutputTokens,
                        status: "success",
                        errorMessage: null,
                        ct: ct);
                    return resp.Result;
                }
                // ---------- TRANSIENT: explicit upstream error (429/503/5xx) ----------
                catch (GeminiTransientException ex)
                {
                    lastEx = ex;
                    // Log the failed call to the AI usage table BEFORE TryPromoteTier
                    // mutates modelOverride. Status "transient_error" keeps the row out
                    // of the cost panel (input=0/output=0) but surfaces it in the
                    // Reliability widget so the admin can see Flash/Pro hiccupping.
                    await aiUsage.LogAsync(
                        source: "CAM",
                        userEmail: user?.Email,
                        clinicId: clinic.Id,
                        modelUsed: EffectiveModelId(),
                        inputTokens: 0,
                        outputTokens: 0,
                        status: "transient_error",
                        errorMessage: $"HTTP {ex.HttpStatusCode}: {(ex.Message.Length > 200 ? ex.Message[..200] : ex.Message)}",
                        ct: ct);
                    if (TryPromoteTier(attempts, ref currentTier, ref modelOverride, settings, progress, lang))
                        continue; // promoted — try again immediately without delay
                    if (attempts >= maxAttempts) break;

                    int wait = BackoffMs(attempts);
                    progress.Log(string.Format(Loc.T("CamBatchLogTierOverloaded", lang),
                                 LabelFor(currentTier), ex.HttpStatusCode, wait / 1000, attempts, maxAttempts));
                    try { await Task.Delay(wait, ct); }
                    catch (OperationCanceledException) { return null; }
                }
                // ---------- TRANSIENT: HttpClient timeout (5-10 min per call) ----------
                // HttpClient.Timeout produces TaskCanceledException / OperationCanceledException.
                // We MUST distinguish it from a real user-initiated cancellation:
                // ct.IsCancellationRequested == true → operator hit "Anulează", honor it.
                catch (Exception ex) when (
                    (ex is TaskCanceledException || ex is OperationCanceledException)
                    && !ct.IsCancellationRequested)
                {
                    lastEx = ex;
                    progress.Log(string.Format(Loc.T("CamBatchLogTierTimeout", lang),
                                 LabelFor(currentTier), attempts, maxAttempts));
                    await aiUsage.LogAsync(
                        source: "CAM",
                        userEmail: user?.Email,
                        clinicId: clinic.Id,
                        modelUsed: EffectiveModelId(),
                        inputTokens: 0,
                        outputTokens: 0,
                        status: "transient_error",
                        errorMessage: "Timeout: " + (ex.Message.Length > 200 ? ex.Message[..200] : ex.Message),
                        ct: CancellationToken.None);
                    if (TryPromoteTier(attempts, ref currentTier, ref modelOverride, settings, progress, lang))
                        continue;
                    if (attempts >= maxAttempts) break;

                    int wait = BackoffMs(attempts);
                    progress.Log(string.Format(Loc.T("CamBatchLogRetryIn", lang), wait / 1000));
                    try { await Task.Delay(wait, ct); }
                    catch (OperationCanceledException) { return null; }
                }
                // ---------- TRANSIENT: network-level timeout via HttpRequestException ----------
                catch (HttpRequestException ex) when (
                    ex.Message.Contains("timeout", StringComparison.OrdinalIgnoreCase)
                    || ex.Message.Contains("timed out", StringComparison.OrdinalIgnoreCase))
                {
                    lastEx = ex;
                    progress.Log(string.Format(Loc.T("CamBatchLogTierSlowNet", lang),
                                 LabelFor(currentTier), attempts, maxAttempts));
                    await aiUsage.LogAsync(
                        source: "CAM",
                        userEmail: user?.Email,
                        clinicId: clinic.Id,
                        modelUsed: EffectiveModelId(),
                        inputTokens: 0,
                        outputTokens: 0,
                        status: "transient_error",
                        errorMessage: "Network: " + (ex.Message.Length > 200 ? ex.Message[..200] : ex.Message),
                        ct: ct);
                    if (TryPromoteTier(attempts, ref currentTier, ref modelOverride, settings, progress, lang))
                        continue;
                    if (attempts >= maxAttempts) break;

                    int wait = BackoffMs(attempts);
                    progress.Log(string.Format(Loc.T("CamBatchLogRetryIn", lang), wait / 1000));
                    try { await Task.Delay(wait, ct); }
                    catch (OperationCanceledException) { return null; }
                }
                // ---------- IMMEDIATE TIER PROMOTION: MaxOutputTokens truncation ----------
                // Not a transient issue — output is too big for the current model.
                // Promote to next tier and DO NOT consume a retry attempt.
                catch (Exception ex) when (
                    ex is InvalidOperationException
                    && ex.Message.Contains("MaxOutputTokens", StringComparison.OrdinalIgnoreCase)
                    && currentTier < 3)
                {
                    lastEx = ex;
                    string? nextModel = currentTier == 1 ? settings.FallbackModel : settings.SecondaryFallbackModel;
                    if (string.IsNullOrWhiteSpace(nextModel)
                        || string.Equals(nextModel, modelOverride ?? settings.Model, StringComparison.OrdinalIgnoreCase))
                    {
                        // No next tier configured — fall through to non-transient handling below.
                        progress.Log(Loc.T("CamBatchLogTruncatedNoFallback", lang));
                        return null;
                    }
                    modelOverride = nextModel;
                    currentTier++;
                    attempts--; // refund this attempt — promotion is "free"
                    progress.Log(string.Format(Loc.T("CamBatchLogTruncatedSwitching", lang), LabelFor(currentTier)));
                }
                // ---------- MODEL RETIRED by Google (404 NOT_FOUND): promote tier or fail clean ----------
                // The configured model id no longer exists at Google (typically a
                // preview that was rotated out). No amount of retry will fix it,
                // and other tiers may very well be healthy — so we skip the
                // remaining attempts on THIS tier and try the next one. If this
                // was already the last tier, we log a clear admin-facing message
                // and fail the file cleanly (NotSends).
                catch (GeminiModelRetiredException ex)
                {
                    lastEx = ex;
                    string? nextModel = currentTier == 1 ? settings.FallbackModel
                                       : currentTier == 2 ? settings.SecondaryFallbackModel
                                       : null;
                    if (currentTier < 3 && !string.IsNullOrWhiteSpace(nextModel)
                        && !string.Equals(nextModel, ex.RetiredModelId, StringComparison.OrdinalIgnoreCase))
                    {
                        progress.Log(string.Format(Loc.T("CamBatchLogTierRetiredPromote", lang),
                                     LabelFor(currentTier), LabelFor(currentTier + 1)));
                        modelOverride = nextModel;
                        currentTier++;
                        attempts--; // refund this attempt — promotion is "free"
                    }
                    else
                    {
                        // Last tier or no next model — surface a friendly Romanian
                        // message in the Log Live, plus the technical detail in the
                        // backend log (already done by the service).
                        progress.Log(string.Format(Loc.T("CamBatchLogTierRetiredFinal", lang),
                                     LabelFor(currentTier), ex.RetiredModelId));
                        return null;
                    }
                }
                // ---------- USER CANCELLATION: honor immediately ----------
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    progress.Log(Loc.T("CamBatchLogCancelledShort", lang));
                    return null;
                }
                // ---------- NON-TRANSIENT: fail fast ----------
                catch (Exception ex)
                {
                    progress.Log(string.Format(Loc.T("CamBatchLogNonTransientFailed", lang),
                                 LabelFor(currentTier), ex.Message));
                    // Final failure on this tier — record so the Admin dashboard
                    // sees that a Gemini call was attempted (tokens unknown on
                    // exception path, so we log 0/0 but with the effective model
                    // and a truncated error message for diagnostics).
                    await aiUsage.LogAsync(
                        source: "CAM",
                        userEmail: user?.Email,
                        clinicId: clinic.Id,
                        modelUsed: EffectiveModelId(),
                        inputTokens: 0,
                        outputTokens: 0,
                        status: "error",
                        errorMessage: ex.Message,
                        ct: CancellationToken.None);
                    return null;
                }
            }

            progress.Log(string.Format(Loc.T("CamBatchLogAllRetriesFailed", lang),
                         maxAttempts, LabelFor(currentTier), lastEx?.Message ?? "?"));
            // Retry budget exhausted across all tiers — also record as a final
            // failed call so the dashboard can show "AI exhausted retries" rows.
            await aiUsage.LogAsync(
                source: "CAM",
                userEmail: user?.Email,
                clinicId: clinic.Id,
                modelUsed: EffectiveModelId(),
                inputTokens: 0,
                outputTokens: 0,
                status: "error",
                errorMessage: lastEx?.Message ?? "Retries exhausted",
                ct: CancellationToken.None);
            return null;
        }

        /// <summary>
        /// Promotes the active model tier when the attempt count crosses a
        /// threshold (Flash → Pro after attempt 2, Pro → next-gen after
        /// attempt 5). Returns <c>true</c> if a promotion happened (the
        /// caller should immediately re-try without sleeping).
        /// </summary>
        private static bool TryPromoteTier(
            int attempts,
            ref int currentTier, ref string? modelOverride,
            GeminiSettings settings, CamBatchProgress progress, string lang)
        {
            // 1 → 2 : after attempt 2, switch to FallbackModel (Pro)
            if (currentTier == 1
                && attempts >= 2
                && !string.IsNullOrWhiteSpace(settings.FallbackModel)
                && !string.Equals(settings.FallbackModel, settings.Model, StringComparison.OrdinalIgnoreCase))
            {
                modelOverride = settings.FallbackModel;
                currentTier = 2;
                progress.Log(Loc.T("CamBatchLogSwitchToPlus", lang));
                return true;
            }
            // 2 → 3 : after attempt 5, switch to SecondaryFallbackModel (next-gen Pro)
            if (currentTier == 2
                && attempts >= 5
                && !string.IsNullOrWhiteSpace(settings.SecondaryFallbackModel)
                && !string.Equals(settings.SecondaryFallbackModel, settings.FallbackModel, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(settings.SecondaryFallbackModel, settings.Model, StringComparison.OrdinalIgnoreCase))
            {
                modelOverride = settings.SecondaryFallbackModel;
                currentTier = 3;
                progress.Log(Loc.T("CamBatchLogSwitchToPlusSafety", lang));
                return true;
            }
            return false;
        }

        /// <summary>Exponential-ish backoff: 5s, 15s, 30s, 60s, 60s, 60s.</summary>
        private static int BackoffMs(int attempt)
        {
            int[] delays = { 5_000, 15_000, 30_000, 60_000, 60_000, 60_000 };
            return delays[Math.Min(attempt - 1, delays.Length - 1)];
        }

        private static DateTime? TryParseDate(string? raw)
            => SamplingDateParser.TryParse(raw);
    }
}
