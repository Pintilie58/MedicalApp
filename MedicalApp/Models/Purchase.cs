using System.ComponentModel.DataAnnotations;

namespace MedicalApp.Models
{
    /// <summary>
    /// One row per successful credit purchase (simulated or real).
    /// Used by the Admin dashboard for revenue reporting.
    /// </summary>
    public class Purchase
    {
        [Key]
        public int Id { get; set; }

        [Required]
        [EmailAddress]
        [StringLength(200)]
        public string UserEmail { get; set; } = string.Empty;

        public DateTime PurchasedAt { get; set; } = DateTime.UtcNow;

        /// <summary>Amount paid in EUR.</summary>
        public decimal AmountEur { get; set; }

        public int CreditsAdded { get; set; }

        /// <summary>"simulated" for now, "stripe" / "netopia" / "paypal" later.</summary>
        [StringLength(50)]
        public string PaymentMethod { get; set; } = "simulated";

        /// <summary>Package key (e.g. "small", "medium", "large").</summary>
        [StringLength(50)]
        public string? PackageKey { get; set; }

        /// <summary>Optional promo code used with the purchase.</summary>
        [StringLength(50)]
        public string? PromoCode { get; set; }
    }
}
