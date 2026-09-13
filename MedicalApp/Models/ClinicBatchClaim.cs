using System.ComponentModel.DataAnnotations;

namespace MedicalApp.Models
{
    /// <summary>
    /// The lock a worker holds while it runs one CAM batch (June 2026).
    ///
    /// Why a separate table: the lease used to live on <see cref="ClinicBatchRun"/>
    /// together with a [Timestamp] RowVersion. The runner writes that same row
    /// continuously (per-file counters) from its own DbContext while the
    /// keep-alive loop renewed the lease from another one, so the first
    /// heartbeat invalidated the runner's row version and every counter save
    /// threw DbUpdateConcurrencyException. Here the claim is a row of its own:
    /// the primary key (BatchRunId) makes claiming atomic across Azure
    /// instances, and renewing it never touches the batch row.
    /// </summary>
    public class ClinicBatchClaim
    {
        /// <summary>The batch being worked on. Primary key — hence the lock.</summary>
        [Key]
        public int BatchRunId { get; set; }

        /// <summary>Machine/process holding the claim (see CamBatchQueueStore.Instance).</summary>
        [Required]
        [StringLength(100)]
        public string OwnerInstance { get; set; } = string.Empty;

        /// <summary>Until when the claim is trusted. Past this, the batch counts as abandoned.</summary>
        public DateTime LeaseUntil { get; set; }

        /// <summary>When the claim was taken. Only for diagnostics.</summary>
        public DateTime ClaimedAt { get; set; } = DateTime.UtcNow;
    }
}
