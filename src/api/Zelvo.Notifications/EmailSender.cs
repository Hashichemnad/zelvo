using System.Net.Sockets;
using MailKit;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using Zelvo.Licensing;

namespace Zelvo.Notifications;

/// <summary>An email ready to send.</summary>
/// <param name="To">Recipient address.</param>
/// <param name="FromName">Sender name (the tenant's name in the email's language).</param>
/// <param name="Subject">Subject line.</param>
/// <param name="Html">HTML body.</param>
/// <param name="Text">Plain-text body.</param>
/// <param name="Language">Content language (<c>en</c> or <c>ar</c>).</param>
/// <param name="MessageId">Stable id, so a retried email is recognisable.</param>
public sealed record EmailMessage(string To, string FromName, string Subject, string Html, string Text, string Language, string MessageId)
{
    /// <summary>The tenant's own verified sender address (task F1); null for the installation's.</summary>
    public string? FromAddress { get; init; }

    /// <summary>Signs the email with the tenant's DKIM key (task F1).</summary>
    public DkimKey? Dkim { get; init; }

    /// <summary>Images embedded in the HTML by content id (the letterhead's logo).</summary>
    public IReadOnlyList<EmailImage> Images { get; init; } = [];

    /// <summary>Files attached to the email (task F2: scheduled report PDFs).</summary>
    public IReadOnlyList<EmailAttachment> Attachments { get; init; } = [];
}

/// <summary>A file attached to an email.</summary>
public sealed record EmailAttachment(string FileName, string ContentType, byte[] Content);

/// <summary>An image embedded in an email (<c>cid:</c>).</summary>
public sealed record EmailImage(string ContentId, string ContentType, byte[] Content);

/// <summary>A DKIM signing key.</summary>
/// <param name="Domain">The signing domain (d=).</param>
/// <param name="Selector">The selector (s=).</param>
/// <param name="PrivateKeyPem">The private key (PKCS #8 PEM), decrypted only for sending.</param>
public sealed record DkimKey(string Domain, string Selector, string PrivateKeyPem);

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
public sealed class SmtpEmailSender(NotificationOptions options, IInstallationWatermark? watermark = null) : IEmailSender
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
            mime = Build(message, smtp.From, watermark?.Current);
        }
        catch (ParseException)
        {
            return new(false, true, "The email address is not valid.");
        }

        using var _ = mime;
        using var client = new SmtpClient { Timeout = (int)smtp.Timeout.TotalMilliseconds };
        try
        {
            await client.ConnectAsync(smtp.Host!, smtp.Port, SocketOptions(smtp.Security, smtp.Port), cancellationToken).ConfigureAwait(false);
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

    /// <summary>
    /// The MIME message of <paramref name="message"/> from <paramref name="from"/>, carrying the
    /// installation's hidden mark when <paramref name="watermark"/> is given (module 12, task F6).
    /// </summary>
    public static MimeMessage Build(EmailMessage message, string from, string? watermark = null)
    {
        ArgumentNullException.ThrowIfNull(message);
        var mime = new MimeMessage();
        mime.From.Add(new MailboxAddress(message.FromName, message.FromAddress ?? from));
        mime.To.Add(MailboxAddress.Parse(message.To));
        mime.Subject = message.Subject;
        mime.MessageId = message.MessageId;
        mime.Headers.Add("Auto-Submitted", "auto-generated");
        mime.Headers.Add("X-Auto-Response-Suppress", "All");
        mime.Headers.Add(HeaderId.ContentLanguage, message.Language);
        if (InstallationMark.Encode(watermark) is { } mark)
        {
            mime.Headers.Add(InstallationMark.EmailHeader, mark);
        }

        var body = new BodyBuilder { TextBody = message.Text, HtmlBody = message.Html };
        foreach (var image in message.Images)
        {
            var resource = body.LinkedResources.Add(image.ContentId, image.Content, ContentType.Parse(image.ContentType));
            resource.ContentId = image.ContentId;
        }

        foreach (var attachment in message.Attachments)
        {
            body.Attachments.Add(attachment.FileName, attachment.Content, ContentType.Parse(attachment.ContentType));
        }

        mime.Body = body.ToMessageBody();
        if (message.Dkim is { } dkim)
        {
            using var key = new MemoryStream(System.Text.Encoding.ASCII.GetBytes(dkim.PrivateKeyPem));
            var signer = new MimeKit.Cryptography.DkimSigner(key, dkim.Domain, dkim.Selector)
            {
                HeaderCanonicalizationAlgorithm = MimeKit.Cryptography.DkimCanonicalizationAlgorithm.Relaxed,
                BodyCanonicalizationAlgorithm = MimeKit.Cryptography.DkimCanonicalizationAlgorithm.Relaxed,
            };
            mime.Prepare(EncodingConstraint.SevenBit);
            signer.Sign(mime, [HeaderId.From, HeaderId.To, HeaderId.Subject, HeaderId.Date, HeaderId.MessageId, HeaderId.ContentType]);
        }

        return mime;
    }

    /// <summary>
    /// The TLS the connection needs. Only <see cref="SmtpSecurity.None"/> sends in clear; the
    /// default never falls back to plain text when a server does not offer STARTTLS (ASVS V12.1.1).
    /// </summary>
    internal static SecureSocketOptions SocketOptions(SmtpSecurity security, int port) => security switch
    {
        SmtpSecurity.None => SecureSocketOptions.None,
        SmtpSecurity.StartTls => SecureSocketOptions.StartTls,
        SmtpSecurity.SslOnConnect => SecureSocketOptions.SslOnConnect,
        _ => port == 465 ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTls,
    };

    private static string Short(string text) =>
        text.Length <= Data.Notifications.NotificationDelivery.ErrorMaxLength ? text : text[..Data.Notifications.NotificationDelivery.ErrorMaxLength];
}
