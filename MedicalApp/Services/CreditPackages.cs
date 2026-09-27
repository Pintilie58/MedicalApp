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
        string Audience = "Individual",
        decimal? OriginalPriceEur = null,
        int DiscountPercent = 0)
    {
        public bool IsDiscounted => DiscountPercent > 0 && OriginalPriceEur.HasValue;
        public decimal SavingEur => IsDiscounted ? OriginalPriceEur!.Value - PriceEur : 0m;
    }

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
            //   39 EUR  →  18 credite  (~2.17 EUR/credit) — pachet "Super" pentru familie
            //   89 EUR  →  45 credite  (~1.98 EUR/credit) — pachet "Premium" volum
            // (Preț Super/Premium recalibrat iunie 2026 la cererea utilizatorului:
            //  50→39 EUR, 100→89 EUR cu 45 credite în loc de 38.)
            new("normal",   "PackageNormal",   6m,   2),
            new("standard", "PackageStandard", 11m,  4),
            new("super",    "PackageSuper",    39m,  18),
            new("premium",  "PackagePremium",  89m,  45),

            // ----- B2B (Clinici de Analize Medicale) -----
            // Stabilit cu utilizatorul Feb 2026. Discount progresiv:
            //   49 EUR  →  17 credite (~2.88 EUR/credit) — pachet "Starter" pentru pilot
            //   499 EUR → 183 credite (~2.73 EUR/credit) — pachet "Business" zilnic
            //   999 EUR → 390 credite (~2.56 EUR/credit) — pachet "Enterprise" volum mare
            // (Prețuri psihologice 49/499/999 din sept. 2026, creditele neschimbate.)
            // Cheile vechi (cam_test / cam_pro) au fost retrase; istoricul Purchases
            // care le conține se afișează corect pe baza credit/eur snapshot-uite.
            new("cam_starter",    "PackageCamStarter",    49m,   17,  "Clinic"),
            new("cam_business",   "PackageCamBusiness",   499m,  183, "Clinic"),
            new("cam_enterprise", "PackageCamEnterprise", 999m,  390, "Clinic"),

            // ----- CM (Cabinet Medical, iunie 2026) -----
            // Un singur pachet: 89 EUR → 45 credite (~1.98 EUR/credit). Aceeași
            // valoare ca "Premium" B2C, dar ofertă SEPARATĂ, ca prețul/creditele
            // cabinetului să poată fi schimbate fără să atingă persoanele fizice.
            new("cabinet_premium", "PackageCabinetPremium", 89m, 45, "Cabinet")
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
