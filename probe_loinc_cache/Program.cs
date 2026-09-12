using System.Net;
using System.Text;
using System.Text.Json;
using MedicalApp.Data;
using MedicalApp.Models;
using MedicalApp.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

// Probe for the persistent, GLOBAL LOINC match cache and its STABLE key.
// The Python matcher is replaced by a fake handler that COUNTS calls, so we can
// prove the cache removes work without ever reusing a mapping it should not.
//
// The key is built from: printed analyte name (native language) + unit +
// specimen/method markers found in the PDF context (vocabulary fetched from the
// matcher) + pipeline version. Gemini's English normalization is deliberately
// NOT part of it, because the model rephrases it between runs.

int fails = 0;
void Check(string label, bool ok, string detail = "")
{
    Console.WriteLine((ok ? "PASS  " : "FAIL  ") + label + (detail.Length > 0 ? "  ->  " + detail : ""));
    if (!ok) fails++;
}

static InterpretationResult Report(params (string printed, string en, string? unit, string? panel, string? line)[] a) =>
    new()
    {
        IsMedicalAnalysis = true,
        KeyResults = a.Select(x => new KeyResult
        {
            Parameter = x.printed,
            ParameterNormalizedEn = x.en,
            Unit = x.unit,
            PanelHeaderRaw = x.panel,
            AnalyteLineRaw = x.line,
            Value = "1",
            Status = "normal"
        }).ToList()
    };

static (LoincMatcherClient client, FakeMatcher fake, AppDbContext db, IServiceProvider sp) Build(
    string dbName, bool cacheEnabled = true, string version = "v2", double minScore = 0.55,
    bool vocabularyAvailable = true)
{
    var fake = new FakeMatcher { NoVocabulary = !vocabularyAvailable };

    var services = new ServiceCollection();
    services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
    services.AddDbContext<AppDbContext>(o => o.UseInMemoryDatabase(dbName));
    services.AddHttpClient(string.Empty).ConfigurePrimaryHttpMessageHandler(() => fake);
    services.Configure<LoincMatcherSettings>(s =>
    {
        s.BaseUrl = "http://localhost:8000";
        s.Enabled = true;
        s.MinScore = minScore;
        s.TimeoutSeconds = 5;
        s.Cache = new LoincMatchCacheSettings { Enabled = cacheEnabled, PipelineVersion = version };
    });
    services.AddSingleton<LoincContextVocabulary>();
    services.AddSingleton<LoincMatchCacheStore>();
    var sp = services.BuildServiceProvider();

    var http = new HttpClient(fake, disposeHandler: false) { BaseAddress = new Uri("http://localhost:8000") };
    var client = new LoincMatcherClient(
        http,
        sp.GetRequiredService<IOptions<LoincMatcherSettings>>(),
        sp.GetRequiredService<LoincMatchCacheStore>(),
        sp.GetRequiredService<LoincContextVocabulary>(),
        sp.GetRequiredService<ILogger<LoincMatcherClient>>());

    return (client, fake, sp.GetRequiredService<AppDbContext>(), sp);
}

const string DbA = "cache-a";
const string RoPanel = "Hemoleucograma completa - Sange - Spectroscopie de impedanta";

// ================================================================ 1. cold run
var (client, fake, db, sp) = Build(DbA);
var r1 = Report(("Glicemie", "Glucose [Mass/volume] in Serum or Plasma", "mg/dL", "Biochimie - Ser", null),
                ("Hemoglobina", "Hemoglobin [Mass/volume] in Blood by Automated count", "g/dL", RoPanel, null),
                ("Colesterol total", "Cholesterol [Mass/volume] in Serum or Plasma", "mg/dL", "Biochimie - Ser", null));
var s1 = await client.MatchAllAsync(r1);
var codes1 = r1.KeyResults!.Select(k => k.LoincCode).ToList();

Check("1. cold run: one call for all 3 analytes",
    fake.BatchCalls == 1 && fake.AnalytesSeen == 3, $"calls={fake.BatchCalls} seen={fake.AnalytesSeen}");
Check("1b. all 3 got a code", s1.Matched == 3 && codes1.All(c => !string.IsNullOrEmpty(c)));
Check("1c. 3 mappings remembered", await db.LoincMatchCache.CountAsync() == 3);
Check("1d. the vocabulary was fetched once", fake.VocabularyCalls == 1, fake.VocabularyCalls.ToString());
var materials = await db.LoincMatchCache.Select(e => e.KeyMaterial).ToListAsync();
Check("1e. KeyMaterial is stored and shows the decisive markers",
    materials.All(m => !string.IsNullOrWhiteSpace(m)) && materials.Any(m => m!.Contains("impedanta")),
    materials.FirstOrDefault(m => m!.Contains("impedanta")) ?? "(none)");

