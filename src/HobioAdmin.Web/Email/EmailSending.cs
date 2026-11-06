using System.Net;
using System.Net.Mail;

namespace HobioAdmin.Web.Email;

/// <summary>
/// SMTP settings for replying to reports, from the "EmailCredentials" block in the hobio-admin AWS secret:
/// <code>"EmailCredentials": { "Host": "smtp.example.com", "Port": 587, "Email": "support@hobio.ca", "Password": "..." }</code>
/// Uses STARTTLS (port 587 is the usual choice; implicit-TLS port 465 is not supported by .NET's SmtpClient).
/// </summary>
public sealed class EmailOptions
{
    public const string SectionName = "EmailCredentials";

    public string? Host { get; set; }
    public int Port { get; set; } = 587;
    public string? Email { get; set; }
    public string? Password { get; set; }

    /// <summary>Display name on outgoing replies.</summary>
    public string FromName { get; set; } = "Hobio Support";

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(Host) && Port > 0 && !string.IsNullOrWhiteSpace(Email) && !string.IsNullOrWhiteSpace(Password);
}

/// <summary>A plain-text email ready to send. Bcc is the sending mailbox, so every reply also lands in that inbox.</summary>
public sealed record OutgoingEmail(string To, string Subject, string Body, string? Bcc);

public interface IEmailSender
{
    bool IsConfigured { get; }

    /// <summary>The address replies are sent from (and BCC'd to).</summary>
    string? FromAddress { get; }

    Task SendAsync(OutgoingEmail email, CancellationToken ct = default);
}

public sealed class SmtpEmailSender(Microsoft.Extensions.Options.IOptions<EmailOptions> options) : IEmailSender
{
    private readonly EmailOptions _options = options.Value;

    public bool IsConfigured => _options.IsConfigured;

    public string? FromAddress => _options.Email;

    public async Task SendAsync(OutgoingEmail email, CancellationToken ct = default)
    {
        if (!IsConfigured)
        {
            throw new InvalidOperationException("Email isn't configured. Add EmailCredentials to the hobio-admin AWS secret.");
        }

        using var message = new MailMessage
        {
            From = new MailAddress(_options.Email!, _options.FromName),
            Subject = email.Subject,
            Body = email.Body,
            IsBodyHtml = false,
        };
        message.To.Add(new MailAddress(email.To));
        message.ReplyToList.Add(new MailAddress(_options.Email!, _options.FromName));
        if (!string.IsNullOrWhiteSpace(email.Bcc))
        {
            message.Bcc.Add(new MailAddress(email.Bcc));
        }

        using var client = new SmtpClient(_options.Host!, _options.Port)
        {
            EnableSsl = true,
            DeliveryMethod = SmtpDeliveryMethod.Network,
            UseDefaultCredentials = false,
            Credentials = new NetworkCredential(_options.Email, _options.Password),
            Timeout = 30_000,
        };
        await client.SendMailAsync(message, ct);
    }
}
