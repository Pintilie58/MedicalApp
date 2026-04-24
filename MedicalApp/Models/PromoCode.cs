using System.ComponentModel.DataAnnotations;

namespace MedicalApp.Models
{
    /// <summary>
    /// Promo code that grants free credits on registration (or later, on purchases).
    /// Example: code "Med3" → +3 free credits for any new user, valid 1 month.
    /// </summary>
    public class PromoCode
    {
        [Key]
        public int Id { get; set; }

        [Required]
        [StringLength(50)]
        public string Code { get; set; } = string.Empty;

        public int CreditsToAdd { get; set; } = 0;

        public DateTime ValidFrom { get; set; } = DateTime.UtcNow;

        public DateTime ValidUntil { get; set; } = DateTime.UtcNow.AddMonths(1);

        /// <summary>How many times the code has been redeemed so far.</summary>
        public int TimesUsed { get; set; } = 0;

        /// <summary>0 = unlimited uses.</summary>
        public int MaxUses { get; set; } = 0;

        public bool IsActive { get; set; } = true;

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        // ----- helpers -----
        public bool IsCurrentlyValid() =>
            IsActive
            && DateTime.UtcNow >= ValidFrom
            && DateTime.UtcNow <= ValidUntil
            && (MaxUses == 0 || TimesUsed < MaxUses);
    }
}
