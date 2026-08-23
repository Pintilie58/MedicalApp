namespace MedicalApp.Models
{
    /// <summary>
    /// On-screen freemium report ("Raportul tău"), the in-app twin of the DEMO PDF.
    /// SECURITY: every locked item is redacted SERVER-SIDE — the hidden text is
    /// never placed in the ViewModel, so it cannot be recovered from the page
    /// source, DevTools or a saved copy of the HTML. A CSS blur would leak it.
    /// The redaction pattern is the SAME one the PDF uses
    /// (<see cref="Services.PdfReportGenerator.IsRedactedAt"/>) so the two views
    /// of the report can never drift apart.
    /// </summary>
    public class ReportScreenViewModel
    {
        public int HistoryId { get; set; }
        public int? ProfileId { get; set; }
        public string? ProfileName { get; set; }
        public DateTime CreatedAt { get; set; }

        public PatientInfo? PatientInfo { get; set; }

        /// <summary>False for users who bought credits: nothing is redacted and the
        /// upgrade CTAs are hidden — they already paid, there is nothing to sell.</summary>
        public bool IsFreemium { get; set; } = true;

        /// <summary>Never redacted — this is the teaser that sells the upgrade.</summary>
        public string? Summary { get; set; }
        public string? Disclaimer { get; set; }

        public List<LockableText> RiskFactors { get; set; } = new();
        public List<LockableRow> KeyResults { get; set; } = new();
        public List<LockableFinding> AbnormalFindings { get; set; } = new();
        public List<LockableText> Correlations { get; set; } = new();
        public List<LockableText> Recommendations { get; set; } = new();
        public List<LockableText> DoctorQuestions { get; set; } = new();

        /// <summary>Total number of hidden items — used in the CTA copy.</summary>
        public int LockedCount { get; set; }

        /// <summary>Visible / total lab results — drives the "you see X of Y" progress card.</summary>
        public int VisibleResultsCount { get; set; }
        public int TotalResultsCount { get; set; }

        /// <summary>True when at least one visible row carries a LOINC code, so the
        /// legend explaining the colored dots is worth rendering.</summary>
        public bool HasLoincCodes { get; set; }

        public class LockableText
        {
            /// <summary>Null when <see cref="Locked"/> is true.</summary>
            public string? Text { get; set; }
            public bool Locked { get; set; }
        }

        public class LockableRow
        {
            public bool Locked { get; set; }
            /// <summary>Panel/section header from the lab PDF — metadata, never locked.</summary>
            public string? PanelHeader { get; set; }
            public string? Parameter { get; set; }
            public string? Value { get; set; }
            public string? Unit { get; set; }
            public string? ReferenceRange { get; set; }
            public string? Status { get; set; }
            public string? Explanation { get; set; }

            /// <summary>Verbatim per-row lab metadata: specimen, method, analyzer
            /// (e.g. "-Ser - Turbidimetrie (ABX PENTRA C400 ISE)").</summary>
            public string? AnalyteLine { get; set; }

            public string? LoincCode { get; set; }
            public string? LoincLongName { get; set; }
            public string? LoincSource { get; set; }
            public double? LoincScore { get; set; }
        }

        public class LockableFinding
        {
            public bool Locked { get; set; }
            public string? Parameter { get; set; }
            public string? Explanation { get; set; }
            public string? Severity { get; set; }
        }
    }
}
