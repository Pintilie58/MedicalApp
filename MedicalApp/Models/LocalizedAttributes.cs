using MedicalApp.Services;
using Microsoft.AspNetCore.Mvc.ModelBinding.Validation;
using System.ComponentModel.DataAnnotations;

namespace MedicalApp.Models
{
    /// <summary>
    /// Validation attributes that resolve their error messages at runtime
    /// using the Loc service, so messages respect the current UI culture.
    /// </summary>

    public class LocalizedRequiredAttribute : RequiredAttribute
    {
        private readonly string _resourceKey;
        public LocalizedRequiredAttribute(string resourceKey) { _resourceKey = resourceKey; }
        public override string FormatErrorMessage(string name) => Loc.T(_resourceKey);
    }

    /// <summary>
    /// EmailAddressAttribute is sealed in .NET, so we wrap it via composition.
    /// </summary>
    public class LocalizedEmailAddressAttribute : ValidationAttribute, IClientModelValidator
    {
        private static readonly EmailAddressAttribute _inner = new EmailAddressAttribute();
        private readonly string _resourceKey;

        public LocalizedEmailAddressAttribute(string resourceKey) { _resourceKey = resourceKey; }

        public override bool IsValid(object? value) => _inner.IsValid(value);

        public override string FormatErrorMessage(string name) => Loc.T(_resourceKey);

        public void AddValidation(ClientModelValidationContext context)
        {
            if (!context.Attributes.ContainsKey("data-val"))
                context.Attributes.Add("data-val", "true");
            if (!context.Attributes.ContainsKey("data-val-email"))
                context.Attributes.Add("data-val-email", FormatErrorMessage(context.ModelMetadata.GetDisplayName() ?? string.Empty));
        }
    }

    public class LocalizedStringLengthAttribute : StringLengthAttribute
    {
        private readonly string _resourceKey;
        public LocalizedStringLengthAttribute(int maximumLength, string resourceKey) : base(maximumLength)
        {
            _resourceKey = resourceKey;
        }
        public override string FormatErrorMessage(string name)
        {
            // {0} in the translation will be replaced by MinimumLength
            return string.Format(Loc.T(_resourceKey), MinimumLength);
        }
    }

    /// <summary>
    /// Enforces the 5-rule password complexity policy introduced in Feb 2026:
    /// (1) min 8 chars, (2) uppercase, (3) lowercase, (4) digit, (5) special char
    /// from the fixed set !?@#$%^&amp;* — deliberately narrow to keep the UI
    /// hint compact and avoid keyboard-layout ambiguity across locales.
    ///
    /// <para>Only applied to NEW passwords (Register + ResetPassword). Login uses
    /// the old (pre-policy) password of existing users unchanged.</para>
    ///
    /// <para>Emits <c>data-val-pwdcomplex</c> and <c>data-val-pwdcomplex-*</c>
    /// attributes so client-side jQuery Validation adapter (registered in
    /// wwwroot/js/password-complexity.js) mirrors the same 5 checks and shows
    /// the localized rule list on submit failure.</para>
    /// </summary>
    public class LocalizedPasswordComplexityAttribute : ValidationAttribute, IClientModelValidator
    {
        // Deliberately narrow special-char set — matches the tooltip shown to
        // the user (PasswordRuleSpecial). Keep in sync with the JS regex in
        // password-complexity.js and the popover text in the two views.
        public const string SpecialChars = "!?@#$%^&*";

        public override bool IsValid(object? value)
        {
            var s = value as string;
            // Empty is intentionally treated as VALID here — the paired
            // LocalizedRequired attribute owns the "empty" error message.
            // Returning true keeps the summary to a single, clear error
            // instead of "Password is required" AND the rule list.
            if (string.IsNullOrEmpty(s)) return true;
            if (s.Length < 8) return false;
            bool hasUpper = false, hasLower = false, hasDigit = false, hasSpecial = false;
            foreach (var c in s)
            {
                if (c >= 'A' && c <= 'Z') hasUpper = true;
                else if (c >= 'a' && c <= 'z') hasLower = true;
                else if (c >= '0' && c <= '9') hasDigit = true;
                else if (SpecialChars.IndexOf(c) >= 0) hasSpecial = true;
            }
            return hasUpper && hasLower && hasDigit && hasSpecial;
        }

