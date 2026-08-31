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

        /// <summary>True when at least one row was produced by the 3-call split pipeline.</summary>
        public bool AnySplit => Rows.Any(r => r.IsSplit);

        /// <summary>Models configured per split stage (for the cost breakdown header).</summary>
        public string SplitModelA { get; set; } = "";
        public string SplitModelB { get; set; } = "";
        public string SplitModelC { get; set; } = "";

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

            /// <summary>Output tokens the model spent "thinking" instead of answering.</summary>
            public long ThinkingTokens => Stages.TryGetValue("ai_thinking_tokens", out var v) ? v : 0;

            /// <summary>Time not attributed to any measured stage (DB, validation, overhead).</summary>
            public long OtherMs
            {
                get
                {
                    long measured = 0;
                    foreach (var kv in Stages)
                    {
                        // "total" is the sum itself; the counters below are not
                        // milliseconds — they must never be subtracted here.
                        if (kv.Key is "total" or "ai_attempts" or "ai_thinking_tokens") continue;
                        if (kv.Key.StartsWith("ai_a_") || kv.Key.StartsWith("ai_b_")
                            || kv.Key.StartsWith("ai_c_") || kv.Key is "ai_bc_ms" or "ai_pipeline") continue;
                        measured += kv.Value;
                    }
                    return Math.Max(0, TotalMs - measured);
                }
            }

            // ---------------- Split pipeline (3 calls) breakdown ----------------
            private long S(string key) => Stages.TryGetValue(key, out var v) ? v : 0;

            /// <summary>This interpretation was produced by the A/B/C split pipeline.</summary>
            public bool IsSplit => S("ai_pipeline") == 1;

            public long StageAMs => S("ai_a_ms");
            /// <summary>Wall clock of stages B and C, which run concurrently.</summary>
            public long StageBcMs => S("ai_bc_ms");
            public long StageABatches => S("ai_b_batches");

            /// <summary>Analytes recovered by the parallel completeness sweep (stage A2).</summary>
            public long SweepRecovered => S("ai_s_recovered");
            public int SweepIn => (int)S("ai_s_in");
            public int SweepOut => (int)S("ai_s_out");

            public int StageAIn => (int)S("ai_a_in");
            public int StageAOut => (int)S("ai_a_out");
            public int StageBIn => (int)S("ai_b_in");
            public int StageBOut => (int)S("ai_b_out");
            public int StageCIn => (int)S("ai_c_in");
            public int StageCOut => (int)S("ai_c_out");
            public long StageCThinking => S("ai_c_think");

            /// <summary>Estimated USD per stage, filled by the controller from GeminiPricing.</summary>
            public decimal CostAUsd { get; set; }
            public decimal CostBUsd { get; set; }
            public decimal CostCUsd { get; set; }
            public decimal CostTotalUsd => CostAUsd + CostBUsd + CostCUsd + CostSweepUsd;

            /// <summary>Estimated USD of the completeness sweep (stage A2).</summary>
            public decimal CostSweepUsd { get; set; }

            /// <summary>Estimated USD of a monolithic row, from its own token counts.</summary>
            public decimal CostMonoUsd { get; set; }

            /// <summary>What this interpretation cost, whichever pipeline produced it.</summary>
            public decimal CostUsd => IsSplit ? CostTotalUsd : CostMonoUsd;

            /// <summary>How many parallel calls the extraction was split into.</summary>
            public long StageAChunks => S("ai_a_chunks");
        }
    }
}
