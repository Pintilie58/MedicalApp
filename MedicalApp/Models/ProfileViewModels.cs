using MedicalApp.Models;
using System.ComponentModel.DataAnnotations;

namespace MedicalApp.Models
{
    /// <summary>ViewModel for /Profiles list page.</summary>
    public class ProfilesIndexViewModel
    {
        public List<ProfileRow> Profiles { get; set; } = new();

        public class ProfileRow
        {
            public int Id { get; set; }
            public string Name { get; set; } = string.Empty;
            public string? Relationship { get; set; }
            public string? Gender { get; set; }
            public int? BirthYear { get; set; }
            public string? Notes { get; set; }
            public bool IsDefault { get; set; }
            public DateTime CreatedAt { get; set; }
            public int InterpretationsCount { get; set; }
        }
    }

    /// <summary>Form for Create/Edit profile.</summary>
    public class ProfileFormViewModel
    {
        public int? Id { get; set; }

        [Required(ErrorMessage = "Name is required")]
        [StringLength(100, MinimumLength = 1)]
        public string Name { get; set; } = string.Empty;

        [StringLength(50)]
        public string? Relationship { get; set; }

        [StringLength(10)]
        public string? Gender { get; set; }

        [Range(1900, 2100, ErrorMessage = "Year must be between 1900 and 2100")]
        public int? BirthYear { get; set; }

        [StringLength(500)]
        public string? Notes { get; set; }

        public bool IsDefault { get; set; }
    }
}