        public override string FormatErrorMessage(string name)
        {
            // Multi-line list shown in the server-side validation summary.
            // Each rule is a separate Loc key so translators handle them
            // independently across the 7 supported languages.
            return string.Join("\n", new[]
            {
                Loc.T("PasswordRulesTitle") + ":",
                "• " + Loc.T("PasswordRuleMinLength"),
                "• " + Loc.T("PasswordRuleUpper"),
                "• " + Loc.T("PasswordRuleLower"),
                "• " + Loc.T("PasswordRuleDigit"),
                "• " + Loc.T("PasswordRuleSpecial"),
            });
        }

        public void AddValidation(ClientModelValidationContext context)
        {
            if (!context.Attributes.ContainsKey("data-val"))
                context.Attributes.Add("data-val", "true");
            if (!context.Attributes.ContainsKey("data-val-pwdcomplex"))
                context.Attributes.Add("data-val-pwdcomplex", FormatErrorMessage(string.Empty));

            // Individual rule messages so the JS adapter can list precisely
            // which rules are still missing when it renders the inline error.
            void Add(string k, string v) { if (!context.Attributes.ContainsKey(k)) context.Attributes.Add(k, v); }
            Add("data-val-pwdcomplex-header",  Loc.T("PasswordRulesTitle"));
            Add("data-val-pwdcomplex-min",     Loc.T("PasswordRuleMinLength"));
            Add("data-val-pwdcomplex-upper",   Loc.T("PasswordRuleUpper"));
            Add("data-val-pwdcomplex-lower",   Loc.T("PasswordRuleLower"));
            Add("data-val-pwdcomplex-digit",   Loc.T("PasswordRuleDigit"));
            Add("data-val-pwdcomplex-special", Loc.T("PasswordRuleSpecial"));
            Add("data-val-pwdcomplex-specialset", SpecialChars);
        }
    }

    /// <summary>
    /// CompareAttribute in .NET uses a special adapter that bypasses FormatErrorMessage,
    /// so we do NOT inherit from it. Instead we implement ValidationAttribute + IClientModelValidator
    /// so both server-side and client-side validation use the localized message.
    /// </summary>
    public class LocalizedCompareAttribute : ValidationAttribute, IClientModelValidator
    {
        private readonly string _otherProperty;
        private readonly string _resourceKey;

        public LocalizedCompareAttribute(string otherProperty, string resourceKey)
        {
            _otherProperty = otherProperty;
            _resourceKey = resourceKey;
        }

        public override string FormatErrorMessage(string name) => Loc.T(_resourceKey);

        protected override ValidationResult? IsValid(object? value, ValidationContext validationContext)
        {
            var otherPropInfo = validationContext.ObjectType.GetProperty(_otherProperty);
            if (otherPropInfo == null)
                return new ValidationResult($"Unknown property: {_otherProperty}");

            var otherValue = otherPropInfo.GetValue(validationContext.ObjectInstance);
            if (!Equals(value, otherValue))
                return new ValidationResult(Loc.T(_resourceKey));

            return ValidationResult.Success;
        }

        public void AddValidation(ClientModelValidationContext context)
        {
            if (!context.Attributes.ContainsKey("data-val"))
                context.Attributes.Add("data-val", "true");
            if (!context.Attributes.ContainsKey("data-val-equalto"))
                context.Attributes.Add("data-val-equalto", Loc.T(_resourceKey));
            if (!context.Attributes.ContainsKey("data-val-equalto-other"))
                context.Attributes.Add("data-val-equalto-other", "*." + _otherProperty);
        }
    }
}
