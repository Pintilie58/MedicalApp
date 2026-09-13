using MedicalApp.Controllers;
using MedicalApp.Data;
using MedicalApp.Models;
using MedicalApp.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

// Probe for the Azure hosting work (June 2026):
//   * CAM batches on a durable queue instead of a fire-and-forget Task.Run
//   * the startup sweep no longer kills batches running on a sibling instance
//   * one interpretation per user enforced in the database, not in memory
//   * the Gemini quota split across instances
//   * the admin bulk email sent by a resumable background worker

int fails = 0;
void Check(string what, bool ok, string? detail = null)
{
    Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {what}{(detail is null ? "" : "  ->  " + detail)}");
    if (!ok) fails++;
}

var dbName = "azure-" + Guid.NewGuid();
var services = new ServiceCollection();
services.AddDbContext<AppDbContext>(o => o.UseInMemoryDatabase(dbName));
services.AddLogging(b => b.SetMinimumLevel(LogLevel.Error));
services.AddDistributedMemoryCache();
var fakeEmail = new FakeEmail();
services.AddSingleton<IEmailService>(fakeEmail);
var sp = services.BuildServiceProvider();
var db = sp.GetRequiredService<AppDbContext>();

CamBatchQueueStore NewStore() => new(
    new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(dbName).Options),
    NullLogger<CamBatchQueueStore>.Instance);

async Task<ClinicBatchRun> Batch(int clinicId, string status,
    DateTime? leaseUntil = null, DateTime? started = null, bool cancel = false)
{
    var b = new ClinicBatchRun
    {
        ClinicId = clinicId,
        Status = status,
        StartedAt = started ?? DateTime.UtcNow,
        LeaseUntil = leaseUntil,
        OwnerInstance = leaseUntil == null ? null : "other-instance/999",
        CancelRequested = cancel,
        LanguageCode = "ro"
    };
    db.ClinicBatchRuns.Add(b);
    await db.SaveChangesAsync();
    return b;
}

async Task<ClinicBatchRun> Reload(int id) =>
    await db.ClinicBatchRuns.AsNoTracking().FirstAsync(b => b.Id == id);

// =====================================================================
//  1. The clinic guard now lives in the database
// =====================================================================
var store = NewStore();
var queued = await Batch(1, "Queued");
Check("1. a queued batch counts as active for the clinic",
    await store.HasActiveForClinicAsync(1));
Check("1b. another clinic is unaffected", !await store.HasActiveForClinicAsync(2));

var doneBatch = await Batch(2, "Completed");
doneBatch.FinishedAt = DateTime.UtcNow;
await db.SaveChangesAsync();
Check("1c. a finished batch does not block the clinic",
    !await store.HasActiveForClinicAsync(2));

// =====================================================================
//  2. Claiming with a lease
// =====================================================================
var claimed = await NewStore().TryClaimNextAsync();
Check("2. the worker claims the queued batch",
    claimed != null && claimed.Id == queued.Id, claimed?.Id.ToString() ?? "null");

var afterClaim = await Reload(queued.Id);
Check("2b. the claim writes Running + owner + lease + attempt",
    afterClaim.Status == "Running"
    && afterClaim.OwnerInstance == CamBatchQueueStore.Instance
    && afterClaim.LeaseUntil > DateTime.UtcNow.AddMinutes(2)
    && afterClaim.Attempts == 1,
    $"{afterClaim.Status}/{afterClaim.Attempts}");

Check("2c. nothing else is claimable while the lease is fresh",
    await NewStore().TryClaimNextAsync() == null);

Check("2d. the owner can renew its lease", await NewStore().RenewLeaseAsync(queued.Id));

// A batch owned by a LIVE sibling instance must be left alone.
var live = await Batch(3, "Running", leaseUntil: DateTime.UtcNow.AddMinutes(2));
Check("3. a batch running on another live instance is NOT claimed",
    await NewStore().TryClaimNextAsync() == null);
