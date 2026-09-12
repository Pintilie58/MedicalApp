using System.Globalization;
using MedicalApp.Controllers;
using MedicalApp.Data;
using MedicalApp.Models;
using MedicalApp.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

// ============================================================================
//  Cabinet Medical (CM) — the new third account type, stage 1:
//  registration, practice name, bonus credit, its own single package,
//  the 2000-patient cap, and the fact that B2C / CAM behaviour is untouched.
// ============================================================================

int fails = 0;
void Check(string label, bool ok, string detail = "")
{
    Console.WriteLine((ok ? "PASS  " : "FAIL  ") + label + (detail.Length > 0 ? "  ->  " + detail : ""));
    if (!ok) fails++;
}

QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;
CultureInfo.CurrentUICulture = new CultureInfo("ro-RO");

var services = new ServiceCollection();
services.AddLogging(b => b.SetMinimumLevel(LogLevel.Error));
services.AddDistributedMemoryCache();
services.AddDbContext<AppDbContext>(o => o.UseInMemoryDatabase(Guid.NewGuid().ToString()));
services.Configure<AdminSettings>(o => { });
services.AddSingleton<FakeEmail>();
services.AddSingleton<IEmailService>(sp => sp.GetRequiredService<FakeEmail>());
services.AddSingleton<PendingRegistrationStore>();
services.AddSingleton<PdfReportGenerator>();
var sp = services.BuildServiceProvider();

var pending = sp.GetRequiredService<PendingRegistrationStore>();
var mail = sp.GetRequiredService<FakeEmail>();
var db = sp.GetRequiredService<AppDbContext>();

AccountController Account(string? sessionEmail = null, Dictionary<string, string>? form = null)
{
    var http = new DefaultHttpContext { Session = new FakeSession(sessionEmail) };
    if (form != null)
        http.Request.Form = new FormCollection(
            form.ToDictionary(kv => kv.Key, kv => new Microsoft.Extensions.Primitives.StringValues(kv.Value)));

    return new AccountController(db,
        sp.GetRequiredService<IEmailService>(), pending,
        Options.Create(new AdminSettings()),
        NullLogger<AccountController>.Instance)
    {
        ControllerContext = Ctx(http),
        TempData = new Microsoft.AspNetCore.Mvc.ViewFeatures.TempDataDictionary(
            http, new FakeTempDataProvider())
    };
}

CreditsController Credits(string sessionEmail)
{
    var http = new DefaultHttpContext { Session = new FakeSession(sessionEmail) };
    var ctx = Ctx(http);
    return new CreditsController(db,
        sp.GetRequiredService<IEmailService>(),
        Options.Create(new AdminSettings()),
        null!,                                   // ICamFileStore: clinic-only path
        sp.GetRequiredService<PdfReportGenerator>(),
        NullLogger<CreditsController>.Instance)
    {
        ControllerContext = ctx,
        Url = new FakeUrlHelper(new ActionContext(http, ctx.RouteData, ctx.ActionDescriptor)),
        TempData = new Microsoft.AspNetCore.Mvc.ViewFeatures.TempDataDictionary(
            http, new FakeTempDataProvider())
    };
}

InterpretationController Interpretation(string? sessionEmail)
{
    var http = new DefaultHttpContext { Session = new FakeSession(sessionEmail) };
    return new InterpretationController(db,
        Options.Create(new GeminiSettings()),
        null!, null!, null!, null!, null!, null!,
        NullLogger<InterpretationController>.Instance)
    {
        ControllerContext = Ctx(http),
        TempData = new Microsoft.AspNetCore.Mvc.ViewFeatures.TempDataDictionary(
            http, new FakeTempDataProvider())
    };
}

ProfilesController Profiles(string sessionEmail)
{
    var http = new DefaultHttpContext { Session = new FakeSession(sessionEmail) };
    return new ProfilesController(db, null!, null!, null!, null!, null!, null!, null!,
        NullLogger<ProfilesController>.Instance)
    {
        ControllerContext = Ctx(http),
        TempData = new Microsoft.AspNetCore.Mvc.ViewFeatures.TempDataDictionary(
            http, new FakeTempDataProvider())
    };
}

