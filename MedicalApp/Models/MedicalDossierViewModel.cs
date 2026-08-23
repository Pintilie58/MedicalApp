namespace MedicalApp.Models
{
    /// <summary>
    /// B2C "Dosar medical" — every out-of-range result the profile has ever had,
    /// collected from ALL interpretations in its archive, grouped by medical
    /// specialty (LOINC CLASS) then by analyte (LOINC code, or normalized name
    /// when the analyte has no code), each analyte listing its own timeline.
    ///
    /// This is the sheet the patient prints and takes to the doctor: only what is
    /// abnormal, with the direction of change over time.
    /// </summary>
    public class MedicalDossierViewModel
    {
        public int ProfileId { get; set; }
        public string ProfileName { get; set; } = string.Empty;
        public string? Gender { get; set; }
        public int? BirthYear { get; set; }
        public int? Age { get; set; }

        /// <summary>Free-text medical history, taken from Profile.Notes.</summary>
        public string? MedicalHistory { get; set; }

        /// <summary>How many archived interpretations were scanned.</summary>
        public int SourceReportsCount { get; set; }
        /// <summary>Distinct abnormal analytes found (i.e. number of analyte groups).</summary>
        public int AbnormalAnalyteCount { get; set; }
        /// <summary>Total abnormal result rows across all analytes.</summary>
        public int AbnormalEntryCount { get; set; }

        public DateTime? FirstDate { get; set; }
        public DateTime? LastDate { get; set; }

        /// <summary>True when an archive credit was charged for opening this view.</summary>
        public bool CreditConsumed { get; set; }

        public List<ClassGroup> Groups { get; set; } = new();

        public class ClassGroup
        {
            /// <summary>Localized specialty name: Hematologie, Biochimie serică, ...</summary>
            public string Label { get; set; } = string.Empty;
            public int Priority { get; set; }
            public List<AnalyteGroup> Analytes { get; set; } = new();
        }

        public class AnalyteGroup
        {
            /// <summary>Display name as written on the newest lab report.</summary>
            public string Parameter { get; set; } = string.Empty;
            public string? LoincCode { get; set; }
            public string? LoincLongName { get; set; }
            public string? LoincSource { get; set; }
            /// <summary>Oldest → newest.</summary>
            public List<Entry> Entries { get; set; } = new();
        }

        public class Entry
        {
            public int HistoryId { get; set; }
            /// <summary>Sampling date from the lab report; falls back to the interpretation date.</summary>
            public DateTime Date { get; set; }
            /// <summary>False when <see cref="Date"/> is the interpretation date (no sampling date on the report).</summary>
            public bool DateIsSampling { get; set; }
            public DateTime InterpretedAt { get; set; }
            public string? Laboratory { get; set; }
            public string? Value { get; set; }
            public string? Unit { get; set; }
            public string? ReferenceRange { get; set; }
            /// <summary>high | low | borderline | positive</summary>
            public string Status { get; set; } = string.Empty;
            /// <summary>up | down | same | "" (unknown / first entry) — versus the PREVIOUS entry.</summary>
            public string Trend { get; set; } = string.Empty;
        }
    }
}
