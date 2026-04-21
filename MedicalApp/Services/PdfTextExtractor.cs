using System.Text;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace MedicalApp.Services
{
    /// <summary>
    /// Extracts text from PDF files using PdfPig (pure C#, no native deps).
    /// Uses ContentOrderTextExtractor to preserve the logical reading order of
    /// content (rows/columns/tables) which is critical for medical lab reports
    /// where values and reference ranges sit in aligned columns.
    /// </summary>
    public static class PdfTextExtractor
    {
        public static string Extract(Stream pdfStream)
        {
            using var document = PdfDocument.Open(pdfStream);
            var sb = new StringBuilder();
            int pageNo = 0;
            foreach (var page in document.GetPages())
            {
                pageNo++;
                sb.Append("--- Page ").Append(pageNo).AppendLine(" ---");

                // ContentOrderTextExtractor preserves the order in which content
                // was added to the page, which for modern lab PDFs correlates
                // well with the visual top-to-bottom, left-to-right reading order
                // and keeps columns aligned.
                string pageText;
                try
                {
                    pageText = ContentOrderTextExtractor.GetText(page, true);
                }
                catch
                {
                    // Fallback to naive extraction if the layout-aware extractor fails
                    // on a malformed page.
                    pageText = page.Text;
                }

                sb.AppendLine(pageText);
                sb.AppendLine();
            }
            return sb.ToString();
        }
    }
}
