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
            /// <summary>Official LOINC long name, when the matcher supplied one.</summary>
            public string? OfficialName;
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
                // group by unit, then cluster COMPATIBLE reference ranges and
                // collapse the codes inside each cluster.
                foreach (var byUnit in byName.GroupBy(k => NormalizeUnit(k.Unit ?? ""), StringComparer.Ordinal))
                {
                    var clusters = ClusterByRange(byUnit.ToList());

                    foreach (var cluster in clusters)
                    {
                        var codes = new Dictionary<string, Candidate>(StringComparer.OrdinalIgnoreCase);
                        foreach (var kr in cluster)
                        {
                            var code = kr.LoincCode!.Trim();
                            if (!codes.TryGetValue(code, out var cand))
                                codes[code] = cand = new Candidate { Code = code };

                            cand.Occurrences++;
                            if (LoincSourceBadge.IsVerified(kr.LoincSource)) cand.IsVerified = true;
                            if (kr.LoincScore.HasValue && kr.LoincScore.Value > cand.BestScore)
                                cand.BestScore = kr.LoincScore.Value;
                            if (string.IsNullOrWhiteSpace(cand.OfficialName) &&
                                !string.IsNullOrWhiteSpace(kr.LoincLongName))
                                cand.OfficialName = kr.LoincLongName!;
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
                        {
                            if (string.Equals(c.Code, winner.Code, StringComparison.OrdinalIgnoreCase))
                                continue;
                            // Second opinion from the LOINC dictionary (fail-open):
                            // only a CLEAR disagreement between the two official
                            // names blocks the merge.
                            if (OfficialNamesConflict(c.OfficialName, winner.OfficialName)) continue;
                            map[c.Code] = winner.Code;
                        }
                    }

                    // Same name and same unit, but reference ranges that really
                    // contradict each other AND different codes: honest "!" hint
                    // instead of a silent merge.
                    if (clusters.Count > 1 &&
                        byUnit.Select(k => k.LoincCode!.Trim())
                              .Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1 &&
                        !axes.ContainsKey(byName.Key))
                        axes[byName.Key] = "range";
                }
            }

            return new UnificationResult { CodeMap = map, MissingAxisByName = axes };
        }

        /// <summary>Applies a map built by <see cref="BuildCodeMap"/>.</summary>
        public static string? Unify(string? code, Dictionary<string, string> map) =>
            !string.IsNullOrWhiteSpace(code) && map.TryGetValue(code!.Trim(), out var better)
                ? better
                : code;

        // ------------------------------------------------------------------
        // Reference range: shape, compatibility, clustering
        // ------------------------------------------------------------------

        /// <summary>
        /// What a reference-range field really says: the OPERATIVE interval
        /// (the first "4.8-5.6" / "&lt; 6" / "up to 200" in the text), every
        /// number it contains, and the plain normalized text as last resort.
        /// </summary>
        private sealed class RangeShape
        {
            public string Text = "";
            public string? Operative;
            public List<string> Numbers = new();
        }

        private static readonly Dictionary<string, RangeShape> _shapeCache = new(StringComparer.Ordinal);

        private static RangeShape ShapeOf(string range)
        {
            lock (_shapeCache)
            {
                if (_shapeCache.TryGetValue(range, out var cached)) return cached;
                var shape = new RangeShape
                {
                    Text = NormalizeRange(range),
                    Operative = OperativeRange(range),
                    Numbers = RangeNumbers(range)
                };
                if (_shapeCache.Count < 5000) _shapeCache[range] = shape;
                return shape;
            }
        }

        private const string Num = @"\d+(?:[.,]\d+)?";

        private static readonly Regex _interval = new(
            $@"(?<lo>{Num})\s*(?:-|–|—|\.\.\.?|to|la)\s*(?<hi>{Num})",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex _bound = new(
            $@"(?<op><=|<|≤|>=|>|=<|=>|sub|pana la|până la|up to|max\.?|min\.?|peste)\s*(?<v>{Num})",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>
        /// The first operative interval stated in the field, ignoring the
        /// interpretive prose some labs append ("normal: 4.8-5.6% - risc
        /// crescut: 5.7-6.4% - diabet: >=6.5% - tinta: ≤7%" ⇒ "4.8~5.6").
        /// Returns null when the text states no usable interval, in which case
        /// the caller keeps the historical, text-based comparison.
        /// </summary>
        internal static string? OperativeRange(string range)
        {
            if (string.IsNullOrWhiteSpace(range)) return null;

            var i = _interval.Match(range);
            var b = _bound.Match(range);

            // The earliest statement in the text is the operative one.
            bool useInterval = i.Success && (!b.Success || i.Index <= b.Index);

            if (useInterval)
            {
                var lo = Canonical(i.Groups["lo"].Value);
                var hi = Canonical(i.Groups["hi"].Value);
                return lo == null || hi == null ? null : $"{lo}~{hi}";
            }

            if (b.Success)
            {
                var v = Canonical(b.Groups["v"].Value);
                if (v == null) return null;
                var op = b.Groups["op"].Value.Trim().ToLowerInvariant();
                bool upper = op is "<" or "<=" or "≤" or "=<" or "sub" or "max" or "max." or "pana la" or "până la" or "up to";
                return (upper ? "le~" : "ge~") + v;
            }

            return null;
        }

        private static List<string> RangeNumbers(string range)
        {
            var flat = range.Replace(',', '.').Replace('–', ' ').Replace('—', ' ').Replace('-', ' ');
            return Regex.Matches(flat, @"\d+(\.\d+)?")
                .Select(m => Canonical(m.Value) ?? m.Value)
                .ToList();
        }

        private static string? Canonical(string raw) =>
            double.TryParse(raw.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
                ? d.ToString("0.####", CultureInfo.InvariantCulture)
                : null;

        /// <summary>
        /// Two reference ranges describe the same analyte when:
        /// identical text, OR the same operative interval, OR (safety net) one
        /// number list is a prefix of the other — a lab simply wrote more.
        /// Operative intervals that differ are a real contradiction: never merged.
        /// </summary>
        private static bool RangesCompatible(RangeShape a, RangeShape b)
        {
            // An operative interval stated on BOTH sides decides alone: two
            // different limits are a real contradiction, even when the plain
            // text happens to normalize to the same numbers ("< 10" vs "> 10").
            if (a.Operative != null && b.Operative != null)
                return string.Equals(a.Operative, b.Operative, StringComparison.Ordinal);

            if (string.Equals(a.Text, b.Text, StringComparison.Ordinal)) return true;

            // Prefix / subset net, only when BOTH sides state numbers.
            if (a.Numbers.Count > 0 && b.Numbers.Count > 0)
            {
                var shorter = a.Numbers.Count <= b.Numbers.Count ? a.Numbers : b.Numbers;
                var longer = a.Numbers.Count <= b.Numbers.Count ? b.Numbers : a.Numbers;
                return shorter.Where((t, i) => longer[i] == t).Count() == shorter.Count;
            }

            return false;
        }

        /// <summary>
        /// Groups results (already narrowed to one name + one unit) into
        /// clusters of mutually compatible reference ranges.
        /// </summary>
        private static List<List<KeyResult>> ClusterByRange(List<KeyResult> sameNameAndUnit)
        {
            var buckets = new List<(RangeShape Shape, List<KeyResult> Items)>();

            foreach (var kr in sameNameAndUnit)
            {
                var shape = ShapeOf(kr.ReferenceRange ?? "");
                var hit = buckets.FirstOrDefault(b => RangesCompatible(b.Shape, shape));
                if (hit.Items != null) hit.Items.Add(kr);
                else buckets.Add((shape, new List<KeyResult> { kr }));
            }

            return buckets.Select(b => b.Items).ToList();
        }

        // ------------------------------------------------------------------
        // LOINC dictionary veto (fail-open)
        // ------------------------------------------------------------------

        private static readonly HashSet<string> _nameStopWords = new(StringComparer.Ordinal)
        {
            "serum","plasma","blood","urine","fluid","mass","moles","volume","ratio","rate",
            "presence","content","number","count","units","panel","auto","manual","poor","assay",
            "identified","measured","calculated","estimated","platelet","specimen","standard","total"
        };

        /// <summary>
        /// True only when both codes carry an official LOINC long name AND those
        /// names have nothing meaningful in common — the sign that two genuinely
        /// different analytes were written identically by the lab. Missing names
        /// never block a merge (fail-open).
        /// </summary>
        internal static bool OfficialNamesConflict(string? a, string? b)
        {
            var ta = SignificantTokens(a);
            var tb = SignificantTokens(b);
            if (ta.Count == 0 || tb.Count == 0) return false;
            return !ta.Overlaps(tb);
        }

        private static HashSet<string> SignificantTokens(string? name)
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            if (string.IsNullOrWhiteSpace(name)) return set;

            foreach (var raw in Regex.Split(name!, @"[^\p{L}\p{Nd}]+"))
            {
                if (raw.Length < 4) continue;
                var t = Normalize(raw);
                if (t.Length < 4 || _nameStopWords.Contains(t)) continue;
                set.Add(t);
            }
            return set;
        }

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

            if (u is "mii" or "mil" or "103ul" or "103l" or "10e3ul" or "kul" or "thousul"
                  or "miiul" or "miil" or "103mmc" or "miimmc")
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
