using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MedicalApp.Services
{
    /// <summary>
    /// Turns <c>analyte_line_raw</c> into the short "specimen / method" caption shown
    /// under the analyte name. When the model leaves the field empty,
    /// <see cref="RawLineReconstructor"/> fills it with the FULL literal PDF row
    /// ("Eozinofile 10 % 1 - 4 / %") for the LOINC matcher — displaying that verbatim
    /// repeats the name, value and range. This strips those parts and hides the
    /// caption when nothing descriptive remains.
    /// </summary>
    public static class AnalyteLineDisplay
    {
        private const string Num = @"-?\d+(?:[.,]\d+)?";
        private static readonly Regex RangeRx = new(
            @"(?<![\w.,])" + Num + @"\s*[-–—]\s*" + Num + @"(?:\s*/\s*[^\s()]+)?", RegexOptions.Compiled);
        private static readonly Regex ThresholdRx = new(
            @"[<>≤≥]\s*=?\s*" + Num + @"(?:\s*/\s*[^\s()]+)?", RegexOptions.Compiled);
        private static readonly Regex SpacesRx = new(@"\s+", RegexOptions.Compiled);

        public static string? Clean(string? line, string? parameter, string? value, string? unit, string? referenceRange)
        {
            if (string.IsNullOrWhiteSpace(line)) return null;
            string s = line.Trim();

            s = RemoveFolded(s, parameter);
            s = RemoveFolded(s, referenceRange);
            s = RangeRx.Replace(s, " ");
            s = ThresholdRx.Replace(s, " ");
            if (!string.IsNullOrWhiteSpace(value))
            {
                var vp = string.Concat(value.Trim().Select(ch => ch is '.' or ',' ? "[.,]" : Regex.Escape(ch.ToString())));
                s = Regex.Replace(s, @"(?<![\w.,])" + vp + @"(?![\w.,])", " ");
            }
            if (!string.IsNullOrWhiteSpace(unit))
                s = Regex.Replace(s, @"(?<![\w])" + Regex.Escape(unit.Trim()) + @"(?![\w])", " ", RegexOptions.IgnoreCase);

            s = SpacesRx.Replace(s, " ").Trim();
            s = Regex.Replace(s, @"^\d{1,3}\.\s*", "");
            s = s.Trim(' ', '-', '–', '—', ':', ';', ',', '.', '/', '|', '*');
            s = SpacesRx.Replace(s, " ").Trim();

            return s.Count(char.IsLetter) >= 3 ? s : null;
        }

        /// <summary>Removes the first case/diacritics-insensitive occurrence of <paramref name="needle"/>.</summary>
        private static string RemoveFolded(string text, string? needle)
        {
            if (string.IsNullOrWhiteSpace(needle)) return text;
            var n = Fold(needle.Trim());
            if (n.Length == 0) return text;
            int idx = Fold(text).IndexOf(n, StringComparison.Ordinal);
            return idx < 0 ? text : text[..idx] + " " + text[(idx + n.Length)..];
        }

        /// <summary>Lower-case, diacritics removed, SAME length as the input (index-preserving).</summary>
        private static string Fold(string text)
        {
            var sb = new StringBuilder(text.Length);
            foreach (var ch in text)
            {
                var d = ch.ToString().Normalize(NormalizationForm.FormD);
                var b = d.Length > 0 && CharUnicodeInfo.GetUnicodeCategory(d[0]) != UnicodeCategory.NonSpacingMark ? d[0] : ch;
                sb.Append(char.ToLowerInvariant(b));
            }
            return sb.ToString();
        }
    }
}
