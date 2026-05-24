namespace MedicalApp.Services
{
    /// <summary>
    /// Configured via appsettings.json -> "GeminiPricing".
    /// Per-million-token prices for each Gemini variant, in USD. Used by the
    /// admin dashboard to estimate "money spent on the AI in the last 30 days"
    /// and to highlight the Flash -> Pro fallback cost ratio.
    ///
    /// Defaults reflect Google's public Gemini 2.5 pricing as of Feb 2026
    /// (paid tier, &lt; 200K input tokens band):
    ///   - Flash:  $0.30 / 1M input,  $2.50 / 1M output
    ///   - Pro:    $1.25 / 1M input, $10.00 / 1M output
    /// Adjust in appsettings.json if Google's pricing changes — no code edit
    /// is required.
    /// </summary>
    public class GeminiPricing
    {
        public ModelPrice Flash { get; set; } = new ModelPrice
        {
            InputPerMillionUsd = 0.30m,
            OutputPerMillionUsd = 2.50m,
        };

        public ModelPrice Pro { get; set; } = new ModelPrice
        {
            InputPerMillionUsd = 1.25m,
            OutputPerMillionUsd = 10.00m,
        };

        /// <summary>
        /// Resolves a Gemini model name (e.g. <c>"gemini-2.5-flash"</c>,
        /// <c>"gemini-2.5-pro"</c>) to its price entry. Any model whose name
        /// contains "pro" maps to <see cref="Pro"/>; everything else (including
        /// nulls and unknown names) maps to <see cref="Flash"/>, which is
        /// also the safe default for legacy rows missing a ModelUsed value.
        /// </summary>
        public ModelPrice Resolve(string? modelName)
        {
            if (string.IsNullOrWhiteSpace(modelName)) return Flash;
            return modelName.Contains("pro", System.StringComparison.OrdinalIgnoreCase)
                ? Pro
                : Flash;
        }

        public class ModelPrice
        {
            public decimal InputPerMillionUsd { get; set; }
            public decimal OutputPerMillionUsd { get; set; }

            public decimal ComputeCost(int? inputTokens, int? outputTokens)
            {
                var inT  = inputTokens ?? 0;
                var outT = outputTokens ?? 0;
                return (inT  / 1_000_000m) * InputPerMillionUsd
                     + (outT / 1_000_000m) * OutputPerMillionUsd;
            }
        }
    }
}
