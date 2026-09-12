using System.Reflection;
using System.Text.Json;
using MedicalApp.Controllers;
using MedicalApp.Models;
using MedicalApp.Services;

static KeyResult K(string p, string v, string? unit, string? range, string status, string? code,
                   string? src = "semantic", double? score = 0.9, string? cls = "CHEM", string? longName = null)
    => new KeyResult
    {
        Parameter = p, Value = v, Unit = unit, ReferenceRange = range, Status = status,
        LoincCode = code, LoincSource = src, LoincScore = score, LoincClass = cls,
        LoincLongName = longName ?? (code == null ? null : "Long name of " + code)
    };

static InterpretationHistory H(int id, DateTime when, string dateTaken, params KeyResult[] krs)
{
    var res = new InterpretationResult
    {
        IsMedicalAnalysis = true,
        PatientInfo = new PatientInfo { Name = "Test", DateTaken = dateTaken, Laboratory = "Lab" },
        KeyResults = krs.ToList()
    };
    return new InterpretationHistory
    {
        Id = id, CreatedAt = when, OriginalFileName = $"file{id}.pdf", Status = "success",
        UserEmail = "t@t.t", Language = "ro",
        RawJsonResult = JsonSerializer.Serialize(res)
    };
}

var profile = new Profile { Id = 1, Name = "Eu", UserEmail = "t@t.t" };

var h1 = H(1, new DateTime(2025, 1, 10), "10/01/2025",
    K("Colesterol total", "250", "mg/dL", "0-200", "high", "2093-3"),
    K("Feritina", "500", "ng/mL", "20-250", "high", "2276-4"),
    K("INR", "1.06", null, "0.8-1.2", "high", "34714-6", src: "semantic", score: 0.64, cls: "COAG"),
    K("Raport A/G", "1.9", null, null, "high", "1759-0"),
    K("Fibrinogen", "4.32", "g/L", "2 - 4", "high", "3255-7", cls: "COAG"),
    K("Limfocite", "45", "%", "20-40", "high", "736-9"));

var h2 = H(2, new DateTime(2026, 1, 10), "10/01/2026",
    K("Colesterol Total", "260", "mg/dL", "0 - 200", "high", "14647-2", src: "anchor", score: null),
    K("Feritina", "600", null, "20-250", "high", "24373-3"),
    K("INR", "1.04", null, "0,8 - 1,2", "high", "6301-6", src: "anchor", score: null, cls: "COAG"),
    K("Raport A/G", "2.1", null, null, "high", "1759-2"),
    K("Fibrinogen", "516", "mg/dL", "200 - 400", "high", "3255-7", cls: "COAG"),
    K("Limfocite", "5.2", "10^3/uL", "1.0-4.0", "high", "731-0"));

int fails = 0;
void Check(string label, bool ok, string detail = "")
{
    Console.WriteLine((ok ? "PASS  " : "FAIL  ") + label + (detail.Length > 0 ? "  ->  " + detail : ""));
    if (!ok) fails++;
}

var all = new[] { h1, h2 }
    .Select(h => JsonSerializer.Deserialize<InterpretationResult>(h.RawJsonResult!)!)
    .SelectMany(r => r.KeyResults!)
    .ToList();

var uni = LoincUnifier.Analyze(all);

// ---------- Regression: unification still works ----------
Check("map remaps 2093-3 -> 14647-2 (anchor wins)",
    uni.CodeMap.TryGetValue("2093-3", out var w) && w == "14647-2",
    string.Join(",", uni.CodeMap.Select(kv => kv.Key + "->" + kv.Value)));
Check("INR unified (unit absent on both reports)",
    uni.CodeMap.TryGetValue("34714-6", out var inr) && inr == "6301-6");
Check("Feritina NOT merged (unit inconsistent)", !uni.CodeMap.ContainsKey("2276-4"));
Check("Feritina flagged unit", uni.MissingAxisFor("Feritina") == "unit", uni.MissingAxisFor("Feritina") ?? "null");
Check("INR no warning", uni.MissingAxisFor("INR") == null);

// ---------- NEW: same code, two units ----------
var scope = LoincUnifier.UnitScope.Build(all, uni.CodeMap);
Check("UnitScope: 3255-7 marked as split", scope.IsSplit("3255-7"));
Check("UnitScope: single-unit codes untouched", !scope.IsSplit("14647-2") && !scope.IsSplit("6301-6"));
Check("UnitScope: g/L and mg/dL get different suffixes",
    scope.Suffix("3255-7", "g/L") != scope.Suffix("3255-7", "mg/dL"),
    scope.Suffix("3255-7", "g/L") + " vs " + scope.Suffix("3255-7", "mg/dL"));
