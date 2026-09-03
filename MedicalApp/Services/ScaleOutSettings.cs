namespace MedicalApp.Services
{
    /// <summary>
    /// Everything needed to run on MORE THAN ONE Azure App Service instance
    /// (or more than one Docker container behind a load balancer).
    ///
    /// <see cref="Enabled"/> = false (default) keeps the exact behaviour of the
    /// single-instance app: session in process memory, Data Protection keys on
    /// the local disk, background timers running unconditionally. Nothing in
    /// development changes. Flip it to true ONLY when hosting with 2+ instances
    /// — see /app/memory/SCALE_OUT.md.
    /// </summary>
    public class ScaleOutSettings
    {
        /// <summary>Master switch. Everything below is ignored while false.</summary>
        public bool Enabled { get; set; } = false;

        /// <summary>Table used by the SQL Server distributed cache (sessions). Created automatically.</summary>
        public string SessionCacheTable { get; set; } = "AppSessionCache";

        public string SessionCacheSchema { get; set; } = "dbo";

        /// <summary>Blob container holding the shared Data Protection keys.</summary>
        public string DataProtectionContainer { get; set; } = "dataprotection";

        public string DataProtectionBlobName { get; set; } = "keys.xml";

        /// <summary>
        /// Identifies this instance in the singleton-lease table. Empty = machine
        /// name, which is what Azure App Service / Docker give us anyway.
        /// </summary>
        public string InstanceId { get; set; } = "";

        /// <summary>
        /// With several instances, a restarting instance must NOT fail the
        /// interpretations running on its siblings. Only "processing" rows older
        /// than this are considered orphaned. A real job never exceeds ~10 min.
        /// </summary>
        public int OrphanGraceMinutes { get; set; } = 30;

        public string ResolvedInstanceId =>
            string.IsNullOrWhiteSpace(InstanceId) ? Environment.MachineName : InstanceId;
    }
}
