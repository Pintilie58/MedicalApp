using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using MedicalApp.Services;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

int fails = 0;
void Check(string label, bool ok, string detail = "")
{
    Console.WriteLine((ok ? "PASS  " : "FAIL  ") + label + (detail.Length > 0 ? "  ->  " + detail : ""));
    if (!ok) fails++;
}

// ---------------------------------------------------------------- 1. Settings
var off = new ScaleOutSettings();
Check("Scale-out e DEZACTIVAT implicit (local neschimbat)", !off.Enabled);
Check("Instanta implicita = numele masinii",
    off.ResolvedInstanceId == Environment.MachineName, off.ResolvedInstanceId);
Check("InstanceId explicit e respectat",
    new ScaleOutSettings { InstanceId = "web-2" }.ResolvedInstanceId == "web-2");
Check("Perioada de grata implicita e 30 min", off.OrphanGraceMinutes == 30);

// ------------------------------------- 2. PendingRegistrationStore pe cache
IDistributedCache memCache = new Microsoft.Extensions.Caching.Distributed
    .MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
var store = new PendingRegistrationStore(memCache);

var pending = new PendingRegistration
{
    Email = "Nou@Test.RO", HashedPassword = "hash", VerificationCode = "1234",
    ExpiresAt = DateTime.UtcNow.AddMinutes(15), AttemptsLeft = 5,
    UserType = "Clinic", ClinicName = "Clinica X", PromoCode = "PROMO10"
};
store.Save(pending);

var back = store.Get("nou@test.ro");
Check("Codul se regaseste (email case-insensitive)", back?.VerificationCode == "1234",
    back?.VerificationCode ?? "null");
Check("Toate campurile supravietuiesc serializarii",
    back != null && back.UserType == "Clinic" && back.ClinicName == "Clinica X"
    && back.PromoCode == "PROMO10" && back.AttemptsLeft == 5);

// Simuleaza ALTA INSTANTA: alt obiect store, ACELASI cache distribuit
var otherInstance = new PendingRegistrationStore(memCache);
Check("O ALTA instanta vede acelasi cod (bugul de inregistrare rezolvat)",
    otherInstance.Get("nou@test.ro")?.VerificationCode == "1234");

// Decrementarea incercarilor se propaga
back!.AttemptsLeft = 3;
store.Save(back);
Check("Actualizarea se vede din cealalta instanta",
    otherInstance.Get("nou@test.ro")?.AttemptsLeft == 3);

store.Remove("NOU@test.ro");
Check("Remove functioneaza (case-insensitive)", store.Get("nou@test.ro") == null);
Check("Get pe inexistent intoarce null", store.Get("nimeni@test.ro") == null);

var expired = new PendingRegistration
{
    Email = "old@test.ro", VerificationCode = "9999",
    ExpiresAt = DateTime.UtcNow.AddMinutes(-1)
};
store.Save(expired);
Check("Codul expirat e tratat ca inexistent", store.Get("old@test.ro") == null);
store.Cleanup();
Check("Cleanup() nu arunca excepții (no-op)", true);

// ------------------------------------- 3. Lease: fail-open cand e dezactivat
var lease = new SingletonLeaseService(new NoScopes(),
    Options.Create(new ScaleOutSettings { Enabled = false }),
    NullLogger<SingletonLeaseService>.Instance);
Check("Cu scale-out oprit, ștafeta se acorda mereu (fara SQL)",
    await lease.TryAcquireAsync("daily-summary", TimeSpan.FromHours(1)));

var leaseOn = new SingletonLeaseService(new NoScopes(),
    Options.Create(new ScaleOutSettings { Enabled = true }),
    NullLogger<SingletonLeaseService>.Instance);
Check("Cu SQL inaccesibil, ștafeta cade OPEN (sumarul nu se blocheaza definitiv)",
    await leaseOn.TryAcquireAsync("daily-summary", TimeSpan.FromHours(1)));

// ------------------------------------- 4. Chei Data Protection in Blob
try
{
    var blobClient = ScaleOutBootstrap.EnsureDataProtectionBlob(
        new CamBlobSettings { ConnectionString = "UseDevelopmentStorage=true" },
        new ScaleOutSettings { Enabled = true, DataProtectionContainer = "dataprotection" });
    Check("Containerul de chei se creeaza si intoarce blobul corect",
        blobClient.Name == "keys.xml" && blobClient.BlobContainerName == "dataprotection",
        blobClient.Uri.ToString());

    // Doua "instante" configurate identic trebuie sa arate spre ACELASI blob
    var blob2 = ScaleOutBootstrap.EnsureDataProtectionBlob(
        new CamBlobSettings { ConnectionString = "UseDevelopmentStorage=true" },
        new ScaleOutSettings { Enabled = true });
    Check("Ambele instante folosesc acelasi blob de chei",
        blob2.Uri == blobClient.Uri, blob2.Uri.ToString());
}
catch (Exception ex)
{
    Check("Azurite disponibil pentru testul de chei", false, ex.GetBaseException().Message);
}

