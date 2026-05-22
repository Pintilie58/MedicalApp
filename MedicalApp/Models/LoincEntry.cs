using System.ComponentModel.DataAnnotations;

namespace MedicalApp.Models
{
    /// <summary>
    /// One entry from the LOINC standard - "Logical Observation Identifiers Names
    /// and Codes" (Regenstrief Institute / Apache-style license). Each row is a
    /// stable, globally-recognized code for a specific lab measurement (e.g.
    /// <c>2324-2</c> = Gamma glutamyl transferase in serum/plasma).
    /// <para>
    /// Populated at app startup by <c>LoincSeeder</c> from the official
    /// "Universal Lab Orders Value Set" CSV shipped with the LOINC release.
    /// Once populated this table serves as a VALIDATION DICTIONARY: Gemini
    /// emits a <c>loinc_code</c> for every extracted parameter, and the
    /// controller cross-checks that the code actually exists in this table.
    /// Unknown codes are flagged and stored as <c>NULL</c>.
    /// </para>
    /// </summary>
    public class LoincEntry
    {
        /// <summary>
        /// The LOINC code (primary key). Stable identifier, e.g. "2324-2".
        /// Format: integer-checkdigit. We store it as a string so we never
        /// drop the leading zero on terms like "1742-6".
        /// </summary>
        [Key]
        [StringLength(20)]
        public string LoincCode { get; set; } = string.Empty;

        /// <summary>
        /// The official human-readable English long name from LOINC, e.g.
        /// "Gamma glutamyl transferase [Enzymatic activity/volume] in Serum or Plasma".
        /// Used for sanity-checking Gemini's mapping (we compare the long
        /// name Gemini returns against this canonical one).
        /// </summary>
        [Required]
        [StringLength(500)]
        public string LongCommonName { get; set; } = string.Empty;

        /// <summary>
        /// Whether the term is usable as an Order, an Observation, or Both.
        /// In the Universal Lab Orders Value Set this is always "Order" or "Both".
        /// Stored as-is for completeness; not used by the matching logic yet.
        /// </summary>
        [StringLength(20)]
        public string? OrderObs { get; set; }

        /// <summary>
        /// JSON array of alternative spellings / abbreviations / national
        /// names for this test, in any language. Populated later (Pasul 2+)
        /// from <c>Loinc.csv</c> <c>RELATEDNAMES2</c> column and from Gemini
        /// batch translation. Empty/null on the initial seed.
        /// </summary>
        public string? AliasesJson { get; set; }

        /// <summary>
        /// JSON object with translations of <see cref="LongCommonName"/> per
        /// locale, e.g. <c>{"ro": "Gamma-glutamil transferaza", "fr": "..."}</c>.
        /// Populated later from LOINC LinguisticVariants CSVs and from a
        /// Gemini batch call for languages LOINC doesn't ship officially
        /// (notably Romanian). Empty/null on the initial seed.
        /// </summary>
        public string? TranslationsJson { get; set; }

        /// <summary>UTC timestamp of the last seed/refresh.</summary>
        public DateTime ImportedAt { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// LOINC CLASS code from the official LOINC release (e.g. <c>HEM</c>
        /// = Hematology, <c>CHEM</c> = Chemistry, <c>SERO</c> = Serology,
        /// <c>ENDO</c> = Endocrinology, <c>COAG</c> = Coagulation,
        /// <c>UA</c> = Urinalysis, etc.). This is the OFFICIAL specialty
        /// classification — it is NOT encoded in the numeric LOINC code
        /// itself; it lives as a separate column in <c>Loinc.csv</c>.
        /// <para>
        /// Used by the Compare view to group parameters by medical specialty
        /// (Hematologie, Biochimie serică, Imunologie, etc.) instead of
        /// listing them in arbitrary order. Null when the seeded CSV did
        /// not include a CLASS column (older Universal Lab Orders subset)
        /// or when the row's CLASS value was blank.
        /// </para>
        /// </summary>
        [StringLength(20)]
        public string? Class { get; set; }
    }
}
