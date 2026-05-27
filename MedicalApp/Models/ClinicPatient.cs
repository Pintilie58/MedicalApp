using System.ComponentModel.DataAnnotations;

namespace MedicalApp.Models
{
    /// <summary>
    /// A patient managed by a CAM clinic, identified by the Romanian CNP
    /// (Cod Numeric Personal, 13 digits). The CNP is stored encrypted at rest
    /// (AES, key in User Secrets) because it is sensitive personal data
    /// under GDPR. The display name + email are stored in clear so the
    /// operator's dashboard can list patients without decrypting.
    /// </summary>
    public class ClinicPatient
    {
        [Key]
        public int Id { get; set; }

        /// <summary>FK to <see cref="Clinic.Id"/>. A patient row belongs to ONE clinic.</summary>
        public int ClinicId { get; set; }

        /// <summary>
        /// Last 6 digits of the CNP, in CLEAR. Used as a non-sensitive
        /// search/lookup key on the dashboard. The full CNP lives encrypted
        /// in <see cref="CnpEncrypted"/>. We never display this in the UI;
        /// it is purely an internal index.
        /// </summary>
        [Required]
        [StringLength(13)]
        public string CnpHashKey { get; set; } = string.Empty;

        /// <summary>AES-encrypted full CNP (13 digits). Base64 string.</summary>
        [Required]
        [StringLength(256)]
        public string CnpEncrypted { get; set; } = string.Empty;

        [Required]
        [StringLength(200)]
        public string Name { get; set; } = string.Empty;

        [Required]
        [EmailAddress]
        [StringLength(200)]
        public string Email { get; set; } = string.Empty;

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }
}
