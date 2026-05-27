namespace MedicalApp.Services
{
    /// <summary>
    /// A single credit package offer.
    /// </summary>
    /// <param name="Key">Stable, lowercase identifier used in URLs and DB (e.g. "premium", "cam_pro").</param>
    /// <param name="NameKey">Localization key for the display name.</param>
    /// <param name="PriceEur">Price in EUR.</param>
    /// <param name="Credits">Number of interpretation credits granted.</param>
    /// <param name="Audience">"Individual" (B2C) or "Clinic" (B2B CAM module).</param>
    public record CreditPackage(
        string Key,
        string NameKey,
        decimal PriceEur,
        int Credits,
        string Audience = "Individual");

    /// <summary>
    /// Available credit packages. Kept as code constants for now; can be moved to DB later.
    /// Pachetele <c>"Clinic"</c> sunt vizibile DOAR pentru conturile <c>User.UserType == "Clinic"</c>.
    /// </summary>
    public static class CreditPackages
    {
        public static readonly IReadOnlyList<CreditPackage> All = new List<CreditPackage>
        {
            // ----- B2C (Persoană fizică) -----
            new("normal",   "PackageNormal",   5m,   25),
            new("standard", "PackageStandard", 10m,  60),
            new("super",    "PackageSuper",    50m,  330),
            new("premium",  "PackagePremium",  100m, 700),

            // ----- B2B (Clinici de Analize Medicale) -----
            // Pachet test: 50 credite la 30 EUR (0.60/credit), pentru ca o clinica
            // sa poata valida fluxul fara cost mare. Apoi trece la pachetul mare.
            new("cam_test", "PackageCamTest", 30m,   50,   "Clinic"),
            // Pachet principal CAM: 1000 credite / 500 EUR (0.50/credit, ~17% discount fata
            // de pachetul Premium B2C). Stabilit cu utilizatorul Feb 2026.
            new("cam_pro",  "PackageCamPro",  500m,  1000, "Clinic")
        };

        public static CreditPackage? GetByKey(string key) =>
            All.FirstOrDefault(p => p.Key.Equals(key, StringComparison.OrdinalIgnoreCase));

        /// <summary>
        /// Packages a given audience is allowed to see/buy.
        /// "Individual" → only B2C packages. "Clinic" → only CAM packages.
        /// </summary>
        public static IEnumerable<CreditPackage> ForAudience(string audience) =>
            All.Where(p => string.Equals(p.Audience, audience, StringComparison.OrdinalIgnoreCase));
    }
}
