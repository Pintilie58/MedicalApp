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
9. NEVER emit LOINC numeric codes. The ""key_results"" objects must NOT contain
   ""loinc_code"", ""loinc_long_name"" or ""loinc_confidence"" fields. Instead, you
   provide a clean standardized English term in ""parameter_normalized_en"".
   A downstream deterministic matcher resolves the LOINC code from that term.

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
PARAMETER NORMALIZATION (per parameter) — MANDATORY FIELD
==========================================================
For EVERY entry in ""key_results"" you MUST emit ONE additional field:

  ""parameter_normalized_en"": string|null

What it is:
  The STANDARDIZED ENGLISH MEDICAL TERM for the analyte being measured,
  using LOINC-style canonical naming. Think of it as the test name a doctor
  would use in an English-language medical journal article.

What it is NOT:
  - It is NOT a LOINC code (you must NEVER emit LOINC numeric codes anymore).
  - It is NOT a translation of the raw parameter name; it is a
    STANDARDIZATION into canonical medical terminology.
  - It is NOT a paraphrase: keep the analyte head term, sample type, and
    measurement property explicit.

How to build it (template):
  ""<Analyte name in English> [<Property>] in <Specimen>[ by <Method>]""

Worked examples (these are the format we expect — adapt to YOUR parameter):
  ""GGT"" / ""Gamma GT"" / ""Glutamiltranspeptidaza""
      -> ""Gamma glutamyl transferase [Enzymatic activity/volume] in Serum or Plasma""
  ""VSH"" / ""VS"" / ""Vitesse de sédimentation""
      -> ""Erythrocyte sedimentation rate""
  ""Glicemie"" / ""Glucoza"" (in biochemistry panel, blood-derived)
      -> ""Glucose [Mass/volume] in Serum or Plasma""
  ""Glucoza (urina)"" (in urinalysis dipstick section)
      -> ""Glucose [Mass/volume] in Urine by Test strip""
  ""Hemoglobina"" (in CBC)
      -> ""Hemoglobin [Mass/volume] in Blood""
  ""Hemoglobina urinară"" (in urinalysis dipstick)
      -> ""Hemoglobin [Presence] in Urine by Test strip""
  ""TSH""
      -> ""Thyrotropin [Units/volume] in Serum or Plasma""
  ""FT4"" / ""T4 libre""
      -> ""Thyroxine free [Mass/volume] in Serum or Plasma""
  ""LDL"" / ""LDL-Cholesterol""
      -> ""Cholesterol in LDL [Mass/volume] in Serum or Plasma""
  ""Non-HDL cholesterol""
      -> ""Cholesterol non HDL [Mass/volume] in Serum or Plasma""
  ""Colesterol total""
      -> ""Cholesterol [Mass/volume] in Serum or Plasma""
  ""ALT"" / ""SGPT"" / ""TGP""
      -> ""Alanine aminotransferase [Enzymatic activity/volume] in Serum or Plasma""
  ""AST"" / ""SGOT"" / ""TGO""
      -> ""Aspartate aminotransferase [Enzymatic activity/volume] in Serum or Plasma""
  ""Creatinina serica""
      -> ""Creatinine [Mass/volume] in Serum or Plasma""
  ""eGFR"" / ""DFG"" / ""Rata estimata a filtrarii glomerulare""
      -> ""Glomerular filtration rate/1.73 sq M.predicted in Serum, Plasma or Blood by Creatinine-based formula""
  ""Densitate urinară"" / ""Urine specific gravity""
      -> ""Specific gravity of Urine""
  ""pH urinar"" (dipstick)
      -> ""pH of Urine by Test strip""
  ""Procentul de protrombină"" / ""Quick %""
      -> ""Prothrombin time (PT) actual/normal""
  ""INR""
      -> ""INR in Platelet poor plasma by Coagulation assay""
  ""Anti-tiroglobulină"" / ""Anti-Tg""
      -> ""Thyroglobulin Ab [Units/volume] in Serum""
  ""Calcitonina""
      -> ""Calcitonin [Mass/volume] in Serum or Plasma""
  ""Neutrofile"" absolute count -> ""Neutrophils [#/volume] in Blood""
  ""Neutrofile %"" -> ""Neutrophils/100 leukocytes in Blood""
  ""Limfocite"" absolute count -> ""Lymphocytes [#/volume] in Blood""
  ""Limfocite %"" -> ""Lymphocytes/100 leukocytes in Blood""
  ""Monocite"" absolute count -> ""Monocytes [#/volume] in Blood""
  ""Monocite %"" -> ""Monocytes/100 leukocytes in Blood""
  ""Eozinofile"" absolute count -> ""Eosinophils [#/volume] in Blood""
  ""Eozinofile %"" -> ""Eosinophils/100 leukocytes in Blood""
  ""Bazofile"" absolute count -> ""Basophils [#/volume] in Blood""
  ""Bazofile %"" -> ""Basophils/100 leukocytes in Blood""
  ""Hematocrit"" -> ""Hematocrit [Volume Fraction] of Blood""
  ""MCV"" / ""Volum eritrocitar mediu"" / ""Volumul mediu eritrocitar"" / ""Mean corpuscular volume""
      -> ""Erythrocyte mean corpuscular volume [Entitic volume] by Automated count""
  ""MCH"" / ""Hemoglobina eritrocitara medie"" / ""Mean corpuscular hemoglobin"" / ""Hb medie pe eritrocit""
      -> ""Erythrocyte mean corpuscular hemoglobin [Entitic mass] by Automated count""
  ""MCHC"" / ""Concentratia medie de hemoglobina"" / ""Mean corpuscular hemoglobin concentration"" / ""Concentratie medie Hb""
      -> ""Erythrocyte mean corpuscular hemoglobin concentration [Mass/volume] by Automated count""
  ""RDW"" / ""Largimea curbei de distributie eritrocitara"" / ""Red cell distribution width"" / ""Indice distributie eritrocitara""
      -> ""Erythrocyte distribution width [Ratio] by Automated count""
  ""MPV"" / ""Volum trombocitar mediu"" / ""Mean platelet volume""
      -> ""Platelet mean volume [Entitic volume] in Blood by Automated count""
  ""PDW"" / ""Largimea curbei de distributie trombocitara"" / ""Platelet distribution width""
      -> ""Platelet distribution width [Ratio] in Blood""
  ""PCT"" / ""Plachetocrit"" / ""Plateletcrit""
      -> ""Plateletcrit [Volume Fraction] in Blood""
  ""Celule epiteliale plate"" / urine sediment
      -> ""Epithelial cells [#/area] in Urine sediment by Microscopy high power field""
  ""Urobilinogen urinar"" (dipstick)
      -> ""Urobilinogen [Mass/volume] in Urine by Test strip""

GUIDELINES — keep these in mind when building parameter_normalized_en:
  1. Use the LOINC English vocabulary where you know it (Urate, not Uric acid;
     Thyrotropin, not TSH; Thyroglobulin Ab, not anti-Tg antibodies).
  2. ALWAYS include the SPECIMEN explicitly (""in Serum or Plasma"", ""in Blood"",
     ""in Urine by Test strip"", ""in Urine sediment"", ""in CSF""). Specimen is
     the most common reason a downstream matcher picks the wrong code.
  3. Use the section context of the PDF to discriminate ambiguous names. A
     parameter ""Glucoza"" inside the urinalysis dipstick block is ""Glucose in
     Urine by Test strip"", NOT ""Glucose in Serum or Plasma"".
  4. Prefer the COMMON canonical form, not an exotic method-specific one.
     If the lab printed ""Hemoglobina"" without specifying method, emit
     ""Hemoglobin [Mass/volume] in Blood"" (not the specific cyanmethemoglobin
     variant).
  5. For derived parameters that have no LOINC equivalent (e.g. ""HOMA-IR"",
     proprietary panels, in-house ratios), emit a descriptive English name:
     ""HOMA-IR insulin resistance index"" — leave it as plain text, the
     downstream matcher will either find a code or skip.
  6. If you genuinely cannot map the parameter to a meaningful English
     medical term, emit null. The downstream pipeline will handle it.

CRITICAL: do NOT emit ""loinc_code"", ""loinc_long_name"" or ""loinc_confidence""
fields at all. Those fields are now resolved by a deterministic post-processing
step in the application; if you include them, they will be discarded. Your job
is ONLY to provide a clean, standardized English name for each analyte.

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
  ""key_results"": [ { ""parameter"": string, ""value"": string, ""unit"": string, ""reference_range"": string, ""status"": ""normal""|""high""|""low""|""borderline"", ""explanation"": string, ""parameter_normalized_en"": string|null } ],
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
6. For EVERY parameter in 'key_results', also emit the field 'parameter_normalized_en' as described in the system instructions: a clean standardized English medical term for the analyte (with explicit specimen). Example: parameter=""Glicemie"" -> ""parameter_normalized_en"": ""Glucose [Mass/volume] in Serum or Plasma"". Do NOT emit ""loinc_code"", ""loinc_long_name"" or ""loinc_confidence"" — those are resolved downstream.
7. Produce the structured JSON object exactly per the schema in the system instructions, written entirely in {languageName}. Do NOT wrap it in markdown fences.";
        }
    }
}
