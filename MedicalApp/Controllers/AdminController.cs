using MedicalApp.Attributes;
using MedicalApp.Data;
using MedicalApp.Models;
using MedicalApp.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MedicalApp.Controllers
{
    [AdminAuthorize]
    public class AdminController : Controller
    {
        private readonly AppDbContext _db;
        private readonly IEmailService _emailService;
        private readonly DailySummaryService _dailySummaryService;
        private readonly GeminiPricing _pricing;
        private readonly LoincMatcherSettings _loincSettings;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger<AdminController> _logger;

        public AdminController(
            AppDbContext db,
            IEmailService emailService,
            DailySummaryService dailySummaryService,
            IOptions<GeminiPricing> pricing,
            IOptions<LoincMatcherSettings> loincSettings,
            IHttpClientFactory httpClientFactory,
            ILogger<AdminController> logger)
        {
            _db = db;
            _emailService = emailService;
            _dailySummaryService = dailySummaryService;
            _pricing = pricing.Value;
            _loincSettings = loincSettings.Value;
            _httpClientFactory = httpClientFactory;
            _logger = logger;
        }

        // =====================================================================
        //  LOINC microservice health probe (admin-only).
        //  Pings http://<loinc-base>/health and returns a tiny JSON the
        //  dashboard widget polls every 30s. Cheap (loopback, <5ms typical)
        //  and CANNOT slow down user-facing pages — only the Admin page calls
        //  it, and the call is async, with a tight 2s timeout so a stale
        //  socket never blocks the response. Returns:
        //    { ok: true,  status: "ok",     loincCount: 12345, latencyMs: 3 }
        //    { ok: false, status: "down",   message: "...",   latencyMs: 2003 }
        // =====================================================================
        [HttpGet]
        public async Task<IActionResult> LoincServiceHealth()
        {
            var baseUrl = (_loincSettings?.BaseUrl ?? "").TrimEnd('/');
            if (string.IsNullOrWhiteSpace(baseUrl) || _loincSettings?.Enabled != true)
            {
                return Json(new { ok = false, status = "disabled",
                    message = "LoincMatcher is disabled in appsettings.json (LoincMatcher.Enabled=false).",
                    baseUrl, latencyMs = 0 });
            }
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var client = _httpClientFactory.CreateClient();
                using var cts = new System.Threading.CancellationTokenSource(System.TimeSpan.FromSeconds(2));
                using var resp = await client.GetAsync(baseUrl + "/ready", cts.Token);
                sw.Stop();
                if (!resp.IsSuccessStatusCode)
                {
                    return Json(new { ok = false, status = "error",
                        message = $"HTTP {(int)resp.StatusCode} from {baseUrl}/ready",
                        baseUrl, latencyMs = sw.ElapsedMilliseconds });
                }
                var body = await resp.Content.ReadAsStringAsync(cts.Token);
                // Best-effort parse of "loinc_count" from /ready payload (e.g. {"status":"ready","loinc_count":12345}).
                int? loincCount = null;
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(body);
                    if (doc.RootElement.TryGetProperty("loinc_count", out var lc) && lc.ValueKind == System.Text.Json.JsonValueKind.Number)
                        loincCount = lc.GetInt32();
                }
                catch { /* not JSON or different schema — keep loincCount = null */ }
                return Json(new { ok = true, status = "ok", loincCount,
                    baseUrl, latencyMs = sw.ElapsedMilliseconds });
            }
            catch (System.OperationCanceledException)
            {
                sw.Stop();
                return Json(new { ok = false, status = "timeout",
                    message = Loc.T("AdminMicroserviceTimeout"),
                    baseUrl, latencyMs = sw.ElapsedMilliseconds });
            }
            catch (System.Exception ex)
            {
                sw.Stop();
                return Json(new { ok = false, status = "down",
                    message = ex.GetBaseException().Message,
                    baseUrl, latencyMs = sw.ElapsedMilliseconds });
            }
        }

        // =====================================================================
        //  Manual trigger for the daily summary email (runs the same job that
        //  fires automatically at the configured local hour, useful for tests
        //  and for catching up after the app was offline at the scheduled time).
        // =====================================================================
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SendDailySummaryNow()
        {
            try
            {
                await _dailySummaryService.RunNowAsync();
                TempData["AdminFlash"] = "Daily summary email triggered manually. Check the admin inbox in ~1 minute.";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Manual daily summary trigger failed.");
                TempData["AdminFlash"] = "Daily summary failed: " + ex.Message;
            }
            return RedirectToAction(nameof(Index));
        }

        // =====================================================================
        //  Translation coverage dashboard
        //  EN is treated as the master/source of truth. For every other
        //  language we compute: how many keys it has, which keys it MISSES
        //  (so they'd fall back to EN at runtime), and any "extra" keys
        //  (drift — a key in e.g. RO that doesn't exist in EN — usually a typo).
        //  Also surfaces the top-10 longest translations across all
        //  languages so the admin can stress-test layouts.
        // =====================================================================
        [HttpGet]
        public IActionResult TranslationCoverage()
        {
            var all = Loc.AllTranslations;
            // Master key set: EN.
            if (!all.TryGetValue("en", out var enDict))
            {
                // Safety: if EN is somehow missing, return an empty model
                // rather than throwing — the page will simply show 0 keys.
                return View(new TranslationCoverageViewModel());
            }
            var enKeys = new HashSet<string>(enDict.Keys);

            var vm = new TranslationCoverageViewModel
            {
                TotalEnKeys = enKeys.Count,
            };

            foreach (var lang in Loc.SupportedLanguages)
            {
                if (string.Equals(lang, "en", StringComparison.OrdinalIgnoreCase))
                    continue;
                var dict = all[lang];
                var present = new HashSet<string>(dict.Keys);
                var missing = enKeys.Except(present).OrderBy(k => k).ToList();
                var extra = present.Except(enKeys).OrderBy(k => k).ToList();
                vm.Languages.Add(new TranslationCoverageViewModel.LanguageCoverage
                {
                    Lang = lang,
                    TotalKeys = present.Count,
                    MissingKeys = missing,
                    ExtraKeys = extra,
                    CoveragePct = enKeys.Count == 0
                        ? 100.0
                        : 100.0 * (enKeys.Count - missing.Count) / enKeys.Count,
                });
            }

            // Top-10 longest translations across all (lang, key) pairs.
            vm.Longest = all
                .SelectMany(kv => kv.Value.Select(e => new TranslationCoverageViewModel.LongestTranslation
                {
                    Lang = kv.Key,
                    Key = e.Key,
                    Length = e.Value?.Length ?? 0,
                    Preview = (e.Value ?? "").Length > 120
                        ? (e.Value ?? "").Substring(0, 120) + "…"
                        : (e.Value ?? ""),
                }))
                .OrderByDescending(x => x.Length)
                .Take(10)
                .ToList();

            return View(vm);
        }

        // =====================================================================
        //  Reset AI usage counters
        //  Wipes every row from AiUsageLogs (the dashboard "AI usage (Gemini)"
        //  widget reads from this table). The user-facing InterpretationHistories
        //  table is NOT touched, so no patient/clinic history is lost — only the
        //  admin's internal cost-tracking counters are reset.
        //
        //  After this call the widget will start fresh and show only the
        //  Gemini calls made AFTER the reset.
        // =====================================================================
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ResetAiUsage()
        {
            try
            {
                int deleted = await _db.AiUsageLogs.ExecuteDeleteAsync();
                _logger.LogInformation("Admin reset AI usage counters: {Count} rows deleted from AiUsageLogs.", deleted);
                TempData["AdminFlash"] = $"AI usage counters reset — {deleted} rows cleared from AiUsageLogs.";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Reset AI usage counters failed.");
                TempData["AdminFlash"] = "Reset failed: " + ex.Message;
            }
            return RedirectToAction(nameof(Index));
        }

        // =====================================================================
        //  Dashboard  (stats overview)
        // =====================================================================
        public async Task<IActionResult> Index()
        {
            var now = DateTime.UtcNow;
            var cutoff7 = now.AddDays(-7);
            var cutoff30 = now.AddDays(-30);

            var totalUsers = await _db.Users.CountAsync();
            var payingUsers = await _db.Users.CountAsync(u => u.TotalPaid > 0);
            var activeUsers = await _db.Users.CountAsync(u => u.CreditConsum > 0);
            var blockedUsers = await _db.Users.CountAsync(u => u.IsBlocked);
            var newUsers7 = await _db.Users.CountAsync(u => u.DataC >= cutoff7);

            var totalBought = await _db.Users.SumAsync(u => (int?)u.Credite) ?? 0;
            var totalConsumed = await _db.Users.SumAsync(u => (int?)u.CreditConsum) ?? 0;
            var totalRemaining = await _db.Users.SumAsync(u => (int?)u.CreditRest) ?? 0;

            var totalBonusGranted = await _db.Users.SumAsync(u => (int?)u.BonusCredits) ?? 0;
            var totalBonusConsumed = await _db.Users.SumAsync(u => (int?)u.BonusCreditsConsumed) ?? 0;
            var totalBonusRemaining = totalBonusGranted - totalBonusConsumed;

            var totalRevenue = await _db.Purchases.SumAsync(p => (decimal?)p.AmountEur) ?? 0m;
            var revenue30 = await _db.Purchases
                .Where(p => p.PurchasedAt >= cutoff30)
                .SumAsync(p => (decimal?)p.AmountEur) ?? 0m;
            var purchases30 = await _db.Purchases.CountAsync(p => p.PurchasedAt >= cutoff30);

            var activePromos = await _db.PromoCodes.CountAsync(p =>
                p.IsActive && p.ValidFrom <= now && p.ValidUntil >= now);

            // Top 10 spenders — left-join with Clinics so the admin sees the
            // clinic name (and the user type badge) directly in the dashboard.
            // For Individuals, ClinicName stays null.
            var topSpenders = await (
                from u in _db.Users
                where u.TotalPaid > 0
                orderby u.TotalPaid descending
                join c in _db.Clinics on u.Email equals c.UserEmail into cj
                from c in cj.DefaultIfEmpty()
                select new TopSpender
                {
                    Email = u.Email,
                    TotalPaid = u.TotalPaid,
                    Credite = u.Credite,
                    CreditConsum = u.CreditConsum,
                    UserType = u.UserType,
                    ClinicName = c != null ? c.Name : null
                })
                .Take(10)
                .ToListAsync();

            var dailyRaw = await _db.Purchases
                .Where(p => p.PurchasedAt >= cutoff30)
                .GroupBy(p => p.PurchasedAt.Date)
                .Select(g => new DailyRevenue
                {
                    Date = g.Key,
                    Amount = g.Sum(x => x.AmountEur),
                    Count = g.Count()
                })
                .ToListAsync();

            // Fill missing days with 0 for a continuous chart
            var daily = Enumerable.Range(0, 30)
                .Select(i => cutoff30.Date.AddDays(i))
                .Select(d => dailyRaw.FirstOrDefault(x => x.Date == d) ?? new DailyRevenue { Date = d })
                .OrderBy(x => x.Date)
                .ToList();

            // -----------------------------------------------------------------
            // AI USAGE WIDGET — last 30 days, reads from the dedicated
            // AiUsageLogs table (populated from BOTH B2C and CAM paths).
            // Includes ALL calls that actually hit Gemini (success/error/
            // rejected), so token-consuming failures are visible too.
            // Grouped by (Source, ModelUsed) so we can render TWO side-by-side
            // doughnuts (one for B2C, one for CAM) — the dashboard now shows
            // exactly how much Gemini cost comes from individual users vs
            // clinic batches, which informs B2B pricing decisions.
            // Resettable via the "Reset AI counters" button on the dashboard.
            // -----------------------------------------------------------------
            var aiRaw = await _db.AiUsageLogs
                .AsNoTracking()
                .Where(h => h.CreatedAt >= cutoff30
                         // Exclude bookkeeping rows written by retry catch blocks
                         // (status="transient_error", 0 tokens). They have no cost
                         // and would otherwise inflate the B2C vs CAM doughnut and
                         // the per-model usage breakdown with non-billable rows.
                         // The Reliability widget below has its OWN query that
                         // explicitly INCLUDES them.
                         && h.Status != "transient_error")
                .GroupBy(h => new { h.Source, h.ModelUsed })
                .Select(g => new
                {
                    g.Key.Source,
                    g.Key.ModelUsed,
                    Count = g.Count(),
                    InputTokens = g.Sum(h => (long?)h.InputTokens) ?? 0L,
                    OutputTokens = g.Sum(h => (long?)h.OutputTokens) ?? 0L,
                })
                .ToListAsync();

            // Local helper: convert raw grouped rows into UI-ready ModelUsageRow
            // entries (with pretty short names + bootstrap badge colors + cost
            // resolved via GeminiPricing). Returns rows sorted by Count desc.
            List<ModelUsageRow> BuildRows(IEnumerable<dynamic> raw) => raw
                .Select(r =>
                {
                    string modelId = (string)(r.ModelUsed ?? "(unknown)");
                    var price = _pricing.Resolve(modelId);
                    var cost = price.ComputeCost((int)(long)r.InputTokens, (int)(long)r.OutputTokens);
                    var isPro = modelId.Contains("pro", StringComparison.OrdinalIgnoreCase);
                    var isFlash = modelId.Contains("flash", StringComparison.OrdinalIgnoreCase);
                    return new ModelUsageRow
                    {
                        ModelId = modelId,
                        ShortName = isPro ? "Pro" : isFlash ? "Flash" : modelId,
                        Count = (int)r.Count,
                        InputTokens = (long)r.InputTokens,
                        OutputTokens = (long)r.OutputTokens,
                        EstimatedCostUsd = cost,
                        BadgeClass = isPro ? "bg-warning text-dark"
                                    : isFlash ? "bg-success"
                                    : "bg-secondary",
                    };
                })
                .OrderByDescending(r => r.Count)
                .ToList();

            // Split the raw rows by Source and build per-Source breakdowns.
            var b2cRaw = aiRaw.Where(r => string.Equals(r.Source, "B2C", StringComparison.OrdinalIgnoreCase));
            var camRaw = aiRaw.Where(r => string.Equals(r.Source, "CAM", StringComparison.OrdinalIgnoreCase));

            var b2cRows = BuildRows(b2cRaw);
            var camRows = BuildRows(camRaw);

            var b2c = new AiUsageBreakdown
            {
                Rows = b2cRows,
                TotalCalls = b2cRows.Sum(r => r.Count),
                TotalCostUsd = b2cRows.Sum(r => r.EstimatedCostUsd),
            };
            var cam = new AiUsageBreakdown
            {
                Rows = camRows,
                TotalCalls = camRows.Sum(r => r.Count),
                TotalCostUsd = camRows.Sum(r => r.EstimatedCostUsd),
            };

            // Per-breakdown fallback (Pro share within that Source) — useful
            // to detect whether Flash congestion hits one path more than the
            // other (e.g. clinics tend to hammer it harder than individuals).
            b2c.FallbackRatioPct = b2c.TotalCalls > 0
                ? 100.0 * b2cRows.Where(r => r.ShortName == "Pro").Sum(r => r.Count) / b2c.TotalCalls
                : 0.0;
            cam.FallbackRatioPct = cam.TotalCalls > 0
                ? 100.0 * camRows.Where(r => r.ShortName == "Pro").Sum(r => r.Count) / cam.TotalCalls
                : 0.0;

            // Combined header totals (used in the card-header summary line).
            var totalCost = b2c.TotalCostUsd + cam.TotalCostUsd;
            var totalCalls = b2c.TotalCalls + cam.TotalCalls;
            var proCalls = b2cRows.Where(r => r.ShortName == "Pro").Sum(r => r.Count)
                         + camRows.Where(r => r.ShortName == "Pro").Sum(r => r.Count);
            var fallbackPct = totalCalls > 0 ? (100.0 * proCalls / totalCalls) : 0.0;

            b2c.ShareOfCombinedPct = totalCost > 0 ? (double)(b2c.TotalCostUsd / totalCost) * 100.0 : 0.0;
            cam.ShareOfCombinedPct = totalCost > 0 ? (double)(cam.TotalCostUsd / totalCost) * 100.0 : 0.0;

            // -----------------------------------------------------------------
            // RELIABILITY — per-model success/error breakdown over last 30 days.
            // We query AiUsageLogs grouped by (ModelUsed, Status) so we can show
            // the admin which model is degrading. High error rate on Flash is the
            // typical signal that Google congestion is hitting us; the admin can
            // then temporarily flip Model = "gemini-2.5-pro" in appsettings.json.
            // -----------------------------------------------------------------
            var reliabilityRaw = await _db.AiUsageLogs
                .AsNoTracking()
                .Where(h => h.CreatedAt >= cutoff30)
                .GroupBy(h => new { h.ModelUsed, h.Status })
                .Select(g => new { g.Key.ModelUsed, g.Key.Status, Count = g.Count() })
                .ToListAsync();

            var errorRates = reliabilityRaw
                .GroupBy(r => r.ModelUsed)
                .Select(g =>
                {
                    string modelId = g.Key ?? "(unknown)";
                    int success   = g.Where(x => x.Status == "success").Sum(x => x.Count);
                    int errors    = g.Where(x => x.Status == "error").Sum(x => x.Count);
                    int transient = g.Where(x => x.Status == "transient_error").Sum(x => x.Count);
                    int rejected  = g.Where(x => x.Status == "rejected").Sum(x => x.Count);
                    int total     = success + errors + transient + rejected;
                    // Both hard errors AND transient retries count toward the rate —
                    // a model that needs 4 retries to succeed is "unreliable" even if
                    // the user eventually got a result. Rejected rows are intentional
                    // refusals (non-medical PDF) so we exclude them from the rate.
                    double rate  = total > rejected ? 100.0 * (errors + transient) / total : 0.0;
                    string color = rate < 5  ? "success"
                                 : rate < 15 ? "warning"
                                 :             "danger";
                    var isPro   = modelId.Contains("pro",   StringComparison.OrdinalIgnoreCase);
                    var isFlash = modelId.Contains("flash", StringComparison.OrdinalIgnoreCase);
                    return new ModelErrorRateRow
                    {
                        ModelId = modelId,
                        ShortName = isPro ? "Pro" : isFlash ? "Flash" : modelId,
                        Total = total,
                        Success = success,
                        Errors = errors,
                        Transient = transient,
                        Rejected = rejected,
                        ErrorRatePct = rate,
                        BadgeColor = color
                    };
                })
                .OrderByDescending(r => r.Total)
                .ToList();

            // Last 5 distinct error messages — fast diagnosis without DB access.
            // Includes both final errors and transient retry failures, because the
            // transient ones are exactly what the admin needs to see when Flash
            // starts hiccupping on 503s.
            var recentErrors = await _db.AiUsageLogs
                .AsNoTracking()
                .Where(h => h.CreatedAt >= cutoff30
                         && (h.Status == "error" || h.Status == "transient_error")
                         && h.ErrorMessage != null)
                .OrderByDescending(h => h.CreatedAt)
                .Take(5)
                .Select(h => new RecentErrorRow
                {
                    CreatedAtUtc = h.CreatedAt,
                    Source = h.Source,
                    ModelId = h.ModelUsed,
                    Message = h.ErrorMessage!
                })
                .ToListAsync();

            var vm = new AdminDashboardViewModel
            {
                TotalUsers = totalUsers,
                PayingUsers = payingUsers,
                ActiveUsers = activeUsers,
                BlockedUsers = blockedUsers,
                NewUsersLast7Days = newUsers7,
                TotalCreditsPurchased = totalBought,
                TotalCreditsConsumed = totalConsumed,
                TotalCreditsRemaining = totalRemaining,
                TotalBonusGranted = totalBonusGranted,
                TotalBonusConsumed = totalBonusConsumed,
                TotalBonusRemaining = totalBonusRemaining,
                TotalRevenueEur = totalRevenue,
                RevenueLast30DaysEur = revenue30,
                PurchasesLast30Days = purchases30,
                ActivePromoCodes = activePromos,
                TopSpenders = topSpenders,
                RevenueChart = daily,
                AiUsageB2C = b2c,
                AiUsageCam = cam,
                AiCost30DaysUsd = totalCost,
                AiFallbackRatioPct = fallbackPct,
                ErrorRates = errorRates,
                RecentErrors = recentErrors,
            };

            return View(vm);
        }

        // =====================================================================
        //  Users list + search
        // =====================================================================
        [HttpGet]
        public async Task<IActionResult> Users(string? q = null, string? type = null, int page = 1)
        {
            const int pageSize = 25;
            page = Math.Max(1, page);

            // Normalize the type filter so the view can reuse it for the
            // active-button highlight without re-parsing.
            var typeFilter = (type ?? "all").Trim().ToLowerInvariant();
            if (typeFilter != "individual" && typeFilter != "clinic") typeFilter = "all";

            IQueryable<User> query = _db.Users.OrderByDescending(u => u.DataC);
            if (!string.IsNullOrWhiteSpace(q))
            {
                var needle = q.Trim().ToLower();
                query = query.Where(u => u.Email.ToLower().Contains(needle));
            }
            if (typeFilter == "individual")
            {
                query = query.Where(u => u.UserType == "Individual" || u.UserType == null);
            }
            else if (typeFilter == "clinic")
            {
                query = query.Where(u => u.UserType == "Clinic");
            }

            var total = await query.CountAsync();
            var users = await query
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync();

            // Enrich the paginated page with clinic name + profile counts.
            // We use batched lookups (3 small queries) to avoid N+1 on the
            // 25-row page — the admin list is bounded so this stays cheap.
            var emails = users.Select(u => u.Email).ToList();

            // Clinics indexed by UserEmail (lowercase) — UserEmail has a
            // unique index, so at most one row per user.
            var clinicsByEmail = await _db.Clinics
                .Where(c => emails.Contains(c.UserEmail))
                .Select(c => new { c.UserEmail, c.Id, c.Name })
                .ToDictionaryAsync(c => c.UserEmail, c => new { c.Id, c.Name });

            // Family profiles per email (for Individual users).
            var profileCountsByEmail = await _db.Profiles
                .Where(p => emails.Contains(p.UserEmail))
                .GroupBy(p => p.UserEmail)
                .Select(g => new { Email = g.Key, Count = g.Count() })
                .ToDictionaryAsync(g => g.Email, g => g.Count);

            // Clinic patients per ClinicId (for Clinic users).
            var clinicIds = clinicsByEmail.Values.Select(c => c.Id).ToList();
            var patientCountsByClinicId = await _db.ClinicPatients
                .Where(p => clinicIds.Contains(p.ClinicId))
                .GroupBy(p => p.ClinicId)
                .Select(g => new { ClinicId = g.Key, Count = g.Count() })
                .ToDictionaryAsync(g => g.ClinicId, g => g.Count);

            var list = users.Select(u =>
            {
                clinicsByEmail.TryGetValue(u.Email, out var clinic);
                int profilesCount;
                if (string.Equals(u.UserType, "Clinic", StringComparison.OrdinalIgnoreCase) && clinic != null)
                {
                    patientCountsByClinicId.TryGetValue(clinic.Id, out profilesCount);
                }
                else
                {
                    profileCountsByEmail.TryGetValue(u.Email, out profilesCount);
                }

                return new UserListItem
                {
                    User = u,
                    ClinicName = clinic?.Name,
                    ProfilesCount = profilesCount
                };
            }).ToList();

            ViewBag.Query = q;
            ViewBag.Type = typeFilter;
            ViewBag.Page = page;
            ViewBag.PageSize = pageSize;
            ViewBag.Total = total;
            ViewBag.TotalPages = (int)Math.Ceiling((double)total / pageSize);

            return View(list);
        }

        // =====================================================================
        //  Admin → Users → "view this user's profiles / patients"
        //
        //  Single endpoint that renders:
        //    * family Profiles  — for Individual users
        //    * ClinicPatients   — for Clinic users
        //  The view uses a unified row shape (AdminProfileRow) so the same
        //  table layout works for both flavours.
        // =====================================================================
        [HttpGet]
        public async Task<IActionResult> UserProfiles(string email)
        {
            if (string.IsNullOrWhiteSpace(email))
                return RedirectToAction(nameof(Users));

            var e = email.Trim().ToLowerInvariant();
            var user = await _db.Users.FirstOrDefaultAsync(u => u.Email == e);
            if (user == null)
            {
                TempData["ErrorMessage"] = "User not found.";
                return RedirectToAction(nameof(Users));
            }

            var vm = new AdminUserProfilesViewModel { User = user };
            var isClinic = string.Equals(user.UserType, "Clinic", StringComparison.OrdinalIgnoreCase);

            if (isClinic)
            {
                var clinic = await _db.Clinics.FirstOrDefaultAsync(c => c.UserEmail == e);
                vm.ClinicName = clinic?.Name;
                vm.ClinicId = clinic?.Id;

                if (clinic != null)
                {
                    var patients = await _db.ClinicPatients
                        .Where(p => p.ClinicId == clinic.Id)
                        .OrderByDescending(p => p.CreatedAt)
                        .ToListAsync();

                    vm.Rows = patients.Select(p => new AdminProfileRow
                    {
                        Name = p.Name,
                        Subtitle = p.Email,
                        CreatedAt = p.CreatedAt,
                        IsDefault = false
                    }).ToList();
                }
            }
            else
            {
                var profiles = await _db.Profiles
                    .Where(p => p.UserEmail == e)
                    .OrderByDescending(p => p.IsDefault)
                    .ThenBy(p => p.CreatedAt)
                    .ToListAsync();

                vm.Rows = profiles.Select(p => new AdminProfileRow
                {
                    Name = p.Name,
                    Subtitle = string.IsNullOrWhiteSpace(p.Relationship) ? p.Gender : p.Relationship,
                    CreatedAt = p.CreatedAt,
                    IsDefault = p.IsDefault
                }).ToList();
            }

            return View(vm);
        }

        // =====================================================================
        //  User detail
        // =====================================================================
        [HttpGet]
        public async Task<IActionResult> UserDetail(string email)
        {
            if (string.IsNullOrWhiteSpace(email))
                return RedirectToAction(nameof(Users));

            var e = email.Trim().ToLowerInvariant();
            var user = await _db.Users.FirstOrDefaultAsync(u => u.Email == e);
            if (user == null)
            {
                TempData["ErrorMessage"] = "User not found.";
                return RedirectToAction(nameof(Users));
            }

            var purchases = await _db.Purchases
                .Where(p => p.UserEmail == e)
                .OrderByDescending(p => p.PurchasedAt)
                .ToListAsync();

            var history = await _db.InterpretationHistories
                .Where(h => h.UserEmail == e)
                .OrderByDescending(h => h.CreatedAt)
                .Take(50)
                .ToListAsync();

            // For Clinic accounts, the real "history" lives in the CAM tables:
            // ClinicBatchRun (one row per "Lansează Lot" run) and ClinicPatients
            // (cumulative patient roster). Fetch them so the admin sees a
            // complete picture instead of an empty "Interpretation history" box.
            List<ClinicBatchRun> batchRuns = new();
            int clinicPatientsCount = 0;
            string? clinicName = null;
            if (string.Equals(user.UserType, "Clinic", StringComparison.OrdinalIgnoreCase))
            {
                var clinic = await _db.Clinics.FirstOrDefaultAsync(c => c.UserEmail == e);
                if (clinic != null)
                {
                    clinicName = clinic.Name;
                    clinicPatientsCount = await _db.ClinicPatients.CountAsync(p => p.ClinicId == clinic.Id);
                    batchRuns = await _db.ClinicBatchRuns
                        .Where(b => b.ClinicId == clinic.Id)
                        .OrderByDescending(b => b.StartedAt)
                        .Take(50)
                        .ToListAsync();
                }
            }

            ViewBag.Purchases = purchases;
            ViewBag.History = history;
            ViewBag.BatchRuns = batchRuns;
            ViewBag.ClinicPatientsCount = clinicPatientsCount;
            ViewBag.ClinicName = clinicName;
            return View(user);
        }

        // =====================================================================
        //  Give free credits (manual)
        // =====================================================================
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> GiveCredits(string email, int credits, string? reason)
        {
            if (credits <= 0 || credits > 1000)
            {
                TempData["ErrorMessage"] = "Credits must be between 1 and 1000.";
                return RedirectToAction(nameof(UserDetail), new { email });
            }

            var e = (email ?? string.Empty).Trim().ToLowerInvariant();
            var user = await _db.Users.FirstOrDefaultAsync(u => u.Email == e);
            if (user == null)
            {
                TempData["ErrorMessage"] = "User not found.";
                return RedirectToAction(nameof(Users));
            }

            user.BonusCredits += credits;
            await _db.SaveChangesAsync();

            _logger.LogInformation("Admin gave {Credits} bonus credits to {Email}. Reason: {Reason}",
                credits, e, reason ?? "(none)");
            TempData["SuccessMessage"] = $"Added {credits} BONUS credits to {e}.";
            return RedirectToAction(nameof(UserDetail), new { email = e });
        }

        // =====================================================================
        //  Reset password for a user (sends new random password by email)
        // =====================================================================
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ResetUserPassword(string email)
        {
            var e = (email ?? string.Empty).Trim().ToLowerInvariant();
            var user = await _db.Users.FirstOrDefaultAsync(u => u.Email == e);
            if (user == null)
            {
                TempData["ErrorMessage"] = "User not found.";
                return RedirectToAction(nameof(Users));
            }

            var newPassword = PasswordGenerator.GenerateStrong(12);
            user.Parola = BCrypt.Net.BCrypt.HashPassword(newPassword);
            user.PasswordResetToken = null;
            user.PasswordResetTokenExpiry = null;
            await _db.SaveChangesAsync();

            try
            {
                var subject = "MyMedicalApp.NET - Your password has been reset by an administrator";
                var body = $@"<div style='font-family:Arial;max-width:600px;margin:0 auto;padding:20px;'>
<h2 style='color:#0d47a1;'>MyMedicalApp.NET</h2>
<p>Hello,</p>
<p>Your password has been reset by an administrator.</p>
<p>Your new temporary password is:</p>
<div style='background:#f8f9fa;border:2px solid #0d47a1;border-radius:10px;padding:20px;text-align:center;margin:24px 0;'>
  <span style='font-family:monospace;font-size:24px;font-weight:bold;color:#0d47a1;letter-spacing:2px;'>
    {System.Net.WebUtility.HtmlEncode(newPassword)}
  </span>
</div>
<p>Please log in with this password and change it immediately from your account settings.</p>
<hr/><p style='color:#6c757d;font-size:0.9em;'>MyMedicalApp.NET - your medical analysis interpreter.</p>
</div>";
                await _emailService.SendEmailAsync(e, subject, body);
                TempData["SuccessMessage"] = $"New password emailed to {e}.";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to email new password to {Email}", e);
                TempData["ErrorMessage"] = $"Password was reset but email failed: {ex.Message}";
            }

            return RedirectToAction(nameof(UserDetail), new { email = e });
        }

        // =====================================================================
        //  Delete user (and all related data)
        // =====================================================================
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteUser(string email)
        {
            var e = (email ?? string.Empty).Trim().ToLowerInvariant();
            var user = await _db.Users.FirstOrDefaultAsync(u => u.Email == e);
            if (user == null)
            {
                TempData["ErrorMessage"] = "User not found.";
                return RedirectToAction(nameof(Users));
            }

            // Safety: do not allow deleting an admin account
            if (user.IsAdmin)
            {
                TempData["ErrorMessage"] = "Cannot delete an admin account.";
                return RedirectToAction(nameof(UserDetail), new { email = e });
            }

            // ---- Cascade-delete ALL related rows so no orphan footprint
            //      remains in the database after the user is gone.
            //
            // Common to both audiences:
            _db.Purchases.RemoveRange(_db.Purchases.Where(p => p.UserEmail == e));
            _db.InterpretationHistories.RemoveRange(_db.InterpretationHistories.Where(h => h.UserEmail == e));

            // Individual: family profiles in dbo.Profiles
            _db.Profiles.RemoveRange(_db.Profiles.Where(p => p.UserEmail == e));

            // Clinic: the clinic row + every CAM artefact attached to its ClinicId.
            // We resolve ClinicId once and then remove from every CAM table.
            var clinic = await _db.Clinics.FirstOrDefaultAsync(c => c.UserEmail == e);
            if (clinic != null)
            {
                var cid = clinic.Id;
                _db.ClinicBatchErrors.RemoveRange(
                    _db.ClinicBatchErrors.Where(x => _db.ClinicBatchRuns
                        .Where(r => r.ClinicId == cid).Select(r => r.Id).Contains(x.BatchRunId)));
                _db.ClinicBatchRuns.RemoveRange(_db.ClinicBatchRuns.Where(b => b.ClinicId == cid));
                _db.ClinicAnalyses.RemoveRange(_db.ClinicAnalyses.Where(a => a.ClinicId == cid));
                _db.ClinicPatients.RemoveRange(_db.ClinicPatients.Where(p => p.ClinicId == cid));
                _db.ClinicPdfOverrides.RemoveRange(_db.ClinicPdfOverrides.Where(o => o.ClinicId == cid));
                _db.Clinics.Remove(clinic);
            }

            _db.Users.Remove(user);
            await _db.SaveChangesAsync();

            _logger.LogWarning("Admin deleted user {Email} (and all related purchases/history/profiles/CAM data).", e);
            TempData["SuccessMessage"] = $"User {e} and all related data were deleted.";
            return RedirectToAction(nameof(Users));
        }

        // =====================================================================
        //  Block / Unblock user
        // =====================================================================
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ToggleBlock(string email)
        {
            var e = (email ?? string.Empty).Trim().ToLowerInvariant();
            var user = await _db.Users.FirstOrDefaultAsync(u => u.Email == e);
            if (user == null)
            {
                TempData["ErrorMessage"] = "User not found.";
                return RedirectToAction(nameof(Users));
            }

            // Don't allow blocking an admin (safety)
            if (user.IsAdmin && !user.IsBlocked)
            {
                TempData["ErrorMessage"] = "Cannot block an admin account.";
                return RedirectToAction(nameof(UserDetail), new { email = e });
            }

            user.IsBlocked = !user.IsBlocked;
            await _db.SaveChangesAsync();

            TempData["SuccessMessage"] = user.IsBlocked
                ? $"{e} is now blocked."
                : $"{e} is unblocked.";
            return RedirectToAction(nameof(UserDetail), new { email = e });
        }

        // =====================================================================
        //  Bulk email  (with filter)
        // =====================================================================
        [HttpGet]
        public async Task<IActionResult> SendEmail(string filter = "all")
        {
            var recipients = await ResolveRecipients(filter);
            return View(new BulkEmailViewModel
            {
                Filter = filter,
                RecipientsCount = recipients.Count
            });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SendEmail(BulkEmailViewModel model)
        {
            if (!ModelState.IsValid)
            {
                model.RecipientsCount = (await ResolveRecipients(model.Filter)).Count;
                return View(model);
            }

            var recipients = await ResolveRecipients(model.Filter);
            model.RecipientsCount = recipients.Count;

            if (recipients.Count == 0)
            {
                TempData["ErrorMessage"] = "No recipients match the selected filter.";
                return View(model);
            }

            int sent = 0, failed = 0;
            foreach (var email in recipients)
            {
                try
                {
                    var wrappedBody = WrapBulkEmailHtml(model.HtmlBody);
                    await _emailService.SendEmailAsync(email, model.Subject, wrappedBody);
                    sent++;
                }
                catch (Exception ex)
                {
                    failed++;
                    _logger.LogError(ex, "Bulk email failed for {Email}", email);
                }
            }

            TempData["SuccessMessage"] = $"Email sent to {sent} users (failed: {failed}).";
            return RedirectToAction(nameof(SendEmail), new { filter = model.Filter });
        }

        /// <summary>
        /// Wraps the admin-typed HTML body with a branded header and footer
        /// so every bulk email looks consistent.
        /// </summary>
        private static string WrapBulkEmailHtml(string innerHtml) => $@"
<div style=""font-family:Arial,Helvetica,sans-serif;max-width:640px;margin:0 auto;padding:0;background:#ffffff;"">
  <div style=""background:#0d47a1;color:#ffffff;padding:20px 24px;border-radius:10px 10px 0 0;"">
    <h2 style=""margin:0;font-size:20px;font-weight:700;letter-spacing:0.3px;"">MyMedicalApp.NET</h2>
    <div style=""font-size:13px;opacity:0.9;margin-top:4px;"">Intelligent interpretation of medical analyses</div>
  </div>
  <div style=""padding:24px;color:#212529;font-size:15px;line-height:1.55;border:1px solid #e9ecef;border-top:0;"">
    {innerHtml}
  </div>
  <div style=""background:#f1f5fb;color:#0d47a1;padding:16px 24px;border-radius:0 0 10px 10px;text-align:center;font-size:13px;font-weight:600;border:1px solid #e9ecef;border-top:0;"">
    Be smart, take care of your health!
  </div>
</div>";

        private async Task<List<string>> ResolveRecipients(string filter)
        {
            var now = DateTime.UtcNow;
            IQueryable<User> q = _db.Users.Where(u => !u.IsBlocked);

            q = filter switch
            {
                "paying" => q.Where(u => u.TotalPaid > 0),
                "with_credits" => q.Where(u => u.CreditRest > 0),
                "registered_last_30_days" => q.Where(u => u.DataC >= now.AddDays(-30)),
                "blocked" => _db.Users.Where(u => u.IsBlocked),
                _ => q
            };

            return await q.Select(u => u.Email).ToListAsync();
        }

        // =====================================================================
        //  Promo codes
        // =====================================================================
        [HttpGet]
        public async Task<IActionResult> PromoCodes()
        {
            var list = await _db.PromoCodes
                .OrderByDescending(p => p.CreatedAt)
                .ToListAsync();
            return View(list);
        }

        [HttpGet]
        public IActionResult NewPromoCode()
            => View("PromoCodeForm", new PromoCodeViewModel
            {
                ValidFrom = DateTime.UtcNow,
                ValidUntil = DateTime.UtcNow.AddMonths(1)
            });

        [HttpGet]
        public async Task<IActionResult> EditPromoCode(int id)
        {
            var p = await _db.PromoCodes.FindAsync(id);
            if (p == null) return RedirectToAction(nameof(PromoCodes));

            return View("PromoCodeForm", new PromoCodeViewModel
            {
                Id = p.Id,
                Code = p.Code,
                CreditsToAdd = p.CreditsToAdd,
                ValidFrom = p.ValidFrom,
                ValidUntil = p.ValidUntil,
                MaxUses = p.MaxUses,
                IsActive = p.IsActive
            });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SavePromoCode(PromoCodeViewModel model)
        {
            if (!ModelState.IsValid) return View("PromoCodeForm", model);

            if (model.ValidUntil <= model.ValidFrom)
            {
                ModelState.AddModelError(nameof(model.ValidUntil), "End date must be after start date.");
                return View("PromoCodeForm", model);
            }

            var codeNorm = model.Code.Trim();

            if (model.Id == 0)
            {
                // create
                var exists = await _db.PromoCodes.AnyAsync(p => p.Code == codeNorm);
                if (exists)
                {
                    ModelState.AddModelError(nameof(model.Code), "A promo code with this name already exists.");
                    return View("PromoCodeForm", model);
                }
                _db.PromoCodes.Add(new PromoCode
                {
                    Code = codeNorm,
                    CreditsToAdd = model.CreditsToAdd,
                    ValidFrom = model.ValidFrom,
                    ValidUntil = model.ValidUntil,
                    MaxUses = model.MaxUses,
                    IsActive = model.IsActive,
                    CreatedAt = DateTime.UtcNow
                });
                TempData["SuccessMessage"] = $"Promo code {codeNorm} created.";
            }
            else
            {
                var p = await _db.PromoCodes.FindAsync(model.Id);
                if (p == null) return RedirectToAction(nameof(PromoCodes));

                p.Code = codeNorm;
                p.CreditsToAdd = model.CreditsToAdd;
                p.ValidFrom = model.ValidFrom;
                p.ValidUntil = model.ValidUntil;
                p.MaxUses = model.MaxUses;
                p.IsActive = model.IsActive;
                TempData["SuccessMessage"] = $"Promo code {codeNorm} updated.";
            }

            await _db.SaveChangesAsync();
            return RedirectToAction(nameof(PromoCodes));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeletePromoCode(int id)
        {
            var p = await _db.PromoCodes.FindAsync(id);
            if (p == null) return RedirectToAction(nameof(PromoCodes));

            _db.PromoCodes.Remove(p);
            await _db.SaveChangesAsync();
            TempData["SuccessMessage"] = $"Promo code {p.Code} deleted.";
            return RedirectToAction(nameof(PromoCodes));
        }
    }
}
