using MedicalApp.Models;
using Microsoft.Extensions.Options;

namespace MedicalApp.Services
{
    /// <summary>The four logical CAM buckets of a clinic.</summary>
    public enum CamFolder
    {
        /// <summary>Operator drops PDFs here; the batch consumes them.</summary>
        Original,
        /// <summary>Successfully processed files are moved here.</summary>
        Sends,
        /// <summary>Per-batch summaries (.txt / .pdf).</summary>
        Sumar,
        /// <summary>Files that failed all retries, with a .reasons.txt next to them.</summary>
        Errors
    }

    /// <summary>One stored file, independent of where it physically lives.</summary>
    public sealed record CamFileEntry(string Name, long SizeBytes, DateTime LastModifiedUtc);

    /// <summary>
    /// Storage abstraction for the CAM module — expressed as OPERATIONS, not as
    /// filesystem paths.
    ///
    /// This shape is deliberate (June 2026). The previous version handed callers
    /// a folder path and they did their own <c>Directory.GetFiles</c> /
    /// <c>File.Move</c>, which cannot be implemented over Azure Blob Storage:
    /// blobs have no folders, no rename and no local path. With operations, the
    /// same controllers work unchanged over local disk (development, Docker
    /// volume) or over Blob Storage (Azure) — see /app/memory/CAM_BLOB_STORAGE.md.
    ///
    /// Implementations: <see cref="LocalDiskCamFileStore"/>, <see cref="BlobCamFileStore"/>.
    /// </summary>
    public interface ICamFileStore
    {
        /// <summary>
        /// Human-readable location shown in the UI ("where are my files?").
        /// A disk path locally, a blob URL prefix in the cloud. Never used to
        /// perform I/O.
        /// </summary>
        string GetDisplayLocation(Clinic clinic, CamFolder? folder = null);

        /// <summary>
        /// Prepares storage for a clinic (folders on disk, container/prefix in
        /// the cloud). Idempotent. Returns the display location of the root.
        /// </summary>
        Task<string> EnsureClinicFoldersAsync(Clinic clinic, CancellationToken ct = default);

        /// <summary>Files in a bucket, newest first. <paramref name="extension"/> like ".pdf".</summary>
        Task<IReadOnlyList<CamFileEntry>> ListAsync(Clinic clinic, CamFolder folder,
            string? extension = null, CancellationToken ct = default);

        Task<bool> ExistsAsync(Clinic clinic, CamFolder folder, string name,
            CancellationToken ct = default);

        /// <summary>File content, or null when it no longer exists.</summary>
        Task<byte[]?> ReadAsync(Clinic clinic, CamFolder folder, string name,
            CancellationToken ct = default);

        /// <summary>
        /// Stores content and returns the name actually used: when
        /// <paramref name="overwrite"/> is false and the name is taken, a
        /// timestamp prefix is added instead of destroying the existing file.
        /// </summary>
        Task<string> WriteAsync(Clinic clinic, CamFolder folder, string name, byte[] content,
            bool overwrite = false, CancellationToken ct = default);

        /// <summary>
        /// Moves a file between buckets (copy + delete in the cloud) and returns
        /// the destination name, or null if the source was already gone.
        /// </summary>
        Task<string?> MoveAsync(Clinic clinic, CamFolder from, CamFolder to, string name,
            CancellationToken ct = default);

        Task<bool> DeleteAsync(Clinic clinic, CamFolder folder, string name,
            CancellationToken ct = default);
    }

    /// <summary>
    /// Local-disk implementation — the one used during development and inside a
    /// Docker container with a mounted volume.
    /// Layout: <c>{FilesRoot}\{clinic-safe-name}\{Original|Sends|Sumar|Errors}\</c>
    /// </summary>
    public class LocalDiskCamFileStore : ICamFileStore
    {
        private readonly CamSettings _settings;
        private readonly ILogger<LocalDiskCamFileStore> _logger;

        public LocalDiskCamFileStore(IOptions<CamSettings> options,
            ILogger<LocalDiskCamFileStore> logger)
        {
            _settings = options.Value;
            _logger = logger;
        }

        private string Root(Clinic clinic) =>
            Path.Combine(_settings.FilesRoot, SafeFolderName(clinic.UserEmail));

        private string Dir(Clinic clinic, CamFolder folder) =>
            Path.Combine(Root(clinic), folder.ToString());

        public string GetDisplayLocation(Clinic clinic, CamFolder? folder = null) =>
            folder.HasValue ? Dir(clinic, folder.Value) : Root(clinic);

