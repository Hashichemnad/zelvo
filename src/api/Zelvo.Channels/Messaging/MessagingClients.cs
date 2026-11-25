using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Zelvo.Data.Channels;

namespace Zelvo.Channels.Messaging;

/// <summary>A message could not be sent or a provider refused a call.</summary>
/// <param name="code">Stable code, for example <c>sign-in-failed</c>, <c>outside-window</c>, <c>unreachable</c>.</param>
/// <param name="message">What happened.</param>
/// <param name="permanent">Trying again will not help (a refused number, a missing template).</param>
public sealed class MessagingException(string code, string message, bool permanent = false, Exception? inner = null) : Exception(message, inner)
{
    /// <summary>Stable code.</summary>
    public string Code { get; } = code;

    /// <summary>Whether trying again will not help.</summary>
    public bool Permanent { get; } = permanent;

    /// <summary>Code and message, for the channel's last error.</summary>
    public string Summary => $"{Code}: {Message}";
}

/// <summary>A message to send.</summary>
/// <param name="Kind">Text, WhatsApp template or Teams card.</param>
/// <param name="Address">Phone number in E.164, or the Teams contact's address.</param>
/// <param name="Text">The text (also the template's variable and the card's fallback).</param>
/// <param name="Payload">Template (<c>{"name","language"}</c>) or Adaptive Card JSON.</param>
/// <param name="Contact">The contact (Teams needs its conversation).</param>
public sealed record OutgoingMessage(ChannelMessageKind Kind, string Address, string? Text, string? Payload, MessagingContact? Contact);

/// <summary>A file of an incoming message.</summary>
public sealed record MessagingMedia(byte[] Content, string ContentType);

/// <summary>What Teams says about a member of a conversation.</summary>
/// <param name="Email">Email of the account.</param>
/// <param name="UserPrincipalName">Sign-in name (UPN).</param>
/// <param name="Name">Display name.</param>
/// <param name="ObjectId">Entra object id.</param>
public sealed record TeamsMember(string? Email, string? UserPrincipalName, string? Name, string? ObjectId);

/// <summary>Talks to the messaging providers (task E3). Replaced in tests.</summary>
public interface IMessagingClients
{
    /// <summary>Sends <paramref name="message"/>; returns the provider's message id.</summary>
    Task<string?> SendAsync(MessagingChannel channel, MessagingSecrets secrets, OutgoingMessage message, CancellationToken cancellationToken);

    /// <summary>WhatsApp: downloads the file <paramref name="mediaId"/> of an incoming message (null when larger than <paramref name="maxBytes"/>).</summary>
    Task<MessagingMedia?> DownloadAsync(MessagingChannel channel, MessagingSecrets secrets, string mediaId, long maxBytes, CancellationToken cancellationToken);

    /// <summary>Checks the account and credential without sending anything; returns what the provider says about it.</summary>
    Task<string> TestAsync(MessagingChannel channel, MessagingSecrets secrets, CancellationToken cancellationToken);

    /// <summary>Teams: who a member of a conversation is (to link their Zelvo account).</summary>
    Task<TeamsMember?> TeamsMemberAsync(MessagingChannel channel, MessagingSecrets secrets, string serviceUrl, string conversationId, string memberId, CancellationToken cancellationToken);
}

/// <summary>
/// Zelvo's own thin clients for Meta's WhatsApp Cloud API, Twilio, generic HTTP SMS gateways and
/// the Bot Framework connector of Microsoft Teams. Every call goes through the channels'
/// outgoing-request policy (<see cref="ChannelHttp"/>), so internal addresses are refused unless
/// the operator allows them.
/// </summary>
public sealed class MessagingClients(ChannelHttp http) : IMessagingClients
{
    private const string TeamsScope = "https://api.botframework.com/.default";
    private readonly ConcurrentDictionary<Guid, (string Token, DateTimeOffset Expires)> _teamsTokens = new();

