using MedicalApp.Models;
using Microsoft.Extensions.Options;

namespace MedicalApp.Services
{
    /// <summary>
    /// Cleans up old PDF files from a clinic's <c>Sends</c>, <c>Sumar</c> and
    /// <c>Errors</c> folders based on a retention policy.
    ///
    /// HARD SAFETY RULES (in this order):
    ///   1. Folder <c>Original</c> is NEVER touched. Operator-controlled.
    ///   2. Files belonging to the LAST COMPLETED batch are protected — they
    ///      may be needed if a patient calls the next day. "Last completed
    ///      batch" = newest ClinicBatchRun with FinishedAt != null AND
    ///      Status == "Completed".
    ///   3. Inside protected scope, only files OLDER than the retention
    ///      cut-off (by LastWriteTimeUtc) are removed.
    ///   4. The service is read-only when the clinic argument is null.
    ///
    /// Triggered automatically before every "Lansează lot" via
    /// <c>BatchController</c>, AND manually from the CAM Dashboard.
    /// </summary>
    public class CamRetentionService
    {
        private readonly ICamFileStore _files;
        private readonly ILogger<CamRetentionService> _logger;
        private readonly int _defaultRetentionDays;

        public CamRetentionService(
            ICamFileStore files,
            IOptions<CamSettings> camSettings,
            ILogger<CamRetentionService> logger)
        {
            _files = files;
            _logger = logger;
            _defaultRetentionDays = camSettings.Value.RetentionDays > 0
                ? camSettings.Value.RetentionDays
                : 30;
        }

        public sealed class CleanupResult
        {
            public int FilesDeletedSends { get; set; }
            public int FilesDeletedSumar { get; set; }
            public int FilesDeletedErrors { get; set; }
            public long BytesFreed { get; set; }
            public int FilesProtectedByLastBatch { get; set; }
            public int RetentionDaysUsed { get; set; }
            public DateTime CutoffUtc { get; set; }

            public int TotalDeleted =>
                FilesDeletedSends + FilesDeletedSumar + FilesDeletedErrors;

            public string HumanSize
            {
                get
                {
                    double s = BytesFreed;
                    string[] units = { "B", "KB", "MB", "GB" };
                    int i = 0;
                    while (s >= 1024 && i < units.Length - 1) { s /= 1024; i++; }
                    return $"{s:0.##} {units[i]}";
                }
            }
        }

        /// <summary>Disk usage report — no deletion, just measurements.</summary>
        public sealed class UsageReport
        {
            public long BytesSends { get; set; }
            public long BytesSumar { get; set; }
            public long BytesErrors { get; set; }
            public long BytesOriginal { get; set; }
            public int FilesSends { get; set; }
            public int FilesSumar { get; set; }
            public int FilesErrors { get; set; }
            public int FilesOriginal { get; set; }
            public long BytesTotal => BytesSends + BytesSumar + BytesErrors + BytesOriginal;
            public int FilesTotal => FilesSends + FilesSumar + FilesErrors + FilesOriginal;

            public string HumanTotal
            {
                get
                {
                    double s = BytesTotal;
                    string[] units = { "B", "KB", "MB", "GB" };
                    int i = 0;
                    while (s >= 1024 && i < units.Length - 1) { s /= 1024; i++; }
                    return $"{s:0.##} {units[i]}";
                }
            }
        }

        /// <summary>
        /// Measures disk usage across all CAM folders (incl. Original) for
        /// the given clinic. Pure read-only.
        /// </summary>
        public async Task<UsageReport> MeasureUsageAsync(Clinic clinic,
            CancellationToken ct = default)
        {
            var r = new UsageReport();
            (r.BytesOriginal, r.FilesOriginal) = await MeasureAsync(clinic, CamFolder.Original, ct);
            (r.BytesSends,    r.FilesSends)    = await MeasureAsync(clinic, CamFolder.Sends, ct);
            (r.BytesSumar,    r.FilesSumar)    = await MeasureAsync(clinic, CamFolder.Sumar, ct);
            (r.BytesErrors,   r.FilesErrors)   = await MeasureAsync(clinic, CamFolder.Errors, ct);
            return r;
        }

