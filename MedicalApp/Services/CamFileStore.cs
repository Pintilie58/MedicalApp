using MedicalApp.Models;
using Microsoft.Extensions.Options;

namespace MedicalApp.Services
{
    /// <summary>
    /// Storage abstraction for the CAM module. The current production implementation
    /// (<see cref="LocalDiskCamFileStore"/>) reads/writes the local Windows disk
    /// (<c>C:\MedicalApp_files\&lt;clinic&gt;\</c>). Tomorrow's cloud
    /// implementation can target Azure Blob Storage without controllers
    /// changing a single line.
    /// </summary>
    public interface ICamFileStore
    {
        /// <summary>
        /// Returns the per-clinic root folder ON DISK (e.g.
        /// <c>C:\MedicalApp_files\clinica_demo_at_example_com</c>). Stable for
        /// the lifetime of the clinic — derived from <see cref="Clinic.UserEmail"/>.
        /// Idempotent (does not create anything).
        /// </summary>
        string GetClinicRoot(Clinic clinic);

        /// <summary>
        /// Per-subfolder helpers. They DON'T create the folders, they just
        /// compute paths. Use <see cref="EnsureClinicFoldersAsync"/> first.
        /// </summary>
        string GetOriginalFolder(Clinic clinic);
        string GetSendsFolder(Clinic clinic);
        string GetSumarFolder(Clinic clinic);
        string GetErrorsFolder(Clinic clinic);

        /// <summary>
        /// Creates the <c>Original</c>, <c>Sends</c>, <c>Sumar</c>,
        /// <c>Errors</c> folders for the clinic if they don't already exist.
        /// Called from <c>CreditsController</c> right after the FIRST
        /// successful CAM credit purchase. Idempotent (safe to call again).
        /// Returns the absolute clinic root path that was ensured.
        /// </summary>
        Task<string> EnsureClinicFoldersAsync(Clinic clinic, CancellationToken ct = default);
    }

    /// <summary>
    /// Local Windows-disk implementation of <see cref="ICamFileStore"/>.
    /// Layout:
    /// <code>
    /// {FilesRoot}\
    ///   {clinic-safe-name}\
    ///     Original\   ← operator drops PDF-uri aici
    ///     Sends\      ← mutate aici dupa procesare cu succes
    ///     Sumar\      ← Sum_yyyyMMdd_HHmm.txt per batch
    ///     Errors\     ← fisiere care au esuat 3 retries
    /// </code>
    /// </summary>
    public class LocalDiskCamFileStore : ICamFileStore
    {
        private readonly CamSettings _settings;
        private readonly ILogger<LocalDiskCamFileStore> _logger;

        public LocalDiskCamFileStore(IOptions<CamSettings> options, ILogger<LocalDiskCamFileStore> logger)
        {
            _settings = options.Value;
            _logger = logger;
        }

        public string GetClinicRoot(Clinic clinic)
        {
            var safe = SafeFolderName(clinic.UserEmail);
            return Path.Combine(_settings.FilesRoot, safe);
        }

        public string GetOriginalFolder(Clinic clinic) => Path.Combine(GetClinicRoot(clinic), "Original");
        public string GetSendsFolder(Clinic clinic)    => Path.Combine(GetClinicRoot(clinic), "Sends");
        public string GetSumarFolder(Clinic clinic)    => Path.Combine(GetClinicRoot(clinic), "Sumar");
        public string GetErrorsFolder(Clinic clinic)   => Path.Combine(GetClinicRoot(clinic), "Errors");

        public Task<string> EnsureClinicFoldersAsync(Clinic clinic, CancellationToken ct = default)
        {
            var root = GetClinicRoot(clinic);
            try
            {
                Directory.CreateDirectory(GetOriginalFolder(clinic));
                Directory.CreateDirectory(GetSendsFolder(clinic));
                Directory.CreateDirectory(GetSumarFolder(clinic));
                Directory.CreateDirectory(GetErrorsFolder(clinic));
                _logger.LogInformation("CAM folders ensured for clinic {Email} at {Root}", clinic.UserEmail, root);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to ensure CAM folders for clinic {Email} at {Root}", clinic.UserEmail, root);
                throw;
            }
            return Task.FromResult(root);
        }

        /// <summary>
        /// Turns an email (or any string) into a safe folder-name segment.
        /// "clinica@example.com" → "clinica_at_example_com".
        /// </summary>
        private static string SafeFolderName(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "unknown";
            var s = raw.Trim().ToLowerInvariant().Replace("@", "_at_");
            var invalid = Path.GetInvalidFileNameChars();
            var chars = s.Select(ch => (invalid.Contains(ch) || ch == '.') ? '_' : ch).ToArray();
            return new string(chars);
        }
    }
}
