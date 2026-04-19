using System.ComponentModel.DataAnnotations;

namespace MedicalApp.Models
{
    public class User
    {
        [Key]
        [Required]
        [EmailAddress]
        [StringLength(200)]
        public string Email { get; set; } = string.Empty;

        [Required]
        [StringLength(255)]
        public string Parola { get; set; } = string.Empty; // hashed cu BCrypt

        public int Credite { get; set; } = 0;

        public DateTime DataC { get; set; } = DateTime.UtcNow;

        public int CreditConsum { get; set; } = 0;

        public int CreditRest { get; set; } = 0;

        // Password reset fields (nullable)
        [StringLength(128)]
        public string? PasswordResetToken { get; set; }

        public DateTime? PasswordResetTokenExpiry { get; set; }
    }
}
