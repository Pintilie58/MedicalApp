using System.Text.RegularExpressions;
using MedicalApp.Models;

namespace MedicalApp.Services
{
    /// <summary>
    /// Rebuilds <see cref="KeyResult.AnalyteLineRaw"/> locally instead of paying
    /// the model to retype every line of the report.
    ///
    /// The raw line is used as context by the LOINC matcher and by the class
    /// inference, but it is a VERBATIM copy of a line we already have in memory
    /// (TEXT mode). Making the model re-emit 84 full lines was ~30-40% of the
    /// extraction stage's generated tokens — pure waste, since a string search
    /// gives the identical result in microseconds.
    ///
    /// Purely additive: only EMPTY raw lines are filled, so a value produced by
    /// the model (or by VISION mode, where there is no text) is never touched.
    /// </summary>
    public static class RawLineReconstructor
    {
        /// <summary>Fills the missing raw lines. Returns how many were recovered.</summary>
        public static int Fill(InterpretationResult? result, string? extractedText)
        {
            if (result?.KeyResults == null || result.KeyResults.Count == 0) return 0;
            if (string.IsNullOrWhiteSpace(extractedText)) return 0;

            var lines = extractedText.Replace("\r\n", "\n").Split('\n');
            var normalized = new string[lines.Length];
            for (int i = 0; i < lines.Length; i++)
                normalized[i] = Normalize(lines[i]);

            var used = new bool[lines.Length];
            int filled = 0;

            foreach (var kr in result.KeyResults)
            {
                if (!string.IsNullOrWhiteSpace(kr.AnalyteLineRaw)) continue;
                if (string.IsNullOrWhiteSpace(kr.Parameter)) continue;

                var needle = Normalize(kr.Parameter);
                if (needle.Length < 3) continue;

                var value = Normalize(kr.Value ?? "");
                int best = -1, bestScore = 0;

                for (int i = 0; i < lines.Length; i++)
                {
                    if (normalized[i].Length == 0) continue;
                    if (!normalized[i].Contains(needle, StringComparison.Ordinal)) continue;

                    // Prefer an unused line that also carries the value: that is
                    // the actual result row rather than a mention in a comment.
                    int score = 1;
                    if (value.Length > 0 && normalized[i].Contains(value, StringComparison.Ordinal)) score += 4;
                    if (!used[i]) score += 2;

                    if (score > bestScore)
                    {
                        bestScore = score;
                        best = i;
                    }
                }

                if (best < 0) continue;

                kr.AnalyteLineRaw = lines[best].Trim();
                used[best] = true;
                filled++;
            }

            return filled;
        }

        /// <summary>Lowercase, no diacritics, no separators — so "Colesterol total" matches "COLESTEROL  TOTAL".</summary>
        private static string Normalize(string text)
        {
            var decomposed = text.Trim().ToLowerInvariant().Normalize(System.Text.NormalizationForm.FormD);
            var sb = new System.Text.StringBuilder(decomposed.Length);
            foreach (var ch in decomposed)
            {
                var cat = System.Globalization.CharUnicodeInfo.GetUnicodeCategory(ch);
                if (cat == System.Globalization.UnicodeCategory.NonSpacingMark) continue;
                if (char.IsLetterOrDigit(ch) || ch == '.' || ch == ',') sb.Append(ch);
            }
            return Regex.Replace(sb.ToString().Normalize(System.Text.NormalizationForm.FormC), @"\s+", "");
        }
    }
}
