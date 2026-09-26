using QuestPDF.Fluent;
using QuestPDF.Infrastructure;

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

        public const string BrandBlue = "#0d47a1";

        /// <summary>Centered "www.MyMedicalApp.net" brand line with a short rule — same look as the web headers.</summary>
        public static void BrandHeader(QuestPDF.Fluent.ColumnDescriptor col)
        {
            col.Item().AlignCenter().Text("www.MyMedicalApp.net")
                .FontSize(11).Bold().FontColor(BrandBlue);
            col.Item().AlignCenter().PaddingTop(2).PaddingBottom(6).Width(150).LineHorizontal(1.5f).LineColor(BrandBlue);
        }

        /// <summary>
        /// Full-width note row printed under an analyte whose reference text is too long
        /// for its column (INR therapeutic ranges, LDL risk tiers…). The column itself
        /// shows <see cref="ReferenceRangeDisplay.Short"/>.
        /// </summary>
        public static void ReferenceNoteRow(QuestPDF.Fluent.TableDescriptor table, uint columnSpan, string label, string fullText, string? background = null)
        {
            IContainer cell = table.Cell().ColumnSpan(columnSpan);
            if (background != null) cell = cell.Background(background);
            cell.PaddingTop(0).PaddingBottom(4).PaddingHorizontal(5)
                .BorderBottom(0.5f).BorderColor(QuestPDF.Helpers.Colors.Grey.Lighten2)
                .Text(t =>
                {
                    t.Span(label.ToUpperInvariant() + ": ").FontSize(6.5f).Bold().FontColor("#8a9099");
                    t.Span(fullText).FontSize(7.5f).FontColor("#5c6670");
                });
        }
    }
}
