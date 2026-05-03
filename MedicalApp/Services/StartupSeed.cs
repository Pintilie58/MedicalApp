using MedicalApp.Data;
using MedicalApp.Models;
using Microsoft.EntityFrameworkCore;

namespace MedicalApp.Services
{
    /// <summary>
    /// Idempotent startup tasks. Runs once when the app starts:
    ///  - Ensures every existing User has a default profile named "Eu".
    ///  - Ensures every existing User has FreeArchiveUntil set (1-year grace period
    ///    from the first time the app boots after the feature was introduced).
    ///  - Safe to run multiple times (checks before inserting/updating).
    /// </summary>
    public static class StartupSeed
    {
        public const string DefaultProfileName = "Eu";

        public static async Task EnsureDefaultProfilesAsync(IServiceProvider services, ILogger logger)
        {
            using var scope = services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            // Users that do not have ANY profile yet
            var emailsWithoutProfile = await db.Users
                .Where(u => !db.Profiles.Any(p => p.UserEmail == u.Email))
                .Select(u => u.Email)
                .ToListAsync();

            if (emailsWithoutProfile.Count == 0)
            {
                logger.LogInformation("StartupSeed: all users already have at least one profile.");
                return;
            }

            foreach (var email in emailsWithoutProfile)
            {
                db.Profiles.Add(new Profile
                {
                    UserEmail = email,
                    Name = DefaultProfileName,
                    Relationship = "self",
                    IsDefault = true,
                    CreatedAt = DateTime.UtcNow
                });
            }

            await db.SaveChangesAsync();
            logger.LogInformation(
                "StartupSeed: created default \"{Name}\" profile for {Count} user(s).",
                DefaultProfileName, emailsWithoutProfile.Count);
        }

        /// <summary>
        /// Backfills FreeArchiveUntil for users who registered BEFORE the premium-
        /// archive policy existed. They get a fresh 1-year grace period from today
        /// (we cannot retroactively punish them). New users set this at registration.
        /// </summary>
        public static async Task EnsureFreeArchiveUntilAsync(IServiceProvider services, ILogger logger)
        {
            using var scope = services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var legacyUsers = await db.Users
                .Where(u => u.FreeArchiveUntil == null)
                .ToListAsync();

            if (legacyUsers.Count == 0)
            {
                logger.LogInformation("StartupSeed: all users already have FreeArchiveUntil set.");
                return;
            }

            var graceUntil = DateTime.UtcNow.Add(ArchiveAccessService.FreePeriod);
            foreach (var u in legacyUsers)
            {
                u.FreeArchiveUntil = graceUntil;
            }
            await db.SaveChangesAsync();

            logger.LogInformation(
                "StartupSeed: granted 1-year FreeArchiveUntil={GraceUntil:u} to {Count} legacy user(s).",
                graceUntil, legacyUsers.Count);
        }
    }
}
