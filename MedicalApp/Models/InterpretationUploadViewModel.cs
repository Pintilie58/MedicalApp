using Microsoft.AspNetCore.Http;
using System.ComponentModel.DataAnnotations;

namespace MedicalApp.Models
{
    public class InterpretationUploadViewModel
    {
        [LocalizedRequired("PdfFileRequired")]
        [Display(Name = "PDF file")]
        public IFormFile? PdfFile { get; set; }
    }
}
