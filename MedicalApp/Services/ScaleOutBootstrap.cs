using Azure.Identity;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;

namespace MedicalApp.Services
{
    /// <summary>
    /// Startup helpers for multi-instance hosting. Called from Program.cs BEFORE
    /// the container is built, so it works with plain settings objects.
    /// </summary>
    public static class ScaleOutBootstrap
    {
        /// <summary>
        /// Makes sure the container that holds the shared Data Protection keys
        /// exists and returns the blob client the keys are written to.
        /// Uses the same credentials as CAM: connection string when provided
        /// (Azurite / dev), otherwise managed identity.
        /// </summary>
        public static BlobClient EnsureDataProtectionBlob(CamBlobSettings blob, ScaleOutSettings scaleOut)
        {
            var service = !string.IsNullOrWhiteSpace(blob.ConnectionString)
                ? new BlobServiceClient(blob.ConnectionString)
                : new BlobServiceClient(new Uri(blob.AccountUrl), new DefaultAzureCredential());

            var container = service.GetBlobContainerClient(scaleOut.DataProtectionContainer);
            container.CreateIfNotExists(PublicAccessType.None);
            return container.GetBlobClient(scaleOut.DataProtectionBlobName);
        }
    }
}
