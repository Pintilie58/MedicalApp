using System;
using System.Threading;
using System.Threading.Tasks;
using MedicalApp.Data;
using MedicalApp.Models;
using Microsoft.Extensions.Logging;

namespace MedicalApp.Services
{
    /// <summary>
    /// Records every real Gemini API call (success/error/rejected) into
    /// the dedicated <see cref="AiUsageLog"/> table. Used ONLY by the
    /// Admin "AI usage" dashboard widget — does not touch InterpretationHistories.
    ///
    /// Implementation is fail-safe: any exception thrown while writing the
    /// log row is swallowed and only logged at Warning level, so an AI-usage
    /// bookkeeping failure can never break a real interpretation flow.
    /// </summary>
    public interface IAiUsageLogger
    {
        /// <summary>
        /// Record a single Gemini call. Pass the EFFECTIVE model id (the one
        /// that actually returned tokens, after any Flash→Pro→Plus promotion),
        /// not the configured primary.
        /// </summary>
        Task LogAsync(
            string source,         // "B2C" or "CAM"
            string? userEmail,
            int? clinicId,
            string modelUsed,
            int inputTokens,
            int outputTokens,
            string status,         // "success" | "error" | "rejected"
            string? errorMessage,
            CancellationToken ct = default);
    }

    public sealed class AiUsageLogger : IAiUsageLogger
    {
        private readonly AppDbContext _db;
        private readonly ILogger<AiUsageLogger> _logger;

        public AiUsageLogger(AppDbContext db, ILogger<AiUsageLogger> logger)
        {
            _db = db;
            _logger = logger;
        }

        public async Task LogAsync(
            string source,
            string? userEmail,
            int? clinicId,
            string modelUsed,
            int inputTokens,
            int outputTokens,
            string status,
            string? errorMessage,
            CancellationToken ct = default)
        {
            try
            {
                var row = new AiUsageLog
                {
                    CreatedAt = DateTime.UtcNow,
                    Source = string.IsNullOrWhiteSpace(source) ? "B2C" : source,
                    UserEmail = userEmail,
                    ClinicId = clinicId,
                    ModelUsed = string.IsNullOrWhiteSpace(modelUsed) ? "(unknown)" : modelUsed,
                    InputTokens = inputTokens,
                    OutputTokens = outputTokens,
                    Status = string.IsNullOrWhiteSpace(status) ? "success" : status,
                    ErrorMessage = errorMessage is { Length: > 500 } ? errorMessage[..500] : errorMessage,
                };
                _db.AiUsageLogs.Add(row);
                await _db.SaveChangesAsync(ct);
            }
            catch (Exception ex)
            {
                // Bookkeeping must NEVER block real work.
                _logger.LogWarning(ex,
                    "AiUsageLogger: failed to record usage row for source={Source}, model={Model}",
                    source, modelUsed);
            }
        }
    }
}
