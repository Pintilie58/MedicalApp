using System.Collections.Concurrent;
using MedicalApp.Data;
using MedicalApp.Models;
using MedicalApp.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

// Probe for CamSettings:MaxParallelFiles (June 2026): the CAM runner may
// pre-launch the Gemini call of the next N-1 files while everything else
// (DB, credits, patient upsert, compare PDF, email) stays strictly sequential.

int fails = 0;
void Check(string what, bool ok, string? detail = null)
{
    Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {what}{(detail is null ? "" : "  ->  " + detail)}");
    if (!ok) fails++;
}

async Task<Scenario> RunScenario(string title, int parallel, int fileCount, int credits,
    int geminiDelayMs = 250, string? failFile = null, string? noOverrideFile = null,
    bool samePatientPair = false, bool cancelAfterFirst = false)
{
    Console.WriteLine($"\n=== {title} (MaxParallelFiles={parallel}, files={fileCount}, credits={credits}) ===");
    var dbName = "campar-" + Guid.NewGuid();
    var gemini = new FakeGemini { DelayMs = geminiDelayMs, FailFor = failFile };
    var store = new MemoryCamFileStore();
    var email = new FakeEmail();

    var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["CamBatch:AttachDebugJson"] = "false"
    }).Build();

    var services = new ServiceCollection();
    services.AddSingleton<IConfiguration>(cfg);
    services.AddDbContext<AppDbContext>(o => o.UseInMemoryDatabase(dbName));
    services.AddLogging(b => b.SetMinimumLevel(LogLevel.Error));
    services.AddHttpClient();
    services.AddSingleton<IMedicalInterpretationProvider>(gemini);
    services.AddSingleton<ICamFileStore>(store);
    services.AddSingleton<IEmailService>(email);
    services.AddSingleton<IAiUsageLogger, FakeUsage>();
    services.AddSingleton<CamBatchRegistry>();
    services.AddScoped<CamPdfMetadataExtractor>();
    services.AddSingleton<PdfReportGenerator>();
    services.AddSingleton<CamComparePdfGenerator>();
    services.Configure<GeminiSettings>(o => { o.Model = "flash"; o.FallbackModel = "pro"; o.SecondaryFallbackModel = "plus"; });
    services.Configure<LoincMatcherSettings>(o => { o.Enabled = false; o.BaseUrl = "http://127.0.0.1:1"; });
    services.Configure<CamSettings>(o => o.MaxParallelFiles = parallel);
    services.AddSingleton<LoincContextVocabulary>();
    services.AddSingleton<LoincMatchCacheStore>();
    services.AddHttpClient<LoincMatcherClient>();
    services.AddScoped<CamBatchService>();
    var sp = services.BuildServiceProvider();

    int batchId;
    var clinicEmail = "clinic@example.com";
    var patientEmails = new List<string>();
    using (var scope = sp.CreateScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var user = new User { Email = clinicEmail, Parola = "x", Credite = credits, CreditRest = credits };
        db.Users.Add(user);
        var clinic = new Clinic { UserEmail = clinicEmail, Name = "Clinica Test", City = "Iasi", Address = "Str. 1" };
        db.Clinics.Add(clinic);
        await db.SaveChangesAsync();

        for (int i = 1; i <= fileCount; i++)
        {
            var name = $"file{i:00}.pdf";
            store.Files[name] = System.Text.Encoding.UTF8.GetBytes("%PDF-1.4 fake " + name);
            // samePatientPair: files 1 and 2 belong to the same patient (compare PDF expected on the 2nd)
            var idx = samePatientPair && i == 2 ? 1 : i;
            var pEmail = $"patient{idx}@example.com";
            patientEmails.Add(pEmail);
            if (name != noOverrideFile)
                db.ClinicPdfOverrides.Add(new ClinicPdfOverride
                {
                    ClinicId = clinic.Id, FileName = name,
                    OverrideName = $"Pacient {idx}", OverrideEmail = pEmail
                });
        }
        var batch = new ClinicBatchRun { ClinicId = clinic.Id, Status = "Running", LanguageCode = "ro" };
        db.ClinicBatchRuns.Add(batch);
        await db.SaveChangesAsync();
        batchId = batch.Id;
    }

    var registry = sp.GetRequiredService<CamBatchRegistry>();
    var sw = System.Diagnostics.Stopwatch.StartNew();
    Task run;
    using (var scope = sp.CreateScope())
    {
        var runner = scope.ServiceProvider.GetRequiredService<CamBatchService>();
        run = runner.RunAsync(batchId, "ro");
        if (cancelAfterFirst)
        {
            while (gemini.Completed < 1) await Task.Delay(10);
            registry.Get(batchId)!.Cts.Cancel();
        }
        await run;
    }
    sw.Stop();

    using var verify = sp.CreateScope();
    var vdb = verify.ServiceProvider.GetRequiredService<AppDbContext>();
    var b = await vdb.ClinicBatchRuns.AsNoTracking().FirstAsync(x => x.Id == batchId);
    var u = await vdb.Users.AsNoTracking().FirstAsync(x => x.Email == clinicEmail);
    var patients = await vdb.ClinicPatients.AsNoTracking().CountAsync();
    var analyses = await vdb.ClinicAnalyses.AsNoTracking().CountAsync();
    var errors = await vdb.ClinicBatchErrors.AsNoTracking().Where(e => e.BatchRunId == batchId).ToListAsync();
    var progress = registry.Get(batchId)!;

    Console.WriteLine($"  status={b.Status} total={b.TotalFiles} sent={b.FilesSent} interpreted={b.FilesInterpreted} compared={b.FilesCompared} notSends={b.NotSends} " +
                      $"geminiCalls={gemini.Calls.Count} maxInFlight={gemini.MaxInFlight} emails={email.Sent.Count} creditsConsumed={u.CreditConsum} patients={patients} analyses={analyses} " +
                      $"sendsFolder={store.Folder(CamFolder.Sends).Count} elapsed={sw.ElapsedMilliseconds}ms");
    foreach (var line in progress.LogSnapshot().Take(12)) Console.WriteLine("    " + line);

    return new Scenario(b, u, gemini, email, store, patients, analyses, errors, sw.ElapsedMilliseconds, patientEmails, progress);
}

