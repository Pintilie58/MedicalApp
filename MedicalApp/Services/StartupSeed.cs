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

        /// <summary>
        /// Demo seed for the CAM (Clinici Analize Medicale) module.
        /// Creates a ready-to-use clinic account with:
        ///   * email:  <c>clinica.demo@medicalapp.test</c>
        ///   * pass:   <c>Demo1234!</c>
        ///   * clinic: "Clinica Demo Test", București, Str. Test 1
        ///   * 1000 credits pre-loaded (cam_pro package, marked "seed")
        ///   * Original/Sends/Sumar/Errors folders created on disk
        ///   * 5 fictional patients, ALL with emails pointing to the developer's
        ///     mailbox (vasilepintilie2003@gmail.com) so batch testing in
        ///     Faza 3 sends every test email to a single inbox.
        ///
        /// IDEMPOTENT: re-running the app does NOT re-create the user, top-up
        /// credits, or duplicate patients. Safe to call on every startup.
        /// </summary>
        public static async Task EnsureClinicaDemoAsync(
            IServiceProvider services,
            ICamFileStore camFiles,
            ILogger logger)
        {
            const string demoEmail = "clinica.demo@medicalapp.test";
            const string demoPasswordPlain = "Demo1234!";
            const string operatorRedirectEmail = "vasilepintilie2003@gmail.com";

            using var scope = services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            // -------- User --------
            var user = await db.Users.FirstOrDefaultAsync(u => u.Email == demoEmail);
            bool createdUser = false;
            if (user == null)
            {
                user = new User
                {
                    Email = demoEmail,
                    Parola = BCrypt.Net.BCrypt.HashPassword(demoPasswordPlain),
                    Credite = 1000,
                    CreditConsum = 0,
                    CreditRest = 1000,
                    TotalPaid = 500m,
                    DataC = DateTime.UtcNow,
                    UserType = "Clinic",
                    FreeArchiveUntil = DateTime.UtcNow.Add(ArchiveAccessService.FreePeriod),
                };
                db.Users.Add(user);
                createdUser = true;
            }

            // -------- Clinic --------
            var clinic = await db.Clinics.FirstOrDefaultAsync(c => c.UserEmail == demoEmail);
            if (clinic == null)
            {
                clinic = new Clinic
                {
                    UserEmail = demoEmail,
                    Name = "Clinica Demo Test",
                    City = "București",
                    Address = "Str. Test 1",
                    CreatedAt = DateTime.UtcNow
                };
                db.Clinics.Add(clinic);
                await db.SaveChangesAsync(); // need clinic.Id for the folder step below
            }

            // -------- Folders on disk --------
            if (clinic.FoldersCreatedAt == null)
            {
                try
                {
                    await camFiles.EnsureClinicFoldersAsync(clinic);
                    clinic.FoldersCreatedAt = DateTime.UtcNow;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex,
                        "StartupSeed: failed to create Clinica Demo folders on disk. " +
                        "User can still log in and trigger creation manually from the dashboard.");
                }
            }

            // -------- Marker Purchase row (so admin dashboard shows the seed) --------
            var alreadyHasSeedPurchase = await db.Purchases
                .AnyAsync(p => p.UserEmail == demoEmail && p.PaymentMethod == "seed");
            if (!alreadyHasSeedPurchase)
            {
                db.Purchases.Add(new Purchase
                {
                    UserEmail = demoEmail,
                    PurchasedAt = DateTime.UtcNow,
                    AmountEur = 500m,
                    CreditsAdded = 1000,
                    PaymentMethod = "seed",
                    PackageKey = "cam_pro"
                });
            }

            // -------- 5 fictional patients --------
            // Each patient's "real" email is set to the developer's inbox so we
            // can verify batch-result emails in Faza 3 without spamming anyone.
            var fictionalNames = new[]
            {
                "Ion Popescu",
                "Maria Ionescu",
                "Andrei Georgescu",
                "Elena Vasilescu",
                "Mihai Constantinescu"
            };
            foreach (var name in fictionalNames)
            {
                var key = CamPatientKey.Normalize(name);
                var exists = await db.ClinicPatients
                    .AnyAsync(p => p.ClinicId == clinic.Id
                                   && p.NameKey == key
                                   && p.Email == operatorRedirectEmail);
                if (exists) continue;
                db.ClinicPatients.Add(new ClinicPatient
                {
                    ClinicId = clinic.Id,
                    Name = name,
                    NameKey = key,
                    Email = operatorRedirectEmail,
                    CreatedAt = DateTime.UtcNow
                });
            }

            await db.SaveChangesAsync();

            if (createdUser)
            {
                logger.LogInformation(
                    "StartupSeed: Clinica Demo created — login {Email} / password {Pass} / 1000 credits.",
                    demoEmail, demoPasswordPlain);
            }
            else
            {
                logger.LogInformation(
                    "StartupSeed: Clinica Demo already exists — refreshed patients/purchase rows if missing.");
            }
        }

        /// <summary>
        /// CAM Faza 3 — decizie d)i: NU avem auto-resume pentru loturi în execuție.
        /// Orice ClinicBatchRun rămas cu Status="Running" la pornirea aplicației
        /// (după un crash/restart) e marcat ca "Failed" + FinishedAt = now. Operatorul
        /// vede statusul real în history și relansează manual lotul.
        /// </summary>
        public static async Task FailOrphanedBatchesAsync(IServiceProvider services, ILogger logger)
        {
            using var scope = services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var orphans = await db.ClinicBatchRuns
                .Where(b => b.Status == "Running")
                .ToListAsync();
            if (orphans.Count == 0) return;

            var now = DateTime.UtcNow;
            foreach (var b in orphans)
            {
                b.Status = "Failed";
                b.FinishedAt = now;
            }
            await db.SaveChangesAsync();
            logger.LogWarning(
                "StartupSeed: flipped {Count} orphaned CAM batch(es) from Running → Failed.",
                orphans.Count);
        }
    }
}
