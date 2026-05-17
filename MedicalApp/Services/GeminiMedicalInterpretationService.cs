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
            Stream pdfStream, string fileName, string languageCode,
            PatientContext? patientContext = null, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(_settings.ApiKey))
                throw new InvalidOperationException(
                    "Gemini API key is not configured. Run: dotnet user-secrets set \"Gemini:ApiKey\" \"<your-key>\"");

            // 1) Read the PDF into memory and Base64-encode it for inline_data
            byte[] pdfBytes;
            using (var ms = new MemoryStream())
            {
                await pdfStream.CopyToAsync(ms, ct);
                pdfBytes = ms.ToArray();
            }
            var pdfBase64 = Convert.ToBase64String(pdfBytes);

            return await CallGeminiAsync(
                languageCode: languageCode,
                fileName: fileName,
                patientContext: patientContext,
                pdfBase64: pdfBase64,
                pdfBytesLength: pdfBytes.Length,
                extractedText: null,
                ct: ct);
        }

        /// <summary>
        /// TEXT-BASED interpretation path.
        /// The PDF text is extracted upstream by <see cref="PdfTextExtractor"/> (PdfPig reads
        /// the PDF's text layer directly, so digits are LITERAL — no OCR hallucination
        /// possible like with the vision pipeline). Gemini then receives only the extracted
        /// text and focuses on medical reasoning, not on reading pixels. This dramatically
        /// reduces hallucinations on values, reference ranges and unit signs for digital PDFs.
        /// </summary>
        public Task<(InterpretationResult Result, int InputTokens, int OutputTokens, string RawResponse)> InterpretAsync(
            string extractedText, string languageCode, CancellationToken ct = default)
            => InterpretTextAsync(extractedText, fileName: "(text extracted from PDF)",
                                  languageCode: languageCode, patientContext: null, ct: ct);

        /// <summary>Internal text-based interpretation with optional patient context.</summary>
        public async Task<(InterpretationResult Result, int InputTokens, int OutputTokens, string RawResponse)> InterpretTextAsync(
            string extractedText, string fileName, string languageCode,
            PatientContext? patientContext = null, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(_settings.ApiKey))
                throw new InvalidOperationException(
                    "Gemini API key is not configured. Run: dotnet user-secrets set \"Gemini:ApiKey\" \"<your-key>\"");

            if (string.IsNullOrWhiteSpace(extractedText) || extractedText.Length < 50)
                throw new InvalidOperationException(
                    "Extracted text is empty or too short for text-based Gemini interpretation.");

            return await CallGeminiAsync(
                languageCode: languageCode,
                fileName: fileName,
                patientContext: patientContext,
                pdfBase64: null,
                pdfBytesLength: 0,
                extractedText: extractedText,
                ct: ct);
        }

        /// <summary>
        /// Shared core that builds the Gemini request and parses the response. Either a
        /// base64 PDF (vision path) OR an extracted-text string (text path) is provided,
        /// never both. The same prompt and JSON schema are used in either case; only the
        /// input modality and a couple of lines in the user prompt differ.
        /// </summary>
        private async Task<(InterpretationResult Result, int InputTokens, int OutputTokens, string RawResponse)> CallGeminiAsync(
            string languageCode, string fileName, PatientContext? patientContext,
            string? pdfBase64, int pdfBytesLength,
            string? extractedText, CancellationToken ct)
        {
            var languageName = LanguageNames.TryGetValue(languageCode, out var n) ? n : "English";

            // 2) Build the request body
            var systemPrompt = BuildSystemPrompt();
            var userPrompt = BuildUserPrompt(languageName, languageCode, fileName, patientContext,
                                             hasInlinePdf: pdfBase64 != null,
                                             extractedText: extractedText);
            var requestBody = BuildRequestBody(systemPrompt, userPrompt, pdfBase64);

            var url = string.Format(EndpointFormat, _settings.Model, _settings.ApiKey);

            // 3) Send request
            using var http = _httpClientFactory.CreateClient();
            http.Timeout = TimeSpan.FromSeconds(_settings.TimeoutSeconds);

            using var content = new StringContent(requestBody, Encoding.UTF8, "application/json");

            if (pdfBase64 != null)
            {
                _logger.LogInformation(
                    "Calling Gemini {Model} for {Language} (VISION mode). PDF bytes: {Bytes}, base64 length: {B64}",
                    _settings.Model, languageCode, pdfBytesLength, pdfBase64.Length);
            }
            else
            {
                _logger.LogInformation(
                    "Calling Gemini {Model} for {Language} (TEXT mode). Extracted text chars: {Chars}",
                    _settings.Model, languageCode, extractedText?.Length ?? 0);
            }

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

            var jsonOptions = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                AllowTrailingCommas = true,
                ReadCommentHandling = JsonCommentHandling.Skip
            };

            InterpretationResult? result;
            try
            {
                result = JsonSerializer.Deserialize<InterpretationResult>(cleaned, jsonOptions);
            }
            catch (JsonException firstEx)
            {
                // Plan A: defensive JSON auto-repair.
                // On very long Gemini outputs (~6k+ tokens) the model occasionally drops
                // a closing brace between two adjacent objects in an array (typical
                // "structural drift" symptom on long structured outputs). The classic
                // signature is `... "value" , { ... }` instead of `... "value" }, { ... }`.
                // Before bouncing to the controller's retry-loop (which costs another
                // ~60s round-trip + tokens), we try a targeted in-place repair.
                var repaired = TryRepairGeminiJsonDrift(cleaned, firstEx, _logger);
                if (repaired != null && !ReferenceEquals(repaired, cleaned))
                {
                    try
                    {
                        result = JsonSerializer.Deserialize<InterpretationResult>(repaired, jsonOptions);
                        _logger.LogWarning(
                            "Gemini JSON auto-repair succeeded. Original error: {Err}. The interpretation pipeline continued without a retry.",
                            firstEx.Message);
                        cleaned = repaired; // for downstream consumers (re-serialize, debug)
                    }
                    catch (JsonException secondEx)
                    {
                        _logger.LogError(secondEx,
                            "Gemini JSON auto-repair FAILED on second parse. FinishReason={Finish}. Original error: {OrigErr}. Cleaned (first 3000 chars): {Body}",
                            finishReason, firstEx.Message, Truncate(cleaned, 3000));
                        throw new InvalidOperationException(
                            "The AI returned an unparseable response. Please try again.", secondEx);
                    }
                }
                else
                {
                    _logger.LogError(firstEx,
                        "Failed to parse Gemini structured JSON. FinishReason={Finish}. Cleaned (first 3000 chars): {Body}",
                        finishReason, Truncate(cleaned, 3000));
                    throw new InvalidOperationException(
                        "The AI returned an unparseable response. Please try again.", firstEx);
                }
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
        // (Old vision-only stub removed - InterpretAsync is now implemented as the text-based path
        //  pointing to InterpretTextAsync, see above.)

        // =========================================================================
        // Request body construction
        // =========================================================================
        private string BuildRequestBody(string systemPrompt, string userPrompt, string? pdfBase64)
        {
            // We rely on responseMimeType=application/json (Gemini's "JSON mode") plus a
            // detailed schema described in the prompt. Gemini also supports responseSchema,
            // but mixing it with PDF inline_data is brittle - JSON mode + a strict prompt
            // is sufficient and more flexible.
            //
            // Build the user-message parts: always the prompt text; optionally the PDF
            // as inline_data (vision path).
            object[] userParts;
            if (pdfBase64 != null)
            {
                userParts = new object[]
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
                };
            }
            else
            {
                // Text-only path: the extracted text is embedded inside userPrompt itself.
                userParts = new object[] { new { text = userPrompt } };
            }

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
                        parts = userParts
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
        // JSON Auto-Repair — defensive last-mile fixer for Gemini structural drift
        // =========================================================================
        /// <summary>
        /// Attempts to repair the most common Gemini JSON malformations observed in
        /// production. Returns the repaired string when a fix was applied, OR the
        /// SAME reference as the input when no fix is applicable (caller compares
        /// references to decide whether to retry parsing).
        /// </summary>
        /// <remarks>
        /// Currently fixes:
        /// <list type="bullet">
        ///   <item><b>Missing-close-brace-between-array-objects</b>: pattern
        ///   <c>"\s*,\s*\n\s*\{</c> where the character before the comma is a closing
        ///   string quote (i.e. end of a property value). In an object array this
        ///   means a `}` was dropped just before the comma. Confirmed in
        ///   production on long outputs (~6.7k tokens) when patient has CV-risk
        ///   declared (longer prompt → longer output → higher drift risk).</item>
        /// </list>
        /// The repair is intentionally conservative: it ONLY inserts a `}` and only
        /// in positions whose surrounding context clearly identifies the drift. If
        /// the second parse also fails, the original error is propagated unchanged.
        /// </remarks>
        internal static string? TryRepairGeminiJsonDrift(string json, JsonException originalError, ILogger? logger = null)
        {
            if (string.IsNullOrEmpty(json)) return json;

            // Heuristic: scan the string for `"<ws>,<ws>{` patterns. For each match,
            // walk backwards from the position of `"` to make sure it CLOSES a string
            // value (i.e. not preceded by `\`, and there is a `:` somewhere on the
            // same line indicating "property: value" syntax). If so, insert `}`
            // BETWEEN the closing quote and the comma. This converts
            //    "explanation": "..." , {
            // into
            //    "explanation": "..." }, {
            // which is the well-formed equivalent for object arrays.

            var sb = new StringBuilder(json.Length + 16);
            int i = 0;
            int repairsApplied = 0;
            int len = json.Length;

            while (i < len)
            {
                char c = json[i];

                // Look for an UNESCAPED closing quote (not preceded by an odd number
                // of backslashes).
                if (c == '"' && !IsEscapedAt(json, i))
                {
                    // Tentatively look ahead: whitespace, then comma, then whitespace, then `{`.
                    int j = i + 1;
                    while (j < len && char.IsWhiteSpace(json[j])) j++;
                    if (j < len && json[j] == ',')
                    {
                        int afterComma = j + 1;
                        while (afterComma < len && char.IsWhiteSpace(json[afterComma])) afterComma++;
                        if (afterComma < len && json[afterComma] == '{')
                        {
                            // Confirmed drift pattern. Verify left side: is this `"`
                            // actually closing a string VALUE (not a property key)?
                            // A property value `"..."` is preceded by `:` (with possible
                            // whitespace), e.g. `"explanation": "..."`. A property KEY
                            // is followed by `:`, e.g. `"explanation":`. We want VALUE.
                            //
                            // Find the matching opening `"` of this string (walk back
                            // until previous unescaped `"`), then look at what's BEFORE
                            // the opening quote. If it's `:` (after whitespace), the
                            // string is a value -> repair. Otherwise (e.g. `,` or `{`),
                            // it's a property key in an empty-object slot — don't touch.
                            if (LooksLikeValueClosingQuote(json, i))
                            {
                                // Emit the closing `"`, then INSERT the missing `}`,
                                // then let the outer loop continue past the quote.
                                sb.Append(c);
                                sb.Append('}');
                                repairsApplied++;
                                i++;
                                continue;
                            }
                        }
                    }
                }

                sb.Append(c);
                i++;
            }

            if (repairsApplied == 0)
            {
                logger?.LogDebug(
                    "JSON auto-repair found no applicable patterns. Original parse error stands: {Err}",
                    originalError.Message);
                return json; // same reference — caller treats this as "no repair available"
            }

            logger?.LogWarning(
                "JSON auto-repair: inserted {Count} missing closing brace(s) before sibling object(s). Will retry parsing.",
                repairsApplied);
            return sb.ToString();
        }

        /// <summary>
        /// Returns true iff the quote at <paramref name="quotePos"/> in <paramref name="json"/>
        /// is preceded by an EVEN number of consecutive backslashes (i.e. not escaped).
        /// </summary>
        private static bool IsEscapedAt(string json, int quotePos)
        {
            int backslashes = 0;
            int k = quotePos - 1;
            while (k >= 0 && json[k] == '\\') { backslashes++; k--; }
            return (backslashes % 2) == 1;
        }

        /// <summary>
        /// Checks whether the closing quote at <paramref name="closingQuotePos"/> ends a
        /// JSON string VALUE (preceded by `:` ignoring whitespace before the matching
        /// opening quote) vs. a property KEY. Repair is only valid for VALUES.
        /// </summary>
        private static bool LooksLikeValueClosingQuote(string json, int closingQuotePos)
        {
            // Walk backwards to find the matching opening quote.
            int k = closingQuotePos - 1;
            while (k >= 0)
            {
                if (json[k] == '"' && !IsEscapedAt(json, k))
                    break;
                k--;
            }
            if (k < 0) return false; // no opening quote found

            // Walk further back from the opening quote, skipping whitespace.
            int p = k - 1;
            while (p >= 0 && char.IsWhiteSpace(json[p])) p--;
            // For a property value, the previous non-whitespace char must be `:`.
            return p >= 0 && json[p] == ':';
        }

        // =========================================================================
        // PROMPTS - adapted from the OpenAI version, with Gemini-specific reminders
        // =========================================================================
        private static string BuildSystemPrompt() => @"You are MedicalApp's medical analysis interpretation assistant. Your role is to provide an EDUCATIONAL and INFORMATIVE interpretation of laboratory medical results extracted from a PDF lab report.

STRICT RULES - you MUST follow ALL of them:
1. Respond ENTIRELY in the language specified by the user in their message.
2. You are NOT a doctor. You do NOT give medical diagnoses.
3. You do NOT recommend specific medications, doses, or treatments.
4. You ALWAYS recommend consulting a qualified physician.
5. You do NOT invent values - if a parameter is missing from the input, do NOT include it.
6. Use an empathetic, professional, CALM tone - never alarming.
7. Use SIMPLE language, accessible to a non-medical reader.
8. Provide a DETAILED interpretation (not a short one) - include thorough explanations.
9. NEVER fabricate or guess LOINC codes. Emit ""loinc_code"": null whenever
   you are not certain. A null mapping is ALWAYS preferred over a wrong code.
   For the analytes listed in the ""ANCHORED LOINC CODES"" section below,
   use ONLY the exact code provided — do not substitute a similar one.

==========================================================
INPUT SOURCE — TWO POSSIBLE MODES
==========================================================
You receive the lab report in ONE of two forms (the user message will tell you which):

MODE A — INLINE PDF (visual): The PDF is attached as inline_data and you read it
visually as a human radiographer would, respecting tabular layouts (each row is one
parameter, columns are usually [Parameter | Value | Unit | Reference range | Previous value]).

MODE B — EXTRACTED TEXT (literal): A layout-aware extractor (PdfPig) has already converted
the PDF text-layer to plain text and grouped words into visual rows. Multiple spaces
separate columns. Each visible row of the PDF appears on its own line of text.
**WHEN IN MODE B THE NUMBERS ARE LITERAL — copy them character-for-character. NEVER
""correct"" a digit because it looks unusual. The extractor reads the file's text layer
directly, so values, units and reference ranges are exactly what the lab printed.**

In BOTH modes apply the following parsing rules:
- For parameters reported as TWO SEPARATE ROWS (each row has its own value AND its own reference range) - typically a WBC differential printed as both COUNTS and PERCENTS, e.g.
      ""Numar total de neutrofile: 5.73 10^3/mm3 (ref 2-8 / 10^3/mm3)""
      ""Procent de neutrofile:    59.3 %       (ref 45-80 / %)""
  - Treat these as **TWO INDEPENDENT PARAMETERS**. Output BOTH entries in key_results,
    each with its own value, unit and reference range exactly as printed. NEVER drop one of
    them just because the other is also present. The percentage row and the absolute-count
    row both carry diagnostic information (the percent shows distribution, the absolute count
    shows the total burden) and clinicians use them together.
  - The same applies to ANY family that the lab printed twice (ex. lymphocytes-count vs
    lymphocytes-percent, monocytes-count vs monocytes-percent, eosinophiles-count vs
    eosinophiles-percent, basophiles-count vs basophiles-percent).
- For parameters reported on a SINGLE ROW that contains BOTH a percent and an absolute count
  but only ONE reference range (e.g. ""Neutrofile: 65% (5.73 10^3/mm3) - ref 2-8 / 10^3/mm3""),
  output ONE entry only - the absolute count - paired with the absolute reference range.
  Do NOT mix the 65% value with an absolute reference range. This is the rare ambiguous case.
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
- ""key_results"": THE COMPLETE LIST of all measured parameters. EACH ENTRY's ""explanation""
  field MUST be **AT LEAST 3 FULL SENTENCES, written in clear lay-person language**, AND it
  MUST be filled with substantive content REGARDLESS of whether the value is normal or abnormal.
  Concretely, every ""explanation"" must include all four of the following:
    (1) WHAT this parameter measures and where in the body / which system it relates to
        (e.g. ""Hemoglobina este proteina din globulele roșii care transportă oxigenul de la
         plămâni către țesuturi"").
    (2) WHY this measurement matters clinically (what doctors look for when they see it).
    (3) HOW this specific value compares with the reference range (whether it falls inside or
        outside, and how close to the limits it is). For normal values, reassure clearly;
        for abnormal values, mention what high or low typically points to in plain terms,
        WITHOUT giving a diagnosis.
    (4) A short suggestion of what kind of follow-up or context the patient should consider
        (e.g. ""Dacă valoarea rămâne stabilă la analize repetate, e un semn liniștitor"" or
         ""Discutați cu medicul împreună cu valorile XYZ pentru o imagine completă"").
  AVOID one-line stubs like ""Valoarea este în limite normale"" or ""Procentul este în
  limite normale"". Such terse explanations are NOT acceptable - if you find yourself writing
  one, expand it with the four points above.
- ""abnormal_findings"": EVERY value outside the normal range, with educational explanations
  of possible causes (multi-sentence, even more detailed than the key_results explanation
  for the same parameter).
- ""correlations"": MANDATORY MINIMUM 5-6 full sentences. Explain meaningful combinations (e.g. low Hb + low ferritin = possible iron-deficiency anemia; elevated Gamma GT + normal transaminases = possible cholestatic pattern or alcohol use; high ferritin + normal iron saturation = inflammation rather than overload). Discuss 2-3 different combinations across panels (hematology, liver, kidney, lipid, thyroid, iron) when present.
- ""recommendations"": MANDATORY MINIMUM 5-6 full sentences. Concrete general guidance: (1) lifestyle/dietary advice, (2) hydration/activity, (3) when to repeat the tests and which parameters to monitor, (4) which specialty to consult, (5) red-flag symptoms, (6) reassurance for normal findings. NEVER mention specific medications, doses or treatments.
- ""disclaimer"": educational only, NOT a medical diagnosis, qualified doctor must be consulted.

==========================================================
LOINC CODE MAPPING (per parameter) — MANDATORY FIELDS
==========================================================
LOINC (Logical Observation Identifiers Names and Codes) is the international
standard for identifying lab tests. For EVERY entry in ""key_results"" you MUST
emit THREE additional fields (the value may be null when you don't know,
but THE KEY MUST ALWAYS BE PRESENT):

  ""loinc_code"":       string|null  - the official LOINC code (e.g. ""2324-2"" for GGT)
  ""loinc_long_name"":  string|null  - the LOINC Long Common Name in English
                                       (e.g. ""Gamma glutamyl transferase [Enzymatic
                                       activity/volume] in Serum or Plasma"")
  ""loinc_confidence"": ""high""|""medium""|""low""|null

CRITICAL: skipping these three keys is INCORRECT output. Every key_results
entry MUST contain ALL nine fields: parameter, value, unit, reference_range,
status, explanation, loinc_code, loinc_long_name, loinc_confidence. If you
do not know the LOINC code, emit explicit nulls — never omit the keys.

GUIDELINES:
- Identify the test from its name AS PRINTED in the report, its unit, its
  reference range, and the section context (Hematology, Lipids, Hormones, ...).
- Common Romanian/French/etc abbreviations and full names map to ONE LOINC:
    ""GGT"" / ""Gamma GT"" / ""γ-GT"" / ""Glutamiltranspeptidaza"" / ""Gamma-glutamyl transferase""
       all -> 2324-2 (Gamma glutamyl transferase in Ser/Plas)
    ""VSH"" / ""VS"" / ""ESR"" / ""Vitesse de sédimentation"" / ""Erythrocyte sedimentation rate""
       all -> 4537-7 (Erythrocyte sedimentation rate)
    ""Glicemie"" / ""Glucoza"" / ""Glucose"" -> 2345-7 (Glucose in Ser/Plas, mass concentration)
    ""TSH"" / ""Thyrotropin"" -> 3016-3
    ""LDL"" / ""LDL-Cholesterol"" -> 13457-7 (LDL calculated) or 18262-6 (LDL direct measurement);
                                    pick the one that matches the reference range or method.
    ""Acid uric"" / ""Uric acid"" / ""Urate"" / ""Urat"" -> 3084-1 (Urate [Mass/volume] in Ser/Plas).
       LOINC uses ""Urate"" as canonical English term even when the lab printed
       ""Uric acid"". Emit long_name=""Urate [Mass/volume] in Serum or Plasma"".
    ""ALT"" / ""SGPT"" / ""TGP"" / ""Alaninaminotransferaza"" / ""Alanine aminotransferase""
       -> 1742-6 (ALT in Ser/Plas, enzymatic activity)
    ""AST"" / ""SGOT"" / ""TGO"" / ""Aspartataminotransferaza"" / ""Aspartate aminotransferase""
       -> 1920-8 (AST in Ser/Plas, enzymatic activity)
    ""Lipaza"" / ""Lipase"" -> 3040-3 (Lipase in Ser/Plas, enzymatic activity). NOT 2571-8 (that is Triglyceride).
    ""Bazofile"" / ""Basophils"" -> 704-7 (count) or 706-2 (fraction). NEVER 701-3 (that is a microbiology code).
    ""Neutrofile"" absolute count -> 751-8 ; ""Neutrofile %"" fraction -> 770-8
    ""Limfocite"" absolute count -> 731-0 ; ""Limfocite %"" fraction -> 736-9
    ""Monocite"" absolute count -> 742-7 ; ""Monocite %"" fraction -> 5905-5
    ""Eozinofile"" absolute count -> 711-2 ; ""Eozinofile %"" fraction -> 713-8
    ""Hematocrit"" -> 4544-3 (volume fraction in Blood, automated count)
    ""MCV"" / ""Volum eritrocitar mediu"" -> 787-2 (Erythrocyte mean corpuscular volume)
    ""MCH"" / ""Hemoglobina eritrocitara medie"" -> 785-6
    ""MCHC"" / ""Concentratie medie a Hb / eritrocit"" -> 786-4
    ""RDW"" / ""Largimea distributiei eritrocitare"" -> 788-0
    ""MPV"" / ""Volum trombocitar mediu"" -> 32623-1
    ""Insulina"" -> 1558-6 (NOT 2044-6 which is sometimes Free insulin)

- ANCHORED LOINC CODES — these specific parameters have been observed to be
  FREQUENTLY MISIDENTIFIED by the model. The codes below are the OFFICIAL,
  VERIFIED LOINC codes for the canonical Serum/Plasma quantitative variant
  most commonly reported by Romanian labs. You MUST use EXACTLY these codes
  when the parameter name matches (any printed alias, including Romanian /
  French / English wording). DO NOT substitute a similar-looking code, DO NOT
  swap digits, DO NOT pick a ""close"" code from memory. If the parameter
  name matches one of these analytes, emit the code EXACTLY as written below
  with confidence=""high"":
    * LDH / Lactat dehidrogenaza / Lactate dehydrogenase (serum, enzymatic activity)
        -> ""14804-9""  (Lactate dehydrogenase [Enzymatic activity/volume] in Serum or Plasma by Lactate to pyruvate reaction)
        ALSO accepted aliases: ""LDH total"", ""LDH seric"", ""L-Lactat dehidrogenaza"".
    * eGFR / DFG / RFG estimat / Estimated GFR / Rata estimata a filtrarii glomerulare
        -> ""62238-1""  (Glomerular filtration rate/1.73 sq M.predicted [Volume Rate/Area] in Serum, Plasma or Blood by Creatinine-based formula (CKD-EPI))
        Use this code regardless of which CKD-EPI / MDRD formula the lab printed —
        it is the most widely-used eGFR LOINC code in Romanian labs. NOTE: a newer
        race-free CKD-EPI 2021 LOINC exists (""98979-8""), but ""62238-1"" remains the
        canonical anchor here for compatibility with the local LOINC subset.
    * Densitate urinară / Urine specific gravity / Densitatea urinei
        -> ""2965-2""   (Specific gravity of Urine)
    * Non-HDL cholesterol / Colesterol non-HDL / Non-HDL-C
        -> ""43396-1""  (Cholesterol non HDL [Mass/volume] in Serum or Plasma)
    * Procentul de protrombină / Activitate protrombinică / Indice de protrombină (%) / Prothrombin time activity (%)
        -> ""5894-1""   (Prothrombin time (PT) actual/normal in Platelet poor plasma by Coagulation assay)
        NOTE: this is the PERCENTAGE result (Quick %), NOT the seconds value (5902-2) and NOT the INR (6301-6).
    * Celule epiteliale plate / Epiteliu plat / Squamous epithelial cells / Epithelial cells (urine sediment)
        -> ""5787-7""   (Epithelial cells [#/area] in Urine sediment by Microscopy high power field)
        NOTE: this is the GENERAL epithelial-cells code most Romanian/French labs print.
        Do NOT swap to ""5787-2"" — that code does NOT exist in LOINC.
    * Anti-tiroglobulină / Ac anti-tireoglobulinici / Anti-Tg / Anti-thyroglobulin antibody
        -> ""8098-6""   (Thyroglobulin Ab [Units/volume] in Serum)
    * Calcitonina / Calcitonin
        -> ""1992-7""   (Calcitonin [Mass/volume] in Serum or Plasma)
        NOTE: do NOT use ""8000-2"" — that is an unrelated LOINC code.
    * pH urinar / pH urină / pH of Urine (dipstick / test strip)
        -> ""5803-2""   (pH of Urine by Test strip)
        NOTE: this is the dipstick variant Romanian labs print most often.
        Do NOT swap to ""2720-1"", ""2720-4"" or other ""2720-*"" codes — those
        describe a DIFFERENT body fluid, not Urine. If the lab clearly used a
        pH-meter (not a dipstick), the alternative is ""106930-1"" (pH of Urine
        by pH-meter); if no method is stated, ""2756-5"" (pH of Urine, generic)
        is acceptable. Default to ""5803-2"" for typical urine analysis reports.

  STRICT RULE on these eight anchored mappings:
    1. If you recognize the analyte but you are not 100% sure the unit and
       sample type match the anchored code's description above, STILL emit
       the anchored code (it covers the canonical lab variant). Better an
       anchored well-known code than a guess.
    2. NEVER ""correct"" an anchored code by digit-swap, by check-digit
       recomputation, or because another code looks more familiar.
    3. NEVER invent a new LOINC code for these eight analytes. If you find
       a lab variant that genuinely doesn't fit (e.g. an unusual sample
       type like ""LDH in CSF"" or ""Calcitonin stimulation test""), emit
       loinc_code=null rather than guessing a different code.

- CRITICAL distinctions you MUST respect:
    * Total vs Free (e.g. T4 total = 3026-2  vs FT4 = 3024-7;
                     PSA total = 2857-1     vs PSA free = 10886-0)
    * Serum/Plasma vs Whole Blood vs Capillary blood
    * Mass concentration vs Activity concentration vs Catalytic activity
  Use the reference range and unit to discriminate. When in doubt, prefer the
  most common Serum/Plasma quantitative variant.
- Confidence calibration (BE HONEST - we use it to flag borderline mappings):
    ""high""   = textbook match, code is unambiguous (~95% of common tests)
    ""medium"" = code is likely but there are 2-3 plausible LOINC variants
    ""low""    = guessing; the test name is unusual or the panel is lab-specific
    null      = no reasonable LOINC; e.g. a derived index, a ratio,
                a lab proprietary panel, or a multi-test composite score.
- DO NOT invent codes. If you cannot recall the LOINC for a parameter,
  emit ""loinc_code"": null and ""loinc_confidence"": null - that is the
  correct, safe choice. A NULL is always better than a fabricated code.
- The ""loinc_long_name"" must be the EXACT canonical English name LOINC
  uses; do not translate or paraphrase. We use it to sanity-check the code.

SELF-CONSISTENCY CHECK (CRITICAL - perform this BEFORE submitting):
For EVERY entry in key_results, re-read silently:
  ""Does my loinc_long_name actually describe the test from 'parameter'?""
  Examples of FAILURES that you MUST fix to null:
    - parameter=""Lipaza""   loinc_long_name=""Triglyceride...""    -> WRONG, set all 3 LOINC fields to null
    - parameter=""Bazofil"" loinc_long_name=""Yersinia identified..."" -> WRONG, set all 3 LOINC fields to null
    - parameter=""Sodiu""    loinc_long_name=""Potassium...""        -> WRONG, set all 3 LOINC fields to null
A simple rule: the head word of the long_name (Triglyceride / Yersinia / Potassium) MUST match the
analyte in 'parameter'. If you cannot make the head words agree, EMIT NULL FOR ALL THREE LOINC
FIELDS. A nulled mapping is FAR better than a contradictory one - the C# validator will catch
contradictions and flag them, costing time. Self-correct here.

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
  ""key_results"": [ { ""parameter"": string, ""value"": string, ""unit"": string, ""reference_range"": string, ""status"": ""normal""|""high""|""low""|""borderline"", ""explanation"": string, ""loinc_code"": string|null, ""loinc_long_name"": string|null, ""loinc_confidence"": ""high""|""medium""|""low""|null } ],
  ""abnormal_findings"": [ { ""parameter"": string, ""explanation"": string, ""severity"": ""mild""|""moderate""|""severe"" } ],
  ""correlations"": string,
  ""recommendations"": string,
  ""disclaimer"": string,
  ""_extraction_audit"": { ""expected_count"": integer, ""parameter_names"": [string, ...] }
}";

        private static string BuildUserPrompt(string languageName, string languageCode, string fileName,
            PatientContext? ctx = null,
            bool hasInlinePdf = true,
            string? extractedText = null)
        {
            // Build the patient-context block. If we know nothing, omit it entirely
            // so the model falls back to its general multi-threshold rule.
            string patientBlock = "";
            if (ctx != null && (ctx.CardiovascularRisk != null || ctx.AgeYears.HasValue || !string.IsNullOrWhiteSpace(ctx.Gender)))
            {
                patientBlock += "\nPATIENT CONTEXT (declared by the app's owner — use this to pick the correct lipid targets and to mention it in 'summary' and 'recommendations'):\n";
                if (ctx.AgeYears.HasValue)
                    patientBlock += $"- Age: {ctx.AgeYears} years\n";
                if (!string.IsNullOrWhiteSpace(ctx.Gender))
                    patientBlock += $"- Gender: {(ctx.Gender == "M" ? "Male" : ctx.Gender == "F" ? "Female" : ctx.Gender)}\n";
                if (!string.IsNullOrWhiteSpace(ctx.CardiovascularRisk))
                {
                    var label = ctx.CardiovascularRisk switch
                    {
                        "very_high"    => "VERY HIGH cardiovascular risk",
                        "high"         => "HIGH cardiovascular risk",
                        "low_moderate" => "LOW or MODERATE cardiovascular risk",
                        _              => "(unknown)"
                    };
                    patientBlock += $"- Declared cardiovascular risk: **{label}**\n";
                    patientBlock += "  Therefore the lipid-panel targets to use for THIS patient are:\n";
                    patientBlock += ctx.CardiovascularRisk switch
                    {
                        "very_high"    => "    * LDL-C target:    <55 mg/dL  (or <1.4 mmol/L)\n    * non-HDL target: <85 mg/dL  (or <2.2 mmol/L)\n    * Triglycerides target: <150 mg/dL\n",
                        "high"         => "    * LDL-C target:    <70 mg/dL  (or <1.8 mmol/L)\n    * non-HDL target: <100 mg/dL (or <2.6 mmol/L)\n    * Triglycerides target: <150 mg/dL\n",
                        "low_moderate" => "    * LDL-C target:    <100 mg/dL (or <2.6 mmol/L)\n    * non-HDL target: <130 mg/dL (or <3.4 mmol/L)\n    * Triglycerides target: <150 mg/dL\n",
                        _              => ""
                    };
                    patientBlock += "  When evaluating LDL-C, non-HDL or Triglycerides:\n";
                    patientBlock += "    * Use the target above as the SINGLE applicable threshold for this patient.\n";
                    patientBlock += "    * status='normal' if value is BELOW the target.\n";
                    patientBlock += "    * status='high' if value is AT OR ABOVE the target — even by a small margin.\n";
                    patientBlock += "    * Add it to abnormal_findings when status='high'. Severity = 'severe' for very_high risk, 'moderate' for high risk, 'mild' for low_moderate.\n";
                    patientBlock += "    * In 'reference_range' write: '<TARGET mg/dL — țintă pentru risc cardiovascular CATEGORY' (in the response language).\n";
                    patientBlock += "    * In 'explanation' EXPLICITLY mention the patient's declared CV-risk category and how that determined the chosen target.\n";
                    patientBlock += "    * In 'summary' AND 'recommendations' mention that the interpretation used the user-declared cardiovascular risk category.\n";
                    patientBlock += "    * Do NOT apply the multi-threshold-strictest-satisfied rule for these three parameters when a CV-risk category is declared — the category alone selects the target.\n";
                }
            }

            // Source-modality block: either tell the model the PDF is attached
            // visually, or embed the literally extracted text so the model never
            // has to OCR the digits itself (this is the key anti-hallucination move).
            string sourceBlock;
            string sourceInstruction;
            if (hasInlinePdf)
            {
                sourceBlock = $"The patient's medical PDF is attached as inline data (file name: {fileName}).";
                sourceInstruction = "Read the PDF visually and identify every section header it contains";
            }
            else
            {
                // Trim text to avoid blowing past Gemini's context budget on absurdly long PDFs.
                // 200_000 chars ~ 50k tokens, which is well within Gemini's 1M-token context.
                var text = extractedText ?? "";
                if (text.Length > 200_000) text = text[..200_000] + "\n...[truncated]";
                sourceBlock =
                    $"The patient's medical PDF has been parsed by a layout-aware text extractor (file name: {fileName})."
                    + " The extracted text is provided verbatim below between the <PDF_TEXT> tags."
                    + " VALUES, UNITS AND REFERENCE RANGES ARE LITERAL - DO NOT RE-READ OR SECOND-GUESS THEM."
                    + " If a digit looks unusual, TRUST IT - it is what the lab printed."
                    + " The extractor preserves visual row-and-column order, so each lab row appears as a sequence"
                    + " of tokens like: \"Parameter name <spaces> value <spaces> unit <spaces> reference range\"."
                    + "\n\n<PDF_TEXT>\n" + text + "\n</PDF_TEXT>";
                sourceInstruction =
                    "Read the extracted text above carefully and identify every section header it contains";
            }

            return $@"RESPONSE LANGUAGE: {languageName} (code: {languageCode})

{sourceBlock}
{patientBlock}
Task:
1. {sourceInstruction} (Hematology, Biochemistry, Immunochemistry, Lipid panel, Coagulation, ESR/VSH, Urinalysis, Hormones, Tumor markers, Vitamins, etc.).
2. For each section, extract EVERY measured parameter with its value, unit and reference range exactly as printed. Pay extra attention to Immunochemistry (hormones, tumor markers, vitamins) which is often forgotten.
3. Apply the value-vs-reference pairing rules from the system instructions (WBC differential, age-dependent ranges, dual-unit rows, mismatched magnitudes).
4. Determine each parameter's status (normal/high/low/borderline).
5. If a cardiovascular-risk category was declared above, USE THE PROVIDED LIPID TARGETS for LDL-C, non-HDL and Triglycerides INSTEAD OF the multi-threshold rule, and explicitly mention the declared risk category in 'summary', 'explanation' for those parameters, and 'recommendations'.
6. For EVERY parameter in 'key_results', also emit the three LOINC fields described in the system instructions: 'loinc_code', 'loinc_long_name', 'loinc_confidence'. The keys MUST be present in every entry; their value may be null when you are not sure (do NOT invent codes). Example: ""loinc_code"": ""2324-2"", ""loinc_long_name"": ""Gamma glutamyl transferase [Enzymatic activity/volume] in Serum or Plasma"", ""loinc_confidence"": ""high"".
7. Produce the structured JSON object exactly per the schema in the system instructions, written entirely in {languageName}. Do NOT wrap it in markdown fences.";
        }
    }
}
