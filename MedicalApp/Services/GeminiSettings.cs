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
        /// Base of the Gemini REST API. Only ever changed to point the app at a
        /// local stub in tests; production keeps the default.
        /// </summary>
        public string BaseUrl { get; set; } = "https://generativelanguage.googleapis.com/v1beta";

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
        /// Token budget Gemini may spend "thinking" before it starts writing.
        /// Those thought tokens are generated like any other output token, so they
        /// are paid in wall-clock time — on a 20.000-token report they can be a
        /// large slice of the ~150 tokens/second generation speed.
        /// <list type="bullet">
        ///   <item><c>-1</c> (default): key is NOT sent — Gemini's own dynamic
        ///     thinking, i.e. exactly the behaviour before this setting existed.</item>
        ///   <item><c>0</c>: thinking disabled (fastest; 2.5 Flash/Flash-Lite only,
        ///     2.5 Pro cannot disable it).</item>
        ///   <item><c>256..24576</c>: capped thinking — keeps some reasoning for the
        ///     correlations section while bounding the latency.</item>
        /// </list>
        /// Measure before choosing: the actual thought tokens per call are logged
        /// and shown in the Admin performance panel.
        /// </summary>
        public int ThinkingBudget { get; set; } = -1;

        // =====================================================================
        //  SPLIT PIPELINE (3 parallel-ish calls instead of one monolithic one)
        // =====================================================================
        /// <summary>
        /// "monolithic" (default, the historical single-call behaviour) or
        /// "split": stage A extracts the table, then stage B (per-analyte
        /// explanations, in parallel batches) and stage C (clinical narrative,
        /// with real thinking) run CONCURRENTLY. Total time becomes
        /// A + max(B, C) instead of the sum of everything.
        /// Any failure of the split path silently falls back to the monolithic
        /// call, so switching this on can never leave a user without a report.
        /// </summary>
        public string PipelineMode { get; set; } = "monolithic";

        /// <summary>Stage A — table extraction. Speed + reading accuracy, no reasoning needed.</summary>
        public string ExtractorModel { get; set; } = "gemini-2.5-flash";

        /// <summary>Stage B — per-analyte explanations. High volume of short texts.</summary>
        public string ExplainModel { get; set; } = "gemini-2.5-flash";

        /// <summary>Stage C — narrative, correlations, recommendations. Real reasoning; tiny input.</summary>
        public string NarrativeModel { get; set; } = "gemini-2.5-pro";

        /// <summary>thinkingLevel for Gemini 3.x: minimal | low | medium | high.</summary>
        public string ExtractorThinkingLevel { get; set; } = "low";
        public string ExplainThinkingLevel { get; set; } = "minimal";
        public string NarrativeThinkingLevel { get; set; } = "medium";

        /// <summary>How many analytes one stage-B call explains. Batches run in parallel.</summary>
        public int ExplainBatchSize { get; set; } = 12;

        /// <summary>
        /// Stage A2 — completeness sweep. A second reading pass that runs IN
        /// PARALLEL with B and C: it receives the report plus the list of names
        /// already extracted and returns ONLY what stage A missed. Costs almost
        /// no wall-clock time and fixes the historical "83 of 84 analytes" loss.
        /// </summary>
        public bool EnableCompletenessSweep { get; set; } = true;

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