static ControllerContext Ctx(HttpContext http) => new()
{
    HttpContext = http,
    RouteData = new RouteData(),
    ActionDescriptor = new ControllerActionDescriptor()
};

// =====================================================================
//  1. The account type itself
// =====================================================================
Check("1. 'Cabinet' is recognised in any casing",
    AccountTypes.Normalize("Cabinet") == "Cabinet"
    && AccountTypes.Normalize("cabinet") == "Cabinet"
    && AccountTypes.Normalize("  CABINET ") == "Cabinet");
Check("1b. the other two types are untouched",
    AccountTypes.Normalize("Clinic") == "Clinic"
    && AccountTypes.Normalize("Individual") == "Individual");
Check("1c. anything invented falls back to Individual (safest)",
    AccountTypes.Normalize("Admin") == "Individual"
    && AccountTypes.Normalize(null) == "Individual"
    && AccountTypes.Normalize("") == "Individual");
Check("1d. a cabinet uses the personal (B2C) screens, a clinic does not",
    AccountTypes.UsesPersonalScreens("Cabinet") && !AccountTypes.UsesPersonalScreens("Clinic"));

// =====================================================================
//  2. Registration: the practice name is mandatory
// =====================================================================
var noName = Account(form: new Dictionary<string, string>());
var resNoName = await noName.Register(new RegisterViewModel
{
    Email = "cab1@test.ro", Parola = "Abcdef1!", ConfirmParola = "Abcdef1!",
    UserType = "Cabinet", CabinetName = "   "
});
Check("2. a cabinet sign-up without a practice name is refused",
    noName.ModelState[nameof(RegisterViewModel.CabinetName)]?.Errors.Count > 0,
    (resNoName as ViewResult)?.ViewName ?? resNoName.GetType().Name);
Check("2b. nothing was stored for that attempt", pending.Get("cab1@test.ro") == null);

// =====================================================================
//  3. Registration: the happy path carries the name through verification
// =====================================================================
mail.Sent.Clear();
var reg = Account(form: new Dictionary<string, string>());
await reg.Register(new RegisterViewModel
{
    Email = " Cab@Test.ro ", Parola = "Abcdef1!", ConfirmParola = "Abcdef1!",
    UserType = "cabinet", CabinetName = "  DR. Ionescu Felicia — Medic de familie  "
});
var pend = pending.Get("cab@test.ro");
Check("3. the pending registration keeps the Cabinet type and the practice name",
    pend?.UserType == "Cabinet" && pend?.CabinetName == "DR. Ionescu Felicia — Medic de familie",
    pend == null ? "null" : $"{pend.UserType}/{pend.CabinetName}");
Check("3b. a verification email was sent", mail.Sent.Count == 1, mail.Sent.Count.ToString());

var verify = Account(form: new Dictionary<string, string>());
var afterVerify = await verify.VerifyEmail(new VerifyEmailViewModel
{
    Email = "cab@test.ro", Code = pend!.VerificationCode
});
var cabUser = await db.Users.FirstOrDefaultAsync(u => u.Email == "cab@test.ro");
Check("3c. the account is created as Cabinet, with the practice name stored",
    cabUser?.UserType == "Cabinet" && cabUser?.CabinetName == "DR. Ionescu Felicia — Medic de familie",
    cabUser == null ? "null" : $"{cabUser.UserType}/{cabUser.CabinetName}");
Check("3d. it gets the 1 free (bonus) credit, no paid credits",
    cabUser!.BonusCredits == 1 && cabUser.BonusCreditsConsumed == 0 && cabUser.Credite == 0,
    $"bonus={cabUser.BonusCredits} paid={cabUser.Credite}");
Check("3e. a default patient profile is seeded",
    await db.Profiles.CountAsync(p => p.UserEmail == "cab@test.ro") == 1);
Check("3f. it lands straight on the interpretation screen",
    (afterVerify as RedirectToActionResult)?.ActionName == "Upload"
    && (afterVerify as RedirectToActionResult)?.ControllerName == "Interpretation",
    (afterVerify as RedirectToActionResult)?.ActionName ?? afterVerify.GetType().Name);
