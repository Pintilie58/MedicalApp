namespace MedicalApp.Services
{
    /// <summary>
    /// Bound from the "Payments" section. Provider "Simulated" keeps the historical
    /// always-succeeds card form (local development only); "Stripe" sends the user
    /// to Stripe Checkout and credits the account only after Stripe confirms.
    /// </summary>
    public class PaymentSettings
    {
        public const string ProviderSimulated = "Simulated";
        public const string ProviderStripe = "Stripe";

        public string Provider { get; set; } = ProviderSimulated;

        public StripeOptions Stripe { get; set; } = new();

        public bool UseStripe =>
            string.Equals(Provider, ProviderStripe, StringComparison.OrdinalIgnoreCase);

        public class StripeOptions
        {
            /// <summary>sk_test_… / sk_live_… — never in a file, only User Secrets / App Settings.</summary>
            public string SecretKey { get; set; } = string.Empty;

            /// <summary>whsec_… from Dashboard → Developers → Webhooks. Empty = webhook endpoint refuses everything.</summary>
            public string WebhookSecret { get; set; } = string.Empty;

            /// <summary>pk_… (not needed for hosted Checkout; kept for a future embedded form).</summary>
            public string PublishableKey { get; set; } = string.Empty;
        }
    }
}
