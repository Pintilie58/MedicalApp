using System.ComponentModel.DataAnnotations;

namespace MedicalApp.Models
{
    /// <summary>
    /// One "send email to everybody" job started from the admin panel.
    ///
    /// It used to run inside the POST that pressed the button: with a few
    /// hundred recipients that request blows past Azure App Service's 230-second
    /// limit, the admin gets a 502 and has no idea how many mails went out.
    /// Now the request only writes this row and BulkEmailWorker sends them,
    /// keeping the counters up to date so the admin can watch the progress.
    /// </summary>
    public class BulkEmailJob
    {
        [Key]
        public int Id { get; set; }

        /// <summary>Who started it (admin email), for the audit trail.</summary>
        [Required]
        [StringLength(200)]
        public string CreatedBy { get; set; } = string.Empty;

        /// <summary>all | paying | with_credits | registered_last_30_days | blocked</summary>
        [Required]
        [StringLength(40)]
        public string Filter { get; set; } = "all";

        [Required]
        [StringLength(200)]
        public string Subject { get; set; } = string.Empty;

        [Required]
        public string HtmlBody { get; set; } = string.Empty;

        /// <summary>queued | running | done | failed</summary>
        [Required]
        [StringLength(20)]
        public string Status { get; set; } = "queued";

        /// <summary>Recipients resolved when the job was created.</summary>
        public int Total { get; set; }

        public int Sent { get; set; }

        public int Failed { get; set; }

        /// <summary>Index of the next recipient to handle — a retry resumes here.</summary>
        public int NextIndex { get; set; }

        /// <summary>Error of the last failure, for the admin list.</summary>
        [StringLength(500)]
        public string? LastError { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        public DateTime? FinishedAt { get; set; }

        [StringLength(100)]
        public string? OwnerInstance { get; set; }

        public DateTime? LeaseUntil { get; set; }

        public int Attempts { get; set; }

        [Timestamp]
        public byte[]? RowVersion { get; set; }
    }
}