Check("UnitScope: missing unit joins the majority bucket",
    scope.Suffix("3255-7", null) == scope.Suffix("3255-7", "g/L") ||
    scope.Suffix("3255-7", null) == scope.Suffix("3255-7", "mg/dL"),
    scope.Suffix("3255-7", null));
Check("UnitScope: no suffix for a non-split code", scope.Suffix("14647-2", "mg/dL") == "");

// ---------- Compare ----------
var sorted = new List<(InterpretationHistory h, InterpretationResult r)>
{
    (h1, JsonSerializer.Deserialize<InterpretationResult>(h1.RawJsonResult!)!),
    (h2, JsonSerializer.Deserialize<InterpretationResult>(h2.RawJsonResult!)!)
};
var cmp = ProfilesController.BuildComparison(profile, sorted);

var fib = cmp.Rows.Where(r => r.Parameter.Equals("Fibrinogen", StringComparison.OrdinalIgnoreCase)).ToList();
Check("Compare: Fibrinogen on TWO rows (g/L and mg/dL)", fib.Count == 2, "rows=" + fib.Count);
Check("Compare: one row g/L 4.32 with range 2 - 4",
    fib.Any(r => r.Unit == "g/L" && r.ReferenceRange == "2 - 4" && r.Cells.Any(c => c.Value == "4.32")));
Check("Compare: one row mg/dL 516 with range 200 - 400",
    fib.Any(r => r.Unit == "mg/dL" && r.ReferenceRange == "200 - 400" && r.Cells.Any(c => c.Value == "516")));
Check("Compare: both Fibrinogen rows keep code 3255-7",
    fib.All(r => r.LoincCode == "3255-7"), string.Join("/", fib.Select(r => r.LoincCode ?? "null")));
Check("Compare: each Fibrinogen row has exactly ONE real measurement",
    fib.All(r => r.Cells.Count(c => c.CellDirection != "absent") == 1),
    string.Join("/", fib.Select(r => r.Cells.Count(c => c.CellDirection != "absent").ToString())));

var chol = cmp.Rows.Where(r => r.Parameter.ToLower().StartsWith("colesterol")).ToList();
Check("Compare (regression): cholesterol still ONE row", chol.Count == 1, "rows=" + chol.Count);
Check("Compare (regression): cholesterol unified code", chol.Count == 1 && chol[0].LoincCode == "14647-2");
var cInr = cmp.Rows.Where(r => r.Parameter.Equals("INR", StringComparison.OrdinalIgnoreCase)).ToList();
Check("Compare (regression): INR ONE row, 2 cells",
    cInr.Count == 1 && cInr[0].Cells.Count(c => c.CellDirection != "absent") == 2);
var fer = cmp.Rows.Where(r => r.Parameter.Equals("Feritina", StringComparison.OrdinalIgnoreCase)).ToList();
Check("Compare (regression): ferritin 2 rows flagged unit",
    fer.Count == 2 && fer.All(r => r.MissingAxis == "unit"));

// ---------- Dossier ----------
var mi = typeof(ProfilesController).GetMethod("BuildDossier", BindingFlags.NonPublic | BindingFlags.Static)!;
var dossier = (MedicalDossierViewModel)mi.Invoke(null, new object[] { profile, new List<InterpretationHistory> { h1, h2 } })!;
var analytes = dossier.Groups.SelectMany(g => g.Analytes).ToList();

var dFib = analytes.Where(a => a.Parameter.Equals("Fibrinogen", StringComparison.OrdinalIgnoreCase)).ToList();
Check("Dossier: Fibrinogen on TWO cards", dFib.Count == 2, "cards=" + dFib.Count);
Check("Dossier: each Fibrinogen card has ONE entry", dFib.All(a => a.Entries.Count == 1),
    string.Join("/", dFib.Select(a => a.Entries.Count.ToString())));
Check("Dossier: Fibrinogen units are g/L and mg/dL",
    dFib.Select(a => a.Entries[0].Unit).OrderBy(u => u).SequenceEqual(new[] { "g/L", "mg/dL" }.OrderBy(u => u)),
    string.Join("/", dFib.Select(a => a.Entries[0].Unit ?? "null")));
var dChol = analytes.Where(a => a.Parameter.ToLower().StartsWith("colesterol")).ToList();
Check("Dossier (regression): cholesterol ONE card, 2 entries",
    dChol.Count == 1 && dChol[0].Entries.Count == 2, "cards=" + dChol.Count);
var dInr = analytes.Where(a => a.Parameter.Equals("INR", StringComparison.OrdinalIgnoreCase)).ToList();
Check("Dossier (regression): INR ONE card, 2 entries",
    dInr.Count == 1 && dInr[0].Entries.Count == 2, "cards=" + dInr.Count);

Console.WriteLine(fails == 0 ? "\nALL CHECKS PASSED" : $"\n{fails} CHECK(S) FAILED");
return fails == 0 ? 0 : 1;
