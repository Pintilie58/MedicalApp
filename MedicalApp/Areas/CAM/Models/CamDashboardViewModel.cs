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

        // ----------------- Loturi pe perioade (an curent + luna curentă) -----------------
        /// <summary>Anul curent (server UTC), folosit pentru titlul rândului 1.</summary>
        public int CurrentYear { get; set; }
        /// <summary>Eticheta lunii curente, ex. "Mai-2026". Folosit pentru titlul rândului 2.</summary>
        public string CurrentMonthLabel { get; set; } = string.Empty;
        /// <summary>Anul selectat curent în dropdown (default = anul curent UTC).</summary>
        public int SelectedYear { get; set; }
        /// <summary>Luna selectată în dropdown 1..12 (default = luna curentă UTC).</summary>
        public int SelectedMonth { get; set; }
        /// <summary>Anii care apar în dropdown — extrași din DB, desc. Mereu include și anul curent.</summary>
        public List<int> AvailableYears { get; set; } = new();
        /// <summary>Statistici loturi pentru anul curent.</summary>
        public BatchPeriodStats YearStats { get; set; } = new();
        /// <summary>Statistici loturi pentru luna curentă.</summary>
        public BatchPeriodStats MonthStats { get; set; } = new();

        // ----------------- Faza 4 — Istoric ultimele 20 loturi -----------------
        public List<BatchHistoryRow> RecentBatches { get; set; } = new();

        public class BatchPeriodStats
        {
            public int Total { get; set; }
            public int Completed { get; set; }
            public int Failed { get; set; }
            public int Cancelled { get; set; }
            /// <summary>Pacienți distincți cu cel puțin o analiză în această perioadă.</summary>
            public int Patients { get; set; }
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
