using MedicalApp.Data;
using QuestPDF.Fluent;
using QuestPDF.Infrastructure;
using MedicalApp.Models;
using MedicalApp.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Text.Json;

int fails = 0;
void Check(string label, bool ok, string detail = "")
{
    Console.WriteLine((ok ? "PASS  " : "FAIL  ") + label + (detail.Length > 0 ? "  ->  " + detail : ""));
    if (!ok) fails++;
}

QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;

// ---------------------------------------------------------------- fake PDF
byte[] labPdf = QuestPDF.Fluent.Document.Create(c =>
{
    c.Page(p => p.Content().Column(col =>
    {
        col.Item().Text("Buletin analize");
        col.Item().Text("Hemoglobina 9.1 g/dL   12-16 g/dL");
        col.Item().Text("LDL colesterol 180 mg/dL   0-130 mg/dL");
        col.Item().Text("Glicemie 99 mg/dL  70-99 mg/dL");
        col.Item().Text("Creatinina 0.9 mg/dL  0.7-1.2 mg/dL");
    }));
}).GeneratePdf();

// ---------------------------------------------------------------- DI
var services = new ServiceCollection();
services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning).AddConsole());
var dbName = Guid.NewGuid().ToString();
services.AddDbContext<AppDbContext>(o => o.UseInMemoryDatabase(dbName),
    ServiceLifetime.Scoped);
services.Configure<InterpretationSettings>(o => o.Provider = "Gemini");
services.Configure<GeminiSettings>(o => { o.Model = "gemini-2.5-flash"; o.ApiKey = "x"; });
services.Configure<LoincMatcherSettings>(o => { o.Enabled = false; });
services.AddSingleton<FakeAi>();
services.AddSingleton<IMedicalInterpretationProvider>(sp => sp.GetRequiredService<FakeAi>());
services.AddSingleton<FakeEmail>();
services.AddSingleton<IEmailService>(sp => sp.GetRequiredService<FakeEmail>());
services.AddScoped<IAiUsageLogger, AiUsageLogger>();
services.AddScoped<PdfReportGenerator>();
services.AddHttpClient<LoincMatcherClient>();
services.AddSingleton<InterpretationProgressTracker>();
services.AddSingleton<InterpretationJobQueue>();
services.AddScoped<B2cInterpretationRunner>();
services.AddSingleton<InterpretationQueueWorker>();

var sp = services.BuildServiceProvider();
var tracker = sp.GetRequiredService<InterpretationProgressTracker>();
var queue = sp.GetRequiredService<InterpretationJobQueue>();
var fakeAi = sp.GetRequiredService<FakeAi>();
var fakeEmail = sp.GetRequiredService<FakeEmail>();

async Task Seed()
{
    using var scope = sp.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Users.Add(new User { Email = "u@test.ro", Parola = "x", Credite = 10, CreditConsum = 0, CreditRest = 10 });
    db.Profiles.Add(new Profile { Id = 1, UserEmail = "u@test.ro", Name = "Eu", IsDefault = true });
    // Freemium: nicio achizitie (Credite = 0), doar 1 credit bonus.
    db.Users.Add(new User { Email = "free@test.ro", Parola = "x", Credite = 0, BonusCredits = 1 });
    db.Profiles.Add(new Profile { Id = 2, UserEmail = "free@test.ro", Name = "Eu", IsDefault = true });
    await db.SaveChangesAsync();
}
await Seed();

async Task<int> NewPendingRow(string email)
{
    using var scope = sp.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    var user = await db.Users.FirstAsync(u => u.Email == email);
    CreditLedger.ReserveOne(user);
    var row = new InterpretationHistory
    {
        UserEmail = email, OriginalFileName = "buletin.pdf", Language = "ro",
        Status = "processing", CreditsConsumed = 1, ProfileId = 1,
        PdfSha256 = "hash", CreatedAt = DateTime.UtcNow
    };
    db.InterpretationHistories.Add(row);
    await db.SaveChangesAsync();
    return row.Id;
}

