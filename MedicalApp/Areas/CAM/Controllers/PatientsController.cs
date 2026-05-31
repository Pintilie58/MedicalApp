using MedicalApp.Data;
using MedicalApp.Models;
using MedicalApp.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MedicalApp.Areas.CAM.Controllers
{
    /// <summary>
    /// Listă pacienți pentru o clinică (CAM). Faza 2: căutare + tabel
    /// simplu. Faza 3 va adăuga și istoricul analizelor per pacient.
    /// </summary>
    [Area("CAM")]
    public class PatientsController : Controller
    {
        private readonly AppDbContext _db;

        public PatientsController(AppDbContext db)
        {
            _db = db;
        }

        private string? CurrentEmail => HttpContext.Session.GetString("UserEmail");

        [HttpGet]
        public async Task<IActionResult> Index(string? q)
        {
            if (string.IsNullOrEmpty(CurrentEmail))
                return RedirectToAction("Index", "Home", new { area = "" });

            var clinic = await _db.Clinics.AsNoTracking()
                .FirstOrDefaultAsync(c => c.UserEmail == CurrentEmail);
            if (clinic == null)
            {
                return RedirectToAction("Index", "Dashboard", new { area = "CAM" });
            }

            var query = _db.ClinicPatients.AsNoTracking()
                .Where(p => p.ClinicId == clinic.Id);

            if (!string.IsNullOrWhiteSpace(q))
            {
                // Search on normalized key (covers diacritics + word order)
                // OR on email substring. Both case-insensitive on SQL Server.
                var qNorm = CamPatientKey.Normalize(q);
                var qLower = q.Trim().ToLowerInvariant();
                query = query.Where(p =>
                    p.NameKey.Contains(qNorm) ||
                    p.Email.ToLower().Contains(qLower));
            }

            var rows = await query
                .OrderBy(p => p.Name)
                .Take(500)
                .ToListAsync();

            // Count analyses per patient — single round-trip.
            var patientIds = rows.Select(p => p.Id).ToList();
            var counts = await _db.ClinicAnalyses.AsNoTracking()
                .Where(a => patientIds.Contains(a.PatientId))
                .GroupBy(a => a.PatientId)
                .Select(g => new { Pid = g.Key, Cnt = g.Count() })
                .ToDictionaryAsync(x => x.Pid, x => x.Cnt);

            var vm = new Models.CamPatientsListViewModel
            {
                ClinicName = clinic.Name,
                Query = q ?? string.Empty,
                Items = rows.Select(p => new Models.CamPatientsListViewModel.Row
                {
                    Id = p.Id,
                    Name = p.Name,
                    Email = p.Email,
                    CreatedAt = p.CreatedAt,
                    AnalysesCount = counts.TryGetValue(p.Id, out var c) ? c : 0
                }).ToList()
            };

            return View(vm);
        }

        // ----- POST: șterge pacient + toate analizele asociate -----
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Delete(int id)
        {
            if (string.IsNullOrEmpty(CurrentEmail))
                return RedirectToAction("Index", "Home", new { area = "" });

            var clinic = await _db.Clinics.FirstOrDefaultAsync(c => c.UserEmail == CurrentEmail);
            if (clinic == null)
                return RedirectToAction("Index", "Dashboard", new { area = "CAM" });

            // Scope by ClinicId to prevent cross-clinic deletion.
            var patient = await _db.ClinicPatients
                .FirstOrDefaultAsync(p => p.Id == id && p.ClinicId == clinic.Id);
            if (patient == null)
            {
                TempData["ErrorMessage"] = "Pacientul nu a fost găsit (sau nu aparține acestei clinici).";
                return RedirectToAction(nameof(Index));
            }

            // Remove all analyses tied to this patient first (FK chain).
            var analyses = await _db.ClinicAnalyses
                .Where(a => a.PatientId == patient.Id)
                .ToListAsync();
            if (analyses.Count > 0)
            {
                _db.ClinicAnalyses.RemoveRange(analyses);
            }

            _db.ClinicPatients.Remove(patient);
            await _db.SaveChangesAsync();

            TempData["SuccessMessage"] =
                $"Pacientul „{patient.Name}” a fost șters (împreună cu {analyses.Count} analiză(e)).";
            return RedirectToAction(nameof(Index));
        }
    }
}
