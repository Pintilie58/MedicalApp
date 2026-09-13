using System.Text.RegularExpressions;
using MedicalApp.Services;

// Guard for translation coverage: every supported language must carry every EN
// key, with the same placeholders, so Admin -> Translation coverage stays 100%.

int fails = 0;
void Check(string what, bool ok, string? detail = null)
{
    Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {what}{(detail is null ? "" : "  ->  " + detail)}");
    if (!ok) fails++;
}

var all = Loc.AllTranslations;
var en = all["en"];
Console.WriteLine($"      EN dictionary: {en.Count} keys");

foreach (var lang in Loc.SupportedLanguages)
{
    if (lang == "en") continue;

    Check($"{lang}: has its own dictionary", all.ContainsKey(lang));
    if (!all.TryGetValue(lang, out var dict)) continue;

    var missing = en.Keys.Where(k => !dict.ContainsKey(k)).OrderBy(k => k).ToList();
    Check($"{lang}: no missing key (coverage 100%)", missing.Count == 0,
        missing.Count == 0 ? $"{dict.Count}/{en.Count}" : string.Join(", ", missing.Take(8)));

    var extra = dict.Keys.Where(k => !en.ContainsKey(k)).OrderBy(k => k).ToList();
    Check($"{lang}: no drifted key that EN does not have", extra.Count == 0,
        string.Join(", ", extra.Take(8)));

    var blank = dict.Where(kv => string.IsNullOrWhiteSpace(kv.Value)).Select(kv => kv.Key).ToList();
    Check($"{lang}: no empty translation", blank.Count == 0, string.Join(", ", blank.Take(8)));

    // A missing {0} would crash string.Format at runtime.
    static IEnumerable<string> Slots(string s) =>
        Regex.Matches(s, @"\{\d+\}").Select(m => m.Value).Distinct().OrderBy(x => x);
    var badSlots = en.Keys
        .Where(k => dict.ContainsKey(k) && !Slots(en[k]).SequenceEqual(Slots(dict[k])))
        .OrderBy(k => k).ToList();
    Check($"{lang}: placeholders match EN everywhere", badSlots.Count == 0,
        string.Join(", ", badSlots.Take(8)));

    var untranslated = en.Keys.Count(k => dict.ContainsKey(k) && dict[k] == en[k]);
    Console.WriteLine($"      {lang}: {dict.Count} keys, {untranslated} identical to EN (names, codes, symbols)");
}

Console.WriteLine(fails == 0 ? "\nALL CHECKS PASSED" : $"\n{fails} CHECK(S) FAILED");
return fails == 0 ? 0 : 1;