// ================================================================ 2. the reported bug: Gemini rephrases
var (client2, fake2, _, _) = Build(DbA);
var r2 = Report(("Glicemie", "Glucose [Mass/volume] in Serum or Plasma by Automated count", "mg/dL", "Biochimie - Ser", null),
                ("Hemoglobina", "Hemoglobin [Mass/volume] in Blood", "g/dL", RoPanel, null),
                ("Colesterol total", "Cholesterol [Moles/volume] in Serum or Plasma immunoassay", "mg/dL", "Biochimie - Ser", null));
var s2 = await client2.MatchAllAsync(r2);

Check("2. Gemini rephrasing the English term => still a cache HIT",
    fake2.BatchCalls == 0 && s2.FromCache == 3, $"calls={fake2.BatchCalls} cache={s2.FromCache}");
Check("2b. the codes are identical to the first run",
    r2.KeyResults!.Select(k => k.LoincCode).SequenceEqual(codes1),
    string.Join(",", r2.KeyResults!.Select(k => k.LoincCode)));
Check("2c. no duplicate rows were created", await db.LoincMatchCache.CountAsync() == 3);
Check("2d. long name, class, source, score and axis verdict survived",
    r2.KeyResults!.All(k => !string.IsNullOrEmpty(k.LoincLongName) && k.LoincClass == "CHEM"
        && k.LoincSource == "semantic" && k.LoincScore == 0.92
        && k.LoincAxisVerdict != null && k.LoincAxisVerdict!.ContainsKey("component")));

// ================================================================ 3. a real difference must NOT be reused
async Task<bool> IsMiss(string db_, string printed, string? unit, string? panel, string? line,
                        string printedSeed, string? unitSeed, string? panelSeed, string? lineSeed)
{
    var (c1, f1, _, _) = Build(db_);
    await c1.MatchAllAsync(Report((printedSeed, "Seed analyte", unitSeed, panelSeed, lineSeed)));
    var (c2, f2, _, _) = Build(db_);
    await c2.MatchAllAsync(Report((printed, "Seed analyte", unit, panel, line)));
    return f2.BatchCalls == 1;   // it had to ask the matcher again
}

Check("3. RO: impedanta vs citometrie in flux => MISS",
    await IsMiss("m-ro", "Limfocite", "%", "Hemoleucograma - Sange - Citometrie in flux", null,
                          "Limfocite", "%", "Hemoleucograma - Sange - Spectroscopie de impedanta", null));
Check("3b. FR: cytometrie en flux vs impedance => MISS",
    await IsMiss("m-fr", "Lymphocytes", "%", "Hemogramme - Sang - Impédance", null,
                          "Lymphocytes", "%", "Hemogramme - Sang - Cytométrie en flux", null));
Check("3c. DE: Durchflusszytometrie vs Impedanz => MISS",
    await IsMiss("m-de", "Lymphozyten", "%", "Blutbild - Blut - Impedanz", null,
                          "Lymphozyten", "%", "Blutbild - Blut - Durchflusszytometrie", null));
Check("3d. PL: mikroskopia vs automat => MISS",
    await IsMiss("m-pl", "Limfocyty", "%", "Morfologia - krew - automat", null,
                          "Limfocyty", "%", "Morfologia - krew - mikroskopia", null));
Check("3e. RO: specimen ser vs urina => MISS",
    await IsMiss("m-sp", "Glucoza", "mg/dL", "Sumar de urina", null,
                          "Glucoza", "mg/dL", "Biochimie - ser", null));
Check("3f. FR: serum vs urine => MISS",
    await IsMiss("m-sp2", "Glucose", "mg/dL", "Analyse d'urine", null,
                           "Glucose", "mg/dL", "Biochimie - sérum", null));
Check("3g. different unit => MISS",
    await IsMiss("m-unit", "Glicemie", "mmol/L", "Biochimie - ser", null,
                            "Glicemie", "mg/dL", "Biochimie - ser", null));
Check("3h. different printed name (another language) => MISS, no cross-language reuse",
    await IsMiss("m-lang", "Glycémie", "mg/dL", "Biochimie - sérum", null,
                            "Glicemie", "mg/dL", "Biochimie - ser", null));
