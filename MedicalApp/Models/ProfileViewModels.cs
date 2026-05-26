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

            /// <summary>
            /// LOINC CLASS code (HEM, CHEM, SERO, ENDO, COAG, UA, ...) used
            /// for grouping the Compare table by medical specialty. Null on
            /// legacy rows interpreted before the CLASS column was seeded,
            /// or for LOINC codes without a CLASS value.
            /// </summary>
            public string? LoincClass { get; set; }

            /// <summary>
            /// Romanian display label for <see cref="LoincClass"/>, used as
            /// the group-header text in the Compare view (e.g. "Hematologie",
            /// "Biochimie serică"). Falls back to "Alte analize" for rows
            /// without a class.
            /// </summary>
            public string ClassDisplayLabel { get; set; } = "Alte analize";

            /// <summary>
            /// Provenance of the LOINC code: "anchor" (verified canonical
            /// mapping) or "semantic" (matcher best-guess). Drives the small
            /// green/yellow badge next to the LOINC code on each row.
            /// </summary>
            public string? LoincSource { get; set; }

            /// <summary>
            /// Raw matcher score (0..1). Shown as a tiny percentage next to
            /// the blue dot for semantic mappings — gives the user a quick
            /// confidence cue. Picked from the LATEST cell that has a
            /// LoincSource value (most-recently-reinterpreted wins).
            /// </summary>
            public double? LoincScore { get; set; }

            /// <summary>
            /// True iff this row is the FIRST in its class group. The view
            /// uses this flag to render a section header above the row.
            /// </summary>
            public bool IsFirstInClass { get; set; }

            /// <summary>
            /// True when the SAME (case-insensitive) parameter name has been
            /// mapped to DIFFERENT LOINC codes across this set of compared
            /// interpretations — a telltale sign of Gemini extracting the
            /// analyte under slightly different wording in different reports
            /// (e.g. "Glucose in blood" vs "Glucose in serum"), which the
            /// matcher then resolves to two distinct LOINC codes. The view
            /// shows a small ⚠ next to the row label so the user knows the
            /// split into two rows might be an artifact, not a real medical
            /// distinction.
            /// </summary>
            public bool HasLoincDrift { get; set; }

            /// <summary>
            /// The OTHER LOINC codes assigned to the same normalized
            /// parameter name (excluding this row's own LoincCode). Used by
            /// the tooltip on the drift warning. Empty when
            /// <see cref="HasLoincDrift"/> is false.
            /// </summary>
            public List<string> DriftLoincCodes { get; set; } = new();

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

    /// <summary>
    /// ViewModel for /Profiles/Evolution — multi-LOINC time series chart.
    /// User pastes 1..5 LOINC codes (taken from any prior Interpretation or
    /// Compare table), and we collect every measurement of those codes across
    /// ALL of the profile's successful interpretations. Output:
    ///   - one table per LOINC code (patient name, lab, sampling date, value,
    ///     status, unit, reference range);
    ///   - one combined Chart.js line chart with one series per code, plus
    ///     the reference-range high/low drawn as dashed horizontal lines.
    /// </summary>
    public class EvolutionViewModel
    {
        public const int MinSelections = 1;
        public const int MaxSelections = 5;

        public int ProfileId { get; set; }
        public string ProfileName { get; set; } = string.Empty;

        /// <summary>The codes the user requested (verbatim, after sanitization).</summary>
        public List<string> RequestedCodes { get; set; } = new();

        /// <summary>Codes the user requested but for which we found ZERO measurements.</summary>
        public List<string> CodesNotFound { get; set; } = new();

        public List<EvolutionSeries> Series { get; set; } = new();

        public class EvolutionSeries
        {
            public string LoincCode { get; set; } = string.Empty;
            /// <summary>LoincLongName from the most recent interpretation that carried it.</summary>
            public string? LoincLongName { get; set; }
            /// <summary>Romanian-displayed LOINC CLASS (e.g. "Hematologie").</summary>
            public string ClassDisplayLabel { get; set; } = "Alte analize";

            /// <summary>Parameter name as it appeared in the most recent report
            /// (may differ between labs, we pick the latest spelling).</summary>
            public string DisplayParameter { get; set; } = string.Empty;

            /// <summary>Unit string from the most recent measurement (best-effort).</summary>
            public string? Unit { get; set; }

            /// <summary>Reference range string from the most recent measurement.</summary>
            public string? ReferenceRange { get; set; }

            /// <summary>Parsed numeric range LOWER bound (for the dashed line on the chart). Null when unparsable.</summary>
            public double? RefLow { get; set; }

            /// <summary>Parsed numeric range UPPER bound. Null when unparsable.</summary>
            public double? RefHigh { get; set; }

            /// <summary>Color hint for the chart (hex). Assigned by the controller in order.</summary>
            public string ColorHex { get; set; } = "#0d6efd";

            /// <summary>
            /// Provenance of the LOINC code for this series ("anchor" or
            /// "semantic"). Picked from the most recent measurement that
            /// carried the field. Drives the green/yellow verification
            /// badge in the series header.
            /// </summary>
            public string? LoincSource { get; set; }

            /// <summary>
            /// Latest matcher score for this series (0..1). Rendered next
            /// to the blue dot when LoincSource == "semantic".
            /// </summary>
            public double? LoincScore { get; set; }

            public List<EvolutionPoint> Points { get; set; } = new();
        }

        public class EvolutionPoint
        {
            /// <summary>Sampling date (or interpretation date as fallback) — used as X axis.</summary>
            public DateTime EffectiveDate { get; set; }
            /// <summary>Formatted "yyyy-MM-dd" for chart labels.</summary>
            public string DateLabel { get; set; } = string.Empty;
            /// <summary>Raw value string as printed by the lab.</summary>
            public string? Value { get; set; }
            /// <summary>Parsed numeric value for the Y axis. Null when value is qualitative.</summary>
            public double? NumericValue { get; set; }
            /// <summary>"normal" | "high" | "low" | "borderline" | null.</summary>
            public string? Status { get; set; }
            /// <summary>Per-row metadata used in the table.</summary>
            public string? PatientName { get; set; }
            public string? Laboratory { get; set; }
            public string? Unit { get; set; }
            public string? ReferenceRange { get; set; }
        }
    }
}
