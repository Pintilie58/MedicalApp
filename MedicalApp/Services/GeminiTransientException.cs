namespace MedicalApp.Services
{
    /// <summary>
    /// Marks errors coming from the Gemini API that are TRANSIENT (server-side overload,
    /// rate-limiting, etc.) so the controller's retry loop can apply a longer backoff
    /// and a higher attempt count for them.
    ///
    /// Mapped from HTTP statuses: 429 (RESOURCE_EXHAUSTED) and 503 (UNAVAILABLE).
    /// </summary>
    public class GeminiTransientException : System.InvalidOperationException
    {
        public int HttpStatusCode { get; }

        public GeminiTransientException(int statusCode, string message)
            : base(message)
        {
            HttpStatusCode = statusCode;
        }
    }
}
