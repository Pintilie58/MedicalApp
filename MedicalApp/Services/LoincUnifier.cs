using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using MedicalApp.Models;

namespace MedicalApp.Services
{
    /// <summary>
    /// Collapses the LOINC codes that Gemini's wording variability split apart.
    ///
    /// Rule (user-specified): two results are the SAME analyte only when their
    /// name, unit of measure AND reference range are identical after
    /// normalization. That single rule also protects the legitimate cases —
    /// "Limfocit (%)" 736-9 and "Limfocit (mii/µL)" 731-0 differ on unit and
    /// range, so they are never merged.
    ///
    /// PRUDENT mode (user-specified): when one report is missing the unit or the
    /// range, nothing is merged; the caller can tell the user exactly which axis
    /// was missing instead of guessing.
    /// </summary>
    public static class LoincUnifier
    {
        /// <summary>One candidate code seen for a given analyte signature.</summary>
        private sealed class Candidate
        {
            public string Code = "";
            public bool IsVerified;
            public double BestScore;
            public int Occurrences;
        }

        /// <summary>
        /// Builds a map <c>original LOINC code -> unified LOINC code</c> from all
        /// the results of an archive. Codes that need no change are absent from
        /// the map. Never merges across different units or ranges.
        /// </summary>
        public static Dictionary<string, string> BuildCodeMap(IEnumerable<KeyResult> allResults)
        {
            var bySignature = new Dictionary<string, Dictionary<string, Candidate>>(StringComparer.Ordinal);

            foreach (var kr in allResults)
            {
                if (string.IsNullOrWhiteSpace(kr?.LoincCode) || string.IsNullOrWhiteSpace(kr.Parameter))
                    continue;

                // PRUDENT: an incomplete signature can never drive a merge.
                if (string.IsNullOrWhiteSpace(kr.Unit) || string.IsNullOrWhiteSpace(kr.ReferenceRange))
                    continue;

                var sig = Signature(kr.Parameter, kr.Unit, kr.ReferenceRange);
                if (!bySignature.TryGetValue(sig, out var codes))
                    bySignature[sig] = codes = new Dictionary<string, Candidate>(StringComparer.OrdinalIgnoreCase);

                var code = kr.LoincCode!.Trim();
                if (!codes.TryGetValue(code, out var cand))
                    codes[code] = cand = new Candidate { Code = code };

                cand.Occurrences++;
                if (LoincSourceBadge.IsVerified(kr.LoincSource)) cand.IsVerified = true;
                if (kr.LoincScore.HasValue && kr.LoincScore.Value > cand.BestScore)
                    cand.BestScore = kr.LoincScore.Value;
            }

            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var (_, codes) in bySignature)
            {
                if (codes.Count < 2) continue; // nothing to unify

                // Best code: verified beats guessed, then score, then how many
                // reports used it (the majority is usually right), then the code
                // itself so the outcome is stable across runs.
                var winner = codes.Values
                    .OrderByDescending(c => c.IsVerified)
                    .ThenByDescending(c => c.BestScore)
                    .ThenByDescending(c => c.Occurrences)
                    .ThenBy(c => c.Code, StringComparer.Ordinal)
                    .First();

                foreach (var c in codes.Values)
                    if (!string.Equals(c.Code, winner.Code, StringComparison.OrdinalIgnoreCase))
                        map[c.Code] = winner.Code;
            }

            return map;
        }

        /// <summary>
        /// Outcome of a whole-archive analysis: the code map plus the analytes
        /// we REFUSED to unify because a report was missing its unit or its
        /// reference range. The caller turns the second part into a discreet
        /// "!" hint so the user understands why a row is still duplicated.
        /// </summary>
        public sealed class UnificationResult
        {
            public Dictionary<string, string> CodeMap { get; init; } = new(StringComparer.OrdinalIgnoreCase);

            /// <summary>normalized parameter name -> "unit" | "range" | "both".</summary>
            public Dictionary<string, string> MissingAxisByName { get; init; } = new(StringComparer.Ordinal);

