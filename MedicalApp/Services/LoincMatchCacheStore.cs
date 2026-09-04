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
        private readonly LoincMatcherSettings _settings;
        private readonly ILogger<LoincMatchCacheStore> _logger;

        public LoincMatchCacheStore(
            IServiceScopeFactory scopes,
            IOptions<LoincMatcherSettings> settings,
            ILogger<LoincMatchCacheStore> logger)
        {
            _scopes = scopes;
            _settings = settings.Value;
            _logger = logger;
        }

        public bool Enabled => _settings.Cache.Enabled;

        public string PipelineVersion =>
            string.IsNullOrWhiteSpace(_settings.Cache.PipelineVersion)
                ? "v1"
                : _settings.Cache.PipelineVersion.Trim();

        /// <summary>
        /// The lookup key. Contains every input the matcher's decision depends
        /// on — the normalized English term, the unit, the raw analyte name and
        /// the two verbatim source-context strings — plus the pipeline version.
        /// </summary>
        public string BuildKey(
            string testName, string? unit, string? rawParameterName,
            string? panelHeaderRaw, string? analyteLineRaw)
        {
            static string N(string? s) =>
                s == null ? string.Empty : string.Join(' ', s.ToLowerInvariant().Split(
                    (char[]?)null, StringSplitOptions.RemoveEmptyEntries));

            var material = string.Join('\u001f', new[]
            {
                PipelineVersion, N(testName), N(unit), N(rawParameterName),
                N(panelHeaderRaw), N(analyteLineRaw)
            });

            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material)))
                          .ToLowerInvariant();
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
