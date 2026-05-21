using MedicalApp.Models;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace MedicalApp.Services
{
    /// <summary>
    /// HTTP client that talks to the Python FastAPI LOINC matcher microservice.
    /// The matcher takes a clean English medical term (e.g. "Glucose [Mass/volume]
    /// in Serum or Plasma") and returns the canonical LOINC code resolved by a
    /// deterministic semantic + fuzzy + rules pipeline over the local
    /// LoincDictionary table.
    ///
    /// Why this exists:
    ///   LLMs (Gemini, GPT, Claude) hallucinate LOINC numeric codes — they confuse
    ///   the check digit, mix up specimens (Serum vs Blood vs Urine), pick
    ///   methods that don't match the lab report. The matcher fixes all of that
    ///   by NEVER asking the LLM to produce a numeric code. The LLM only
    ///   produces standardized English text; the matcher does the lookup.
    ///
    /// Configuration in appsettings.json:
    ///   "LoincMatcher": {
    ///     "BaseUrl": "http://localhost:8000",   // FastAPI dev server
    ///     "Enabled": true,                       // master switch
    ///     "TimeoutSeconds": 5,                   // per-parameter timeout
    ///     "MinScore": 0.55                       // discard matches below this
    ///   }
    /// </summary>
    public class LoincMatcherClient
    {
        private readonly HttpClient _http;
        private readonly LoincMatcherSettings _settings;
        private readonly ILogger<LoincMatcherClient> _logger;

        public LoincMatcherClient(
            HttpClient http,
            Microsoft.Extensions.Options.IOptions<LoincMatcherSettings> settings,
            ILogger<LoincMatcherClient> logger)
        {
            _http = http;
            _settings = settings.Value;
            _logger = logger;
        }

        /// <summary>
        /// Resolves a LOINC code for every <see cref="InterpretationResult.KeyResults"/>
        /// entry that has a non-empty <c>ParameterNormalizedEn</c>. Mutates
        /// <see cref="KeyResult.LoincCode"/>, <see cref="KeyResult.LoincLongName"/>
        /// and <see cref="KeyResult.LoincConfidence"/> in place.
        ///
        /// Safe-by-default: any individual matcher error or timeout is logged
        /// and skipped — the entry simply stays without a LOINC code, and the
        /// caller continues normally.
        /// </summary>
        public async Task<MatcherStats> MatchAllAsync(
            InterpretationResult result, CancellationToken ct = default)
        {
            var stats = new MatcherStats();
            if (!_settings.Enabled)
            {
                _logger.LogInformation("LoincMatcher disabled via configuration. Skipping LOINC resolution.");
                return stats;
            }

            if (result.KeyResults == null || result.KeyResults.Count == 0)
                return stats;

            foreach (var kr in result.KeyResults)
            {
                stats.Total++;
                if (string.IsNullOrWhiteSpace(kr.ParameterNormalizedEn))
                {
                    stats.NoNormalizedTerm++;
                    continue;
                }

                var match = await MatchOneAsync(kr.ParameterNormalizedEn!, ct);
                if (match == null)
                {
                    stats.NoMatch++;
                    continue;
                }

                if (match.Score < _settings.MinScore)
                {
                    _logger.LogInformation(
                        "LoincMatcher: parameter \"{Param}\" -> code {Code} score {Score:F2} BELOW threshold {Min:F2}. Discarding.",
                        kr.Parameter, match.Loinc, match.Score, _settings.MinScore);
                    stats.BelowThreshold++;
                    continue;
                }

                kr.LoincCode = match.Loinc;
                kr.LoincLongName = match.Name;
                kr.LoincConfidence = match.Score switch
                {
                    >= 0.85 => "high",
                    >= 0.65 => "medium",
                    _ => "low"
                };
                stats.Matched++;

                _logger.LogInformation(
                    "LoincMatcher: \"{Param}\" [normalized_en=\"{NormEn}\"] -> {Code} \"{Name}\" (score {Score:F2}, confidence {Conf}).",
                    kr.Parameter, kr.ParameterNormalizedEn, match.Loinc, match.Name, match.Score, kr.LoincConfidence);
            }

            _logger.LogInformation(
                "LoincMatcher summary: total={Total} matched={Matched} below_threshold={Low} no_match={None} no_normalized_term={Skip}.",
                stats.Total, stats.Matched, stats.BelowThreshold, stats.NoMatch, stats.NoNormalizedTerm);

            return stats;
        }

        private async Task<MatcherResponse?> MatchOneAsync(string normalizedText, CancellationToken ct)
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(_settings.TimeoutSeconds));

                var resp = await _http.PostAsJsonAsync("/loinc/match",
                    new MatcherRequest { TestName = normalizedText }, cts.Token);

                if (!resp.IsSuccessStatusCode)
                {
                    _logger.LogWarning(
                        "LoincMatcher: HTTP {Status} for \"{Text}\".",
                        (int)resp.StatusCode, normalizedText);
                    return null;
                }

                return await resp.Content.ReadFromJsonAsync<MatcherResponse>(cancellationToken: cts.Token);
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested)
            {
                _logger.LogWarning("LoincMatcher: timeout for \"{Text}\" after {Secs}s.",
                    normalizedText, _settings.TimeoutSeconds);
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "LoincMatcher: unexpected error for \"{Text}\".", normalizedText);
                return null;
            }
        }

        private class MatcherRequest
        {
            [JsonPropertyName("test_name")] public string TestName { get; set; } = string.Empty;
        }

        private class MatcherResponse
        {
            [JsonPropertyName("loinc")]     public string Loinc { get; set; } = string.Empty;
            [JsonPropertyName("name")]      public string Name { get; set; } = string.Empty;
            [JsonPropertyName("score")]     public double Score { get; set; }
            [JsonPropertyName("component")] public string? Component { get; set; }
            [JsonPropertyName("system")]    public string? System { get; set; }
            [JsonPropertyName("method")]    public string? Method { get; set; }
        }

        public class MatcherStats
        {
            public int Total { get; set; }
            public int Matched { get; set; }
            public int BelowThreshold { get; set; }
            public int NoMatch { get; set; }
            public int NoNormalizedTerm { get; set; }
        }
    }

    /// <summary>Strongly-typed settings for the LOINC matcher microservice client.</summary>
    public class LoincMatcherSettings
    {
        public string BaseUrl { get; set; } = "http://localhost:8000";
        public bool Enabled { get; set; } = true;
        public int TimeoutSeconds { get; set; } = 5;
        public double MinScore { get; set; } = 0.55;
    }
}
