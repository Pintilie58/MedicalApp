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
        /// </summary>
        public async Task RunAsync(int batchRunId)
        {
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
                progress.Log($"Lot pornit. {pdfPaths.Count} fișiere în coadă.");

                if (pdfPaths.Count == 0)
                {
                    progress.Log("Niciun fișier în Original. Lot finalizat.");
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
                        progress.Log("Anulat de operator. Opresc lotul.");
                        batch.Status = "Cancelled";
                        break;
                    }

                    progress.CurrentFile = Path.GetFileName(path);
                    progress.Log($"➜ {progress.CurrentFile}");

                    try
                    {
                        await ProcessOneFileAsync(
                            db, files, extractor, gemini, pdfGen, compareGen, loincMatcher, email,
                            clinic, user, batch, progress, path, sendsFolder, errorsFolder, ct);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "CAM batch {Id}: uncaught error for {File}", batchRunId, path);
                        progress.Log($"   ✘ Eroare neașteptată: {ex.Message}");
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
                progress.Log($"Lot finalizat. Status: {batch.Status}.");

                WriteSumar(db, batch, clinic, sumarFolder);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "CAM batch {Id}: fatal error", batchRunId);
                if (progress != null)
                {
                    progress.Status = "Failed";
                    progress.FinishedAt = DateTime.UtcNow;
                    progress.Log("✘ Eroare fatală: " + ex.Message);
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
            CancellationToken ct)
        {
            var fileName = Path.GetFileName(path);
            byte[] bytes;
            try { bytes = await File.ReadAllBytesAsync(path, ct); }
            catch (Exception ex)
            {
                progress.Log("   ✘ Citire eșuată: " + ex.Message);
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
                progress.Log("   ◇ Folosesc override manual: " + overrideRow.OverrideName);
            }
            else
            {
                // Try the explicit [MedicalApp] block first (gold path, free).
                var probe = extractor.Extract(bytes, fileName, clinicDomainBlacklist: null);
                if (probe.MatchedExplicitBlock && probe.IsValid)
                {
                    meta = probe;
                    progress.Log($"   ⭐ Bloc [MedicalApp]: {probe.PatientName} <{probe.PatientEmail}>");
                }
                else if (!probe.IsMedicalLabReport)
                {
                    // Pre-filter: refuse non-medical PDFs immediately (no AI cost).
                    progress.Log($"   ✘ {probe.Reason}");
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
                    progress.Log("   ✘ Fără bloc [MedicalApp] și fără override manual — apasă „Editează” în pagina Verificare PDF-uri.");
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
                progress.Log("   ✘ Credite epuizate. Cumpără credite și relansează.");
                await RecordErrorAsync(db, batch, path, meta!.PatientName, "Out of credits");
                batch.NotSends++; progress.NotSends++;
                return;
            }

            // 3. Call Gemini with retry + Flash→Pro fallback (mirrors InterpretationController logic).
            InterpretationResult? result = await CallGeminiWithRetryAsync(gemini, bytes, fileName, progress, ct);
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
                    progress.Log($"   ⚙ LOINC matcher: {matchStats.Matched} parametri rezolvați din {matchStats.Total}.");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "CAM batch {Id}: LoincMatcherClient failed (continuing without LOINC codes)", batch.Id);
                progress.Log("   ⚠ LOINC matcher indisponibil — Compare-ul nu va avea grupare per clasă.");
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
                    progress.Log($"   🛠 Status validator: {v.Corrected} status(uri) corectate din {v.Total} parametri.");
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
                progress.Log($"   + Pacient nou: {patient.Name} <{patient.Email}>");
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
                progress.Log("   ✘ Generare PDF interpretare eșuată: " + ex.Message);
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
                    progress.Log("   ⚠ Compare PDF a eșuat (continuăm): " + ex.Message);
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
                progress.Log("   ✘ Email eșuat: " + ex.Message);
                await RecordErrorAsync(db, batch, path, patient.Name, "Email failure: " + ex.Message);
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
                progress.Log("   ⚠ Mutarea fișierului în Sends a eșuat (rămâne în Original): " + ex.Message);
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

            progress.Log($"   ✓ Trimis la {patient.Email}{(comparePdf != null ? " (+ comparație)" : "")}");
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
            CamBatchProgress progress,
            CancellationToken ct)
        {
            const int maxAttempts = 7;
            const int firstFallbackThreshold = 2;   // after attempt 2 → switch to Pro
            const int secondFallbackThreshold = 5;  // after attempt 5 → switch to next-gen Pro

            using var settingsScope = _scopeFactory.CreateScope();
            var settings = settingsScope.ServiceProvider
                .GetRequiredService<IOptions<GeminiSettings>>().Value;

            string? modelOverride = null;     // null = use primary (Flash)
            int currentTier = 1;              // 1 = primary, 2 = first fallback, 3 = second fallback

            // Resolve display labels once (operator-facing, anonymized).
            string LabelFor(int tier) => tier switch
            {
                2 => "motor MedicalApp+",
                3 => "motor MedicalApp Plus",
                _ => "motor MedicalApp"
            };

            int attempts = 0;
            Exception? lastEx = null;

            while (attempts < maxAttempts)
            {
                attempts++;
                try
                {
                    using var ms = new MemoryStream(pdfBytes);
                    var resp = await gemini.InterpretPdfAsync(
                        ms, fileName, "ro",
                        patientContext: null,
                        ct: ct,
                        modelOverride: modelOverride);
                    return resp.Result;
                }
                // ---------- TRANSIENT: explicit upstream error (429/503/5xx) ----------
                catch (GeminiTransientException ex)
                {
                    lastEx = ex;
                    if (TryPromoteTier(attempts, ref currentTier, ref modelOverride, settings, progress))
                        continue; // promoted — try again immediately without delay
                    if (attempts >= maxAttempts) break;

                    int wait = BackoffMs(attempts);
                    progress.Log($"   ⏳ {LabelFor(currentTier)} suprasolicitat ({ex.HttpStatusCode}), " +
                                 $"reîncerc în {wait / 1000}s (try {attempts}/{maxAttempts})…");
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
                    progress.Log($"   ⌛ {LabelFor(currentTier)} a depășit timpul de răspuns " +
                                 $"(try {attempts}/{maxAttempts}).");
                    if (TryPromoteTier(attempts, ref currentTier, ref modelOverride, settings, progress))
                        continue;
                    if (attempts >= maxAttempts) break;

                    int wait = BackoffMs(attempts);
                    progress.Log($"   ⏳ Reîncerc în {wait / 1000}s…");
                    try { await Task.Delay(wait, ct); }
                    catch (OperationCanceledException) { return null; }
                }
                // ---------- TRANSIENT: network-level timeout via HttpRequestException ----------
                catch (HttpRequestException ex) when (
                    ex.Message.Contains("timeout", StringComparison.OrdinalIgnoreCase)
                    || ex.Message.Contains("timed out", StringComparison.OrdinalIgnoreCase))
                {
                    lastEx = ex;
                    progress.Log($"   ⌛ {LabelFor(currentTier)} răspuns întârziat de rețea " +
                                 $"(try {attempts}/{maxAttempts}).");
                    if (TryPromoteTier(attempts, ref currentTier, ref modelOverride, settings, progress))
                        continue;
                    if (attempts >= maxAttempts) break;

                    int wait = BackoffMs(attempts);
                    progress.Log($"   ⏳ Reîncerc în {wait / 1000}s…");
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
                        progress.Log($"   ⚠ Răspuns trunchiat și nu există alt motor configurat.");
                        return null;
                    }
                    modelOverride = nextModel;
                    currentTier++;
                    attempts--; // refund this attempt — promotion is "free"
                    progress.Log($"   ↪ Răspuns trunchiat. Comut pe {LabelFor(currentTier)} (output mai mare).");
                }
                // ---------- USER CANCELLATION: honor immediately ----------
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    progress.Log("   ✘ Anulat de operator.");
                    return null;
                }
                // ---------- NON-TRANSIENT: fail fast ----------
                catch (Exception ex)
                {
                    progress.Log($"   ✘ {LabelFor(currentTier)} a eșuat (non-transient): {ex.Message}");
                    return null;
                }
            }

            progress.Log($"   ✘ Toate încercările au eșuat (max {maxAttempts}, ultima cu {LabelFor(currentTier)}): " +
                         (lastEx?.Message ?? "?"));
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
            GeminiSettings settings, CamBatchProgress progress)
        {
            // 1 → 2 : after attempt 2, switch to FallbackModel (Pro)
            if (currentTier == 1
                && attempts >= 2
                && !string.IsNullOrWhiteSpace(settings.FallbackModel)
                && !string.Equals(settings.FallbackModel, settings.Model, StringComparison.OrdinalIgnoreCase))
            {
                modelOverride = settings.FallbackModel;
                currentTier = 2;
                progress.Log($"   ↪ Comut pe motor MedicalApp+.");
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
                progress.Log($"   ↪ Comut pe motor MedicalApp Plus (plasă de siguranță).");
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
