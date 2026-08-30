using System.Globalization;
using System.Text.RegularExpressions;
using MedicalApp.Models;

namespace MedicalApp.Services
{
    /// <summary>
    /// Guarantees that the "out of range" section lists EVERY out-of-range
    /// analyte. The model writes <c>abnormal_findings</c> itself and on dense
    /// reports it silently drops some (observed: 8 listed out of 12 actual).
    /// That is a data-completeness problem, so it gets a deterministic answer
    /// instead of a stronger prompt: whatever is high / low / borderline in
    /// <c>key_results</c> MUST appear in <c>abnormal_findings</c>.
    ///
    /// Nothing is ever removed — an entry written by the model is kept as is,
    /// only the missing ones are added, using the analyte's own explanation and
    /// a severity computed from how far the value sits outside its range.
    /// </summary>
    public static class AbnormalFindingsCompleter
    {
        public static int Complete(InterpretationResult? result)
        {
            if (result?.KeyResults == null || result.KeyResults.Count == 0) return 0;

            var findings = result.AbnormalFindings ??= new List<AbnormalFinding>();

            var alreadyListed = findings
                .Where(f => !string.IsNullOrWhiteSpace(f.Parameter))
                .Select(f => Key(f.Parameter))
                .ToHashSet(StringComparer.Ordinal);

            int added = 0;
            foreach (var kr in result.KeyResults)
            {
                if (string.IsNullOrWhiteSpace(kr.Parameter)) continue;
                if (!IsOutOfRange(kr.Status)) continue;
                if (!alreadyListed.Add(Key(kr.Parameter))) continue;

                findings.Add(new AbnormalFinding
                {
                    Parameter = kr.Parameter.Trim(),
                    Severity = Severity(kr),
                    Explanation = BuildExplanation(kr)
                });
                added++;
            }

            return added;
        }

        private static string Key(string parameter) =>
            Regex.Replace(parameter.Trim().ToLowerInvariant(), @"[\s\.\-_]+", "");

        private static bool IsOutOfRange(string? status) =>
            status != null && status.Trim().ToLowerInvariant() is "high" or "low" or "borderline";

        private static string BuildExplanation(KeyResult kr)
        {
            // The analyte already carries its own explanation (written by the
            // model). Reusing it keeps the section consistent and language-correct
            // without inventing medical text here.
            if (!string.IsNullOrWhiteSpace(kr.Explanation))
                return kr.Explanation!.Trim();

            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(kr.Value))
                parts.Add($"{kr.Value}{(string.IsNullOrWhiteSpace(kr.Unit) ? "" : " " + kr.Unit)}");
            if (!string.IsNullOrWhiteSpace(kr.ReferenceRange))
                parts.Add(kr.ReferenceRange!.Trim());
            return string.Join(" / ", parts);
        }

        /// <summary>
        /// mild / moderate / severe from the relative distance to the breached
        /// bound (&lt;20% mild, &lt;50% moderate, otherwise severe). Borderline is
        /// always mild. When the range cannot be parsed we stay conservative.
        /// </summary>
        private static string Severity(KeyResult kr)
        {
            var status = kr.Status?.Trim().ToLowerInvariant();
            if (status == "borderline") return "mild";

            var value = ParseNumber(kr.Value);
            var (low, high) = ParseRange(kr.ReferenceRange);
            if (value == null) return "mild";

            double? deviation = null;
            if (status == "high" && high != null && high != 0)
                deviation = (value.Value - high.Value) / Math.Abs(high.Value);
            else if (status == "low" && low != null && low != 0)
                deviation = (low.Value - value.Value) / Math.Abs(low.Value);

            if (deviation == null || deviation <= 0) return "mild";
            return deviation < 0.20 ? "mild" : deviation < 0.50 ? "moderate" : "severe";
        }

        private static double? ParseNumber(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            var m = Regex.Match(raw.Replace(',', '.'), @"-?\d+(\.\d+)?");
            return m.Success && double.TryParse(m.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
                ? d : null;
        }

        private static (double? Low, double? High) ParseRange(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return (null, null);
            var text = raw.Replace(',', '.');

            // "< 150" / "<=150" -> upper bound only; "> 40" -> lower bound only.
            var single = Regex.Match(text, @"(?<op>[<>]=?)\s*(?<num>\d+(\.\d+)?)");
            var pair = Regex.Matches(text, @"\d+(\.\d+)?");

            if (pair.Count >= 2
                && double.TryParse(pair[0].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var a)
                && double.TryParse(pair[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var b))
                return (Math.Min(a, b), Math.Max(a, b));

            if (single.Success
                && double.TryParse(single.Groups["num"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var n))
                return single.Groups["op"].Value.StartsWith('<') ? (null, n) : (n, null);

            return (null, null);
        }
    }
}
