using MedicalApp.Data;
using MedicalApp.Models;
using MedicalApp.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

// Probe for the free period of the premium archive features:
// extended from 1 year to 3 years from registration (June 2026).

int fails = 0;
void Check(string what, bool ok, string? detail = null)
{
    Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {what}{(detail is null ? "" : "  ->  " + detail)}");
    if (!ok) fails++;
}

var services = new ServiceCollection();
// One name for ALL scopes: the seed opens its own scope and must see the same
// in-memory database (a Guid inside the lambda would create a new one each time).
var dbName = "freeperiod-" + Guid.NewGuid();
services.AddDbContext<AppDbContext>(o => o.UseInMemoryDatabase(dbName));
services.AddLogging(b => b.SetMinimumLevel(LogLevel.Error));
var sp = services.BuildServiceProvider();
var db = sp.GetRequiredService<AppDbContext>();

// =====================================================================
//  1. The period itself
// =====================================================================
Check("1. the free period is 3 years", ArchiveAccessService.FreeYears == 3,
    ArchiveAccessService.FreeYears.ToString());

var registered = new DateTime(2026, 9, 13, 10, 0, 0, DateTimeKind.Utc);
Check("1b. it is counted from the registration date, on the same calendar day",
    ArchiveAccessService.FreeUntilFrom(registered) == new DateTime(2029, 9, 13, 10, 0, 0, DateTimeKind.Utc),
    ArchiveAccessService.FreeUntilFrom(registered).ToString("u"));
Check("1c. a 29 February sign-up does not throw or drift",
    ArchiveAccessService.FreeUntilFrom(new DateTime(2028, 2, 29, 8, 0, 0, DateTimeKind.Utc))
        == new DateTime(2031, 2, 28, 8, 0, 0, DateTimeKind.Utc));

User NewUser(string email, DateTime dataC, DateTime? freeUntil) => new()
{
    Email = email, Parola = "x", DataC = dataC, FreeArchiveUntil = freeUntil,
    Credite = 0, CreditRest = 0, CreditConsum = 0, BonusCredits = 1, BonusCreditsConsumed = 0
};

// =====================================================================
//  2. Premium features stay free inside the 3 years
// =====================================================================
var archive = new ArchiveAccessService(db,
    NullLogger<ArchiveAccessService>.Instance);

// Registered 2 years ago: under the old 1-year rule this user was already paying.
var twoYearsAgo = DateTime.UtcNow.AddYears(-2);
var u2 = NewUser("two@test.ro", twoYearsAgo, ArchiveAccessService.FreeUntilFrom(twoYearsAgo));
Check("2. a user who signed up 2 years ago is still inside the free period",
    ArchiveAccessService.IsInFreePeriod(u2));

var r1 = archive.TryConsume(u2, "compare");
Check("2b. comparing costs nothing and touches no counter",
    r1.Allowed && !r1.CreditConsumed && u2.ArchivePremiumCounter == 0
    && u2.BonusCreditsConsumed == 0 && u2.CreditConsum == 0);

for (int i = 0; i < 12; i++) archive.TryConsume(u2, "chart");
Check("2c. 12 more uses are still free — no 3-use bundle inside the free period",
    u2.ArchivePremiumCounter == 0 && u2.BonusCreditsConsumed == 0 && u2.CreditConsum == 0);

// Registered 3 years and a day ago: the period is over.
var overdue = DateTime.UtcNow.AddYears(-3).AddDays(-1);
var uOld = NewUser("old@test.ro", overdue, ArchiveAccessService.FreeUntilFrom(overdue));
Check("3. after 3 years the free period is over",
    !ArchiveAccessService.IsInFreePeriod(uOld));
var billed = archive.TryConsume(uOld, "compare");
Check("3b. and the 3-uses-per-credit rule kicks in again, unchanged",
    billed.Allowed && !billed.CreditConsumed && uOld.ArchivePremiumCounter == 1);
archive.TryConsume(uOld, "compare");
archive.TryConsume(uOld, "compare");
var charged = archive.TryConsume(uOld, "compare");
Check("3c. the 4th use charges exactly 1 credit (bonus first)",
    charged.Allowed && charged.CreditConsumed
    && uOld.BonusCreditsConsumed == 1 && uOld.ArchivePremiumCounter == 1,
    $"bonus={uOld.BonusCreditsConsumed} counter={uOld.ArchivePremiumCounter}");

