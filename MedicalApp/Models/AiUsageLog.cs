using System;
using System.ComponentModel.DataAnnotations;

namespace MedicalApp.Models
{
    /// <summary>
    /// Dedicated log for every real Gemini API call, regardless of outcome.
    /// Used exclusively by the Admin Dashboard "AI usage" widget so we can:
    ///   1. Count ALL token-consuming calls (success / error / rejected).
    ///   2. Cover BOTH paths: B2C (InterpretationController) and B2B (CamBatchService).
    ///   3. Reset counters without touching InterpretationHistories (user-facing
    ///      history must remain intact).
    ///
    /// One row per Gemini API call. If a call retries (Flash -> Pro -> Plus),
    /// we log only the FINAL successful attempt (or the LAST failed attempt
    /// if all tiers failed). This matches the user's mental model of "one
    /// interpretation = one call" while still attributing cost to the model
    /// that actually consumed tokens.
    /// </summary>
    public class AiUsageLog
    {
        public int Id { get; set; }

        /// <summary>UTC timestamp of the call. Indexed for "last 30 days" queries.</summary>
        public DateTime CreatedAt { get; set; }

        /// <summary>"B2C" (individual user) or "CAM" (clinic batch). Indexed.</summary>
        [MaxLength(10)]
        public string Source { get; set; } = "B2C";

        /// <summary>Owner email (B2C path) or clinic owner email (CAM path). Nullable for legacy safety.</summary>
        [MaxLength(200)]
        public string? UserEmail { get; set; }

        /// <summary>Clinic id when Source = "CAM", otherwise null.</summary>
        public int? ClinicId { get; set; }

        /// <summary>Effective Gemini model id that consumed tokens (e.g. "gemini-2.5-flash"). Never null on new rows.</summary>
        [MaxLength(80)]
        public string ModelUsed { get; set; } = "(unknown)";

        public int InputTokens { get; set; }
        public int OutputTokens { get; set; }

        /// <summary>"success", "error" or "rejected". Indexed for filtering.</summary>
        [MaxLength(20)]
        public string Status { get; set; } = "success";

        /// <summary>Truncated error message when Status != "success". Capped at 500 chars.</summary>
        [MaxLength(500)]
        public string? ErrorMessage { get; set; }
    }
}
