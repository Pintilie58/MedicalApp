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

        public async Task<(InterpretationResult Result, int InputTokens, int OutputTokens)> InterpretAsync(
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

            return (result, inputTokens, outputTokens);
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

EXTRACTION COMPLETENESS - CRITICAL:
- You MUST include in ""key_results"" EVERY single measured parameter that appears in the extracted text, WITHOUT EXCEPTION.
- Do NOT skip parameters because their value is normal, borderline, high, or low.
- Do NOT skip parameters because you consider them less important.
- Do NOT summarize or group multiple parameters into one entry.
- Iterate through the extracted text systematically, top to bottom, and for each numeric lab result (with a value and/or a reference range) produce one entry in ""key_results"".
- Preserve the EXACT parameter name, value, unit and reference range as they appear in the source text (do not round, convert or rename units).
- The ""status"" field must be computed from the actual value vs. the reference range in the source text:
    * ""normal""     => value falls inside the reference range
    * ""high""       => value is above the reference range
    * ""low""        => value is below the reference range
    * ""borderline"" => value is at the exact limit of the reference range, or within ~5% of a limit
- EVERY parameter whose status is ""high"", ""low"" or ""borderline"" MUST also appear as an entry in ""abnormal_findings"".
- If ""key_results"" ends up with fewer entries than the number of measured parameters visible in the text, you have failed the task.

DETECTING NON-MEDICAL FILES:
If the text received is NOT a medical analysis (wrong document type, empty text, unintelligible, or clearly not a lab result), set ""is_medical_analysis"": false and explain the reason in ""rejection_reason"". In that case set ""key_results"" and ""abnormal_findings"" to empty arrays, and keep ""summary"", ""correlations"", ""recommendations"", ""disclaimer"" as short explanatory strings.

CONTENT GUIDELINES:
- ""summary"": 2-3 sentences overviewing what was analyzed.
- ""key_results"": ALL measured parameters found in the text. Each with clear, simple explanation.
- ""abnormal_findings"": EVERY value outside the normal range. Explain possible causes in educational terms.
- ""correlations"": explain meaningful combinations (e.g. low hemoglobin + low ferritin = possible iron-deficiency anemia).
- ""recommendations"": general lifestyle/dietary advice, when to repeat tests, when to see a doctor. NO specific medications or doses.
- ""disclaimer"": explicit statement that this is educational only, NOT a medical diagnosis, and a qualified doctor must be consulted.

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

Task:
1. Extract EVERY measured lab parameter from the text above (do NOT skip any).
2. For each one, determine status vs. its reference range.
3. Produce the full structured JSON per the ""medical_interpretation"" schema, written entirely in {languageName}.";

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
