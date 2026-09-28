using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace MedicalApp.Services
{
    /// <summary>
    /// Business metrics for Application Insights (Azure, June 2026).
    ///
    /// Built on the .NET <see cref="Meter"/> API only, so this file has no
    /// dependency on the Application Insights package: when nobody listens
    /// (local run, no connection string) every call is a cheap no-op.
    /// Program.cs registers the meter with <c>AddMeter(MeterName)</c> only
    /// when Application Insights is on. Guide: Docs/APPLICATION_INSIGHTS.md
    /// </summary>
    public static class AppTelemetry
    {
        public const string MeterName = "MyMedicalApp";

        private static readonly Meter Meter = new(MeterName, "1.0");

        // ---- Gemini -------------------------------------------------------
        private static readonly Counter<long> GeminiCalls =
            Meter.CreateCounter<long>("gemini.calls", "call",
                "Gemini API calls (tags: model, outcome = ok|rate_limited|unavailable|model_retired|cancelled|error)");

        private static readonly Histogram<double> GeminiCallDuration =
            Meter.CreateHistogram<double>("gemini.call.duration", "ms",
                "Wall time of one Gemini call, including the wait in the internal rate limiter");

        private static readonly Counter<long> GeminiTokens =
            Meter.CreateCounter<long>("gemini.tokens", "token",
                "Gemini tokens consumed (tags: model, kind = input|output|thinking)");

        // ---- Queues -------------------------------------------------------
        private static readonly Counter<long> B2cJobs =
            Meter.CreateCounter<long>("interpretation.b2c.jobs", "job",
                "B2C interpretations finished by the background worker (tag: outcome)");

        private static readonly Histogram<double> B2cJobDuration =
            Meter.CreateHistogram<double>("interpretation.b2c.duration", "s",
                "Seconds from worker pick-up to finish for one B2C interpretation");

        private static readonly Counter<long> CamBatches =
            Meter.CreateCounter<long>("cam.batches", "batch",
                "CAM (B2B) batches finished by the background worker (tag: outcome)");

        private static readonly Histogram<double> CamBatchDuration =
            Meter.CreateHistogram<double>("cam.batch.duration", "s",
                "Seconds from claim to finish for one CAM batch");

        // ---- Payments -----------------------------------------------------
        private static readonly Counter<long> Payments =
            Meter.CreateCounter<long>("payments.completed", "payment",
                "Fulfilled credit purchases (tags: provider, package)");

        private static readonly Counter<double> PaymentsAmount =
            Meter.CreateCounter<double>("payments.amount", "EUR",
                "Revenue from fulfilled purchases (tags: provider, package)");

        private static readonly Counter<long> PaymentsCredits =
            Meter.CreateCounter<long>("payments.credits", "credit",
                "Credits granted by fulfilled purchases (tags: provider, package)");

        public static void RecordGeminiCall(string model, string outcome, double milliseconds)
        {
            var tags = new TagList { { "model", model }, { "outcome", outcome } };
            GeminiCalls.Add(1, tags);
            GeminiCallDuration.Record(milliseconds, tags);
        }

        public static void RecordGeminiTokens(string model, int input, int output, int thinking)
        {
            if (input > 0) GeminiTokens.Add(input, new TagList { { "model", model }, { "kind", "input" } });
            if (output > 0) GeminiTokens.Add(output, new TagList { { "model", model }, { "kind", "output" } });
            if (thinking > 0) GeminiTokens.Add(thinking, new TagList { { "model", model }, { "kind", "thinking" } });
        }

        public static void RecordB2cJob(string outcome, TimeSpan elapsed)
        {
            var tags = new TagList { { "outcome", outcome } };
            B2cJobs.Add(1, tags);
            B2cJobDuration.Record(elapsed.TotalSeconds, tags);
        }

        public static void RecordCamBatch(string outcome, TimeSpan elapsed)
        {
            var tags = new TagList { { "outcome", outcome } };
            CamBatches.Add(1, tags);
            CamBatchDuration.Record(elapsed.TotalSeconds, tags);
        }

        public static void RecordPayment(string provider, string package, decimal amountEur, int credits)
        {
            var tags = new TagList { { "provider", provider }, { "package", package } };
            Payments.Add(1, tags);
            PaymentsAmount.Add((double)amountEur, tags);
            PaymentsCredits.Add(credits, tags);
        }

        /// <summary>
        /// Gauges are read by the exporter on its own schedule (every ~60 s),
        /// so the queue itself needs no changes. Values are per instance.
        /// </summary>
        public static void RegisterQueueGauges(InterpretationJobQueue queue)
        {
            Meter.CreateObservableGauge("interpretation.b2c.queue.waiting", () => queue.QueuedCount, "job",
                "B2C jobs waiting for a free slot on this instance");
            Meter.CreateObservableGauge("interpretation.b2c.queue.active", () => queue.ActiveCount, "job",
                "B2C jobs queued or running on this instance");
        }
    }
}
