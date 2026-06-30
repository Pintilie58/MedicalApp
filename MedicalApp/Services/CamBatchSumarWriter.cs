using System.Text;
using MedicalApp.Models;

namespace MedicalApp.Services
{
    /// <summary>
    /// Writes the per-batch session summary as a plain UTF-8 .txt file
    /// in the clinic's <c>Sumar</c> folder. Name convention:
    /// <c>Sum_yyyyMMdd_HHmm.txt</c>. Used by the operator to keep a hard
    /// record of each batch run (file count, errors, durations).
    ///
    /// Labels follow the operator's current UI culture via <see cref="Loc"/>.
    /// </summary>
    public static class CamBatchSumarWriter
    {
        public static string Write(
            ClinicBatchRun batch,
            Clinic clinic,
            List<ClinicBatchError> errors,
            string sumarFolder)
        {
            var fileName = $"Sum_{batch.StartedAt.ToLocalTime():yyyyMMdd_HHmm}.txt";
            var path = Path.Combine(sumarFolder, fileName);

            var sb = new StringBuilder();
            sb.AppendLine("=========================================");
            sb.AppendLine($"  {Loc.T("SumarPdfTitle")} — {clinic.Name}");
            sb.AppendLine("=========================================");
            sb.AppendLine();
            sb.AppendLine($"{Loc.T("SumarPdfStartedLabel")}{batch.StartedAt.ToLocalTime():dd MMM yyyy HH:mm:ss}");
            sb.AppendLine($"{Loc.T("SumarPdfFinishedLabel")}{batch.FinishedAt?.ToLocalTime():dd MMM yyyy HH:mm:ss}");
            sb.AppendLine($"{Loc.T("SumarTxtStatusLabel")}{batch.Status}");
            sb.AppendLine($"{Loc.T("SumarPdfDurationLabel")}{(batch.FinishedAt - batch.StartedAt)?.ToString(@"hh\:mm\:ss") ?? "-"}");
            sb.AppendLine();
            sb.AppendLine(Loc.T("SumarTxtStatsHeader"));
            sb.AppendLine($"  {Loc.T("SumarPdfKpiSuccess")}: {batch.FilesInterpreted}");
            sb.AppendLine($"  {Loc.T("SumarPdfKpiSent")}: {batch.FilesSent}");
            sb.AppendLine($"  {Loc.T("SumarPdfKpiCompare")}: {batch.FilesCompared}");
            sb.AppendLine($"  {Loc.T("SumarPdfKpiNotSent")}: {batch.NotSends}");
            sb.AppendLine($"  {Loc.T("SumarPdfTotalFilesLabel")}{batch.TotalFiles}");
            sb.AppendLine();

            if (errors.Count > 0)
            {
                sb.AppendLine(Loc.T("SumarTxtNotSendsHeader"));
                foreach (var e in errors)
                {
                    var patient = string.IsNullOrWhiteSpace(e.PatientName) ? "?" : e.PatientName;
                    sb.AppendLine($"  • [{e.OccurredAt.ToLocalTime():HH:mm:ss}] {e.FileName}  →  {Loc.T("SumarTxtPatientLabel")}{patient}");
                    sb.AppendLine($"      {Loc.T("SumarPdfErrColReason")}: {e.Reason}");
                    sb.AppendLine($"      {Loc.T("SumarTxtTriesLabel")}{e.RetryCount}");
                }
                sb.AppendLine();
            }

            sb.AppendLine("---");
            sb.AppendLine($"{Loc.T("SumarPdfFooterGenerated")}medicalapp.ro");

            Directory.CreateDirectory(sumarFolder);
            File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
            return path;
        }
    }
}
