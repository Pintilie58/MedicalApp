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
        /// code that is NOT in our DB. The model is usually correct about WHICH
        /// ANALYTE the test measures (the long_name) but sometimes off on
        /// the check digit or emits a DEPRECATED code we filtered out at seed.
        /// <para>
        /// STRICT POLICY (no permissive fallback): we only recover when we
        /// can prove the swap is safe. The previous "tokens overlap on any
        /// head" fallback was too aggressive in practice - it collapsed
        /// multiple distinct urinalysis parameters onto the same "magnet"
        /// code (95233-3) whenever their long_names shared the word "Urine"
        /// or "Test strip", and merged Borrelia IgG with Borrelia IgM.
        /// </para>
        /// <para>
        /// New rules:
        ///   1. Head-term must match EXACTLY one bucket in the head index
        ///      (no fuzzy fallback).
        ///   2. Among the candidates that share that head, the best one must
        ///      score STRICTLY MORE than every other candidate (a unique
        ///      winner). Ties -> abort. This prevents Borrelia.IgG and
        ///      Borrelia.IgM from collapsing onto the same recovered code.
        ///   3. The winning score must be at least 2 (i.e. share at least
        ///      two significant words with Gemini's long_name). A single
        ///      shared word like "Urine" is too weak.
        /// </para>
        /// Returns null when any rule fails - the caller then keeps the
        /// original code as "out_of_subset" (better than wrong grouping).
        /// </summary>
        private static string? TryRecoverByLongName(
            string? geminiLongName,
            Dictionary<string, string> dict,
            Dictionary<string, List<string>> headIndex)
        {
            if (string.IsNullOrWhiteSpace(geminiLongName)) return null;

            var head = ExtractHeadTerm(geminiLongName);
            if (string.IsNullOrEmpty(head)) return null;

            // Rule 1: exact head-term hit, no permissive fallback.
            if (!headIndex.TryGetValue(head, out var candidates) || candidates.Count == 0)
                return null;

            // Rule 2 + 3: unique winner by token-overlap, score >= 2.
            var geminiTokens = TokenSet(geminiLongName);
            if (geminiTokens.Count < 2) return null; // Not enough signal to discriminate.

            string? best = null;
            int bestScore = -1;
            int runnerUpScore = -1;
            foreach (var code in candidates)
            {
                if (!dict.TryGetValue(code, out var name)) continue;
                int score = TokenSet(name).Intersect(geminiTokens, StringComparer.OrdinalIgnoreCase).Count();
                if (score > bestScore)
                {
                    runnerUpScore = bestScore;
                    bestScore = score;
                    best = code;
                }
                else if (score > runnerUpScore)
                {
                    runnerUpScore = score;
                }
            }

            // Need an unambiguous winner with non-trivial agreement.
            if (best == null) return null;
            if (bestScore < 2) return null;                  // Rule 3: too weak.
            if (bestScore == runnerUpScore) return null;     // Rule 2: tie -> abort.

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
