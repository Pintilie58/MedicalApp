using MedicalApp.Data;
using MedicalApp.Models;
using MedicalApp.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace MedicalApp.Controllers
{
    /// <summary>
    /// Manages the current user's health profiles (Eu, Mama, Tata, etc.).
    /// All actions require an authenticated user (session "UserEmail" set).
    /// </summary>
    public class ProfilesController : Controller
    {
        private readonly AppDbContext _db;
        private readonly PdfReportGenerator _pdfGenerator;
        private readonly ArchiveAccessService _archiveAccess;
        private readonly ILogger<ProfilesController> _logger;

        public ProfilesController(
            AppDbContext db,
            PdfReportGenerator pdfGenerator,
            ArchiveAccessService archiveAccess,
            ILogger<ProfilesController> logger)
        {
            _db = db;
            _pdfGenerator = pdfGenerator;
            _archiveAccess = archiveAccess;
            _logger = logger;
        }

        private string? CurrentEmail => HttpContext.Session.GetString("UserEmail");

        // ====================================================================
        // LIST
        // ====================================================================
        [HttpGet]
        public async Task<IActionResult> Index()
        {
            if (string.IsNullOrEmpty(CurrentEmail))
                return RedirectToAction("Index", "Home");

            var profiles = await _db.Profiles
                .AsNoTracking()
                .Where(p => p.UserEmail == CurrentEmail)
                .OrderByDescending(p => p.IsDefault)
                .ThenBy(p => p.Name)
                .ToListAsync();

            // Interpretation counts per profile (successful ones only).
            var profileIds = profiles.Select(p => p.Id).ToList();
            var counts = await _db.InterpretationHistories
                .AsNoTracking()
                .Where(h => h.ProfileId.HasValue
                            && profileIds.Contains(h.ProfileId.Value)
                            && h.Status == "success")
                .GroupBy(h => h.ProfileId!.Value)
                .Select(g => new { ProfileId = g.Key, Count = g.Count() })
                .ToDictionaryAsync(x => x.ProfileId, x => x.Count);

            var vm = new ProfilesIndexViewModel
            {
                Profiles = profiles.Select(p => new ProfilesIndexViewModel.ProfileRow
                {
                    Id = p.Id,
                    Name = p.Name,
                    Relationship = p.Relationship,
                    Gender = p.Gender,
                    BirthYear = p.BirthYear,
                    Notes = p.Notes,
                    IsDefault = p.IsDefault,
                    CreatedAt = p.CreatedAt,
                    InterpretationsCount = counts.TryGetValue(p.Id, out var c) ? c : 0
                }).ToList()
            };

            return View(vm);
        }

        // ====================================================================
        // HISTORY (archive) - list interpretations for a specific profile
        // ====================================================================
        [HttpGet]
        public async Task<IActionResult> History(int id)
        {
            if (string.IsNullOrEmpty(CurrentEmail))
                return RedirectToAction("Index", "Home");

            var profile = await _db.Profiles.AsNoTracking()
                .FirstOrDefaultAsync(p => p.Id == id && p.UserEmail == CurrentEmail);
            if (profile == null)
            {
                TempData["ErrorMessage"] = "Profilul nu a fost găsit.";
                return RedirectToAction(nameof(Index));
            }

            var rows = await _db.InterpretationHistories
                .AsNoTracking()
                .Where(h => h.UserEmail == CurrentEmail
                            && h.ProfileId == profile.Id
                            && h.Status == "success")
                .OrderByDescending(h => h.CreatedAt)
                .Select(h => new
                {
                    h.Id,
                    h.CreatedAt,
                    h.OriginalFileName,
                    h.Language,
                    h.RawJsonResult
                })
                .ToListAsync();

            var items = new List<ProfileHistoryViewModel.HistoryRow>(rows.Count);
            foreach (var r in rows)
            {
                var row = new ProfileHistoryViewModel.HistoryRow
                {
                    Id = r.Id,
                    CreatedAt = r.CreatedAt,
                    OriginalFileName = r.OriginalFileName,
                    Language = r.Language,
                    HasRawJson = !string.IsNullOrWhiteSpace(r.RawJsonResult)
                };

                // Lightweight parse only to show counts in the table - never block the page if parsing fails.
                if (row.HasRawJson)
                {
                    try
                    {
                        var parsed = JsonSerializer.Deserialize<InterpretationResult>(r.RawJsonResult!,
                            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                        row.KeyResultsCount = parsed?.KeyResults?.Count;
                        row.AbnormalFindingsCount = parsed?.AbnormalFindings?.Count;
                        row.PatientName = parsed?.PatientInfo?.Name;
                        row.DateTaken = parsed?.PatientInfo?.DateTaken;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Could not parse stored RawJsonResult for history id={Id}", r.Id);
                    }
                }

                items.Add(row);
            }

            var vm = new ProfileHistoryViewModel
            {
                ProfileId = profile.Id,
                ProfileName = profile.Name,
                Relationship = profile.Relationship,
                Items = items
            };

            // Fetch the user to know their free-period state (for the UI hint only;
            // nothing is charged on this page).
            var user = await _db.Users.AsNoTracking()
                .FirstOrDefaultAsync(u => u.Email == CurrentEmail);
            if (user != null)
            {
                vm.IsInFreePeriod = ArchiveAccessService.IsInFreePeriod(user);
                vm.FreeUntil = user.FreeArchiveUntil ?? user.DataC.Add(ArchiveAccessService.FreePeriod);
                vm.FreeUsesLeftInBundle = ArchiveAccessService.FreeUsesLeftInBundle(user);
            }

            return View(vm);
        }

        // ====================================================================
        // DOWNLOAD REPORT - regenerate PDF from stored JSON on the fly
        // ====================================================================
        [HttpGet]
        public async Task<IActionResult> DownloadReport(int id)
        {
            if (string.IsNullOrEmpty(CurrentEmail))
                return RedirectToAction("Index", "Home");

            var history = await _db.InterpretationHistories
                .AsNoTracking()
                .FirstOrDefaultAsync(h => h.Id == id
                                          && h.UserEmail == CurrentEmail
                                          && h.Status == "success");
            if (history == null || string.IsNullOrWhiteSpace(history.RawJsonResult))
            {
                TempData["ErrorMessage"] = "Raportul nu a fost găsit sau nu mai are date salvate.";
                return RedirectToAction(nameof(Index));
            }

            InterpretationResult? result;
            try
            {
                result = JsonSerializer.Deserialize<InterpretationResult>(history.RawJsonResult,
                    new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true,
                        AllowTrailingCommas = true,
                        ReadCommentHandling = JsonCommentHandling.Skip
                    });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to deserialize RawJsonResult for history id={Id}", id);
                TempData["ErrorMessage"] = "Raportul nu a putut fi reconstruit din datele stocate.";
                return RedirectToAction(nameof(History), new { id = history.ProfileId ?? 0 });
            }

            if (result == null)
            {
                TempData["ErrorMessage"] = "Raportul nu a putut fi reconstruit din datele stocate.";
                return RedirectToAction(nameof(History), new { id = history.ProfileId ?? 0 });
            }

            byte[] pdfBytes;
            try
            {
                pdfBytes = _pdfGenerator.Generate(result, LocalizedLabels.ForCurrentUi());
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "PDF regeneration failed for history id={Id}", id);
                TempData["ErrorMessage"] = "Eroare la generarea PDF-ului.";
                return RedirectToAction(nameof(History), new { id = history.ProfileId ?? 0 });
            }

            var fileName = $"MedicalApp_{history.CreatedAt:yyyyMMdd_HHmmss}_report.pdf";
            return File(pdfBytes, "application/pdf", fileName);
        }

        // ====================================================================
        // DELETE one interpretation from the archive (with explicit user confirmation
        // submitted from the History page).
        // ====================================================================
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteHistory(int id, int profileId)
        {
            if (string.IsNullOrEmpty(CurrentEmail))
                return RedirectToAction("Index", "Home");

            var history = await _db.InterpretationHistories
                .FirstOrDefaultAsync(h => h.Id == id && h.UserEmail == CurrentEmail);
            if (history == null)
            {
                TempData["ErrorMessage"] = "Interpretarea nu a fost găsită.";
                return RedirectToAction(nameof(History), new { id = profileId });
            }

            _db.InterpretationHistories.Remove(history);
            await _db.SaveChangesAsync();

            _logger.LogInformation(
                "User {Email} deleted interpretation history id={Id} (profile={Pid}, file={File}).",
                CurrentEmail, id, history.ProfileId, history.OriginalFileName);

            TempData["SuccessMessage"] = "Interpretarea a fost ștearsă din arhivă.";
            return RedirectToAction(nameof(History), new { id = profileId });
        }

        // ====================================================================
        // COMPARE two interpretations side-by-side (P1.5.5, premium feature)
        // ====================================================================
        [HttpGet]
        public async Task<IActionResult> Compare(int id1, int id2, int profileId)
        {
            if (string.IsNullOrEmpty(CurrentEmail))
                return RedirectToAction("Index", "Home");

            if (id1 == id2)
            {
                TempData["ErrorMessage"] = "Alege două interpretări diferite pentru comparație.";
                return RedirectToAction(nameof(History), new { id = profileId });
            }

            var user = await _db.Users.FirstOrDefaultAsync(u => u.Email == CurrentEmail);
            if (user == null)
            {
                HttpContext.Session.Clear();
                return RedirectToAction("Index", "Home");
            }

            var profile = await _db.Profiles.AsNoTracking()
                .FirstOrDefaultAsync(p => p.Id == profileId && p.UserEmail == CurrentEmail);
            if (profile == null)
            {
                TempData["ErrorMessage"] = "Profilul nu a fost găsit.";
                return RedirectToAction(nameof(Index));
            }

            var items = await _db.InterpretationHistories
                .AsNoTracking()
                .Where(h => (h.Id == id1 || h.Id == id2)
                            && h.UserEmail == CurrentEmail
                            && h.ProfileId == profile.Id
                            && h.Status == "success"
                            && h.RawJsonResult != null)
                .ToListAsync();

            if (items.Count != 2)
            {
                TempData["ErrorMessage"] = "Una sau ambele interpretări selectate nu au fost găsite.";
                return RedirectToAction(nameof(History), new { id = profileId });
            }

            // Archive premium billing: check & consume. If refused, redirect with error.
            var check = _archiveAccess.TryConsume(user, "compare");
            if (!check.Allowed)
            {
                TempData["ErrorMessage"] =
                    "Ai rămas fără credite pentru comparație. Cumpără credite pentru a continua.";
                return RedirectToAction("Buy", "Credits");
            }
            await _db.SaveChangesAsync();

            // Deserialize both JSONs.
            var left = items.OrderBy(h => h.CreatedAt).First();
            var right = items.OrderBy(h => h.CreatedAt).Last();

            var leftResult = DeserializeSafe(left.RawJsonResult);
            var rightResult = DeserializeSafe(right.RawJsonResult);
            if (leftResult == null || rightResult == null)
            {
                TempData["ErrorMessage"] = "Comparația nu a putut fi generată din datele stocate.";
                return RedirectToAction(nameof(History), new { id = profileId });
            }

            var vm = BuildComparison(profile, left, right, leftResult, rightResult);
            vm.CreditConsumed = check.CreditConsumed;
            return View(vm);
        }

        private static InterpretationResult? DeserializeSafe(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            try
            {
                return JsonSerializer.Deserialize<InterpretationResult>(raw,
                    new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true,
                        AllowTrailingCommas = true,
                        ReadCommentHandling = JsonCommentHandling.Skip
                    });
            }
            catch
            {
                return null;
            }
        }

        private static CompareInterpretationsViewModel BuildComparison(
            Profile profile,
            InterpretationHistory leftH, InterpretationHistory rightH,
            InterpretationResult left, InterpretationResult right)
        {
            static string Key(string param) =>
                (param ?? string.Empty).Trim().ToLowerInvariant();

            var leftMap = (left.KeyResults ?? new())
                .Where(r => !string.IsNullOrWhiteSpace(r.Parameter))
                .GroupBy(r => Key(r.Parameter))
                .ToDictionary(g => g.Key, g => g.First());
            var rightMap = (right.KeyResults ?? new())
                .Where(r => !string.IsNullOrWhiteSpace(r.Parameter))
                .GroupBy(r => Key(r.Parameter))
                .ToDictionary(g => g.Key, g => g.First());

            var allKeys = leftMap.Keys.Union(rightMap.Keys).OrderBy(k => k).ToList();

            int risen = 0, fallen = 0, unchanged = 0, onlyLeft = 0, onlyRight = 0;

            var rows = new List<CompareInterpretationsViewModel.ComparisonRow>(allKeys.Count);
            foreach (var k in allKeys)
            {
                leftMap.TryGetValue(k, out var l);
                rightMap.TryGetValue(k, out var r);

                var row = new CompareInterpretationsViewModel.ComparisonRow
                {
                    Parameter = r?.Parameter ?? l?.Parameter ?? k,
                    Unit = r?.Unit ?? l?.Unit,
                    ReferenceRange = r?.ReferenceRange ?? l?.ReferenceRange,
                    LeftValue = l?.Value,
                    LeftStatus = l?.Status,
                    RightValue = r?.Value,
                    RightStatus = r?.Status
                };

                if (l == null)
                {
                    row.Direction = "only_right";
                    onlyRight++;
                }
                else if (r == null)
                {
                    row.Direction = "only_left";
                    onlyLeft++;
                }
                else
                {
                    var (lv, lok) = ParseNumeric(l.Value);
                    var (rv, rok) = ParseNumeric(r.Value);
                    if (lok && rok)
                    {
                        row.NumericDelta = rv - lv;
                        row.PercentDelta = lv != 0 ? (rv - lv) / Math.Abs(lv) * 100.0 : (double?)null;
                        if (Math.Abs(rv - lv) < 1e-9) { row.Direction = "unchanged"; unchanged++; }
                        else if (rv > lv) { row.Direction = "risen"; risen++; }
                        else { row.Direction = "fallen"; fallen++; }
                    }
                    else
                    {
                        // Non-numeric values (e.g. "negativ" / "pozitiv"): string compare.
                        var same = string.Equals(l.Value?.Trim(), r.Value?.Trim(),
                            StringComparison.OrdinalIgnoreCase);
                        if (same) { row.Direction = "unchanged"; unchanged++; }
                        else { row.Direction = "unparsable"; }
                    }
                }
                rows.Add(row);
            }

            return new CompareInterpretationsViewModel
            {
                ProfileId = profile.Id,
                ProfileName = profile.Name,
                Left = new CompareInterpretationsViewModel.Side
                {
                    HistoryId = leftH.Id,
                    CreatedAt = leftH.CreatedAt,
                    OriginalFileName = leftH.OriginalFileName,
                    DateTaken = left.PatientInfo?.DateTaken,
                    KeyResultsCount = left.KeyResults?.Count ?? 0,
                    AbnormalFindingsCount = left.AbnormalFindings?.Count ?? 0
                },
                Right = new CompareInterpretationsViewModel.Side
                {
                    HistoryId = rightH.Id,
                    CreatedAt = rightH.CreatedAt,
                    OriginalFileName = rightH.OriginalFileName,
                    DateTaken = right.PatientInfo?.DateTaken,
                    KeyResultsCount = right.KeyResults?.Count ?? 0,
                    AbnormalFindingsCount = right.AbnormalFindings?.Count ?? 0
                },
                Rows = rows,
                RisenCount = risen,
                FallenCount = fallen,
                UnchangedCount = unchanged,
                OnlyLeftCount = onlyLeft,
                OnlyRightCount = onlyRight
            };
        }

        /// <summary>
        /// Tries to extract a numeric value from labels like "4.6", "4,6", "12.3 x10^9/L",
        /// "&lt;0.5", "&gt;200". Returns (0, false) when no parse is possible.
        /// </summary>
        private static (double value, bool ok) ParseNumeric(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return (0, false);
            var s = raw.Trim().TrimStart('<', '>', '=', '~', '≤', '≥', ' ').Replace(',', '.');
            // Take the first contiguous number-ish token.
            var buf = new System.Text.StringBuilder();
            bool seenDigit = false;
            foreach (var c in s)
            {
                if (char.IsDigit(c) || c == '.' || (c == '-' && buf.Length == 0))
                {
                    buf.Append(c);
                    if (char.IsDigit(c)) seenDigit = true;
                }
                else if (seenDigit) break;
            }
            if (buf.Length == 0 || !seenDigit) return (0, false);
            return double.TryParse(buf.ToString(),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out var v)
                ? (v, true)
                : (0, false);
        }

        // ====================================================================
        // CREATE
        // ====================================================================
        [HttpGet]
        public IActionResult Create()
        {
            if (string.IsNullOrEmpty(CurrentEmail))
                return RedirectToAction("Index", "Home");

            return View("Form", new ProfileFormViewModel());
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create(ProfileFormViewModel model)
        {
            if (string.IsNullOrEmpty(CurrentEmail))
                return RedirectToAction("Index", "Home");

            if (!ModelState.IsValid) return View("Form", model);

            var trimmedName = (model.Name ?? "").Trim();

            // Case-insensitive duplicate check
            var nameExists = await _db.Profiles
                .AnyAsync(p => p.UserEmail == CurrentEmail &&
                               p.Name.ToLower() == trimmedName.ToLower());
            if (nameExists)
            {
                ModelState.AddModelError(nameof(model.Name),
                    "Ai deja un profil cu acest nume. Alege altul.");
                return View("Form", model);
            }

            _db.Profiles.Add(new Profile
            {
                UserEmail = CurrentEmail,
                Name = trimmedName,
                Relationship = string.IsNullOrWhiteSpace(model.Relationship) ? null : model.Relationship.Trim(),
                Gender = string.IsNullOrWhiteSpace(model.Gender) ? null : model.Gender.Trim(),
                BirthYear = model.BirthYear,
                Notes = string.IsNullOrWhiteSpace(model.Notes) ? null : model.Notes.Trim(),
                IsDefault = false,
                CreatedAt = DateTime.UtcNow
            });
            await _db.SaveChangesAsync();

            TempData["SuccessMessage"] = $"Profilul \"{trimmedName}\" a fost creat.";
            return RedirectToAction(nameof(Index));
        }

        // ====================================================================
        // EDIT
        // ====================================================================
        [HttpGet]
        public async Task<IActionResult> Edit(int id)
        {
            if (string.IsNullOrEmpty(CurrentEmail))
                return RedirectToAction("Index", "Home");

            var profile = await _db.Profiles
                .FirstOrDefaultAsync(p => p.Id == id && p.UserEmail == CurrentEmail);
            if (profile == null) return RedirectToAction(nameof(Index));

            var vm = new ProfileFormViewModel
            {
                Id = profile.Id,
                Name = profile.Name,
                Relationship = profile.Relationship,
                Gender = profile.Gender,
                BirthYear = profile.BirthYear,
                Notes = profile.Notes,
                IsDefault = profile.IsDefault
            };
            return View("Form", vm);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(ProfileFormViewModel model)
        {
            if (string.IsNullOrEmpty(CurrentEmail))
                return RedirectToAction("Index", "Home");

            if (!ModelState.IsValid) return View("Form", model);

            var profile = await _db.Profiles
                .FirstOrDefaultAsync(p => p.Id == model.Id && p.UserEmail == CurrentEmail);
            if (profile == null) return RedirectToAction(nameof(Index));

            var trimmedName = (model.Name ?? "").Trim();

            var nameExists = await _db.Profiles
                .AnyAsync(p => p.UserEmail == CurrentEmail &&
                               p.Id != profile.Id &&
                               p.Name.ToLower() == trimmedName.ToLower());
            if (nameExists)
            {
                ModelState.AddModelError(nameof(model.Name),
                    "Ai deja un profil cu acest nume. Alege altul.");
                return View("Form", model);
            }

            profile.Name = trimmedName;
            profile.Relationship = string.IsNullOrWhiteSpace(model.Relationship) ? null : model.Relationship.Trim();
            profile.Gender = string.IsNullOrWhiteSpace(model.Gender) ? null : model.Gender.Trim();
            profile.BirthYear = model.BirthYear;
            profile.Notes = string.IsNullOrWhiteSpace(model.Notes) ? null : model.Notes.Trim();

            await _db.SaveChangesAsync();
            TempData["SuccessMessage"] = $"Profilul \"{trimmedName}\" a fost actualizat.";
            return RedirectToAction(nameof(Index));
        }

        // ====================================================================
        // DELETE
        // ====================================================================
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Delete(int id)
        {
            if (string.IsNullOrEmpty(CurrentEmail))
                return RedirectToAction("Index", "Home");

            var profile = await _db.Profiles
                .FirstOrDefaultAsync(p => p.Id == id && p.UserEmail == CurrentEmail);
            if (profile == null) return RedirectToAction(nameof(Index));

            if (profile.IsDefault)
            {
                TempData["ErrorMessage"] = "Profilul implicit \"Eu\" nu poate fi șters. Doar poate fi redenumit.";
                return RedirectToAction(nameof(Index));
            }

            _db.Profiles.Remove(profile);
            await _db.SaveChangesAsync();

            TempData["SuccessMessage"] = $"Profilul \"{profile.Name}\" a fost șters.";
            return RedirectToAction(nameof(Index));
        }
    }
}