Check("3i. ESR: Westergren vs microfotometric => MISS",
    await IsMiss("m-esr", "VSH", "mm/h", "Hematologie - Sange - Microfotometric capilar", null,
                           "VSH", "mm/h", "Hematologie - Sange - Westergren", null));

// ================================================================ 4. irrelevant differences must be reused
async Task<bool> IsHit(string db_, string printed, string? unit, string? panel, string? line,
                       string printedSeed, string? unitSeed, string? panelSeed, string? lineSeed)
{
    var (c1, _, _, _) = Build(db_);
    await c1.MatchAllAsync(Report((printedSeed, "Seed analyte", unitSeed, panelSeed, lineSeed)));
    var (c2, f2, _, _) = Build(db_);
    var st = await c2.MatchAllAsync(Report((printed, "Another wording entirely", unit, panel, line)));
    return f2.BatchCalls == 0 && st.FromCache == 1;
}

Check("4. diacritics: impedanță == impedanta => HIT",
    await IsHit("h-dia", "Limfocite", "%", "Hemoleucograma - Sânge - Spectroscopie de impedanță", null,
                          "Limfocite", "%", "Hemoleucograma - Sange - Spectroscopie de impedanta", null));
Check("4b. FR diacritics: cytométrie == cytometrie => HIT",
    await IsHit("h-dia2", "Lymphocytes", "%", "Hemogramme - Sang - Cytometrie en flux", null,
                           "Lymphocytes", "%", "Hémogramme - Sang - Cytométrie en flux", null));
Check("4c. reworded context with the same markers => HIT",
    await IsHit("h-word", "Limfocite", "%", "HLG completa (sange integral) — analizor cu impedanta, seria 7734", null,
                           "Limfocite", "%", "Hemoleucograma - Sange - Spectroscopie de impedanta", null));
Check("4d. reference-range noise in the analyte line => HIT",
    await IsHit("h-noise", "Limfocite", "%", "Hemoleucograma - Sange - impedanta", "Limfocite 24.1 % 20-40 (adulti)",
                            "Limfocite", "%", "Hemoleucograma - Sange - impedanta", "Limfocite 24.1 % 20 - 40"));
Check("4e. unit spelling: 10^3/µL == 10^3/uL => HIT",
    await IsHit("h-unit", "Leucocite", "10^3/uL", "Hemoleucograma - Sange - impedanta", null,
                           "Leucocite", "10^3/µL", "Hemoleucograma - Sange - impedanta", null));
Check("4f. printed name case/spacing => HIT",
    await IsHit("h-name", "  LIMFOCITE ", "%", "Hemoleucograma - Sange - impedanta", null,
                           "Limfocite", "%", "Hemoleucograma - Sange - impedanta", null));

// ================================================================ 5. vocabulary unavailable => conservative
var (clientV1, fakeV1, dbV, _) = Build("cache-v", vocabularyAvailable: false);
await clientV1.MatchAllAsync(Report(("Limfocite", "Lymphocytes", "%", "Hemoleucograma - Sange - impedanta", null)));
Check("5. without the vocabulary the analyte is still matched and remembered",
    fakeV1.BatchCalls == 1 && await dbV.LoincMatchCache.CountAsync() == 1);
var (clientV2, fakeV2, _, _) = Build("cache-v", vocabularyAvailable: false);
var sV2 = await clientV2.MatchAllAsync(Report(("Limfocite", "Lymphocytes", "%", "Hemoleucograma - Sange - impedanta", null)));
Check("5b. identical context still hits without the vocabulary",
    fakeV2.BatchCalls == 0 && sV2.FromCache == 1, $"calls={fakeV2.BatchCalls} cache={sV2.FromCache}");
var (clientV3, fakeV3, _, _) = Build("cache-v", vocabularyAvailable: false);
await clientV3.MatchAllAsync(Report(("Limfocite", "Lymphocytes", "%", "HLG (sange integral) - analizor cu impedanta", null)));
Check("5c. without the vocabulary a reworded context is a MISS (prudent, never a wrong reuse)",
    fakeV3.BatchCalls == 1, fakeV3.BatchCalls.ToString());
var conservative = await dbV.LoincMatchCache.Select(e => e.KeyMaterial).ToListAsync();
Check("5d. KeyMaterial marks the conservative mode",
    conservative.Any(m => m!.Contains("full:")), conservative.FirstOrDefault() ?? "(none)");

