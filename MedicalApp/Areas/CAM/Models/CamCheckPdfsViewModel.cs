namespace MedicalApp.Areas.CAM.Models
{
    /// <summary>
    /// View model pentru /CAM/CheckPdfs — preview live al extragerii nume+email.
    /// </summary>
    public class CamCheckPdfsViewModel
    {
        public string ClinicName { get; set; } = string.Empty;
        public string OriginalFolder { get; set; } = string.Empty;
        public bool FolderMissing { get; set; }
        public List<Row> Items { get; set; } = new();

        public class Row
        {
            public string FileName { get; set; } = string.Empty;
            public int SizeKb { get; set; }
            public string? PatientName { get; set; }
            public string? PatientEmail { get; set; }
            public bool IsValid { get; set; }
            public string? Reason { get; set; }
        }
    }
}
