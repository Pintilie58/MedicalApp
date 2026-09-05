using MedicalApp.Controllers;
using MedicalApp.Data;
using MedicalApp.Models;
using MedicalApp.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

// Probe for the Admin > Diagnostics panel: does it report the real numbers of
// the quota guard, the durable queue and the LOINC cache?

int fails = 0;
void Check(string label, bool ok, string detail = "")
{
    Console.WriteLine((ok ? "PASS  " : "FAIL  ") + label + (detail.Length > 0 ? "  ->  " + detail : ""));
    if (!ok) fails++;
}

var services = new ServiceCollection();
services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
services.AddDbContext<AppDbContext>(o => o.UseInMemoryDatabase("diag"));
services.AddHttpClient();
services.AddControllersWithViews();   // for TempData when the controller returns View(...)
var sp = services.BuildServiceProvider();
var db = sp.GetRequiredService<AppDbContext>();

// Two jobs: one waiting, one running with an expired lease (a dead instance).
db.InterpretationJobs.AddRange(
    new InterpretationJobRecord
    {
        HistoryId = 10, UserEmail = "a@x.ro", ProfileId = 1, ProfileName = "Eu",
        PdfBytes = new byte[] { 1 }, OriginalFileName = "a.pdf", LanguageCode = "ro",
        Status = "queued", Attempts = 0, EnqueuedAt = DateTime.UtcNow.AddMinutes(-9)
    },
    new InterpretationJobRecord
    {
        HistoryId = 11, UserEmail = "b@x.ro", ProfileId = 1, ProfileName = "Eu",
        PdfBytes = new byte[] { 1 }, OriginalFileName = "b.pdf", LanguageCode = "ro",
        Status = "running", Attempts = 2, EnqueuedAt = DateTime.UtcNow.AddMinutes(-3),
        Owner = "DEAD/1", LeaseUntil = DateTime.UtcNow.AddMinutes(-1)
    });

// Mappings: two on the live version (one heavily reused), one leftover.
db.LoincMatchCache.AddRange(
    new LoincMatchCacheEntry
    {
        CacheKey = "k1", TestName = "Hemoglobin", Unit = "g/dL", PipelineVersion = "v2",
        LoincCode = "718-7", LongName = "Hemoglobin", Score = 0.99, HitCount = 12,
        CreatedAt = DateTime.UtcNow.AddHours(-2), LastUsedAt = DateTime.UtcNow
    },
    new LoincMatchCacheEntry
    {
        CacheKey = "k2", TestName = "Glucose", Unit = "mg/dL", PipelineVersion = "v2",
        LoincCode = "2345-7", LongName = "Glucose", Score = 0.97, HitCount = 3,
        CreatedAt = DateTime.UtcNow.AddMinutes(-5), LastUsedAt = DateTime.UtcNow
    },
    new LoincMatchCacheEntry
    {
        CacheKey = "k3", TestName = "Old", Unit = null, PipelineVersion = "v1",
        LoincCode = "1-1", LongName = "Old", Score = 0.9, HitCount = 99,
        CreatedAt = DateTime.UtcNow.AddDays(-3), LastUsedAt = DateTime.UtcNow
    });

db.LoincVocabulary.Add(new LoincVocabularySnapshot
{
    PhrasesJson = "[\"ser\",\"impedanta\"]", PhraseCount = 160,
    FetchedAt = DateTime.UtcNow.AddMinutes(-30)
});
await db.SaveChangesAsync();

var geminiSettings = new GeminiSettings
{
    RateLimit = new GeminiRateLimitSettings
    {
        Enabled = true, RequestsPerMinute = 3, WindowSeconds = 2, MaxConcurrentCalls = 6
    }
};
var limiter = new GeminiRateLimiter(
    new StaticMonitor<GeminiSettings>(geminiSettings), NullLogger<GeminiRateLimiter>.Instance);
