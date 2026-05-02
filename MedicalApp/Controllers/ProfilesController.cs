using MedicalApp.Data;
using MedicalApp.Models;
using MedicalApp.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MedicalApp.Controllers
{
    /// <summary>
    /// Manages the current user's health profiles (Eu, Mama, Tata, etc.).
    /// All actions require an authenticated user (session "UserEmail" set).
    /// </summary>
    public class ProfilesController : Controller
    {
        private readonly AppDbContext _db;
        private readonly ILogger<ProfilesController> _logger;

        public ProfilesController(AppDbContext db, ILogger<ProfilesController> logger)
        {
            _db = db;
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
