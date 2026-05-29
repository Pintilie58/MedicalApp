namespace MedicalApp.Areas.CAM.Models
{
    /// <summary>
    /// View model pentru landing page-ul CAM (/CAM/Dashboard).
    /// Faza 4: KPIs lifetime + istoric loturi + activitate 30 zile.
    /// </summary>
    public class CamDashboardViewModel
    {
        public string ClinicName { get; set; } = string.Empty;
        public string ClinicCity { get; set; } = string.Empty;
        public string ClinicAddress { get; set; } = string.Empty;

        public int CreditsAvailable { get; set; }

        public bool FoldersCreated { get; set; }
        public DateTime? FoldersCreatedAt { get; set; }

        public string ClinicFolderRoot { get; set; } = string.Empty;
        public string OriginalFolder { get; set; } = string.Empty;
        public string SendsFolder { get; set; } = string.Empty;
        public string SumarFolder { get; set; } = string.Empty;
        public string ErrorsFolder { get; set; } = string.Empty;

        // ----------------- Faza 4 — KPIs lifetime -----------------
        /// <summary>Total fișiere procesate cu succes (aggregat peste TOATE loturile).</summary>
        public int LifetimeFilesInterpreted { get; set; }
        /// <summary>Total emailuri trimise pacienților (aggregat).</summary>
        public int LifetimeFilesSent { get; set; }
        /// <summary>Total Compare PDF-uri atașate (aggregat).</summary>
        public int LifetimeFilesCompared { get; set; }
        /// <summary>Total fișiere NotSends (aggregat).</summary>
        public int LifetimeNotSends { get; set; }
        /// <summary>Total loturi finalizate (orice status terminal).</summary>
        public int TotalBatches { get; set; }
        public int CompletedBatches { get; set; }
        public int FailedBatches { get; set; }
        public int CancelledBatches { get; set; }

        /// <summary>Numărul DISTINCT de pacienți cu cel puțin o analiză înregistrată.</summary>
        public int TotalPatients { get; set; }

        // ----------------- Faza 4 — Top 5 pacienți după nr. analize -----------------
        public List<TopPatientRow> TopPatients { get; set; } = new();

        // ----------------- Faza 4 — Istoric ultimele 20 loturi -----------------
        public List<BatchHistoryRow> RecentBatches { get; set; } = new();

        // ----------------- Faza 4 — Activitate 30 zile (Chart.js bar) -----------------
        /// <summary>Labels — datele ultimelor 30 zile, format "dd MMM".</summary>
        public List<string> ActivityLabels { get; set; } = new();
        /// <summary>Counts — fișiere procesate / zi, aliniat cu ActivityLabels.</summary>
        public List<int> ActivityCounts { get; set; } = new();

        public class TopPatientRow
        {
            public int PatientId { get; set; }
            public string Name { get; set; } = string.Empty;
            public string Email { get; set; } = string.Empty;
            public int AnalysesCount { get; set; }
            public DateTime? LastSamplingDate { get; set; }
        }

        public class BatchHistoryRow
        {
            public int BatchRunId { get; set; }
            public DateTime StartedAt { get; set; }
            public DateTime? FinishedAt { get; set; }
            public string Status { get; set; } = "Running";
            public int TotalFiles { get; set; }
            public int FilesSent { get; set; }
            public int FilesCompared { get; set; }
            public int NotSends { get; set; }
            /// <summary>Format "hh:mm:ss" sau "-" dacă încă rulează.</summary>
            public string DurationDisplay { get; set; } = "-";
        }
    }
}
