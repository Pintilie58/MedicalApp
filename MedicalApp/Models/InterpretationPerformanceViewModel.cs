namespace MedicalApp.Models
{
    /// <summary>
    /// Admin diagnostic view: per-stage timings of the most recent
    /// interpretations, so a slow interpretation can be attributed to a
    /// specific stage (AI call, LOINC matching, PDF, email) instead of guessed.
    /// </summary>
    public class InterpretationPerformanceViewModel
    {
        public List<Row> Rows { get; set; } = new();

        /// <summary>Column order of the timing table (keys used by StageTimer).</summary>
        public string[] StageOrder { get; } =
            { "pdf_extract", "ai_calls", "loinc_match", "pdf_report", "email" };

        public Dictionary<string, string> StageLabels { get; } = new()
        {
            ["pdf_extract"] = "Extracție PDF",
            ["ai_calls"] = "Apel AI (Gemini)",
            ["loinc_match"] = "Matching LOINC",
            ["pdf_report"] = "Generare PDF",
            ["email"] = "Trimitere email"
        };

        public Dictionary<string, long> AverageByStage { get; set; } = new();
        public long AverageTotalMs { get; set; }

        public class Row
        {
            public int HistoryId { get; set; }
            public DateTime CreatedAt { get; set; }
            public string? UserEmail { get; set; }
            public string? FileName { get; set; }
            public string? Status { get; set; }
            public string? ModelUsed { get; set; }
            public long TotalMs { get; set; }
            public int? InputTokens { get; set; }
            public int? OutputTokens { get; set; }
            public Dictionary<string, long> Stages { get; set; } = new();

            /// <summary>How many AI calls this interpretation needed (retries included).</summary>
            public long AiAttempts => Stages.TryGetValue("ai_attempts", out var v) ? v : 1;

            /// <summary>Time not attributed to any measured stage (DB, validation, overhead).</summary>
            public long OtherMs
            {
                get
                {
                    long measured = 0;
                    foreach (var kv in Stages)
                        if (kv.Key != "total" && kv.Key != "ai_attempts") measured += kv.Value;
                    return Math.Max(0, TotalMs - measured);
                }
            }
        }
    }
}
