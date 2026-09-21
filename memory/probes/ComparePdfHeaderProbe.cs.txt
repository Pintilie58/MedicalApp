using MedicalApp.Controllers;
using MedicalApp.Models;
using MedicalApp.Services;
using UglyToad.PdfPig;

// Probe: compare-PDF headers (B2C / CM / CAM) show owner + profile bigger and the website.
int fails = 0;
void Check(string what, bool ok, string? detail = null)
{ Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {what}{(detail is null ? "" : "  ->  " + detail)}"); if (!ok) fails++; }

string Text(byte[] pdf)
{
    using var doc = PdfDocument.Open(pdf);
    return string.Join("\n", doc.GetPages().Select(p => p.Text));
}

InterpretationResult Result(string date, double v) => new()
{
    PatientInfo = new PatientInfo { Name = "Ion", DateTaken = date },
    KeyResults = new List<KeyResult> { new() { Parameter = "Glucoza", Value = v.ToString("0.0"), Unit = "mg/dL", ReferenceRange = "70-100" } }
};
var profile = new Profile { Id = 1, Name = "Maria Pop", UserEmail = "ana@example.com" };
var hist = new List<(InterpretationHistory h, InterpretationResult r)>();
for (int i = 1; i <= 6; i++)
    hist.Add((new InterpretationHistory { Id = i, UserEmail = "ana@example.com", ProfileId = 1, CreatedAt = new DateTime(2026, 1, i), Status = "success", RawJsonResult = "{}" }, Result($"2026-01-0{i}", 80 + i)));
var vm = ProfilesController.BuildComparison(profile, hist);
var gen = new ProfileComparePdfGenerator();

// 1. B2C (Individual) -> "Cont: email · Profil: name" + website
var b2c = Text(gen.Generate(profile, vm, new User { Email = "ana@example.com", UserType = "Individual" }));
Check("1a B2C header has account label + e-mail", (b2c.Contains("Account:") || b2c.Contains("Cont:")) && b2c.Contains("ana@example.com"));
Check("1b B2C header has profile label + name", b2c.Contains("Maria Pop") /* label glyphs use ligatures (ﬁ/ti) in extraction */);
Check("1c website printed", b2c.Contains("WWW.MyMedicalApp.NET"));
Check("1d 6 columns rendered", vm.Columns.Count == 6 && b2c.Contains("2026-01-06"));

// 2. CM (Cabinet) -> cabinet name instead of e-mail
var cm = Text(gen.Generate(profile, vm, new User { Email = "cab@example.com", UserType = "Cabinet", CabinetName = "Cabinet Dr. Ionescu" }));
Check("2a CM header shows cabinet name (not the e-mail)", cm.Contains("Cabinet Dr. Ionescu") && !cm.Contains("cab@example.com"));

// 3. no owner -> only profile line (backward compatible)
var none = Text(gen.Generate(profile, vm));
Check("3a without owner: profile line only", none.Contains("Maria Pop") && !none.Contains("Account:") && !none.Contains("Cont:"));

// 4. CAM compare PDF -> clinic + website
var clinic = new Clinic { Id = 1, Name = "Clinica Demo Test", UserEmail = "c@x.ro" };
var patient = new ClinicPatient { Id = 1, ClinicId = 1, Name = "CIRIP19", NameKey = "cirip19" };
var analyses = new List<ClinicAnalysis>();
for (int i = 1; i <= 6; i++)
    analyses.Add(new ClinicAnalysis { Id = i, ClinicId = 1, PatientId = 1, OriginalFileName = $"f{i}.pdf", RawJsonResult = System.Text.Json.JsonSerializer.Serialize(Result($"2026-02-0{i}", 90 + i)), SamplingDate = new DateTime(2026, 2, i), ProcessedAt = DateTime.UtcNow });
var camPdf = new CamComparePdfGenerator().GenerateIfPossible(clinic, patient, analyses);
Check("4a CAM compare PDF generated", camPdf != null);
if (camPdf != null)
{
    var cam = Text(camPdf);
    Check("4b CAM header shows clinic + website", cam.Contains("Clinica Demo Test") && cam.Contains("WWW.MyMedicalApp.NET"));
    Check("4c 6 columns", cam.Contains("2026-02-06") && cam.Contains("2026-02-01"));
}

Console.WriteLine(fails == 0 ? "ALL CHECKS PASSED" : $"{fails} CHECK(S) FAILED");
return fails == 0 ? 0 : 1;