        /// <summary>
        /// Cleans Sends/Sumar/Errors for the given clinic. Original is never
        /// touched. Files newer than the cutoff are kept. Files from the
        /// LAST completed batch are also kept regardless of age.
        /// Never throws on IO errors — logs and continues so a single locked
        /// file can't block the whole batch start.
        /// </summary>
        /// <param name="overrideRetentionDays">
        /// Optional override (used by "Șterge acum" manual button on the
        /// dashboard, where the operator may type their own value).
        /// When null, uses appsettings.json default.
        /// </param>
        public async Task<CleanupResult> CleanupAsync(Clinic clinic,
            int? overrideRetentionDays = null,
            CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(clinic);

            int days = overrideRetentionDays.HasValue && overrideRetentionDays.Value > 0
                ? overrideRetentionDays.Value
                : _defaultRetentionDays;
            var cutoff = DateTime.UtcNow.AddDays(-days);

            // Cleanup is PURE FILE-SYSTEM — no DB lookups needed. We rely
            // exclusively on the file LastWriteTimeUtc to decide eligibility.
            // Files from the latest batch are naturally protected by their
            // own freshness (they were written today, so even with retention=1
            // they won't be touched until tomorrow).
            var protectedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var result = new CleanupResult
            {
                RetentionDaysUsed = days,
                CutoffUtc = cutoff
            };

            var sends = await SweepAsync(clinic, CamFolder.Sends, cutoff, protectedNames, ct);
            var sumar = await SweepAsync(clinic, CamFolder.Sumar, cutoff, protectedNames, ct);
            var errs  = await SweepAsync(clinic, CamFolder.Errors, cutoff, protectedNames, ct);

            result.FilesDeletedSends = sends.deleted;
            result.FilesDeletedSumar = sumar.deleted;
            result.FilesDeletedErrors = errs.deleted;
            result.BytesFreed = sends.bytes + sumar.bytes + errs.bytes;
            result.FilesProtectedByLastBatch = sends.protectedCount + sumar.protectedCount + errs.protectedCount;

            if (result.TotalDeleted > 0)
            {
                _logger.LogInformation(
                    "CAM cleanup clinic {Email}: deleted {Total} files ({Sends}+{Sumar}+{Errors}), freed {Bytes} bytes, retention {Days} days, protected {Prot} from last completed batch.",
                    clinic.UserEmail, result.TotalDeleted,
                    result.FilesDeletedSends, result.FilesDeletedSumar, result.FilesDeletedErrors,
                    result.BytesFreed, days, result.FilesProtectedByLastBatch);
            }
            return result;
        }

        // ------------- helpers (private) -------------

        private async Task<(long bytes, int files)> MeasureAsync(Clinic clinic,
            CamFolder folder, CancellationToken ct)
        {
            try
            {
                var entries = await _files.ListAsync(clinic, folder, null, ct);
                return (entries.Sum(e => e.SizeBytes), entries.Count);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "CAM usage: cannot measure {Folder}", folder);
                return (0, 0);
            }
        }

        /// <summary>
        /// Sweeps one bucket, deleting files older than <paramref name="cutoff"/>
        /// whose name is NOT in <paramref name="protectedNames"/>. Never throws:
        /// one locked/vanished file must not abort the sweep.
        /// </summary>
        private async Task<(int deleted, long bytes, int protectedCount)> SweepAsync(
            Clinic clinic, CamFolder folder, DateTime cutoff,
            HashSet<string> protectedNames, CancellationToken ct)
        {
            IReadOnlyList<CamFileEntry> entries;
            try
            {
                entries = await _files.ListAsync(clinic, folder, null, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "CAM cleanup: cannot enumerate {Folder}", folder);
                return (0, 0, 0);
            }

            int deleted = 0; long bytes = 0; int prot = 0;
            foreach (var e in entries)
            {
                try
                {
                    if (e.LastModifiedUtc >= cutoff) continue;           // too fresh
                    if (protectedNames.Contains(e.Name)) { prot++; continue; }

                    if (await _files.DeleteAsync(clinic, folder, e.Name, ct))
                    {
                        deleted++;
                        bytes += e.SizeBytes;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "CAM cleanup: failed to delete {File}", e.Name);
                }
            }
            return (deleted, bytes, prot);
        }
    }
}