for (int i = 0; i < 3; i++) (await limiter.AcquireAsync(default)).Dispose();
limiter.NoteRejected(429, TimeSpan.FromMilliseconds(1));

var loincSettings = new LoincMatcherSettings
{
    BaseUrl = "http://127.0.0.1:59999",   // nothing listens: "service down" branch
    Cache = new LoincMatchCacheSettings { Enabled = true, PipelineVersion = "v2" }
};

var controller = new AdminController(
    db,
    null!, null!,
    new OptionsWrapper<GeminiPricing>(new GeminiPricing()),
    new OptionsWrapper<GeminiSettings>(geminiSettings),
    new OptionsWrapper<LoincMatcherSettings>(loincSettings),
    sp.GetRequiredService<IHttpClientFactory>(),
    NullLogger<AdminController>.Instance)
{
    ControllerContext = new ControllerContext
    {
        HttpContext = new DefaultHttpContext { RequestServices = sp }
    }
};

var result = await controller.Diagnostics(
    limiter, new OptionsWrapper<InterpretationQueueSettings>(
        new InterpretationQueueSettings { MaxConcurrent = 3, MaxPerUser = 1 }));

Check("1. the panel renders a view", result is ViewResult);
var m = (result as ViewResult)?.Model as InfrastructureDiagnosticsViewModel;
Check("1b. with the diagnostics model", m != null);
if (m == null) { Console.WriteLine("\n1 CHECK(S) FAILED"); return 1; }

Check("2. Gemini: quota and calls in the last minute are real",
    m.QuotaEnabled && m.RequestsPerMinute == 3 && m.MaxConcurrentCalls == 6
    && m.CallsInLastMinute == 3 && m.TotalCalls == 3,
    $"{m.CallsInLastMinute}/{m.RequestsPerMinute} total={m.TotalCalls}");
Check("2b. Gemini: a refusal from Google is shown", m.Rejections == 1);

Check("3. queue: waiting / running / retried are counted",
    m.JobsQueued == 1 && m.JobsRunning == 1 && m.JobsRetried == 1,
    $"queued={m.JobsQueued} running={m.JobsRunning} retried={m.JobsRetried}");
Check("3b. queue: an expired lease is flagged as recoverable", m.JobsStale == 1);
Check("3c. queue: the oldest job and the slot count are shown",
    m.OldestJobEnqueuedAt != null && m.QueueMaxConcurrent == 3
    && m.Jobs.Count == 2 && m.Jobs[0].HistoryId == 10);

Check("4. LOINC cache: only the live version is counted",
    m.PipelineVersion == "v2" && m.MappingsCurrentVersion == 2 && m.MappingsOtherVersions == 1,
    $"{m.PipelineVersion}: {m.MappingsCurrentVersion} vs {m.MappingsOtherVersions}");
Check("4b. LOINC cache: reuses are summed for the live version only",
    m.MappingReuses == 15, m.MappingReuses.ToString());
Check("4c. LOINC cache: the most reused mapping is first",
    m.TopMappings.Count == 2 && m.TopMappings[0].TestName == "Hemoglobin"
    && m.TopMappings[0].HitCount == 12);
Check("4d. LOINC cache: the last learned mapping is dated", m.LastMappingLearnedAt != null);
Check("5. the saved vocabulary is reported", m.VocabularyPhrases == 160 && m.VocabularyFetchedAt != null);
Check("6. a stopped LOINC service is reported, without breaking the page", !m.ServiceReachable);

Console.WriteLine(fails == 0 ? "\nALL CHECKS PASSED" : $"\n{fails} CHECK(S) FAILED");
return fails == 0 ? 0 : 1;

sealed class StaticMonitor<T> : IOptionsMonitor<T>
{
    public StaticMonitor(T value) => CurrentValue = value;
    public T CurrentValue { get; }
    public T Get(string? name) => CurrentValue;
    public IDisposable OnChange(Action<T, string?> listener) => new Dummy();
    private sealed class Dummy : IDisposable { public void Dispose() { } }
}