var liveAfter = await Reload(live.Id);
Check("3b. and it is NOT marked failed",
    liveAfter.Status == "Running" && liveAfter.FinishedAt == null, liveAfter.Status);

// A batch whose owner died (expired lease) is closed, not resumed.
var dead = await Batch(4, "Running", leaseUntil: DateTime.UtcNow.AddMinutes(-5));
await NewStore().TryClaimNextAsync();
var deadAfter = await Reload(dead.Id);
Check("4. a batch with an expired lease is marked Failed (no double e-mails)",
    deadAfter.Status == "Failed" && deadAfter.FinishedAt != null
    && deadAfter.OwnerInstance == null && deadAfter.LeaseUntil == null,
    deadAfter.Status);

// Cancel travels through the row.
var cancelMe = await Batch(5, "Running", leaseUntil: DateTime.UtcNow.AddMinutes(2), cancel: true);
Check("5. a cancel pressed on another instance is visible in the row",
    await NewStore().IsCancelRequestedAsync(cancelMe.Id));
Check("5b. a batch without a cancel is not reported as cancelled",
    !await NewStore().IsCancelRequestedAsync(live.Id));

// Release after the runner returned.
await NewStore().ReleaseAsync(live.Id);
var released = await Reload(live.Id);
Check("6. releasing a still-Running row closes it instead of leaving it stuck",
    released.Status == "Failed" && released.LeaseUntil == null && released.OwnerInstance == null,
    released.Status);

// =====================================================================
//  7. The startup sweep is multi-instance safe
// =====================================================================
db.ClinicBatchRuns.RemoveRange(db.ClinicBatchRuns);
await db.SaveChangesAsync();

var sweepLive = await Batch(10, "Running", leaseUntil: DateTime.UtcNow.AddMinutes(2));
var sweepDead = await Batch(11, "Running", leaseUntil: DateTime.UtcNow.AddMinutes(-1));
var sweepLegacy = await Batch(12, "Running");              // no lease at all (old rows)
var sweepQueued = await Batch(13, "Queued");

await StartupSeed.FailOrphanedBatchesAsync(sp, NullLogger.Instance);

Check("7. the sweep leaves a batch alive on another instance alone (the old bug)",
    (await Reload(sweepLive.Id)).Status == "Running");
Check("7b. it fails the batch of a dead instance",
    (await Reload(sweepDead.Id)).Status == "Failed");
Check("7c. it still fails legacy rows with no lease (single-instance behaviour)",
    (await Reload(sweepLegacy.Id)).Status == "Failed");
Check("7d. it does not touch queued batches — a worker will pick them up",
    (await Reload(sweepQueued.Id)).Status == "Queued");

// =====================================================================
//  8. One interpretation per user, checked in the database
// =====================================================================
var jobStore = new InterpretationJobStore(db,
    Options.Create(new ScaleOutSettings()),
    NullLogger<InterpretationJobStore>.Instance);

async Task JobRow(string email, string status)
{
    db.InterpretationJobs.Add(new InterpretationJobRecord
    {
        HistoryId = Random.Shared.Next(1, 999999),
        UserEmail = email,
        ProfileName = "P",
        OriginalFileName = "f.pdf",
        LanguageCode = "ro",
        Status = status,
        EnqueuedAt = DateTime.UtcNow
    });
    await db.SaveChangesAsync();
}

Check("8. nobody is busy on an empty queue",
    !await jobStore.HasActiveForUserAsync("a@test.ro"));

await JobRow("a@test.ro", "queued");
Check("8b. a queued job blocks a second upload by the same user",
    await jobStore.HasActiveForUserAsync("a@test.ro"));
Check("8c. another user is not blocked",
    !await jobStore.HasActiveForUserAsync("b@test.ro"));

await JobRow("b@test.ro", "running");
Check("8d. a running job blocks too", await jobStore.HasActiveForUserAsync("b@test.ro"));

