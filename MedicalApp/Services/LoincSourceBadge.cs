namespace MedicalApp.Services
{
    /// <summary>
    /// Compact visual marker (a single colored dot) for the "loinc_source"
    /// value that the Python matcher service assigns to each parameter:
    ///   - "anchor"   => hard-curated canonical mapping (green dot)
    ///   - "semantic" => embedding+fuzzy+rules pipeline (blue dot)
    ///
    /// We deliberately avoid the words "verified" / "auto" next to every
    /// parameter — the codes are correct in both cases; the source is just
    /// metadata about the matching pipeline. A small legend at the end of
    /// each interpretation explains the dots in the user's language.
    /// </summary>
    public static class LoincSourceBadge
    {
        public const string AnchorSource   = "anchor";
        public const string SemanticSource = "semantic";

        /// <summary>
        /// CSS color for the inline UI dot. Green for anchors (manually
        /// curated mapping), blue for semantic matches (high-confidence
        /// algorithmic mapping).
        /// </summary>
        public static string GetDotColor(string? source)
            => source == AnchorSource ? "#198754" : "#0d6efd";

        /// <summary>Same color, used by the PDF generator (QuestPDF hex).</summary>
        public static string GetPdfColor(string? source)
            => GetDotColor(source);

        /// <summary>Localized tooltip shown on hover in the UI.</summary>
        public static string GetTooltip(string? source)
            => source == AnchorSource
                ? Loc.T("LoincSourceTooltipVerified")
                : Loc.T("LoincSourceTooltipAuto");

        /// <summary>True when the source is the high-confidence "anchor" value.</summary>
        public static bool IsVerified(string? source) => source == AnchorSource;
    }
}
