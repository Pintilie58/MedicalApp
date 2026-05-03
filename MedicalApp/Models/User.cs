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

        // ----- Bonus credits tracking (separate from paid credits) -----

        /// <summary>Total bonus credits the user has ever received (promo codes, gifts).</summary>
        public int BonusCredits { get; set; } = 0;

        /// <summary>Bonus credits consumed so far. Bonus credits are consumed FIRST.</summary>
        public int BonusCreditsConsumed { get; set; } = 0;

        // ----- Archive premium features (P1.5.5 compare, P1.8 charts, exports) -----
        // Viewing the archive list and downloading a PDF stay FREE forever
        // (user's right to their own paid medical data).
        // Premium archive features are free for 1 year after registration.
        // After that, the user pays 1 credit for every 3 premium feature uses
        // (cumulative counter: uses 1,2,3 are free; use 4 consumes 1 credit and
        // the counter resets to 1; uses 5,6 free; use 7 consumes 1 credit; etc.).

        /// <summary>Date after which premium archive features become billable.
        /// NULL = not initialized yet (treated as already expired).</summary>
        public DateTime? FreeArchiveUntil { get; set; }

        /// <summary>Cumulative counter of premium archive feature uses in the current
        /// 3-use cycle. Resets to 0 after a credit is consumed (or stays 0 during the
        /// free period).</summary>
        public int ArchivePremiumCounter { get; set; } = 0;

        // ----- Computed (NOT mapped to DB) -----

        /// <summary>Bonus credits still available.</summary>
        [System.ComponentModel.DataAnnotations.Schema.NotMapped]
        public int BonusCreditsRemaining => BonusCredits - BonusCreditsConsumed;

        /// <summary>Total credits the user can still spend (paid + bonus remaining).</summary>
        [System.ComponentModel.DataAnnotations.Schema.NotMapped]
        public int TotalAvailableCredits => CreditRest + BonusCreditsRemaining;
    }
}
