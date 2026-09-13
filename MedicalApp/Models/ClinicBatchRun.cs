using System.ComponentModel.DataAnnotations;

namespace MedicalApp.Models
{
    /// <summary>
    /// One row per "Lansează Interpretare Lot" run started by a clinic operator.
    /// Used to render the live progress page (AJAX polls), to write the
    /// <c>Sumar/Sum_yyyyMMdd_HHmm.txt</c> file when the run finishes, and to
    /// build the historical batches list on the CAM dashboard.
    /// </summary>
    public class ClinicBatchRun
    {
        [Key]
        public int Id { get; set; }

        public int ClinicId { get; set; }

        public DateTime StartedAt { get; set; } = DateTime.UtcNow;

        /// <summary>NULL while the run is still in progress.</summary>
        public DateTime? FinishedAt { get; set; }

        /// <summary>
        /// "Running" while in progress, "Completed" on normal exit,
        /// "Cancelled" if the operator aborted, "Failed" on fatal error.
        /// </summary>
        [Required]
        [StringLength(20)]
        public string Status { get; set; } = "Running";

        /// <summary>Number of files Gemini interpreted successfully.</summary>
        public int FilesInterpreted { get; set; }

        /// <summary>Number of files for which the result email was actually sent to the patient.</summary>
        public int FilesSent { get; set; }

        /// <summary>Number of times a Compare PDF was attached (patient had ≥2 analyses in history).</summary>
        public int FilesCompared { get; set; }

        /// <summary>Number of files that could not be processed (CNP/email missing, AI failure, etc.).</summary>
        public int NotSends { get; set; }

        /// <summary>Total number of PDF files picked up from the Original folder at run start.</summary>
        public int TotalFiles { get; set; }

        // ================================================================
        //  Durable queue (June 2026) — Azure hosting
        //  The batch used to be started with a fire-and-forget Task.Run inside
        //  the request that pressed the button: an instance recycle killed it
        //  silently and the row stayed "Running" until the next app start.
        //  Now the row IS the queue: the operator's request only writes
        //  Status="Queued", and CamBatchQueueWorker (on any instance) claims it
        //  with a lease and renews that lease while it works.
        // ================================================================

        /// <summary>
        /// UI language of the operator who started the batch. Persisted because
        /// the worker runs without an HttpContext, possibly on another instance.
        /// </summary>
        [StringLength(10)]
        public string? LanguageCode { get; set; }

        /// <summary>Instance currently running the batch. NULL while queued or finished.</summary>
        [StringLength(100)]
        public string? OwnerInstance { get; set; }

        /// <summary>
        /// How long the owner's claim is trusted. Renewed by the heartbeat; once
        /// it lapses, the batch is considered abandoned (the instance died).
        /// </summary>
        public DateTime? LeaseUntil { get; set; }

        /// <summary>How many times this batch has been claimed (a retry counts).</summary>
        public int Attempts { get; set; }

        /// <summary>
        /// Set by the Cancel button. Read by the worker's heartbeat, so cancelling
        /// works even when the poll lands on a different instance than the one
        /// doing the work.
        /// </summary>
        public bool CancelRequested { get; set; }

        /// <summary>Optimistic concurrency: exactly one instance can win a claim.</summary>
        [Timestamp]
        public byte[]? RowVersion { get; set; }
    }
}