async Task<(string status, int credits, int rest, int consum)> RowState(int id)
{
    using var scope = sp.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    var r = await db.InterpretationHistories.FirstAsync(h => h.Id == id);
    var u = await db.Users.FirstAsync(x => x.Email == r.UserEmail);
    return (r.Status, r.CreditsConsumed, u.CreditRest, u.CreditConsum);
}

async Task Run(InterpretationJob job)
{
    using var scope = sp.CreateScope();
    var runner = scope.ServiceProvider.GetRequiredService<B2cInterpretationRunner>();
    await runner.RunAsync(job, CancellationToken.None);
}

InterpretationJob Job(int historyId, string token) => new(
    historyId, "u@test.ro", 1, "Eu", labPdf, "buletin.pdf", "hash", "ro", false, token);

// =================================================================
// 1. Credit ledger
// =================================================================
{
    var u = new User { Credite = 5, CreditConsum = 0, CreditRest = 5, BonusCredits = 1 };
    CreditLedger.ReserveOne(u);
    Check("Rezervare consuma bonusul primul", u.BonusCreditsConsumed == 1 && u.CreditRest == 5);
    CreditLedger.RefundOne(u);
    Check("Restituirea intoarce bonusul", u.BonusCreditsConsumed == 0 && u.CreditRest == 5);
    CreditLedger.ReserveOne(u); CreditLedger.ReserveOne(u); // bonus, apoi plătit
    Check("Al doilea consum ia din creditele platite", u.CreditConsum == 1 && u.CreditRest == 4,
        $"consum={u.CreditConsum} rest={u.CreditRest}");
    CreditLedger.RefundOne(u); CreditLedger.RefundOne(u);
    Check("Restituirile readuc totul la initial",
        u.CreditRest == 5 && u.CreditConsum == 0 && u.BonusCreditsConsumed == 0);
}

// =================================================================
// 2. Coada: 1 job per utilizator
// =================================================================
{
    var j1 = Job(0, "t1");
    Check("Utilizator liber la inceput", !queue.IsUserBusy("u@test.ro"));
    Check("Primul job intra in coada", queue.TryEnqueue(j1));
    Check("Utilizatorul e marcat ocupat", queue.IsUserBusy("u@test.ro"));
    Check("Al doilea job e refuzat", !queue.TryEnqueue(Job(0, "t2")));
    queue.ReleaseUser("u@test.ro");
    Check("Dupa eliberare poate relansa", !queue.IsUserBusy("u@test.ro") && queue.TryEnqueue(Job(0, "t3")));
    queue.ReleaseUser("u@test.ro");
    Check("Coada chiar a primit joburile", queue.Reader.Count == 2, queue.Reader.Count.ToString());
    while (queue.Reader.TryRead(out _)) { }
}

// =================================================================
// 3. Happy path: processing -> success, credit consumat, email trimis
// =================================================================
{
    fakeAi.Mode = FakeAi.Behaviour.Ok;
    var id = await NewPendingRow("u@test.ro");
    var before = await RowState(id);
    Check("Randul porneste ca 'processing'", before.status == "processing", before.status);

    await Run(Job(id, "tok-ok"));

    var after = await RowState(id);
    Check("Devine 'success'", after.status == "success", after.status);
    Check("Creditul rezervat rămâne consumat", after.credits == 1 && after.consum == 1,
        $"credits={after.credits} consum={after.consum}");
    Check("Emailul a fost trimis cu PDF atasat",
        fakeEmail.Sent.Count == 1 && fakeEmail.Sent[0].attachments == 1);
    var st = tracker.Get("tok-ok");
    Check("Progresul se termina cu stage=done", st?.Stage == "done", st?.Stage ?? "null");
    Check("Progresul da URL de redirect", st?.RedirectUrl == "/Account/Dashboard", st?.RedirectUrl ?? "null");
    Check("Progresul da id-ul din arhiva", st?.HistoryId == id, st?.HistoryId?.ToString() ?? "null");
    // (tabelul preliminar e publicat doar de serviciul Gemini real, prin OnStage)
}

