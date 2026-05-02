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
}