// ---------------------------------------------------------------- 1. baseline sequential
{
    var s = await RunScenario("Sequential baseline", parallel: 1, fileCount: 5, credits: 10);
    Check("1a all 5 files sent", s.Batch.FilesSent == 5 && s.Batch.Status == "Completed");
    Check("1b exactly one Gemini call in flight at a time", s.Gemini.MaxInFlight == 1, $"max={s.Gemini.MaxInFlight}");
    Check("1c 5 Gemini calls, 5 emails, 5 credits", s.Gemini.Calls.Count == 5 && s.Email.Sent.Count == 5 && s.User.CreditConsum == 5);
    Check("1d files moved to Sends", s.Store.Folder(CamFolder.Sends).Count == 5 && s.Store.Folder(CamFolder.Original).Count == 0);
    Check("1e no 'parallel mode' log line", !s.Progress.LogSnapshot().Any(l => l.Contains("Mod paralel")));
}

// ---------------------------------------------------------------- 2. parallel happy path
{
    var s = await RunScenario("Parallel 4", parallel: 4, fileCount: 8, credits: 20, geminiDelayMs: 400);
    Check("2a all 8 files sent, batch Completed", s.Batch.FilesSent == 8 && s.Batch.Status == "Completed", $"sent={s.Batch.FilesSent} status={s.Batch.Status}");
    Check("2b up to 4 Gemini calls in flight (>=3 observed)", s.Gemini.MaxInFlight <= 4 && s.Gemini.MaxInFlight >= 3, $"max={s.Gemini.MaxInFlight}");
    Check("2c exactly 8 Gemini calls (no double call)", s.Gemini.Calls.Count == 8, $"calls={s.Gemini.Calls.Count}");
    Check("2d emails in file order", s.Email.Sent.Select(e => e.to).SequenceEqual(s.PatientEmails), string.Join(",", s.Email.Sent.Select(e => e.to)));
    Check("2e counters consistent: interpreted=sent=8, notSends=0, credits=8", s.Batch.FilesInterpreted == 8 && s.Batch.NotSends == 0 && s.User.CreditConsum == 8);
    Check("2f 8 patients / 8 analyses (no duplicates)", s.Patients == 8 && s.Analyses == 8, $"patients={s.Patients} analyses={s.Analyses}");
    Check("2g wall clock clearly below sequential (8x400ms=3200ms)", s.ElapsedMs < 2000, $"{s.ElapsedMs}ms");
    Check("2h prefetch log lines present (last 30 lines kept)", s.Progress.LogSnapshot().Any(l => l.Contains("Pre-procesare AI")));
}

// ---------------------------------------------------------------- 3. credit cap
{
    var s = await RunScenario("Parallel 4, only 3 credits", parallel: 4, fileCount: 6, credits: 3);
    Check("3a exactly 3 Gemini calls (never more than credits)", s.Gemini.Calls.Count == 3, $"calls={s.Gemini.Calls.Count}");
    Check("3b 3 sent + 3 notSends (out of credits)", s.Batch.FilesSent == 3 && s.Batch.NotSends == 3, $"sent={s.Batch.FilesSent} notSends={s.Batch.NotSends}");
    Check("3c 3 'Out of credits' errors recorded", s.Errors.Count(e => e.Reason.Contains("Out of credits")) == 3);
    Check("3d credits consumed = 3", s.User.CreditConsum == 3);
}

