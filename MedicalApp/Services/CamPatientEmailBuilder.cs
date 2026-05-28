using System.Net;
using MedicalApp.Models;

namespace MedicalApp.Services
{
    /// <summary>
    /// Build the patient-facing email sent by the CAM batch runner. The
    /// branding is dual: the clinic's name is the visual hero (so the
    /// patient knows where the results came from), with a small footer
    /// crediting MedicalApp+ for the AI interpretation.
    /// </summary>
    public static class CamPatientEmailBuilder
    {
        /// <summary>
        /// Returns the email subject. Convention: "Rezultate analize - {Clinic}".
        /// </summary>
        public static string BuildSubject(Clinic clinic) =>
            $"Rezultate analize - {clinic.Name}";

        public static string BuildHtml(
            Clinic clinic,
            ClinicPatient patient,
            bool hasInterpretation,
            bool hasCompareReport,
            string? originalFileName)
        {
            var clinicName = WebUtility.HtmlEncode(clinic.Name);
            var clinicCity = WebUtility.HtmlEncode(clinic.City);
            var clinicAddress = WebUtility.HtmlEncode(clinic.Address);
            var patientName = WebUtility.HtmlEncode(patient.Name);
            var origFile = WebUtility.HtmlEncode(originalFileName ?? "analiza.pdf");

            string attachmentsLine;
            if (hasCompareReport)
            {
                attachmentsLine =
                    "Atașat găsești <strong>3 documente</strong>: " +
                    "<em>" + origFile + "</em> (buletinul original), " +
                    "<em>Raport_Interpretare.pdf</em> (interpretarea medicală AI) și " +
                    "<em>Raport_Comparatie.pdf</em> (comparație cu analizele tale anterioare).";
            }
            else if (hasInterpretation)
            {
                attachmentsLine =
                    "Atașat găsești <strong>2 documente</strong>: " +
                    "<em>" + origFile + "</em> (buletinul original) și " +
                    "<em>Raport_Interpretare.pdf</em> (interpretarea medicală AI).";
            }
            else
            {
                attachmentsLine = "Atașat găsești buletinul original: <em>" + origFile + "</em>.";
            }

            return $@"
<div style=""font-family:Arial,Helvetica,sans-serif;max-width:640px;margin:0 auto;padding:0;background:#ffffff;"">
  <div style=""background:#0d47a1;color:#ffffff;padding:24px;border-radius:10px 10px 0 0;text-align:center;"">
    <div style=""font-size:13px;opacity:0.85;letter-spacing:0.06em;text-transform:uppercase;margin-bottom:4px;"">
      Rezultate analize medicale
    </div>
    <h1 style=""margin:0;font-size:24px;font-weight:700;"">{clinicName}</h1>
    <div style=""font-size:13px;opacity:0.9;margin-top:6px;"">{clinicAddress}, {clinicCity}</div>
  </div>

  <div style=""padding:28px 24px;color:#212529;font-size:15px;line-height:1.6;border:1px solid #e9ecef;border-top:0;"">
    <p style=""margin:0 0 14px 0;font-size:16px;"">
      Bună, <strong>{patientName}</strong>!
    </p>

    <p style=""margin:0 0 14px 0;"">
      Clinica <strong>{clinicName}</strong> ți-a trimis rezultatele analizelor medicale,
      însoțite de o interpretare detaliată realizată de motorul AI medical
      al aplicației <strong>MedicalApp+</strong>.
    </p>

    <p style=""margin:0 0 14px 0;"">
      {attachmentsLine}
    </p>

    <div style=""background:#eef5ff;border-left:4px solid #0d47a1;padding:14px 18px;border-radius:6px;margin:20px 0;font-size:14px;"">
      <strong style=""color:#0d47a1;"">Notă importantă:</strong>
      interpretarea AI este un instrument informativ și NU înlocuiește
      consultul medical. Pentru orice valoare anormală sau întrebare,
      consultă medicul tău curant.
    </div>

    <p style=""margin:20px 0 0 0;color:#6c757d;font-size:13px;"">
      Acest email a fost trimis automat la cererea clinicii.
      Dacă nu recunoști solicitarea, te rugăm să contactezi {clinicName}.
    </p>
  </div>

  <div style=""background:#f1f5fb;color:#0d47a1;padding:14px 24px;border-radius:0 0 10px 10px;text-align:center;font-size:12px;border:1px solid #e9ecef;border-top:0;"">
    Powered by
    <a href=""https://medicalapp.ro"" style=""color:#0d47a1;font-weight:700;text-decoration:none;"">MedicalApp+</a>
    &mdash; interpretare AI a analizelor medicale &mdash;
    <a href=""https://medicalapp.ro"" style=""color:#0d47a1;text-decoration:none;"">medicalapp.ro</a>
  </div>
</div>";
        }
    }
}