Check("3g. NO clinic row was created for a cabinet",
    !await db.Clinics.AnyAsync(c => c.UserEmail == "cab@test.ro"));

// The "free interpretation" CTA must still force a B2C account.
var freeFlow = Account(form: new Dictionary<string, string> { ["flow"] = "free" });
await freeFlow.Register(new RegisterViewModel
{
    Email = "free1@test.ro", Parola = "Abcdef1!", ConfirmParola = "Abcdef1!",
    UserType = "Cabinet", CabinetName = "Hack"
});
var freePend = pending.Get("free1@test.ro");
Check("3h. the free-interpretation flow still forces Individual (no cabinet by URL)",
    freePend?.UserType == "Individual" && freePend?.CabinetName == null,
    freePend == null ? "null" : $"{freePend.UserType}/{freePend.CabinetName}");

// =====================================================================
//  4. Pricing: one package for the cabinet, the others untouched
// =====================================================================
var cabinetPkgs = CreditPackages.ForAudience("Cabinet").ToList();
Check("4. the cabinet sees exactly ONE package", cabinetPkgs.Count == 1, cabinetPkgs.Count.ToString());
Check("4b. that package is 89 EUR for 45 credits",
    cabinetPkgs[0].PriceEur == 89m && cabinetPkgs[0].Credits == 45,
    $"{cabinetPkgs[0].PriceEur}/{cabinetPkgs[0].Credits}");
Check("4c. it is a SEPARATE offer, not the B2C premium",
    cabinetPkgs[0].Key == "cabinet_premium" && cabinetPkgs[0].NameKey == "PackageCabinetPremium");
Check("4d. B2C still sees its 4 tiers, CAM its 3 (regression)",
    CreditPackages.ForAudience("Individual").Count() == 4
    && CreditPackages.ForAudience("Clinic").Count() == 3,
    $"{CreditPackages.ForAudience("Individual").Count()}/{CreditPackages.ForAudience("Clinic").Count()}");

var buy = await Credits("cab@test.ro").Buy();
var offered = (buy as ViewResult)?.Model as List<CreditPackage>;
Check("4e. the Buy page really offers the cabinet package only",
    offered?.Count == 1 && offered[0].Key == "cabinet_premium",
    (offered?.Count ?? -1).ToString());

// A cabinet must not buy a B2C tier by typing the URL.
var sneak = await Credits("cab@test.ro").Checkout(new CheckoutViewModel
{
    PackageKey = "premium", CardNumber = "4111111111111111",
    CardHolder = "T T", Expiry = "12/30", Cvv = "123"
});
await db.Entry(cabUser).ReloadAsync();
Check("4f. buying a B2C package as a cabinet is refused (no credits granted)",
    (sneak as RedirectToActionResult)?.ActionName == "Buy" && cabUser.Credite == 0,
    $"{(sneak as RedirectToActionResult)?.ActionName}/credite={cabUser.Credite}");

// =====================================================================
//  5. Buying the cabinet package
// =====================================================================
// A blurred free report exists, so the first purchase must unlock it (decision 1a).
db.InterpretationHistories.Add(new InterpretationHistory
{
    UserEmail = "cab@test.ro", OriginalFileName = "pacient.pdf", Language = "ro",
    Status = "success", CreditsConsumed = 1, ProfileId = 1, PdfSha256 = "h",
    CreatedAt = DateTime.UtcNow,
    RawJsonResult = "{\"is_medical_analysis\":true,\"summary\":\"ok\",\"key_results\":[" +
                    "{\"parameter\":\"Hemoglobina\",\"value\":\"9.1\",\"unit\":\"g/dL\"," +
                    "\"reference_range\":\"12-16\",\"status\":\"low\"}]}"
});
await db.SaveChangesAsync();
mail.Sent.Clear();

var bought = await Credits("cab@test.ro").Checkout(new CheckoutViewModel
{
    PackageKey = "cabinet_premium", CardNumber = "4111111111111111",
    CardHolder = "T T", Expiry = "12/30", Cvv = "123"
});
await db.Entry(cabUser).ReloadAsync();
Check("5. the 45 credits landed and 89 EUR were recorded",
    cabUser.Credite == 45 && cabUser.CreditRest == 45 && cabUser.TotalPaid == 89m,
    $"{cabUser.Credite}/{cabUser.CreditRest}/{cabUser.TotalPaid}");