// ================================================================ 6. graceful degradation
var (client4, fake4, _, _) = Build(DbA);
fake4.Down = true;
var r4 = Report(("Glicemie", "Glucose [Mass/volume] in Serum or Plasma", "mg/dL", "Biochimie - Ser", null),
                ("Hemoglobina", "Hemoglobin [Mass/volume] in Blood", "g/dL", RoPanel, null));
var s4 = await client4.MatchAllAsync(r4);
Check("6. Python service DOWN + known analytes => still coded",
    s4.Matched == 2 && r4.KeyResults!.All(k => !string.IsNullOrEmpty(k.LoincCode)), $"matched={s4.Matched}");
Check("6b. and with the same codes",
    r4.KeyResults!.Select(k => k.LoincCode).SequenceEqual(codes1.Take(2)));

var (client5, fake5, _, _) = Build(DbA);
fake5.Down = true;
var r5 = Report(("Analiza noua", "Brand new analyte", "u", null, null));
var s5 = await client5.MatchAllAsync(r5);
Check("6c. service down + unknown analyte => no code, no crash",
    s5.Matched == 0 && string.IsNullOrEmpty(r5.KeyResults![0].LoincCode));

// ================================================================ 7. kill switch and versioning
var (client6, fake6, _, _) = Build(DbA, cacheEnabled: false);
var s6 = await client6.MatchAllAsync(
    Report(("Glicemie", "Glucose [Mass/volume] in Serum or Plasma", "mg/dL", "Biochimie - Ser", null)));
Check("7. cache disabled => every analyte goes to the matcher, as before",
    fake6.BatchCalls == 1 && s6.FromCache == 0 && s6.Matched == 1,
    $"calls={fake6.BatchCalls} cache={s6.FromCache}");

var (client7, fake7, _, _) = Build(DbA, version: "v3");
var s7 = await client7.MatchAllAsync(
    Report(("Glicemie", "Glucose [Mass/volume] in Serum or Plasma", "mg/dL", "Biochimie - Ser", null)));
Check("7b. bumping PipelineVersion invalidates the old rows",
    fake7.BatchCalls == 1 && s7.FromCache == 0);
var (client8, fake8, _, _) = Build(DbA, version: "v3");
var s8 = await client8.MatchAllAsync(
    Report(("Glicemie", "Glucose [Mass/volume] in Serum or Plasma", "mg/dL", "Biochimie - Ser", null)));
Check("7c. the new version then caches normally", fake8.BatchCalls == 0 && s8.FromCache == 1);

// ================================================================ 8. threshold, fallback, edge cases
var (clientB, fakeB, dbB, _) = Build("cache-b", minScore: 0.90);
fakeB.Score = 0.40;
var rB = Report(("Ceva", "Low score analyte", "u", null, null));
var sB = await clientB.MatchAllAsync(rB);
Check("8. below-threshold match is discarded",
    sB.BelowThreshold == 1 && string.IsNullOrEmpty(rB.KeyResults![0].LoincCode));
Check("8b. but remembered, so it is not recomputed", await dbB.LoincMatchCache.CountAsync() == 1);
var (clientC, fakeC, _, _) = Build("cache-b", minScore: 0.90);
var sC = await clientC.MatchAllAsync(Report(("Ceva", "Low score analyte", "u", null, null)));
Check("8c. the remembered low score is applied the same way (no call, still discarded)",
    fakeC.BatchCalls == 0 && sC.BelowThreshold == 1 && sC.FromCache == 1);

var (clientD, fakeD, dbD, _) = Build("cache-c");
fakeD.NoBatchEndpoint = true;
var sD = await clientD.MatchAllAsync(Report(("A", "Analyte A", "u", null, null), ("B", "Analyte B", "u", null, null)));
Check("8d. older Python service (no batch endpoint) => per-analyte path works",
    sD.Matched == 2 && fakeD.SingleCalls == 2, $"matched={sD.Matched} single={fakeD.SingleCalls}");
Check("8e. the sequential path also fills the cache", await dbD.LoincMatchCache.CountAsync() == 2);
var (clientE, fakeE, _, _) = Build("cache-c");
var sE = await clientE.MatchAllAsync(Report(("A", "Analyte A", "u", null, null), ("B", "Analyte B", "u", null, null)));
Check("8f. next time nothing is called at all",
    fakeE.SingleCalls == 0 && fakeE.BatchCalls == 0 && sE.FromCache == 2);

