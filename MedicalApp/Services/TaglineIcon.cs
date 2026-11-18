using QuestPDF.Fluent;
using QuestPDF.Infrastructure;

namespace MedicalApp.Services
{
    // Smiling "smart" face shown next to the "Take care of your health!" tagline (web, PDF, email).
    public static class TaglineIcon
    {
        public const string WebPath = "/images/tagline-icon.png";
        public const string EmailUrl = "https://www.mymedicalapp.net/images/tagline-icon.png";

        public static readonly byte[] Png = Load();

        private static byte[] Load()
        {
            using var s = typeof(TaglineIcon).Assembly.GetManifestResourceStream("tagline-icon.png")!;
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            return ms.ToArray();
        }

        // Icon height = 2x the text size, as requested.
        public static string EmailImg(int textPx = 16) =>
            $"<img src='{EmailUrl}' width='{textPx * 2}' height='{textPx * 2}' alt='' style='vertical-align:middle;margin-left:6px;border:0;' />";

        public static void PdfLine(IContainer container, Action<TextSpanDescriptor> style, string text, float fontSize)
        {
            container.AlignCenter().Row(r =>
            {
                r.AutoItem().AlignMiddle().Text(t => style(t.Span(text)));
                r.AutoItem().AlignMiddle().PaddingLeft(4).Height(fontSize * 2).Image(Png).FitHeight();
            });
        }
    }
}