Check("5b. the purchase row keeps the cabinet package key",
    await db.Purchases.AnyAsync(p => p.UserEmail == "cab@test.ro" && p.PackageKey == "cabinet_premium"));
Check("5c. the bonus credit is still untouched",
    cabUser.BonusCreditsRemaining == 1, cabUser.BonusCreditsRemaining.ToString());
Check("5d. the free blurred report was unlocked and emailed in full (retroactive)",
    mail.Sent.Any(m => m.to == "cab@test.ro" && m.attachments == 1),
    string.Join(",", mail.Sent.Select(m => m.to + ":" + m.attachments)));
Check("5e. after buying, the cabinet goes back to its own dashboard (not CAM)",
    (bought as RedirectToActionResult)?.ActionName == "Dashboard"
    && (bought as RedirectToActionResult)?.ControllerName == "Account",
    (bought as RedirectToActionResult)?.ActionName ?? bought.GetType().Name);

// =====================================================================
//  6. The 2000-patient cap
// =====================================================================
User Cab(int paid = 45) => new()
{
    Email = "c@x.ro", UserType = "Cabinet", CabinetName = "Cab",
    Credite = paid, CreditRest = paid, BonusCredits = 1
};
User B2c(int paid = 5) => new()
{
    Email = "i@x.ro", UserType = "Individual", Credite = paid, CreditRest = paid, BonusCredits = 1
};
User Clinic() => new() { Email = "l@x.ro", UserType = "Clinic", Credite = 17, CreditRest = 17 };

Check("6. the cabinet cap is 2000 patients",
    ProfileGateService.LimitFor(Cab()) == 2000 && ProfileGateService.MaxProfilesPerCabinet == 2000);
Check("6b. B2C is still capped at 20 (regression)",
    ProfileGateService.LimitFor(B2c()) == 20);
Check("6c. clinics stay uncapped (regression)",
    ProfileGateService.LimitFor(Clinic()) == null && !ProfileGateService.IsCapped(Clinic()));
Check("6d. a cabinet at 1999 patients can add one more",
    ProfileGateService.CanCreateAdditionalProfile(Cab(), 1999));
Check("6e. a cabinet at 2000 patients is refused",
    !ProfileGateService.CanCreateAdditionalProfile(Cab(), 2000)
    && ProfileGateService.IsAtProfileLimit(Cab(), 2000));
Check("6f. a cabinet above the cap keeps its patients, only new ones are refused",
    !ProfileGateService.CanCreateAdditionalProfile(Cab(), 2500)
    && ProfileGateService.IsAtProfileLimit(Cab(), 2500));
Check("6g. the paid-credit rule applies to the cabinet too (user decision)",
    !ProfileGateService.CanCreateAdditionalProfile(Cab(paid: 0), 1)
    && ProfileGateService.CanCreateAdditionalProfile(Cab(paid: 45), 1));
Check("6h. B2C at 20 is still refused, at 19 still allowed (regression)",
    !ProfileGateService.CanCreateAdditionalProfile(B2c(), 20)
    && ProfileGateService.CanCreateAdditionalProfile(B2c(), 19));

// The counter the cabinet actually sees on screen.
var pc = Profiles("cab@test.ro");
await pc.Index();
Check("6i. the profiles page publishes the 2000 cap for the badge",
    (pc.ViewBag.ProfileLimit as int?) == 2000
    && (pc.ViewBag.ProfileCapApplies as bool?) == true
    && (pc.ViewBag.ProfileCount as int?) == 1,
    $"{pc.ViewBag.ProfileLimit}/{pc.ViewBag.ProfileCount}");

