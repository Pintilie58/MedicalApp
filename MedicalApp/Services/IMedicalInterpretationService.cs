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
        /// <returns>
        /// Tuple containing:
        ///   * <c>Result</c>    – deserialized interpretation object
        ///   * <c>InputTokens</c>  – prompt tokens reported by OpenAI
        ///   * <c>OutputTokens</c> – completion tokens reported by OpenAI
        ///   * <c>RawResponse</c>  – raw JSON string exactly as returned by GPT (for debugging)
        /// </returns>
        Task<(InterpretationResult Result, int InputTokens, int OutputTokens, string RawResponse)> InterpretAsync(
            string extractedText, string languageCode, CancellationToken ct = default);
    }
}
