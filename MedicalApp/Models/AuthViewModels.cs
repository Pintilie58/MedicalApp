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
}
