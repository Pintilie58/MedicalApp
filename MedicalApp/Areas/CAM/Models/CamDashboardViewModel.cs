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

        // ----------------- Loturi pe an / lună (anul & luna curentă) -----------------
        /// <summary>Total loturi rulate în anul curent (UTC).</summary>
        public int BatchesThisYear { get; set; }
        /// <summary>Anul curent (server UTC), folosit pentru afișaj în card.</summary>
        public int CurrentYear { get; set; }
        /// <summary>Total loturi rulate în luna curentă (UTC).</summary>
        public int BatchesThisMonth { get; set; }
        /// <summary>Eticheta lunii curente, ex. "Mai-2026".</summary>
        public string CurrentMonthLabel { get; set; } = string.Empty;

        // ----------------- Faza 4 — Istoric ultimele 20 loturi -----------------
        public List<BatchHistoryRow> RecentBatches { get; set; } = new();

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
