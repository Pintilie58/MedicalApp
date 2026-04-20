namespace MedicalApp.Services
{
    public record CreditPackage(string Key, string NameKey, decimal PriceEur, int Credits);

    /// <summary>
    /// Available credit packages. Kept as code constants for now; can be moved to DB later.
    /// </summary>
    public static class CreditPackages
    {
        public static readonly IReadOnlyList<CreditPackage> All = new List<CreditPackage>
        {
            new("normal",   "PackageNormal",   5m,   25),
            new("standard", "PackageStandard", 10m,  60),
            new("super",    "PackageSuper",    50m,  330),
            new("premium",  "PackagePremium",  100m, 700)
        };

        public static CreditPackage? GetByKey(string key) =>
            All.FirstOrDefault(p => p.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
    }
}
