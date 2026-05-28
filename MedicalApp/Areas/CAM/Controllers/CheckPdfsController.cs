using MedicalApp.Data;
using MedicalApp.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MedicalApp.Areas.CAM.Controllers
{
    /// <summary>
    /// Pagina /CAM/CheckPdfs — scanează folderul "Original" al clinicii curente
    /// și afișează, pentru fiecare PDF, ce a reușit să extragă
    /// <see cref="CamPdfMetadataExtractor"/>: nume + email + status.
    /// Util pentru a verifica înainte de Faza 3 dacă regex-urile prind corect
    /// metadata din PDF-urile lab-urilor cu care vei lucra.
    /// </summary>
    [Area("CAM")]
    public class CheckPdfsController : Controller
    {
        private readonly AppDbContext _db;
        private readonly ICamFileStore _files;
        private readonly CamPdfMetadataExtractor _extractor;
        private readonly ILogger<CheckPdfsController> _logger;

        public CheckPdfsController(
            AppDbContext db,
            ICamFileStore files,
            CamPdfMetadataExtractor extractor,
            ILogger<CheckPdfsController> logger)
        {
            _db = db;
            _files = files;
            _extractor = extractor;
            _logger = logger;
        }

        private string? CurrentEmail => HttpContext.Session.GetString("UserEmail");

        [HttpGet]
        public async Task<IActionResult> Index()
        {
            if (string.IsNullOrEmpty(CurrentEmail))
                return RedirectToAction("Index", "Home", new { area = "" });

            var clinic = await _db.Clinics.AsNoTracking()
                .FirstOrDefaultAsync(c => c.UserEmail == CurrentEmail);
            if (clinic == null)
                return RedirectToAction("Index", "Dashboard", new { area = "CAM" });

            var vm = new Models.CamCheckPdfsViewModel
            {
                ClinicName = clinic.Name,
                OriginalFolder = _files.GetOriginalFolder(clinic)
            };

            if (!Directory.Exists(vm.OriginalFolder))
            {
                vm.FolderMissing = true;
                return View(vm);
            }

            var pdfs = Directory.GetFiles(vm.OriginalFolder, "*.pdf", SearchOption.TopDirectoryOnly)
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var path in pdfs)
            {
                var row = new Models.CamCheckPdfsViewModel.Row
                {
                    FileName = Path.GetFileName(path),
                    SizeKb = (int)Math.Round(new FileInfo(path).Length / 1024.0)
                };
                try
                {
                    var bytes = await System.IO.File.ReadAllBytesAsync(path);
                    var meta = _extractor.Extract(bytes, row.FileName);
                    row.PatientName = meta.PatientName;
                    row.PatientEmail = meta.PatientEmail;
                    row.IsValid = meta.IsValid;
                    row.Reason = meta.Reason;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "CheckPdfs: failed reading {File}", path);
                    row.IsValid = false;
                    row.Reason = "I/O error: " + ex.Message;
                }
                vm.Items.Add(row);
            }

            return View(vm);
        }
    }
}