// =================================================================
// 4. Esec AI -> error + credit restituit + mesaj de progres
// =================================================================
{
    fakeAi.Mode = FakeAi.Behaviour.Throw;
    fakeEmail.Sent.Clear();
    var id = await NewPendingRow("u@test.ro");
    await Run(Job(id, "tok-err"));
    var after = await RowState(id);
    Check("Esecul marcheaza randul 'error'", after.status == "error", after.status);
    Check("Creditul e restituit la esec", after.credits == 0 && after.consum == 1,
        $"credits={after.credits} consum={after.consum}");
    Check("Nu se trimite email la esec", fakeEmail.Sent.Count == 0);
    Check("Progresul arata eroare", tracker.Get("tok-err")?.Stage == "error");
    Check("Eroarea are mesaj pentru utilizator",
        !string.IsNullOrWhiteSpace(tracker.Get("tok-err")?.Error));
}

// =================================================================
// 5. PDF care nu e analiza medicala -> rejected + credit restituit
// =================================================================
{
    fakeAi.Mode = FakeAi.Behaviour.NotMedical;
    var id = await NewPendingRow("u@test.ro");
    await Run(Job(id, "tok-rej"));
    var after = await RowState(id);
    Check("Documentul non-medical e 'rejected'", after.status == "rejected", after.status);
    Check("Creditul e restituit la respingere", after.credits == 0, after.credits.ToString());
}

// =================================================================
// 6. Recuperare dupa repornirea aplicatiei
// =================================================================
{
    var id = await NewPendingRow("u@test.ro");
    var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger("probe");
    await StartupSeed.FailOrphanedInterpretationsAsync(sp, logger);
    var after = await RowState(id);
    Check("Jobul orfan devine 'error' la pornire", after.status == "error", after.status);
    Check("Creditul e restituit jobului orfan", after.credits == 0, after.credits.ToString());
}

// =================================================================
// 7. Freemium: redirect pe raportul din aplicatie, nu pe dashboard
// =================================================================
{
    fakeAi.Mode = FakeAi.Behaviour.Ok;
    var id = await NewPendingRow("free@test.ro");
    await Run(new InterpretationJob(id, "free@test.ro", 2, "Eu", labPdf, "buletin.pdf",
        "hash2", "ro", false, "tok-free"));
    var st = tracker.Get("tok-free");
    Check("Freemium e trimis la raportul pe ecran",
        st?.RedirectUrl == $"/Profiles/ViewReport/{id}", st?.RedirectUrl ?? "null");
}

// =================================================================
// 8. Worker-ul de fundal duce jobul la final si elibereaza slotul
// =================================================================
{
    fakeAi.Mode = FakeAi.Behaviour.Ok;
    var worker = sp.GetRequiredService<InterpretationQueueWorker>();
    await worker.StartAsync(CancellationToken.None);

    var id = await NewPendingRow("u@test.ro");
    Check("Jobul intra in coada pentru worker", queue.TryEnqueue(Job(id, "tok-worker")));

    var deadline = DateTime.UtcNow.AddSeconds(30);
    string status = "processing";
    while (DateTime.UtcNow < deadline)
    {
        status = (await RowState(id)).status;
        if (status != "processing") break;
        await Task.Delay(250);
    }
    Check("Workerul a finalizat jobul fara request HTTP", status == "success", status);
    await Task.Delay(500);
    Check("Slotul utilizatorului e eliberat dupa job", !queue.IsUserBusy("u@test.ro"));
    await worker.StopAsync(CancellationToken.None);
}

