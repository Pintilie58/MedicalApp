namespace MedicalApp.Areas.CAM.Models
{
    /// <summary>
    /// View model pentru pagina /CAM/Batch/Start — preview înainte de a porni lotul.
    /// </summary>
    public class CamBatchStartViewModel
    {
        public string ClinicName { get; set; } = string.Empty;
        public int FileCount { get; set; }
        public int CreditsAvailable { get; set; }
        public List<FileRow> Files { get; set; } = new();
        public bool AlreadyRunning { get; set; }
        public int? RunningBatchId { get; set; }

        public class FileRow
        {
            public string FileName { get; set; } = string.Empty;
            public int SizeKb { get; set; }
        }
    }

    /// <summary>
    /// View model pentru pagina /CAM/Batch/Progress/{id}.
    /// Datele dinamice (procesate, log) sunt populate prin AJAX poll
    /// la endpoint-ul /CAM/Batch/Status/{id}.
    /// </summary>
    public class CamBatchProgressPageViewModel
    {
        public int BatchRunId { get; set; }
        public string ClinicName { get; set; } = string.Empty;
        public DateTime StartedAt { get; set; }
    }
}
