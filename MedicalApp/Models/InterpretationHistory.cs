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

        /// <summary>
        /// SHA-256 hash (hex, 64 chars) of the original uploaded PDF bytes.
        /// Used for duplicate detection: same (UserEmail, ProfileId, PdfSha256)
        /// means the exact same file was already interpreted for that profile.
        /// NULL for interpretations created before duplicate-detection was introduced.
        /// </summary>
        [StringLength(64)]
        public string? PdfSha256 { get; set; }

        /// <summary>
        /// Gemini model that actually produced the successful response
        /// (e.g. <c>gemini-2.5-flash</c> or <c>gemini-2.5-pro</c>). When the
        /// controller fell back from Flash to Pro because of repeated 503s,
        /// this column reflects Pro — that's how the admin dashboard
        /// distinguishes "regular" interpretations from "rescued by Pro".
        /// NULL on rows created before this column was introduced or when
        /// the interpretation failed before any model was tried.
        /// </summary>
        [StringLength(40)]
        public string? ModelUsed { get; set; }
    }
}