// =================================================================
// 9. Endpointul global de status (indicatorul din dreapta sus)
// =================================================================
{
    using var scope = sp.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    var ctrl = new MedicalApp.Controllers.InterpretationController(
        db,
        Options.Create(new GeminiSettings { Model = "gemini-2.5-flash" }),
        new Microsoft.Extensions.Caching.Memory.MemoryCache(
            new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions()),
        scope.ServiceProvider.GetRequiredService<LoincMatcherClient>(),
        scope.ServiceProvider.GetRequiredService<IAiUsageLogger>(),
        tracker, queue,
        scope.ServiceProvider.GetRequiredService<ILogger<MedicalApp.Controllers.InterpretationController>>());

    var http = new Microsoft.AspNetCore.Http.DefaultHttpContext();
    http.Session = new FakeSession("u@test.ro");
    ctrl.ControllerContext = new Microsoft.AspNetCore.Mvc.ControllerContext { HttpContext = http };

    // The action returns an anonymous type from another assembly, so we read
    // it the way the browser does: through its JSON shape.
    JsonElement Status()
    {
        var res = ctrl.JobStatus().GetAwaiter().GetResult() as Microsoft.AspNetCore.Mvc.JsonResult;
        return JsonDocument.Parse(JsonSerializer.Serialize(res!.Value!)).RootElement.Clone();
    }
    static bool Flag(JsonElement e, string n) => e.GetProperty(n).GetBoolean();
    static int? Num(JsonElement e, string n) =>
        e.GetProperty(n).ValueKind == JsonValueKind.Null ? null : e.GetProperty(n).GetInt32();
    static string? Str(JsonElement e, string n) =>
        e.GetProperty(n).ValueKind == JsonValueKind.Null ? null : e.GetProperty(n).GetString();

    // Momentan nu rulează nimic pentru acest user.
    var idle = Status();
    Check("Fara job activ, running = false", !Flag(idle, "running"));
    Check("Ultimul raport reusit e raportat", Num(idle, "lastDoneId") != null);
    Check("URL-ul raportului e corect",
        (Str(idle, "lastDoneUrl") ?? "").StartsWith("/Profiles/ViewReport/"), Str(idle, "lastDoneUrl") ?? "null");

    // Un job in lucru trebuie sa apara imediat.
    var pendingId = await NewPendingRow("u@test.ro");
    var busy = Status();
    Check("Cu job in lucru, running = true", Flag(busy, "running"));
    Check("Se raporteaza id-ul jobului in lucru", Num(busy, "runningId") == pendingId,
        Num(busy, "runningId")?.ToString() ?? "null");

    // Dupa finalizare, running dispare si lastDoneId ajunge la jobul nostru.
    var row = await db.InterpretationHistories.FirstAsync(h => h.Id == pendingId);
    row.Status = "success";
    await db.SaveChangesAsync();
    var done = Status();
    Check("Dupa finalizare running = false", !Flag(done, "running"));
    Check("lastDoneId ajunge la jobul urmarit", Num(done, "lastDoneId") == pendingId,
        Num(done, "lastDoneId")?.ToString() ?? "null");

    // Un job eșuat este raportat separat, ca sa putem afisa pastila roșie.
    row.Status = "error";
    await db.SaveChangesAsync();
    var failed = Status();
    Check("Jobul eșuat e raportat prin lastFailedId", Num(failed, "lastFailedId") == pendingId,
        Num(failed, "lastFailedId")?.ToString() ?? "null");

    // Fara sesiune (utilizator delogat) -> 401, ca sa opreasca polling-ul.
    var anon = new MedicalApp.Controllers.InterpretationController(
        db, Options.Create(new GeminiSettings()),
        new Microsoft.Extensions.Caching.Memory.MemoryCache(
            new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions()),
        scope.ServiceProvider.GetRequiredService<LoincMatcherClient>(),
        scope.ServiceProvider.GetRequiredService<IAiUsageLogger>(),
        tracker, queue,
        scope.ServiceProvider.GetRequiredService<ILogger<MedicalApp.Controllers.InterpretationController>>());
    var anonHttp = new Microsoft.AspNetCore.Http.DefaultHttpContext();
    anonHttp.Session = new FakeSession(null);
    anon.ControllerContext = new Microsoft.AspNetCore.Mvc.ControllerContext { HttpContext = anonHttp };
    var unauth = await anon.JobStatus();
    Check("Delogat -> 401 (polling-ul se opreste)",
        unauth is Microsoft.AspNetCore.Mvc.UnauthorizedResult);
}

