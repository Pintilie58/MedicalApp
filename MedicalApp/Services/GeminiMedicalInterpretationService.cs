using MedicalApp.Models;
using Microsoft.Extensions.Options;
using System.Text;
using System.Text.Json;

namespace MedicalApp.Services
{
    /// <summary>
    /// Interpretation provider backed by Google Gemini 2.5 Flash via the public REST API.
    /// Gemini natively accepts PDFs (no text extraction needed) which dramatically
    /// improves accuracy on complex lab reports (multi-column tables, age-dependent
    /// reference ranges, dual-unit rows, scanned PDFs, etc.).
    ///
    /// Endpoint:
    ///   POST https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent?key={apiKey}
    ///
    /// Cost reference (gemini-2.5-flash, Feb 2026, paid tier):
    ///   ~$0.30 / 1M input tokens, ~$2.50 / 1M output tokens
    ///   (free tier covers typical MedicalApp volumes).
    /// </summary>
    public class GeminiMedicalInterpretationService : IMedicalInterpretationProvider
    {
        private readonly GeminiSettings _settings;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger<GeminiMedicalInterpretationService> _logger;

        private const string EndpointFormat =
            "https://generativelanguage.googleapis.com/v1beta/models/{0}:generateContent?key={1}";

        private static readonly Dictionary<string, string> LanguageNames = new()
        {
            ["en"] = "English",
            ["ro"] = "Romanian (Română)",
            ["fr"] = "French (Français)",
            ["es"] = "Spanish (Español)",
            ["de"] = "German (Deutsch)"
        };

        public GeminiMedicalInterpretationService(
            IOptions<GeminiSettings> options,
            IHttpClientFactory httpClientFactory,
            ILogger<GeminiMedicalInterpretationService> logger)
        {
            _settings = options.Value;
            _httpClientFactory = httpClientFactory;
            _logger = logger;
        }

        public async Task<(InterpretationResult Result, int InputTokens, int OutputTokens, string RawResponse)> InterpretPdfAsync(
            Stream pdfStream, string fileName, string languageCode, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(_settings.ApiKey))
                throw new InvalidOperationException(
                    "Gemini API key is not configured. Run: dotnet user-secrets set \"Gemini:ApiKey\" \"<your-key>\"");

            var languageName = LanguageNames.TryGetValue(languageCode, out var n) ? n : "English";

            // 1) Read the PDF into memory and Base64-encode it for inline_data
            byte[] pdfBytes;
            using (var ms = new MemoryStream())
            {
                await pdfStream.CopyToAsync(ms, ct);
                pdfBytes = ms.ToArray();
            }
            var pdfBase64 = Convert.ToBase64String(pdfBytes);

            // 2) Build the request body
            var systemPrompt = BuildSystemPrompt();
            var userPrompt = BuildUserPrompt(languageName, languageCode, fileName);
            var requestBody = BuildRequestBody(systemPrompt, userPrompt, pdfBase64);

            var url = string.Format(EndpointFormat, _settings.Model, _settings.ApiKey);

            // 3) Send request
            using var http = _httpClientFactory.CreateClient();
            http.Timeout = TimeSpan.FromSeconds(_settings.TimeoutSeconds);

            using var content = new StringContent(requestBody, Encoding.UTF8, "application/json");

            _logger.LogInformation(
                "Calling Gemini {Model} for {Language}. PDF bytes: {Bytes}, base64 length: {B64}",
                _settings.Model, languageCode, pdfBytes.Length, pdfBase64.Length);

            using var response = await http.PostAsync(url, content, ct);
            var responseString = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("Gemini returned {Status}. Body: {Body}",
                    (int)response.StatusCode, Truncate(responseString, 2000));
                throw new InvalidOperationException(
                    $"Gemini API error {(int)response.StatusCode}: {Truncate(responseString, 500)}");
            }

            // 4) Parse the wrapper to extract the JSON the model produced
            string modelText;
            int inputTokens = 0, outputTokens = 0;
            try
            {
                using var doc = JsonDocument.Parse(responseString);
                var root = doc.RootElement;

                // candidates[0].content.parts[0].text  -> the JSON string we asked the model to produce
                modelText = root
                    .GetProperty("candidates")[0]
                    .GetProperty("content")
                    .GetProperty("parts")[0]
                    .GetProperty("text")
                    .GetString() ?? string.Empty;

                if (root.TryGetProperty("usageMetadata", out var usage))
                {
                    if (usage.TryGetProperty("promptTokenCount", out var pt))
                        inputTokens = pt.GetInt32();
                    if (usage.TryGetProperty("candidatesTokenCount", out var ct2))
                        outputTokens = ct2.GetInt32();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Could not parse Gemini wrapper response. Body: {Body}",
                    Truncate(responseString, 2000));
                throw new InvalidOperationException("Gemini returned an unrecognized response shape.", ex);
            }

            _logger.LogInformation("Gemini response received. Tokens in={In} out={Out}", inputTokens, outputTokens);

