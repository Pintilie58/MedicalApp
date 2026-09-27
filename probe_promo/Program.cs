using MedicalApp.Data;
using MedicalApp.Models;
using MedicalApp.Services;
using Microsoft.EntityFrameworkCore;

int fails = 0;
void Check(string what, bool ok, string? d = null) { if (!ok) fails++; Console.WriteLine((ok ? "PASS  " : "FAIL  ") + what + (d != null ? "  ->  " + d : "")); }
string P(IReadOnlyList<CreditPackage> l) => string.Join(" ", l.Select(p => p.IsDiscounted ? $"{p.Key}={p.PriceEur}({p.OriginalPriceEur}-{p.DiscountPercent}%)" : $"{p.Key}={p.PriceEur}"));

// ---- pricing rules (pure) ----
var b2c = CreditPackages.ForAudience("Individual").ToList();
var l20 = PromotionService.PriceList(b2c, "Individual", 20);
Check("B2C 20%: last 3 discounted, floor", l20[0].PriceEur == 6 && !l20[0].IsDiscounted && l20[1].PriceEur == 8 && l20[2].PriceEur == 31 && l20[3].PriceEur == 71 && l20[3].SavingEur == 18, P(l20));
var l50 = PromotionService.PriceList(b2c, "Individual", 50);
Check("B2C 50%: last 2 discounted", !l50[1].IsDiscounted && l50[2].PriceEur == 19 && l50[3].PriceEur == 44 && l50[3].Credits == 45, P(l50));
var b2b = CreditPackages.ForAudience("Clinic").ToList();
Check("B2B new prices 49/499/999, credits unchanged", b2b[0].PriceEur == 49 && b2b[1].PriceEur == 499 && b2b[2].PriceEur == 999 && b2b[2].Credits == 390, P(b2b));
var c20 = PromotionService.PriceList(b2b, "Clinic", 20);
Check("B2B 20%: only last 2 (399 / 799)", !c20[0].IsDiscounted && c20[1].PriceEur == 399 && c20[2].PriceEur == 799, P(c20));
var c50 = PromotionService.PriceList(b2b, "Clinic", 50);
Check("B2B 50%: 249 / 499", c50[1].PriceEur == 249 && c50[2].PriceEur == 499, P(c50));
var cm = CreditPackages.ForAudience("Cabinet").ToList();
var m50 = PromotionService.PriceList(cm, "Cabinet", 50);
Check("CM 50%: single package 89 -> 44", m50.Count == 1 && m50[0].PriceEur == 44 && m50[0].SavingEur == 45, P(m50));
Check("0% leaves list untouched", ReferenceEquals(PromotionService.PriceList(b2c, "Individual", 0), b2c));

// ---- DB-backed state ----
var opts = new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase("promo").Options;
await using var db = new AppDbContext(opts);
var svc = new PromotionService(db);
var all = await svc.GetAllAsync();
Check("defaults: 3 modules, nothing active", all.Count == 3 && all.All(s => !s.HasDiscount && !s.PurchasesSuspended));

await svc.SetDiscountAsync("Individual", 20, true, "admin@x");
PromotionService.InvalidateCache();
var st = await svc.GetAsync("Individual");
Check("20% ON for B2C", st.DiscountPercent == 20 && st.HasDiscount && st.UpdatedBy == "admin@x");
await svc.SetDiscountAsync("Individual", 50, true, "admin@x");
st = await svc.GetAsync("Individual");
Check("50% ON replaces 20% (exclusive)", st.DiscountPercent == 50);
await svc.SetDiscountAsync("Individual", 20, false, "admin@x");
st = await svc.GetAsync("Individual");
Check("turning 20% OFF while 50% is ON does nothing", st.DiscountPercent == 50);
await svc.SetDiscountAsync("Individual", 50, false, "admin@x");
st = await svc.GetAsync("Individual");
Check("50% OFF -> no discount", st.DiscountPercent == 0 && !st.HasDiscount);

await svc.SetDiscountAsync("Cabinet", 20, true, "admin@x");
await svc.SetSuspendedAsync("Cabinet", true, "admin@x");
st = await svc.GetAsync("Cabinet");
Check("Stop overrides discount (HasDiscount false, percent kept)", st.PurchasesSuspended && !st.HasDiscount && st.DiscountPercent == 20);
var cmPk = await svc.PackagesForAsync("Cabinet");
Check("suspended module -> packages at full price", cmPk.All(p => !p.IsDiscounted));
await svc.SetSuspendedAsync("Cabinet", false, "admin@x");
cmPk = await svc.PackagesForAsync("Cabinet");
Check("un-suspend -> 20% discount returns (89 -> 71)", cmPk[0].PriceEur == 71 && cmPk[0].IsDiscounted);

await svc.SetDiscountAsync("Clinic", 50, true, "admin@x");
var resolved = await svc.ResolveAsync("cam_enterprise");
Check("ResolveAsync returns charged price 499 (from 999)", resolved != null && resolved.PriceEur == 499 && resolved.OriginalPriceEur == 999);
var starter = await svc.ResolveAsync("cam_starter");
Check("ResolveAsync non-eligible package stays full price", starter != null && starter.PriceEur == 49 && !starter.IsDiscounted);
Check("ResolveAsync unknown key -> null", await svc.ResolveAsync("nope") == null);
Check("unique row per module", await db.PromotionSettings.CountAsync() == 3);

try { await svc.SetDiscountAsync("Individual", 30, true, "x"); Check("invalid level rejected", false); }
catch (ArgumentOutOfRangeException) { Check("invalid level rejected", true); }
Check("unknown module normalizes to Individual (AccountTypes.Normalize)", AccountTypes.Normalize("Alien") == AccountTypes.Individual);

Console.WriteLine(fails == 0 ? "ALL PASS" : $"{fails} FAILURE(S)");
return fails == 0 ? 0 : 1;
