using System.Collections.Concurrent;

using Microsoft.Extensions.Caching.Distributed;
using System.Text.Json;

namespace MedicalApp.Services
{
    /// <summary>
    /// Holds data for a user who completed the register form but has NOT yet
    /// verified their email. Expires after a short time so stale data is purged.
    /// </summary>
    public class PendingRegistration
    {
        public string Email { get; set; } = string.Empty;
        public string HashedPassword { get; set; } = string.Empty;
        public string VerificationCode { get; set; } = string.Empty;
        public DateTime ExpiresAt { get; set; }
        public int AttemptsLeft { get; set; } = 5;

        /// <summary>Promo code typed at register, applied on successful verification.</summary>
        public string? PromoCode { get; set; }

        // ----- CAM (Clinici de Analize Medicale) optional fields -----
        // Carried verbatim from the register form so we can create the
        // matching Clinic row right after the user verifies their email.

        /// <summary>"Individual" (default) or "Clinic".</summary>
        public string UserType { get; set; } = "Individual";

        public string? ClinicName { get; set; }
        public string? ClinicCity { get; set; }
        public string? ClinicAddress { get; set; }
    }

    /// <summary>
    /// In-memory thread-safe store for pending registrations, keyed by email.
    /// For a production system this would be replaced with a persistent cache
    /// (Redis) or a DB table, but in-memory is sufficient for single-instance apps.
    /// </summary>
    /// <summary>
    /// Holds registrations waiting for email verification.
    ///
    /// Backed by <see cref="IDistributedCache"/> — NOT by a dictionary in this
    /// process. With two instances, the verification code was written on one
    /// instance and looked up on the other, so half the sign-ups failed with
    /// "invalid code". Locally the distributed cache IS in-process memory, so
    /// behaviour is identical to before; in the cloud it becomes the SQL Server
    /// cache automatically (see ScaleOutSettings).
    ///
    /// The public API stays synchronous on purpose: registration happens a few
    /// times a day, and changing it would mean touching every call site in
    /// AccountController for no real gain.
    /// </summary>
    public class PendingRegistrationStore
    {
        private const string KeyPrefix = "pendreg:";
        private readonly IDistributedCache _cache;

        public PendingRegistrationStore(IDistributedCache cache) => _cache = cache;

        private static string Key(string email) =>
            KeyPrefix + (email ?? string.Empty).Trim().ToLowerInvariant();

        public void Save(PendingRegistration pending)
        {
            // Keep it a little past its own expiry so Get() can still report
            // "expired" rather than "never existed".
            var ttl = pending.ExpiresAt - DateTime.UtcNow + TimeSpan.FromMinutes(30);
            if (ttl < TimeSpan.FromMinutes(1)) ttl = TimeSpan.FromMinutes(1);

            _cache.SetString(Key(pending.Email),
                JsonSerializer.Serialize(pending),
                new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = ttl });
        }

        public PendingRegistration? Get(string email)
        {
            var raw = _cache.GetString(Key(email));
            if (string.IsNullOrEmpty(raw)) return null;

            var pending = JsonSerializer.Deserialize<PendingRegistration>(raw);
            if (pending == null) return null;

            if (pending.ExpiresAt < DateTime.UtcNow)
            {
                Remove(email);
                return null;
            }
            return pending;
        }

        public void Remove(string email) => _cache.Remove(Key(email));

        /// <summary>
        /// No-op: the cache expires entries by itself. Kept so existing callers
        /// (and the cleanup timer) continue to compile and behave.
        /// </summary>
        public void Cleanup() { }
    }
}
