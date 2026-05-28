using System.Text.Json;
using MedicalApp.Data;
using MedicalApp.Models;
using Microsoft.EntityFrameworkCore;

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
                            db, files, extractor, gemini, pdfGen, compareGen, email,
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

            // 1. Decide patient identification path:
            //    (a) operator override → use as-is, skip auto-extraction
            //    (b) explicit [MedicalApp] block → use, no Gemini cost saved later
            //    (c) Gemini-first → call Gemini once, then read PatientInfo.Name
            //        from the AI's structured output (which is much more reliable
            //        than PdfPig text + regex on weird PDF layouts).
            CamPdfMetadata? meta = null;
            bool needGeminiForName = false;

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
                    // No block, valid medical PDF → identify via Gemini's PatientInfo.
                    needGeminiForName = true;
                    // We still need an email candidate. Re-run extractor with NO
                    // blacklist (per user's decision to drop the blacklist UI),
                    // pick the first email — Gemini will provide the authoritative
                    // patient name a moment later.
                    var emailRx = new System.Text.RegularExpressions.Regex(
                        @"\b[A-Z0-9._%+\-]+@[A-Z0-9.\-]+\.[A-Z]{2,}\b",
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    var raw = PdfTextExtractor.Extract(new MemoryStream(bytes));
                    var emailMatch = emailRx.Match(raw);
                    if (!emailMatch.Success)
                    {
                        progress.Log("   ✘ Niciun email găsit în PDF.");
                        await RecordErrorAsync(db, batch, path, null, "Email not found in PDF");
                        batch.NotSends++; progress.NotSends++;
                        await MoveToErrorsIfRetriesExhaustedAsync(db, batch, path, errorsFolder);
                        return;
                    }
                    meta = new CamPdfMetadata
                    {
                        PatientEmail = emailMatch.Value.Trim(),
                        IsMedicalLabReport = true,
                        IsValid = false // name still missing — filled after Gemini
                    };
                }
            }

            // 2. Check credit budget BEFORE we spend AI tokens
            if (user == null || user.TotalAvailableCredits <= 0)
            {
                progress.Log("   ✘ Credite epuizate. Cumpără credite și relansează.");
                await RecordErrorAsync(db, batch, path, meta.PatientName, "Out of credits");
                batch.NotSends++; progress.NotSends++;
                return;
            }

            // 3. Call Gemini with retry + Flash→Pro fallback (mirrors InterpretationController logic).
            InterpretationResult? result = await CallGeminiWithRetryAsync(gemini, bytes, fileName, progress, ct);
            if (result == null)
            {
                await RecordErrorAsync(db, batch, path, meta.PatientName, "AI exhausted retries (incl. fallback model)");
                batch.NotSends++; progress.NotSends++;
                return;
            }

            // 3b. If we still need the patient name, pull it from Gemini's structured output.
            if (needGeminiForName)
            {
                var aiName = result.PatientInfo?.Name?.Trim();
                if (string.IsNullOrWhiteSpace(aiName))
                {
                    progress.Log("   ✘ Gemini nu a putut identifica numele pacientului.");
                    await RecordErrorAsync(db, batch, path, null, "Patient name missing from AI output");
                    batch.NotSends++; progress.NotSends++;
                    return;
                }
                meta.PatientName = aiName;
                meta.IsValid = true;
                progress.Log($"   ✓ Identificat de Gemini: {aiName} <{meta.PatientEmail}>");
            }

            // 4. Find or create patient (lookup by NameKey + Email)
            var nameKey = CamPatientKey.Normalize(meta.PatientName!);
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
        private async Task<InterpretationResult?> CallGeminiWithRetryAsync(
            IMedicalInterpretationProvider gemini,
            byte[] pdfBytes,
            string fileName,
            CamBatchProgress progress,
            CancellationToken ct)
        {
            const int maxAttempts = 5;
            const int fallbackThreshold = 2;
            // Pull fallback settings via the registered options inside the runner's scope.
            // We do this lazily here (instead of constructor-inject) to keep the service
            // constructor signature stable.
            using var settingsScope = _scopeFactory.CreateScope();
            var settings = settingsScope.ServiceProvider
                .GetRequiredService<Microsoft.Extensions.Options.IOptions<GeminiSettings>>().Value;

            int transient = 0;
            string? modelOverride = null;
            Exception? lastEx = null;

            while (transient < maxAttempts)
            {
                try
                {
                    using var ms = new MemoryStream(pdfBytes);
                    var resp = await gemini.InterpretPdfAsync(ms, fileName, "ro", patientContext: null, ct: ct, modelOverride: modelOverride);
                    return resp.Result;
                }
                catch (GeminiTransientException ex)
                {
                    transient++;
                    lastEx = ex;
                    if (modelOverride == null
                        && transient >= fallbackThreshold
                        && !string.IsNullOrWhiteSpace(settings.FallbackModel)
                        && !string.Equals(settings.FallbackModel, settings.Model, StringComparison.OrdinalIgnoreCase))
                    {
                        modelOverride = settings.FallbackModel;
                        progress.Log($"   ↪ Comut pe modelul fallback: {modelOverride}");
                    }
                    if (transient >= maxAttempts) break;

                    int[] delaysMs = { 5_000, 15_000, 30_000, 60_000 };
                    int wait = delaysMs[Math.Min(transient - 1, delaysMs.Length - 1)];
                    progress.Log($"   ⏳ Gemini suprasolicitat ({ex.HttpStatusCode}), reîncerc în {wait / 1000}s (try {transient}/{maxAttempts})...");
                    try { await Task.Delay(wait, ct); }
                    catch (OperationCanceledException) { return null; }
                }
                catch (Exception ex)
                {
                    // Non-transient — fail fast.
                    progress.Log("   ✘ Gemini a eșuat (non-transient): " + ex.Message);
                    return null;
                }
            }

            progress.Log("   ✘ Gemini a eșuat după toate încercările (inclusiv fallback): " + (lastEx?.Message ?? "?"));
            return null;
        }

        private static DateTime? TryParseDate(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            string[] formats =
            {
                "yyyy-MM-dd", "yyyy/MM/dd", "dd/MM/yyyy", "dd-MM-yyyy", "dd.MM.yyyy",
                "d/M/yyyy", "d-M-yyyy", "d.M.yyyy", "MM/dd/yyyy"
            };
            foreach (var f in formats)
                if (DateTime.TryParseExact(raw.Trim(), f, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeLocal, out var d)) return d;
            return DateTime.TryParse(raw, out var any) ? any : (DateTime?)null;
        }
    }
}
