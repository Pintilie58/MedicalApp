using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace MedicalApp.Services
{
    /// <summary>
    /// Independent post-Gemini sanity check that compares the number of
    /// parameters Gemini emitted in <c>key_results</c> against a heuristic
    /// row-count derived from the PDF's text layer (via <see cref="PdfTextExtractor"/>).
    ///
    /// PURPOSE: catch the failure mode where Gemini visually misses a row in
    /// the PDF — most commonly the FIRST data row directly under a section
    /// title or column headers. The existing self-audit in
    /// <c>GeminiMedicalInterpretationService.CallGeminiAsync</c> relies on
    /// Gemini declaring its own <c>expected_count</c>; it does NOT catch the
    /// case where Gemini never saw the row at all (then both
    /// <c>expected_count</c> and <c>key_results</c> are short).
    ///
    /// This auditor is the INDEPENDENT cross-check using a different source
    /// of truth (the PDF's text layer extracted by PdfPig).
    ///
    /// DESIGN PRINCIPLES:
    ///   - NEVER modifies the interpretation result. Pure observation.
    ///   - Logs WARNING when a divergence is detected. Operations / Admin
    ///     dashboards pick it up from there.
    ///   - Language-agnostic, lab-agnostic: heuristic based on
    ///     ""any line that contains at least 2 alphabetic words plus a
    ///     numeric value"" — works for RO, EN, FR, ES, DE labs without
    ///     hard-coding analyte names.
    ///   - Behind a feature flag (<c>CompletenessAudit:Enabled</c>) so it
    ///     can be turned off in one click if it ever logs false alarms.
    /// </summary>
    public static class InterpretationCompletenessAuditor
    {
        // Heuristic threshold: a 0-1 row drift is normal (off-by-one between
        // "rows the lab printed" and "analytes Gemini logically grouped").
        // Anything >= 2 missing rows AND >= 10% of total is suspicious.
        private const int AbsoluteDiffThreshold = 2;
        private const double RelativeDiffThreshold = 0.10;

        // A "candidate analyte row" — heuristic:
        //   * starts with at least 2 letters (analyte name token)
        //   * contains at least one number after some whitespace (the value)
        //   * not too short (filter out page numbers, page headers)
        // Letters include Latin-1 and Latin Extended-A to handle RO/FR/ES/DE
        // diacritics (ăâîșț, éèê, áí, äöüß).
        private static readonly Regex AnalyteRowRegex = new(
            @"^[A-Za-zÀ-ž][A-Za-zÀ-ž0-9 .,%/\-\(\)\+]+\s+[\d]+([.,][\d]+)?",
            RegexOptions.Compiled);

        // Lines that look like section titles or page footers (skip).
        private static readonly Regex SectionTitleRegex = new(
            @"^[A-ZĂÂÎȘȚ\s]{4,}$",
            RegexOptions.Compiled);

        /// <summary>
        /// Counts analyte-like rows in extracted PDF text.
        /// </summary>
        public static int CountAnalyteRows(string extractedText)
        {
            if (string.IsNullOrWhiteSpace(extractedText)) return 0;

            int count = 0;
            foreach (var rawLine in extractedText.Split('\n'))
            {
                var line = rawLine.Trim();
                if (line.Length < 6) continue;            // too short
                if (line.StartsWith("--- Page")) continue; // PdfTextExtractor separator
                if (SectionTitleRegex.IsMatch(line)) continue; // section title
                if (AnalyteRowRegex.IsMatch(line)) count++;
            }
            return count;
        }

        /// <summary>
        /// Performs the audit. Returns a warning string when a significant
        /// divergence is detected (suitable for logging or attaching to a
        /// telemetry record). Returns <c>null</c> when the counts agree
        /// within tolerance — the typical case.
        /// </summary>
        public static string? Audit(
            string? extractedText,
            int keyResultsCount,
            ILogger? logger = null)
        {
            if (string.IsNullOrWhiteSpace(extractedText)) return null;

            int detected = CountAnalyteRows(extractedText);
            int diff = detected - keyResultsCount;
            if (diff < AbsoluteDiffThreshold) return null;

            double ratio = (double)diff / Math.Max(detected, 1);
            if (ratio < RelativeDiffThreshold) return null;

            var msg =
                $"Completeness audit divergence: heuristic detected ~{detected} analyte rows " +
                $"in PDF text but Gemini emitted {keyResultsCount} key_results " +
                $"(diff={diff}, ratio={ratio:P0}). Possible missing rows — operator should review.";

            logger?.LogWarning("{Audit}", msg);
            return msg;
        }
    }
}