    /// <inheritdoc />
    public async Task<string?> SendAsync(MessagingChannel channel, MessagingSecrets secrets, OutgoingMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(secrets);
        ArgumentNullException.ThrowIfNull(message);
        return channel.Provider switch
        {
            MessagingProvider.WhatsAppCloud => await WhatsAppSendAsync(channel, secrets, message, cancellationToken).ConfigureAwait(false),
            MessagingProvider.Twilio => await TwilioSendAsync(channel, secrets, message, cancellationToken).ConfigureAwait(false),
            MessagingProvider.GenericHttp => await GatewaySendAsync(channel, secrets, message, cancellationToken).ConfigureAwait(false),
            _ => await TeamsSendAsync(channel, secrets, message, cancellationToken).ConfigureAwait(false),
        };
    }

    /// <inheritdoc />
    public async Task<MessagingMedia?> DownloadAsync(MessagingChannel channel, MessagingSecrets secrets, string mediaId, long maxBytes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(secrets);
        var info = await SendJsonAsync(Bearer(HttpMethod.Get, new Uri(http.Options.WhatsAppGraphUrl, Uri.EscapeDataString(mediaId)), secrets), "WhatsApp media", cancellationToken)
            .ConfigureAwait(false);
        if (info["url"]?.GetValue<string>() is not { } address || !Uri.TryCreate(address, UriKind.Absolute, out var url))
        {
            throw new MessagingException("bad-answer", "WhatsApp did not say where the file is.");
        }

        if (info["file_size"] is { } size && long.TryParse(size.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var bytes) && bytes > maxBytes)
        {
            return null;
        }

        using var response = await SendAsync(Bearer(HttpMethod.Get, url, secrets), cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw Error(response.StatusCode, "WhatsApp media", await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        }

        if (response.Content.Headers.ContentLength > maxBytes)
        {
            return null;
        }

        var content = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        return content.LongLength > maxBytes ? null : new MessagingMedia(content, info["mime_type"]?.GetValue<string>() ?? response.Content.Headers.ContentType?.MediaType ?? "application/octet-stream");
    }

    /// <inheritdoc />
    public async Task<string> TestAsync(MessagingChannel channel, MessagingSecrets secrets, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(secrets);
        switch (channel.Provider)
        {
            case MessagingProvider.WhatsAppCloud:
                var number = await SendJsonAsync(Bearer(HttpMethod.Get,
                    new Uri(http.Options.WhatsAppGraphUrl, $"{Uri.EscapeDataString(Require(channel.AccountId, "phone number id"))}?fields=display_phone_number,verified_name"), secrets),
                    "WhatsApp", cancellationToken).ConfigureAwait(false);
                return $"{number["verified_name"]?.GetValue<string>()} {number["display_phone_number"]?.GetValue<string>()}".Trim();
            case MessagingProvider.Twilio:
                var account = await SendJsonAsync(TwilioRequest(HttpMethod.Get, channel, secrets, ".json"), "Twilio", cancellationToken).ConfigureAwait(false);
                return $"{account["friendly_name"]?.GetValue<string>()} ({account["status"]?.GetValue<string>()})";
            case MessagingProvider.GenericHttp:
                // A gateway can only be tried by sending; the settings are checked when saved.
                var settings = MessagingSettings.Read(channel.Settings).Gateway ?? throw new MessagingException("not-set-up", "The gateway is not set up.", true);
                return new Uri(settings.Url).Host;
            default:
                await TeamsTokenAsync(channel, secrets, cancellationToken, fresh: true).ConfigureAwait(false);
                return channel.AccountId ?? string.Empty;
        }
    }

    /// <inheritdoc />
    public async Task<TeamsMember?> TeamsMemberAsync(
        MessagingChannel channel, MessagingSecrets secrets, string serviceUrl, string conversationId, string memberId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(secrets);
        var url = new Uri(TeamsServiceUrl(serviceUrl), $"v3/conversations/{Uri.EscapeDataString(conversationId)}/members/{Uri.EscapeDataString(memberId)}");
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await TeamsTokenAsync(channel, secrets, cancellationToken).ConfigureAwait(false));
        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        var json = await ReadAsync(response, "Teams", cancellationToken).ConfigureAwait(false);
        return new TeamsMember(Text(json, "email"), Text(json, "userPrincipalName"), Text(json, "name"), Text(json, "aadObjectId") ?? Text(json, "objectId"));
    }

