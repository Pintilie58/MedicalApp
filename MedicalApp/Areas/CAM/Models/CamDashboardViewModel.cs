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

        // ----------------- Disk usage (Sends/Sumar/Errors/Original) -----------------
        public long DiskBytesTotal { get; set; }
        public int DiskFilesTotal { get; set; }
        public long DiskBytesSends { get; set; }
        public long DiskBytesSumar { get; set; }
        public long DiskBytesErrors { get; set; }
        public long DiskBytesOriginal { get; set; }
        public int RetentionDaysDefault { get; set; }

        public string DiskHumanTotal
        {
            get
            {
                double s = DiskBytesTotal;
                string[] units = { "B", "KB", "MB", "GB" };
                int i = 0;
                while (s >= 1024 && i < units.Length - 1) { s /= 1024; i++; }
                return $"{s:0.##} {units[i]}";
            }
        }

        /// <summary>UI alert level: 0=ok, 1=warn, 2=critical.</summary>
        public int DiskWarnLevel
        {
            get
            {
                // 500 MB or 1000 files → yellow; 2 GB or 5000 files → red.
                if (DiskBytesTotal > 2L * 1024 * 1024 * 1024 || DiskFilesTotal > 5000) return 2;
                if (DiskBytesTotal > 500L * 1024 * 1024 || DiskFilesTotal > 1000) return 1;
                return 0;
            }
        }
        /// <summary>Statistici loturi pentru anul curent.</summary>
        public BatchPeriodStats YearStats { get; set; } = new();
        /// <summary>Statistici loturi pentru luna curentă.</summary>
        public BatchPeriodStats MonthStats { get; set; } = new();

        // ----------------- Faza 4 — Istoric ultimele 20 loturi -----------------
        public List<BatchHistoryRow> RecentBatches { get; set; } = new();

        // ----------------- A5 — Alertă lot recent cu trimiteri eșuate -----------------
        /// <summary>
        /// Set when the clinic's MOST RECENT completed batch had at least one email
        /// failure. Drives the warning banner at the top of /CAM/Dashboard so the
        /// operator notices even if they were away from the screen when the batch
        /// finished. Cleared automatically when the next batch finishes cleanly.
        /// </summary>
        public RecentBatchEmailIssues? RecentEmailIssues { get; set; }

        public class RecentBatchEmailIssues
        {
            public int BatchRunId { get; set; }
            public DateTime FinishedAt { get; set; }
            public int NotSends { get; set; }
            public int TotalFiles { get; set; }
            /// <summary>Up to 3 specific reasons taken from ClinicBatchErrors (newest first).</summary>
            public List<string> SampleReasons { get; set; } = new();
        }

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