            // 5) Strip any accidental markdown fences and parse the structured JSON
            var cleaned = StripMarkdownFences(modelText);

            InterpretationResult? result;
            try
            {
                result = JsonSerializer.Deserialize<InterpretationResult>(cleaned, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });
            }
            catch (JsonException ex)
            {
                _logger.LogError(ex, "Failed to parse Gemini structured JSON. Cleaned: {Body}",
                    Truncate(cleaned, 2000));
                throw new InvalidOperationException("The AI returned an unparseable response. Please try again.", ex);
            }

            if (result == null)
                throw new InvalidOperationException("The AI returned an empty response.");

            return (result, inputTokens, outputTokens, cleaned);
        }

        /// <summary>Not implemented for Gemini provider - use InterpretPdfAsync instead.</summary>
        public Task<(InterpretationResult Result, int InputTokens, int OutputTokens, string RawResponse)> InterpretAsync(
            string extractedText, string languageCode, CancellationToken ct = default)
            => throw new NotSupportedException(
                "GeminiMedicalInterpretationService does not accept pre-extracted text. Use InterpretPdfAsync(stream).");

        // =========================================================================
        // Request body construction
        // =========================================================================
        private string BuildRequestBody(string systemPrompt, string userPrompt, string pdfBase64)
        {
            // We rely on responseMimeType=application/json (Gemini's "JSON mode") plus a
            // detailed schema described in the prompt. Gemini also supports responseSchema,
            // but mixing it with PDF inline_data is brittle - JSON mode + a strict prompt
            // is sufficient and more flexible.
            var payload = new
            {
                systemInstruction = new
                {
                    parts = new[] { new { text = systemPrompt } }
                },
                contents = new[]
                {
                    new
                    {
                        role = "user",
                        parts = new object[]
                        {
                            new { text = userPrompt },
                            new
                            {
                                inline_data = new
                                {
                                    mime_type = "application/pdf",
                                    data = pdfBase64
                                }
                            }
                        }
                    }
                },
                generationConfig = new
                {
                    temperature = _settings.Temperature,
                    maxOutputTokens = _settings.MaxOutputTokens,
                    responseMimeType = "application/json"
                }
            };

            return JsonSerializer.Serialize(payload);
        }

        // =========================================================================
        // Helpers
        // =========================================================================
        private static string Truncate(string s, int max)
            => string.IsNullOrEmpty(s) ? string.Empty : (s.Length <= max ? s : s[..max] + "...");

        private static string StripMarkdownFences(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return s;
            var t = s.Trim();
            if (t.StartsWith("```"))
            {
                // remove the opening fence (```json\n or ```\n)
                var firstNewline = t.IndexOf('\n');
                if (firstNewline > 0) t = t[(firstNewline + 1)..];
                if (t.EndsWith("```")) t = t[..^3];
            }
            return t.Trim();
        }

        // =========================================================================
        // PROMPTS - adapted from the OpenAI version, with Gemini-specific reminders
        // =========================================================================
        private static string BuildSystemPrompt() => @"You are MedicalApp's medical analysis interpretation assistant. Your role is to provide an EDUCATIONAL and INFORMATIVE interpretation of laboratory medical results extracted DIRECTLY from a PDF file you can read natively.

STRICT RULES - you MUST follow ALL of them:
1. Respond ENTIRELY in the language specified by the user in their message.
2. You are NOT a doctor. You do NOT give medical diagnoses.
3. You do NOT recommend specific medications, doses, or treatments.
4. You ALWAYS recommend consulting a qualified physician.
5. You do NOT invent values - if a parameter is missing from the PDF, do NOT include it.
6. Use an empathetic, professional, CALM tone - never alarming.
7. Use SIMPLE language, accessible to a non-medical reader.
8. Provide a DETAILED interpretation (not a short one) - include thorough explanations.

==========================================================
PDF READING - YOU SEE THE PDF VISUALLY
==========================================================
You receive the original PDF as native input. Read it visually as a human radiographer would:
- Respect tabular layouts: each row is one parameter, columns are usually [Parameter | Value | Unit | Reference range | Previous value].
- For parameters that have BOTH a percentage and an absolute count on the same row (e.g. WBC differential: Neutrophiles, Eosinophiles, Basophiles, Lymphocytes, Monocytes), prefer the ABSOLUTE COUNT (giga/L, 10^9/L) and use its absolute reference range. Do NOT mix the percent value with an absolute reference range.
- For age-dependent reference ranges (e.g. PSA, where the reference depends on patient age and is shown as a separate small lookup table), pick the row that matches the patient's age and use that range.
- For parameters reported in two different units on adjacent lines (e.g. Iron in µg/dL and µmol/L), emit ONE entry only - using the unit that matches the patient's locale - with its correctly paired reference range. Do NOT emit two separate entries.
- If a value and a reference range have CLEARLY mismatched units or magnitudes (e.g. value=46.1% paired with (0.05-0.60) absolute), you have associated the wrong reference. Re-read the page and pick the correct one. If you really cannot pair them, omit the parameter rather than display a wrong pairing.

