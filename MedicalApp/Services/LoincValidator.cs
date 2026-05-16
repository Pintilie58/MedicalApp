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

        /// <summary>
        /// Entries whose Gemini-emitted code was wrong BUT we could find the
        /// correct code in the DB by searching on the long_name head term.
        /// These were RECOVERED (code replaced with the DB-derived one)
        /// instead of being nulled.
        /// </summary>
        public int Recovered { get; set; }

        /// <summary>Entries with no LOINC code at all (Gemini emitted null).</summary>
        public int NoCode { get; set; }
    }

    /// <summary>
    /// Pas 3 of the LOINC pipeline: validates the codes Gemini emitted against
    /// our local <c>LoincDictionary</c> table (~97k ACTIVE entries seeded from
    /// the full LOINC release).
    /// <para>
    /// Behaviour (Option A — "trust the code"):
    ///   1. <b>Code IN DB</b> -> accepted unconditionally. The code is the
    ///      contract; long_name is just a human-readable label that the model
    ///      may have phrased differently (e.g. "Erythrocyte mean corpuscular
    ///      volume" vs the canonical "MCV"). If long_name disagrees we log
    ///      at debug level for later inspection but never overrule the code.
    ///   2. <b>Code NOT in DB</b> -> we try to RECOVER it by looking up DB
    ///      entries whose canonical long_name matches the long_name Gemini
    ///      provided. Useful when the model emitted a check-digit-shifted
    ///      variant (e.g. 2085-2 instead of 2085-9 for HDL cholesterol).
    ///      If recovery succeeds, the code is replaced; otherwise it stays
    ///      as-is and is logged at info level.
    ///   3. <b>No code</b> -> counted, untouched.
    /// </para>
    /// <para>
    /// Trade-off accepted: this policy will silently accept a code that
    /// describes a DIFFERENT analyte than the parameter label (the classical
    /// "Lipase=2571-8 which is actually Triglyceride" case). We rely on the
    /// prompt's self-consistency check + few-shot examples to prevent that
    /// at the source. In return we no longer FALSELY null good codes whose
    /// long_name simply uses different English wording.
    /// </para>
    /// <para>
    /// The validator mutates the <see cref="InterpretationResult"/> in place
    /// (replacing LoincCode / LoincLongName on a successful recovery). The
    /// caller is expected to re-serialize the result after this call if it
    /// wants the persisted JSON to reflect the corrections.
    /// </para>
    /// </summary>
    public static class LoincValidator
    {
        // Cache key for the dictionary of valid LOINC codes. We load the
        // entire dictionary into memory once per process boot (~97k entries
        // ~ 20 MB) and refresh hourly. Cheap and lookup-fast.
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
            // Also load a head-term index used by the recovery lookup.
            var (dict, headIndex) = await GetDictionaryAsync(db, cache, ct);

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
                    // Code is not in our subset. Try to RECOVER it by looking up
                    // a code whose canonical long_name matches Gemini's. This
                    // helps when the model emitted a check-digit-shifted variant
                    // (e.g. 2085-2 instead of 2085-9 for HDL cholesterol).
                    var recoveredCode = TryRecoverByLongName(kr.LoincLongName, dict, headIndex);
                    if (recoveredCode != null)
                    {
                        logger.LogInformation(
                            "LoincValidator: RECOVERED parameter \"{Param}\". " +
                            "Gemini said code={WrongCode} long_name=\"{Gemini}\" (not in DB). " +
                            "Replaced with DB code={RightCode}.",
                            kr.Parameter, kr.LoincCode, kr.LoincLongName, recoveredCode);

                        kr.LoincCode = recoveredCode;
                        kr.LoincLongName = dict[recoveredCode];
                        stats.Recovered++;
                    }
                    else
                    {
                        stats.OutOfSubset++;
                        logger.LogInformation(
                            "LoincValidator: parameter \"{Param}\" code {Code} not in local subset (kept as-is).",
                            kr.Parameter, kr.LoincCode);
                    }
                    continue;
                }

                // Code IS in our subset. Option A policy: TRUST THE CODE.
                // The code is the contract; long_name is just a human-readable
                // label that the model may have phrased differently (e.g.
                // "Erythrocyte mean corpuscular volume" vs the canonical "MCV").
                // We accept the code unconditionally. If the long_name disagrees,
                // we log at debug level so an operator can investigate later
                // without polluting the warning stream.
                var headDb = ExtractHeadTerm(dbLongName);
                var headGemini = ExtractHeadTerm(kr.LoincLongName);
                if (!string.IsNullOrEmpty(headGemini) && !HeadTermsMatch(headDb, headGemini))
                {
                    logger.LogDebug(
                        "LoincValidator: code {Code} accepted for \"{Param}\" but long_name disagrees " +
                        "(Gemini=\"{Gemini}\" vs DB=\"{DbName}\"). Trusting the code per policy.",
                        kr.LoincCode, kr.Parameter, kr.LoincLongName, dbLongName);
                }
                stats.ValidatedHigh++;
            }

            // Per-call summary line.
            logger.LogInformation(
                "LoincValidator summary: total={Total} validated_high={High} recovered={Rec} " +
                "mismatch_nulled={Corr} out_of_subset={OOS} no_code={None}.",
                stats.Total, stats.ValidatedHigh, stats.Recovered, stats.CorrectedToNull,
                stats.OutOfSubset, stats.NoCode);

            return stats;
        }

        // =====================================================================
        // Dictionary loading + caching
        // =====================================================================
        /// <summary>
        /// Loads (and caches) the LOINC dictionary as TWO structures:
        ///  - <c>dict</c>: code -> long_name (used to validate any code Gemini emits)
        ///  - <c>headIndex</c>: head_term -> list of codes (used by the recovery
        ///    lookup when Gemini's code is wrong but its long_name is right).
        /// The index is keyed on the head term ONLY (everything before '[' or '('),
        /// lowercase, so we get fast O(1) candidate lookup followed by a small
        /// linear scan to pick the best matching variant.
        /// </summary>
        private static async Task<(Dictionary<string, string> dict, Dictionary<string, List<string>> headIndex)>
            GetDictionaryAsync(AppDbContext db, IMemoryCache cache, CancellationToken ct)
        {
            if (cache.TryGetValue(CacheKey_ValidCodes, out (Dictionary<string, string>, Dictionary<string, List<string>>) cached)
                && cached.Item1 != null)
                return cached;

            var rows = await db.LoincDictionary
                .Select(e => new { e.LoincCode, e.LongCommonName })
                .ToListAsync(ct);

            var dict = rows.ToDictionary(
                r => r.LoincCode,
                r => r.LongCommonName,
                StringComparer.OrdinalIgnoreCase);

            // Build the head-term index. Many rows share the same head term
            // (e.g. all "Glucose [...]" variants), so each key maps to a list.
            var headIndex = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in rows)
            {
                var head = ExtractHeadTerm(r.LongCommonName);
                if (string.IsNullOrEmpty(head)) continue;
                if (!headIndex.TryGetValue(head, out var bucket))
                {
                    bucket = new List<string>(2);
                    headIndex[head] = bucket;
                }
                bucket.Add(r.LoincCode);
            }

            cache.Set(CacheKey_ValidCodes, (dict, headIndex), CacheTtl);
            return (dict, headIndex);
        }

        // =====================================================================
        // Recovery lookup: find the correct code from Gemini's long_name
        // =====================================================================
        /// <summary>
        /// Attempts to recover the correct LOINC code when Gemini emitted a
        /// long_name that does not match the canonical name of the code it
        /// also emitted. The model is usually correct about WHICH ANALYTE
        /// the test measures (the long_name) but wrong about the digits.
        /// <para>
        /// Strategy: extract Gemini's head term, find all DB codes that share
        /// the same head term, then prefer the variant whose full canonical
        /// name most overlaps with Gemini's long_name (token-overlap score).
        /// </para>
        /// Returns null when no head-term candidates exist (i.e. Gemini's
        /// long_name was also wrong).
        /// </summary>
        private static string? TryRecoverByLongName(
            string? geminiLongName,
            Dictionary<string, string> dict,
            Dictionary<string, List<string>> headIndex)
        {
            if (string.IsNullOrWhiteSpace(geminiLongName)) return null;

            var head = ExtractHeadTerm(geminiLongName);
            if (string.IsNullOrEmpty(head)) return null;

            // Exact head-term hit?
            if (headIndex.TryGetValue(head, out var candidates) && candidates.Count > 0)
                return PickBestCandidate(geminiLongName, candidates, dict);

            // Fallback: try a permissive token-overlap search across all heads.
            // Only triggered when no exact head match; cheap enough for ~97k entries.
            var geminiTokens = TokenSet(head);
            if (geminiTokens.Count == 0) return null;

            string? bestKey = null; int bestScore = 0;
            foreach (var kv in headIndex)
            {
                var t = TokenSet(kv.Key);
                int score = t.Intersect(geminiTokens, StringComparer.OrdinalIgnoreCase).Count();
                if (score > bestScore) { bestScore = score; bestKey = kv.Key; }
            }
            if (bestKey == null || bestScore == 0) return null;

            return PickBestCandidate(geminiLongName, headIndex[bestKey], dict);
        }

        /// <summary>
        /// When multiple codes share the same head term (e.g. all "Glucose..."
        /// variants), picks the one whose full canonical name has the most
        /// token overlap with Gemini's long_name. This lets us discriminate
        /// "Glucose in Serum" from "Glucose in Urine".
        /// </summary>
        private static string PickBestCandidate(
            string geminiLongName,
            List<string> codes,
            Dictionary<string, string> dict)
        {
            if (codes.Count == 1) return codes[0];

            var geminiTokens = TokenSet(geminiLongName);
            string best = codes[0];
            int bestScore = -1;
            foreach (var code in codes)
            {
                if (!dict.TryGetValue(code, out var name)) continue;
                int score = TokenSet(name).Intersect(geminiTokens, StringComparer.OrdinalIgnoreCase).Count();
                if (score > bestScore) { bestScore = score; best = code; }
            }
            return best;
        }

        /// <summary>Tokenize a string into a set of significant words (length &gt;=4, lowercase).</summary>
        private static HashSet<string> TokenSet(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return new HashSet<string>();
            return s.ToLowerInvariant()
                .Split(new[] { ' ', '-', '/', ',', '.', ';', ':', '[', ']', '(', ')' },
                       StringSplitOptions.RemoveEmptyEntries)
                .Where(t => t.Length >= 4)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
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
