using System.ComponentModel.DataAnnotations;

namespace MedicalApp.Models
{
    /// <summary>
    /// One row per file that could NOT be sent in a given batch run. Drives
    /// the "NotSends" section of the <c>Sum_data.txt</c> summary and the
    /// errors detail view on the CAM dashboard.
    /// </summary>
    public class ClinicBatchError
    {
        [Key]
        public int Id { get; set; }

        public int BatchRunId { get; set; }

        [Required]
        [StringLength(255)]
        public string FileName { get; set; } = string.Empty;

        [StringLength(200)]
        public string? PatientName { get; set; }

        /// <summary>
        /// Human-readable reason: "CNP missing", "Email invalid", "AI timeout",
        /// "Gemini quota exceeded", etc.
        /// </summary>
        [Required]
        [StringLength(500)]
        public string Reason { get; set; } = string.Empty;

        public DateTime OccurredAt { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// How many retry attempts have been made on this file (across batch
        /// runs). When this reaches 3 the file is moved to the <c>Errors/</c>
        /// subfolder so it stops being picked up by future batches.
        /// </summary>
        public int RetryCount { get; set; } = 1;
    }
}
