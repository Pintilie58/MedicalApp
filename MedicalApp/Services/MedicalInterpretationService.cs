using MedicalApp.Models;
using Microsoft.Extensions.Options;
using OpenAI.Chat;
using System.ClientModel;
using System.Text.Json;

namespace MedicalApp.Services
{
    public class MedicalInterpretationService : IMedicalInterpretationService
    {
        private readonly OpenAISettings _settings;
        private readonly ILogger<MedicalInterpretationService> _logger;

        private static readonly Dictionary<string, string> LanguageNames = new()
        {
            ["en"] = "English",
            ["ro"] = "Romanian (Română)",
            ["fr"] = "French (Français)",
            ["es"] = "Spanish (Español)",
            ["de"] = "German (Deutsch)"
        };

        public MedicalInterpretationService(
            IOptions<OpenAISettings> options,
            ILogger<MedicalInterpretationService> logger)
        {
            _settings = options.Value;
            _logger = logger;
        }

        public async Task<(InterpretationResult Result, int InputTokens, int OutputTokens, string RawResponse)> InterpretAsync(
            string extractedText, string languageCode, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(_settings.ApiKey))
                throw new InvalidOperationException("OpenAI API key is not configured. Set OpenAI:ApiKey in appsettings.json or User Secrets.");

            var languageName = LanguageNames.TryGetValue(languageCode, out var n) ? n : "English";

            var systemPrompt = BuildSystemPrompt();
            var userPrompt = BuildUserPrompt(extractedText, languageName, languageCode);

            var client = new ChatClient(_settings.Model, new ApiKeyCredential(_settings.ApiKey));

            // Strict JSON Schema (OpenAI "Structured Outputs") - guarantees the model
            // cannot skip required fields and cannot invent additional fields.
            var schemaJson = BuildJsonSchema();

            var options = new ChatCompletionOptions
            {
                Temperature = _settings.Temperature,
                MaxOutputTokenCount = _settings.MaxOutputTokens,
                Seed = _settings.Seed,
                ResponseFormat = ChatResponseFormat.CreateJsonSchemaFormat(
                    jsonSchemaFormatName: "medical_interpretation",
                    jsonSchema: BinaryData.FromString(schemaJson),
                    jsonSchemaFormatDescription: "Structured interpretation of a medical laboratory analysis.",
                    jsonSchemaIsStrict: true)
            };

            var messages = new List<ChatMessage>
            {
                new SystemChatMessage(systemPrompt),
                new UserChatMessage(userPrompt)
            };

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(_settings.TimeoutSeconds));

            _logger.LogInformation("Calling OpenAI {Model} for {Language} interpretation. Text length: {Length}, Temperature: {Temp}, Seed: {Seed}",
                _settings.Model, languageCode, extractedText.Length, _settings.Temperature, _settings.Seed);

            ChatCompletion completion = await client.CompleteChatAsync(messages, options, cts.Token);

            string responseText = completion.Content.Count > 0 ? completion.Content[0].Text : string.Empty;

            var inputTokens = completion.Usage?.InputTokenCount ?? 0;
            var outputTokens = completion.Usage?.OutputTokenCount ?? 0;

            _logger.LogInformation("OpenAI response received. Tokens in={In} out={Out}. FinishReason={Finish}",
                inputTokens, outputTokens, completion.FinishReason);

            if (completion.FinishReason == ChatFinishReason.Length)
            {
                _logger.LogWarning("OpenAI hit MaxOutputTokenCount ({Max}) - response may be truncated. Consider increasing OpenAI:MaxOutputTokens.",
                    _settings.MaxOutputTokens);
            }

            InterpretationResult? result;
            try
            {
                result = JsonSerializer.Deserialize<InterpretationResult>(responseText, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });
            }
            catch (JsonException ex)
            {
                _logger.LogError(ex, "Failed to parse OpenAI JSON response. Response: {Response}", responseText);
                throw new InvalidOperationException("The AI returned an unparseable response. Please try again.", ex);
            }

            if (result == null)
                throw new InvalidOperationException("The AI returned an empty response.");

            return (result, inputTokens, outputTokens, responseText);
        }

        // =============================================================================
        // PROMPT (system) - carefully crafted for medical analysis interpretation
        // =============================================================================
        private static string BuildSystemPrompt() => @"You are MedicalApp's medical analysis interpretation assistant. Your role is to provide an EDUCATIONAL and INFORMATIVE interpretation of laboratory medical results.

STRICT RULES - you MUST follow ALL of them:
1. Respond ENTIRELY in the language specified by the user in their message.
2. You are NOT a doctor. You do NOT give medical diagnoses.
3. You do NOT recommend specific medications, doses, or treatments.
4. You ALWAYS recommend consulting a qualified physician.
5. You do NOT invent values - if a parameter is missing from the text, do NOT include it.
6. Use an empathetic, professional, CALM tone - never alarming.
7. Use SIMPLE language, accessible to a non-medical reader.
8. Provide a DETAILED interpretation (not a short one) - include thorough explanations.

