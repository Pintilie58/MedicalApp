namespace MedicalApp.Areas.CAM.Models
{
    /// <summary>
    /// View model pentru landing page-ul CAM (/CAM/Dashboard).
    /// Faza 1: doar identificare clinică + status foldere + credite.
    /// </summary>
    public class CamDashboardViewModel
    {
        public string ClinicName { get; set; } = string.Empty;
        public string ClinicCity { get; set; } = string.Empty;
        public string ClinicAddress { get; set; } = string.Empty;

        public int CreditsAvailable { get; set; }

        public bool FoldersCreated { get; set; }
        public DateTime? FoldersCreatedAt { get; set; }

        public string ClinicFolderRoot { get; set; } = string.Empty;
        public string OriginalFolder { get; set; } = string.Empty;
        public string SendsFolder { get; set; } = string.Empty;
        public string SumarFolder { get; set; } = string.Empty;
        public string ErrorsFolder { get; set; } = string.Empty;
    }
}
