namespace MedicalApp.Models
{
    /// <summary>Everything the Admin diagnostics page shows, in one shape.</summary>
    public class InfrastructureDiagnosticsViewModel
    {
        // ---- Gemini quota (in-memory, per instance) ----
        public bool QuotaEnabled { get; set; }
        /// <summary>Quota available to THIS instance (project quota / instance count).</summary>
        public int RequestsPerMinute { get; set; }
        public int MaxConcurrentCalls { get; set; }

        /// <summary>The whole Google project quota, as configured.</summary>
        public int ProjectRequestsPerMinute { get; set; }

        /// <summary>How many instances share that quota (Gemini:RateLimit:InstanceCount).</summary>
        public int InstanceCount { get; set; } = 1;
        public int CallsInLastMinute { get; set; }
        public long TotalCalls { get; set; }
        public long ThrottledCalls { get; set; }
        public long TotalWaitMs { get; set; }
        public long Rejections { get; set; }
        public bool CoolingDown { get; set; }

        // ---- Durable interpretation queue (SQL) ----
        public int JobsQueued { get; set; }
        public int JobsRunning { get; set; }
        public int JobsRetried { get; set; }
        public int JobsStale { get; set; }
        public DateTime? OldestJobEnqueuedAt { get; set; }
        public int QueueMaxConcurrent { get; set; }
        public List<JobRow> Jobs { get; set; } = new();

        // ---- LOINC mapping cache (SQL) ----
        public string PipelineVersion { get; set; } = "";
        public bool LoincCacheEnabled { get; set; }
        public int MappingsCurrentVersion { get; set; }
        public int MappingsOtherVersions { get; set; }
        public long MappingReuses { get; set; }
        public DateTime? LastMappingLearnedAt { get; set; }
        public List<MappingRow> TopMappings { get; set; } = new();

        // ---- LOINC service (Python, in-process cache + vocabulary) ----
        public bool ServiceReachable { get; set; }
        public int ServiceCacheSize { get; set; }
        public int ServiceCacheCapacity { get; set; }
        public long ServiceCacheHits { get; set; }
        public long ServiceCacheMisses { get; set; }
        public double ServiceCacheHitRate { get; set; }
        public int VocabularyPhrases { get; set; }
        public DateTime? VocabularyFetchedAt { get; set; }

        public class JobRow
        {
            public int HistoryId { get; set; }
            public string UserEmail { get; set; } = "";
            public string Status { get; set; } = "";
            public int Attempts { get; set; }
            public DateTime EnqueuedAt { get; set; }
            public DateTime? LeaseUntil { get; set; }
            public string? Owner { get; set; }
        }

        public class MappingRow
        {
            public string TestName { get; set; } = "";
            public string? Unit { get; set; }
            public string LoincCode { get; set; } = "";
            public int HitCount { get; set; }
        }
    }
}
