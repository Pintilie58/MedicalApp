using System.ComponentModel.DataAnnotations;

namespace MedicalApp.Models
{
    public class InterpretationHistory
    {
        [Key]
        public int Id { get; set; }

        [Required]
        [StringLength(200)]
        public string UserEmail { get; set; } = string.Empty;

        [StringLength(300)]
        public string? OriginalFileName { get; set; }

        [StringLength(10)]
        public string? Language { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        /// <summary>success | rejected | error</summary>
        [StringLength(30)]
        public string Status { get; set; } = "success";

        [StringLength(500)]
        public string? ErrorMessage { get; set; }

        public int CreditsConsumed { get; set; } = 1;

        public int? InputTokens { get; set; }
        public int? OutputTokens { get; set; }

        /// <summary>FK to Profiles.Id - which health profile this interpretation is for.</summary>
        public int? ProfileId { get; set; }

        /// <summary>
        /// Raw JSON output returned by the AI provider (Gemini) for this interpretation.
        /// Persisted so the PDF report can be regenerated on-demand later, without
        /// re-uploading or re-calling the AI. Stored as NVARCHAR(MAX).
        /// Populated only for Status == "success" (and for "rejected" when the provider
        /// produced a valid JSON object with is_medical_analysis=false).
        /// </summary>
        public string? RawJsonResult { get; set; }
    }
}