==========================================================
EXTRACTION COMPLETENESS - THIS IS THE MOST IMPORTANT RULE
==========================================================
The JSON field is named ""key_results"" for legacy technical reasons.
IT DOES NOT MEAN ""important results"" OR ""selected results"".
IT MEANS ""THE COMPLETE LIST OF EVERY SINGLE LAB RESULT FOUND IN THE TEXT"".

- You MUST include in ""key_results"" EVERY single measured parameter that appears in the extracted text, WITHOUT EXCEPTION.
- NEVER skip a parameter. Not because it is normal. Not because it is not interesting. Not because it is a ""less important"" test. NEVER.
- Do NOT summarize or group multiple parameters into one entry - one row per parameter.
- Preserve the EXACT parameter name, value, unit and reference range as they appear in the source text.

METHODICAL PROCESSING (follow this algorithm, do not deviate):
Lab reports are typically organized in SECTIONS with section headers such as:
    * Hematologie / Hematology        (Hb, Ht, leukocytes, erythrocytes, platelets, neutrophils, lymphocytes, etc.)
    * Coagulare / Coagulation         (Timp de protrombina / Quick, INR, APTT, Fibrinogen, etc.)
    * VSH / ESR
    * Biochimie / Biochemistry        (glucose, urea, creatinine, AST, ALT, GGT, bilirubin, uric acid, electrolytes, CK, amylase, lipase, etc.)
    * Imunochimie / Immunochemistry   (!!! THYROID HORMONES: TSH, FT3, FT4, T3, T4; TUMOR MARKERS: PSA, CEA, AFP, CA 19-9, CA 125, CA 15-3; CARDIAC: troponin, BNP; REPRODUCTIVE: LH, FSH, estradiol, testosterone, progesterone, prolactin; VITAMINS: vitamin D, B12, folate, ferritin; insulin, C-peptide, cortisol, etc.)
    * Profil lipidic / Lipid panel    (total cholesterol, HDL, LDL, VLDL, non-HDL, triglycerides, total lipids)
    * Urinar / Urinalysis             (pH, density, protein, glucose, ketones, blood, leukocytes, nitrites, microscopy, etc.)
    * Serologie / Serology            (Hepatitis markers, HIV, syphilis, etc.)
    * Microbiologie / Microbiology    (cultures, sensitivity tests, etc.)
    * Hormoni / Hormones (standalone section with any hormone not listed above)
    * Markeri tumorali / Tumor markers (standalone section)
    * Vitamine / Vitamins (standalone section)

MANDATORY ALGORITHM (execute strictly):
  Step 1. Read the entire text from start to end and identify EVERY section header.
  Step 2. For EACH section you identified, list internally every parameter that belongs to it and its value.
  Step 3. MERGE all those lists into ""key_results"" - preserving original order.
  Step 4. Before finalizing, COUNT how many numeric measurements exist in the source text.
          If your ""key_results"" array has fewer entries than that count, GO BACK to step 2 - you missed something.
  Step 5. In particular, VERIFY that you did NOT skip the Imunochimie / Immunochemistry section:
          if the source text contains TSH, FT3, FT4, PSA, or any hormone / tumor marker / vitamin,
          those MUST appear in ""key_results"".

STATUS FIELD:
  The ""status"" field must be computed from the actual value vs. the reference range:
    * ""normal""     => value falls inside the reference range
    * ""high""       => value is above the reference range
    * ""low""        => value is below the reference range
    * ""borderline"" => value is at the exact limit of the reference range, or within ~5% of a limit
  EVERY parameter whose status is ""high"", ""low"" or ""borderline"" MUST also appear as an entry in ""abnormal_findings"".

DETECTING NON-MEDICAL FILES:
If the text received is NOT a medical analysis (wrong document type, empty text, unintelligible, or clearly not a lab result), set ""is_medical_analysis"": false and explain the reason in ""rejection_reason"". In that case set ""key_results"" and ""abnormal_findings"" to empty arrays, and keep ""summary"", ""correlations"", ""recommendations"", ""disclaimer"" as short explanatory strings.