// =====================================================================
//  6j. Etapa 2: search + paging for the cabinet, nothing for B2C
// =====================================================================
{
    // 60 patients for the cabinet: 3 pages of 24.
    var extra = new List<Profile>();
    for (int i = 1; i <= 59; i++)
    {
        extra.Add(new Profile
        {
            UserEmail = "cab@test.ro",
            Name = $"Pacient {i:D3}",
            Notes = i == 7 ? "fisa 12345 diabet" : (i == 8 ? "fisa 99999" : null),
            IsDefault = false,
            CreatedAt = DateTime.UtcNow
        });
    }
    db.Profiles.AddRange(extra);
    await db.SaveChangesAsync();

    var cabList = Profiles("cab@test.ro");
    var page1 = ((await cabList.Index()) as ViewResult)?.Model as ProfilesIndexViewModel;
    Check("6j. cabinet list is paged: 24 patients on page 1 of 3",
        page1 != null && page1.IsPaged && page1.Profiles.Count == 24
        && page1.Page == 1 && page1.TotalPages == 3 && page1.TotalCount == 60,
        page1 == null ? "null" : $"{page1.Profiles.Count}/{page1.TotalPages}/{page1.TotalCount}");
    Check("6k. the default patient stays first on page 1",
        page1!.Profiles[0].IsDefault);

    var page3 = ((await Profiles("cab@test.ro").Index(null, 3)) as ViewResult)?.Model as ProfilesIndexViewModel;
    Check("6l. the last page holds the remaining 12 patients",
        page3 != null && page3.Page == 3 && page3.Profiles.Count == 12,
        page3 == null ? "null" : $"{page3.Page}/{page3.Profiles.Count}");
    Check("6m. no patient appears on two pages",
        !page1!.Profiles.Select(x => x.Id).Intersect(page3!.Profiles.Select(x => x.Id)).Any());

    var tooFar = ((await Profiles("cab@test.ro").Index(null, 99)) as ViewResult)?.Model as ProfilesIndexViewModel;
    Check("6n. a page number past the end lands on the last page, not on an empty screen",
        tooFar != null && tooFar.Page == 3 && tooFar.Profiles.Count == 12,
        tooFar == null ? "null" : $"{tooFar.Page}/{tooFar.Profiles.Count}");

    var byName = ((await Profiles("cab@test.ro").Index("pacient 01")) as ViewResult)?.Model as ProfilesIndexViewModel;
    Check("6o. search by name finds only the matching patients",
        byName != null && byName.MatchingCount == 10
        && byName.Profiles.All(x => x.Name.StartsWith("Pacient 01")),
        (byName?.MatchingCount ?? -1).ToString());
    Check("6p. search keeps the real owned total for the counter",
        byName!.TotalCount == 60, byName.TotalCount.ToString());

    var byNotes = ((await Profiles("cab@test.ro").Index("12345")) as ViewResult)?.Model as ProfilesIndexViewModel;
    Check("6q. search also looks into the notes (file number)",
        byNotes != null && byNotes.MatchingCount == 1 && byNotes.Profiles[0].Name == "Pacient 007",
        byNotes == null ? "null" : $"{byNotes.MatchingCount}");

    var noHit = ((await Profiles("cab@test.ro").Index("zzz-nu-exista")) as ViewResult)?.Model as ProfilesIndexViewModel;
    Check("6r. a search with no hit returns an empty page, not an error",
        noHit != null && noHit.MatchingCount == 0 && noHit.Profiles.Count == 0);

    var caseInsensitive = ((await Profiles("cab@test.ro").Index("PACIENT 007")) as ViewResult)?.Model as ProfilesIndexViewModel;
    Check("6s. search is case-insensitive",
        caseInsensitive != null && caseInsensitive.MatchingCount == 1);

    // B2C: the screen must stay EXACTLY as it was — no paging, no server search.
    db.Profiles.AddRange(Enumerable.Range(1, 30).Select(i => new Profile
    {
        UserEmail = "b2c@test.ro", Name = $"Membru {i:D2}", CreatedAt = DateTime.UtcNow
    }));
    await db.SaveChangesAsync();
    var b2cList = ((await Profiles("b2c@test.ro").Index("membru 01")) as ViewResult)?.Model as ProfilesIndexViewModel;
    Check("6t. B2C list is NOT paged and ignores the search box (regression)",
        b2cList != null && !b2cList.IsPaged && b2cList.Query == null
        && b2cList.Profiles.Count == 30 && b2cList.PageSize == 0,
        b2cList == null ? "null" : $"{b2cList.IsPaged}/{b2cList.Profiles.Count}");

    // The patient picker endpoint used by the interpretation screen.
    var interp = Interpretation("cab@test.ro");
    var found = ((await interp.SearchProfiles("pacient")) as JsonResult)?.Value;
    var foundCount = ((System.Collections.IEnumerable)found!).Cast<object>().Count();
    Check("6u. the patient picker returns at most 20 matches per keystroke",
        foundCount == 20, foundCount.ToString());

    var pickedByNotes = ((await Interpretation("cab@test.ro").SearchProfiles("99999")) as JsonResult)?.Value;
    var pickedNames = ((System.Collections.IEnumerable)pickedByNotes!).Cast<object>()
        .Select(o => o.GetType().GetProperty("name")!.GetValue(o) as string).ToList();
    Check("6v. the picker also searches the notes",
        pickedNames.Count == 1 && pickedNames[0] == "Pacient 008",
        string.Join(",", pickedNames));

    var otherAccount = ((await Interpretation("b2c@test.ro").SearchProfiles("Pacient")) as JsonResult)?.Value;
    Check("6w. the picker never leaks another account's patients",
        !((System.Collections.IEnumerable)otherAccount!).Cast<object>().Any());

    var anon = await Interpretation(null).SearchProfiles("x");
    Check("6x. the picker refuses anonymous callers", anon is UnauthorizedResult);
}

