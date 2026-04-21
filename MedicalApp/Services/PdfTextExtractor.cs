using System.Text;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace MedicalApp.Services
{
    /// <summary>
    /// Extracts text from PDF files using PdfPig (pure C#, no native deps).
    /// Instead of using <c>page.Text</c> (which returns text in raw PDF content-stream
    /// order and often scrambles rows/columns in lab reports), this extractor walks
    /// the list of words and groups them into visual rows by Y-coordinate, then sorts
    /// each row left-to-right by X-coordinate. This preserves the reading order of
    /// tabular lab data (parameter | value | unit | reference range).
    /// </summary>
    public static class PdfTextExtractor
    {
        // Two words are considered on the same visual row if the difference between
        // their baselines is smaller than this fraction of the average word height.
        private const double RowGroupingTolerance = 0.5;

        public static string Extract(Stream pdfStream)
        {
            using var document = PdfDocument.Open(pdfStream);
            var sb = new StringBuilder();
            int pageNo = 0;
            foreach (var page in document.GetPages())
            {
                pageNo++;
                sb.Append("--- Page ").Append(pageNo).AppendLine(" ---");

                string pageText;
                try
                {
                    pageText = ExtractOrderedText(page);
                }
                catch
                {
                    // Fallback to naive extraction if layout ordering fails on a
                    // malformed page.
                    pageText = page.Text;
                }

                sb.AppendLine(pageText);
                sb.AppendLine();
            }
            return sb.ToString();
        }

        private static string ExtractOrderedText(Page page)
        {
            var words = page.GetWords().ToList();
            if (words.Count == 0)
                return page.Text;

            // Average word height – used as the basis for row detection tolerance.
            double avgHeight = words.Average(w => w.BoundingBox.Height);
            if (avgHeight <= 0) avgHeight = 10;
            double rowTolerance = avgHeight * RowGroupingTolerance;

            // Sort by Y descending (PDF coordinate system has origin at bottom-left,
            // so higher Y = visually higher on the page = read first).
            var sorted = words
                .OrderByDescending(w => w.BoundingBox.Bottom)
                .ToList();

            var sb = new StringBuilder();
            var currentRow = new List<Word>();
            double? currentRowBaseline = null;

            foreach (var word in sorted)
            {
                if (currentRowBaseline == null ||
                    Math.Abs(currentRowBaseline.Value - word.BoundingBox.Bottom) <= rowTolerance)
                {
                    currentRow.Add(word);
                    currentRowBaseline ??= word.BoundingBox.Bottom;
                }
                else
                {
                    FlushRow(currentRow, sb);
                    currentRow.Clear();
                    currentRow.Add(word);
                    currentRowBaseline = word.BoundingBox.Bottom;
                }
            }
            FlushRow(currentRow, sb);

            return sb.ToString();
        }

        private static void FlushRow(List<Word> row, StringBuilder sb)
        {
            if (row.Count == 0) return;

            // Sort words in this row left-to-right.
            var ordered = row.OrderBy(w => w.BoundingBox.Left).ToList();

            // Insert a larger gap between words that are clearly separated on the X
            // axis (likely different columns in a table).
            double avgWidth = ordered.Average(w => w.BoundingBox.Width);
            if (avgWidth <= 0) avgWidth = 5;

            for (int i = 0; i < ordered.Count; i++)
            {
                if (i > 0)
                {
                    double gap = ordered[i].BoundingBox.Left - ordered[i - 1].BoundingBox.Right;
                    if (gap > avgWidth * 2)
                        sb.Append("    "); // column separator
                    else
                        sb.Append(' ');
                }
                sb.Append(ordered[i].Text);
            }
            sb.AppendLine();
        }
    }
}