var (clientF, fakeF, _, _) = Build("cache-d");
var sF = await clientF.MatchAllAsync(Report(("Fara termen", "", "u", null, null), ("Bun", "Good analyte", "u", null, null)));
Check("9. analytes without an English term are skipped, not cached",
    sF.NoNormalizedTerm == 1 && fakeF.AnalytesSeen == 1, $"skip={sF.NoNormalizedTerm} seen={fakeF.AnalytesSeen}");

// Printed name missing (extractor gave nothing) => the English term is the identity.
var (clientG, fakeG, dbG, _) = Build("cache-e");
await clientG.MatchAllAsync(Report(("", "Ferritin [Mass/volume] in Serum or Plasma", "ng/mL", null, null)));
var (clientH, fakeH, _, _) = Build("cache-e");
var sH = await clientH.MatchAllAsync(Report(("", "Ferritin [Mass/volume] in Serum or Plasma", "ng/mL", null, null)));
Check("9b. with no printed name the English term is used as identity (still cached)",
    await dbG.LoincMatchCache.CountAsync() == 1 && fakeH.BatchCalls == 0 && sH.FromCache == 1,
    $"rows={await dbG.LoincMatchCache.CountAsync()} calls={fakeH.BatchCalls}");

// The vocabulary is fetched once per process, not once per report.
var (clientI, fakeI, _, _) = Build("cache-f");
await clientI.MatchAllAsync(Report(("X", "Analyte X", "u", "ser", null)));
await clientI.MatchAllAsync(Report(("Y", "Analyte Y", "u", "ser", null)));
await clientI.MatchAllAsync(Report(("Z", "Analyte Z", "u", "ser", null)));
Check("10. the vocabulary is fetched ONCE, not per report",
    fakeI.VocabularyCalls == 1, fakeI.VocabularyCalls.ToString());

// Two analytes in the same report that differ only by printed method.
var (clientJ, fakeJ, dbJ, _) = Build("cache-g");
await clientJ.MatchAllAsync(Report(
    ("Limfocite", "Lymphocytes", "%", "Hemoleucograma - Sange - impedanta", null),
    ("Limfocite", "Lymphocytes", "%", "Hemoleucograma - Sange - citometrie in flux", null)));
Check("10b. same name, two printed methods in one report => two separate entries",
    await dbJ.LoincMatchCache.CountAsync() == 2, (await dbJ.LoincMatchCache.CountAsync()).ToString());

// ================================================================ 11. key internals
var store = sp.GetRequiredService<LoincMatchCacheStore>();
var vocab = await sp.GetRequiredService<LoincContextVocabulary>().GetAsync();
Check("11. the vocabulary snapshot is available", vocab.Available, vocab.Phrases.Count.ToString());
var kA = store.BuildKey(vocab, "Limfocite", "%", "Lymphocytes", "Hemoleucograma - Sange - impedanta", null);
var kB = store.BuildKey(vocab, "  limfocite ", "%", "Totally different wording", "HLG sange impedanta", null);
var kC = store.BuildKey(vocab, "Limfocite", "%", "Lymphocytes", "Hemoleucograma - Sange - citometrie in flux", null);
Check("11b. key is stable across wording, case and spacing", kA.Key == kB.Key);
Check("11c. key changes with the printed method", kA.Key != kC.Key);
Check("11d. key is a 64-char hex digest", kA.Key.Length == 64 && kA.Key.All(Uri.IsHexDigit));
Check("11e. material is human-readable and versioned",
    kA.Material.Contains("v2") && kA.Material.Contains("limfocite"), kA.Material.Replace('\u001f', '|'));
Check("11f. Normalize strips diacritics like the Python side does",
    LoincContextVocabulary.Normalize(" Impedanță  ȘI  Sérique ") == "impedanta si serique",
    LoincContextVocabulary.Normalize(" Impedanță  ȘI  Sérique "));

var vocabSvc = sp.GetRequiredService<LoincContextVocabulary>();
Check("11g. a short marker is not found inside another word ('ser' in 'seria')",
    !vocabSvc.DecisiveTokens(vocab, "Analizor seria 7734, natura probei necunoscuta").Contains("ser"),
    vocabSvc.DecisiveTokens(vocab, "Analizor seria 7734, natura probei necunoscuta"));
Check("11h. but a real short marker on its own is found ('ser')",
    vocabSvc.DecisiveTokens(vocab, "Biochimie - ser").Contains("ser"),
    vocabSvc.DecisiveTokens(vocab, "Biochimie - ser"));
