using System.ComponentModel.DataAnnotations;

namespace MedicalApp.Models
{
    /// <summary>ViewModel for the admin "Send bulk email" form.</summary>
    public class BulkEmailViewModel
    {
        [Required(ErrorMessage = "Subject is required")]
        [StringLength(200)]
        public string Subject { get; set; } = string.Empty;

        [Required(ErrorMessage = "Message body is required")]
        [StringLength(10000)]
        public string HtmlBody { get; set; } = string.Empty;

        /// <summary>all | paying | with_credits | registered_last_30_days | blocked</summary>
        public string Filter { get; set; } = "all";

        public int RecipientsCount { get; set; }
    }

    /// <summary>ViewModel for the admin create/edit promo code form.</summary>
    public class PromoCodeViewModel
    {
        public int Id { get; set; }

        [Required]
        [StringLength(50, MinimumLength = 2)]
        [RegularExpression(@"^[A-Za-z0-9_-]+$",
            ErrorMessage = "Only letters, digits, underscore and dash are allowed")]
        public string Code { get; set; } = string.Empty;

        [Range(1, 1000, ErrorMessage = "Credits must be between 1 and 1000")]
        public int CreditsToAdd { get; set; } = 3;

        [DataType(DataType.DateTime)]
        public DateTime ValidFrom { get; set; } = DateTime.UtcNow;

        [DataType(DataType.DateTime)]
        public DateTime ValidUntil { get; set; } = DateTime.UtcNow.AddMonths(1);

        [Range(0, 100000, ErrorMessage = "Max uses must be 0 (unlimited) or positive")]
        public int MaxUses { get; set; } = 0;

        public bool IsActive { get; set; } = true;
    }

    /// <summary>ViewModel for giving free credits to a user manually.</summary>
    public class GiveCreditsViewModel
    {
        [Required]
        [EmailAddress]
        public string Email { get; set; } = string.Empty;

        [Range(1, 1000)]
        public int Credits { get; set; } = 1;

        [StringLength(200)]
        public string? Reason { get; set; }
    }

    /// <summary>Dashboard stats aggregate.</summary>
    public class AdminDashboardViewModel
    {
        public int TotalUsers { get; set; }
        public int PayingUsers { get; set; }
        public int ActiveUsers { get; set; }              // users with credits > 0 consumed
        public int BlockedUsers { get; set; }
        public int NewUsersLast7Days { get; set; }

        public int TotalCreditsPurchased { get; set; }
        public int TotalCreditsConsumed { get; set; }
        public int TotalCreditsRemaining { get; set; }

        public int TotalBonusGranted { get; set; }
        public int TotalBonusConsumed { get; set; }
        public int TotalBonusRemaining { get; set; }

        public decimal TotalRevenueEur { get; set; }
        public decimal RevenueLast30DaysEur { get; set; }
        public int PurchasesLast30Days { get; set; }

        public int ActivePromoCodes { get; set; }

        /// <summary>Top 10 spenders.</summary>
        public List<TopSpender> TopSpenders { get; set; } = new();

        /// <summary>Last 30 days: (date, amount) for simple chart.</summary>
        public List<DailyRevenue> RevenueChart { get; set; } = new();

        // ----- AI usage widget (B2C vs CAM, last 30 days) -----
        // Populated by AdminController.Index from the dedicated AiUsageLogs
        // table. Counts EVERY real Gemini call (success/error/rejected) so
        // token-consuming failures are visible too. Split per Source so the
        // admin can see how much cost comes from individual users vs clinics.
        public AiUsageBreakdown AiUsageB2C { get; set; } = new();
        public AiUsageBreakdown AiUsageCam { get; set; } = new();

        /// <summary>Combined total estimated cost (USD) across BOTH sources, last 30 days.</summary>
        public decimal AiCost30DaysUsd { get; set; }

