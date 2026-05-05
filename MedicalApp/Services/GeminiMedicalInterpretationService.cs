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

                // 429 (rate-limit) and 503 (server overload) are TRANSIENT.
                // Throw a typed exception so the controller can apply a longer backoff
                // and a higher attempt count for them.
                var statusInt = (int)response.StatusCode;
                if (statusInt == 429 || statusInt == 503)
                {
                    throw new GeminiTransientException(statusInt,
                        $"Gemini API transient error {statusInt}: {Truncate(responseString, 300)}");
                }

                throw new InvalidOperationException(
                    $"Gemini API error {statusInt}: {Truncate(responseString, 500)}");
            }

            // 4) Parse the wrapper to extract the JSON the model produced
            string modelText;
            string finishReason = "";
            int inputTokens = 0, outputTokens = 0;
            try
            {
                using var doc = JsonDocument.Parse(responseString);
                var root = doc.RootElement;

                // Validate candidates exist (Gemini may block content with safety filters)
                if (!root.TryGetProperty("candidates", out var candidates) || candidates.GetArrayLength() == 0)
                {
                    var promptFeedback = root.TryGetProperty("promptFeedback", out var pf)
                        ? pf.ToString() : "(no feedback)";
                    _logger.LogError("Gemini returned no candidates. promptFeedback: {Feedback}. Body: {Body}",
                        promptFeedback, Truncate(responseString, 1000));
                    throw new InvalidOperationException(
                        "Gemini returned no candidates (possibly blocked by safety filters).");
                }

                var candidate = candidates[0];
                if (candidate.TryGetProperty("finishReason", out var fr))
                    finishReason = fr.GetString() ?? "";

                // candidates[0].content.parts[0].text  -> the JSON string we asked the model to produce
                if (!candidate.TryGetProperty("content", out var contentEl)
                    || !contentEl.TryGetProperty("parts", out var parts)
                    || parts.GetArrayLength() == 0
                    || !parts[0].TryGetProperty("text", out var textEl))
                {
                    _logger.LogError("Gemini candidate has no text part. finishReason={Finish}. Body: {Body}",
                        finishReason, Truncate(responseString, 1500));
                    throw new InvalidOperationException(
                        $"Gemini returned an empty response (finishReason={finishReason}).");
                }

                modelText = textEl.GetString() ?? string.Empty;

                if (root.TryGetProperty("usageMetadata", out var usage))
                {
                    if (usage.TryGetProperty("promptTokenCount", out var pt))
                        inputTokens = pt.GetInt32();
                    if (usage.TryGetProperty("candidatesTokenCount", out var ct2))
                        outputTokens = ct2.GetInt32();
                }
            }
            catch (InvalidOperationException) { throw; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Could not parse Gemini wrapper response. Body: {Body}",
                    Truncate(responseString, 2000));
                throw new InvalidOperationException("Gemini returned an unrecognized response shape.", ex);
            }

            _logger.LogInformation(
                "Gemini response received. Tokens in={In} out={Out}. FinishReason={Finish}. TextLen={Len}",
                inputTokens, outputTokens, finishReason, modelText.Length);

            // Truncated output -> JSON will be invalid. Fail fast with a clear message
            // so the auto-retry in the controller kicks in.
            if (string.Equals(finishReason, "MAX_TOKENS", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Gemini hit MaxOutputTokens ({_settings.MaxOutputTokens}). Response was truncated.");
            }

            // 5) Strip any accidental markdown fences and extract JSON, then parse
            var cleaned = ExtractJsonObject(modelText);

            InterpretationResult? result;
            try
            {
                result = JsonSerializer.Deserialize<InterpretationResult>(cleaned, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    AllowTrailingCommas = true,
                    ReadCommentHandling = JsonCommentHandling.Skip
                });
            }
            catch (JsonException ex)
            {
                _logger.LogError(ex,
                    "Failed to parse Gemini structured JSON. FinishReason={Finish}. Cleaned (first 3000 chars): {Body}",
                    finishReason, Truncate(cleaned, 3000));
                throw new InvalidOperationException(
                    "The AI returned an unparseable response. Please try again.", ex);
            }

            if (result == null)
                throw new InvalidOperationException("The AI returned an empty response.");

            // Self-audit verification: if the model declared more parameters than it
            // actually emitted in key_results, it silently skipped some.
            // Throw a recoverable error so the controller's retry loop kicks in.
            if (result.IsMedicalAnalysis && result.Audit != null)
            {
                var listed = result.KeyResults?.Count ?? 0;
                var expected = result.Audit.ExpectedCount;
                var auditNames = result.Audit.ParameterNames?.Count ?? 0;

                // Use the larger of expected_count and parameter_names.Count as
                // ground truth (the model sometimes fills only one of them).
                var groundTruth = Math.Max(expected, auditNames);

                if (groundTruth > listed && groundTruth - listed >= 1)
                {
                    _logger.LogWarning(
                        "Gemini self-audit mismatch: declared {GroundTruth} parameters but emitted only {Listed} in key_results. Forcing retry.",
                        groundTruth, listed);
                    throw new InvalidOperationException(
                        $"Gemini extraction incomplete: model declared {groundTruth} parameters but only emitted {listed}. Retrying.");
                }
            }

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

        /// <summary>
        /// Robustly extracts the first complete JSON object from the model output.
        /// Handles: markdown code fences, leading/trailing prose, BOM/whitespace.
        /// Returns the inner text unchanged if no balanced object is found (caller
        /// will then fail with a useful error message).
        /// </summary>
        private static string ExtractJsonObject(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return s ?? string.Empty;

            // Strip markdown code fences first
            var t = StripMarkdownFences(s);

            // Find the first '{' and the matching closing '}', ignoring braces inside strings
            int start = t.IndexOf('{');
            if (start < 0) return t;

            int depth = 0;
            bool inString = false;
            bool escape = false;

            for (int i = start; i < t.Length; i++)
            {
                char c = t[i];

                if (escape) { escape = false; continue; }
                if (c == '\\' && inString) { escape = true; continue; }
                if (c == '"') { inString = !inString; continue; }
                if (inString) continue;

                if (c == '{') depth++;
                else if (c == '}')
                {
                    depth--;
                    if (depth == 0)
                        return t.Substring(start, i - start + 1);
                }
            }

            // Unbalanced (likely truncated by MAX_TOKENS) - return what we have so the
            // JSON parser raises a clear error.
            return t.Substring(start);
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

==========================================================
TWIN-PARAMETER RULE (CRITICAL)
==========================================================
Several common laboratory parameters come in PAIRS or SMALL FAMILIES on the same panel.
If you see ONE of them, you MUST scan again for the OTHERS in the SAME report - they are
almost always present together. Concretely:
  * PSA family → look for BOTH ""PSA total"" AND ""PSA free"" (a.k.a. ""FREE PSA"", ""PSA libre"", ""PSA liber""). If only one is in your output but the PDF shows two, you missed one.
  * Thyroid family → ""TSH"", ""FT3"" (T3 libre), ""FT4"" (T4 libre), often ""anti-TPO"".
  * Iron family → ""Fer (Iron)"", ""Transferrine"", ""CTFF / TIBC"", ""Coefficient de saturation""/""TSAT"".
  * B-vitamin family → ""Vitamin B12"", ""Folate / Folic acid"", often together with ""Ferritin"".
  * Liver enzymes → ""ALT/TGP"", ""AST/TGO"", ""Gamma GT"", ""ALP"", ""Bilirubin"".
  * Lipid panel → ""Cholesterol total"", ""HDL"", ""LDL"", ""Triglycerides"", ""Cholesterol non-HDL"".
  * Renal panel → ""Creatinine"", ""Urea"", ""eGFR (DFG/MDRD/CKD-EPI)"".
  * Hematology core → ""Hemoglobin"", ""Hematocrit"", ""RBC"", ""WBC"", ""Platelets"", and ALL the WBC differential lines.
Before you finalize, RE-READ the PDF specifically looking for these family members. Missing a twin parameter (e.g. reporting only Total PSA when Free PSA is also printed) is THE most common error and MUST be avoided.

==========================================================
MULTI-THRESHOLD / TIERED-TARGET RULE (CRITICAL)
==========================================================
Some parameters (typically lipid panel: non-HDL cholesterol, LDL, HDL, triglycerides;
and glucose targets for diabetics) show MULTIPLE reference thresholds stacked in the
""reference range"" column, each tied to a clinical RISK/TARGET label like:
   ""<85 mg/dL - pacienți cu risc cardiovascular foarte înalt""
   ""<100 mg/dL - pacienți cu risc cardiovascular înalt""
   ""<130 mg/dL - pacienți cu risc cardiovascular moderat""
When you see a list of thresholds LIKE THIS (the thresholds are ordered from STRICTEST
to most permissive, and the labels describe RISK categories, NOT patient age), you MUST
apply the STRICTEST-SATISFIED-THRESHOLD algorithm:

  1. Identify the list of thresholds. First select only the thresholds for the correct
     age category (adult vs pediatric, based on patient age shown in the PDF).
  2. Sort the remaining thresholds ASCENDING by numeric cut-off (strictest first).
  3. Walk them in order. The FIRST threshold T for which (value < T) is the ANSWER:
     - reference_range = that exact threshold string (e.g. ""<100 mg/dL - risc cardiovascular înalt"")
     - status = ""high""  IF the chosen label is NOT the most permissive label
                          (i.e. the value failed at least one stricter threshold first)
              = ""normal""  IF the chosen label IS the most permissive label
                            (value satisfies all thresholds AND only the loosest one is ""normal"")
     - When status = ""high"", ALSO add an entry to abnormal_findings with severity
       ""severe"" if the selected label is the MOST SEVERE risk-category, ""moderate""
       if it is a middle risk-category, ""mild"" otherwise.
  4. If NO threshold is satisfied (value >= all thresholds), status = ""high"" and use
     the most permissive threshold as reference_range, severity = ""severe"".
  5. In the ""explanation"" field, spell out the calculation briefly in plain language,
     e.g.:  ""Valoarea 96.1 mg/dL nu se încadrează sub 85 (țintă pentru risc CV foarte înalt),
             dar se încadrează sub 100 - țintă pentru risc CV înalt. Valoarea indică deci
             o poziție în zona de risc cardiovascular înalt, motiv pentru care este
             marcată ca anormală.""

WORKED EXAMPLE — non-HDL cholesterol = 96.1 mg/dL, adult patient:
  Adult thresholds (strictest first):  <85 (very high risk), <100 (high risk), <130 (moderate risk)
  96.1 < 85 ? NO.
  96.1 < 100 ? YES → SELECT this row.
  => reference_range = ""<100 mg/dL - risc cardiovascular înalt""
  => status = ""high"" (chosen label ""înalt"" is NOT the most permissive ""moderat"")
  => abnormal_findings += { parameter: ""Colesterol non-HDL"", severity: ""moderate"",
                            explanation: ""Valoarea indică risc cardiovascular înalt..."" }

NOTE: this rule is DIFFERENT from the AGE-DEPENDENT rule (PSA by age bracket, hemoglobin
in children). For age-dependent ranges, you pick the ROW MATCHING THE PATIENT'S AGE
and then interpret normally (value-inside-range = normal). The multi-threshold rule
only applies when the rows are labeled by RISK/TARGET, not by age bracket.

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

==========================================================
SELF-VERIFICATION FIELD (MANDATORY)
==========================================================
Add a TOP-LEVEL field ""_extraction_audit"" to the JSON output. It is your private audit
trail that we use to verify completeness. Format:
  ""_extraction_audit"": {
    ""expected_count"": <integer - the number of distinct measured parameters you SAW in the PDF>,
    ""parameter_names"": [<every parameter name you saw, in order, exactly as printed>]
  }
Rules:
  - ""expected_count"" MUST equal the length of ""parameter_names"" AND the length of ""key_results"".
  - List EVERY parameter you noticed - even if you decided not to include it in ""key_results""
    (in which case explain in a separate trailing entry ""<NAME> [skipped because ...]"").
  - This is a sanity check: if you wrote 10 names in ""parameter_names"" but only 8 entries
    in ""key_results"", you skipped 2 parameters and your output is INCOMPLETE.
  - Do this BEFORE you submit. Re-scan the PDF if the counts disagree.

OUTPUT FORMAT (CRITICAL):
- Respond ONLY with a JSON OBJECT (no markdown, no code fences, no commentary).
- The JSON MUST conform exactly to this schema (all keys required, no extra keys except _extraction_audit):
{
  ""is_medical_analysis"": boolean,
  ""rejection_reason"": string|null,
  ""patient_info"": { ""name"": string|null, ""age"": string|null, ""sex"": string|null, ""date_taken"": string|null, ""laboratory"": string|null, ""doctor_requesting"": string|null },
  ""summary"": string,
  ""key_results"": [ { ""parameter"": string, ""value"": string, ""unit"": string, ""reference_range"": string, ""status"": ""normal""|""high""|""low""|""borderline"", ""explanation"": string } ],
  ""abnormal_findings"": [ { ""parameter"": string, ""explanation"": string, ""severity"": ""mild""|""moderate""|""severe"" } ],
  ""correlations"": string,
  ""recommendations"": string,
  ""disclaimer"": string,
  ""_extraction_audit"": { ""expected_count"": integer, ""parameter_names"": [string, ...] }
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
