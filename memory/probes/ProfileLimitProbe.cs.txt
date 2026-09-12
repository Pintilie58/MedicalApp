using System.Globalization;
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

// Probe for the hard cap of 20 B2C profiles per account (June 2026).
// Covers the rule itself, the real controller (UI cannot be trusted), the
// grandfather clause and the 7 translations of the new messages.

int fails = 0;
void Check(string label, bool ok, string detail = "")
{
    Console.WriteLine((ok ? "PASS  " : "FAIL  ") + label + (detail.Length > 0 ? "  ->  " + detail : ""));
    if (!ok) fails++;
}

const int Max = ProfileGateService.MaxProfilesPerUser;

// =====================================================================
//  PART 1 — the rule
// =====================================================================
static User B2c(int paidLeft) => new()
{
    Email = "u@x.ro", UserType = "Individual",
    Credite = paidLeft, CreditRest = paidLeft, CreditConsum = 0, BonusCredits = 1
};
static User Clinic() => new() { Email = "c@x.ro", UserType = "Clinic" };

Check($"1. the cap is {Max} profiles", Max == 20, Max.ToString());
Check("2. a paying user below the cap may still add profiles",
    ProfileGateService.CanCreateAdditionalProfile(B2c(5), Max - 1)
    && !ProfileGateService.IsAtProfileLimit(B2c(5), Max - 1));
Check("3. at the cap, even a paying user is refused",
    !ProfileGateService.CanCreateAdditionalProfile(B2c(50), Max)
    && ProfileGateService.IsAtProfileLimit(B2c(50), Max));
Check("4. the old rule still stands below the cap (no paid credits, no new profile)",
    !ProfileGateService.CanCreateAdditionalProfile(B2c(0), 3)
    && !ProfileGateService.IsAtProfileLimit(B2c(0), 3));
Check("5. clinics (B2B) are not capped here",
    ProfileGateService.CanCreateAdditionalProfile(Clinic(), Max + 5)
    && !ProfileGateService.IsAtProfileLimit(Clinic(), Max + 5));
Check("6. an account that somehow has MORE than the cap is only blocked from adding",
    !ProfileGateService.CanCreateAdditionalProfile(B2c(50), Max + 5)
    && ProfileGateService.IsAtProfileLimit(B2c(50), Max + 5));

// =====================================================================
//  PART 2 — the real controller (the UI check is cosmetic)
// =====================================================================
var services = new ServiceCollection();
services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
services.AddControllersWithViews();
var sp = services.BuildServiceProvider();

static AppDbContext Db(string name) => new(
    new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(name).Options);

async Task<AppDbContext> Seed(string name, int profiles, int paidCredits)
{
    var db = Db(name);
    db.Users.Add(new User
    {
        Email = "u@x.ro", Parola = "x", UserType = "Individual",
        Credite = paidCredits, CreditRest = paidCredits, CreditConsum = 0
    });
    for (int i = 0; i < profiles; i++)
        db.Profiles.Add(new Profile
        {
            UserEmail = "u@x.ro", Name = i == 0 ? "Eu" : $"Membru {i}",
            IsDefault = i == 0, CreatedAt = DateTime.UtcNow
        });
    await db.SaveChangesAsync();
    return db;
}

ProfilesController Controller(AppDbContext db)
{
    var http = new DefaultHttpContext { RequestServices = sp };
    http.Session = new FakeSession();
    http.Session.SetString("UserEmail", "u@x.ro");
    return new ProfilesController(db, null!, null!, null!, null!, null!, null!, null!,
        NullLogger<ProfilesController>.Instance)
    {
        ControllerContext = new ControllerContext
        {
            HttpContext = http,
            RouteData = new Microsoft.AspNetCore.Routing.RouteData(),
            ActionDescriptor = new Microsoft.AspNetCore.Mvc.Controllers.ControllerActionDescriptor()
        }
    };
}

CultureInfo.CurrentUICulture = new CultureInfo("ro");

// At the cap: the POST must refuse even though the button is hidden in the UI.
var dbFull = await Seed("full", Max, paidCredits: 50);
var full = Controller(dbFull);
var postFull = await full.Create(new ProfileFormViewModel { Name = "Al 21-lea" });
Check("7. POST /Profiles/Create at the cap redirects instead of creating",
    postFull is RedirectToActionResult r7 && r7.ActionName == "Index");
