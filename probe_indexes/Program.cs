using MedicalApp.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

// Probe for the SQL index audit (June 2026, hosting prep step 4).
// Builds the real EF model (no database needed) and checks that every hot
// query of the app has a matching index, that redundant indexes are gone and
// that no index would be rejected by SQL Server.

int fails = 0;
void Check(string label, bool ok, string detail = "")
{
    Console.WriteLine((ok ? "PASS  " : "FAIL  ") + label + (detail.Length > 0 ? "  ->  " + detail : ""));
    if (!ok) fails++;
}

var options = new DbContextOptionsBuilder<AppDbContext>()
    .UseSqlServer("Server=unreachable;Database=probe;Trusted_Connection=True;")
    .Options;
using var db = new AppDbContext(options);
// Design-time model: the runtime one drops metadata we need (sort direction).
var model = db.GetService<Microsoft.EntityFrameworkCore.Metadata.IDesignTimeModel>().Model;

IEntityType Entity(string table) =>
    model.GetEntityTypes().First(e => e.GetTableName() == table);

List<IIndex> Indexes(string table) => Entity(table).GetIndexes().ToList();

string Shape(IIndex ix)
{
    var desc = ix.IsDescending;
    var cols = ix.Properties.Select((p, i) =>
        p.Name + (desc != null && (desc.Count == 0 || (i < desc.Count && desc[i])) ? " DESC" : ""));
    var inc = ix.FindAnnotation("SqlServer:Include")?.Value as string[];
    return "[" + string.Join(", ", cols) + "]"
           + (inc != null && inc.Length > 0 ? " INCLUDE(" + string.Join(", ", inc) + ")" : "");
}

// An index matches when the key columns AND their sort direction are exactly
// the ones the query needs, and the extra output columns are carried along.
IIndex? Match(string table, string[] keys, bool[]? descending = null, string[]? include = null)
{
    foreach (var ix in Indexes(table))
    {
        if (!ix.Properties.Select(p => p.Name).SequenceEqual(keys)) continue;

        var want = descending ?? new bool[keys.Length];
        var have = new bool[keys.Length];
        var d = ix.IsDescending;
        if (d != null)
            for (int i = 0; i < keys.Length; i++)
                have[i] = d.Count == 0 || (i < d.Count && d[i]);
        if (!have.SequenceEqual(want)) continue;

        if (include != null)
        {
            var inc = ix.FindAnnotation("SqlServer:Include")?.Value as string[] ?? Array.Empty<string>();
            if (!include.All(c => inc.Contains(c))) continue;
        }
        return ix;
    }
    return null;
}

bool Has(string table, string[] keys, bool[]? descending = null, string[]? include = null)
    => Match(table, keys, descending, include) != null;

string Dump(string table) => string.Join(" | ", Indexes(table).Select(Shape));

// =====================================================================
//  1. InterpretationHistories — the biggest table and the hottest queries
// =====================================================================
Check("1. archive / charts / comparisons: (UserEmail, ProfileId, Status) ordered by CreatedAt DESC",
    Has("InterpretationHistories",
        new[] { "UserEmail", "ProfileId", "Status", "CreatedAt" },
        new[] { false, false, false, true }),
    Dump("InterpretationHistories"));

Check("2. job pill poll (newest row of the user) is covered — Status without touching the table",
    Has("InterpretationHistories", new[] { "UserEmail", "Id" }, new[] { false, true },
        new[] { "Status" }));

Check("3. global 'processing' sweep + ETA from the last durations is covered",
    Has("InterpretationHistories", new[] { "Status", "Id" }, new[] { false, true },
        new[] { "DurationMs" }));

Check("4. interpretations-per-profile on the profiles list",
    Has("InterpretationHistories", new[] { "ProfileId", "Status" }));

Check("5. duplicate PDF check on every upload",
    Has("InterpretationHistories", new[] { "UserEmail", "PdfSha256" }));

