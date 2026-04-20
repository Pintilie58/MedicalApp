using MedicalApp.Models;

namespace MedicalApp.Services
{
    public interface IMedicalInterpretationService
    {
        /// <summary>
        /// Sends the extracted PDF text to GPT-4o-mini and returns a structured interpretation.
        /// </summary>
        /// <param name="extractedText">Raw text extracted from the PDF.</param>
        /// <param name="languageCode">Two-letter language code (en, ro, fr, es, de).</param>
        /// <returns>Parsed interpretation result + token usage stats.</returns>
        Task<(InterpretationResult Result, int InputTokens, int OutputTokens)> InterpretAsync(
            string extractedText, string languageCode, CancellationToken ct = default);
    }
}
