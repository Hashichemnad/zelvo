using System.Net.Sockets;
using MailKit;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace Zelvo.Notifications;

/// <summary>An email ready to send.</summary>
/// <param name="To">Recipient address.</param>
/// <param name="FromName">Sender name (the tenant's name in the email's language).</param>
/// <param name="Subject">Subject line.</param>
/// <param name="Html">HTML body.</param>
/// <param name="Text">Plain-text body.</param>
/// <param name="Language">Content language (<c>en</c> or <c>ar</c>).</param>
/// <param name="MessageId">Stable id, so a retried email is recognisable.</param>
public sealed record EmailMessage(string To, string FromName, string Subject, string Html, string Text, string Language, string MessageId);

/// <summary>What happened to one attempt.</summary>
/// <param name="Sent">The mail server accepted it.</param>
/// <param name="Permanent">The failure will not go away by trying again (address or message refused).</param>
/// <param name="Error">Why it failed; never the body.</param>
public sealed record EmailResult(bool Sent, bool Permanent, string? Error)
{
    /// <summary>Accepted.</summary>
    public static EmailResult Ok { get; } = new(true, false, null);
}

/// <summary>Sends email. The worker uses SMTP; tests replace it.</summary>
public interface IEmailSender
{
    /// <summary>Sends <paramref name="message"/> once.</summary>
    Task<EmailResult> SendAsync(EmailMessage message, CancellationToken cancellationToken);
}

/// <summary>
/// Sends through the installation's mail server with MailKit (MIT). Every email is
/// <c>multipart/alternative</c> (plain text and HTML), marked <c>Auto-Submitted: auto-generated</c>
/// (RFC 3834) so mail servers and later email-to-ticket (task E2) do not answer it, and carries
/// its <c>Content-Language</c>.
/// </summary>
public sealed class SmtpEmailSender(NotificationOptions options) : IEmailSender
{
    /// <inheritdoc />
    public async Task<EmailResult> SendAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        var smtp = options.Smtp;
        if (!smtp.Configured)
        {
            return new(false, true, "No mail server is set up for this installation (Notifications:Smtp:Host).");
        }

        MimeMessage mime;
        try
        {
            mime = Build(message, smtp.From);
        }
        catch (ParseException)
        {
            return new(false, true, "The email address is not valid.");
        }

        using var _ = mime;
        using var client = new SmtpClient { Timeout = (int)smtp.Timeout.TotalMilliseconds };
        try
        {
            var security = smtp.Security switch
            {
                SmtpSecurity.None => SecureSocketOptions.None,
                SmtpSecurity.StartTls => SecureSocketOptions.StartTls,
                SmtpSecurity.SslOnConnect => SecureSocketOptions.SslOnConnect,
                _ => SecureSocketOptions.Auto,
            };
            await client.ConnectAsync(smtp.Host!, smtp.Port, security, cancellationToken).ConfigureAwait(false);
            if (smtp.User is { } user)
            {
                await client.AuthenticateAsync(user, smtp.Password ?? string.Empty, cancellationToken).ConfigureAwait(false);
            }

            await client.SendAsync(mime, cancellationToken).ConfigureAwait(false);
            await client.DisconnectAsync(quit: true, cancellationToken).ConfigureAwait(false);
            return EmailResult.Ok;
        }
        catch (SmtpCommandException exception)
        {
            // 5xx answers about the recipient or the message do not improve by waiting; 4xx do.
            var permanent = (int)exception.StatusCode >= 500 && exception.ErrorCode is SmtpErrorCode.RecipientNotAccepted or SmtpErrorCode.MessageNotAccepted;
            return new(false, permanent, Short($"The mail server answered {(int)exception.StatusCode}: {exception.Message}"));
        }
        catch (AuthenticationException exception)
        {
            return new(false, false, Short($"The mail server refused the sign-in: {exception.Message}"));
        }
        catch (Exception exception) when (exception is SmtpProtocolException or SocketException or IOException or TimeoutException or SslHandshakeException or ServiceNotConnectedException)
        {
            return new(false, false, Short($"The mail server could not be reached: {exception.Message}"));
        }
    }

    /// <summary>The MIME message of <paramref name="message"/> from <paramref name="from"/>.</summary>
    public static MimeMessage Build(EmailMessage message, string from)
    {
        ArgumentNullException.ThrowIfNull(message);
        var mime = new MimeMessage();
        mime.From.Add(new MailboxAddress(message.FromName, from));
        mime.To.Add(MailboxAddress.Parse(message.To));
        mime.Subject = message.Subject;
        mime.MessageId = message.MessageId;
        mime.Headers.Add("Auto-Submitted", "auto-generated");
        mime.Headers.Add("X-Auto-Response-Suppress", "All");
        mime.Headers.Add(HeaderId.ContentLanguage, message.Language);
        mime.Body = new BodyBuilder { TextBody = message.Text, HtmlBody = message.Html }.ToMessageBody();
        return mime;
    }

    private static string Short(string text) =>
        text.Length <= Data.Notifications.NotificationDelivery.ErrorMaxLength ? text : text[..Data.Notifications.NotificationDelivery.ErrorMaxLength];
}
