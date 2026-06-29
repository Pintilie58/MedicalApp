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

        /// <summary>
        /// SECOND-tier fallback used only when both the primary AND the first
        /// fallback have exhausted their retry budgets. This is a "safety net"
        /// (e.g. <c>gemini-3.1-pro-preview</c>, Google's recommended preview as
        /// of Feb 2026): it should very rarely be reached, but keeps the batch
        /// moving instead of marking the file as NotSends. Set to <c>null</c>
        /// or empty to disable.
        ///
        /// NOTE: Google occasionally retires preview models (e.g. the older
        /// "gemini-3-pro-preview" was retired in Feb 2026). When that happens,
        /// the Gemini API returns HTTP 404 "no longer available" — the service
        /// catches this with <see cref="GeminiModelRetiredException"/> and the
        /// CAM batch falls through cleanly to non-transient handling instead
        /// of pointlessly retrying. Keep this value in sync with the model
        /// list at https://ai.google.dev/gemini-api/docs/models .
        /// </summary>
        public string? SecondaryFallbackModel { get; set; } = "gemini-3.1-pro-preview";

        public int MaxOutputTokens { get; set; } = 32000;
        public float Temperature { get; set; } = 0.0f;
        public int TimeoutSeconds { get; set; } = 600;

        /// <summary>
        /// Enables the independent post-Gemini completeness audit
        /// (<see cref="InterpretationCompletenessAuditor"/>): a heuristic
        /// cross-check between Gemini's <c>key_results.Count</c> and the
        /// row-count detected in the PDF's text layer (PdfPig). The audit
        /// only LOGS warnings — it never modifies the interpretation. Set to
        /// <c>false</c> in <c>appsettings.json</c> to silence the warnings.
        /// </summary>
        public bool CompletenessAuditEnabled { get; set; } = true;
    }
}
