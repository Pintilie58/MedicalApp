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

        // ----- Admin dashboard / tracking fields (added Feb 2026) -----

        /// <summary>Total amount spent by the user in EUR (sum of successful purchases).</summary>
        public decimal TotalPaid { get; set; } = 0m;

        /// <summary>Timestamp of the user's last successful login.</summary>
        public DateTime? LastLoginAt { get; set; }

        /// <summary>When true the user cannot log in (admin can block abusive accounts).</summary>
        public bool IsBlocked { get; set; } = false;

        /// <summary>When true the user has admin dashboard access.</summary>
        public bool IsAdmin { get; set; } = false;
    }
}
