using System.Globalization;
using System.Net.Mail;
using System.Text;
using HobioAdmin.Web.Analytics;

namespace HobioAdmin.Web.Email;

public sealed record ReplyResult(bool Sent, string Message)
{
    public static ReplyResult Ok(string to) => new(true, $"Reply sent to {to}.");
    public static ReplyResult Fail(string message) => new(false, message);
}

/// <summary>
/// Sends an admin's reply to the person who submitted a report. The recipient is always read from the database
/// by report id (never taken from the page), and nothing is written to the database: the sending mailbox gets a
/// BCC of every reply, which is the record of what was sent.
/// </summary>
public sealed class ReportReplyService(
    IAnalyticsRepository repository,
    IEmailSender sender,
    ILogger<ReportReplyService> logger)
{
    public const int MaxMessageLength = 5000;
    public const int MaxSubjectLength = 200;

    public bool IsConfigured => sender.IsConfigured;

    public static string DefaultSubject(string title) => $"Re: {title}";

    public async Task<ReplyResult> SendAsync(long reportId, string? subject, string? message, string? adminEmail, CancellationToken ct = default)
    {
        if (!sender.IsConfigured)
        {
            return ReplyResult.Fail("Email isn't set up yet. Add EmailCredentials to the hobio-admin AWS secret.");
        }

        var body = message?.Trim() ?? string.Empty;
        if (body.Length == 0)
        {
            return ReplyResult.Fail("Write a message first.");
        }

        if (body.Length > MaxMessageLength)
        {
            return ReplyResult.Fail($"The message is too long (max {MaxMessageLength:N0} characters).");
        }

        var report = await repository.GetIssueReportAsync(reportId, ct);
        if (report is null)
        {
            return ReplyResult.Fail("That report no longer exists.");
        }

        if (report.ReporterIsDeleted || !IsValidAddress(report.ReporterEmail))
        {
            return ReplyResult.Fail("The reporter has no email address on file, so there's nobody to reply to.");
        }

        var email = Build(report, subject, body, sender.FromAddress);
        try
        {
            await sender.SendAsync(email, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Log the failure without the message body or recipient address.
            logger.LogError(ex, "Reply to report {ReportId} by {Admin} failed.", reportId, adminEmail);
            return ReplyResult.Fail($"The email couldn't be sent: {ex.Message}");
        }

        logger.LogInformation("Reply to report {ReportId} sent by {Admin}.", reportId, adminEmail);
        return ReplyResult.Ok(email.To);
    }

    /// <summary>Plain-text reply with the original report quoted underneath.</summary>
    public static OutgoingEmail Build(IssueReport report, string? subject, string message, string? bcc)
    {
        var cleanSubject = OneLine(string.IsNullOrWhiteSpace(subject) ? DefaultSubject(report.Title) : subject);
        if (cleanSubject.Length > MaxSubjectLength)
        {
            cleanSubject = cleanSubject[..MaxSubjectLength];
        }

        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(report.CreatedAtUtc, DateTimeKind.Utc), ReportPeriods.HomeTimeZone);
        var greeting = string.IsNullOrWhiteSpace(report.ReporterFullName) ? "Hi," : $"Hi {report.ReporterFullName.Split(' ')[0]},";

        var text = new StringBuilder()
            .AppendLine(greeting)
            .AppendLine()
            .AppendLine(message.Trim())
            .AppendLine()
            .AppendLine("— The Hobio team")
            .AppendLine()
            .AppendLine("----- Your report -----")
            .AppendLine(local.ToString("MMM d, yyyy h:mm tt", CultureInfo.InvariantCulture))
            .AppendLine(report.Title)
            .AppendLine()
            .AppendLine(report.Description)
            .ToString();

        return new OutgoingEmail(report.ReporterEmail!.Trim(), cleanSubject, text, bcc);
    }

    public static bool IsValidAddress(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        try
        {
            return new MailAddress(value.Trim()).Address.Equals(value.Trim(), StringComparison.OrdinalIgnoreCase);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    // Header injection guard: a subject can never contain line breaks.
    private static string OneLine(string value) =>
        string.Join(' ', value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
}
