using MedicalApp.Services;
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

    public class LocalizedEmailAddressAttribute : EmailAddressAttribute
    {
        private readonly string _resourceKey;
        public LocalizedEmailAddressAttribute(string resourceKey) { _resourceKey = resourceKey; }
        public override string FormatErrorMessage(string name) => Loc.T(_resourceKey);
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
