using System.Security.Cryptography;
using System.Text;
using MedicalApp.Data;
using MedicalApp.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MedicalApp.Services
{
    /// <summary>
    /// Persistent, GLOBAL store of already-resolved LOINC mappings, sitting in
    /// front of the Python matcher (see <see cref="LoincMatcherClient"/>).
    ///
    /// Effects at scale: the matcher only sees analyte names it has never met
    /// (in practice under 10% of a report after the first weeks), and a report
    /// whose analytes are all known is coded even when the Python service is
    /// down — graceful degradation instead of a missing-codes interpretation.
    ///
    /// Every failure here is swallowed: a cache is an optimisation, never a
    /// reason for an interpretation to fail. It also opens its OWN DbContext
    /// scope so it can never save the caller's unrelated tracked changes.
    /// KILL SWITCH: "LoincMatcher:Cache:Enabled" = false.
    /// </summary>
    public class LoincMatchCacheStore
    {
        private readonly IServiceScopeFactory _scopes;
        private readonly LoincContextVocabulary _vocabulary;
        private readonly LoincMatcherSettings _settings;
        private readonly ILogger<LoincMatchCacheStore> _logger;

        public LoincMatchCacheStore(
            IServiceScopeFactory scopes,
            LoincContextVocabulary vocabulary,
            IOptions<LoincMatcherSettings> settings,
            ILogger<LoincMatchCacheStore> logger)
        {
            _scopes = scopes;
            _vocabulary = vocabulary;
            _settings = settings.Value;
            _logger = logger;
        }

        public bool Enabled => _settings.Cache.Enabled;

        public string PipelineVersion =>
            string.IsNullOrWhiteSpace(_settings.Cache.PipelineVersion)
                ? "v1"
                : _settings.Cache.PipelineVersion.Trim();

        /// <summary>
        /// The lookup key — deliberately built ONLY from what is stable:
        ///
        ///   • the analyte name exactly as printed in the lab report (the
        ///     PDF's own language: "Glucoza", "Glukose", "Glycémie"),
        ///   • the unit, canonicalised (µL = uL),
        ///   • the specimen/method markers found in the section header and the
        ///     analyte line, using the vocabulary owned by the Python matcher,
        ///   • the pipeline version.
        ///
        /// What is deliberately EXCLUDED: Gemini's English normalization. The
        /// model rephrases it between runs ("Hematocrit [Volume Fraction] in
        /// Blood" vs "…in Blood by Automated count", "Carcinoembryonic Ag" vs
        /// "…antigen … immunoassay") while resolving to the SAME LOINC code, so
        /// hashing it produced a 0% hit rate and duplicate rows. Keying on the
        /// printed name also makes the app's output reproducible: the same
        /// report always yields the same codes.
        ///
        /// A real difference still separates entries: another unit, another
        /// printed method (impedance vs flow cytometry) or another specimen
        /// (serum vs urine) changes the tokens, hence the key.
        /// </summary>
        public string BuildKeyMaterial(
            LoincContextVocabulary.Snapshot vocabulary,
            string? printedName, string? unit, string? normalizedEn,
            string? panelHeaderRaw, string? analyteLineRaw)
        {
            // The printed name is the analyte's identity. Only if the extractor
            // gave us nothing do we fall back to the English normalization.
            var identity = LoincContextVocabulary.Normalize(printedName);
            if (identity.Length == 0)
                identity = LoincContextVocabulary.Normalize(normalizedEn);

            return string.Join('\u001f', new[]
            {
                PipelineVersion,
                identity,
                CanonicalUnit(unit),
                _vocabulary.DecisiveTokens(vocabulary, panelHeaderRaw, analyteLineRaw)
            });
        }

        /// <summary>Key + the exact material it was hashed from (persisted for diagnostics).</summary>
        public readonly record struct CacheKey(string Key, string Material);

        public CacheKey BuildKey(
            LoincContextVocabulary.Snapshot vocabulary,
            string? printedName, string? unit, string? normalizedEn,
            string? panelHeaderRaw, string? analyteLineRaw)
        {
            var material = BuildKeyMaterial(
                vocabulary, printedName, unit, normalizedEn, panelHeaderRaw, analyteLineRaw);

            var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material)))
                             .ToLowerInvariant();

            return new CacheKey(key, material);
        }

        /// <summary>"10^3/µL", "10^3/uL" and " 10^3/UL " are the same unit.</summary>
        private static string CanonicalUnit(string? unit)
        {
            var n = LoincContextVocabulary.Normalize(unit)
                .Replace("µ", "u", StringComparison.Ordinal)
                .Replace("μ", "u", StringComparison.Ordinal);
            return string.Concat(n.Where(c => !char.IsWhiteSpace(c)));
        }

        /// <summary>Reads the known mappings. Returns an empty map when disabled or on any error.</summary>
        public async Task<Dictionary<string, LoincMatchCacheEntry>> GetAsync(
            IReadOnlyCollection<string> keys, CancellationToken ct = default)
        {
            var empty = new Dictionary<string, LoincMatchCacheEntry>(StringComparer.Ordinal);
            if (!Enabled || keys.Count == 0) return empty;

            try
            {
                using var scope = _scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var rows = await db.LoincMatchCache
                    .AsNoTracking()
                    .Where(e => keys.Contains(e.CacheKey))
                    .ToListAsync(ct);
                return rows.ToDictionary(r => r.CacheKey, r => r, StringComparer.Ordinal);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "LOINC cache read failed; falling back to the matcher.");
                return empty;
            }
        }

        /// <summary>Remembers new mappings. Duplicate keys (parallel jobs) are ignored.</summary>
        public async Task SaveAsync(
            IReadOnlyCollection<LoincMatchCacheEntry> entries, CancellationToken ct = default)
        {
            if (!Enabled || entries.Count == 0) return;

            try
            {
                using var scope = _scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

                var keys = entries.Select(e => e.CacheKey).Distinct(StringComparer.Ordinal).ToList();
                var existing = await db.LoincMatchCache
                    .Where(e => keys.Contains(e.CacheKey))
                    .Select(e => e.CacheKey)
                    .ToListAsync(ct);

                var fresh = entries
                    .GroupBy(e => e.CacheKey, StringComparer.Ordinal)
                    .Select(g => g.First())
                    .Where(e => !existing.Contains(e.CacheKey, StringComparer.Ordinal))
                    .ToList();

                if (fresh.Count == 0) return;

                db.LoincMatchCache.AddRange(fresh);
                await db.SaveChangesAsync(ct);
                _logger.LogInformation("LOINC cache: stored {Count} new mapping(s).", fresh.Count);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "LOINC cache write failed; the mappings will be recomputed.");
            }
        }

        /// <summary>Usage counters, so the admin can see whether the cache is earning its keep.</summary>
        public async Task TouchAsync(IReadOnlyCollection<string> keys, CancellationToken ct = default)
        {
            if (!Enabled || keys.Count == 0) return;

            try
            {
                using var scope = _scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var now = DateTime.UtcNow;
                await db.LoincMatchCache
                    .Where(e => keys.Contains(e.CacheKey))
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(e => e.HitCount, e => e.HitCount + 1)
                        .SetProperty(e => e.LastUsedAt, now), ct);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "LOINC cache counters not updated (harmless).");
            }
        }
    }

    /// <summary>Settings for the persistent LOINC mapping cache ("LoincMatcher:Cache").</summary>
    public class LoincMatchCacheSettings
    {
        /// <summary>Master switch. False ⇒ every analyte goes to the Python matcher, as before.</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>
        /// Bump this string whenever the matcher's behaviour changes (weights,
        /// anchors, dictionary re-seed). Old rows are then simply never read.
        /// </summary>
        public string PipelineVersion { get; set; } = "v1";
    }
}
