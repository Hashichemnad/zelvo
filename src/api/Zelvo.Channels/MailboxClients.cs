using System.Buffers.Text;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Net.Smtp;
using MailKit.Search;
using MailKit.Security;
using MimeKit;
using Zelvo.Data.Channels;
using Zelvo.Integrations;

namespace Zelvo.Channels;

/// <summary>An open connection to a mailbox for one read.</summary>
public interface IMailboxConnection : IAsyncDisposable
{
    /// <summary>Ids of unread emails in the inbox, oldest first where the provider says, at most <paramref name="max"/>.</summary>
    Task<IReadOnlyList<string>> ListUnreadAsync(int max, CancellationToken cancellationToken);

    /// <summary>The email, or null when it is larger than <paramref name="maxBytes"/>.</summary>
    Task<MimeMessage?> GetAsync(string id, long maxBytes, CancellationToken cancellationToken);

    /// <summary>Marks the email read, so it is not read again.</summary>
    Task MarkReadAsync(string id, CancellationToken cancellationToken);

    /// <summary>A refresh token the provider rotated during this connection, for the caller to store.</summary>
    string? RotatedRefreshToken { get; }
}

/// <summary>Opens mailboxes and sends from them, for each provider.</summary>
public interface IMailboxClients
{
    /// <summary>Connects to <paramref name="mailbox"/> to read it.</summary>
    Task<IMailboxConnection> OpenAsync(Mailbox mailbox, MailboxCredential credential, CancellationToken cancellationToken);

    /// <summary>Sends <paramref name="message"/> from <paramref name="mailbox"/>; returns a rotated refresh token, if any.</summary>
    Task<string?> SendAsync(Mailbox mailbox, MailboxCredential credential, MimeMessage message, CancellationToken cancellationToken);
}

/// <summary>
/// IMAP and SMTP (MailKit), Microsoft Graph and the Gmail API. Every connection follows the mail
/// network policy (<c>Channels:Network</c>), checked on the address actually connected to.
/// </summary>
public sealed class MailboxClients(ChannelHttp http, OAuthTokens tokens) : IMailboxClients
{
    /// <inheritdoc />
    public async Task<IMailboxConnection> OpenAsync(Mailbox mailbox, MailboxCredential credential, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(mailbox);
        ArgumentNullException.ThrowIfNull(credential);
        switch (mailbox.Provider)
        {
            case MailboxProvider.Imap:
                return await ImapConnection.OpenAsync(mailbox, credential, http, cancellationToken).ConfigureAwait(false);
            case MailboxProvider.Microsoft365:
            case MailboxProvider.Gmail:
                var token = await tokens.GetAsync(mailbox, credential, cancellationToken).ConfigureAwait(false);
                return new RestConnection(mailbox, token, http);
            default:
                throw new MailboxException("provider-not-supported", "Unknown provider.");
        }
    }

