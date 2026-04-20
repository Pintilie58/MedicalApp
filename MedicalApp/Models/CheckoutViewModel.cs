using System.ComponentModel.DataAnnotations;

namespace MedicalApp.Models
{
    public class CheckoutViewModel
    {
        [Required]
        public string PackageKey { get; set; } = string.Empty;

        [LocalizedRequired("CardNumberRequired")]
        [RegularExpression(@"^[0-9\s]{13,19}$", ErrorMessage = "Invalid card number")]
        [Display(Name = "Card number")]
        public string CardNumber { get; set; } = string.Empty;

        [LocalizedRequired("CardHolderRequired")]
        [StringLength(100)]
        [Display(Name = "Card holder")]
        public string CardHolder { get; set; } = string.Empty;

        [LocalizedRequired("ExpiryRequired")]
        [RegularExpression(@"^(0[1-9]|1[0-2])\/?([0-9]{2})$", ErrorMessage = "Format MM/YY")]
        [Display(Name = "Expiry MM/YY")]
        public string Expiry { get; set; } = string.Empty;

        [LocalizedRequired("CvvRequired")]
        [RegularExpression(@"^[0-9]{3,4}$", ErrorMessage = "3 or 4 digits")]
        [Display(Name = "CVV")]
        public string Cvv { get; set; } = string.Empty;
    }
}