CONTENT GUIDELINES:
- ""summary"": 2-3 sentences overviewing what was analyzed.
- ""key_results"": THE COMPLETE LIST (re-read the rule above) of ALL measured parameters. Each with a clear, simple explanation.
- ""abnormal_findings"": EVERY value outside the normal range. Explain possible causes in educational terms.
- - """"correlations"""": this field MUST be substantial and detailed. Write at LEAST 3-5 full sentences (minimum 300 characters). Explain meaningful combinations between the abnormal and borderline values found, such as: (a) metabolic patterns (e.g. elevated glucose + high HOMA index + elevated triglycerides may suggest insulin resistance / metabolic syndrome); (b) hepatobiliary patterns (e.g. elevated GGT + elevated ALT/AST may suggest liver stress); (c) inflammatory patterns (e.g. elevated ESR/VSH + elevated leukocytes may suggest inflammation/infection); (d) anemia patterns (e.g. low hemoglobin + low ferritin = possible iron-deficiency anemia); (e) lipid / cardiovascular risk patterns (elevated non-HDL, LDL, triglycerides together). If no strong correlations exist, still explain in 2-3 sentences what combinations were examined and why they are NOT concerning. Never write a single short sentence - the user expects a thorough analysis."",
- """"recommendations"""": this field MUST also be substantial and detailed. Write at LEAST 4-6 full sentences (minimum 400 characters), structured in clear practical paragraphs. Cover these aspects where relevant to the patient's findings: (a) LIFESTYLE - concrete suggestions regarding physical activity, sleep, stress management, smoking, alcohol (mention only what is generally recommended - NO medical prescriptions); (b) DIET - specific nutritional suggestions tied to the abnormal values found (e.g. for high cholesterol: reduce saturated fats, more fiber, omega-3; for insulin resistance: reduce refined sugars, increase fiber; for high uric acid: limit purine-rich foods); (c) FOLLOW-UP TESTS - suggest which specific tests should be repeated and in what timeframe (e.g. 'repeat lipid panel in 3 months', 'repeat fasting glucose + HbA1c in 3 months'); (d) WHEN TO SEE A DOCTOR - clearly state the patient should show these results to a qualified physician, and mention which specialist(s) may be relevant based on findings (e.g. endocrinolog for thyroid/glucose, cardiolog for lipid profile, gastroenterolog for liver enzymes). NEVER recommend specific medications, doses, or supplements - only general educational advice."",- ""disclaimer"": explicit statement that this is educational only, NOT a medical diagnosis, and a qualified doctor must be consulted.

OUTPUT FORMAT:
- Respond ONLY with a JSON object conforming to the ""medical_interpretation"" schema provided by the API.
- Do NOT wrap the JSON in markdown code fences.
- Do NOT add any commentary before or after the JSON.";

        private static string BuildUserPrompt(string extractedText, string languageName, string languageCode) =>
            $@"RESPONSE LANGUAGE: {languageName} (code: {languageCode})

Text extracted from the patient's PDF analysis:
===
{extractedText}
===

Task (follow the MANDATORY ALGORITHM from your system instructions, do not skip any step):
1. Identify EVERY section header present in the text above (Hematologie, Biochimie, Imunochimie, Profil lipidic, Coagulare, VSH, Urinar, etc.).
2. For EACH section, extract EVERY measured parameter (do NOT skip any - pay SPECIAL ATTENTION to Imunochimie / hormones / tumor markers / vitamins, which are frequently forgotten).
3. For each parameter determine status vs. its reference range.
4. Count the numeric measurements in the text - your ""key_results"" array must have AT LEAST that many entries.
5. Produce the full structured JSON per the ""medical_interpretation"" schema, written entirely in {languageName}.";

        // =============================================================================
        // JSON SCHEMA for OpenAI Structured Outputs (strict = true)
        // Rules:
        //  * every property must be listed in "required"
        //  * "additionalProperties" must be false on every object
        //  * nullable fields use {"type": ["string", "null"]}
        // =============================================================================
        private static string BuildJsonSchema() => /*lang=json,strict*/ """
{
  "type": "object",
  "additionalProperties": false,
  "required": [
    "is_medical_analysis",
    "rejection_reason",
    "patient_info",
    "summary",
    "key_results",
    "abnormal_findings",
    "correlations",
    "recommendations",
    "disclaimer"
  ],
  "properties": {
    "is_medical_analysis": { "type": "boolean" },
    "rejection_reason":    { "type": ["string", "null"] },
    "patient_info": {
      "type": "object",
      "additionalProperties": false,
      "required": ["name", "age", "sex", "date_taken", "laboratory", "doctor_requesting"],
      "properties": {
        "name":              { "type": ["string", "null"] },
        "age":               { "type": ["string", "null"] },
        "sex":               { "type": ["string", "null"] },
        "date_taken":        { "type": ["string", "null"] },
        "laboratory":        { "type": ["string", "null"] },
        "doctor_requesting": { "type": ["string", "null"] }
      }
    },
    "summary": { "type": "string" },
    "key_results": {
      "type": "array",
      "items": {
        "type": "object",
        "additionalProperties": false,
        "required": ["parameter", "value", "unit", "reference_range", "status", "explanation"],
        "properties": {
          "parameter":       { "type": "string" },
          "value":           { "type": "string" },
          "unit":            { "type": "string" },
          "reference_range": { "type": "string" },
          "status":          { "type": "string", "enum": ["normal", "high", "low", "borderline"] },
          "explanation":     { "type": "string" }
        }
      }
    },
    "abnormal_findings": {
      "type": "array",
      "items": {
        "type": "object",
        "additionalProperties": false,
        "required": ["parameter", "explanation", "severity"],
        "properties": {
          "parameter":   { "type": "string" },
          "explanation": { "type": "string" },
          "severity":    { "type": "string", "enum": ["mild", "moderate", "severe"] }
        }
      }
    },
    "correlations":    { "type": "string" },
    "recommendations": { "type": "string" },
    "disclaimer":      { "type": "string" }
  }
}
""";
    }
}