await JobRow("c@test.ro", "done");
await JobRow("d@test.ro", "failed");
Check("8e. finished jobs do not block anything",
    !await jobStore.HasActiveForUserAsync("c@test.ro")
    && !await jobStore.HasActiveForUserAsync("d@test.ro"));

// =====================================================================
//  9. The Gemini quota is split across instances
// =====================================================================
var single = new GeminiRateLimitSettings { RequestsPerMinute = 60, MaxConcurrentCalls = 6 };
Check("9. with one instance nothing changes",
    single.EffectiveRequestsPerMinute == 60 && single.EffectiveMaxConcurrentCalls == 6,
    $"{single.EffectiveRequestsPerMinute}/{single.EffectiveMaxConcurrentCalls}");

var three = new GeminiRateLimitSettings { RequestsPerMinute = 60, MaxConcurrentCalls = 6, InstanceCount = 3 };
Check("9b. three instances get a third of the quota each",
    three.EffectiveRequestsPerMinute == 20 && three.EffectiveMaxConcurrentCalls == 2,
    $"{three.EffectiveRequestsPerMinute}/{three.EffectiveMaxConcurrentCalls}");

var crowded = new GeminiRateLimitSettings { RequestsPerMinute = 5, MaxConcurrentCalls = 2, InstanceCount = 10 };
Check("9c. the share never drops to zero (the app would deadlock)",
    crowded.EffectiveRequestsPerMinute == 1 && crowded.EffectiveMaxConcurrentCalls == 1,
    $"{crowded.EffectiveRequestsPerMinute}/{crowded.EffectiveMaxConcurrentCalls}");

var unlimited = new GeminiRateLimitSettings { RequestsPerMinute = 0, InstanceCount = 4 };
Check("9d. 'no per-minute limit' stays unlimited",
    unlimited.EffectiveRequestsPerMinute == 0);

var silly = new GeminiRateLimitSettings { RequestsPerMinute = 60, MaxConcurrentCalls = 6, InstanceCount = 0 };
Check("9e. a nonsense instance count behaves like 1",
    silly.EffectiveRequestsPerMinute == 60 && silly.EffectiveMaxConcurrentCalls == 6);

// The limiter really uses the split budget.
var limiter = new GeminiRateLimiter(
    new StaticMonitor<GeminiSettings>(new GeminiSettings
    {
        RateLimit = new GeminiRateLimitSettings
        {
            Enabled = true, RequestsPerMinute = 60, MaxConcurrentCalls = 4, InstanceCount = 4
        }
    }),
    NullLogger<GeminiRateLimiter>.Instance);
using (await limiter.AcquireAsync(default))
{
    var second = limiter.AcquireAsync(default);
    await Task.Delay(200);
    Check("9f. with a 1-call share, the second call waits for the first to finish",
        !second.IsCompleted);
    // release happens when the using block exits
    var stats = limiter.Stats();
    Check("9g. the wait is recorded in the diagnostics panel",
        stats.InLastMinute >= 1, stats.InLastMinute.ToString());
    _ = second;
}

// =====================================================================
//  10. Admin bulk email: queued, then sent by the worker
// =====================================================================
db.Users.AddRange(
    new User { Email = "u1@test.ro", Parola = "x", DataC = DateTime.UtcNow, Credite = 10, CreditRest = 10, TotalPaid = 50m },
    new User { Email = "u2@test.ro", Parola = "x", DataC = DateTime.UtcNow, Credite = 0, CreditRest = 0 },
    new User { Email = "boom@test.ro", Parola = "x", DataC = DateTime.UtcNow, Credite = 5, CreditRest = 5, TotalPaid = 10m }
);
await db.SaveChangesAsync();