// ---- 5. Doua "instante" cripteaza/decripteaza reciproc (antiforgery/cookie)
try
{
    Microsoft.AspNetCore.DataProtection.IDataProtectionProvider Instance()
    {
        var sc = new ServiceCollection();
        sc.AddDataProtection()
            .SetApplicationName("MyMedicalApp")
            .PersistKeysToAzureBlobStorage(ScaleOutBootstrap.EnsureDataProtectionBlob(
                new CamBlobSettings { ConnectionString = "UseDevelopmentStorage=true" },
                new ScaleOutSettings { Enabled = true }));
        return sc.BuildServiceProvider()
            .GetRequiredService<Microsoft.AspNetCore.DataProtection.IDataProtectionProvider>();
    }

    var token = Instance().CreateProtector("antiforgery").Protect("utilizator-123");
    var decoded = Instance().CreateProtector("antiforgery").Unprotect(token);
    Check("Instanta B decripteaza tokenul creat de instanta A", decoded == "utilizator-123", decoded);
}
catch (Exception ex)
{
    Check("Round-trip Data Protection intre instante", false, ex.GetBaseException().Message);
}

// ---- 6. Recuperarea orfanilor nu omoara joburile altei instante
{
    var opts = new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<MedicalApp.Data.AppDbContext>()
        .UseInMemoryDatabase("orphans_" + Guid.NewGuid()).Options;
    var sc = new ServiceCollection();
    sc.AddSingleton(opts);
    sc.AddScoped<MedicalApp.Data.AppDbContext>();
    var provider = sc.BuildServiceProvider();

    using (var db = new MedicalApp.Data.AppDbContext(opts))
    {
        db.Users.Add(new MedicalApp.Models.User { Email = "u@test.ro", Parola = "x",
            Credite = 10, CreditConsum = 2, CreditRest = 8 });
        // Job pornit ACUM pe o alta instanta
        db.InterpretationHistories.Add(new MedicalApp.Models.InterpretationHistory {
            UserEmail = "u@test.ro", Status = "processing", CreditsConsumed = 1,
            CreatedAt = DateTime.UtcNow.AddMinutes(-2) });
        // Job rămas orfan de ieri
        db.InterpretationHistories.Add(new MedicalApp.Models.InterpretationHistory {
            UserEmail = "u@test.ro", Status = "processing", CreditsConsumed = 1,
            CreatedAt = DateTime.UtcNow.AddHours(-5) });
        db.SaveChanges();
    }

    await StartupSeed.FailOrphanedInterpretationsAsync(provider,
        NullLogger.Instance, new ScaleOutSettings { Enabled = true, OrphanGraceMinutes = 30 });

    using (var db = new MedicalApp.Data.AppDbContext(opts))
    {
        var rows = db.InterpretationHistories.OrderBy(h => h.CreatedAt).ToList();
        Check("Jobul vechi (orfan real) devine 'error'", rows[0].Status == "error", rows[0].Status);
        Check("Jobul proaspat al altei instante NU e atins",
            rows[1].Status == "processing", rows[1].Status);
        var u = db.Users.First();
        Check("Se restituie exact un credit (nu doua)",
            u.CreditConsum == 1 && u.CreditRest == 9, $"consum={u.CreditConsum} rest={u.CreditRest}");
    }

    // Mod single-instance: comportamentul de dinainte (toate devin error)
    var opts2 = new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<MedicalApp.Data.AppDbContext>()
        .UseInMemoryDatabase("orphans2_" + Guid.NewGuid()).Options;
    var sc2 = new ServiceCollection();
    sc2.AddSingleton(opts2);
    sc2.AddScoped<MedicalApp.Data.AppDbContext>();
    using (var db = new MedicalApp.Data.AppDbContext(opts2))
    {
        db.Users.Add(new MedicalApp.Models.User { Email = "u@test.ro", Parola = "x",
            Credite = 10, CreditConsum = 1, CreditRest = 9 });
        db.InterpretationHistories.Add(new MedicalApp.Models.InterpretationHistory {
            UserEmail = "u@test.ro", Status = "processing", CreditsConsumed = 1,
            CreatedAt = DateTime.UtcNow.AddMinutes(-1) });
        db.SaveChanges();
    }
    await StartupSeed.FailOrphanedInterpretationsAsync(sc2.BuildServiceProvider(),
        NullLogger.Instance, new ScaleOutSettings { Enabled = false });
    using (var db = new MedicalApp.Data.AppDbContext(opts2))
    {
        Check("Mod single-instance: jobul proaspat e marcat error (ca inainte)",
            db.InterpretationHistories.First().Status == "error");
    }
}

Console.WriteLine(fails == 0 ? "\nALL PASS" : $"\n{fails} FAIL(S)");
return fails == 0 ? 0 : 1;

sealed class NoScopes : IServiceScopeFactory
{
    public IServiceScope CreateScope() => throw new InvalidOperationException("no DB in probe");
}
