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
services.AddSingleton<LoincContextVocabulary>();
services.AddSingleton<LoincMatchCacheStore>();
services.AddHttpClient<LoincMatcherClient>();
services.AddSingleton<InterpretationProgressTracker>();
services.Configure<InterpretationQueueSettings>(o => { o.MaxConcurrent = 3; o.MaxPerUser = 1; });
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
    Check("Limitele vin din configuratie", queue.MaxConcurrent == 3 && queue.MaxPerUser == 1,
        $"{queue.MaxConcurrent}/{queue.MaxPerUser}");
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
    Check("Ultimul rand (reusit) e raportat", Num(idle, "lastDoneId") != null);
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

// =================================================================
// 10. Limitele din appsettings schimba REAL comportamentul
// =================================================================
{
    var q2 = new InterpretationJobQueue(new StaticOptions<InterpretationQueueSettings>(
        new InterpretationQueueSettings { MaxConcurrent = 7, MaxPerUser = 2 }));
    Check("Citeste MaxConcurrent din configuratie", q2.MaxConcurrent == 7, q2.MaxConcurrent.ToString());
    Check("Primul job al userului intra", q2.TryEnqueue(Job(0, "a")));
    Check("Al doilea job intra cand limita e 2", q2.TryEnqueue(Job(0, "b")));
    Check("Al treilea job e refuzat", !q2.TryEnqueue(Job(0, "c")));
    Check("Contoarele pentru widgetul de admin sunt corecte",
        q2.ActiveCount == 2 && q2.QueuedCount == 2, $"active={q2.ActiveCount} queued={q2.QueuedCount}");
    q2.ReleaseUser("u@test.ro");
    Check("Dupa eliberare mai incape un job", !q2.IsUserBusy("u@test.ro") && q2.TryEnqueue(Job(0, "d")));

    var q1 = new InterpretationJobQueue(new StaticOptions<InterpretationQueueSettings>(
        new InterpretationQueueSettings { MaxConcurrent = 3, MaxPerUser = 1 }));
    Check("Cu limita 1 al doilea job e refuzat (comportamentul actual)",
        q1.TryEnqueue(Job(0, "x")) && !q1.TryEnqueue(Job(0, "y")));

    var q0 = new InterpretationJobQueue(new StaticOptions<InterpretationQueueSettings>(
        new InterpretationQueueSettings { MaxConcurrent = 0, MaxPerUser = 0 }));
    Check("Valorile de 0 din config sunt corectate la 1 (fara blocaj total)",
        q0.MaxConcurrent == 1 && q0.MaxPerUser == 1);
}

// =================================================================
// 11. Configurarea SQL Azure: retry + command timeout, model valid
// =================================================================
{
    var opts = new DbContextOptionsBuilder<AppDbContext>()
        .UseSqlServer("Server=(local);Database=Fake;Trusted_Connection=True;TrustServerCertificate=True",
            sql =>
            {
                sql.EnableRetryOnFailure(maxRetryCount: 5, maxRetryDelay: TimeSpan.FromSeconds(10),
                    errorNumbersToAdd: null);
                sql.CommandTimeout(30);
            })
        .Options;

    var ext = opts.FindExtension<Microsoft.EntityFrameworkCore.SqlServer.Infrastructure.Internal
        .SqlServerOptionsExtension>();
    Check("Command timeout este setat la 30s", ext?.CommandTimeout == 30,
        ext?.CommandTimeout?.ToString() ?? "null");
    Check("Strategia de retry este activa",
        ext?.ExecutionStrategyFactory != null);

    // Modelul se construieste fara conexiune la baza: prinde erori de mapare
    // introduse accidental (regresie de configurare EF).
    using var probeDb = new AppDbContext(opts);
    var entityCount = probeDb.Model.GetEntityTypes().Count();
    Check("Modelul EF se construieste corect", entityCount > 5, entityCount + " entitati");
    Check("InterpretationHistory e in model",
        probeDb.Model.FindEntityType(typeof(InterpretationHistory)) != null);
}