        /// <summary>
        /// Combined percentage of Gemini calls (B2C + CAM) in the last 30 days
        /// that ran on the Pro fallback model. 0..100. Zero when no calls.
        /// </summary>
        public double AiFallbackRatioPct { get; set; }
    }

    /// <summary>
    /// Per-Source breakdown of AI usage rendered inside the dashboard
    /// (one instance for B2C, one for CAM). Each holds its own list of
    /// model rows so we can render side-by-side doughnut charts.
    /// </summary>
    public class AiUsageBreakdown
    {
        public List<ModelUsageRow> Rows { get; set; } = new();
        public int TotalCalls { get; set; }
        public decimal TotalCostUsd { get; set; }
        /// <summary>Share of "Pro" model calls in this breakdown (0..100).</summary>
        public double FallbackRatioPct { get; set; }
        /// <summary>This breakdown's share of the combined total cost (0..100).</summary>
        public double ShareOfCombinedPct { get; set; }
    }

    public class ModelUsageRow
    {
        /// <summary>Pretty short label for the chart legend (e.g. "Flash", "Pro", "Other").</summary>
        public string ShortName { get; set; } = string.Empty;
        /// <summary>Raw model id stored in the DB (e.g. "gemini-2.5-flash").</summary>
        public string ModelId { get; set; } = string.Empty;
        public int Count { get; set; }
        public long InputTokens { get; set; }
        public long OutputTokens { get; set; }
        public decimal EstimatedCostUsd { get; set; }
        /// <summary>Tailwind-like bootstrap color class, picked by the controller.</summary>
        public string BadgeClass { get; set; } = "bg-secondary";
    }

    public class TopSpender
    {
        public string Email { get; set; } = string.Empty;
        public decimal TotalPaid { get; set; }
        public int Credite { get; set; }
        public int CreditConsum { get; set; }

        /// <summary>"Individual" or "Clinic" — drives the type badge in the Admin dashboard.</summary>
        public string UserType { get; set; } = "Individual";

        /// <summary>Clinic name resolved from dbo.Clinics by UserEmail. Null for Individuals.</summary>
        public string? ClinicName { get; set; }
    }

    public class DailyRevenue
    {
        public DateTime Date { get; set; }
        public decimal Amount { get; set; }
        public int Count { get; set; }
    }

    /// <summary>
    /// Row model for the Admin → Users list. Wraps a User and adds the
    /// per-row context the admin needs at a glance: clinic name (B2B only)
    /// and the count of "profiles" — which is family Profiles for
    /// Individual accounts and ClinicPatients for Clinic accounts.
    /// </summary>
    public class UserListItem
    {
        public User User { get; set; } = null!;

        /// <summary>Clinic display name. Null for Individual users or orphans.</summary>
        public string? ClinicName { get; set; }

        /// <summary>
        /// For Individuals: number of rows in dbo.Profiles for this UserEmail.
        /// For Clinics:    number of rows in dbo.ClinicPatients for this ClinicId.
        /// </summary>
        public int ProfilesCount { get; set; }
    }

    /// <summary>
    /// Page model for Admin → UserProfiles — a single page that shows either
    /// family Profiles (Individual) or ClinicPatients (Clinic) for one user.
    /// </summary>
    public class AdminUserProfilesViewModel
    {
        public User User { get; set; } = null!;
        public string? ClinicName { get; set; }
        public int? ClinicId { get; set; }
        public List<AdminProfileRow> Rows { get; set; } = new();
    }

    /// <summary>Unified row shape used by AdminUserProfilesViewModel.</summary>
    public class AdminProfileRow
    {
        public string Name { get; set; } = string.Empty;

        /// <summary>For Individuals: relationship/gender. For Clinics: patient email.</summary>
        public string? Subtitle { get; set; }

        public DateTime CreatedAt { get; set; }

        /// <summary>True only for the auto-generated "Eu" profile (Individual).</summary>
        public bool IsDefault { get; set; }
    }
}
