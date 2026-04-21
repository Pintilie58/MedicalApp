namespace MedicalApp.Services
{
    public interface IEmailService
    {
        Task SendEmailAsync(string toEmail, string subject, string htmlBody);

        Task SendEmailWithAttachmentAsync(
            string toEmail,
            string subject,
            string htmlBody,
            byte[] attachmentBytes,
            string attachmentFileName);

        /// <summary>
        /// Sends an email with multiple attachments.
        /// Each attachment is a tuple of (bytes, file name, mime type).
        /// Example mime types: "application/pdf", "text/plain", "application/json".
        /// </summary>
        Task SendEmailWithAttachmentsAsync(
            string toEmail,
            string subject,
            string htmlBody,
            IEnumerable<(byte[] Bytes, string FileName, string MimeType)> attachments);
    }
}
