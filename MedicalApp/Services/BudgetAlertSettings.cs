namespace MedicalApp.Services
{
    /// <summary>
    /// Configuration for the monthly Gemini budget alert. Reads from
    /// appsettings.json section "BudgetAlert". When the current calendar
    /// month's estimated Gemini cost (USD, computed via GeminiPricing on
    /// the AiUsageLogs table) crosses <see cref="MonthlyBudgetUsd"/>, the
    /// service sends an email to every address in AdminSettings.Emails.
    ///
    /// To avoid spam, the service tracks the last alert timestamp in a
    /// small marker file under the system temp dir and re-arms after
    /// <see cref="CooldownHours"/> have passed. Once a new month starts,
    /// the marker is implicitly reset (the budget calculation looks at
    /// the current month only).
    /// </summary>
    public class BudgetAlertSettings
    {
        /// <summary>Master switch — if false the background service exits on start.</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>
        /// Hard cap (USD) for the CURRENT calendar month. Default 35 USD ≈ 30 EUR
        /// at typical EUR/USD ~ 1.10. Adjust to your taste in appsettings.json.
        /// We use USD internally because GeminiPricing returns USD; the alert
        /// email shows both USD and an approximate EUR equivalent.
        /// </summary>
        public decimal MonthlyBudgetUsd { get; set; } = 35m;

        /// <summary>
        /// After an alert is sent we suppress further alerts for this many
        /// hours, even if cost keeps climbing. Prevents email spam during a
        /// runaway spike. Default 24h — one nudge per day at most.
        /// </summary>
        public int CooldownHours { get; set; } = 24;

        /// <summary>
        /// How often the background service wakes up to check the budget.
        /// Default 60 minutes — a 1-hour resolution is more than enough for
        /// a monthly cap and keeps DB load negligible.
        /// </summary>
        public int CheckIntervalMinutes { get; set; } = 60;
    }
}
