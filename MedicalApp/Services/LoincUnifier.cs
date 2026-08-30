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
    /// PRUDENT mode (user-specified): when one report states the unit or the
    /// range and another does NOT, nothing is merged; the caller can tell the
    /// user exactly which axis was inconsistent instead of guessing. An axis
    /// that is missing on EVERY report (INR and other dimensionless analytes)
    /// is not a doubt — those rows unify normally.
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
            => Analyze(allResults).CodeMap;

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
        /// an axis was present on one report and missing on another.
        ///
        /// A missing axis only blocks the merge when it is INCONSISTENT across
        /// the reports. An analyte that is dimensionless everywhere (INR, raport
        /// albumine/globuline, indici) has no unit on ANY report — that is not
        /// uncertainty, it is the nature of the analyte, so it unifies normally.
        /// </summary>
        public static UnificationResult Analyze(IEnumerable<KeyResult> allResults)
        {
            var all = allResults
                .Where(k => k != null
                            && !string.IsNullOrWhiteSpace(k.Parameter)
                            && !string.IsNullOrWhiteSpace(k.LoincCode))
                .ToList();

            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var axes = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (var byName in all.GroupBy(k => Normalize(k.Parameter)))
            {
                bool unitMixed = byName.Any(k => string.IsNullOrWhiteSpace(k.Unit))
                                 && byName.Any(k => !string.IsNullOrWhiteSpace(k.Unit));
                bool rangeMixed = byName.Any(k => string.IsNullOrWhiteSpace(k.ReferenceRange))
                                  && byName.Any(k => !string.IsNullOrWhiteSpace(k.ReferenceRange));

                if (unitMixed || rangeMixed)
                {
                    // PRUDENT: one report states the axis, another does not — we
                    // cannot tell whether it is the same analyte, so we leave the
                    // rows apart and explain why (only worth saying when the
                    // codes actually disagree).
                    bool split = byName.Select(k => k.LoincCode!.Trim())
                                       .Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1;
                    if (split)
                        axes[byName.Key] = unitMixed && rangeMixed ? "both" : unitMixed ? "unit" : "range";
                    continue;
                }

                // Axes are consistent (present everywhere, or absent everywhere):
                // group by the full signature and collapse the codes inside it.
                foreach (var bySig in byName.GroupBy(k =>
                             Signature(k.Parameter, k.Unit ?? "", k.ReferenceRange ?? "")))
                {
                    var codes = new Dictionary<string, Candidate>(StringComparer.OrdinalIgnoreCase);
                    foreach (var kr in bySig)
                    {
                        var code = kr.LoincCode!.Trim();
                        if (!codes.TryGetValue(code, out var cand))
                            codes[code] = cand = new Candidate { Code = code };

                        cand.Occurrences++;
                        if (LoincSourceBadge.IsVerified(kr.LoincSource)) cand.IsVerified = true;
                        if (kr.LoincScore.HasValue && kr.LoincScore.Value > cand.BestScore)
                            cand.BestScore = kr.LoincScore.Value;
                    }

                    if (codes.Count < 2) continue; // nothing to unify

                    // Best code: verified beats guessed, then score, then how many
                    // reports used it (the majority is usually right), then the
                    // code itself so the outcome is stable across runs.
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

        /// <summary>
        /// Same LOINC code, DIFFERENT units of measure (Fibrinogen reported as
        /// g/L by one lab and mg/dL by another both map to 3255-7). Grouping by
        /// code alone would put 4.32 g/L and 516 mg/dL on the same row with a
        /// single reference range — medically wrong. This tells the callers to
        /// keep those measurements apart.
        /// </summary>
        public sealed class UnitScope
        {
            private readonly Dictionary<string, string> _majorityUnitByCode = new(StringComparer.OrdinalIgnoreCase);

            /// <summary>Codes measured in more than one unit across the archive.</summary>
            public static UnitScope Build(IEnumerable<KeyResult> allResults, Dictionary<string, string> codeMap)
            {
                var counts = new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase);

                foreach (var kr in allResults)
                {
                    if (kr == null || string.IsNullOrWhiteSpace(kr.LoincCode) || string.IsNullOrWhiteSpace(kr.Unit))
                        continue;

                    var code = Unify(kr.LoincCode, codeMap)!.Trim();
                    var unit = NormalizeUnit(kr.Unit!);
                    if (unit.Length == 0) continue;

                    if (!counts.TryGetValue(code, out var perUnit))
                        counts[code] = perUnit = new Dictionary<string, int>(StringComparer.Ordinal);
                    perUnit[unit] = perUnit.TryGetValue(unit, out var c) ? c + 1 : 1;
                }

                var scope = new UnitScope();
                foreach (var (code, perUnit) in counts)
                {
                    if (perUnit.Count < 2) continue; // single unit — nothing to split
                    scope._majorityUnitByCode[code] = perUnit
                        .OrderByDescending(kv => kv.Value)
                        .ThenBy(kv => kv.Key, StringComparer.Ordinal)
                        .First().Key;
                }
                return scope;
            }

            /// <summary>True when this code was reported in several units.</summary>
            public bool IsSplit(string? code) =>
                !string.IsNullOrWhiteSpace(code) && _majorityUnitByCode.ContainsKey(code!.Trim());

            /// <summary>
            /// Group-key suffix: empty for the usual single-unit code, otherwise
            /// "|u=&lt;unit&gt;". A measurement with no unit joins the unit used by
            /// most reports instead of creating a third, meaningless bucket.
            /// </summary>
            public string Suffix(string? code, string? unit)
            {
                if (string.IsNullOrWhiteSpace(code) ||
                    !_majorityUnitByCode.TryGetValue(code!.Trim(), out var majority))
                    return string.Empty;

                var u = string.IsNullOrWhiteSpace(unit) ? "" : NormalizeUnit(unit!);
                return "|u=" + (u.Length == 0 ? majority : u);
            }
        }

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
