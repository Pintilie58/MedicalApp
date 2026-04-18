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
    /// EmailAddressAttribute is sealed in .NET, so we wrap it instead of inheriting.
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

    public class LocalizedCompareAttribute : CompareAttribute
    {
        private readonly string _resourceKey;
        public LocalizedCompareAttribute(string otherProperty, string resourceKey) : base(otherProperty)
        {
            _resourceKey = resourceKey;
        }
        public override string FormatErrorMessage(string name) => Loc.T(_resourceKey);
    }
}