// ---------------------------------------------------------------- 4. one file fails at Gemini (non-transient)
{
    var s = await RunScenario("Parallel 3, file03 fails at Gemini", parallel: 3, fileCount: 5, credits: 10, failFile: "file03.pdf");
    Check("4a 4 sent, 1 notSend", s.Batch.FilesSent == 4 && s.Batch.NotSends == 1, $"sent={s.Batch.FilesSent} notSends={s.Batch.NotSends}");
    Check("4b failed file called Gemini exactly once (no inline re-try)", s.Gemini.Calls.Count(c => c == "file03.pdf") == 1, $"calls for file03={s.Gemini.Calls.Count(c => c == "file03.pdf")}");
    Check("4c error reason = AI exhausted", s.Errors.Any(e => e.FileName == "file03.pdf" && e.Reason.Contains("AI exhausted")));
    Check("4d credits consumed = 4", s.User.CreditConsum == 4);
    Check("4e file03 still in Original (not moved)", s.Store.Folder(CamFolder.Original).Contains("file03.pdf"));
}

// ---------------------------------------------------------------- 5. ineligible file (no override, no [MedicalApp] block)
{
    var s = await RunScenario("Parallel 3, file02 has no override", parallel: 3, fileCount: 4, credits: 10, noOverrideFile: "file02.pdf");
    Check("5a no Gemini call for the ineligible file", !s.Gemini.Calls.Contains("file02.pdf"));
    Check("5b 3 sent, 1 notSend", s.Batch.FilesSent == 3 && s.Batch.NotSends == 1);
    Check("5c 3 Gemini calls total", s.Gemini.Calls.Count == 3);
}

// ---------------------------------------------------------------- 6. same patient twice in one batch -> compare PDF, sequential order kept
{
    var s = await RunScenario("Parallel 4, files 1+2 same patient", parallel: 4, fileCount: 4, credits: 10, samePatientPair: true);
    Check("6a 4 sent", s.Batch.FilesSent == 4);
    Check("6b exactly 1 compare PDF (second file of the pair)", s.Batch.FilesCompared == 1, $"compared={s.Batch.FilesCompared}");
    Check("6c 3 patients, 4 analyses (no duplicate patient)", s.Patients == 3 && s.Analyses == 4, $"patients={s.Patients} analyses={s.Analyses}");
    Check("6d emails in file order", s.Email.Sent.Select(e => e.to).SequenceEqual(s.PatientEmails));
}

// ---------------------------------------------------------------- 7. cancel while prefetching
{
    var s = await RunScenario("Parallel 4, cancel after first file", parallel: 4, fileCount: 8, credits: 20, geminiDelayMs: 600, cancelAfterFirst: true);
    Check("7a batch Cancelled", s.Batch.Status == "Cancelled", s.Batch.Status);
    Check("7b fewer than 8 files sent", s.Batch.FilesSent < 8, $"sent={s.Batch.FilesSent}");
    Check("7c sent + notSends == processed, no counter corruption", s.Batch.FilesSent + s.Batch.NotSends == s.Progress.Processed, $"sent={s.Batch.FilesSent} notSends={s.Batch.NotSends} processed={s.Progress.Processed}");
}

Console.WriteLine();
Console.WriteLine(fails == 0 ? "ALL CHECKS PASSED" : $"{fails} CHECK(S) FAILED");
return fails == 0 ? 0 : 1;

sealed record Scenario(ClinicBatchRun Batch, User User, FakeGemini Gemini, FakeEmail Email, MemoryCamFileStore Store,
    int Patients, int Analyses, List<ClinicBatchError> Errors, long ElapsedMs, List<string> PatientEmails, CamBatchProgress Progress);

sealed class FakeGemini : IMedicalInterpretationProvider
{
    public int DelayMs { get; set; } = 250;
    public string? FailFor { get; set; }
    public ConcurrentBag<string> Calls { get; } = new();
    int _inFlight, _max, _completed;
    public int MaxInFlight => _max;
    public int Completed => _completed;

