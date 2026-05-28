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
        }
    }
}
