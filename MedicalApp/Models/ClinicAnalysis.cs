using System.ComponentModel.DataAnnotations;

namespace MedicalApp.Models
{
    /// <summary>
    /// One row per successfully interpreted PDF for a CAM clinic patient.
    /// Holds the full Gemini RawJsonResult so the system can later run a
    /// Compare across the LAST 4 analyses of the same patient (the "debug2"
    /// in the original CAM business spec).
    ///
    /// Older rows beyond the last 4 per patient are auto-pruned by the
    /// batch runner — see <see cref="MedicalApp.Services.CamFileStore"/> /
    /// (future) batch processing service.
    /// </summary>
    public class ClinicAnalysis
    {
        [Key]
        public int Id { get; set; }

        public int ClinicId { get; set; }

        public int PatientId { get; set; }

        [Required]
        [StringLength(255)]
        public string OriginalFileName { get; set; } = string.Empty;

        /// <summary>Full Gemini JSON result (KeyResults + LOINC codes + abnormalities).</summary>
        public string? RawJsonResult { get; set; }

        /// <summary>Sampling date parsed from the PDF (PatientInfo.DateTaken).
        /// Falls back to <see cref="ProcessedAt"/> when missing/unparsable.</summary>
        public DateTime? SamplingDate { get; set; }

        public DateTime ProcessedAt { get; set; } = DateTime.UtcNow;
    }
}
