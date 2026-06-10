namespace MedicalApp.Services
{
    /// <summary>
    /// Thrown when the Gemini API replies with HTTP 404 + a payload signalling
    /// that the configured model id no longer exists ("is no longer available",
    /// "NOT_FOUND" status). This typically happens when Google retires a
    /// preview model (e.g. <c>gemini-3-pro-preview</c> was retired in Feb 2026
    /// in favor of <c>gemini-3.1-pro-preview</c>).
    ///
    /// IMPORTANT: this is NOT a transient error — retrying the SAME model will
    /// never succeed. The CAM batch loop treats it as non-transient so the
    /// pipeline falls through to the next configured tier (or marks the file
    /// as NotSends if no more tiers are available) without burning the rest
    /// of the retry budget.
    /// </summary>
    public class GeminiModelRetiredException : System.InvalidOperationException
    {
        public string RetiredModelId { get; }

        public GeminiModelRetiredException(string retiredModelId, string message)
            : base(message)
        {
            RetiredModelId = retiredModelId;
        }
    }
}