Check("6. the redundant standalone index on UserEmail was removed (one less write per row)",
    !Has("InterpretationHistories", new[] { "UserEmail" }));

// =====================================================================
//  2. Admin dashboard tables
// =====================================================================
Check("7. revenue queries (since <date>) are covered by the amount",
    Has("Purchases", new[] { "PurchasedAt" }, null, new[] { "AmountEur" }));

Check("8. AI usage widgets: one index on (CreatedAt, Status) carrying everything they group by",
    Has("AiUsageLogs", new[] { "CreatedAt", "Status" }, null,
        new[] { "Source", "ModelUsed", "InputTokens", "OutputTokens" }),
    Dump("AiUsageLogs"));

Check("8b. the never-used standalone indexes on AiUsageLogs are gone",
    !Has("AiUsageLogs", new[] { "Status" })
    && !Has("AiUsageLogs", new[] { "Source" })
    && !Has("AiUsageLogs", new[] { "CreatedAt" }));

// =====================================================================
//  3. CAM (B2B)
// =====================================================================
Check("9. CAM comparisons: all analyses of one patient inside one clinic",
    Has("ClinicAnalyses", new[] { "ClinicId", "PatientId" }));

Check("10. CAM dashboard: distinct patients in a period, covered by PatientId",
    Has("ClinicAnalyses", new[] { "ClinicId", "ProcessedAt" }, null, new[] { "PatientId" }),
    Dump("ClinicAnalyses"));

Check("10b. standalone ClinicId dropped (leading column of both composites)",
    !Has("ClinicAnalyses", new[] { "ClinicId" }));

Check("11. patient lookup by (clinic, name key, email) stays unique",
    Match("ClinicPatients", new[] { "ClinicId", "NameKey", "Email" })?.IsUnique == true);

// =====================================================================
//  4. Queue + LOINC cache (untouched, guarded against regressions)
// =====================================================================
Check("12. durable queue: recovery scan (Status, EnqueuedAt) and unique HistoryId",
    Has("InterpretationJobs", new[] { "Status", "EnqueuedAt" })
    && Match("InterpretationJobs", new[] { "HistoryId" })?.IsUnique == true);

Check("13. LOINC cache is read by primary key and counted per pipeline version",
    Entity("LoincMatchCache").FindPrimaryKey()!.Properties.Single().Name == "CacheKey"
    && Has("LoincMatchCache", new[] { "PipelineVersion" }));

// =====================================================================
//  5. Nothing SQL Server would refuse to create
// =====================================================================
var badType = new List<string>();
var tooWide = new List<string>();
foreach (var entity in model.GetEntityTypes())
{
    foreach (var ix in entity.GetIndexes())
    {
        int bytes = 0;
        foreach (var p in ix.Properties)
        {
            var max = p.GetMaxLength();
            var clr = p.ClrType;
            var unicode = p.IsUnicode() ?? true;
            if (clr == typeof(string) || clr == typeof(byte[]))
            {
                if (max == null) { badType.Add($"{entity.GetTableName()}.{p.Name}"); continue; }
                bytes += max.Value * (clr == typeof(string) && unicode ? 2 : 1);
            }
            else bytes += 8;
        }
        // SQL Server nonclustered key limit is 1700 bytes (900 for clustered/unique-on-clustered).
        if (bytes > 1700) tooWide.Add($"{entity.GetTableName()} {Shape(ix)} = {bytes}B");
    }
}
Check("14. no index key on an unbounded (nvarchar(max)) column",
    badType.Count == 0, string.Join(", ", badType));
Check("15. every index key fits in SQL Server's 1700-byte limit",
    tooWide.Count == 0, string.Join(", ", tooWide));

Console.WriteLine(fails == 0 ? "\nALL CHECKS PASSED" : $"\n{fails} CHECK(S) FAILED");
return fails == 0 ? 0 : 1;