    /// <summary>The address Teams said to answer on, if it is one of Microsoft's (else refused).</summary>
    public Uri TeamsServiceUrl(string serviceUrl)
    {
        if (!Uri.TryCreate(serviceUrl, UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps && !http.Options.Network.AllowInsecureHttp
            || !http.Options.TeamsServiceHosts.Any(h => url.Host.Equals(h, StringComparison.OrdinalIgnoreCase) || url.Host.EndsWith("." + h, StringComparison.OrdinalIgnoreCase)))
        {
            throw new MessagingException("service-url-refused", "The Teams service address is not one of Microsoft's.", true);
        }

        return url.AbsoluteUri.EndsWith('/') ? url : new Uri(url.AbsoluteUri + "/");
    }

    private async Task<string?> WhatsAppSendAsync(MessagingChannel channel, MessagingSecrets secrets, OutgoingMessage message, CancellationToken cancellationToken)
    {
        var to = message.Address.TrimStart('+');
        var body = new JsonObject { ["messaging_product"] = "whatsapp", ["recipient_type"] = "individual", ["to"] = to };
        if (message.Kind == ChannelMessageKind.Template)
        {
            var template = JsonNode.Parse(message.Payload ?? "{}")!;
            body["type"] = "template";
            body["template"] = new JsonObject
            {
                ["name"] = template["name"]?.GetValue<string>(),
                ["language"] = new JsonObject { ["code"] = template["language"]?.GetValue<string>() ?? "en" },
                ["components"] = new JsonArray(new JsonObject
                {
                    ["type"] = "body",
                    ["parameters"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = TemplateVariable(message.Text) }),
                }),
            };
        }
        else
        {
            body["type"] = "text";
            body["text"] = new JsonObject { ["preview_url"] = false, ["body"] = Cut(message.Text ?? string.Empty, 4096) };
        }

        var request = Bearer(HttpMethod.Post, new Uri(http.Options.WhatsAppGraphUrl, $"{Uri.EscapeDataString(Require(channel.AccountId, "phone number id"))}/messages"), secrets);
        request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        var answer = await SendJsonAsync(request, "WhatsApp", cancellationToken).ConfigureAwait(false);
        return answer["messages"]?[0]?["id"]?.GetValue<string>();
    }

