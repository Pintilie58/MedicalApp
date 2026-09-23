using MedicalApp.Services;
using System.Reflection;

Console.WriteLine("--- Loc.T(\"StripeLineItemCredits\", lang) for every shipped language ---");
foreach (var code in SupportedLanguagesConfig.Codes)
{
    var template = Loc.T("StripeLineItemCredits", code);
    var rendered = string.Format(template, 25);
    var missing = template == "StripeLineItemCredits";
    Console.WriteLine($"{(missing ? "FAIL" : "PASS")}  {code,-3} -> \"{rendered}\"");
}

Console.WriteLine("\n--- StripeLocale(uiLanguage) mapping ---");
var m = typeof(StripePaymentService).GetMethod("StripeLocale",
    BindingFlags.NonPublic | BindingFlags.Static)!;
foreach (var input in new string?[] { "en", "ro", "es", "de", "it", "pt", "fr", "es-ES", "zz", "", null })
    Console.WriteLine($"  {(input is null ? "null" : $"\"{input}\""),-8} -> \"{m.Invoke(null, new object?[] { input })}\"");
