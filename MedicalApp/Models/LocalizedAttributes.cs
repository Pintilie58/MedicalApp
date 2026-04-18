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
