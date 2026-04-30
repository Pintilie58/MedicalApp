using MedicalApp.Models;

namespace MedicalApp.Services
{
    /// <summary>
    /// High-level provider for medical PDF interpretation. Two paths exist:
    ///  - <see cref="InterpretPdfAsync"/> sends the PDF directly to a multimodal model
    ///    (used by the Gemini provider, which natively handles PDFs).
    ///  - <see cref="InterpretAsync(string, string, CancellationToken)"/> sends already-extracted
    ///    text to a text-only model (used by the OpenAI fallback, which keeps PdfPig).
    /// Implementations are free to throw <see cref="NotSupportedException"/> on the path they
    /// don't implement.
    /// </summary>
    public interface IMedicalInterpretationProvider
    {
        Task<(InterpretationResult Result, int InputTokens, int OutputTokens, string RawResponse)> InterpretPdfAsync(
            Stream pdfStream, string fileName, string languageCode, CancellationToken ct = default);

        Task<(InterpretationResult Result, int InputTokens, int OutputTokens, string RawResponse)> InterpretAsync(
            string extractedText, string languageCode, CancellationToken ct = default);
    }
}