// =====================================================================
//  7. Texts in all 7 languages
// =====================================================================
var langs = new[] { "en", "ro", "fr", "es", "de", "it", "pt" };
var keys = new[] { "UserTypeCabinet", "CabinetNameLabel", "CabinetNamePlaceholder",
                   "CabinetNameRequired", "RegisterCabinetNote", "PackageCabinetPremium" };
var missing = new List<string>();
foreach (var lang in langs)
    foreach (var key in keys)
    {
        var value = Loc.T(key, lang);
        if (value == key || string.IsNullOrWhiteSpace(value)) missing.Add($"{lang}/{key}");
    }
Check("7. all cabinet texts exist in all 7 languages",
    missing.Count == 0, string.Join(", ", missing));
Check("7b. the 2000 limit message is formatted with the right number",
    string.Format(Loc.T("ProfileLimitReached", "ro"), 2000).Contains("2000")
    && string.Format(Loc.T("ProfileQuotaBadge", "ro"), 7, 2000).Contains("2000"));
Console.WriteLine("      ro: " + Loc.T("UserTypeCabinet", "ro")
                  + " | " + string.Format(Loc.T("ProfileQuotaBadge", "ro"), 7, 2000));

// =====================================================================
//  8. Regression: B2C and CAM sign-ups still behave exactly as before
// =====================================================================
var b2cReg = Account(form: new Dictionary<string, string>());
await b2cReg.Register(new RegisterViewModel
{
    Email = "pers@test.ro", Parola = "Abcdef1!", ConfirmParola = "Abcdef1!",
    UserType = "Individual"
});
var persPend = pending.Get("pers@test.ro");
Check("8. a B2C sign-up still works with no extra field",
    persPend?.UserType == "Individual" && persPend?.CabinetName == null);

var clinicReg = Account(form: new Dictionary<string, string>());
await clinicReg.Register(new RegisterViewModel
{
    Email = "clin@test.ro", Parola = "Abcdef1!", ConfirmParola = "Abcdef1!",
    UserType = "Clinic", ClinicName = "Lab SRL"
});
Check("8b. a clinic sign-up still demands city and address (regression)",
    clinicReg.ModelState[nameof(RegisterViewModel.ClinicCity)]?.Errors.Count > 0
    && clinicReg.ModelState[nameof(RegisterViewModel.ClinicAddress)]?.Errors.Count > 0);

