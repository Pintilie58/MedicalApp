using System.ComponentModel.DataAnnotations;

namespace MedicalApp.Models
{
    /// <summary>
    /// The last specimen/method vocabulary successfully fetched from the LOINC
    /// matcher, kept so cache keys stay reproducible even when the Python
    /// service is unreachable (app restart during an outage). One row.
    /// </summary>
    public class LoincVocabularySnapshot
    {
        [Key]
        public int Id { get; set; } = 1;

        /// <summary>JSON array of the phrases, already normalized.</summary>
        public string PhrasesJson { get; set; } = "[]";

        public int PhraseCount { get; set; }

        public DateTime FetchedAt { get; set; }
    }
}
