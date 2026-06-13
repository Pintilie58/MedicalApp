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
            // Stabilit cu utilizatorul Feb 2026 — paliere accesibile + discount progresiv:
            //    6 EUR  →   2 credite  (~3.00 EUR/credit) — pachet "Normal" pentru încercare
            //   11 EUR  →   4 credite  (~2.75 EUR/credit) — pachet "Standard" uzual
            //   50 EUR  →  18 credite  (~2.78 EUR/credit) — pachet "Super" pentru familie
            //  100 EUR  →  38 credite  (~2.63 EUR/credit) — pachet "Premium" volum
            new("normal",   "PackageNormal",   6m,   2),
            new("standard", "PackageStandard", 11m,  4),
            new("super",    "PackageSuper",    50m,  18),
            new("premium",  "PackagePremium",  100m, 38),

            // ----- B2B (Clinici de Analize Medicale) -----
            // Stabilit cu utilizatorul Feb 2026. Discount progresiv:
            //   50 EUR  →  17 credite (~2.94 EUR/credit) — pachet "Starter" pentru pilot
            //   500 EUR → 167 credite (~2.99 EUR/credit) — pachet "Business" zilnic
            //  1000 EUR → 350 credite (~2.86 EUR/credit) — pachet "Enterprise" volum mare
            // Cheile vechi (cam_test / cam_pro) au fost retrase; istoricul Purchases
            // care le conține se afișează corect pe baza credit/eur snapshot-uite.
            new("cam_starter",    "PackageCamStarter",    50m,   17,  "Clinic"),
            new("cam_business",   "PackageCamBusiness",   500m,  167, "Clinic"),
            new("cam_enterprise", "PackageCamEnterprise", 1000m, 350, "Clinic")
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
