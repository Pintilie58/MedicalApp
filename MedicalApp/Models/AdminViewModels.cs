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
    }

    public class TopSpender
    {
        public string Email { get; set; } = string.Empty;
        public decimal TotalPaid { get; set; }
        public int Credite { get; set; }
        public int CreditConsum { get; set; }
    }

    public class DailyRevenue
    {
        public DateTime Date { get; set; }
        public decimal Amount { get; set; }
        public int Count { get; set; }
    }
}
