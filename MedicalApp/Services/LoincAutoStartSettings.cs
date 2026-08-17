namespace MedicalApp.Services
{
    /// <summary>
    /// Configuration for the optional auto-start / self-healing behavior of the
    /// Python LOINC microservice.
    ///
    /// AZURE-SAFE DESIGN: <see cref="Enabled"/> defaults to <b>false</b> in
    /// <c>appsettings.json</c>. It is only turned on explicitly in
    /// <c>appsettings.Development.json</c> (or by an env-var override on-prem).
    /// On Azure App Service / Container Apps the Python service will be a
    /// separate resource with its own "Always On" and no C# process is allowed
    /// to spawn OS processes anyway, so this whole feature is dormant there.
    /// </summary>
    public class LoincAutoStartSettings
    {
        /// <summary>Master feature flag. Default false = current behavior.</summary>
        public bool Enabled { get; set; } = false;

        /// <summary>How often the background monitor probes /ready. Default 15s.</summary>
        public int PollIntervalSeconds { get; set; } = 15;

        /// <summary>Per-probe HTTP timeout. Default 800 ms (was 2000 in the old inline check).</summary>
        public int ProbeTimeoutMs { get; set; } = 800;

        /// <summary>Consecutive failed probes required before we attempt a restart. Default 2 (avoids one-off blips).</summary>
        public int FailuresBeforeRestart { get; set; } = 2;

        /// <summary>Minimum seconds between two restart attempts. Default 60s.</summary>
        public int RestartCooldownSeconds { get; set; } = 60;

        /// <summary>Working directory that contains the venv and main.py. Windows path OK.</summary>
        public string WorkingDirectory { get; set; } = @"C:\Projects\MedicalApp-repo\loinc_service";

        /// <summary>PowerShell command line executed inside <see cref="WorkingDirectory"/>.</summary>
        public string StartCommand { get; set; } =
            @"$host.UI.RawUI.WindowTitle='LOINC Service (auto-started)'; .\.venv\Scripts\Activate.ps1; uvicorn main:app --host 127.0.0.1 --port 8000";

        /// <summary>When true, the spawned PowerShell window is visible (dev). When false, hidden (prod on-prem).</summary>
        public bool ShowWindow { get; set; } = true;
    }
}
