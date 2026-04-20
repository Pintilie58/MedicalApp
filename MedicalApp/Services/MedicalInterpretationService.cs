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

            var options = new ChatCompletionOptions
            {
                Temperature = _settings.Temperature,
                MaxOutputTokenCount = _settings.MaxOutputTokens,
                ResponseFormat = ChatResponseFormat.CreateJsonObjectFormat()
            };

            var messages = new List<ChatMessage>
            {
                new SystemChatMessage(systemPrompt),
                new UserChatMessage(userPrompt)
            };

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(_settings.TimeoutSeconds));

            _logger.LogInformation("Calling OpenAI {Model} for {Language} interpretation. Text length: {Length}",
                _settings.Model, languageCode, extractedText.Length);

            ChatCompletion completion = await client.CompleteChatAsync(messages, options, cts.Token);

            string responseText = completion.Content.Count > 0 ? completion.Content[0].Text : string.Empty;

            var inputTokens = completion.Usage?.InputTokenCount ?? 0;
            var outputTokens = completion.Usage?.OutputTokenCount ?? 0;

            _logger.LogInformation("OpenAI response received. Tokens in={In} out={Out}.", inputTokens, outputTokens);

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
8. Respond ONLY with a valid JSON object - no surrounding text, no markdown code fences.
9. Provide a DETAILED interpretation (not a short one) - include as many key_results as are present, write thorough explanations.

DETECTING NON-MEDICAL FILES:
If the text received is NOT a medical analysis (wrong document type, empty text, unintelligible, or clearly not a lab result), set ""is_medical_analysis"": false and explain the reason in ""rejection_reason"". Do NOT try to interpret non-medical content.

JSON RESPONSE SCHEMA (return EXACTLY this structure, no extra fields):
{
  ""is_medical_analysis"": boolean,
  ""rejection_reason"": string | null,
  ""patient_info"": {
    ""name"": string | null,
    ""age"": string | null,
    ""sex"": string | null,
    ""date_taken"": string | null,
    ""laboratory"": string | null,
    ""doctor_requesting"": string | null
  },
  ""summary"": string,
  ""key_results"": [
    {
      ""parameter"": string,
      ""value"": string,
      ""unit"": string,
      ""reference_range"": string,
      ""status"": ""normal"" | ""high"" | ""low"" | ""borderline"",
      ""explanation"": string
    }
  ],
  ""abnormal_findings"": [
    {
      ""parameter"": string,
      ""explanation"": string,
      ""severity"": ""mild"" | ""moderate"" | ""severe""
    }
  ],
  ""correlations"": string,
  ""recommendations"": string,
  ""disclaimer"": string
}

CONTENT GUIDELINES:
- ""summary"": 2-3 sentences overviewing what was analyzed.
- ""key_results"": ALL measured parameters found in the text. Each with clear, simple explanation.
- ""abnormal_findings"": EVERY value outside the normal range. Explain possible causes in educational terms.
- ""correlations"": explain meaningful combinations (e.g. low hemoglobin + low ferritin = possible iron-deficiency anemia).
- ""recommendations"": general lifestyle/dietary advice, when to repeat tests, when to see a doctor. NO specific medications or doses.
- ""disclaimer"": explicit statement that this is educational only, NOT a medical diagnosis, and a qualified doctor must be consulted.";

        private static string BuildUserPrompt(string extractedText, string languageName, string languageCode) =>
            $@"RESPONSE LANGUAGE: {languageName} (code: {languageCode})

Text extracted from the patient's PDF analysis:
===
{extractedText}
===

Return ONLY the JSON object per the specified schema, written entirely in {languageName}.";
    }
}
