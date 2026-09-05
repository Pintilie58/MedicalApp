using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MedicalApp.Services
{
    /// <summary>
    /// The specimen/method vocabulary used to build a STABLE persistent-cache
    /// key out of the lab PDF's own words.
    ///
    /// It is FETCHED from the Python matcher (`/loinc/context-keywords`) and
    /// never hard-coded here. Reason: the app is used in 20+ languages and the
    /// matcher already owns hand-curated markers for them ("impedanță" RO,
    /// "impedancia" ES/PT, "Durchflusszytometrie" DE, "cytométrie en flux" FR,
    /// "mikroskopia" PL…). Two copies of that list would drift apart, and a
    /// missing language would let two analytes measured by different methods
    /// share a cache key — a medical error. One source of truth instead.
    ///
    /// When the vocabulary cannot be fetched we degrade to the CONSERVATIVE
    /// key (the whole context text): fewer cache hits, never a wrong reuse.
    /// </summary>
    public sealed class LoincContextVocabulary
    {
        private readonly IHttpClientFactory _httpFactory;
        private readonly IServiceScopeFactory _scopes;
        private readonly LoincMatcherSettings _settings;
        private readonly ILogger<LoincContextVocabulary> _logger;
        private readonly SemaphoreSlim _gate = new(1, 1);

        private Snapshot _current = Snapshot.Unavailable;
        private DateTime _nextAttemptUtc = DateTime.MinValue;

        public LoincContextVocabulary(
            IHttpClientFactory httpFactory,
            IServiceScopeFactory scopes,
            IOptions<LoincMatcherSettings> settings,
            ILogger<LoincContextVocabulary> logger)
        {
            _httpFactory = httpFactory;
            _scopes = scopes;
            _settings = settings.Value;
            _logger = logger;
        }

        /// <summary>Immutable view of the vocabulary at a point in time.</summary>
        public sealed class Snapshot
        {
            public static readonly Snapshot Unavailable = new(Array.Empty<string>());

            public Snapshot(IReadOnlyList<string> phrases) => Phrases = phrases;

            public IReadOnlyList<string> Phrases { get; }

            public bool Available => Phrases.Count > 0;
        }

        /// <summary>
        /// Cached for the lifetime of the process; a failed fetch is retried at
        /// most every 5 minutes so a cold Python service does not turn every
        /// interpretation into a hanging HTTP call.
        /// </summary>
        public async Task<Snapshot> GetAsync(CancellationToken ct = default)
        {
            if (_current.Available) return _current;
            if (DateTime.UtcNow < _nextAttemptUtc) return _current;

            await _gate.WaitAsync(ct);
            try
            {
                if (_current.Available) return _current;
                if (DateTime.UtcNow < _nextAttemptUtc) return _current;
                _nextAttemptUtc = DateTime.UtcNow.AddMinutes(5);

                var http = _httpFactory.CreateClient();
                http.BaseAddress = new Uri(_settings.BaseUrl);
                http.Timeout = TimeSpan.FromSeconds(Math.Max(_settings.TimeoutSeconds, 3));

                var payload = await http.GetFromJsonAsync<VocabularyResponse>(
                    "/loinc/context-keywords", ct);

                var phrases = (payload?.Phrases ?? new List<string>())
                    .Select(p => Normalize(p))
                    .Where(p => p.Length >= 2)
                    .Distinct(StringComparer.Ordinal)
                    // Longest first: a match on "flow cytometry" should not be
                    // hidden by an earlier match on "cytometry".
                    .OrderByDescending(p => p.Length)
                    .ThenBy(p => p, StringComparer.Ordinal)
                    .ToList();

                if (phrases.Count == 0)
                {
                    _logger.LogWarning(
                        "LOINC context vocabulary came back empty; trying the stored copy.");
                    return await LoadStoredAsync(ct);
                }

                _current = new Snapshot(phrases);
                await StoreAsync(phrases, ct);
                _logger.LogInformation(
                    "LOINC context vocabulary loaded: {Count} specimen/method phrase(s).",
                    phrases.Count);
                return _current;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Could not fetch the LOINC context vocabulary; trying the stored copy.");
                return await LoadStoredAsync(ct);
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>
        /// Falls back to the last vocabulary we ever fetched. Without this, an
        /// app restart while the Python service is down would silently change
        /// the shape of every cache key — exactly when the cache is the only
        /// thing that can still code a report.
        /// </summary>
        private async Task<Snapshot> LoadStoredAsync(CancellationToken ct)
        {
            try
            {
                using var scope = _scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<Data.AppDbContext>();
                var row = await db.LoincVocabulary.AsNoTracking()
                    .OrderBy(v => v.Id)
                    .FirstOrDefaultAsync(ct);
                if (row == null) return _current;

                var stored = System.Text.Json.JsonSerializer
                    .Deserialize<List<string>>(row.PhrasesJson) ?? new List<string>();
                if (stored.Count == 0) return _current;

                _current = new Snapshot(stored
                    .OrderByDescending(p => p.Length)
                    .ThenBy(p => p, StringComparer.Ordinal)
                    .ToList());
                _logger.LogInformation(
                    "LOINC context vocabulary restored from the database ({Count} phrase(s), fetched {When:u}).",
                    stored.Count, row.FetchedAt);
                return _current;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "No usable stored LOINC vocabulary; using the conservative cache key.");
                return _current;
            }
        }

        private async Task StoreAsync(List<string> phrases, CancellationToken ct)
        {
            try
            {
                using var scope = _scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<Data.AppDbContext>();
                var row = await db.LoincVocabulary.OrderBy(v => v.Id).FirstOrDefaultAsync(ct);
                var json = System.Text.Json.JsonSerializer.Serialize(phrases);

                if (row == null)
                {
                    db.LoincVocabulary.Add(new Models.LoincVocabularySnapshot
                    {
                        // Id is generated by the database: setting it explicitly
                        // fails with "IDENTITY_INSERT is OFF".
                        PhrasesJson = json,
                        PhraseCount = phrases.Count,
                        FetchedAt = DateTime.UtcNow
                    });
                }
                else
                {
                    row.PhrasesJson = json;
                    row.PhraseCount = phrases.Count;
                    row.FetchedAt = DateTime.UtcNow;
                }

                await db.SaveChangesAsync(ct);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not persist the LOINC vocabulary (harmless).");
            }
        }

        /// <summary>
        /// Distils the lab's context text down to the markers that can change a
        /// LOINC code. "Hemoleucograma completa - Sange - Spectroscopie de
        /// impedanta" becomes "impedanta|sange" — stable even if the lab (or
        /// the extractor) rewords everything around it.
        /// Returns the FULL normalized text when the vocabulary is unavailable.
        /// </summary>
        public string DecisiveTokens(Snapshot snapshot, params string?[] contextParts)
        {
            var context = Normalize(string.Join(' ', contextParts.Where(p => !string.IsNullOrWhiteSpace(p))));
            if (context.Length == 0) return string.Empty;
            if (!snapshot.Available) return "full:" + context;

            // Words only, punctuation as separator: "impedanta," and
            // "(sange integral)" must still yield "impedanta" / "sange".
            var words = context.Split(
                context.Where(c => !char.IsLetterOrDigit(c)).Distinct().ToArray(),
                StringSplitOptions.RemoveEmptyEntries);
            var wordSet = new HashSet<string>(words, StringComparer.Ordinal);
            var wordText = ' ' + string.Join(' ', words) + ' ';

            var found = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var phrase in snapshot.Phrases)
            {
                // Long phrases match as a word PREFIX, so native inflections
                // still count ("impedantei", "sangele", "urinar"). Short ones
                // must match a WHOLE word: substring matching would find "ser"
                // inside "seria" or "kal" inside "local" and make the key
                // jitter for no reason.
                bool hit = phrase.Contains(' ')
                    ? wordText.Contains(' ' + phrase, StringComparison.Ordinal)
                    : phrase.Length >= 5
                        ? words.Any(w => w.StartsWith(phrase, StringComparison.Ordinal))
                        : wordSet.Contains(phrase);
                if (hit) found.Add(phrase);
            }

            return string.Join('|', found);
        }

        /// <summary>
        /// Lowercase, collapse whitespace and strip diacritics — the SAME
        /// treatment the Python side applies before matching its ASCII
        /// keyword dictionaries, so "impedanță", "sérique" and
        /// "turbidimétrie" produce identical tokens on both sides.
        /// </summary>
        public static string Normalize(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return string.Empty;

            var collapsed = string.Join(' ', s!.ToLowerInvariant()
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

            var decomposed = collapsed.Normalize(NormalizationForm.FormD);
            var sb = new StringBuilder(decomposed.Length);
            foreach (var ch in decomposed)
                if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
                    sb.Append(ch);

            return sb.ToString().Normalize(NormalizationForm.FormC);
        }

        private sealed class VocabularyResponse
        {
            [JsonPropertyName("phrases")] public List<string>? Phrases { get; set; }
        }
    }
}
