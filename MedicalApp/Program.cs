using MedicalApp.Data;
using MedicalApp.Services;
using Microsoft.AspNetCore.Localization;
using Microsoft.EntityFrameworkCore;
using System.Globalization;

var builder = WebApplication.CreateBuilder(args);

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
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection")));

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
builder.Services.AddSingleton<EvolutionPdfGenerator>();

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
builder.Services.AddSingleton<ICamFileStore, LocalDiskCamFileStore>();
builder.Services.AddScoped<CamPdfMetadataExtractor>();
builder.Services.AddSingleton<CamBatchRegistry>();
builder.Services.AddScoped<CamBatchService>();
builder.Services.AddScoped<CamRetentionService>();
builder.Services.AddScoped<CamComparePdfGenerator>();
builder.Services.AddScoped<ProfileComparePdfGenerator>();
builder.Services.AddScoped<CamBatchSumarPdfGenerator>();

// LOINC matcher microservice client (Python FastAPI).
// Gemini emits standardized English medical names; this client calls the
// Python pipeline (semantic + fuzzy + rules over the local LoincDictionary)
// to resolve the canonical LOINC code. Eliminates LLM LOINC hallucinations.
builder.Services.Configure<LoincMatcherSettings>(
    builder.Configuration.GetSection("LoincMatcher"));
builder.Services.AddHttpClient<LoincMatcherClient>((sp, http) =>
{
    var opts = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<LoincMatcherSettings>>().Value;
    http.BaseAddress = new Uri(opts.BaseUrl);
    // Per-call timeout is enforced by LoincMatcherClient itself; we set a
    // generous outer ceiling here so HttpClient does not pre-emptively cancel.
    http.Timeout = TimeSpan.FromSeconds(Math.Max(opts.TimeoutSeconds * 2, 10));
});

// Pending registrations (in-memory, singleton)
builder.Services.AddSingleton<PendingRegistrationStore>();

// LOINC dictionary - configuration for the optional startup seed.
builder.Services.Configure<LoincSettings>(builder.Configuration.GetSection("Loinc"));

// Localization - supported cultures
var supportedCultures = new[]
{
    new CultureInfo("en"),
    new CultureInfo("ro"),
    new CultureInfo("fr"),
    new CultureInfo("es"),
    new CultureInfo("de")
};

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