    private async Task<string?> TwilioSendAsync(MessagingChannel channel, MessagingSecrets secrets, OutgoingMessage message, CancellationToken cancellationToken)
    {
        var sender = Require(channel.Sender, "sender");
        var fields = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["To"] = message.Address,
            [sender.StartsWith("MG", StringComparison.Ordinal) ? "MessagingServiceSid" : "From"] = sender,
            ["Body"] = Cut(message.Text ?? string.Empty, 1600),
        };
        var request = TwilioRequest(HttpMethod.Post, channel, secrets, "/Messages.json");
        request.Content = new FormUrlEncodedContent(fields);
        var answer = await SendJsonAsync(request, "Twilio", cancellationToken).ConfigureAwait(false);
        return answer["sid"]?.GetValue<string>();
    }

    private async Task<string?> GatewaySendAsync(MessagingChannel channel, MessagingSecrets secrets, OutgoingMessage message, CancellationToken cancellationToken)
    {
        var settings = MessagingSettings.Read(channel.Settings).Gateway ?? throw new MessagingException("not-set-up", "The gateway is not set up.", true);
        var rendered = settings.Render(message.Address, Cut(message.Text ?? string.Empty, 1600), channel.Sender);
        HttpRequestMessage request;
        if (settings.Method == "GET")
        {
            var url = settings.Url + (settings.Url.Contains('?', StringComparison.Ordinal) ? "&" : "?") + rendered.TrimStart('?', '&');
            request = new HttpRequestMessage(HttpMethod.Get, new Uri(url));
        }
        else
        {
            request = new HttpRequestMessage(HttpMethod.Post, new Uri(settings.Url))
            {
                Content = new StringContent(rendered, Encoding.UTF8, settings.Body == GatewayBody.Json ? "application/json" : "application/x-www-form-urlencoded"),
            };
        }

        switch (settings.Auth)
        {
            case GatewayAuth.Basic:
                request.Headers.Authorization = new AuthenticationHeaderValue("Basic",
                    Convert.ToBase64String(Encoding.UTF8.GetBytes($"{settings.Username}:{secrets.Credential}")));
                break;
            case GatewayAuth.Bearer:
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Require(secrets.Credential, "secret"));
                break;
            case GatewayAuth.Header:
                request.Headers.TryAddWithoutValidation(settings.AuthHeader!, Require(secrets.Credential, "secret"));
                break;
        }

        using (request)
        {
            using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw Error(response.StatusCode, "SMS gateway", body);
            }

            if (settings.MessageIdPath is { Length: > 0 } path)
            {
                try
                {
                    return JsonPath.Find(JsonNode.Parse(body), path)?.ToString();
                }
                catch (JsonException)
                {
                    return null;
                }
            }

            return null;
        }
    }

    private async Task<string?> TeamsSendAsync(MessagingChannel channel, MessagingSecrets secrets, OutgoingMessage message, CancellationToken cancellationToken)
    {
        var contact = message.Contact;
        if (contact?.TeamsServiceUrl is not { } serviceUrl || contact.TeamsConversationId is not { } conversation)
        {
            throw new MessagingException("not-reachable", "Zelvo has no Teams conversation with this person yet.", true);
        }

        var activity = new JsonObject { ["type"] = "message", ["textFormat"] = "plain" };
        if (message.Kind == ChannelMessageKind.Card && message.Payload is { } card)
        {
            activity["summary"] = Cut(message.Text ?? string.Empty, 200);
            activity["attachments"] = new JsonArray(new JsonObject { ["contentType"] = "application/vnd.microsoft.card.adaptive", ["content"] = JsonNode.Parse(card) });
        }
        else
        {
            activity["text"] = Cut(message.Text ?? string.Empty, 28_000);
        }

        var request = new HttpRequestMessage(HttpMethod.Post, new Uri(TeamsServiceUrl(serviceUrl), $"v3/conversations/{Uri.EscapeDataString(conversation)}/activities"))
        {
            Content = new StringContent(activity.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await TeamsTokenAsync(channel, secrets, cancellationToken).ConfigureAwait(false));
        var answer = await SendJsonAsync(request, "Teams", cancellationToken).ConfigureAwait(false);
        return answer["id"]?.GetValue<string>();
    }

    // Client credentials of the Teams app in the customer's own Entra directory (a single-tenant bot).
    private async Task<string> TeamsTokenAsync(MessagingChannel channel, MessagingSecrets secrets, CancellationToken cancellationToken, bool fresh = false)
    {
        if (!fresh && _teamsTokens.TryGetValue(channel.Id, out var cached) && cached.Expires > DateTimeOffset.UtcNow.AddMinutes(1))
        {
            return cached.Token;
        }

        var directory = Require(channel.DirectoryId, "directory id");
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(http.Options.MicrosoftLoginUrl, $"{Uri.EscapeDataString(directory)}/oauth2/v2.0/token"))
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = Require(channel.AccountId, "app id"),
                ["client_secret"] = Require(secrets.Credential, "client secret"),
                ["scope"] = TeamsScope,
            }),
        };
        var answer = await SendJsonAsync(request, "Microsoft sign-in", cancellationToken).ConfigureAwait(false);
        var token = answer["access_token"]?.GetValue<string>() ?? throw new MessagingException("bad-answer", "Microsoft returned no token.");
        var seconds = answer["expires_in"] is { } expires && int.TryParse(expires.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) ? s : 3600;
        _teamsTokens[channel.Id] = (token, DateTimeOffset.UtcNow.AddSeconds(seconds));
        return token;
    }

    /// <summary>Forgets a channel's cached token (its credential changed).</summary>
    public void Forget(Guid channelId) => _teamsTokens.TryRemove(channelId, out _);

    private HttpRequestMessage TwilioRequest(HttpMethod method, MessagingChannel channel, MessagingSecrets secrets, string path)
    {
        var sid = Require(channel.AccountId, "account SID");
        var request = new HttpRequestMessage(method, new Uri(http.Options.TwilioUrl, $"2010-04-01/Accounts/{Uri.EscapeDataString(sid)}{path}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{sid}:{Require(secrets.Credential, "auth token")}")));
        return request;
    }

    private static HttpRequestMessage Bearer(HttpMethod method, Uri url, MessagingSecrets secrets)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Require(secrets.Credential, "access token"));
        return request;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        try
        {
            return await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (MailboxException ex)
        {
            throw new MessagingException(ex.Code, ex.Message, ex.Code is "url-not-https" or "address-not-allowed", ex);
        }
    }

    private async Task<JsonNode> SendJsonAsync(HttpRequestMessage request, string what, CancellationToken cancellationToken)
    {
        using (request)
        {
            using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
            return await ReadAsync(response, what, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<JsonNode> ReadAsync(HttpResponseMessage response, string what, CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw Error(response.StatusCode, what, body);
        }

        try
        {
            return JsonNode.Parse(body) ?? new JsonObject();
        }
        catch (JsonException ex)
        {
            throw new MessagingException("bad-answer", $"{what}: the answer was not understood.", false, ex);
        }
    }

    /// <summary>The error of a failed call: refusals of the message itself are permanent; sign-in, rate limits and server errors are tried again.</summary>
    internal static MessagingException Error(HttpStatusCode status, string what, string body)
    {
        var detail = Detail(body);
        // WhatsApp 131047: more than 24 hours since the person wrote (a template is needed).
        if (detail.Contains("131047", StringComparison.Ordinal))
        {
            return new MessagingException("outside-window", $"{what}: more than 24 hours since the person last wrote; a template is needed.", true);
        }

        var code = status switch
        {
            HttpStatusCode.Unauthorized => "sign-in-failed",
            HttpStatusCode.Forbidden => "access-denied",
            HttpStatusCode.NotFound => "not-found",
            HttpStatusCode.TooManyRequests => "rate-limited",
            _ when (int)status >= 500 => "provider-error",
            _ => "refused",
        };
        var permanent = code is "refused" or "not-found";
        return new MessagingException(code, $"{what}: {(int)status} {detail}".Trim(), permanent);
    }

    private static string Detail(string body)
    {
        try
        {
            var json = JsonNode.Parse(body);
            var error = json?["error"];
            var text = error is JsonObject e
                ? $"{e["code"]} {e["message"]}"
                : json?["message"] is { } m ? $"{json["code"]} {m}" : body;
            return Cut(text.Trim(), 300);
        }
        catch (JsonException)
        {
            return Cut(body.Trim(), 300);
        }
    }

    // WhatsApp template variables cannot hold line breaks, tabs or more than four spaces in a row.
    private static string TemplateVariable(string? text) =>
        Cut(System.Text.RegularExpressions.Regex.Replace((text ?? string.Empty).Replace('\n', ' ').Replace('\t', ' '), " {4,}", "   "), 1000);

    private static string? Text(JsonNode node, string name) => node[name] is JsonValue value && value.TryGetValue<string>(out var text) && text.Length > 0 ? text : null;

    private static string Require(string? value, string what) =>
        string.IsNullOrEmpty(value) ? throw new MessagingException("not-set-up", $"The channel has no {what}.", true) : value;

    private static string Cut(string value, int max) => value.Length <= max ? value : value[..(max - 1)] + "…";
}

/// <summary>Dotted paths into JSON (<c>messages.0.id</c>), for gateways set up on screen.</summary>
public static class JsonPath
{
    /// <summary>The node at <paramref name="path"/>, or null.</summary>
    public static JsonNode? Find(JsonNode? root, string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var node = root;
        foreach (var part in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            node = node switch
            {
                JsonObject o => o.TryGetPropertyValue(part, out var child) ? child : null,
                JsonArray a when int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) && i >= 0 && i < a.Count => a[i],
                _ => null,
            };
            if (node is null)
            {
                return null;
            }
        }

        return node;
    }

    /// <summary>The text at <paramref name="path"/> (numbers and booleans as written), or null.</summary>
    public static string? Text(JsonNode? root, string path) =>
        Find(root, path) switch
        {
            JsonValue v when v.TryGetValue<string>(out var s) => s,
            JsonValue v => v.ToJsonString(),
            JsonNode n => n.ToJsonString(),
            null => null,
        };
}
