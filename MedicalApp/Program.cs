using MedicalApp.Data;
using MedicalApp.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Localization;
using Microsoft.EntityFrameworkCore;
using System.Globalization;

var builder = WebApplication.CreateBuilder(args);

// ---------------------------------------------------------------------------
// SCALE-OUT (June 2026). Everything here is INACTIVE until
// ScaleOut:Enabled = true, so development and single-instance hosting behave
// exactly as before. Activation steps: /app/memory/SCALE_OUT.md
// ---------------------------------------------------------------------------
builder.Services.Configure<ScaleOutSettings>(builder.Configuration.GetSection("ScaleOut"));
builder.Services.AddSingleton<SingletonLeaseService>();

var scaleOut = builder.Configuration.GetSection("ScaleOut").Get<ScaleOutSettings>()
               ?? new ScaleOutSettings();

if (scaleOut.Enabled)
{
    // 1) Sessions must live outside the process, otherwise the load balancer
    //    logs users out at random (Session["UserEmail"] is our identity).
    builder.Services.AddDistributedSqlServerCache(o =>
    {
        o.ConnectionString = builder.Configuration.GetConnectionString("DefaultConnection");
        o.SchemaName = scaleOut.SessionCacheSchema;
        o.TableName = scaleOut.SessionCacheTable;
    });

    // 2) Data Protection keys encrypt the session cookie and every antiforgery
    //    token. Local keys ⇒ "the antiforgery token could not be decrypted" as
    //    soon as a second instance (or a fresh container) answers the request.
    var camBlob = builder.Configuration.GetSection("CamSettings:Blob").Get<CamBlobSettings>()
                  ?? new CamBlobSettings();
    var keysBlobUri = ScaleOutBootstrap.EnsureDataProtectionBlob(camBlob, scaleOut);

    builder.Services.AddDataProtection()
        .SetApplicationName("MyMedicalApp")
        .PersistKeysToAzureBlobStorage(keysBlobUri);
}
else
{
    // Single instance: an in-process distributed cache. Same object the app has
    // always used implicitly, now explicit so PendingRegistrationStore has a
    // dependency to resolve in BOTH modes.
    builder.Services.AddDistributedMemoryCache();
}

// MVC + Session
builder.Services.AddControllersWithViews();
builder.Services.AddHttpContextAccessor();

builder.Services.AddSession(options =>
{
    options.IdleTimeout = TimeSpan.FromMinutes(60);
    options.Cookie.HttpOnly = true;
    options.Cookie.IsEssential = true;
});

// Entity Framework + SQL Server
// Retry + command timeout: SQL Azure drops idle connections and throttles
// periodically, so a transient failure is NORMAL there, not an incident.
// Safe to enable because the app uses no explicit transactions (a retrying
// execution strategy would otherwise refuse to run a manual BeginTransaction).
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlServer(
        builder.Configuration.GetConnectionString("DefaultConnection"),
        sql =>
        {
            sql.EnableRetryOnFailure(
                maxRetryCount: 5,
                maxRetryDelay: TimeSpan.FromSeconds(10),
                errorNumbersToAdd: null);
            sql.CommandTimeout(30);
        }));

// Email service configuration
builder.Services.Configure<EmailSettings>(builder.Configuration.GetSection("EmailSettings"));
builder.Services.AddScoped<IEmailService, EmailService>();

// Admin settings (list of admin emails)
builder.Services.Configure<AdminSettings>(builder.Configuration.GetSection("AdminSettings"));

// Daily summary email to admins (background job, default 09:00 local).
// Registered as Singleton so the AdminController can reach it via DI and trigger
// a manual "send now" run.
builder.Services.Configure<DailySummarySettings>(builder.Configuration.GetSection("DailySummarySettings"));
builder.Services.AddSingleton<DailySummaryService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<DailySummaryService>());

// Monthly Gemini budget alert (background job, default check every 60 min).
// Sends an email to AdminSettings.Emails when month-to-date cost crosses the
// configured threshold. Same Singleton pattern as DailySummary so a future
// Admin "test now" button can reuse it via DI without restarting the app.
builder.Services.Configure<BudgetAlertSettings>(builder.Configuration.GetSection("BudgetAlert"));
builder.Services.AddSingleton<BudgetAlertService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<BudgetAlertService>());

// Email deliverability pre-check (syntactic + DNS A-record + typo suggestion).
// Used by CAM CheckPdfs to highlight rows where the patient email is almost
// certainly a typo before a batch consumes Gemini credits on a doomed send.
// Singleton because IMemoryCache and ILogger are thread-safe.
builder.Services.AddSingleton<EmailDeliverabilityChecker>();

// HttpClient factory (used by Gemini service for direct REST calls)
builder.Services.AddHttpClient();

// OpenAI service configuration (kept as fallback)
builder.Services.Configure<OpenAISettings>(builder.Configuration.GetSection("OpenAI"));

