using System.ComponentModel.DataAnnotations;

namespace MedicalApp.Models
{
    /// <summary>
    /// Admin-controlled promotion per module ("Individual" = B2C, "Clinic" = B2B,
    /// "Cabinet" = CM). One row per module. Valid until the admin changes it.
    /// </summary>
    public class PromotionSetting
    {
        [Key]
        public int Id { get; set; }

        /// <summary>Audience key as in <see cref="Services.CreditPackage.Audience"/>.</summary>
        [Required, MaxLength(20)]
        public string Module { get; set; } = string.Empty;

        /// <summary>0 = no discount, otherwise 20 or 50.</summary>
        public int DiscountPercent { get; set; }

        /// <summary>When true nobody in this module can buy credits (kill switch).</summary>
        public bool PurchasesSuspended { get; set; }

        public DateTime UpdatedAt { get; set; }

        [MaxLength(200)]
        public string? UpdatedBy { get; set; }
    }
}
