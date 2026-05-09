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

            // Sort by patient's sampling date (newest sampling first), with a tolerant
            // parser - falls back to CreatedAt when DateTaken is missing or unparsable.
            items = items
                .OrderByDescending(r => ParseSamplingDate(r.DateTaken) ?? r.CreatedAt)
                .ThenByDescending(r => r.CreatedAt)
                .ToList();

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
        // COMPARE 2 to 4 interpretations side-by-side (P1.5.5, premium feature).
        // Columns are ordered oldest → newest by patient's sampling date
        // (PatientInfo.DateTaken in the stored JSON, with a tolerant parser),
        // falling back to CreatedAt when the date cannot be parsed.
        // ====================================================================
        [HttpGet]
        public async Task<IActionResult> Compare(int profileId, int[]? ids)
        {
            if (string.IsNullOrEmpty(CurrentEmail))
                return RedirectToAction("Index", "Home");

            // Sanitize: distinct, non-zero ids, max 4.
            var distinctIds = (ids ?? Array.Empty<int>())
                .Where(i => i > 0)
                .Distinct()
                .Take(CompareInterpretationsViewModel.MaxSelections)
                .ToArray();

            if (distinctIds.Length < CompareInterpretationsViewModel.MinSelections)
            {
                TempData["ErrorMessage"] =
                    $"Selectează între {CompareInterpretationsViewModel.MinSelections} și " +
                    $"{CompareInterpretationsViewModel.MaxSelections} interpretări pentru comparație.";
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
                .Where(h => distinctIds.Contains(h.Id)
                            && h.UserEmail == CurrentEmail
                            && h.ProfileId == profile.Id
                            && h.Status == "success"
                            && h.RawJsonResult != null)
                .ToListAsync();

            if (items.Count != distinctIds.Length)
            {
                TempData["ErrorMessage"] = "Una sau mai multe interpretări selectate nu au fost găsite.";
                return RedirectToAction(nameof(History), new { id = profileId });
            }

            // Archive premium billing: 1 use regardless of how many columns are compared.
            var check = _archiveAccess.TryConsume(user, "compare");
            if (!check.Allowed)
            {
                TempData["ErrorMessage"] =
                    "Ai rămas fără credite pentru comparație. Cumpără credite pentru a continua.";
                return RedirectToAction("Buy", "Credits");
            }
            await _db.SaveChangesAsync();

            // Deserialize each JSON; drop any that fail to parse.
            var parsed = new List<(InterpretationHistory h, InterpretationResult r)>();
            foreach (var h in items)
            {
                var r = DeserializeSafe(h.RawJsonResult);
                if (r != null) parsed.Add((h, r));
            }
            if (parsed.Count < CompareInterpretationsViewModel.MinSelections)
            {
                TempData["ErrorMessage"] = "Comparația nu a putut fi generată din datele stocate.";
                return RedirectToAction(nameof(History), new { id = profileId });
            }

            // Sort oldest → newest by patient's SAMPLING date (PatientInfo.DateTaken).
            // Fallback to CreatedAt when DateTaken is missing or unparsable.
            parsed = parsed
                .Select(t => (t.h, t.r,
                              eff: ParseSamplingDate(t.r.PatientInfo?.DateTaken) ?? t.h.CreatedAt))
                .OrderBy(t => t.eff)
                .Select(t => (t.h, t.r))
                .ToList();

            var vm = BuildComparison(profile, parsed);
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

        /// <summary>
        /// Tolerantly parses the various date formats labs print on PDFs. Examples we want
        /// to handle: "27/01/2014", "27.01.2014", "27-01-2014", "2014-01-27", "01/27/2014",
        /// "27/01/2014 14:30", "27 Jan 2014" etc. Returns null when no parse succeeds.
        /// </summary>
        private static DateTime? ParseSamplingDate(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            var s = raw.Trim();

            string[] formats =
            {
                "yyyy-MM-dd",
                "yyyy/MM/dd",
                "dd/MM/yyyy", "dd-MM-yyyy", "dd.MM.yyyy",
                "d/M/yyyy",  "d-M-yyyy",  "d.M.yyyy",
                "MM/dd/yyyy",
                "dd/MM/yyyy HH:mm", "dd-MM-yyyy HH:mm", "dd.MM.yyyy HH:mm",
                "yyyy-MM-dd HH:mm", "yyyy-MM-ddTHH:mm:ss",
                "dd MMM yyyy", "dd MMMM yyyy",
                "MMM dd, yyyy", "MMMM dd, yyyy"
            };

            // Try several culture-specific parses (locales used by the lab PDFs we see).
            string[] cultures = { "en-US", "ro-RO", "fr-FR", "es-ES", "de-DE" };
            foreach (var cult in cultures)
            {
                var ci = System.Globalization.CultureInfo.GetCultureInfo(cult);
                if (DateTime.TryParseExact(s, formats, ci,
                        System.Globalization.DateTimeStyles.AssumeLocal, out var d))
                    return d;
            }
            // Last-ditch generic parse.
            return DateTime.TryParse(s,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeLocal, out var any) ? any : (DateTime?)null;
        }

        private static CompareInterpretationsViewModel BuildComparison(
            Profile profile,
            List<(InterpretationHistory h, InterpretationResult r)> sortedOldestFirst)
        {
            static string Key(string param) =>
                (param ?? string.Empty).Trim().ToLowerInvariant();

            int n = sortedOldestFirst.Count;

            // Build per-column key→KeyResult dictionaries.
            var keyMaps = sortedOldestFirst
                .Select(t => (t.r.KeyResults ?? new())
                    .Where(k => !string.IsNullOrWhiteSpace(k.Parameter))
                    .GroupBy(k => Key(k.Parameter))
                    .ToDictionary(g => g.Key, g => g.First()))
                .ToList();

            var allKeys = keyMaps.SelectMany(m => m.Keys).Distinct().OrderBy(k => k).ToList();

            int risen = 0, fallen = 0, unchanged = 0, partial = 0;

            var rows = new List<CompareInterpretationsViewModel.ComparisonRow>(allKeys.Count);
            foreach (var k in allKeys)
            {
                // Find a representative parameter object for the row's metadata
                // (latest column wins, falls back through earlier columns).
                KeyResult? meta = null;
                for (int i = n - 1; i >= 0 && meta == null; i--)
                    keyMaps[i].TryGetValue(k, out meta);

                var row = new CompareInterpretationsViewModel.ComparisonRow
                {
                    Parameter = meta?.Parameter ?? k,
                    Unit = meta?.Unit,
                    ReferenceRange = meta?.ReferenceRange
                };

                // First numeric value index (used as the baseline for "risen/fallen").
                int? baseIdx = null;
                double baseValue = 0;
                int presentCount = 0;
                int numericCount = 0;

                for (int i = 0; i < n; i++)
                {
                    var cell = new CompareInterpretationsViewModel.Cell();
                    if (keyMaps[i].TryGetValue(k, out var kr))
                    {
                        presentCount++;
                        cell.Value = kr.Value;
                        cell.Status = kr.Status;
                        cell.CellDirection = "unchanged"; // refined below
                        var (v, ok) = ParseNumeric(kr.Value);
                        if (ok)
                        {
                            numericCount++;
                            if (baseIdx == null)
                            {
                                baseIdx = i;
                                baseValue = v;
                                cell.CellDirection = "first";
                            }
                            else
                            {
                                if (Math.Abs(v - baseValue) < 1e-9) cell.CellDirection = "unchanged";
                                else if (v > baseValue) cell.CellDirection = "risen";
                                else cell.CellDirection = "fallen";
                            }
                        }
                        else
                        {
                            cell.CellDirection = baseIdx == null ? "first" : "unchanged";
                        }
                    }
                    else
                    {
                        cell.CellDirection = "absent";
                    }
                    row.Cells.Add(cell);
                }

                // Aggregate row-level direction.
                if (presentCount < n)
                {
                    row.Direction = "partial";
                    partial++;
                }
                else if (numericCount == n && baseIdx != null)
                {
                    // Compare LAST numeric vs the baseline (first numeric).
                    var lastNumeric = row.Cells
                        .Select((c, idx) => (c, idx))
                        .Where(t => t.c.CellDirection != "absent" && ParseNumeric(t.c.Value).ok)
                        .Select(t => ParseNumeric(t.c.Value).value)
                        .Last();
                    if (Math.Abs(lastNumeric - baseValue) < 1e-9) { row.Direction = "unchanged"; unchanged++; }
                    else if (lastNumeric > baseValue) { row.Direction = "risen"; risen++; }
                    else { row.Direction = "fallen"; fallen++; }
                }
                else
                {
                    // All cells present but at least one non-numeric: compare strings.
                    var first = row.Cells[0].Value?.Trim();
                    bool allEqual = row.Cells.All(c =>
                        string.Equals(c.Value?.Trim(), first, StringComparison.OrdinalIgnoreCase));
                    if (allEqual) { row.Direction = "unchanged"; unchanged++; }
                    else { row.Direction = "unparsable"; }
                }

                rows.Add(row);
            }

            var columns = sortedOldestFirst.Select(t =>
            {
                var eff = ParseSamplingDate(t.r.PatientInfo?.DateTaken) ?? t.h.CreatedAt;
                return new CompareInterpretationsViewModel.Column
                {
                    HistoryId = t.h.Id,
                    CreatedAt = t.h.CreatedAt,
                    OriginalFileName = t.h.OriginalFileName,
                    DateTaken = t.r.PatientInfo?.DateTaken,
                    EffectiveDate = eff,
                    KeyResultsCount = t.r.KeyResults?.Count ?? 0,
                    AbnormalFindingsCount = t.r.AbnormalFindings?.Count ?? 0
                };
            }).ToList();

            return new CompareInterpretationsViewModel
            {
                ProfileId = profile.Id,
                ProfileName = profile.Name,
                Columns = columns,
                Rows = rows,
                RisenCount = risen,
                FallenCount = fallen,
                UnchangedCount = unchanged,
                PartialCount = partial
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
                CardiovascularRisk = NormalizeCvRisk(model.CardiovascularRisk),
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
                CardiovascularRisk = profile.CardiovascularRisk,
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
            profile.CardiovascularRisk = NormalizeCvRisk(model.CardiovascularRisk);

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

        /// <summary>
        /// Validates and normalizes the cardiovascular-risk dropdown value.
        /// Accepts only the three known categories; everything else (including the
        /// "unknown" placeholder) is mapped to null so the AI prompt can fall back
        /// to its multi-threshold rule.
        /// </summary>
        private static string? NormalizeCvRisk(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            var v = raw.Trim().ToLowerInvariant();
            return v switch
            {
                "low_moderate" => "low_moderate",
                "high"         => "high",
                "very_high"    => "very_high",
                _              => null
            };
        }
    }
}
