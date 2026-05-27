namespace MedicalApp.Services
{
    /// <summary>
    /// Configuration block for the CAM (Clinici de Analize Medicale) module.
    /// Bound from <c>appsettings.json</c> via the <c>CamSettings</c> section.
    /// </summary>
    public class CamSettings
    {
        /// <summary>
        /// Absolute root path on the local disk where each clinic's working
        /// folders (<c>Original</c>, <c>Sends</c>, <c>Sumar</c>, <c>Errors</c>)
        /// are created. Defaults to <c>C:\MedicalApp_files</c> on Windows.
        ///
        /// Each clinic gets its OWN subfolder under this root, named after a
        /// safe form of its email, so multiple clinics on the same machine
        /// never collide. Example:
        /// <code>
        ///   C:\MedicalApp_files\clinica_abc_at_example_com\Original\...
        ///   C:\MedicalApp_files\clinica_xyz_at_example_com\Original\...
        /// </code>
        ///
        /// When the app is moved to a server later, this path can be swapped
        /// for an Azure Blob container by replacing only
        /// <see cref="ICamFileStore"/>'s implementation — the controllers
        /// don't touch the disk directly.
        /// </summary>
        public string FilesRoot { get; set; } = @"C:\MedicalApp_files";

        /// <summary>
        /// AES key (base64, 32 bytes) used to encrypt patient CNP values at
        /// rest. MUST be set in User Secrets (development) or environment
        /// variable (production). When NULL the CNP encryption layer falls
        /// back to a deterministic warning string so the app does not crash
        /// during the very first run before the operator sets the secret.
        /// </summary>
        public string? CnpEncryptionKeyBase64 { get; set; }
    }
}
