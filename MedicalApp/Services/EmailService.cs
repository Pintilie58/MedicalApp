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
            => SendInternalAsync(toEmail, subject, htmlBody, null, null);

        public Task SendEmailWithAttachmentAsync(
            string toEmail, string subject, string htmlBody,
            byte[] attachmentBytes, string attachmentFileName)
            => SendInternalAsync(toEmail, subject, htmlBody, attachmentBytes, attachmentFileName);

        private async Task SendInternalAsync(
            string toEmail, string subject, string htmlBody,
            byte[]? attachmentBytes, string? attachmentFileName)
        {
            var message = new MimeMessage();
            message.From.Add(new MailboxAddress(_settings.SenderName, _settings.SenderEmail));
            message.To.Add(MailboxAddress.Parse(toEmail));
            message.Subject = subject;

            var builder = new BodyBuilder { HtmlBody = htmlBody };

            if (attachmentBytes is { Length: > 0 } && !string.IsNullOrWhiteSpace(attachmentFileName))
            {
                builder.Attachments.Add(attachmentFileName, attachmentBytes,
                    new ContentType("application", "pdf"));
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
