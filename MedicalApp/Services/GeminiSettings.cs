namespace MedicalApp.Services
{
    /// <summary>
    /// Configured via appsettings.json -> "Gemini".
    /// API key MUST be stored in .NET User Secrets (or env vars), never in appsettings.json.
    ///
    /// Free tier (as of Feb 2026) for gemini-2.5-flash:
    ///   - 15 requests/minute
    ///   - 1500 requests/day
    ///   - 1,000,000 tokens/minute
    /// </summary>
    public class GeminiSettings
    {
        public string ApiKey { get; set; } = string.Empty;
        public string Model { get; set; } = "gemini-2.5-flash";
        public int MaxOutputTokens { get; set; } = 32000;
        public float Temperature { get; set; } = 0.0f;
        public int TimeoutSeconds { get; set; } = 300;
    }
}