            /// <summary>Which axis blocked the merge for this parameter, or null when nothing was blocked.</summary>
            public string? MissingAxisFor(string? parameter) =>
                !string.IsNullOrWhiteSpace(parameter) &&
                MissingAxisByName.TryGetValue(Normalize(parameter!), out var axis)
                    ? axis
                    : null;
        }

        /// <summary>
        /// Builds the code map AND reports the analytes left duplicated because
        /// their signature was incomplete on at least one report.
        /// </summary>
        public static UnificationResult Analyze(IEnumerable<KeyResult> allResults)
        {
            var all = allResults.Where(k => k != null && !string.IsNullOrWhiteSpace(k.Parameter)).ToList();
            var map = BuildCodeMap(all);

            var codesByName = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            var missingUnit = new HashSet<string>(StringComparer.Ordinal);
            var missingRange = new HashSet<string>(StringComparer.Ordinal);

            foreach (var kr in all)
            {
                if (string.IsNullOrWhiteSpace(kr.LoincCode)) continue;

                var name = Normalize(kr.Parameter);
                if (!codesByName.TryGetValue(name, out var set))
                    codesByName[name] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                set.Add(Unify(kr.LoincCode, map)!.Trim());

                if (string.IsNullOrWhiteSpace(kr.Unit)) missingUnit.Add(name);
                if (string.IsNullOrWhiteSpace(kr.ReferenceRange)) missingRange.Add(name);
            }

            var axes = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (name, codes) in codesByName)
            {
                if (codes.Count < 2) continue; // unified (or never split) — nothing to explain
                bool noUnit = missingUnit.Contains(name);
                bool noRange = missingRange.Contains(name);
                if (noUnit && noRange) axes[name] = "both";
                else if (noUnit) axes[name] = "unit";
                else if (noRange) axes[name] = "range";
            }

            return new UnificationResult { CodeMap = map, MissingAxisByName = axes };
        }

        /// <summary>Applies a map built by <see cref="BuildCodeMap"/>.</summary>
        public static string? Unify(string? code, Dictionary<string, string> map) =>
            !string.IsNullOrWhiteSpace(code) && map.TryGetValue(code!.Trim(), out var better)
                ? better
                : code;

        private static string Signature(string name, string unit, string range) =>
            $"{Normalize(name)}|{NormalizeUnit(unit)}|{NormalizeRange(range)}";

        /// <summary>Lowercase, diacritic-free, punctuation-free: "I.N.R." == "inr".</summary>
        internal static string Normalize(string s)
        {
            var decomposed = s.Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD);
            var sb = new StringBuilder(decomposed.Length);
            foreach (var ch in decomposed)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
                if (char.IsLetterOrDigit(ch)) sb.Append(ch);
            }
            return sb.ToString();
        }

        /// <summary>Canonical unit: "mii/µL", "10^3/uL" and "10³/µL" all collapse.</summary>
        internal static string NormalizeUnit(string unit)
        {
            var u = Normalize(unit)
                .Replace("µ", "u").Replace("μ", "u")
                .Replace("³", "3").Replace("⁶", "6").Replace("⁹", "9");

            if (u is "mii" or "mil" or "103ul" or "103l" or "10e3ul" or "kul" or "thousul")
                return "10e3/ul";
            if (u is "mil6ul" or "106ul" or "10e6ul" or "milul")
                return "10e6/ul";
            return u;
        }

        /// <summary>
        /// Compares ranges by their NUMBERS, so "0.8-1.2", "0,8 - 1,2" and
        /// "0.80–1.20" are one and the same. Non-numeric ranges fall back to text.
        /// </summary>
        internal static string NormalizeRange(string range)
        {
            // Hyphens / dashes are separators here, never minus signs — lab
            // ranges are written "0-200", "0 - 200", "0–200". Stripping them
            // first is what makes those three collapse to the same signature.
            var flat = range.Replace(',', '.').Replace('–', ' ').Replace('—', ' ').Replace('-', ' ');

            var numbers = Regex.Matches(flat, @"\d+(\.\d+)?")
                .Select(m => double.TryParse(m.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
                    ? d.ToString("0.####", CultureInfo.InvariantCulture)
                    : m.Value)
                .ToList();

            return numbers.Count > 0 ? string.Join("~", numbers) : Normalize(range);
        }
    }
}
