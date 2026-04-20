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
    }
}
