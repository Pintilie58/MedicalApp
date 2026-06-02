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
        /// Default retention (in days) for files in <c>Sends</c>, <c>Sumar</c>
        /// and <c>Errors</c>. Files older than this are eligible for the
        /// automatic cleanup that runs before every "Lansează lot".
        /// <c>Original</c> is NEVER touched (operator-controlled).
        /// Files from the LAST completed batch are also protected regardless
        /// of age. Default = 30 days. Operator can override per-cleanup from
        /// the dashboard.
        /// </summary>
        public int RetentionDays { get; set; } = 30;
    }
}