// =====================================================================
//  4. Existing accounts are extended by the start-up seed
// =====================================================================
var yesterday = DateTime.UtcNow.AddDays(-1);
var lastYear = DateTime.UtcNow.AddYears(-1);
db.Users.AddRange(
    // signed up yesterday with the OLD 1-year date stored
    NewUser("old1@test.ro", yesterday, yesterday.AddYears(1)),
    // signed up a year ago with the OLD date stored (already 1 day from expiring)
    NewUser("old2@test.ro", lastYear, lastYear.AddYears(1)),
    // legacy row with nothing stored at all
    NewUser("legacy@test.ro", lastYear, null),
    // a hand-granted, longer courtesy period — must NOT be shortened
    NewUser("vip@test.ro", lastYear, DateTime.UtcNow.AddYears(9)),
    // already correct — must stay untouched
    NewUser("ok@test.ro", yesterday, ArchiveAccessService.FreeUntilFrom(yesterday))
);
await db.SaveChangesAsync();

await StartupSeed.EnsureFreeArchiveUntilAsync(sp, NullLogger.Instance);

async Task<User> Get(string email) =>
    (await db.Users.AsNoTracking().FirstAsync(u => u.Email == email));

var old1 = await Get("old1@test.ro");
Check("4. an account with the old 1-year date is extended to registration + 3 years",
    old1.FreeArchiveUntil == ArchiveAccessService.FreeUntilFrom(old1.DataC),
    old1.FreeArchiveUntil?.ToString("u"));

var old2 = await Get("old2@test.ro");
Check("4b. an account about to expire gets 2 more years, counted from its sign-up",
    old2.FreeArchiveUntil == old2.DataC.AddYears(3)
    && old2.FreeArchiveUntil > DateTime.UtcNow.AddYears(1).AddDays(300),
    old2.FreeArchiveUntil?.ToString("u"));

var legacy = await Get("legacy@test.ro");
Check("4c. a legacy row with no date at all is filled in",
    legacy.FreeArchiveUntil == legacy.DataC.AddYears(3),
    legacy.FreeArchiveUntil?.ToString("u"));

var vip = await Get("vip@test.ro");
Check("4d. a longer courtesy period is NEVER shortened",
    vip.FreeArchiveUntil > DateTime.UtcNow.AddYears(8),
    vip.FreeArchiveUntil?.ToString("u"));

var okUser = await Get("ok@test.ro");
Check("4e. an already-correct account keeps its exact date",
    okUser.FreeArchiveUntil == ArchiveAccessService.FreeUntilFrom(okUser.DataC));

// Running the seed twice must change nothing (it runs at every app start).
var before = await db.Users.AsNoTracking()
    .OrderBy(u => u.Email).Select(u => u.Email + "|" + u.FreeArchiveUntil).ToListAsync();
await StartupSeed.EnsureFreeArchiveUntilAsync(sp, NullLogger.Instance);
var after = await db.Users.AsNoTracking()
    .OrderBy(u => u.Email).Select(u => u.Email + "|" + u.FreeArchiveUntil).ToListAsync();
Check("5. running the seed again is a no-op (idempotent)",
    before.SequenceEqual(after));

// Everyone now really is inside the free period.
var allUsers = await db.Users.AsNoTracking().ToListAsync();
Check("5b. every account now has premium archive features free",
    allUsers.All(u => ArchiveAccessService.IsInFreePeriod(u)),
    allUsers.Count + " accounts");

// =====================================================================
//  6. The on-screen message, in all 7 languages
// =====================================================================
var langs = new[] { "en", "ro", "fr", "es", "de", "it", "pt" };
var stillSaysOne = new List<string>();
var missing = new List<string>();
foreach (var lang in langs)
{
    var raw = Loc.T("HistoryPremiumFreeFmt", lang);
    if (raw == "HistoryPremiumFreeFmt" || !raw.Contains("{0}")) { missing.Add(lang); continue; }
    if (!raw.Contains("3")) stillSaysOne.Add(lang);
}
Check("6. the message exists with its date placeholder in all 7 languages",
    missing.Count == 0, string.Join(",", missing));
Check("6b. and every language says 3 years, not 1",
    stillSaysOne.Count == 0, string.Join(",", stillSaysOne));
Console.WriteLine("      en: " + string.Format(Loc.T("HistoryPremiumFreeFmt", "en"), "13 Sep 2029"));
Console.WriteLine("      ro: " + string.Format(Loc.T("HistoryPremiumFreeFmt", "ro"), "13 sep 2029"));

Console.WriteLine(fails == 0 ? "\nALL CHECKS PASSED" : $"\n{fails} CHECK(S) FAILED");
return fails == 0 ? 0 : 1;