// Gemini service configuration (primary interpretation provider)
builder.Services.Configure<GeminiSettings>(builder.Configuration.GetSection("Gemini"));
builder.Services.Configure<GeminiPricing>(builder.Configuration.GetSection("GeminiPricing"));

// Interpretation provider toggle (Gemini default, OpenAI fallback)
builder.Services.Configure<InterpretationSettings>(builder.Configuration.GetSection("Interpretation"));

// Register both concrete providers + a keyed factory that picks one based on settings.
builder.Services.AddScoped<MedicalInterpretationService>();        // OpenAI implementation
builder.Services.AddScoped<GeminiMedicalInterpretationService>();  // Gemini implementation
builder.Services.AddScoped<IMedicalInterpretationProvider>(sp =>
{
    var cfg = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<InterpretationSettings>>().Value;
    var provider = (cfg.Provider ?? "Gemini").Trim();
    if (string.Equals(provider, "OpenAI", StringComparison.OrdinalIgnoreCase))
        return sp.GetRequiredService<MedicalInterpretationService>();
    // default = Gemini
    return sp.GetRequiredService<GeminiMedicalInterpretationService>();
});

builder.Services.AddSingleton<PdfReportGenerator>();
builder.Services.AddSingleton<InterpretationProgressTracker>();  // live upload progress (in-memory, short TTL)
builder.Services.AddSingleton<EvolutionPdfGenerator>();

// Where the in-PDF freemium "unlock for FREE" buttons point. Configurable so the
// link can move from localhost to the public domain without touching code.
var pdfCtaUrl = builder.Configuration["PdfCta:BuyCreditsUrl"];
if (!string.IsNullOrWhiteSpace(pdfCtaUrl))
    PdfReportGenerator.BuyCreditsUrl = pdfCtaUrl.Trim();

// AI usage logger (writes to AiUsageLogs table, used by Admin "AI usage" widget).
// Fail-safe: never throws back to the interpretation flow.
builder.Services.AddScoped<IAiUsageLogger, AiUsageLogger>();

// In-memory cache (used to briefly hold uploaded PDF bytes while the user
// decides what to do about a duplicate-interpretation detection).
builder.Services.AddMemoryCache();

// Archive premium access billing (P1.5.5, P1.8, exports).
builder.Services.AddScoped<ArchiveAccessService>();

