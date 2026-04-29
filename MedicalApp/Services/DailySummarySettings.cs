namespace MedicalApp.Services
{
    /// <summary>
    /// Configured via appsettings.json → "DailySummarySettings".
    /// Controls the daily admin summary background job.
    /// </summary>
    public class DailySummarySettings
    {
        /// <summary>When false, the background service will exit immediately and send no emails.</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>Hour of the day (0-23) in SERVER LOCAL TIME when the summary is sent. Default = 9 (09:00).</summary>
        public int HourOfDayLocal { get; set; } = 9;
    }
}
