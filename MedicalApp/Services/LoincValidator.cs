using MedicalApp.Data;
using MedicalApp.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace MedicalApp.Services
{
    /// <summary>
    /// Aggregated outcome of running <see cref="LoincValidator"/> on a single
    /// interpretation result. Useful both for logging and for future UI badges.
    /// </summary>
    public class LoincValidationStats
    {
        /// <summary>Total number of key_results inspected.</summary>
        public int Total { get; set; }

        /// <summary>Entries whose code matched our subset AND whose long_name matched the canonical one.</summary>
        public int ValidatedHigh { get; set; }

        /// <summary>Entries whose code matched our subset but whose long_name diverged (kept as-is, only logged).</summary>
        public int LongNameMismatch { get; set; }

        /// <summary>Entries whose code matched NOTHING in the subset (likely a valid LOINC outside our 1520).</summary>
        public int OutOfSubset { get; set; }

        /// <summary>Entries whose code disagreed STRONGLY with the long_name - code was nulled out.</summary>
        public int CorrectedToNull { get; set; }

        /// <summary>Entries with no LOINC code at all (Gemini emitted null).</summary>
        public int NoCode { get; set; }
    }

    /// <summary>
    /// Pas 3 of the LOINC pipeline: validates the codes Gemini emitted against
    /// our local <c>LoincDictionary</c> table (1520 entries seeded from the
    /// LOINC Universal Lab Orders Value Set).
    /// <para>
    /// This validator is CONSERVATIVE by design:
    ///   1. <b>Code in DB + long_name head-term matches</b> -> accepted, confidence stays.
    ///   2. <b>Code in DB + long_name head-term differs</b> -> code is nulled out and
    ///      confidence is downgraded to "low". This catches the well-known LLM
    ///      pattern where the model emits a code that contradicts its own
    ///      long_name (e.g. emitting code 2571-8 ""Triglyceride"" for a row whose
    ///      long_name says ""Lipase""). Better no grouping than wrong grouping.
    ///   3. <b>Code NOT in DB</b> -> kept as-is. Our DB is only the Universal Lab
    ///      Orders subset (1520 codes); the LOINC standard has ~95.000 codes
    ///      total. A code we don't recognise may still be a perfectly valid
    ///      LOINC term. We just log it for visibility.
    /// </para>
    /// <para>
    /// The validator mutates the <see cref="InterpretationResult"/> in place
    /// (only NULL-ing the code on case 2). The caller is expected to re-serialize
    /// the result after this call if it wants the persisted JSON to reflect
    /// the corrections.
    /// </para>
    /// </summary>
    public static class LoincValidator
    {
        // Cache key for the dictionary of valid LOINC codes. We re-load the
        // entire dictionary into memory once per process boot (1520 entries
        // ~ 350 KB) and refresh hourly. Cheap and lookup-fast.
        private const string CacheKey_ValidCodes = "loinc:dictionary_v1";
        private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(1);

        public static async Task<LoincValidationStats> ValidateAsync(
            InterpretationResult result,
            AppDbContext db,
            IMemoryCache cache,
            ILogger logger,
            CancellationToken ct = default)
        {
            var stats = new LoincValidationStats();
            if (result.KeyResults == null || result.KeyResults.Count == 0)
                return stats;

            // Load the dictionary (cached). Key = LOINC code, Value = canonical long name.
            var dict = await GetDictionaryAsync(db, cache, ct);

            foreach (var kr in result.KeyResults)
            {
                stats.Total++;

                if (string.IsNullOrWhiteSpace(kr.LoincCode))
                {
                    stats.NoCode++;
                    continue;
                }

                if (!dict.TryGetValue(kr.LoincCode, out var dbLongName))
                {
                    // Code is not in our subset. May still be a valid LOINC term
                    // somewhere in the ~95.000-strong full standard. We keep it
                    // and log at Information level (not a problem).
                    stats.OutOfSubset++;
                    logger.LogInformation(
                        "LoincValidator: parameter \"{Param}\" code {Code} not in local subset (kept as-is).",
                        kr.Parameter, kr.LoincCode);
                    continue;
                }

                // Code IS in our subset. Compare head-terms.
                var headDb = ExtractHeadTerm(dbLongName);
                var headGemini = ExtractHeadTerm(kr.LoincLongName);

                if (string.IsNullOrEmpty(headGemini) ||
                    HeadTermsMatch(headDb, headGemini))
                {
                    // Match (or Gemini didn't bother to emit a long name -
                    // we trust the code alone in that case).
                    stats.ValidatedHigh++;
                }
                else
                {
                    // STRONG mismatch: the model emitted a code that does not
                    // describe the test it labelled. Most likely the code is
                    // wrong (e.g. 2571-8 paired with "Lipase..." - 2571-8 is
                    // Triglyceride). Null the code and downgrade confidence.
                    logger.LogWarning(
                        "LoincValidator: HEAD-TERM MISMATCH for parameter \"{Param}\". " +
                        "Gemini said code={Code} long_name=\"{Gemini}\" but our DB says \"{DbName}\". " +
                        "Nulling code and setting confidence=low.",
                        kr.Parameter, kr.LoincCode, kr.LoincLongName, dbLongName);

                    kr.LoincCode = null;
                    kr.LoincConfidence = "low";
                    stats.CorrectedToNull++;
                }
            }

            // Per-call summary line.
            logger.LogInformation(
                "LoincValidator summary: total={Total} validated_high={High} mismatch_corrected={Corr} " +
                "out_of_subset={OOS} no_code={None}.",
                stats.Total, stats.ValidatedHigh, stats.CorrectedToNull, stats.OutOfSubset, stats.NoCode);

            return stats;
        }

        // =====================================================================
        // Dictionary loading + caching
        // =====================================================================
        private static async Task<Dictionary<string, string>> GetDictionaryAsync(
            AppDbContext db, IMemoryCache cache, CancellationToken ct)
        {
            if (cache.TryGetValue(CacheKey_ValidCodes, out Dictionary<string, string>? cached) && cached != null)
                return cached;

            var rows = await db.LoincDictionary
                .Select(e => new { e.LoincCode, e.LongCommonName })
                .ToListAsync(ct);
            var dict = rows.ToDictionary(
                r => r.LoincCode,
                r => r.LongCommonName,
                StringComparer.OrdinalIgnoreCase);

            cache.Set(CacheKey_ValidCodes, dict, CacheTtl);
            return dict;
        }

        // =====================================================================
        // Head-term extraction + comparison
        // =====================================================================
        /// <summary>
        /// Extracts the "head term" of a LOINC long name - everything before
        /// the first '[' or '(' bracket - and normalizes it to lowercase
        /// for comparison. The head term is the analyte name (e.g.
        /// "Gamma glutamyl transferase" out of
        /// "Gamma glutamyl transferase [Enzymatic activity/volume] in Serum or Plasma").
        /// </summary>
        internal static string ExtractHeadTerm(string? longName)
        {
            if (string.IsNullOrWhiteSpace(longName)) return string.Empty;
            var s = longName.Trim();
            int cut = s.IndexOfAny(new[] { '[', '(' });
            if (cut > 0) s = s[..cut];
            return s.Trim().ToLowerInvariant();
        }

        /// <summary>
        /// Compares two head terms. Returns true when they share at least one
        /// significant token (length &gt;= 4). This is permissive enough to
        /// handle minor variants ("Cholesterol" vs "Cholesterol in HDL") but
        /// catches the clear-cut wrong-code cases ("Triglyceride" vs "Lipase").
        /// </summary>
        internal static bool HeadTermsMatch(string headA, string headB)
        {
            if (string.IsNullOrEmpty(headA) || string.IsNullOrEmpty(headB)) return false;
            if (headA == headB) return true;

            // Tokenize on whitespace + punctuation common in LOINC names.
            var splitChars = new[] { ' ', '-', '/', ',', '.', ';', ':' };
            var setA = headA.Split(splitChars, StringSplitOptions.RemoveEmptyEntries)
                            .Where(t => t.Length >= 4)
                            .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var setB = headB.Split(splitChars, StringSplitOptions.RemoveEmptyEntries)
                            .Where(t => t.Length >= 4)
                            .ToHashSet(StringComparer.OrdinalIgnoreCase);

            // If either side has no >=4-letter words, fall back to substring check.
            if (setA.Count == 0 || setB.Count == 0)
                return headA.Contains(headB) || headB.Contains(headA);

            return setA.Overlaps(setB);
        }
    }
}
