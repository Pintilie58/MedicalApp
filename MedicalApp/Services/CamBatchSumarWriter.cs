using System.Text;
using MedicalApp.Models;

namespace MedicalApp.Services
{
    /// <summary>
    /// Writes the per-batch session summary as a plain UTF-8 .txt file
    /// in the clinic's <c>Sumar</c> folder. Name convention:
    /// <c>Sum_yyyyMMdd_HHmm.txt</c>. Used by the operator to keep a hard
    /// record of each batch run (file count, errors, durations).
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
            sb.AppendLine($"  Sumar Lot — {clinic.Name}");
            sb.AppendLine("=========================================");
            sb.AppendLine();
            sb.AppendLine($"Pornit:    {batch.StartedAt.ToLocalTime():dd MMM yyyy HH:mm:ss}");
            sb.AppendLine($"Finalizat: {batch.FinishedAt?.ToLocalTime():dd MMM yyyy HH:mm:ss}");
            sb.AppendLine($"Status:    {batch.Status}");
            sb.AppendLine($"Durată:    {(batch.FinishedAt - batch.StartedAt)?.ToString(@"hh\:mm\:ss") ?? "-"}");
            sb.AppendLine();
            sb.AppendLine("--- Statistici ---");
            sb.AppendLine($"  Fișiere procesate (cu succes):   {batch.FilesInterpreted}");
            sb.AppendLine($"  Emailuri trimise pacienților:    {batch.FilesSent}");
            sb.AppendLine($"  Comparații atașate (≥2 analize): {batch.FilesCompared}");
            sb.AppendLine($"  Fișiere NEtrimise (NotSends):    {batch.NotSends}");
            sb.AppendLine($"  Total fișiere în lot:            {batch.TotalFiles}");
            sb.AppendLine();

            if (errors.Count > 0)
            {
                sb.AppendLine("--- NotSends (motive) ---");
                foreach (var e in errors)
                {
                    var patient = string.IsNullOrWhiteSpace(e.PatientName) ? "?" : e.PatientName;
                    sb.AppendLine($"  • [{e.OccurredAt.ToLocalTime():HH:mm:ss}] {e.FileName}  →  pacient: {patient}");
                    sb.AppendLine($"      Motiv: {e.Reason}");
                    sb.AppendLine($"      Încercări: {e.RetryCount}");
                }
                sb.AppendLine();
            }

            sb.AppendLine("---");
            sb.AppendLine("Generat automat de MedicalApp+ — medicalapp.ro");

            Directory.CreateDirectory(sumarFolder);
            File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
            return path;
        }
    }
}
