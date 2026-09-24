using System.Globalization;
using System.Text.RegularExpressions;
using MedicalApp.Models;

namespace MedicalApp.Services
{
    /// <summary>
    /// Cross-checks the model's <see cref="KeyResult.ReferenceRange"/> against the
    /// LITERAL text layer of the PDF. Gemini sometimes "corrects" a printed range
    /// to the textbook one it remembers (e.g. basophils "0 - 0.2" becomes "0-2"),
    /// which then makes an out-of-range value look normal.
    ///
    /// Deliberately conservative — a range is replaced ONLY when ALL hold:
    ///   1. the model's range is a plain "X - Y" with two finite numbers;
    ///   2. exactly ONE line of the extracted text contains both the parameter
    ///      name and the value as a whole token;
    ///   3. the model's range is NOT printed on that line;
    ///   4. exactly ONE printed range on that line is a digit-corruption of the
    ///      model's range (each bound equal, or its digits a subsequence of the
    ///      printed bound: "2" ⊂ "0.2", "13" ⊂ "13.2").
    /// Anything else (scanned PDFs, thresholds like "&lt; 200", ambiguous rows)
    /// is left untouched. Must run BEFORE <see cref="StatusValidator"/>.
    /// </summary>
    public static class ReferenceRangeVerifier
    {
        public sealed record Correction(string Parameter, string From, string To, string Line);

        private const string Num = @"-?\d+(?:[.,]\d+)?";

        private static readonly Regex RangeRx = new(
            @"(?<![\d.,\-–—/^])(" + Num + @")\s*[-–—]\s*(" + Num + @")(?![\d.,]*[-–—/.]\d)",
            RegexOptions.Compiled);

        public static List<Correction> Verify(InterpretationResult? result, string? extractedText, ILogger? logger = null)
        {
            var corrections = new List<Correction>();
            var keyResults = result?.KeyResults;
            if (keyResults == null || keyResults.Count == 0) return corrections;
            if (string.IsNullOrWhiteSpace(extractedText) || extractedText.StartsWith("(text extraction failed"))
                return corrections;

            var lines = extractedText.Replace("\r\n", "\n").Split('\n');
            var normalized = lines.Select(Normalize).ToArray();

            foreach (var kr in keyResults)
            {
                if (kr == null || string.IsNullOrWhiteSpace(kr.ReferenceRange)) continue;

                if (!StatusValidator.TryParseRange(kr.ReferenceRange, out var mLo, out var mHi, out _, out _)
                    || mLo == null || mHi == null) continue;
                if (!StatusValidator.TryParseValue(kr.Value, out _)) continue;

                var line = FindUniqueLine(kr, lines, normalized);
                if (line == null) continue;

                var printed = ExtractRanges(line);
                if (printed.Count == 0) continue;
                if (printed.Any(p => Same(p.Lo, mLo.Value) && Same(p.Hi, mHi.Value))) continue;

                var related = printed.Where(p => IsCorruptionOf(mLo.Value, mHi.Value, p)).ToList();
                if (related.Count != 1) continue;

                var fix = related[0];
                var mm = RangeRx.Match(kr.ReferenceRange);
                if (!mm.Success) continue;

                string before = kr.ReferenceRange;
                string after = kr.ReferenceRange[..mm.Groups[1].Index] + fix.LoText
                             + kr.ReferenceRange[(mm.Groups[1].Index + mm.Groups[1].Length)..mm.Groups[2].Index] + fix.HiText
                             + kr.ReferenceRange[(mm.Groups[2].Index + mm.Groups[2].Length)..];

                if (!StatusValidator.TryParseRange(after, out var nLo, out var nHi, out _, out _)
                    || !Same(nLo ?? double.NaN, fix.Lo) || !Same(nHi ?? double.NaN, fix.Hi)) continue;

                kr.ReferenceRange = after;
                corrections.Add(new Correction(kr.Parameter, before, after, line.Trim()));
            }

            if (corrections.Count > 0 && logger != null)
                logger.LogWarning("ReferenceRangeVerifier corrected {Count} reference range(s):\n{Details}",
                    corrections.Count,
                    string.Join("\n", corrections.Select(c => $"  {c.Parameter}: '{c.From}' -> '{c.To}'  [pdf: {c.Line}]")));

            return corrections;
        }

        private sealed record PrintedRange(double Lo, double Hi, string LoText, string HiText);

        private static string? FindUniqueLine(KeyResult kr, string[] lines, string[] normalized)
        {
            var needle = Normalize(kr.Parameter);
            if (needle.Length < 3) return null;

            var valuePattern = string.Concat(kr.Value!.Trim().Select(ch =>
                ch is '.' or ',' ? "[.,]" : Regex.Escape(ch.ToString())));
            var valueRx = new Regex(@"(?<![\d.,])" + valuePattern + @"(?![\d.,]*\d)");

            string? found = null;
            for (int i = 0; i < lines.Length; i++)
            {
                if (normalized[i].Length == 0 || !normalized[i].Contains(needle, StringComparison.Ordinal)) continue;
                if (!valueRx.IsMatch(lines[i])) continue;
                if (found != null) return null;
                found = lines[i];
            }
            return found;
        }

        private static List<PrintedRange> ExtractRanges(string line)
        {
            var list = new List<PrintedRange>();
            foreach (Match m in RangeRx.Matches(line))
            {
                if (!TryNum(m.Groups[1].Value, out var lo) || !TryNum(m.Groups[2].Value, out var hi)) continue;
                if (lo > hi) continue;
                list.Add(new PrintedRange(lo, hi, m.Groups[1].Value, m.Groups[2].Value));
            }
            return list;
        }

        private static bool IsCorruptionOf(double mLo, double mHi, PrintedRange p)
            => BoundRelated(mLo, p.Lo, p.LoText) && BoundRelated(mHi, p.Hi, p.HiText);

        private static bool BoundRelated(double model, double printed, string printedText)
        {
            if (Same(model, printed)) return true;
            var m = Digits(model.ToString("0.############", CultureInfo.InvariantCulture));
            var p = Digits(printedText);
            return m.Length > 0 && m.Length <= p.Length && IsSubsequence(m, p);
        }

        private static string Digits(string s) => new(s.Where(char.IsDigit).ToArray());

        private static bool IsSubsequence(string small, string big)
        {
            int j = 0;
            foreach (var ch in big)
                if (j < small.Length && small[j] == ch) j++;
            return j == small.Length;
        }

        private static bool Same(double a, double b) => Math.Abs(a - b) < 1e-9;

        private static bool TryNum(string s, out double v)
            => double.TryParse(s.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out v);

        private static string Normalize(string text)
        {
            var decomposed = text.Trim().ToLowerInvariant().Normalize(System.Text.NormalizationForm.FormD);
            var sb = new System.Text.StringBuilder(decomposed.Length);
            foreach (var ch in decomposed)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
                if (char.IsLetterOrDigit(ch)) sb.Append(ch);
            }
            return sb.ToString().Normalize(System.Text.NormalizationForm.FormC);
        }
    }
}
