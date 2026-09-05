using System.ComponentModel.DataAnnotations;

namespace MedicalApp.Models
{
    /// <summary>
    /// One remembered LOINC mapping: "this analyte name, with this unit and
    /// this source context, resolves to this LOINC code".
    ///
    /// Why it exists: the Python matcher is deterministic but expensive
    /// (~15 ms of sentence-embedding plus a 142 MB similarity scan over the
    /// 97k-row dictionary, per analyte). Analyte names repeat massively across
    /// users and reports, so the same question was being recomputed thousands
    /// of times. The cache is GLOBAL (user decision, June 2026): a mapping
    /// validated once is reused for everybody.
    ///
    /// Safety: the key covers EVERY input that can change the matcher's
    /// decision, plus a pipeline version. Change the weights, the anchors or
    /// the dictionary and you bump <c>LoincMatcher:Cache:PipelineVersion</c>;
    /// the old rows stay in the table but are never read again.
    /// </summary>
    public class LoincMatchCacheEntry
    {
        /// <summary>SHA-256 (hex) of version + printed analyte name + unit + decisive context markers.</summary>
        [Key]
        [StringLength(64)]
        public string CacheKey { get; set; } = string.Empty;

        /// <summary>
        /// Exactly what was hashed, human-readable (fields separated by '|').
        /// Kept so a cache miss can be diagnosed with one SQL query.
        /// </summary>
        [StringLength(1000)]
        public string? KeyMaterial { get; set; }

        /// <summary>Kept for humans reading the table; never part of the lookup.</summary>
        [StringLength(500)]
        public string TestName { get; set; } = string.Empty;

        [StringLength(64)]
        public string? Unit { get; set; }

        [StringLength(20)]
        public string PipelineVersion { get; set; } = string.Empty;

        [StringLength(20)]
        public string LoincCode { get; set; } = string.Empty;

        [StringLength(500)]
        public string LongName { get; set; } = string.Empty;

        [StringLength(20)]
        public string? LoincClass { get; set; }

        [StringLength(20)]
        public string? LoincSource { get; set; }

        public double Score { get; set; }

        /// <summary>Serialized per-axis explanation, exactly as the matcher returned it.</summary>
        public string? AxisVerdictJson { get; set; }

        public DateTime CreatedAt { get; set; }

        public DateTime LastUsedAt { get; set; }

        public int HitCount { get; set; }
    }
}