Console.WriteLine(fails == 0 ? "\nALL PASS" : $"\n{fails} FAIL(S)");
return fails == 0 ? 0 : 1;

// =====================================================================
sealed class FakeAi : IMedicalInterpretationProvider
{
    public enum Behaviour { Ok, Throw, NotMedical }
    public Behaviour Mode { get; set; } = Behaviour.Ok;
    public int Calls { get; private set; }

    private (InterpretationResult, int, int, string) Build()
    {
        Calls++;
        if (Mode == Behaviour.Throw)
            throw new InvalidOperationException("fake model failure");

        var r = new InterpretationResult
        {
            IsMedicalAnalysis = Mode != Behaviour.NotMedical,
            RejectionReason = Mode == Behaviour.NotMedical ? "not a lab report" : null,
            Summary = "sumar",
            KeyResults = Mode == Behaviour.NotMedical
                ? new List<KeyResult>()
                : new List<KeyResult>
                {
                    new() { Parameter = "Hemoglobina", Value = "9.1", Unit = "g/dL", ReferenceRange = "12-16", Status = "low" },
                    new() { Parameter = "LDL colesterol", Value = "180", Unit = "mg/dL", ReferenceRange = "0-130", Status = "high" },
                    new() { Parameter = "Glicemie", Value = "99", Unit = "mg/dL", ReferenceRange = "70-99", Status = "borderline" },
                    new() { Parameter = "Creatinina", Value = "0.9", Unit = "mg/dL", ReferenceRange = "0.7-1.2", Status = "normal" }
                }
        };
        return (r, 1000, 500, JsonSerializer.Serialize(r));
    }

    public Task<(InterpretationResult Result, int InputTokens, int OutputTokens, string RawResponse)>
        InterpretPdfAsync(Stream pdfStream, string fileName, string languageCode,
            PatientContext? patientContext = null, CancellationToken ct = default,
            string? modelOverride = null) => Task.FromResult(Build());

    public Task<(InterpretationResult Result, int InputTokens, int OutputTokens, string RawResponse)>
        InterpretAsync(string extractedText, string languageCode, CancellationToken ct = default)
        => Task.FromResult(Build());

    public Task<(InterpretationResult Result, int InputTokens, int OutputTokens, string RawResponse)>
        InterpretTextAsync(string extractedText, string fileName, string languageCode,
            PatientContext? patientContext = null, CancellationToken ct = default,
            string? modelOverride = null) => Task.FromResult(Build());
}

sealed class FakeEmail : IEmailService
{
    public List<(string to, string subject, int attachments)> Sent { get; } = new();

    public Task SendEmailAsync(string toEmail, string subject, string htmlBody)
    { Sent.Add((toEmail, subject, 0)); return Task.CompletedTask; }

    public Task SendEmailWithAttachmentAsync(string toEmail, string subject, string htmlBody,
        byte[] attachmentBytes, string attachmentFileName)
    { Sent.Add((toEmail, subject, 1)); return Task.CompletedTask; }

    public Task SendEmailWithAttachmentsAsync(string toEmail, string subject, string htmlBody,
        IEnumerable<(byte[] Bytes, string FileName, string MimeType)> attachments)
    { Sent.Add((toEmail, subject, attachments.Count())); return Task.CompletedTask; }
}

sealed class FakeSession : Microsoft.AspNetCore.Http.ISession
{
    private readonly Dictionary<string, byte[]> _data = new();
    public FakeSession(string? userEmail)
    {
        if (userEmail != null)
            _data["UserEmail"] = System.Text.Encoding.UTF8.GetBytes(userEmail);
    }
    public bool IsAvailable => true;
    public string Id => "fake";
    public IEnumerable<string> Keys => _data.Keys;
    public void Clear() => _data.Clear();
    public Task CommitAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task LoadAsync(CancellationToken ct = default) => Task.CompletedTask;
    public void Remove(string key) => _data.Remove(key);
    public void Set(string key, byte[] value) => _data[key] = value;
    public bool TryGetValue(string key, out byte[] value) => _data.TryGetValue(key, out value!);
}
