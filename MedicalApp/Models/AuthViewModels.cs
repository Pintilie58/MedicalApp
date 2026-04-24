using System.ComponentModel.DataAnnotations;

namespace MedicalApp.Models
{
    public class RegisterViewModel
    {
        [LocalizedRequired("EmailRequired")]
        [LocalizedEmailAddress("EmailInvalid")]
        [Display(Name = "Email")]
        public string Email { get; set; } = string.Empty;

        [LocalizedRequired("PasswordRequired")]
        [DataType(DataType.Password)]
        [LocalizedStringLength(100, "PasswordMinLength", MinimumLength = 6)]
        [Display(Name = "Password")]
        public string Parola { get; set; } = string.Empty;

        [LocalizedRequired("ConfirmPasswordRequired")]
        [DataType(DataType.Password)]
        [LocalizedCompare("Parola", "PasswordMismatch")]
        [Display(Name = "Confirm Password")]
        public string ConfirmParola { get; set; } = string.Empty;

        /// <summary>Optional promo code entered at registration (e.g. "Med3").</summary>
        [StringLength(50)]
        [Display(Name = "Promo Code")]
        public string? PromoCode { get; set; }
    }

    public class LoginViewModel
    {
        [LocalizedRequired("EmailRequired")]
        [LocalizedEmailAddress("EmailInvalid")]
        [Display(Name = "Email")]
        public string Email { get; set; } = string.Empty;

        [LocalizedRequired("PasswordRequired")]
        [DataType(DataType.Password)]
        [Display(Name = "Password")]
        public string Parola { get; set; } = string.Empty;
    }

    public class ForgotPasswordViewModel
    {
        [LocalizedRequired("EmailRequired")]
        [LocalizedEmailAddress("EmailInvalid")]
        [Display(Name = "Email")]
        public string Email { get; set; } = string.Empty;
    }

    public class ResetPasswordViewModel
    {
        [Required]
        public string Token { get; set; } = string.Empty;

        [LocalizedRequired("EmailRequired")]
        [LocalizedEmailAddress("EmailInvalid")]
        public string Email { get; set; } = string.Empty;

        [LocalizedRequired("PasswordRequired")]
        [DataType(DataType.Password)]
        [LocalizedStringLength(100, "PasswordMinLength", MinimumLength = 6)]
        [Display(Name = "New Password")]
        public string Parola { get; set; } = string.Empty;

        [LocalizedRequired("ConfirmPasswordRequired")]
        [DataType(DataType.Password)]
        [LocalizedCompare("Parola", "PasswordMismatch")]
        [Display(Name = "Confirm Password")]
        public string ConfirmParola { get; set; } = string.Empty;
    }

    public class VerifyEmailViewModel
    {
        [Required]
        [EmailAddress]
        public string Email { get; set; } = string.Empty;

        [LocalizedRequired("VerificationCodeRequired")]
        [RegularExpression(@"^\d{4}$", ErrorMessage = "Code must be 4 digits")]
        [Display(Name = "Verification Code")]
        public string Code { get; set; } = string.Empty;
    }
}
