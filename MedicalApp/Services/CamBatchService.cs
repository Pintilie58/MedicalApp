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

            // 1. Extract metadata
            var meta = extractor.Extract(bytes, fileName);
            if (!meta.IsValid)
            {
                progress.Log($"   ✘ Metadata invalidă: {meta.Reason}");
                await RecordErrorAsync(db, batch, path, meta.PatientName, meta.Reason ?? "Metadata extract failed");
                batch.NotSends++; progress.NotSends++;
                await MoveToErrorsIfRetriesExhaustedAsync(db, batch, path, errorsFolder);
                return;
            }

            // 2. Check credit budget BEFORE we spend AI tokens
            if (user == null || user.TotalAvailableCredits <= 0)
            {
                progress.Log("   ✘ Credite epuizate. Cumpără credite și relansează.");
                await RecordErrorAsync(db, batch, path, meta.PatientName, "Out of credits");
                batch.NotSends++; progress.NotSends++;
                return;
            }

            // 3. Find or create patient (lookup by NameKey + Email)
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

            // 4. Call Gemini
            InterpretationResult? result;
            try
            {
                using var ms = new MemoryStream(bytes);
                var resp = await gemini.InterpretPdfAsync(ms, fileName, "ro", patientContext: null, ct: ct);
                result = resp.Result;
            }
            catch (Exception ex)
            {
                progress.Log("   ✘ Gemini a eșuat: " + ex.Message);
                await RecordErrorAsync(db, batch, path, meta.PatientName, "AI failure: " + ex.Message);
                batch.NotSends++; progress.NotSends++;
                return;
            }
            if (result == null)
            {
                progress.Log("   ✘ Răspuns AI gol.");
                await RecordErrorAsync(db, batch, path, meta.PatientName, "AI returned null");
                batch.NotSends++; progress.NotSends++;
                return;
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
