using System.Globalization;
using System.Text.RegularExpressions;
using MedicalApp.Models;

namespace MedicalApp.Services
{
    /// <summary>
    /// Post-processes the AI's structured output and corrects parameter statuses by
    /// re-computing them from the value and the reference range. Gemini occasionally
    /// labels a value that is inside the range as "high"/"low", or vice-versa, even
    /// when it has correctly extracted both the value and the range. This validator
    /// is the safety net: status is a mathematical decision, so we make it ourselves.
    ///
    /// Supported reference-range syntaxes (case- and whitespace-insensitive):
    ///   "X - Y"            -> normal when X <= v <= Y
    ///   "X - Y / unit"     -> same; everything after the slash is ignored
    ///   "X-Y"              -> same
    ///   "&lt; X" / "&lt;= X" / "≤ X"  -> normal when v &lt; X (or &lt;= X)
    ///   "&gt; X" / "&gt;= X" / "≥ X"  -> normal when v &gt; X (or &gt;= X)
    ///   plus the target-style "&lt;X mg/dL — țintă pentru risc cardiovascular ..."
    ///
    /// For parameters whose value is non-numeric (e.g. "negativ"/"pozitiv") we don't
    /// touch the status - the model's judgement stands.
    /// </summary>
    public static class StatusValidator
    {
        public sealed record ValidationStats(int Total, int Corrected, int Skipped);

        public static ValidationStats Validate(InterpretationResult result, ILogger? logger = null)
        {
            ArgumentNullException.ThrowIfNull(result);
            if (result.KeyResults == null || result.KeyResults.Count == 0)
                return new ValidationStats(0, 0, 0);

            int corrected = 0, skipped = 0;
            var correctionsLog = new List<string>();

            foreach (var kr in result.KeyResults)
            {
                if (kr == null) continue;

                if (!TryParseValue(kr.Value, out double v))
                {
                    skipped++;
                    continue;
                }

                if (!TryParseRange(kr.ReferenceRange, out double? lo, out double? hi,
                                   out bool loInclusive, out bool hiInclusive))
                {
                    skipped++;
                    continue;
                }

                // Borderline tolerance: when value is within 2% of a finite boundary,
                // we keep status="borderline" (the model's own value if it picked it,
                // otherwise we generate it).
                string computed = ComputeStatus(v, lo, hi, loInclusive, hiInclusive);

                var current = (kr.Status ?? "").Trim().ToLowerInvariant();
                if (current != computed)
                {
                    correctionsLog.Add(
                        $"  {kr.Parameter}: '{current}' -> '{computed}' (value={kr.Value}, ref={kr.ReferenceRange})");
                    kr.Status = computed;
                    corrected++;
                }
            }

            // Rebuild abnormal_findings to match the corrected statuses.
            // We keep the model's existing entries when the parameter is still
            // high/low/borderline, and drop the entries whose parameter has been
            // re-classified as 'normal'.
            if (result.AbnormalFindings != null)
            {
                var abnormalKeys = new HashSet<string>(
                    result.KeyResults
                        .Where(k => k.Status is "high" or "low" or "borderline")
                        .Select(k => (k.Parameter ?? "").Trim().ToLowerInvariant()));

                result.AbnormalFindings = result.AbnormalFindings
                    .Where(a => abnormalKeys.Contains((a.Parameter ?? "").Trim().ToLowerInvariant()))
                    .ToList();
            }

            if (corrected > 0 && logger != null)
            {
                logger.LogWarning(
                    "StatusValidator corrected {Corrected} parameter status(es):\n{Details}",
                    corrected, string.Join("\n", correctionsLog));
            }

            return new ValidationStats(result.KeyResults.Count, corrected, skipped);
        }

        // ---------------- value parser ----------------

        private static readonly Regex NumberRx = new(@"-?\d+([.,]\d+)?", RegexOptions.Compiled);