var email = fakeEmail;
var admin = new AdminController(db, email, null!,
    Options.Create(new GeminiPricing()),
    Options.Create(new GeminiSettings()),
    Options.Create(new LoincMatcherSettings()),
    null!, NullLogger<AdminController>.Instance)
{
    ControllerContext = new ControllerContext
    {
        HttpContext = new DefaultHttpContext { Session = new FakeSession("admin@test.ro") },
        RouteData = new Microsoft.AspNetCore.Routing.RouteData(),
        ActionDescriptor = new Microsoft.AspNetCore.Mvc.Controllers.ControllerActionDescriptor()
    },
    TempData = new Microsoft.AspNetCore.Mvc.ViewFeatures.TempDataDictionary(
        new DefaultHttpContext(), new FakeTempDataProvider())
};

var post = await admin.SendEmail(new BulkEmailViewModel
{
    Filter = "all", Subject = "Salut", HtmlBody = "<p>Mesaj</p>"
});
Check("10. the admin request returns immediately, without sending anything",
    post is RedirectToActionResult && email.Sent.Count == 0, email.Sent.Count.ToString());

var job = await db.BulkEmailJobs.AsNoTracking().OrderByDescending(j => j.Id).FirstAsync();
Check("10b. a queued job is written with the recipient count",
    job.Status == "queued" && job.Total == 3 && job.Subject == "Salut"
    && job.CreatedBy == "admin@test.ro",
    $"{job.Status}/{job.Total}");

// The worker does the work.
var worker = new BulkEmailWorker(sp.GetRequiredService<IServiceScopeFactory>(),
    NullLogger<BulkEmailWorker>.Instance);
await worker.RunOnceAsync(CancellationToken.None);

var finished = await db.BulkEmailJobs.AsNoTracking().FirstAsync(j => j.Id == job.Id);
Check("11. the worker sends every email and closes the job",
    finished.Status == "done" && finished.Sent == 3 && finished.Failed == 0
    && finished.NextIndex == 3 && finished.FinishedAt != null,
    $"{finished.Status} sent={finished.Sent} failed={finished.Failed}");
Check("11b. the three recipients really got the email",
    email.Sent.Count == 3 && email.Sent.All(s => s.subject == "Salut"),
    email.Sent.Count.ToString());
Check("11c. the body is wrapped in the branded template",
    email.Sent[0].body.Contains("MyMedicalApp") && email.Sent[0].body.Contains("<p>Mesaj</p>"));

// A second pass must not send anything again.
email.Sent.Clear();
await worker.RunOnceAsync(CancellationToken.None);
Check("12. a finished job is never sent twice", email.Sent.Count == 0);

// Filters are honoured.
var post2 = await admin.SendEmail(new BulkEmailViewModel
{
    Filter = "paying", Subject = "Doar platitori", HtmlBody = "x"
});
await worker.RunOnceAsync(CancellationToken.None);
var payingJob = await db.BulkEmailJobs.AsNoTracking().OrderByDescending(j => j.Id).FirstAsync();
Check("13. the 'paying users' filter is respected (u2 never paid)",
    payingJob.Total == 2 && payingJob.Sent == 2
    && email.Sent.All(s => s.to != "u2@test.ro"),
    $"{payingJob.Total}/{payingJob.Sent}");

// Resume: half-sent job (instance died mid-send) continues where it stopped.
email.Sent.Clear();
var half = new BulkEmailJob
{
    CreatedBy = "admin@test.ro", Filter = "all", Subject = "Reluare", HtmlBody = "y",
    Total = 3, Sent = 2, NextIndex = 2, Status = "running",
    OwnerInstance = "dead-instance/1", LeaseUntil = DateTime.UtcNow.AddMinutes(-10)
};
db.BulkEmailJobs.Add(half);
await db.SaveChangesAsync();
await worker.RunOnceAsync(CancellationToken.None);
var resumed = await db.BulkEmailJobs.AsNoTracking().FirstAsync(j => j.Id == half.Id);
Check("14. a job abandoned by a dead instance is resumed, not restarted",
    resumed.Status == "done" && resumed.Sent == 3 && email.Sent.Count == 1,
    $"sent_now={email.Sent.Count} total_sent={resumed.Sent}");

