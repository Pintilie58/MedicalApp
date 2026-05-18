using MedicalApp.Models;
using System.ComponentModel.DataAnnotations;

namespace MedicalApp.Models
{
    /// <summary>ViewModel for /Profiles list page.</summary>
    public class ProfilesIndexViewModel
    {
        public List<ProfileRow> Profiles { get; set; } = new();

        public class ProfileRow
        {
            public int Id { get; set; }
            public string Name { get; set; } = string.Empty;
            public string? Relationship { get; set; }
            public string? Gender { get; set; }
            public int? BirthYear { get; set; }
            public string? Notes { get; set; }
            public bool IsDefault { get; set; }
            public DateTime CreatedAt { get; set; }
            public int InterpretationsCount { get; set; }
        }
    }

    /// <summary>Form for Create/Edit profile.</summary>
    public class ProfileFormViewModel
    {
        public int? Id { get; set; }

        [Required(ErrorMessage = "Name is required")]
        [StringLength(100, MinimumLength = 1)]
        public string Name { get; set; } = string.Empty;

        [StringLength(50)]
        public string? Relationship { get; set; }

        [StringLength(10)]
        public string? Gender { get; set; }

        [Range(1900, 2100, ErrorMessage = "Year must be between 1900 and 2100")]
        public int? BirthYear { get; set; }

        [StringLength(500)]
        public string? Notes { get; set; }

        /// <summary>"low_moderate" | "high" | "very_high" | null. Used by AI prompt
        /// to pick the right target for lipid panel (LDL, non-HDL).</summary>
        [StringLength(20)]
        public string? CardiovascularRisk { get; set; }

        public bool IsDefault { get; set; }
    }

    /// <summary>ViewModel for /Profiles/History/{id} - the archive of interpretations
    /// done for one specific profile. Lists only status=success items, newest first.</summary>
    public class ProfileHistoryViewModel
    {
        public int ProfileId { get; set; }
        public string ProfileName { get; set; } = string.Empty;
        public string? Relationship { get; set; }
        public List<HistoryRow> Items { get; set; } = new();

        /// <summary>True when the user is still inside the 1-year free premium period.</summary>
        public bool IsInFreePeriod { get; set; }
        /// <summary>When the free period for premium archive features expires.</summary>
        public DateTime FreeUntil { get; set; }
        /// <summary>Remaining free uses in the current 3-use bundle (only meaningful when IsInFreePeriod == false).</summary>
        public int FreeUsesLeftInBundle { get; set; }

        public class HistoryRow
        {
            public int Id { get; set; }
            public DateTime CreatedAt { get; set; }
            public string? OriginalFileName { get; set; }
            public string? Language { get; set; }
            public int? AbnormalFindingsCount { get; set; }
            public int? KeyResultsCount { get; set; }
            public string? PatientName { get; set; }
            public string? DateTaken { get; set; }
            public bool HasRawJson { get; set; }
        }
    }

    /// <summary>ViewModel for /Profiles/Compare - side-by-side comparison of 2 to 4
    /// interpretations for the same profile. Parameters are joined on a canonical
    /// name key (case-insensitive, whitespace-trimmed) so the same parameter shows
    /// on the same row even if the labs used slightly different label spacing.
    /// Columns are ordered LEFT (oldest sampling date) → RIGHT (most recent),
    /// based on the patient's sampling date (PatientInfo.DateTaken from the JSON);
    /// when the sampling date can't be parsed we fall back to CreatedAt.
    /// </summary>
    public class CompareInterpretationsViewModel
    {
        public const int MinSelections = 2;
        public const int MaxSelections = 4;

        public int ProfileId { get; set; }
        public string ProfileName { get; set; } = string.Empty;

        /// <summary>Ordered oldest → newest by sampling date.</summary>
        public List<Column> Columns { get; set; } = new();

        public List<ComparisonRow> Rows { get; set; } = new();

        public int RisenCount { get; set; }
        public int FallenCount { get; set; }
        public int UnchangedCount { get; set; }
        public int PartialCount { get; set; }

        public bool CreditConsumed { get; set; }

        public class Column
        {
            public int HistoryId { get; set; }
            public DateTime CreatedAt { get; set; }
            public string? OriginalFileName { get; set; }
            public string? DateTaken { get; set; }
            /// <summary>Date used for ordering (parsed from DateTaken, fallback CreatedAt).</summary>
            public DateTime EffectiveDate { get; set; }
            public int KeyResultsCount { get; set; }
            public int AbnormalFindingsCount { get; set; }
        }

        public class ComparisonRow
        {
            public string Parameter { get; set; } = string.Empty;
            public string? Unit { get; set; }
            public string? ReferenceRange { get; set; }

            // --- LOINC grouping metadata (Pas 4) ---
            // When two interpretations from different labs / different languages
            // report the same analyte under slightly different names
            // (e.g. "VSH" vs "Vitesse de sédimentation" vs "ESR"), grouping
            // them by their official LoincCode finally lets the Compare view
            // line them up on the SAME row. These two fields surface that
            // standardized identity to the view.
            //
            // LoincCode is null for legacy rows where Gemini did not emit a
            // code AND the validator could not recover one — in that case the
            // row is still keyed by the (normalized) parameter name, like
            // before, so old interpretations remain comparable.

            /// <summary>
            /// Canonical LOINC code shared by all cells on this row.
            /// Null when this row was grouped by parameter-name fallback
            /// (legacy interpretations without LOINC, or LOINC was nulled
            /// out by the validator).
            /// </summary>
            public string? LoincCode { get; set; }

            /// <summary>
            /// LOINC Long Common Name for display in tooltips, e.g.
            /// "Gamma glutamyl transferase [Enzymatic activity/volume] in
            ///  Serum or Plasma". Null when <see cref="LoincCode"/> is null.
            /// </summary>
            public string? LoincLongName { get; set; }

            /// <summary>One cell per Column (same length and order as <see cref="Columns"/>).</summary>
            public List<Cell> Cells { get; set; } = new();

            /// <summary>
            /// Aggregate trend across the columns:
            ///   risen     - last present value &gt; first present value
            ///   fallen    - last present value &lt; first present value
            ///   unchanged - all present values numerically equal
            ///   partial   - parameter only appears in some columns
            ///   unparsable- present in all but values are not numeric and differ
            /// </summary>
            public string Direction { get; set; } = "unchanged";
        }

        public class Cell
        {
            public string? Value { get; set; }
            public string? Status { get; set; }
            /// <summary>
            /// Per-cell direction relative to the FIRST cell that contains a value:
            ///   first | risen | fallen | unchanged | only_here | absent
            /// Used by the view to color the cell.
            /// </summary>
            public string CellDirection { get; set; } = "absent";
        }
    }
}
