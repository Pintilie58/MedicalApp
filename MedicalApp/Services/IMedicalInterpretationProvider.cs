using MedicalApp.Models;

namespace MedicalApp.Services
{
    /// <summary>
    /// High-level provider for medical PDF interpretation. Three paths exist:
    ///  - <see cref="InterpretPdfAsync"/> sends the PDF directly to a multimodal model
    ///    (vision path - used by Gemini for image-only / scanned PDFs).
    ///  - <see cref="InterpretTextAsync"/> sends the layout-aware extracted text together with
    ///    optional patient context (preferred Gemini path for digital PDFs - eliminates OCR
    ///    hallucination on values/units/ranges).
    ///  - <see cref="InterpretAsync(string, string, CancellationToken)"/> is the legacy text
    ///    path kept for the OpenAI fallback (no patient context).
    /// Implementations are free to throw <see cref="NotSupportedException"/> on the path they
    /// don't implement.
    /// </summary>
    public interface IMedicalInterpretationProvider
    {
        Task<(InterpretationResult Result, int InputTokens, int OutputTokens, string RawResponse)> InterpretPdfAsync(
            Stream pdfStream, string fileName, string languageCode,
            PatientContext? patientContext = null, CancellationToken ct = default);

        Task<(InterpretationResult Result, int InputTokens, int OutputTokens, string RawResponse)> InterpretAsync(
            string extractedText, string languageCode, CancellationToken ct = default);

        Task<(InterpretationResult Result, int InputTokens, int OutputTokens, string RawResponse)> InterpretTextAsync(
            string extractedText, string fileName, string languageCode,
            PatientContext? patientContext = null, CancellationToken ct = default);
    }
}
