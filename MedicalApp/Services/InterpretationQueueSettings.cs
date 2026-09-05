namespace MedicalApp.Services
{
    /// <summary>
    /// Limits for the background B2C interpretation queue, bound from the
    /// <c>InterpretationQueue</c> section of appsettings.json so they can be
    /// raised on the server WITHOUT a rebuild.
    ///
    /// Context for whoever tunes these: one interpretation is ~2-4 minutes of
    /// WAITING on Gemini (1-5 requests total), so a paid Gemini tier (~1000
    /// RPM per project) is nowhere near the bottleneck at these numbers. The
    /// real ceilings today are the single-worker LOINC microservice, the PDF
    /// bytes held in RAM per job, and the fact that this queue lives inside one
    /// process (see /app/memory/AZURE_SCALING.md).
    /// </summary>
    public class InterpretationQueueSettings
    {
        /// <summary>Jobs running at the same time in THIS process instance.</summary>
        public int MaxConcurrent { get; set; } = 3;

        /// <summary>Jobs (queued + running) allowed per user.</summary>
        public int MaxPerUser { get; set; } = 1;

        /// <summary>
        /// How often the durable queue is scanned for work nobody is doing:
        /// jobs left behind by a restart, by an instance that died mid-flight,
        /// or waiting on a busy sibling instance. Minimum 15 s.
        /// </summary>
        public int RecoveryIntervalSeconds { get; set; } = 60;
    }
}
