using MedicalApp.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MedicalApp.Services
{
    /// <summary>
    /// "Only one instance may do this" — a lease row in SQL Server.
    ///
    /// Needed because hosted services (daily summary, budget alert) have their
    /// own clock and would otherwise run on EVERY instance: three instances =
    /// three identical emails. The winner of the lease does the work; the others
    /// skip that round.
    ///
    /// When scale-out is disabled (development, single instance) this always
    /// grants the lease without touching the database, so behaviour is unchanged.
    /// </summary>
    public class SingletonLeaseService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ScaleOutSettings _scaleOut;
        private readonly ILogger<SingletonLeaseService> _logger;

        public SingletonLeaseService(IServiceScopeFactory scopeFactory,
            IOptions<ScaleOutSettings> scaleOut, ILogger<SingletonLeaseService> logger)
        {
            _scopeFactory = scopeFactory;
            _scaleOut = scaleOut.Value;
            _logger = logger;
        }

        /// <summary>
        /// Tries to become the owner of <paramref name="jobName"/> for
        /// <paramref name="ttl"/>. True = this instance must do the work.
        /// </summary>
        public async Task<bool> TryAcquireAsync(string jobName, TimeSpan ttl,
            CancellationToken ct = default)
        {
            if (!_scaleOut.Enabled) return true;

            var owner = _scaleOut.ResolvedInstanceId;
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

                // One statement, so two instances racing cannot both win: the row
                // is only taken when it is free/expired or already ours.
                var rows = await db.Database.ExecuteSqlRawAsync(@"
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'AppSingletonLease')
BEGIN
    CREATE TABLE dbo.AppSingletonLease (
        JobName     nvarchar(100) NOT NULL PRIMARY KEY,
        Owner       nvarchar(200) NOT NULL,
        ExpiresUtc  datetime2      NOT NULL
    );
END;

MERGE dbo.AppSingletonLease WITH (HOLDLOCK) AS t
USING (SELECT {0} AS JobName) AS s ON t.JobName = s.JobName
WHEN MATCHED AND (t.ExpiresUtc < SYSUTCDATETIME() OR t.Owner = {1})
    THEN UPDATE SET Owner = {1}, ExpiresUtc = DATEADD(second, {2}, SYSUTCDATETIME())
WHEN NOT MATCHED
    THEN INSERT (JobName, Owner, ExpiresUtc)
         VALUES ({0}, {1}, DATEADD(second, {2}, SYSUTCDATETIME()));",
                    jobName, owner, (int)ttl.TotalSeconds);

                var won = rows > 0;
                if (!won)
                    _logger.LogInformation(
                        "Singleton lease '{Job}' is held by another instance; skipping this round.",
                        jobName);
                return won;
            }
            catch (Exception ex)
            {
                // Fail OPEN on purpose: a database hiccup must not silently stop
                // the daily summary from ever being sent again.
                _logger.LogWarning(ex,
                    "Singleton lease '{Job}' could not be evaluated; running anyway.", jobName);
                return true;
            }
        }
    }
}
