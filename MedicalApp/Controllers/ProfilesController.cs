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
        private readonly ILogger<ProfilesController> _logger;

        public ProfilesController(
            AppDbContext db,
            PdfReportGenerator pdfGenerator,
            ILogger<ProfilesController> logger)
        {
            _db = db;
            _pdfGenerator = pdfGenerator;
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
