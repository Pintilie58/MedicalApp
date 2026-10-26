using System.Diagnostics;
using System.Text.Json;
using MedicalApp.Services;

var config = new ConfigurationBuilder()
    .AddJsonFile("/app/MedicalApp/appsettings.json")
    .AddInMemoryCollection(new Dictionary<string, string?> { ["Gemini:ApiKey"] = Environment.GetEnvironmentVariable("GEMINI_API_KEY") })
    .Build();

var sc = new ServiceCollection();
sc.AddLogging(b => b.AddSimpleConsole().SetMinimumLevel(LogLevel.Warning));
sc.Configure<GeminiSettings>(config.GetSection("Gemini"));
sc.AddHttpClient();
sc.AddSingleton<GeminiRateLimiter>();
sc.AddTransient<GeminiMedicalInterpretationService>();
var sp = sc.BuildServiceProvider();

var pdf = args[0];
var model = args[1];
var outPath = args[2];
var text = PdfTextExtractor.Extract(File.OpenRead(pdf));
var svc = sp.GetRequiredService<GeminiMedicalInterpretationService>();
var sw = Stopwatch.StartNew();
var (result, inTok, outTok, raw) = await svc.InterpretTextAsync(text, Path.GetFileName(pdf), "ro", null, default, model);
sw.Stop();
File.WriteAllText(outPath, raw);
File.WriteAllText(outPath + ".meta.json", JsonSerializer.Serialize(new
{
    pdf = Path.GetFileName(pdf), model, seconds = Math.Round(sw.Elapsed.TotalSeconds, 1),
    input_tokens = inTok, output_tokens = outTok, key_results = result.KeyResults?.Count ?? 0,
    expected = result.Audit?.ExpectedCount ?? 0, text_chars = text.Length
}));
File.WriteAllText(outPath + ".text.txt", text);
Console.WriteLine($"DONE {Path.GetFileName(pdf)} {model} {sw.Elapsed.TotalSeconds:F1}s in={inTok} out={outTok} rows={result.KeyResults?.Count}");
