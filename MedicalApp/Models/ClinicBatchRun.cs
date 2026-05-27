using System.ComponentModel.DataAnnotations;

namespace MedicalApp.Models
{
    /// <summary>
    /// One row per "Lansează Interpretare Lot" run started by a clinic operator.
    /// Used to render the live progress page (AJAX polls), to write the
    /// <c>Sumar/Sum_yyyyMMdd_HHmm.txt</c> file when the run finishes, and to
    /// build the historical batches list on the CAM dashboard.
    /// </summary>
    public class ClinicBatchRun
    {
        [Key]
        public int Id { get; set; }

        public int ClinicId { get; set; }

        public DateTime StartedAt { get; set; } = DateTime.UtcNow;

        /// <summary>NULL while the run is still in progress.</summary>
        public DateTime? FinishedAt { get; set; }

        /// <summary>
        /// "Running" while in progress, "Completed" on normal exit,
        /// "Cancelled" if the operator aborted, "Failed" on fatal error.
        /// </summary>
        [Required]
        [StringLength(20)]
        public string Status { get; set; } = "Running";

        /// <summary>Number of files Gemini interpreted successfully.</summary>
        public int FilesInterpreted { get; set; }

        /// <summary>Number of files for which the result email was actually sent to the patient.</summary>
        public int FilesSent { get; set; }

        /// <summary>Number of times a Compare PDF was attached (patient had ≥2 analyses in history).</summary>
        public int FilesCompared { get; set; }

        /// <summary>Number of files that could not be processed (CNP/email missing, AI failure, etc.).</summary>
        public int NotSends { get; set; }

        /// <summary>Total number of PDF files picked up from the Original folder at run start.</summary>
        public int TotalFiles { get; set; }
    }
}