// CAM (Clinici de Analize Medicale) module: settings + local-disk file store +
// AES crypto for patient CNP. Tomorrow's cloud deployment can swap
// LocalDiskCamFileStore for an AzureBlobCamFileStore without controllers
// changing a single line.
builder.Services.Configure<CamSettings>(builder.Configuration.GetSection("CamSettings"));
// CAM file storage: local disk by default (dev / Docker volume), Azure Blob in
// the cloud. Selected by CamSettings:Storage — see /app/memory/CAM_BLOB_STORAGE.md.
if (string.Equals(builder.Configuration["CamSettings:Storage"], "Blob",
        StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddSingleton<ICamFileStore, BlobCamFileStore>();
}
else
{
    builder.Services.AddSingleton<ICamFileStore, LocalDiskCamFileStore>();
}
builder.Services.AddScoped<CamPdfMetadataExtractor>();
builder.Services.AddSingleton<CamBatchRegistry>();
builder.Services.AddScoped<CamBatchService>();
builder.Services.AddScoped<CamRetentionService>();
builder.Services.AddScoped<CamComparePdfGenerator>();
builder.Services.AddScoped<ProfileComparePdfGenerator>();
builder.Services.AddScoped<MedicalDossierPdfGenerator>();
builder.Services.AddScoped<CamBatchSumarPdfGenerator>();

// LOINC matcher microservice client (Python FastAPI).
// Gemini emits standardized English medical names; this client calls the
// Python pipeline (semantic + fuzzy + rules over the local LoincDictionary)
// to resolve the canonical LOINC code. Eliminates LLM LOINC hallucinations.
builder.Services.Configure<LoincMatcherSettings>(
    builder.Configuration.GetSection("LoincMatcher"));
// Persistent, GLOBAL cache of resolved LOINC mappings. Sits in front of the
// Python matcher so it is only asked about analyte names never seen before.
// KILL SWITCH: "LoincMatcher:Cache:Enabled" = false ⇒ every analyte is matched
// live, exactly as before.
// Specimen/method vocabulary for the cache key, fetched from the Python
// matcher so the 20+ supported languages live in ONE place (see
// LoincContextVocabulary).
builder.Services.AddSingleton<LoincContextVocabulary>();
builder.Services.AddSingleton<LoincMatchCacheStore>();
builder.Services.AddHttpClient<LoincMatcherClient>((sp, http) =>
{
    var opts = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<LoincMatcherSettings>>().Value;
    http.BaseAddress = new Uri(opts.BaseUrl);
    // Per-call timeout is enforced by LoincMatcherClient itself; we set a
    // generous outer ceiling here so HttpClient does not pre-emptively cancel.
    http.Timeout = TimeSpan.FromSeconds(Math.Max(opts.TimeoutSeconds * 2, 10));
});

// ---------------------------------------------------------------------------
// LOINC microservice health monitor + optional auto-start (self-healing).
// AZURE-SAFE: LoincAutoStart.Enabled defaults to false in appsettings.json,
// and the restart code path is additionally gated on Windows only. On Azure
// this hosted service just keeps an in-memory /ready snapshot fresh so the
// admin widget stops issuing live blocking HTTP calls on every dashboard hit.
// KILL SWITCH: set "LoincAutoStart:Enabled" to false in appsettings.json and
// restart the app — nothing else changes.
// ---------------------------------------------------------------------------
builder.Services.Configure<LoincAutoStartSettings>(
    builder.Configuration.GetSection("LoincAutoStart"));
builder.Services.AddSingleton<ILoincHealthState, LoincHealthState>();
builder.Services.AddHostedService<LoincHealthMonitor>();

// Pending registrations (in-memory, singleton)
builder.Services.AddSingleton<PendingRegistrationStore>();

// ---------------------------------------------------------------------------
// B2C interpretations run in the BACKGROUND (June 2026). The HTTP request only
// reserves the credit and queues the job; the worker executes it even if the
// user closes the tab. Concurrency is capped inside InterpretationJobQueue
// (3 app-wide, 1 per user) to stay clear of Gemini's rate limit.
// ---------------------------------------------------------------------------
builder.Services.Configure<InterpretationQueueSettings>(
    builder.Configuration.GetSection("InterpretationQueue"));
builder.Services.AddSingleton<InterpretationJobQueue>();
builder.Services.AddScoped<B2cInterpretationRunner>();
builder.Services.AddHostedService<InterpretationQueueWorker>();

// LOINC dictionary - configuration for the optional startup seed.
builder.Services.Configure<LoincSettings>(builder.Configuration.GetSection("Loinc"));

// Localization - supported cultures
// Supported cultures come from the single source of truth
// SupportedLanguagesConfig — adding a new language there automatically
// registers it with ASP.NET Core's request localization here.
var supportedCultures = SupportedLanguagesConfig.All
    .Select(l => new CultureInfo(l.Code))
    .ToArray();

builder.Services.Configure<RequestLocalizationOptions>(options =>
{
    options.DefaultRequestCulture = new RequestCulture("en");
    options.SupportedCultures = supportedCultures;
    options.SupportedUICultures = supportedCultures;
    options.RequestCultureProviders.Insert(0, new CookieRequestCultureProvider());
});

var app = builder.Build();

// Run idempotent startup seed tasks (creates "Eu" profile for existing users).
using (var scopedServices = app.Services.CreateScope())
{
    var seedLogger = scopedServices.ServiceProvider.GetRequiredService<ILoggerFactory>()
        .CreateLogger("StartupSeed");
    try
    {
        await StartupSeed.EnsureDefaultProfilesAsync(app.Services, seedLogger);
        await StartupSeed.EnsureFreeArchiveUntilAsync(app.Services, seedLogger);
        // Sync IsAdmin flag in DB with AdminSettings.Emails (promote + demote).
        var adminSettingsForSeed = scopedServices.ServiceProvider
            .GetRequiredService<Microsoft.Extensions.Options.IOptions<AdminSettings>>().Value;
        await StartupSeed.EnsureAdminConsistencyAsync(app.Services, adminSettingsForSeed, seedLogger);
        // CAM: idempotent demo clinic seed — only inserts when missing.
        var camFilesForSeed = scopedServices.ServiceProvider.GetRequiredService<ICamFileStore>();
        await StartupSeed.EnsureClinicaDemoAsync(app.Services, camFilesForSeed, seedLogger);
        // CAM: per decision d)i, any batch left "Running" from a previous app
        // life-cycle is unrecoverable in-process — flip it to "Failed" so the
        // operator sees the truth and can re-launch manually.
        await StartupSeed.FailOrphanedBatchesAsync(app.Services, seedLogger);
        // B2C: same rule for interpretations queued in the previous app life —
        // unrecoverable in-process, so mark them failed and refund the credit.
        // With scale-out on, only rows past the grace period (siblings may still
        // be working on the fresh ones).
        await StartupSeed.EnsureScaleOutInfrastructureAsync(app.Services, scaleOut, seedLogger);
        await StartupSeed.FailOrphanedInterpretationsAsync(app.Services, seedLogger, scaleOut);
        await LoincSeeder.EnsureSeededAsync(app.Services, app.Environment, seedLogger);
    }
    catch (Exception ex)
    {
        seedLogger.LogError(ex, "StartupSeed failed (app will continue running).");
    }
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();

app.UseRouting();

var locOptions = app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<RequestLocalizationOptions>>().Value;
app.UseRequestLocalization(locOptions);

app.UseSession();
app.UseAuthorization();

app.MapControllerRoute(
    name: "areas",
    pattern: "{area:exists}/{controller=Dashboard}/{action=Index}/{id?}");

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

app.Run();