==========================================================
EXTRACTION COMPLETENESS - MOST IMPORTANT RULE
==========================================================
The JSON field is named ""key_results"" for legacy technical reasons.
IT MEANS ""THE COMPLETE LIST OF EVERY SINGLE LAB RESULT FOUND IN THE PDF"".
- Include EVERY measured parameter that appears in the PDF, WITHOUT EXCEPTION.
- NEVER skip a parameter because it is normal or because it seems ""less important"".
- Preserve the EXACT parameter name, value, unit and reference range as they appear.
- Pay SPECIAL ATTENTION to: Imunochimie / hormones (TSH, FT3, FT4), tumor markers (PSA, CEA, AFP, CA125, CA15-3, CA19-9), vitamins (D, B12, folate, ferritin), iron panel (Fer, Transferrine, CTFF, CST). These are often in separate sub-sections and frequently forgotten.

STATUS FIELD:
  * ""normal""     => value falls inside the reference range
  * ""high""       => value is above the reference range
  * ""low""        => value is below the reference range
  * ""borderline"" => value is at the exact limit of the reference range, or within ~5% of a limit
  Every parameter whose status is high/low/borderline MUST also appear as an entry in ""abnormal_findings"".

DETECTING NON-MEDICAL FILES:
If the PDF is NOT a medical analysis (wrong document type, empty, unintelligible, or clearly not a lab result), set ""is_medical_analysis"": false and explain in ""rejection_reason"". In that case set ""key_results"" and ""abnormal_findings"" to empty arrays, and keep ""summary"", ""correlations"", ""recommendations"", ""disclaimer"" as short explanatory strings.

CONTENT GUIDELINES:
- ""summary"": 2-3 sentences overviewing what was analyzed.
- ""key_results"": THE COMPLETE LIST of all measured parameters with simple explanations.
- ""abnormal_findings"": EVERY value outside the normal range, with educational explanations of possible causes.
- ""correlations"": MANDATORY MINIMUM 5-6 full sentences. Explain meaningful combinations (e.g. low Hb + low ferritin = possible iron-deficiency anemia; elevated Gamma GT + normal transaminases = possible cholestatic pattern or alcohol use; high ferritin + normal iron saturation = inflammation rather than overload). Discuss 2-3 different combinations across panels (hematology, liver, kidney, lipid, thyroid, iron) when present.
- ""recommendations"": MANDATORY MINIMUM 5-6 full sentences. Concrete general guidance: (1) lifestyle/dietary advice, (2) hydration/activity, (3) when to repeat the tests and which parameters to monitor, (4) which specialty to consult, (5) red-flag symptoms, (6) reassurance for normal findings. NEVER mention specific medications, doses or treatments.
- ""disclaimer"": educational only, NOT a medical diagnosis, qualified doctor must be consulted.

OUTPUT FORMAT (CRITICAL):
- Respond ONLY with a JSON OBJECT (no markdown, no code fences, no commentary).
- The JSON MUST conform exactly to this schema (all keys required, no extra keys):
{
  ""is_medical_analysis"": boolean,
  ""rejection_reason"": string|null,
  ""patient_info"": { ""name"": string|null, ""age"": string|null, ""sex"": string|null, ""date_taken"": string|null, ""laboratory"": string|null, ""doctor_requesting"": string|null },
  ""summary"": string,
  ""key_results"": [ { ""parameter"": string, ""value"": string, ""unit"": string, ""reference_range"": string, ""status"": ""normal""|""high""|""low""|""borderline"", ""explanation"": string } ],
  ""abnormal_findings"": [ { ""parameter"": string, ""explanation"": string, ""severity"": ""mild""|""moderate""|""severe"" } ],
  ""correlations"": string,
  ""recommendations"": string,
  ""disclaimer"": string
}";

        private static string BuildUserPrompt(string languageName, string languageCode, string fileName) =>
$@"RESPONSE LANGUAGE: {languageName} (code: {languageCode})

The patient's medical PDF is attached as inline data (file name: {fileName}).

Task:
1. Read the PDF visually and identify every section header it contains (Hematology, Biochemistry, Immunochemistry, Lipid panel, Coagulation, ESR/VSH, Urinalysis, Hormones, Tumor markers, Vitamins, etc.).
2. For each section, extract EVERY measured parameter with its value, unit and reference range exactly as printed. Pay extra attention to Immunochemistry (hormones, tumor markers, vitamins) which is often forgotten.
3. Apply the value-vs-reference pairing rules from the system instructions (WBC differential, age-dependent ranges, dual-unit rows, mismatched magnitudes).
4. Determine each parameter's status (normal/high/low/borderline).
5. Produce the structured JSON object exactly per the schema in the system instructions, written entirely in {languageName}. Do NOT wrap it in markdown fences.";
    }
}