Check("11i. native inflections count when the matcher sees them too ('sangele', 'urinar')",
    vocabSvc.DecisiveTokens(vocab, "Determinare prin impedanta in sangele integral").Contains("impedanta")
    && vocabSvc.DecisiveTokens(vocab, "Determinare prin impedanta in sangele integral").Contains("sange")
    && vocabSvc.DecisiveTokens(vocab, "Sediment urinar").Contains("urina"),
    vocabSvc.DecisiveTokens(vocab, "Determinare prin impedanta in sangele integral"));

// The vocabulary must survive a Python outage that starts BEFORE the app does.
var (clientK, fakeK, _, spK) = Build(DbA);
fakeK.Down = true;
var vocabK = await spK.GetRequiredService<LoincContextVocabulary>().GetAsync();
Check("12. Python down from the start => vocabulary restored from the database",
    vocabK.Available && fakeK.VocabularyCalls == 1,
    $"available={vocabK.Available} calls={fakeK.VocabularyCalls}");

var (clientL, fakeL, _, _) = Build("cache-empty");
fakeL.Down = true;
var rL = Report(("Ceva nou", "Fresh analyte", "u", "ser", null));
var sL = await clientL.MatchAllAsync(rL);
Check("12b. no vocabulary anywhere + service down => prudent, no wrong reuse, no crash",
    sL.Matched == 0 && sL.FromCache == 0);

Console.WriteLine(fails == 0 ? "\nALL CHECKS PASSED" : $"\n{fails} CHECK(S) FAILED");
return fails == 0 ? 0 : 1;

// ---------------------------------------------------------------- fake matcher
class FakeMatcher : HttpMessageHandler
{
    public int BatchCalls;
    public int SingleCalls;
    public int VocabularyCalls;
    public int AnalytesSeen;
    public bool Down;
    public bool NoBatchEndpoint;
    public bool NoVocabulary;
    public double Score = 0.92;

    // A realistic slice of the multilingual vocabulary the real service exports.
    static readonly string[] Phrases =
    {
        "serum", "ser", "serica", "serique", "plasma", "sange", "sang", "sangre", "blood", "blut",
        "krew", "urina", "urine", "urin", "orina", "mocz", "saliva", "csf", "lcr",
        "automated", "automat", "impedance", "impedanta", "impedancia", "impedanz",
        "flow cytometry", "cytometry", "citometrie", "citometria", "cytometrie en flux",
        "durchflusszytometrie", "microscopie", "microscopia", "mikroskopie", "mikroskopia",
        "manual", "westergren", "fotometric", "photometric", "turbidimetrie", "turbidimetry",
        "nefelometrie", "eclia", "elisa", "hplc", "coagulometrie", "calculat", "estimat"
    };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var path = request.RequestUri!.AbsolutePath;

        if (path.EndsWith("context-keywords"))
        {
            VocabularyCalls++;
            if (NoVocabulary || Down) return new HttpResponseMessage(HttpStatusCode.NotFound);
            return Json(JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["count"] = Phrases.Length,
                ["phrases"] = Phrases
            }));
        }

        if (path.EndsWith("/ready")) return new HttpResponseMessage(HttpStatusCode.OK);
        if (Down) throw new HttpRequestException("LOINC service is down");

        var body = await request.Content!.ReadAsStringAsync(ct);

        if (path.EndsWith("match-batch"))
        {
            BatchCalls++;
            if (NoBatchEndpoint) return new HttpResponseMessage(HttpStatusCode.NotFound);

            var items = JsonSerializer.Deserialize<List<Dictionary<string, JsonElement>>>(body)!;
            AnalytesSeen += items.Count;
            var arr = items.Select(i => Answer(i["test_name"].GetString() ?? ""));
            return Json("[" + string.Join(",", arr) + "]");
        }

        SingleCalls++;
        var one = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(body)!;
        AnalytesSeen++;
        return Json(Answer(one["test_name"].GetString() ?? ""));
    }

    string Answer(string name) => JsonSerializer.Serialize(new Dictionary<string, object?>
    {
        ["loinc"] = "CODE-" + Math.Abs(name.GetHashCode()) % 10000,
        ["name"] = "Official name of " + name,
        ["score"] = Score,
        ["loinc_class"] = "CHEM",
        ["loinc_source"] = "semantic",
        ["axis_verdict"] = new Dictionary<string, string> { ["component"] = "ok" }
    });

    static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };
}
