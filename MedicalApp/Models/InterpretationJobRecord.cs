using System.ComponentModel.DataAnnotations;

namespace MedicalApp.Models
{
    /// <summary>
    /// A B2C interpretation waiting for (or occupying) a processing slot,
    /// stored in SQL so the queue survives restarts and can be picked up by a
    /// sibling instance.
    ///
    /// Before this table the queue lived only in the process memory: a restart,
    /// a deploy or a crash silently dropped everything that was waiting — the
    /// user had uploaded and paid a credit but never received a result. Now the
    /// row is the source of truth and the in-memory channel is just the fast
    /// dispatch path.
    ///
    /// Lifecycle: "queued" → "running" (with a lease) → row deleted when the
    /// interpretation finishes, whatever the outcome.
    /// </summary>
    public class InterpretationJobRecord
    {
        public int Id { get; set; }

        /// <summary>The history row this job produces. One job per history row.</summary>
        public int HistoryId { get; set; }

        [StringLength(256)]
        public string UserEmail { get; set; } = string.Empty;

        public int ProfileId { get; set; }

        [StringLength(200)]
        public string ProfileName { get; set; } = string.Empty;

        /// <summary>The uploaded report. Deleted together with the row when the job ends.</summary>
        public byte[] PdfBytes { get; set; } = Array.Empty<byte>();

        [StringLength(300)]
        public string OriginalFileName { get; set; } = string.Empty;

        [StringLength(100)]
        public string? PdfHash { get; set; }

        [StringLength(10)]
        public string LanguageCode { get; set; } = "ro";

        public bool Force { get; set; }

        [StringLength(100)]
        public string? ProgressToken { get; set; }

        /// <summary>"queued" or "running".</summary>
        [StringLength(20)]
        public string Status { get; set; } = "queued";

        /// <summary>How many times this job has been started. Guards against a poison job looping forever.</summary>
        public int Attempts { get; set; }

        public DateTime EnqueuedAt { get; set; }

        public DateTime? StartedAt { get; set; }

        /// <summary>Which instance is running it (diagnostics).</summary>
        [StringLength(100)]
        public string? Owner { get; set; }

        /// <summary>While in the future, another instance must not touch the job.</summary>
        public DateTime? LeaseUntil { get; set; }

        /// <summary>Optimistic concurrency: two instances can never claim the same job.</summary>
        [Timestamp]
        public byte[]? RowVersion { get; set; }
    }
}
