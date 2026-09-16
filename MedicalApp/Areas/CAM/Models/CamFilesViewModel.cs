using MedicalApp.Services;

namespace MedicalApp.Areas.CAM.Models
{
    /// <summary>/CAM/Files — the browser replacement for Windows Explorer over the 4 CAM buckets.</summary>
    public class CamFilesViewModel
    {
        public string ClinicName { get; set; } = string.Empty;
        public CamFolder Folder { get; set; } = CamFolder.Original;
        public string DisplayLocation { get; set; } = string.Empty;
        public Dictionary<CamFolder, int> Counts { get; set; } = new();
        public List<Row> Items { get; set; } = new();

        public bool CanUpload => Folder == CamFolder.Original;
        public bool CanDelete => Folder == CamFolder.Original || Folder == CamFolder.Errors;
        public bool CanRestore => Folder == CamFolder.Errors;

        public class Row
        {
            public string FileName { get; set; } = string.Empty;
            public long SizeBytes { get; set; }
            public DateTime LastModifiedUtc { get; set; }
            /// <summary>Errors tab only — latest failure reason (DB first, .reasons.txt as fallback).</summary>
            public string? Reason { get; set; }
        }
    }
}
