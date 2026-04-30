using System.ComponentModel.DataAnnotations;

namespace MedicalApp.Models
{
    /// <summary>
    /// A "health profile" owned by a User. Represents someone whose medical
    /// analyses are interpreted using the owner's credits (self, mother,
    /// father, child, friend, etc.). Every User has exactly one default
    /// profile named "Eu" created automatically on first login.
    /// </summary>
    public class Profile
    {
        [Key]
        public int Id { get; set; }

        /// <summary>Owner of the profile. FK to Users.Email.</summary>
        [Required]
        [EmailAddress]
        [StringLength(200)]
        public string UserEmail { get; set; } = string.Empty;

        /// <summary>Display name of the profile (e.g. "Mama", "Tata", "Viorel", "Eu").</summary>
        [Required]
        [StringLength(100)]
        public string Name { get; set; } = string.Empty;

        /// <summary>Optional relationship: self / mother / father / child / sibling / partner / friend / other.</summary>
        [StringLength(50)]
        public string? Relationship { get; set; }

        /// <summary>Optional: "M" / "F" — helps AI use age/gender-appropriate reference ranges in the future.</summary>
        [StringLength(10)]
        public string? Gender { get; set; }

        /// <summary>Optional birth year — helps AI interpret age-dependent parameters (e.g. VSH).</summary>
        public int? BirthYear { get; set; }

        /// <summary>Free-text notes (allergies, chronic conditions). Kept short and optional.</summary>
        [StringLength(500)]
        public string? Notes { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// True for the auto-generated "Eu" profile. Default profiles cannot
        /// be deleted (only renamed) so the user always has at least one profile.
        /// </summary>
        public bool IsDefault { get; set; } = false;

        /// <summary>
        /// Reserved for a future "share profile between two accounts" feature
        /// (e.g. husband and wife both see "Mama" profile). Unused for now.
        /// </summary>
        public bool IsShared { get; set; } = false;
    }
}