        public Task<string> EnsureClinicFoldersAsync(Clinic clinic, CancellationToken ct = default)
        {
            var root = Root(clinic);
            try
            {
                foreach (CamFolder f in Enum.GetValues<CamFolder>())
                    Directory.CreateDirectory(Dir(clinic, f));
                _logger.LogInformation("CAM folders ensured for clinic {Email} at {Root}",
                    clinic.UserEmail, root);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to ensure CAM folders for clinic {Email} at {Root}",
                    clinic.UserEmail, root);
                throw;
            }
            return Task.FromResult(root);
        }

        public Task<IReadOnlyList<CamFileEntry>> ListAsync(Clinic clinic, CamFolder folder,
            string? extension = null, CancellationToken ct = default)
        {
            var dir = Dir(clinic, folder);
            if (!Directory.Exists(dir))
                return Task.FromResult<IReadOnlyList<CamFileEntry>>(Array.Empty<CamFileEntry>());

            var pattern = string.IsNullOrWhiteSpace(extension) ? "*" : "*" + extension;
            var list = new List<CamFileEntry>();
            foreach (var path in Directory.GetFiles(dir, pattern, SearchOption.TopDirectoryOnly))
            {
                var fi = new FileInfo(path);
                list.Add(new CamFileEntry(fi.Name, fi.Length, fi.LastWriteTimeUtc));
            }
            list.Sort((a, b) => b.LastModifiedUtc.CompareTo(a.LastModifiedUtc));
            return Task.FromResult<IReadOnlyList<CamFileEntry>>(list);
        }

        public Task<bool> ExistsAsync(Clinic clinic, CamFolder folder, string name,
            CancellationToken ct = default) =>
            Task.FromResult(File.Exists(FullPath(clinic, folder, name)));

        public async Task<byte[]?> ReadAsync(Clinic clinic, CamFolder folder, string name,
            CancellationToken ct = default)
        {
            var path = FullPath(clinic, folder, name);
            if (!File.Exists(path)) return null;
            return await File.ReadAllBytesAsync(path, ct);
        }

        public async Task<string> WriteAsync(Clinic clinic, CamFolder folder, string name,
            byte[] content, bool overwrite = false, CancellationToken ct = default)
        {
            var dir = Dir(clinic, folder);
            Directory.CreateDirectory(dir);
            var finalName = SafeFileName(name);
            if (!overwrite && File.Exists(Path.Combine(dir, finalName)))
                finalName = Stamped(finalName);
            await File.WriteAllBytesAsync(Path.Combine(dir, finalName), content, ct);
            return finalName;
        }

        public Task<string?> MoveAsync(Clinic clinic, CamFolder from, CamFolder to, string name,
            CancellationToken ct = default)
        {
            var src = FullPath(clinic, from, name);
            if (!File.Exists(src)) return Task.FromResult<string?>(null);

            var destDir = Dir(clinic, to);
            Directory.CreateDirectory(destDir);
            var finalName = SafeFileName(name);
            if (File.Exists(Path.Combine(destDir, finalName)))
                finalName = Stamped(finalName);
            File.Move(src, Path.Combine(destDir, finalName));
            return Task.FromResult<string?>(finalName);
        }

        public Task<bool> DeleteAsync(Clinic clinic, CamFolder folder, string name,
            CancellationToken ct = default)
        {
            var path = FullPath(clinic, folder, name);
            if (!File.Exists(path)) return Task.FromResult(false);
            File.Delete(path);
            return Task.FromResult(true);
        }

        private string FullPath(Clinic clinic, CamFolder folder, string name) =>
            Path.Combine(Dir(clinic, folder), SafeFileName(name));

        internal static string Stamped(string name) =>
            $"{DateTime.Now:yyyyMMdd_HHmmss}_{name}";

        /// <summary>Strips any path information — callers pass user-supplied names.</summary>
        internal static string SafeFileName(string name) =>
            Path.GetFileName(name ?? string.Empty);

        /// <summary>
        /// Turns an email (or any string) into a safe folder-name segment.
        /// "clinica@example.com" → "clinica_at_example_com".
        /// </summary>
        internal static string SafeFolderName(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "unknown";
            var s = raw.Trim().ToLowerInvariant().Replace("@", "_at_");
            var invalid = Path.GetInvalidFileNameChars();
            var chars = s.Select(ch => (invalid.Contains(ch) || ch == '.') ? '_' : ch).ToArray();
            return new string(chars);
        }
    }
}
