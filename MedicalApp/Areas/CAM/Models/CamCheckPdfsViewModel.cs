using MedicalApp.Services;

namespace MedicalApp.Areas.CAM.Models
{
    /// <summary>
    /// View model pentru /CAM/CheckPdfs — preview live al extragerii nume+email,
    /// cu suport pentru override manual + upload + blacklist de domenii.
    /// </summary>
    public class CamCheckPdfsViewModel
    {
        public string ClinicName { get; set; } = string.Empty;
        public string OriginalFolder { get; set; } = string.Empty;
        public bool FolderMissing { get; set; }
        public string EmailDomainBlacklist { get; set; } = string.Empty;
        public List<Row> Items { get; set; } = new();

        /// <summary>
        /// True when AT LEAST ONE row has a syntactically invalid email or its
        /// domain does not resolve via DNS. The Run-batch button is disabled while
        /// this is true so the clinic doesn't burn credits on a doomed send.
        /// </summary>
        public bool HasBlockingEmailIssues => Items.Any(r =>
            r.IsValid &&
            (r.EmailValidity == EmailValidity.InvalidSyntax || r.EmailValidity == EmailValidity.NoMxRecord));

        public class Row
        {
            public string FileName { get; set; } = string.Empty;
            public int SizeKb { get; set; }
            public string? PatientName { get; set; }
            public string? PatientEmail { get; set; }
            public bool IsValid { get; set; }
            public string? Reason { get; set; }

            /// <summary>True când Strategy 0 (blocul [MedicalApp]) a fost recunoscut — gold path.</summary>
            public bool MatchedExplicitBlock { get; set; }

            /// <summary>True când operatorul a salvat manual un override pentru acest fișier.</summary>
            public bool IsManualOverride { get; set; }

            // ---- Email deliverability hints (A1/A2) ----
            /// <summary>Result of EmailDeliverabilityChecker on PatientEmail. Drives the new badge column.</summary>
            public EmailValidity EmailValidity { get; set; } = EmailValidity.Empty;
            /// <summary>Human-friendly explanation for the hover tooltip.</summary>
            public string EmailValidityMessage { get; set; } = string.Empty;
            /// <summary>Optional typo correction ("Did you mean gmail.com?"), null when none.</summary>
            public string? EmailDomainSuggestion { get; set; }
        }
    }
}
