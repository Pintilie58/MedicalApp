namespace MedicalApp.Services
{
    /// <summary>
    /// Configured via appsettings.json → "AdminSettings".
    /// Any user whose email matches an entry here will be granted IsAdmin=true
    /// at registration time (and auto-promoted at login if they were not already).
    /// </summary>
    public class AdminSettings
    {
        public List<string> Emails { get; set; } = new();

        public bool IsAdminEmail(string? email)
        {
            if (string.IsNullOrWhiteSpace(email)) return false;
            var normalized = email.Trim().ToLowerInvariant();
            return Emails.Any(e =>
                string.Equals(e?.Trim(), normalized, StringComparison.OrdinalIgnoreCase));
        }
    }
}
