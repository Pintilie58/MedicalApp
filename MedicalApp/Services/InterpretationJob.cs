namespace MedicalApp.Services
{
    /// <summary>
    /// One B2C interpretation queued for background execution. Everything the
    /// pipeline needs is captured HERE, at enqueue time, because the background
    /// worker has no HttpContext, no session and no request-scoped DbContext.
    /// </summary>
    /// <param name="HistoryId">
    /// Row already inserted in <c>InterpretationHistories</c> with
    /// Status = "processing" and the credit already reserved. The runner only
    /// updates it — so the archive shows "în lucru" the second the user submits.
    /// </param>
    public sealed record InterpretationJob(
        int HistoryId,
        string UserEmail,
        int ProfileId,
        string ProfileName,
        byte[] PdfBytes,
        string OriginalFileName,
        string PdfHash,
        string LanguageCode,
        bool Force,
        string? ProgressToken);
}
