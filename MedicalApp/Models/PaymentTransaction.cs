using System.ComponentModel.DataAnnotations;

namespace MedicalApp.Models
{
    /// <summary>
    /// One row per Stripe Checkout Session we started. Created BEFORE the redirect
    /// to Stripe; flipped to "paid" exactly once (return page or webhook, whichever
    /// comes first) and only then are credits granted. Purchases stays the
    /// financial ledger; this table is the payment-provider handshake.
    /// </summary>
    public class PaymentTransaction
    {
        public const string StatusInitiated = "initiated";
        public const string StatusPaid = "paid";
        public const string StatusFailed = "failed";
        public const string StatusExpired = "expired";

        public int Id { get; set; }

        /// <summary>Stripe Checkout Session id (cs_…). Unique.</summary>
        [Required, StringLength(120)]
        public string SessionId { get; set; } = string.Empty;

        [Required, StringLength(200)]
        public string UserEmail { get; set; } = string.Empty;

        [Required, StringLength(50)]
        public string PackageKey { get; set; } = string.Empty;

        public decimal AmountEur { get; set; }

        [StringLength(10)]
        public string Currency { get; set; } = "eur";

        [Required, StringLength(20)]
        public string Status { get; set; } = StatusInitiated;

        /// <summary>Stripe PaymentIntent id (pi_…) once known; stored on the Purchase too.</summary>
        [StringLength(120)]
        public string? PaymentIntentId { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime? PaidAt { get; set; }

        /// <summary>Optimistic concurrency: identifies the loser of a webhook-vs-return race, so credits are never granted twice.</summary>
        [Timestamp]
        public byte[]? RowVersion { get; set; }
    }
}
