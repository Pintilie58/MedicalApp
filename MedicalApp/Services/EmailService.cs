using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Options;
using MimeKit;

namespace MedicalApp.Services
{
    /// <summary>
    /// Email service that sends messages via SMTP using MailKit.
    /// Settings are read from appsettings.json -> EmailSettings.
    /// </summary>
    public class EmailService : IEmailService
    {
        private readonly EmailSettings _settings;
        private readonly ILogger<EmailService> _logger;

        public EmailService(IOptions<EmailSettings> options, ILogger<EmailService> logger)
        {
            _settings = options.Value;
            _logger = logger;
        }

        public Task SendEmailAsync(string toEmail, string subject, string htmlBody)
            => SendInternalAsync(toEmail, subject, htmlBody, Array.Empty<(byte[], string, string)>());

        public Task SendEmailWithAttachmentAsync(
            string toEmail, string subject, string htmlBody,
            byte[] attachmentBytes, string attachmentFileName)
            => SendInternalAsync(toEmail, subject, htmlBody,
                new[] { (attachmentBytes, attachmentFileName, "application/pdf") });

        public Task SendEmailWithAttachmentsAsync(
            string toEmail, string subject, string htmlBody,
            IEnumerable<(byte[] Bytes, string FileName, string MimeType)> attachments)
            => SendInternalAsync(toEmail, subject, htmlBody, attachments);

        private async Task SendInternalAsync(
            string toEmail, string subject, string htmlBody,
            IEnumerable<(byte[] Bytes, string FileName, string MimeType)> attachments)
        {
            var message = new MimeMessage();
            message.From.Add(new MailboxAddress(_settings.SenderName, _settings.SenderEmail));
            message.To.Add(MailboxAddress.Parse(toEmail));
            message.Subject = subject;

            var builder = new BodyBuilder { HtmlBody = htmlBody };

            foreach (var (bytes, fileName, mimeType) in attachments)
            {
                if (bytes is null || bytes.Length == 0 || string.IsNullOrWhiteSpace(fileName))
                    continue;

                // Split mime type "type/subtype" (fallback to application/octet-stream).
                var parts = (mimeType ?? "application/octet-stream").Split('/', 2);
                var type = parts.Length == 2 ? parts[0] : "application";
                var subtype = parts.Length == 2 ? parts[1] : "octet-stream";

                builder.Attachments.Add(fileName, bytes, new ContentType(type, subtype));
            }

            message.Body = builder.ToMessageBody();

            using var client = new SmtpClient();
            try
            {
                await client.ConnectAsync(_settings.SmtpServer, _settings.SmtpPort, SecureSocketOptions.StartTls);
                await client.AuthenticateAsync(_settings.Username, _settings.Password);
                await client.SendAsync(message);
                await client.DisconnectAsync(true);
                _logger.LogInformation("Email sent to {To}", toEmail);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send email to {To}", toEmail);
                throw;
            }
        }
    }
}
