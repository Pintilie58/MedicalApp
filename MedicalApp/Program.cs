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

// Daily summary email to admins (background job, default 09:00 local)
builder.Services.Configure<DailySummarySettings>(builder.Configuration.GetSection("DailySummarySettings"));
builder.Services.AddHostedService<DailySummaryService>();

// OpenAI service configuration
builder.Services.Configure<OpenAISettings>(builder.Configuration.GetSection("OpenAI"));
builder.Services.AddScoped<IMedicalInterpretationService, MedicalInterpretationService>();
builder.Services.AddSingleton<PdfReportGenerator>();

// Pending registrations (in-memory, singleton)
builder.Services.AddSingleton<PendingRegistrationStore>();

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
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

app.Run();