    /// <inheritdoc />
    public async Task<string?> SendAsync(Mailbox mailbox, MailboxCredential credential, MimeMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(mailbox);
        ArgumentNullException.ThrowIfNull(credential);
        ArgumentNullException.ThrowIfNull(message);
        if (mailbox.Provider == MailboxProvider.Imap)
        {
            await SmtpSendAsync(mailbox, credential, message, http, cancellationToken).ConfigureAwait(false);
            return null;
        }

        var token = await tokens.GetAsync(mailbox, credential, cancellationToken).ConfigureAwait(false);
        using var mime = new MemoryStream();
        await message.WriteToAsync(FormatOptions.Default, mime, cancellationToken).ConfigureAwait(false);
        HttpRequestMessage request;
        if (mailbox.Provider == MailboxProvider.Microsoft365)
        {
            // Graph takes the MIME message base64-encoded as text/plain.
            request = new HttpRequestMessage(HttpMethod.Post, new Uri(http.Options.MicrosoftGraphUrl, $"{GraphBase(mailbox)}/sendMail"))
            {
                Content = new StringContent(Convert.ToBase64String(mime.ToArray()), Encoding.ASCII, "text/plain"),
            };
        }
        else
        {
            request = new HttpRequestMessage(HttpMethod.Post, new Uri(http.Options.GmailUrl, "gmail/v1/users/me/messages/send"))
            {
                Content = JsonContent(new Dictionary<string, string>(StringComparer.Ordinal) { ["raw"] = Base64Url.EncodeToString(mime.ToArray()) }),
            };
        }

        using (request)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Value);
            using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw ChannelHttp.ProviderError(response.StatusCode, "Sending", await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            }
        }

        return token.RefreshToken;
    }

    internal static string GraphBase(Mailbox mailbox) =>
        mailbox.Auth == MailboxAuth.OAuthSignIn ? "v1.0/me" : $"v1.0/users/{Uri.EscapeDataString(mailbox.Address)}";

    private static StringContent JsonContent(object value) =>
        new(JsonSerializer.Serialize(value, ChannelHttp.Json), Encoding.UTF8, "application/json");

    private static async Task SmtpSendAsync(Mailbox mailbox, MailboxCredential credential, MimeMessage message, ChannelHttp http, CancellationToken cancellationToken)
    {
        if (mailbox.SmtpHost is not { Length: > 0 } host || mailbox.SmtpPort is not { } port)
        {
            throw new MailboxException("smtp-missing", "Enter the SMTP server of the mailbox.");
        }

        using var client = new SmtpClient { Timeout = (int)http.Options.Timeout.TotalMilliseconds };
        try
        {
            var stream = await ConnectAsync(http.Policy, host, port, cancellationToken).ConfigureAwait(false);
            await client.ConnectAsync(stream, host, port, Security(mailbox.SmtpSecurity, port, 465), cancellationToken).ConfigureAwait(false);
            if (mailbox.Username is { Length: > 0 } user)
            {
                await client.AuthenticateAsync(user, credential.Secret, cancellationToken).ConfigureAwait(false);
            }

            await client.SendAsync(message, cancellationToken).ConfigureAwait(false);
            await client.DisconnectAsync(quit: true, cancellationToken).ConfigureAwait(false);
        }
        catch (SmtpCommandException ex)
        {
            throw new MailboxException((int)ex.StatusCode >= 500 ? "smtp-refused" : "smtp-temporary", $"The SMTP server answered {(int)ex.StatusCode}: {Short(ex.Message)}", ex);
        }
        catch (Exception ex) when (ex is not MailboxException and not OperationCanceledException)
        {
            throw Wrap(ex, "SMTP");
        }
    }

    internal static async Task<Stream> ConnectAsync(OutboundAddressPolicy policy, string host, int port, CancellationToken cancellationToken)
    {
        try
        {
            return await policy.ConnectAsync(host, port, cancellationToken).ConfigureAwait(false);
        }
        catch (OutboundRequestBlockedException ex)
        {
            throw new MailboxException(ex.Code, ex.Message, ex);
        }
    }

    internal static SecureSocketOptions Security(MailSecurity security, int port, int implicitTlsPort) => security switch
    {
        MailSecurity.None => SecureSocketOptions.None,
        MailSecurity.StartTls => SecureSocketOptions.StartTls,
        MailSecurity.SslOnConnect => SecureSocketOptions.SslOnConnect,
        // Credentials never travel in clear: implicit TLS on the usual port, STARTTLS required otherwise.
        _ => port == implicitTlsPort ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTls,
    };

    internal static MailboxException Wrap(Exception ex, string what) => ex switch
    {
        AuthenticationException => new MailboxException("sign-in-failed", $"The {what} server refused the user name or password.", ex),
        SslHandshakeException => new MailboxException("tls-failed", $"TLS with the {what} server failed: {Short(ex.Message)}", ex),
        SocketException or IOException or TimeoutException or ServiceNotConnectedException or ProtocolException =>
            new MailboxException("unreachable", $"The {what} server could not be reached: {Short(ex.Message)}", ex),
        CommandException => new MailboxException("command-failed", $"The {what} server refused a command: {Short(ex.Message)}", ex),
        _ => new MailboxException("failed", $"{what}: {Short(ex.Message)}", ex),
    };

    private static string Short(string text) => text.Length <= 300 ? text : text[..300];

    /// <summary>IMAP: unread emails of the inbox; ids are UIDVALIDITY:UID.</summary>
    private sealed class ImapConnection(ImapClient client, IMailFolder inbox) : IMailboxConnection
    {
        public string? RotatedRefreshToken => null;

        public static async Task<IMailboxConnection> OpenAsync(Mailbox mailbox, MailboxCredential credential, ChannelHttp http, CancellationToken cancellationToken)
        {
            if (mailbox.ImapHost is not { Length: > 0 } host || mailbox.ImapPort is not { } port)
            {
                throw new MailboxException("imap-missing", "Enter the IMAP server of the mailbox.");
            }

            var client = new ImapClient { Timeout = (int)http.Options.Timeout.TotalMilliseconds };
            try
            {
                var stream = await ConnectAsync(http.Policy, host, port, cancellationToken).ConfigureAwait(false);
                await client.ConnectAsync(stream, host, port, Security(mailbox.ImapSecurity, port, 993), cancellationToken).ConfigureAwait(false);
                await client.AuthenticateAsync(mailbox.Username ?? mailbox.Address, credential.Secret, cancellationToken).ConfigureAwait(false);
                await client.Inbox.OpenAsync(FolderAccess.ReadWrite, cancellationToken).ConfigureAwait(false);
                return new ImapConnection(client, client.Inbox);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                client.Dispose();
                throw ex as MailboxException ?? Wrap(ex, "IMAP");
            }
        }

        public async Task<IReadOnlyList<string>> ListUnreadAsync(int max, CancellationToken cancellationToken)
        {
            try
            {
                var uids = await inbox.SearchAsync(SearchQuery.NotSeen, cancellationToken).ConfigureAwait(false);
                return [.. uids.OrderBy(u => u.Id).Take(max).Select(u => $"{inbox.UidValidity}:{u.Id}")];
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw Wrap(ex, "IMAP");
            }
        }

        public async Task<MimeMessage?> GetAsync(string id, long maxBytes, CancellationToken cancellationToken)
        {
            var uid = Uid(id);
            try
            {
                var summary = (await inbox.FetchAsync([uid], MessageSummaryItems.Size | MessageSummaryItems.UniqueId, cancellationToken).ConfigureAwait(false)).FirstOrDefault();
                if (summary?.Size is { } size && size > maxBytes)
                {
                    return null;
                }

                return await inbox.GetMessageAsync(uid, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw Wrap(ex, "IMAP");
            }
        }

        public async Task MarkReadAsync(string id, CancellationToken cancellationToken)
        {
            try
            {
                await inbox.StoreAsync(Uid(id), new StoreFlagsRequest(StoreAction.Add, MessageFlags.Seen) { Silent = true }, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw Wrap(ex, "IMAP");
            }
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (client.IsConnected)
                {
                    await client.DisconnectAsync(quit: true).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is IOException or SocketException or ImapProtocolException or ServiceNotConnectedException)
            {
                // Closing anyway.
            }

            client.Dispose();
        }

        private UniqueId Uid(string id) =>
            id.Split(':') is [var validity, var value] && uint.TryParse(validity, out var v) && v == inbox.UidValidity && uint.TryParse(value, out var u)
                ? new UniqueId(v, u)
                : throw new MailboxException("mailbox-changed", "The mailbox was rebuilt on the server; its emails are read again.");
    }

    /// <summary>Microsoft Graph or the Gmail API.</summary>
    private sealed class RestConnection(Mailbox mailbox, AccessToken token, ChannelHttp http) : IMailboxConnection
    {
        public string? RotatedRefreshToken => token.RefreshToken;

        private bool Graph => mailbox.Provider == MailboxProvider.Microsoft365;

        public async Task<IReadOnlyList<string>> ListUnreadAsync(int max, CancellationToken cancellationToken)
        {
            var url = Graph
                ? new Uri(http.Options.MicrosoftGraphUrl, $"{GraphBase(mailbox)}/mailFolders/inbox/messages?$filter=isRead%20eq%20false&$select=id&$top={max}")
                : new Uri(http.Options.GmailUrl, $"gmail/v1/users/me/messages?q={Uri.EscapeDataString("is:unread in:inbox")}&maxResults={max}");
            using var response = await SendAsync(HttpMethod.Get, url, null, cancellationToken).ConfigureAwait(false);
            using var document = await ChannelHttp.ReadJsonAsync<JsonDocument>(response, "Reading the inbox", cancellationToken).ConfigureAwait(false);
            var list = document.RootElement.TryGetProperty(Graph ? "value" : "messages", out var items) && items.ValueKind == JsonValueKind.Array
                ? items.EnumerateArray().Select(i => i.TryGetProperty("id", out var idValue) ? idValue.GetString() : null).OfType<string>().ToList()
                : [];
            // Gmail lists newest first.
            if (!Graph)
            {
                list.Reverse();
            }

            return [.. list.Take(max)];
        }

        public async Task<MimeMessage?> GetAsync(string id, long maxBytes, CancellationToken cancellationToken)
        {
            byte[] bytes;
            if (Graph)
            {
                using var response = await SendAsync(HttpMethod.Get, new Uri(http.Options.MicrosoftGraphUrl, $"{GraphBase(mailbox)}/messages/{Uri.EscapeDataString(id)}/$value"), null,
                    cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    throw ChannelHttp.ProviderError(response.StatusCode, "Reading an email", await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
                }

                if (response.Content.Headers.ContentLength is { } length && length > maxBytes)
                {
                    return null;
                }

                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                using var buffer = new MemoryStream();
                var chunk = new byte[81920];
                int read;
                while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    buffer.Write(chunk, 0, read);
                    if (buffer.Length > maxBytes)
                    {
                        return null;
                    }
                }

                bytes = buffer.ToArray();
            }
            else
            {
                using var response = await SendAsync(HttpMethod.Get, new Uri(http.Options.GmailUrl, $"gmail/v1/users/me/messages/{Uri.EscapeDataString(id)}?format=raw"), null,
                    cancellationToken).ConfigureAwait(false);
                using var document = await ChannelHttp.ReadJsonAsync<JsonDocument>(response, "Reading an email", cancellationToken).ConfigureAwait(false);
                if (document.RootElement.TryGetProperty("sizeEstimate", out var estimate) && estimate.TryGetInt64(out var size) && size > maxBytes)
                {
                    return null;
                }

                var raw = document.RootElement.TryGetProperty("raw", out var rawValue) ? rawValue.GetString() : null;
                if (string.IsNullOrEmpty(raw))
                {
                    throw new MailboxException("bad-answer", "Gmail gave the email without its content.");
                }

                bytes = Base64Url.DecodeFromChars(raw.TrimEnd('='));
            }

            try
            {
                using var stream = new MemoryStream(bytes);
                return await MimeMessage.LoadAsync(stream, cancellationToken).ConfigureAwait(false);
            }
            catch (FormatException ex)
            {
                throw new MailboxException("unreadable-email", "The email could not be read.", ex);
            }
        }

        public async Task MarkReadAsync(string id, CancellationToken cancellationToken)
        {
            using var response = Graph
                ? await SendAsync(HttpMethod.Patch, new Uri(http.Options.MicrosoftGraphUrl, $"{GraphBase(mailbox)}/messages/{Uri.EscapeDataString(id)}"),
                    JsonContent(new Dictionary<string, bool>(StringComparer.Ordinal) { ["isRead"] = true }), cancellationToken).ConfigureAwait(false)
                : await SendAsync(HttpMethod.Post, new Uri(http.Options.GmailUrl, $"gmail/v1/users/me/messages/{Uri.EscapeDataString(id)}/modify"),
                    JsonContent(new Dictionary<string, string[]>(StringComparer.Ordinal) { ["removeLabelIds"] = ["UNREAD"] }), cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw ChannelHttp.ProviderError(response.StatusCode, "Marking an email read", await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            }
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private async Task<HttpResponseMessage> SendAsync(HttpMethod method, Uri url, HttpContent? content, CancellationToken cancellationToken)
        {
            using var request = new HttpRequestMessage(method, url) { Content = content };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Value);
            return await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
    }
}
