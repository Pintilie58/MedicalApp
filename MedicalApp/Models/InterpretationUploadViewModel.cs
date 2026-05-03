using Microsoft.AspNetCore.Http;
using System.ComponentModel.DataAnnotations;

namespace MedicalApp.Models
{
    public class InterpretationUploadViewModel
    {
        [LocalizedRequired("PdfFileRequired")]
        [Display(Name = "PDF file")]
        public IFormFile? PdfFile { get; set; }

        /// <summary>Id of the Profile (Eu / Mama / Tata...) the interpretation is for.</summary>
        [Required(ErrorMessage = "Te rugăm să selectezi profilul pentru care faci interpretarea.")]
        public int? ProfileId { get; set; }

        /// <summary>Populated in GET Upload - list of the user's profiles for the dropdown.</summary>
        public List<ProfileOption> AvailableProfiles { get; set; } = new();

        public class ProfileOption
        {
            public int Id { get; set; }
            public string Name { get; set; } = string.Empty;
            public bool IsDefault { get; set; }
        }
    }

    /// <summary>
    /// Shown when the uploaded PDF exactly matches (SHA-256) an already-successful
    /// interpretation for the same user and same profile. Lets the user either
    /// reuse the existing report for free or force a fresh re-interpretation
    /// (consuming 1 credit).
    /// </summary>
    public class DuplicateDetectedViewModel
    {
        public int ExistingHistoryId { get; set; }
        public DateTime ExistingCreatedAt { get; set; }
        public string? ExistingFileName { get; set; }
        public int ProfileId { get; set; }
        public string ProfileName { get; set; } = string.Empty;
        public string OriginalFileName { get; set; } = string.Empty;

        /// <summary>
        /// Server-side cache key pointing to the uploaded PDF bytes kept in session
        /// until the user picks reuse-existing or force-reinterpret.
        /// </summary>
        public string ReuploadToken { get; set; } = string.Empty;
    }
}
