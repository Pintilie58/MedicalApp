using System.Collections.Concurrent;

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
    }

    /// <summary>
    /// In-memory thread-safe store for pending registrations, keyed by email.
    /// For a production system this would be replaced with a persistent cache
    /// (Redis) or a DB table, but in-memory is sufficient for single-instance apps.
    /// </summary>
    public class PendingRegistrationStore
    {
        private readonly ConcurrentDictionary<string, PendingRegistration> _store = new();

        public void Save(PendingRegistration pending)
        {
            _store[pending.Email] = pending;
        }

        public PendingRegistration? Get(string email)
        {
            if (_store.TryGetValue(email, out var pending))
            {
                if (pending.ExpiresAt < DateTime.UtcNow)
                {
                    _store.TryRemove(email, out _);
                    return null;
                }
                return pending;
            }
            return null;
        }

        public void Remove(string email) => _store.TryRemove(email, out _);

        /// <summary>
        /// Removes entries expired more than 1 hour ago. Call periodically (not critical).
        /// </summary>
        public void Cleanup()
        {
            var cutoff = DateTime.UtcNow.AddHours(-1);
            foreach (var kv in _store)
            {
                if (kv.Value.ExpiresAt < cutoff)
                    _store.TryRemove(kv.Key, out _);
            }
        }
    }
}
