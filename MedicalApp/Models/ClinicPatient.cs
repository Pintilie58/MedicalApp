using System.ComponentModel.DataAnnotations;

namespace MedicalApp.Models
{
    /// <summary>
    /// A patient managed by a CAM clinic. Identified by a (normalized name +
    /// email) pair — a deliberate, globally portable choice:
    ///   * works identically in RO, IN, RU, US, etc. — no national ID format
    ///     to handle / validate;
    ///   * GDPR-friendly — we do NOT store sensitive national identifiers
    ///     (CNP, SSN, NHS Number, Aadhaar, ...) which are classified as
    ///     "high-risk" personal data;
    ///   * the patient has already been physically identified at the clinic
    ///     with a national ID — once that happens, our app only needs a
    ///     stable lookup key to attach future analyses to the same record.
    ///
    /// Uniqueness within a clinic is enforced by the composite index
    /// (<see cref="ClinicId"/>, <see cref="NameKey"/>, <see cref="Email"/>):
    /// two siblings sharing a family email but with different names are
    /// two distinct patients; the same person re-uploaded twice (same
    /// name + same email) is one patient with two analyses.
    /// </summary>
    public class ClinicPatient
    {
        [Key]
        public int Id { get; set; }

        /// <summary>FK to <see cref="Clinic.Id"/>. A patient row belongs to ONE clinic.</summary>
        public int ClinicId { get; set; }

        /// <summary>
        /// Display name, exactly as it was extracted from the PDF
        /// (e.g. "Ion Popescu", "Иван Петров", "राहुल शर्मा").
        /// </summary>
        [Required]
        [StringLength(200)]
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// Normalized name used as the case-insensitive, diacritic-insensitive,
        /// word-order-insensitive lookup key. Produced by
        /// <c>CamPatientKey.Normalize(name)</c>. Examples:
        ///   "Ion Popescu" → "ion popescu"
        ///   "Popescu Ion" → "ion popescu"
        ///   "Ștefan ȚEPEȘ" → "stefan tepes"
        /// Never displayed in the UI.
        /// </summary>
        [Required]
        [StringLength(220)]
        public string NameKey { get; set; } = string.Empty;

        [Required]
        [EmailAddress]
        [StringLength(200)]
        public string Email { get; set; } = string.Empty;

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }
}