var clinicOk = Account(form: new Dictionary<string, string>());
await clinicOk.Register(new RegisterViewModel
{
    Email = "clin2@test.ro", Parola = "Abcdef1!", ConfirmParola = "Abcdef1!",
    UserType = "Clinic", ClinicName = "Lab SRL", ClinicCity = "Cluj", ClinicAddress = "Str. 1"
});
var clinPend = pending.Get("clin2@test.ro");
var clinVerify = Account(form: new Dictionary<string, string>());
var clinRedirect = await clinVerify.VerifyEmail(new VerifyEmailViewModel
{
    Email = "clin2@test.ro", Code = clinPend!.VerificationCode
});
var clinUser = await db.Users.FirstOrDefaultAsync(u => u.Email == "clin2@test.ro");
Check("8c. the clinic account is still created with its Clinic row and CAM redirect",
    clinUser?.UserType == "Clinic" && clinUser?.CabinetName == null
    && await db.Clinics.AnyAsync(c => c.UserEmail == "clin2@test.ro")
    && (clinRedirect as RedirectToActionResult)?.RouteValues?["area"] as string == "CAM",
    clinUser?.UserType ?? "null");

// =====================================================================
//  9. Admin: the new type is visible and filterable
// =====================================================================
{
    var admin = new AdminController(db, null!, null!,
        Options.Create(new GeminiPricing()),
        Options.Create(new GeminiSettings()),
        Options.Create(new LoincMatcherSettings()),
        null!, NullLogger<AdminController>.Instance)
    {
        ControllerContext = Ctx(new DefaultHttpContext { RequestServices = sp }),
        TempData = new Microsoft.AspNetCore.Mvc.ViewFeatures.TempDataDictionary(
            new DefaultHttpContext(), new FakeTempDataProvider())
    };

    var onlyCabinets = await admin.Users(null, "cabinet");
    var cabRows = (onlyCabinets as ViewResult)?.Model as List<UserListItem>;
    Check("9. the admin list can filter to Cabinet accounts only",
        cabRows != null && cabRows.Count == 1 && cabRows[0].User.Email == "cab@test.ro",
        cabRows == null ? "null" : string.Join(",", cabRows.Select(r => r.User.Email)));

    var onlyIndividuals = await admin.Users(null, "individual");
    var indRows = (onlyIndividuals as ViewResult)?.Model as List<UserListItem>;
    Check("9b. the Individuals filter does NOT include cabinets",
        indRows != null && indRows.All(r => r.User.Email != "cab@test.ro"),
        indRows == null ? "null" : string.Join(",", indRows.Select(r => r.User.Email)));

    var allRows = ((await admin.Users(null, "all")) as ViewResult)?.Model as List<UserListItem>;
    Check("9c. the cabinet still shows up in the unfiltered list, with its practice name",
        allRows != null && allRows.Any(r => r.User.Email == "cab@test.ro"
                                            && r.User.CabinetName == "DR. Ionescu Felicia — Medic de familie"),
        (allRows?.Count ?? -1).ToString());
}

Console.WriteLine(fails == 0 ? "\nALL CHECKS PASSED" : $"\n{fails} CHECK(S) FAILED");
return fails == 0 ? 0 : 1;

// The probe has no endpoint routing, so Url.Action() would throw. The real
// app builds links normally; here any absolute-ish string is enough.
sealed class FakeUrlHelper : IUrlHelper
{
    public FakeUrlHelper(ActionContext ctx) => ActionContext = ctx;
    public ActionContext ActionContext { get; }
    public string? Action(UrlActionContext actionContext) => "/Profiles";
    public string? Content(string? contentPath) => contentPath;
    public bool IsLocalUrl(string? url) => true;
    public string? Link(string? routeName, object? values) => "/Profiles";
    public string? RouteUrl(UrlRouteContext routeContext) => "/Profiles";
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

sealed class FakeTempDataProvider : Microsoft.AspNetCore.Mvc.ViewFeatures.ITempDataProvider
{
    public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();
    public void SaveTempData(HttpContext context, IDictionary<string, object> values) { }
}

sealed class FakeSession : ISession
{
    private readonly Dictionary<string, byte[]> _store = new();
    public FakeSession(string? userEmail)
    {
        if (userEmail != null) _store["UserEmail"] = System.Text.Encoding.UTF8.GetBytes(userEmail);
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
