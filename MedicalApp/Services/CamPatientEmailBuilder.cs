using System.Net;
using MedicalApp.Models;

namespace MedicalApp.Services
{
    /// <summary>
    /// Build the patient-facing email sent by the CAM batch runner. The
    /// branding is dual: the clinic's name is the visual hero (so the
    /// patient knows where the results came from), with a small footer
    /// crediting MedicalApp+ for the AI interpretation.
    ///
    /// LANGUAGE: every visible string goes through <see cref="Loc.T(string)"/>,
    /// which resolves against <c>CultureInfo.CurrentUICulture</c>. The CAM
    /// batch runner (<see cref="CamBatchService.RunAsync"/>) sets that
    /// culture once, at the start of each batch, from the operator's chosen
    /// language. So a single batch is internally consistent: the
    /// interpretation PDF, the comparison PDF AND this email all follow the
    /// same language without each call site needing to pass <c>lang</c>
    /// around.
    /// </summary>
    public static class CamPatientEmailBuilder
    {
        /// <summary>
        /// Returns the email subject. Convention: "Lab results - {Clinic}"
        /// (translated to the operator's language).
        /// </summary>
        public static string BuildSubject(Clinic clinic) =>
            string.Format(Loc.T("CamEmailSubject"), clinic.Name);

        public static string BuildHtml(
            Clinic clinic,
            ClinicPatient patient,
            bool hasInterpretation,
            bool hasCompareReport,
            string? originalFileName)
        {
            // All user-supplied strings get HTML-encoded BEFORE they enter
            // the format templates. The templates themselves contain trusted
            // HTML markup (<strong>, <em>, etc.) from Loc.cs, so we can mix
            // template-HTML + encoded-data safely.
            var clinicName    = WebUtility.HtmlEncode(clinic.Name);
            var clinicCity    = WebUtility.HtmlEncode(clinic.City);
            var clinicAddress = WebUtility.HtmlEncode(clinic.Address);
            var patientName   = WebUtility.HtmlEncode(patient.Name);
            var origFile      = WebUtility.HtmlEncode(originalFileName ?? "analiza.pdf");

            string attachmentsKey;
            if (hasCompareReport)        attachmentsKey = "CamEmailAttachmentsThree";
            else if (hasInterpretation)  attachmentsKey = "CamEmailAttachmentsTwo";
            else                         attachmentsKey = "CamEmailAttachmentsOne";
            string attachmentsLine = string.Format(Loc.T(attachmentsKey), origFile);

            string headerEyebrow = Loc.T("CamEmailHeaderEyebrow");
            string greeting      = string.Format(Loc.T("CamEmailGreeting"), patientName);
            string intro         = string.Format(Loc.T("CamEmailIntro"), clinicName);
            string noticeLabel   = Loc.T("CamEmailNoticeLabel");
            string noticeBody    = Loc.T("CamEmailNoticeBody");
            string footerAuto    = string.Format(Loc.T("CamEmailFooterAuto"), clinicName);
            string footerPowered = Loc.T("CamEmailFooterPowered");
            string footerTagline = Loc.T("CamEmailFooterTagline");

            return $@"
<div style=""font-family:Arial,Helvetica,sans-serif;max-width:640px;margin:0 auto;padding:0;background:#ffffff;"">
  <div style=""background:#0d47a1;color:#ffffff;padding:24px;border-radius:10px 10px 0 0;text-align:center;"">
    <div style=""font-size:13px;opacity:0.85;letter-spacing:0.06em;text-transform:uppercase;margin-bottom:4px;"">
      {headerEyebrow}
    </div>
    <h1 style=""margin:0;font-size:24px;font-weight:700;"">{clinicName}</h1>
    <div style=""font-size:13px;opacity:0.9;margin-top:6px;"">{clinicAddress}, {clinicCity}</div>
  </div>

  <div style=""padding:28px 24px;color:#212529;font-size:15px;line-height:1.6;border:1px solid #e9ecef;border-top:0;"">
    <p style=""margin:0 0 14px 0;font-size:16px;"">
      {greeting}
    </p>

    <p style=""margin:0 0 14px 0;"">
      {intro}
    </p>

    <p style=""margin:0 0 14px 0;"">
      {attachmentsLine}
    </p>

    <div style=""background:#eef5ff;border-left:4px solid #0d47a1;padding:14px 18px;border-radius:6px;margin:20px 0;font-size:14px;"">
      <strong style=""color:#0d47a1;"">{noticeLabel}</strong>
      {noticeBody}
    </div>

    <p style=""margin:20px 0 0 0;color:#6c757d;font-size:13px;"">
      {footerAuto}
    </p>
  </div>

  <div style=""background:#f1f5fb;color:#0d47a1;padding:14px 24px;border-radius:0 0 10px 10px;text-align:center;font-size:12px;border:1px solid #e9ecef;border-top:0;"">
    {footerPowered}
    <a href=""https://medicalapp.ro"" style=""color:#0d47a1;font-weight:700;text-decoration:none;"">MedicalApp+</a>
    &mdash; {footerTagline} &mdash;
    <a href=""https://medicalapp.ro"" style=""color:#0d47a1;text-decoration:none;"">medicalapp.ro</a>
  </div>
</div>";
        }
    }
}