Check("8. nothing was written — the account still has exactly the cap",
    await dbFull.Profiles.CountAsync(p => p.UserEmail == "u@x.ro") == Max);
var msg = full.TempData["ErrorMessage"] as string ?? "";
Check("9. the message says the limit was reached (and names the number)",
    msg.Contains(Max.ToString()) && msg.Contains("maxim", StringComparison.OrdinalIgnoreCase),
    msg);
Check("10. GET /Profiles/Create at the cap does not open the form",
    await full.Create() is RedirectToActionResult);

// One below the cap, with paid credits: creation must still work.
var dbOk = await Seed("ok", Max - 1, paidCredits: 5);
var ok = Controller(dbOk);
var postOk = await ok.Create(new ProfileFormViewModel { Name = "Bunica" });
Check("11. one below the cap, a paying user can still create a profile",
    postOk is RedirectToActionResult
    && await dbOk.Profiles.CountAsync(p => p.UserEmail == "u@x.ro") == Max
    && await dbOk.Profiles.AnyAsync(p => p.Name == "Bunica"),
    ok.TempData["ErrorMessage"] as string ?? "");

// Grandfathered account (more profiles than the cap): keeps them all.
var dbOld = await Seed("old", Max + 5, paidCredits: 50);
var old = Controller(dbOld);
await old.Create(new ProfileFormViewModel { Name = "Inca unul" });
Check("12. an over-the-cap account keeps every existing profile",
    await dbOld.Profiles.CountAsync(p => p.UserEmail == "u@x.ro") == Max + 5);

// Without paid credits the refusal must still be the OLD message.
var dbPoor = await Seed("poor", 3, paidCredits: 0);
var poor = Controller(dbPoor);
await poor.Create(new ProfileFormViewModel { Name = "Mama" });
var poorMsg = poor.TempData["ErrorMessage"] as string ?? "";
Check("13. below the cap without paid credits, the message is about credits",
    poorMsg.Contains("credite", StringComparison.OrdinalIgnoreCase)
    && !poorMsg.Contains(Max.ToString()),
    poorMsg);

// The list page tells the view which of the two locked states to render.
var dbView = await Seed("view", Max, paidCredits: 50);
var listing = Controller(dbView);
await listing.Index();
Check("14. the profiles page reports 'cap reached' to the view",
    (listing.ViewBag.CanCreateProfile as bool?) == false
    && (listing.ViewBag.ProfileLimitReached as bool?) == true);

var dbView2 = await Seed("view2", 2, paidCredits: 0);
var listing2 = Controller(dbView2);
await listing2.Index();
Check("15. below the cap the page does NOT claim the cap was reached",
    (listing2.ViewBag.CanCreateProfile as bool?) == false
    && (listing2.ViewBag.ProfileLimitReached as bool?) == false);

// =====================================================================
//  PART 3 — the messages exist in all 7 languages
// =====================================================================
var langs = new[] { "en", "ro", "fr", "es", "de", "it", "pt" };
var missing = new List<string>();
var noPlaceholder = new List<string>();
var notNumbered = new List<string>();
foreach (var lang in langs)
{
    foreach (var key in new[] { "ProfileLimitReached", "ProfileLimitTooltip" })
    {
        var raw = Loc.T(key, lang);
        if (raw == key) { missing.Add($"{lang}/{key}"); continue; }
        if (!raw.Contains("{0}")) { noPlaceholder.Add($"{lang}/{key}"); continue; }
        if (!string.Format(raw, Max).Contains(Max.ToString())) notNumbered.Add($"{lang}/{key}");
    }
}
Check("16. both new messages are translated in all 7 languages",
    missing.Count == 0, string.Join(", ", missing));
Check("17. every translation keeps the {0} placeholder for the limit",
    noPlaceholder.Count == 0, string.Join(", ", noPlaceholder));
Check("18. the formatted message shows the real number",
    notNumbered.Count == 0, string.Join(", ", notNumbered));
Console.WriteLine("      ro: " + string.Format(Loc.T("ProfileLimitReached", "ro"), Max));

Console.WriteLine(fails == 0 ? "\nALL CHECKS PASSED" : $"\n{fails} CHECK(S) FAILED");
return fails == 0 ? 0 : 1;

sealed class FakeSession : ISession
{
    private readonly Dictionary<string, byte[]> _store = new();
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
