using MedicalApp.Data;
using MedicalApp.Models;
using Microsoft.EntityFrameworkCore;

namespace MedicalApp.Services
{
    public sealed record PromotionState(string Module, int DiscountPercent, bool PurchasesSuspended, DateTime? UpdatedAt, string? UpdatedBy)
    {
        public bool HasDiscount => DiscountPercent > 0 && !PurchasesSuspended;
    }

    /// <summary>
    /// Admin-driven discounts / purchase kill-switch per module. Settings live in
    /// SQL (shared by every instance); a short process-wide cache keeps the landing
    /// page and the credits page from hitting the DB on every request.
    /// </summary>
    public class PromotionService
    {
        public static readonly string[] Modules = { AccountTypes.Individual, AccountTypes.Clinic, AccountTypes.Cabinet };
        public static readonly int[] DiscountLevels = { 20, 50 };
        private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(30);

        private static readonly object CacheLock = new();
        private static Dictionary<string, PromotionState>? _cache;
        private static DateTime _cacheLoadedAt;

        private readonly AppDbContext _db;

        public PromotionService(AppDbContext db) => _db = db;

        public async Task<IReadOnlyList<PromotionState>> GetAllAsync(CancellationToken ct = default)
        {
            var map = await LoadAsync(ct);
            return Modules.Select(m => map.TryGetValue(m, out var s) ? s : new PromotionState(m, 0, false, null, null)).ToList();
        }

        public async Task<PromotionState> GetAsync(string? module, CancellationToken ct = default)
        {
            var key = AccountTypes.Normalize(module);
            var map = await LoadAsync(ct);
            return map.TryGetValue(key, out var s) ? s : new PromotionState(key, 0, false, null, null);
        }

        /// <summary>Turns a discount level on (exclusive per module) or off.</summary>
        public async Task SetDiscountAsync(string module, int percent, bool enabled, string? admin, CancellationToken ct = default)
        {
            if (!DiscountLevels.Contains(percent)) throw new ArgumentOutOfRangeException(nameof(percent));
            var row = await GetOrCreateRowAsync(module, ct);
            if (enabled) row.DiscountPercent = percent;
            else if (row.DiscountPercent == percent) row.DiscountPercent = 0;
            await StampAndSaveAsync(row, admin, ct);
        }

        public async Task SetSuspendedAsync(string module, bool suspended, string? admin, CancellationToken ct = default)
        {
            var row = await GetOrCreateRowAsync(module, ct);
            row.PurchasesSuspended = suspended;
            await StampAndSaveAsync(row, admin, ct);
        }

        public static void InvalidateCache()
        {
            lock (CacheLock) _cache = null;
        }

        // ------------------------------------------------------------------
        //  Pricing
        // ------------------------------------------------------------------

        /// <summary>
        /// Applies the module's discount to the packages the rules allow:
        /// B2C 20% → last 3 tiers, 50% → last 2; B2B → last 2; CM → its single package.
        /// Discounted price = floor(original × (100 − pct) / 100), credits unchanged.
        /// </summary>
        public static IReadOnlyList<CreditPackage> PriceList(IReadOnlyList<CreditPackage> packages, string module, int percent)
        {
            if (percent <= 0 || packages.Count == 0) return packages;
            int eligible = AccountTypes.Normalize(module) switch
            {
                AccountTypes.Individual => percent >= 50 ? 2 : 3,
                AccountTypes.Clinic => 2,
                _ => packages.Count
            };
            int firstEligible = Math.Max(0, packages.Count - eligible);
            return packages.Select((p, i) => i >= firstEligible ? Discount(p, percent) : p).ToList();
        }

        public static CreditPackage Discount(CreditPackage p, int percent) =>
            p with
            {
                PriceEur = Math.Floor(p.PriceEur * (100 - percent) / 100m),
                OriginalPriceEur = p.PriceEur,
                DiscountPercent = percent
            };

        /// <summary>Packages the audience may see right now, discounted where applicable.</summary>
        public async Task<IReadOnlyList<CreditPackage>> PackagesForAsync(string? module, CancellationToken ct = default)
        {
            var key = AccountTypes.Normalize(module);
            var state = await GetAsync(key, ct);
            var list = CreditPackages.ForAudience(key).ToList();
            return state.HasDiscount ? PriceList(list, key, state.DiscountPercent) : list;
        }

        /// <summary>The package as it must be charged right now (discount applied), or null if unknown.</summary>
        public async Task<CreditPackage?> ResolveAsync(string? key, CancellationToken ct = default)
        {
            var pkg = CreditPackages.GetByKey(key ?? "");
            if (pkg == null) return null;
            var list = await PackagesForAsync(pkg.Audience, ct);
            return list.FirstOrDefault(p => p.Key.Equals(pkg.Key, StringComparison.OrdinalIgnoreCase)) ?? pkg;
        }

        // ------------------------------------------------------------------

        private async Task<Dictionary<string, PromotionState>> LoadAsync(CancellationToken ct)
        {
            lock (CacheLock)
            {
                if (_cache != null && DateTime.UtcNow - _cacheLoadedAt < CacheTtl)
                    return _cache;
            }
            var rows = await _db.PromotionSettings.AsNoTracking().ToListAsync(ct);
            var map = rows.ToDictionary(
                r => AccountTypes.Normalize(r.Module),
                r => new PromotionState(AccountTypes.Normalize(r.Module), r.DiscountPercent, r.PurchasesSuspended, r.UpdatedAt, r.UpdatedBy));
            lock (CacheLock)
            {
                _cache = map;
                _cacheLoadedAt = DateTime.UtcNow;
            }
            return map;
        }

        private async Task<PromotionSetting> GetOrCreateRowAsync(string module, CancellationToken ct)
        {
            var key = AccountTypes.Normalize(module);
            if (!Modules.Contains(key)) throw new ArgumentOutOfRangeException(nameof(module));
            var row = await _db.PromotionSettings.FirstOrDefaultAsync(p => p.Module == key, ct);
            if (row == null)
            {
                row = new PromotionSetting { Module = key };
                _db.PromotionSettings.Add(row);
            }
            return row;
        }

        private async Task StampAndSaveAsync(PromotionSetting row, string? admin, CancellationToken ct)
        {
            row.UpdatedAt = DateTime.UtcNow;
            row.UpdatedBy = admin;
            await _db.SaveChangesAsync(ct);
            InvalidateCache();
        }
    }
}
