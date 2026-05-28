using System.ComponentModel.DataAnnotations;

namespace MedicalApp.Models
{
    /// <summary>
    /// Operator-confirmed metadata for a specific PDF in a clinic's
    /// <c>Original</c> folder. Created from <c>/CAM/CheckPdfs</c> when the
    /// operator clicks "Edit" on a row and types the correct patient name /
    /// email — typically because the auto-extractor got it wrong or the PDF
    /// has no <c>[MedicalApp]</c> block.
    ///
    /// Lookup key = (ClinicId, FileName). On the next batch run, the
    /// <see cref="MedicalApp.Services.CamBatchService"/> reads any override
    /// matching the file BEFORE invoking the auto-extractor, and prefers it
    /// 100% of the time. When the file finally moves out of Original (Sends
    /// or Errors), the override row is deleted by the batch service.
    /// </summary>
    public class ClinicPdfOverride
    {
        [Key]
        public int Id { get; set; }

        public int ClinicId { get; set; }

        [Required]
        [StringLength(255)]
        public string FileName { get; set; } = string.Empty;

        [Required]
        [StringLength(200)]
        public string OverrideName { get; set; } = string.Empty;

        [Required]
        [EmailAddress]
        [StringLength(200)]
        public string OverrideEmail { get; set; } = string.Empty;

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }
}