    public async Task<(InterpretationResult Result, int InputTokens, int OutputTokens, string RawResponse)> InterpretPdfAsync(
        Stream pdfStream, string fileName, string languageCode, PatientContext? patientContext = null,
        CancellationToken ct = default, string? modelOverride = null)
    {
        Calls.Add(fileName);
        var now = Interlocked.Increment(ref _inFlight);
        int seen; do { seen = _max; } while (now > seen && Interlocked.CompareExchange(ref _max, now, seen) != seen);
        try
        {
            await Task.Delay(DelayMs, ct);
            if (fileName == FailFor) throw new ArgumentException("fake non-transient failure");
            var r = new InterpretationResult
            {
                IsMedicalAnalysis = true,
                Summary = "ok " + fileName,
                PatientInfo = new PatientInfo { Name = "P", DateTaken = "2026-01-01" },
                KeyResults = new List<KeyResult>
                {
                    new() { Parameter = "Glucoza", Value = "95", Unit = "mg/dL", ReferenceRange = "70-100", Status = "normal", Explanation = "ok" }
                }
            };
            return (r, 1000, 500, "{}");
        }
        finally
        {
            Interlocked.Decrement(ref _inFlight);
            Interlocked.Increment(ref _completed);
        }
    }

    public Task<(InterpretationResult Result, int InputTokens, int OutputTokens, string RawResponse)> InterpretAsync(string extractedText, string languageCode, CancellationToken ct = default)
        => throw new NotSupportedException();
    public Task<(InterpretationResult Result, int InputTokens, int OutputTokens, string RawResponse)> InterpretTextAsync(string extractedText, string fileName, string languageCode, PatientContext? patientContext = null, CancellationToken ct = default, string? modelOverride = null)
        => throw new NotSupportedException();
}

sealed class FakeUsage : IAiUsageLogger
{
    public Task LogAsync(string source, string? userEmail, int? clinicId, string modelUsed, int inputTokens, int outputTokens, string status, string? errorMessage, CancellationToken ct = default)
        => Task.CompletedTask;
}

sealed class MemoryCamFileStore : ICamFileStore
{
    public ConcurrentDictionary<string, byte[]> Files { get; } = new();           // Original
    readonly ConcurrentDictionary<CamFolder, ConcurrentDictionary<string, byte[]>> _other = new();

    ConcurrentDictionary<string, byte[]> Of(CamFolder f) => f == CamFolder.Original ? Files : _other.GetOrAdd(f, _ => new());
    public List<string> Folder(CamFolder f) => Of(f).Keys.OrderBy(k => k).ToList();

    public string GetDisplayLocation(Clinic clinic, CamFolder? folder = null) => "memory";
    public Task<string> EnsureClinicFoldersAsync(Clinic clinic, CancellationToken ct = default) => Task.FromResult("memory");
    public Task<IReadOnlyList<CamFileEntry>> ListAsync(Clinic clinic, CamFolder folder, string? extension = null, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<CamFileEntry>>(Of(folder).Select(kv => new CamFileEntry(kv.Key, kv.Value.Length, DateTime.UtcNow)).ToList());
    public Task<bool> ExistsAsync(Clinic clinic, CamFolder folder, string name, CancellationToken ct = default) => Task.FromResult(Of(folder).ContainsKey(name));
    public Task<byte[]?> ReadAsync(Clinic clinic, CamFolder folder, string name, CancellationToken ct = default)
        => Task.FromResult(Of(folder).TryGetValue(name, out var b) ? b : null);
    public Task<string> WriteAsync(Clinic clinic, CamFolder folder, string name, byte[] content, bool overwrite = false, CancellationToken ct = default)
    { Of(folder)[name] = content; return Task.FromResult(name); }
    public Task<string?> MoveAsync(Clinic clinic, CamFolder from, CamFolder to, string name, CancellationToken ct = default)
    {
        if (!Of(from).TryRemove(name, out var b)) return Task.FromResult<string?>(null);
        Of(to)[name] = b; return Task.FromResult<string?>(name);
    }
    public Task<bool> DeleteAsync(Clinic clinic, CamFolder folder, string name, CancellationToken ct = default) => Task.FromResult(Of(folder).TryRemove(name, out _));
}

sealed class FakeEmail : IEmailService
{
    public List<(string to, string subject)> Sent { get; } = new();
    public Task SendEmailAsync(string toEmail, string subject, string htmlBody) { Sent.Add((toEmail, subject)); return Task.CompletedTask; }
    public Task SendEmailWithAttachmentsAsync(string toEmail, string subject, string htmlBody, IEnumerable<(byte[] Bytes, string FileName, string MimeType)> attachments)
    { Sent.Add((toEmail, subject)); return Task.CompletedTask; }
    public Task SendEmailWithAttachmentAsync(string toEmail, string subject, string htmlBody, byte[] attachment, string fileName)
    { Sent.Add((toEmail, subject)); return Task.CompletedTask; }
}
