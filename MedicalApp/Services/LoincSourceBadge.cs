namespace MedicalApp.Services
{
    /// <summary>
    /// Returns user-facing labels + colors for the "loinc_source" value
    /// that the Python matcher service assigns to each parameter:
    ///   - "anchor"   => hard-curated canonical mapping (LOINC code is 100%
    ///                   verified — same code an EMR or HL7 message would use)
    ///   - "semantic" => embedding+fuzzy+rules pipeline (probabilistic; the
    ///                   code is the matcher's best guess and may need review
    ///                   on rare/exotic analytes)
    ///
    /// Centralized here so every view (Compare, Evolution, PDF) uses the
    /// same wording and the same color, and so the Romanian text lives in
    /// ONE file (easier to swap to other languages later).
    /// </summary>
    public static class LoincSourceBadge
    {
        public const string AnchorSource   = "anchor";
        public const string SemanticSource = "semantic";

        /// <summary>Short Romanian label for the in-UI badge.</summary>
        public static string GetLabel(string? source)
            => source == AnchorSource ? "verificat" : "auto";

        /// <summary>Longer Romanian text for tooltips.</summary>
        public static string GetTooltip(string? source)
            => source == AnchorSource
                ? "Cod LOINC verificat manual — același cod folosit de sistemele medicale internaționale pentru această analiză."
                : "Cod LOINC sugerat automat de matcher-ul semantic. Pentru analize standard este în general corect; pentru analize rare verifică pe loinc.org.";

        /// <summary>Bootstrap badge class (background + text color combo).</summary>
        public static string GetBadgeClass(string? source)
            => source == AnchorSource
                ? "bg-success-subtle text-success border border-success-subtle"
                : "bg-warning-subtle text-warning-emphasis border border-warning-subtle";

        /// <summary>Hex color used when rendering in PDF (QuestPDF).</summary>
        public static string GetPdfColor(string? source)
            => source == AnchorSource ? "#198754" : "#b8860b";

        /// <summary>Single-character glyph (✓ for verified, ~ for auto).</summary>
        public static string GetGlyph(string? source)
            => source == AnchorSource ? "✓" : "~";

        /// <summary>True when the source is the high-confidence "anchor" value.</summary>
        public static bool IsVerified(string? source) => source == AnchorSource;
    }
}
