using MedicalApp.Data;
using MedicalApp.Models;

namespace MedicalApp.Services
{
    /// <summary>
    /// Centralizes billing for premium archive features (compare, charts, exports).
    ///
    /// Rules (decided Feb 2026):
    ///  - Viewing the archive list and downloading a stored PDF report is ALWAYS free
    ///    (GDPR: user's right to their own paid medical data). Those paths MUST NOT
    ///    call this service.
    ///  - Premium features (compare two interpretations, parameter evolution chart,
    ///    Excel/CSV export...) are free for 1 year after registration.
    ///  - After the free period expires, the user pays 1 credit for every 3 premium
    ///    feature uses. Cumulative counter:
    ///      use #1 free (counter 0 -> 1)
    ///      use #2 free (counter 1 -> 2)
    ///      use #3 free (counter 2 -> 3)
    ///      use #4 pays 1 credit, counter resets to 1 (this use counts as "one in the new pack")
    ///      use #5 free (counter 1 -> 2)
    ///      ...
    ///  - The credit is taken from the same pool as interpretations:
    ///      bonus first (BonusCreditsRemaining), then paid (CreditRest).
    /// </summary>
    public class ArchiveAccessService
    {
        /// <summary>How many premium uses are bundled per credit after the free period.</summary>
        public const int UsesPerCredit = 3;

        /// <summary>Free-period duration from the user's registration date.</summary>
        public static readonly TimeSpan FreePeriod = TimeSpan.FromDays(365);

        private readonly AppDbContext _db;
        private readonly ILogger<ArchiveAccessService> _logger;

        public ArchiveAccessService(AppDbContext db, ILogger<ArchiveAccessService> logger)
        {
            _db = db;
            _logger = logger;
        }

        public record CheckResult(bool Allowed, bool CreditConsumed, string? DenyReason);

        /// <summary>
        /// Tries to use one premium archive feature for the given user.
        /// On success, mutates the <paramref name="user"/> entity (counter or credit
        /// fields) but does NOT call SaveChangesAsync — the caller must persist.
        /// </summary>
        public CheckResult TryConsume(User user, string featureKey)
        {
            ArgumentNullException.ThrowIfNull(user);
            var now = DateTime.UtcNow;

            // Defensive: if FreeArchiveUntil is null (legacy users) treat it as
            // "already expired" — the seed will populate it at startup.
            var freeUntil = user.FreeArchiveUntil ?? user.DataC.Add(FreePeriod);

            if (now < freeUntil)
            {
                _logger.LogInformation(
                    "Archive premium '{Feature}' for {Email}: within free period (until {FreeUntil:u}).",
                    featureKey, user.Email, freeUntil);
                return new CheckResult(true, false, null);
            }

            // Paid phase. Are we still inside the current free-of-3 bundle?
            if (user.ArchivePremiumCounter < UsesPerCredit)
            {
                user.ArchivePremiumCounter += 1;
                _logger.LogInformation(
                    "Archive premium '{Feature}' for {Email}: free in bundle ({Counter}/{Max}).",
                    featureKey, user.Email, user.ArchivePremiumCounter, UsesPerCredit);
                return new CheckResult(true, false, null);
            }

            // Bundle exhausted — need to charge 1 credit. Bonus first, then paid.
            if (user.BonusCreditsRemaining > 0)
            {
                user.BonusCreditsConsumed += 1;
            }
            else if (user.CreditRest > 0)
            {
                user.CreditConsum += 1;
                user.CreditRest = user.Credite - user.CreditConsum;
            }
            else
            {
                _logger.LogWarning(
                    "Archive premium '{Feature}' for {Email}: DENIED — no credits available.",
                    featureKey, user.Email);
                return new CheckResult(false, false, "NoCredits");
            }

            // New bundle: this use counts as the first in the new pack.
            user.ArchivePremiumCounter = 1;

            _logger.LogInformation(
                "Archive premium '{Feature}' for {Email}: charged 1 credit. New counter={Counter}.",
                featureKey, user.Email, user.ArchivePremiumCounter);

            return new CheckResult(true, true, null);
        }

        /// <summary>
        /// Computes how many free premium uses remain before the user is charged.
        /// Useful to display a "2 left in this bundle" hint on the UI.
        /// </summary>
        public static int FreeUsesLeftInBundle(User user)
            => Math.Max(0, UsesPerCredit - user.ArchivePremiumCounter);

        /// <summary>
        /// Returns true when the user is still inside the 1-year free period.
        /// </summary>
        public static bool IsInFreePeriod(User user, DateTime? nowUtc = null)
        {
            var now = nowUtc ?? DateTime.UtcNow;
            var freeUntil = user.FreeArchiveUntil ?? user.DataC.Add(FreePeriod);
            return now < freeUntil;
        }
    }
}
