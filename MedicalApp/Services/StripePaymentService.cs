using MedicalApp.Data;
using MedicalApp.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Stripe;
using Stripe.Checkout;

namespace MedicalApp.Services
{
    /// <summary>
    /// Thin wrapper over Stripe.net for one-time credit-package purchases via Stripe
    /// Checkout (hosted page). Amounts always come from <see cref="CreditPackages"/>
    /// on the server — the browser only sends a package key.
    /// </summary>
    public class StripePaymentService
    {
        private readonly AppDbContext _db;
        private readonly PaymentSettings _settings;
        private readonly ILogger<StripePaymentService> _logger;
        private readonly IStripeClient? _client;

        public StripePaymentService(AppDbContext db, IOptions<PaymentSettings> settings,
            ILogger<StripePaymentService> logger)
        {
            _db = db;
            _settings = settings.Value;
            _logger = logger;
            if (!string.IsNullOrWhiteSpace(_settings.Stripe.SecretKey))
                _client = new StripeClient(_settings.Stripe.SecretKey);
        }

        public bool IsConfigured => _settings.UseStripe && _client != null;

        private IStripeClient Client =>
            _client ?? throw new InvalidOperationException(
                "Stripe is not configured: set Payments:Stripe:SecretKey (User Secrets locally, App Settings on Azure).");

        /// <summary>
        /// Creates the Checkout Session and the pending <see cref="PaymentTransaction"/>.
        /// Returns the hosted page URL to redirect the browser to.
        /// </summary>
        public async Task<(string CheckoutUrl, string SessionId)> CreateCheckoutAsync(
            User user, CreditPackage package, string packageLabel,
            string successUrl, string cancelUrl, string? uiLanguage = null,
            CancellationToken ct = default)
        {
            var options = new SessionCreateOptions
            {
                Mode = "payment",
                // Without this Stripe uses "auto" = the BROWSER's Accept-Language, so a
                // user with the UI in Spanish still got a Romanian checkout page on a
                // Romanian Windows. Our 7 UI codes (en/ro/fr/es/de/it/pt) are all valid
                // Stripe locales, so the app language maps 1:1; anything unexpected
                // falls back to Stripe's own detection.
                Locale = StripeLocale(uiLanguage),
                CustomerEmail = user.Email,
                ClientReferenceId = user.Email,
                LineItems = new List<SessionLineItemOptions>
                {
                    new()
                    {
                        Quantity = 1,
                        PriceData = new SessionLineItemPriceDataOptions
                        {
                            Currency = "eur",
                            UnitAmount = (long)Math.Round(package.PriceEur * 100m, MidpointRounding.AwayFromZero),
                            ProductData = new SessionLineItemPriceDataProductDataOptions
                            {
                                Name = $"MyMedicalApp — {packageLabel}",
                                Description = string.Format(
                                    Loc.T("StripeLineItemCredits", uiLanguage), package.Credits),
                            },
                        },
                    },
                },
                SuccessUrl = successUrl,
                CancelUrl = cancelUrl,
                Metadata = new Dictionary<string, string>
                {
                    ["userEmail"] = user.Email,
                    ["packageKey"] = package.Key,
                    ["credits"] = package.Credits.ToString(),
                },
            };

            var session = await new SessionService(Client).CreateAsync(options, cancellationToken: ct);

            _db.PaymentTransactions.Add(new PaymentTransaction
            {
                SessionId = session.Id,
                UserEmail = user.Email,
                PackageKey = package.Key,
                AmountEur = package.PriceEur,
                Currency = "eur",
                Status = PaymentTransaction.StatusInitiated,
                CreatedAt = DateTime.UtcNow,
            });
            await _db.SaveChangesAsync(ct);

            _logger.LogInformation("Stripe: Checkout Session {Session} created for {Email} / {Package} ({Price} EUR).",
                session.Id, user.Email, package.Key, package.PriceEur);
            return (session.Url, session.Id);
        }

        /// <summary>
        /// Maps a MyMedicalApp UI language onto a Stripe Checkout locale. All seven
        /// codes we ship (en/ro/fr/es/de/it/pt) are accepted by Stripe verbatim, so
        /// the only job here is to reject anything not in our own supported list and
        /// let Stripe auto-detect instead of failing the session creation.
        /// </summary>
        private static string StripeLocale(string? uiLanguage)
        {
            if (string.IsNullOrWhiteSpace(uiLanguage)) return "auto";
            var lang = (uiLanguage.Length > 2 ? uiLanguage[..2] : uiLanguage).ToLowerInvariant();
            return SupportedLanguagesConfig.Codes.Contains(lang) ? lang : "auto";
        }

        /// <summary>Asks Stripe directly whether the session is paid (return-page path, no webhook needed).</summary>
        public async Task<(bool Paid, string? PaymentIntentId)> IsPaidAtStripeAsync(string sessionId, CancellationToken ct = default)
        {
            var session = await new SessionService(Client).GetAsync(sessionId, cancellationToken: ct);
            var paid = string.Equals(session.PaymentStatus, "paid", StringComparison.OrdinalIgnoreCase);
            return (paid, session.PaymentIntentId);
        }

        /// <summary>
        /// Flips the transaction to "paid" exactly once. Returns false when it was
        /// already paid — or when another request won the race (RowVersion conflict).
        /// The caller grants credits ONLY when this returns true.
        /// </summary>
        public async Task<bool> TryMarkPaidAsync(PaymentTransaction tx, string? paymentIntentId, CancellationToken ct = default)
        {
            if (tx.Status == PaymentTransaction.StatusPaid) return false;
            tx.Status = PaymentTransaction.StatusPaid;
            tx.PaidAt = DateTime.UtcNow;
            tx.PaymentIntentId ??= paymentIntentId;
            try
            {
                await _db.SaveChangesAsync(ct);
                return true;
            }
            catch (DbUpdateConcurrencyException)
            {
                _logger.LogInformation("Stripe: session {Session} was settled concurrently by another request — skipping duplicate fulfilment.", tx.SessionId);
                _db.Entry(tx).State = EntityState.Detached;
                return false;
            }
        }

        public async Task MarkClosedAsync(PaymentTransaction tx, string status, CancellationToken ct = default)
        {
            if (tx.Status == PaymentTransaction.StatusPaid) return;
            tx.Status = status;
            try { await _db.SaveChangesAsync(ct); }
            catch (DbUpdateConcurrencyException) { _db.Entry(tx).State = EntityState.Detached; }
        }

        /// <summary>Verifies the Stripe-Signature header and parses the event. Throws on bad signature.</summary>
        public Event ParseWebhook(string json, string? signatureHeader)
        {
            if (string.IsNullOrWhiteSpace(_settings.Stripe.WebhookSecret))
                throw new InvalidOperationException("Payments:Stripe:WebhookSecret is not configured.");
            return EventUtility.ConstructEvent(json, signatureHeader, _settings.Stripe.WebhookSecret,
                throwOnApiVersionMismatch: false);
        }

        public Task<PaymentTransaction?> FindTransactionAsync(string sessionId, CancellationToken ct = default)
            => _db.PaymentTransactions.FirstOrDefaultAsync(t => t.SessionId == sessionId, ct);
    }
}
