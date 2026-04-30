namespace MedicalApp.Services
{
    /// <summary>
    /// Top-level interpretation flow toggle.
    /// "Gemini" (default) -> sends the PDF directly to Google Gemini 2.5 Flash.
    /// "OpenAI"           -> extracts text via PdfPig and sends it to OpenAI GPT-4o-mini.
    /// </summary>
    public class InterpretationSettings
    {
        public string Provider { get; set; } = "Gemini";
    }
}
