using Azure;
using Azure.Identity;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using MedicalApp.Models;
using Microsoft.Extensions.Options;

namespace MedicalApp.Services
{
    /// <summary>
    /// Azure Blob Storage implementation of <see cref="ICamFileStore"/>.
    ///
    /// Mapping (there are NO folders in Blob Storage — only names with slashes):
    /// <code>
    ///   C:\MedicalApp_files\clinica_at_x_com\Original\a.pdf
    ///   →  container "cam", blob "clinica_at_x_com/Original/a.pdf"
    /// </code>
    ///
    /// Notes that matter in production:
    ///   * a blob is either fully written or absent, so a batch can never pick
    ///     up a half-uploaded PDF (a real risk on a file share);
    ///   * "move" is a server-side copy followed by a delete — the bytes never
    ///     travel to the app;
    ///   * with <c>AccountUrl</c> set and no connection string we authenticate
    ///     through the App Service managed identity: zero secrets in config.
    /// </summary>
    public class BlobCamFileStore : ICamFileStore
    {
        private readonly CamSettings _settings;
        private readonly ILogger<BlobCamFileStore> _logger;
        private readonly BlobContainerClient _container;

        public BlobCamFileStore(IOptions<CamSettings> options, ILogger<BlobCamFileStore> logger)
        {
            _settings = options.Value;
            _logger = logger;

            var blob = _settings.Blob;
            var serviceClient = !string.IsNullOrWhiteSpace(blob.ConnectionString)
                ? new BlobServiceClient(blob.ConnectionString)
                : new BlobServiceClient(new Uri(blob.AccountUrl), new DefaultAzureCredential());

            _container = serviceClient.GetBlobContainerClient(blob.Container);
        }

        private static string Prefix(Clinic clinic) =>
            LocalDiskCamFileStore.SafeFolderName(clinic.UserEmail) + "/";

        private static string Prefix(Clinic clinic, CamFolder folder) =>
            Prefix(clinic) + folder + "/";

        private static string BlobName(Clinic clinic, CamFolder folder, string name) =>
            Prefix(clinic, folder) + LocalDiskCamFileStore.SafeFileName(name);

        public string GetDisplayLocation(Clinic clinic, CamFolder? folder = null)
        {
            var path = folder.HasValue ? Prefix(clinic, folder.Value) : Prefix(clinic);
            return $"{_container.Uri}/{path}";
        }

        public async Task<string> EnsureClinicFoldersAsync(Clinic clinic, CancellationToken ct = default)
        {
            // Only the container is a real object; the per-clinic prefixes come
            // into existence with the first blob, so there is nothing to create.
            await _container.CreateIfNotExistsAsync(PublicAccessType.None, cancellationToken: ct);
            _logger.LogInformation("CAM blob prefix ready for clinic {Email}: {Prefix}",
                clinic.UserEmail, Prefix(clinic));
            return GetDisplayLocation(clinic);
        }

        public async Task<IReadOnlyList<CamFileEntry>> ListAsync(Clinic clinic, CamFolder folder,
            string? extension = null, CancellationToken ct = default)
        {
            var prefix = Prefix(clinic, folder);
            var list = new List<CamFileEntry>();

            await foreach (var item in _container
                .GetBlobsAsync(BlobTraits.None, BlobStates.None, prefix, ct))
            {
                var name = item.Name[prefix.Length..];
                // Defensive: ignore anything nested deeper than the bucket.
                if (name.Contains('/')) continue;
                if (!string.IsNullOrWhiteSpace(extension)
                    && !name.EndsWith(extension, StringComparison.OrdinalIgnoreCase)) continue;

                list.Add(new CamFileEntry(
                    name,
                    item.Properties.ContentLength ?? 0,
                    item.Properties.LastModified?.UtcDateTime ?? DateTime.UtcNow));
            }

            list.Sort((a, b) => b.LastModifiedUtc.CompareTo(a.LastModifiedUtc));
            return list;
        }

        public async Task<bool> ExistsAsync(Clinic clinic, CamFolder folder, string name,
            CancellationToken ct = default) =>
            await _container.GetBlobClient(BlobName(clinic, folder, name)).ExistsAsync(ct);

        public async Task<byte[]?> ReadAsync(Clinic clinic, CamFolder folder, string name,
            CancellationToken ct = default)
        {
            var blob = _container.GetBlobClient(BlobName(clinic, folder, name));
            try
            {
                using var ms = new MemoryStream();
                await blob.DownloadToAsync(ms, ct);
                return ms.ToArray();
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                return null;
            }
        }

        public async Task<string> WriteAsync(Clinic clinic, CamFolder folder, string name,
            byte[] content, bool overwrite = false, CancellationToken ct = default)
        {
            await _container.CreateIfNotExistsAsync(PublicAccessType.None, cancellationToken: ct);

            var finalName = LocalDiskCamFileStore.SafeFileName(name);
            if (!overwrite &&
                await _container.GetBlobClient(BlobName(clinic, folder, finalName)).ExistsAsync(ct))
            {
                finalName = LocalDiskCamFileStore.Stamped(finalName);
            }

            var blob = _container.GetBlobClient(BlobName(clinic, folder, finalName));
            using var ms = new MemoryStream(content, writable: false);
            await blob.UploadAsync(ms, overwrite: true, ct);
            return finalName;
        }

        public async Task<string?> MoveAsync(Clinic clinic, CamFolder from, CamFolder to, string name,
            CancellationToken ct = default)
        {
            var source = _container.GetBlobClient(BlobName(clinic, from, name));
            if (!await source.ExistsAsync(ct)) return null;

            var finalName = LocalDiskCamFileStore.SafeFileName(name);
            if (await _container.GetBlobClient(BlobName(clinic, to, finalName)).ExistsAsync(ct))
                finalName = LocalDiskCamFileStore.Stamped(finalName);

            var target = _container.GetBlobClient(BlobName(clinic, to, finalName));
            var copy = await target.StartCopyFromUriAsync(source.Uri, cancellationToken: ct);
            await copy.WaitForCompletionAsync(ct);
            await source.DeleteIfExistsAsync(cancellationToken: ct);
            return finalName;
        }

        public async Task<bool> DeleteAsync(Clinic clinic, CamFolder folder, string name,
            CancellationToken ct = default) =>
            await _container.GetBlobClient(BlobName(clinic, folder, name))
                .DeleteIfExistsAsync(cancellationToken: ct);
    }
}
