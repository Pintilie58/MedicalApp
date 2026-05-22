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

        /// <summary>
        /// Optional fallback model used by the controller AFTER the primary
        /// <see cref="Model"/> has produced repeated HTTP 503 (server overload)
        /// errors. The fallback model should be a LESS-USED variant that is
        /// usually less congested — e.g. <c>gemini-2.5-pro</c>. Set to null
        /// or empty to disable the fallback (controller will just keep
        /// retrying the primary model).
        ///
        /// Cost note (Feb 2026): gemini-2.5-pro is roughly 5x more expensive
        /// than gemini-2.5-flash per output token. We only switch to it
        /// after the primary already failed twice, so the typical user pays
        /// the flash price; only on heavy global congestion days we incur
        /// the pro cost — which is acceptable as an insurance against a
        /// completely failed interpretation.
        /// </summary>
        public string? FallbackModel { get; set; } = "gemini-2.5-pro";

        public int MaxOutputTokens { get; set; } = 32000;
        public float Temperature { get; set; } = 0.0f;
        public int TimeoutSeconds { get; set; } = 300;
    }
}
