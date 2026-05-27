using System.ComponentModel.DataAnnotations;

namespace MedicalApp.Models
{
    /// <summary>
    /// One row per registered Medical Analysis Clinic (CAM).
    /// Each Clinic is linked 1:1 to a <see cref="User"/> row whose
    /// <c>UserType</c> equals <c>"Clinic"</c>. The clinic-specific
    /// business data (name, address, local folder bookkeeping) lives here so
    /// the existing Users table stays lean.
    /// </summary>
    public class Clinic
    {
        [Key]
        public int Id { get; set; }

        /// <summary>FK to <see cref="User.Email"/>. One clinic per user account.</summary>
        [Required]
        [StringLength(200)]
        public string UserEmail { get; set; } = string.Empty;

        [Required]
        [StringLength(200)]
        public string Name { get; set; } = string.Empty;

        [Required]
        [StringLength(100)]
        public string City { get; set; } = string.Empty;

        [Required]
        [StringLength(300)]
        public string Address { get; set; } = string.Empty;

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// Set once, on the very first credit purchase made by this clinic,
        /// at the moment we create the on-disk folder structure
        /// (<c>Original</c>, <c>Sends</c>, <c>Sumar</c>, <c>Errors</c>).
        /// Stays NULL until then. The presence of this value is how we tell
        /// "first purchase already happened" without scanning Purchases.
        /// </summary>
        public DateTime? FoldersCreatedAt { get; set; }
    }
}