        public static bool TryParseValue(string? raw, out double value)
        {
            value = 0;
            if (string.IsNullOrWhiteSpace(raw)) return false;
            var m = NumberRx.Match(raw);
            if (!m.Success) return false;
            return double.TryParse(m.Value.Replace(',', '.'),
                NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        // ---------------- range parser ----------------
        // Returns the inclusive/exclusive flags so we apply the math exactly.

        public static bool TryParseRange(string? raw,
            out double? lo, out double? hi, out bool loInclusive, out bool hiInclusive)
        {
            lo = null; hi = null; loInclusive = true; hiInclusive = true;
            if (string.IsNullOrWhiteSpace(raw)) return false;

            // Strip unit-suffix after a slash (e.g. "13.2 - 17.2 / g/dL") and any
            // explanatory tail introduced by "—" or "-" followed by a non-digit.
            string s = raw.Trim();
            int slash = s.IndexOf('/');
            if (slash >= 0) s = s[..slash];
            int dash = s.IndexOf('—');
            if (dash >= 0) s = s[..dash];

            s = s.Replace("≤", "<=").Replace("≥", ">=").Trim();

            // <=X  / <X
            var m = Regex.Match(s, @"^\s*<\s*(=)?\s*(-?\d+([.,]\d+)?)\s*$");
            if (m.Success)
            {
                if (!double.TryParse(m.Groups[2].Value.Replace(',', '.'),
                    NumberStyles.Float, CultureInfo.InvariantCulture, out var x)) return false;
                hi = x; hiInclusive = m.Groups[1].Success;
                return true;
            }

            // >=X  / >X
            m = Regex.Match(s, @"^\s*>\s*(=)?\s*(-?\d+([.,]\d+)?)\s*$");
            if (m.Success)
            {
                if (!double.TryParse(m.Groups[2].Value.Replace(',', '.'),
                    NumberStyles.Float, CultureInfo.InvariantCulture, out var x)) return false;
                lo = x; loInclusive = m.Groups[1].Success;
                return true;
            }

            // X - Y  (most common)
            m = Regex.Match(s, @"^\s*(-?\d+([.,]\d+)?)\s*[-–—]\s*(-?\d+([.,]\d+)?)\s*$");
            if (m.Success)
            {
                if (!double.TryParse(m.Groups[1].Value.Replace(',', '.'),
                        NumberStyles.Float, CultureInfo.InvariantCulture, out var a)) return false;
                if (!double.TryParse(m.Groups[3].Value.Replace(',', '.'),
                        NumberStyles.Float, CultureInfo.InvariantCulture, out var b)) return false;
                lo = Math.Min(a, b); hi = Math.Max(a, b);
                loInclusive = true; hiInclusive = true;
                return true;
            }

            return false;
        }

        // ---------------- status compute ----------------

        /// <summary>
        /// "Borderline" band width, expressed as a fraction of the reference
        /// range WIDTH (hi - lo) when both boundaries are finite. Using the
        /// range width — instead of the boundary value as we did before — makes
        /// the tolerance scale with how tight the analyte's normal range is:
        ///   * Densitate urinară (1.005 - 1.03, width 0.025) → ±0.00125 band
        ///     so 1.024 sits firmly in the middle and is "normal".
        ///   * Glycemia (70 - 100, width 30)              → ±1.5 band
        ///   * Cholesterol total (&lt; 200, only hi)      → falls back to the
        ///     boundary-relative tolerance (no width to anchor on).
        /// The old boundary-relative tolerance flagged ~60% of mid-range
        /// values as borderline whenever the analyte had a narrow window —
        /// that's the bug the user spotted on Densitate=1.024.
        /// </summary>
        private const double BorderlineTolerancePct = 0.05;

        public static string ComputeStatus(double v, double? lo, double? hi,
            bool loInc, bool hiInc)
        {
            bool aboveLo = lo == null || (loInc ? v >= lo.Value : v > lo.Value);
            bool belowHi = hi == null || (hiInc ? v <= hi.Value : v < hi.Value);

            if (aboveLo && belowHi)
            {
                // In-range: check whether we're sitting on a boundary edge.
                // Strategy: when BOTH lo and hi are finite, use the range
                // WIDTH as the tolerance anchor. Otherwise (open-ended range
                // like "< 200"), fall back to the old boundary-value anchor.
                bool nearLo = false, nearHi = false;
                if (lo.HasValue && hi.HasValue && hi.Value > lo.Value)
                {
                    double width = hi.Value - lo.Value;
                    double band = width * BorderlineTolerancePct;
                    nearLo = Math.Abs(v - lo.Value) < band;
                    nearHi = Math.Abs(v - hi.Value) < band;
                }
                else
                {
                    if (lo.HasValue && lo.Value != 0)
                        nearLo = Math.Abs(v - lo.Value) / Math.Abs(lo.Value) < BorderlineTolerancePct;
                    if (hi.HasValue && hi.Value != 0)
                        nearHi = Math.Abs(v - hi.Value) / Math.Abs(hi.Value) < BorderlineTolerancePct;
                }
                return (nearLo || nearHi) ? "borderline" : "normal";
            }
            return belowHi ? "low" : "high";
        }
    }
}
