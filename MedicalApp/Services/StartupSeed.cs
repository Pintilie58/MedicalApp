using MedicalApp.Data;
using MedicalApp.Models;
using Microsoft.EntityFrameworkCore;

namespace MedicalApp.Services
{
    /// <summary>
    /// Idempotent startup tasks. Runs once when the app starts:
    ///  - Ensures every existing User has a default profile named "Eu".
    ///  - Safe to run multiple times (checks before inserting).
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
    }
}
