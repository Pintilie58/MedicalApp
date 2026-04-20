using System.Text;
using UglyToad.PdfPig;

namespace MedicalApp.Services
{
    /// <summary>
    /// Extracts raw text from PDF files using PdfPig (pure C#, no native deps).
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
                sb.AppendLine(page.Text);
                sb.AppendLine();
            }
            return sb.ToString();
        }
    }
}