// A live owner is left alone.
var busy = new BulkEmailJob
{
    CreatedBy = "admin@test.ro", Filter = "all", Subject = "Alta instanta", HtmlBody = "y",
    Total = 3, Status = "running",
    OwnerInstance = "other-instance/7", LeaseUntil = DateTime.UtcNow.AddMinutes(5)
};
db.BulkEmailJobs.Add(busy);
await db.SaveChangesAsync();
email.Sent.Clear();
await worker.RunOnceAsync(CancellationToken.None);
var untouched = await db.BulkEmailJobs.AsNoTracking().FirstAsync(j => j.Id == busy.Id);
Check("15. a job being sent by another live instance is not touched",
    untouched.Status == "running" && untouched.Sent == 0 && email.Sent.Count == 0);

// One bad address does not stop the send.
email.FailFor = "boom@test.ro";
email.Sent.Clear();
await admin.SendEmail(new BulkEmailViewModel { Filter = "all", Subject = "Cu eroare", HtmlBody = "z" });
await worker.RunOnceAsync(CancellationToken.None);
var withError = await db.BulkEmailJobs.AsNoTracking()
    .Where(j => j.Subject == "Cu eroare").FirstAsync();
Check("16. a failing recipient is counted but the rest still go out",
    withError.Status == "done" && withError.Sent == 2 && withError.Failed == 1
    && !string.IsNullOrEmpty(withError.LastError),
    $"sent={withError.Sent} failed={withError.Failed}");
email.FailFor = null;

// No recipients -> no job at all.
var before = await db.BulkEmailJobs.CountAsync();
var empty = await admin.SendEmail(new BulkEmailViewModel
{
    Filter = "blocked", Subject = "Nimeni", HtmlBody = "q"
});
Check("17. an empty audience is refused instead of queueing an empty job",
    empty is ViewResult && await db.BulkEmailJobs.CountAsync() == before);

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

sealed class FakeEmail : IEmailService
{
    public List<(string to, string subject, string body)> Sent { get; } = new();
    public string? FailFor { get; set; }

    public Task SendEmailAsync(string toEmail, string subject, string htmlBody)
    {
        if (FailFor != null && toEmail == FailFor) throw new InvalidOperationException("smtp down");
        Sent.Add((toEmail, subject, htmlBody));
        return Task.CompletedTask;
    }

    public Task SendEmailWithAttachmentsAsync(string toEmail, string subject, string htmlBody,
        IEnumerable<(byte[] Bytes, string FileName, string MimeType)> attachments)
    {
        Sent.Add((toEmail, subject, htmlBody));
        return Task.CompletedTask;
    }

    public Task SendEmailWithAttachmentAsync(string toEmail, string subject, string htmlBody,
        byte[] attachment, string fileName)
    {
        Sent.Add((toEmail, subject, htmlBody));
        return Task.CompletedTask;
    }
}

sealed class FakeTempDataProvider : Microsoft.AspNetCore.Mvc.ViewFeatures.ITempDataProvider
{
    public IDictionary<string, object?> LoadTempData(HttpContext context) => new Dictionary<string, object?>();
    public void SaveTempData(HttpContext context, IDictionary<string, object?> values) { }
}

sealed class FakeSession : ISession
{
    private readonly Dictionary<string, byte[]> _store = new();
    public FakeSession(string? email)
    {
        if (email != null) _store["UserEmail"] = System.Text.Encoding.UTF8.GetBytes(email);
    }
    public bool IsAvailable => true;
    public string Id => "probe";
    public IEnumerable<string> Keys => _store.Keys;
    public void Clear() => _store.Clear();
    public Task CommitAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task LoadAsync(CancellationToken ct = default) => Task.CompletedTask;
    public void Remove(string key) => _store.Remove(key);
    public void Set(string key, byte[] value) => _store[key] = value;
    public bool TryGetValue(string key, out byte[] value) => _store.TryGetValue(key, out value!);
}