// =================================================================
// 12. AccountController.Dashboard() este async si intoarce userul
// =================================================================
{
    using var scope = sp.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    var acc = new MedicalApp.Controllers.AccountController(
        db, fakeEmail, new PendingRegistrationStore(new Microsoft.Extensions.Caching.Distributed.MemoryDistributedCache(
            Options.Create(new Microsoft.Extensions.Caching.Memory.MemoryDistributedCacheOptions()))),
        Options.Create(new AdminSettings()),
        scope.ServiceProvider.GetRequiredService<ILogger<MedicalApp.Controllers.AccountController>>());

    var http = new Microsoft.AspNetCore.Http.DefaultHttpContext();
    http.Session = new FakeSession("u@test.ro");
    acc.ControllerContext = new Microsoft.AspNetCore.Mvc.ControllerContext { HttpContext = http };

    var method = typeof(MedicalApp.Controllers.AccountController).GetMethod("Dashboard");
    Check("Dashboard() returneaza Task (este async)",
        method!.ReturnType == typeof(Task<Microsoft.AspNetCore.Mvc.IActionResult>),
        method.ReturnType.Name);

    var view = await acc.Dashboard() as Microsoft.AspNetCore.Mvc.ViewResult;
    Check("Dashboard() randeaza view-ul cu utilizatorul incarcat",
        view?.Model is User u2 && u2.Email == "u@test.ro",
        view?.Model?.GetType().Name ?? "null");

    // Fara sesiune -> redirect la Home (comportament neschimbat).
    var anonHttp = new Microsoft.AspNetCore.Http.DefaultHttpContext();
    anonHttp.Session = new FakeSession(null);
    acc.ControllerContext = new Microsoft.AspNetCore.Mvc.ControllerContext { HttpContext = anonHttp };
    var redirect = await acc.Dashboard() as Microsoft.AspNetCore.Mvc.RedirectToActionResult;
    Check("Fara sesiune, Dashboard() redirecteaza la Home",
        redirect?.ControllerName == "Home", redirect?.ControllerName ?? "null");
}

// =================================================================
// 13. Poziția în coadă (feedback pentru utilizator)
// =================================================================
{
    var q = new InterpretationJobQueue(new StaticOptions<InterpretationQueueSettings>(
        new InterpretationQueueSettings { MaxConcurrent = 2, MaxPerUser = 5 }));

    InterpretationJob J(int id) => new(id, "u@test.ro", 1, "Eu", new byte[] { 1 },
        "f.pdf", "h", "ro", false, null);

    Check("Job necunoscut -> poziția 0", q.GetPosition(999) == 0);

    q.TryEnqueue(J(101));
    q.TryEnqueue(J(102));
    q.TryEnqueue(J(103));
    Check("Primul intrat e primul la rând", q.GetPosition(101) == 1, q.GetPosition(101).ToString());
    Check("Al doilea e pe poziția 2", q.GetPosition(102) == 2, q.GetPosition(102).ToString());
    Check("Al treilea e pe poziția 3", q.GetPosition(103) == 3, q.GetPosition(103).ToString());

    // Workerul preia primul job -> ceilalti avanseaza
    q.MarkStarted(101);
    Check("Jobul pornit nu mai e la rând (poziția 0)", q.GetPosition(101) == 0);
    Check("Al doilea avanseaza pe poziția 1", q.GetPosition(102) == 1, q.GetPosition(102).ToString());
    Check("Al treilea avanseaza pe poziția 2", q.GetPosition(103) == 2, q.GetPosition(103).ToString());

    q.MarkStarted(102);
    q.MarkStarted(103);
    Check("Cand nimic nu mai asteapta, toate pozitiile sunt 0",
        q.GetPosition(102) == 0 && q.GetPosition(103) == 0);

    // Estimarea afisata userului: ceil(poz / MaxConcurrent) * durata medie
    int Eta(int pos, int maxConc, int avgSec) =>
        (int)Math.Ceiling((double)pos / maxConc) * avgSec;
    Check("Estimare: poziția 1 cu 2 sloturi = o tura", Eta(1, 2, 180) == 180, Eta(1, 2, 180).ToString());
    Check("Estimare: poziția 3 cu 2 sloturi = doua ture", Eta(3, 2, 180) == 360, Eta(3, 2, 180).ToString());
    Check("Estimare: poziția 5 cu 3 sloturi = doua ture", Eta(5, 3, 200) == 400, Eta(5, 3, 200).ToString());
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

sealed class StaticOptions<T> : Microsoft.Extensions.Options.IOptionsMonitor<T>
{
    private readonly T _value;
    public StaticOptions(T value) => _value = value;
    public T CurrentValue => _value;
    public T Get(string? name) => _value;
    public IDisposable OnChange(Action<T, string?> listener) => new Noop();
    private sealed class Noop : IDisposable { public void Dispose() { } }
}
