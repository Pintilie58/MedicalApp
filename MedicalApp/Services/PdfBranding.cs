namespace MedicalApp.Services
{
    /// <summary>Branding strings printed on generated PDFs (headers / footers).</summary>
    public static class PdfBranding
    {
        public const string Website = "WWW.MyMedicalApp.NET";

        /// <summary>
        /// Font chain used by every PDF generator. QuestPDF walks the list
        /// PER GLYPH, so the first family that actually contains the character
        /// wins:
        ///   • "Arial"          — Windows / IIS. Keeps the historical look.
        ///   • "Liberation Sans"— Linux container. Metric-compatible with Arial
        ///                        (identical glyph widths), so page layout,
        ///                        column widths and line breaks stay the same.
        ///   • "DejaVu Sans"    — only for the symbols the two above lack:
        ///                        ✓ (U+2713) and ⚠ (U+26A0), which rendered as
        ///                        empty boxes in the container.
        /// Both Linux fonts are installed by MedicalApp/Dockerfile.
        /// </summary>
        public static readonly string[] FontChain =
            { "Arial", "Liberation Sans", "DejaVu Sans" };
    }
}
