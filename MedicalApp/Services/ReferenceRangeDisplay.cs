using System.Text.RegularExpressions;

namespace MedicalApp.Services
{
    /// <summary>
    /// Long, explanatory reference texts (INR therapeutic ranges, LDL risk tiers…)
    /// blow up the "Reference" column. Callers keep the short numeric form in the
    /// column and print the full text on a full-width line under the analyte.
    /// </summary>
    public static class ReferenceRangeDisplay
    {
        public const int LongThreshold = 24;
        private const string Num = @"-?\d+(?:[.,]\d+)?";
        private static readonly Regex RangeRx = new(@"(?<![\w.,])" + Num + @"\s*[-–—]\s*" + Num, RegexOptions.Compiled);
        private static readonly Regex ThresholdRx = new(@"[<>≤≥]\s*=?\s*" + Num, RegexOptions.Compiled);

        public static bool IsLong(string? raw) => raw != null && raw.Trim().Length > LongThreshold;

        /// <summary>First numeric range or threshold found in the text; "—" when none.</summary>
        public static string Short(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "-";
            var s = raw.Trim();
            if (!IsLong(s)) return s;
            var m = RangeRx.Match(s);
            var t = ThresholdRx.Match(s);
            if (m.Success && (!t.Success || m.Index <= t.Index)) return Regex.Replace(m.Value, @"\s+", "");
            if (t.Success) return Regex.Replace(t.Value, @"\s+", "");
            return "—";
        }
    }
}
