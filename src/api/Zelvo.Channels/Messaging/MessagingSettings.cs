using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Zelvo.Channels.Messaging;

/// <summary>How a generic SMS gateway authenticates Zelvo's requests.</summary>
public enum GatewayAuth
{
    /// <summary>No authentication (an internal gateway that trusts the network).</summary>
    None,

    /// <summary>HTTP Basic with <see cref="GenericSmsSettings.Username"/> and the stored secret.</summary>
    Basic,

    /// <summary><c>Authorization: Bearer</c> with the stored secret.</summary>
    Bearer,

    /// <summary>The stored secret in the header <see cref="GenericSmsSettings.AuthHeader"/>.</summary>
    Header,
}

/// <summary>How the generic gateway's request body is written.</summary>
public enum GatewayBody
{
    /// <summary>JSON; placeholders are written as JSON string content.</summary>
    Json,

    /// <summary>Form fields (<c>application/x-www-form-urlencoded</c>); placeholders are URL-encoded.</summary>
    Form,
}

/// <summary>
/// A generic SMS gateway set up on screen (product owner decision 2026-11-25): Zelvo sends
/// <see cref="Method"/> to <see cref="Url"/> with <see cref="BodyTemplate"/> where <c>{{to}}</c>,
/// <c>{{text}}</c> and <c>{{from}}</c> are filled in; any 2xx answer counts as sent. Incoming text
/// messages reach Zelvo's webhook with the channel's token, and the fields named here are read.
/// </summary>
public sealed partial record GenericSmsSettings
{
    /// <summary>The gateway's address (HTTPS, or HTTP when the operator allows it for internal networks).</summary>
    public string Url { get; init; } = string.Empty;

    /// <summary><c>POST</c> or <c>GET</c> (with the template as query string).</summary>
    public string Method { get; init; } = "POST";

    /// <summary>JSON or form fields.</summary>
    public GatewayBody Body { get; init; } = GatewayBody.Json;

    /// <summary>The request body (or query string for GET), with placeholders.</summary>
    public string BodyTemplate { get; init; } = """{"to":"{{to}}","from":"{{from}}","text":"{{text}}"}""";

    /// <summary>Authentication.</summary>
    public GatewayAuth Auth { get; init; } = GatewayAuth.Bearer;

    /// <summary>User name for Basic.</summary>
    public string? Username { get; init; }

    /// <summary>Header name for <see cref="GatewayAuth.Header"/>, for example <c>X-API-Key</c>.</summary>
    public string? AuthHeader { get; init; }

    /// <summary>Dotted path of the provider's message id in a JSON answer, for example <c>messages.0.id</c>.</summary>
    public string? MessageIdPath { get; init; }

    /// <summary>Phone numbers without the leading <c>+</c> (some UAE gateways want <c>9715…</c>).</summary>
    public bool NumbersWithoutPlus { get; init; }

    /// <summary>Incoming: the field (form or dotted JSON path) holding the sender's number.</summary>
    public string InboundFrom { get; init; } = "from";

    /// <summary>Incoming: the field holding the text.</summary>
    public string InboundText { get; init; } = "text";

    /// <summary>Incoming: the field holding the gateway's message id, if any.</summary>
    public string? InboundId { get; init; } = "id";

    /// <summary>Placeholders the template may use.</summary>
    public static IReadOnlyList<string> Placeholders { get; } = ["to", "text", "from"];

    /// <summary>What is wrong with these settings (field, message), or nothing.</summary>
    public IEnumerable<(string Field, string Message)> Problems()
    {
        if (!Uri.TryCreate(Url, UriKind.Absolute, out var url) || url.Scheme is not ("https" or "http") || !string.IsNullOrEmpty(url.UserInfo))
        {
            yield return ("gateway.url", "Enter the gateway's address, starting with https://.");
        }

        if (Method is not ("POST" or "GET"))
        {
            yield return ("gateway.method", "The method is POST or GET.");
        }

        if (string.IsNullOrWhiteSpace(BodyTemplate) || BodyTemplate.Length > 4000)
        {
            yield return ("gateway.bodyTemplate", "The request needs a template of up to 4,000 characters.");
        }
        else
        {
            foreach (Match match in PlaceholderPattern().Matches(BodyTemplate))
            {
                if (!Placeholders.Contains(match.Groups[1].Value, StringComparer.Ordinal))
                {
                    yield return ("gateway.bodyTemplate", $"Unknown placeholder {{{{{match.Groups[1].Value}}}}}; use {{{{to}}}}, {{{{text}}}} and {{{{from}}}}.");
                }
            }

            if (!BodyTemplate.Contains("{{to}}", StringComparison.Ordinal) || !BodyTemplate.Contains("{{text}}", StringComparison.Ordinal))
            {
                yield return ("gateway.bodyTemplate", "The template needs {{to}} and {{text}}.");
            }
        }

        if (Auth == GatewayAuth.Basic && string.IsNullOrWhiteSpace(Username))
        {
            yield return ("gateway.username", "Basic authentication needs a user name.");
        }

        if (Auth == GatewayAuth.Header && (string.IsNullOrWhiteSpace(AuthHeader) || !HeaderName().IsMatch(AuthHeader)))
        {
            yield return ("gateway.authHeader", "Enter the header's name, for example X-API-Key.");
        }

        if (string.IsNullOrWhiteSpace(InboundFrom) || string.IsNullOrWhiteSpace(InboundText))
        {
            yield return ("gateway.inboundFrom", "Name the fields of incoming messages that hold the sender and the text.");
        }
    }

    /// <summary>The template with values put in, escaped for <see cref="Body"/>.</summary>
    public string Render(string to, string text, string? from) =>
        PlaceholderPattern().Replace(BodyTemplate, match => Escape(match.Groups[1].Value switch
        {
            "to" => NumbersWithoutPlus ? to.TrimStart('+') : to,
            "text" => text,
            _ => from ?? string.Empty,
        }));

    private string Escape(string value) =>
        Body == GatewayBody.Form || Method == "GET"
            ? Uri.EscapeDataString(value)
            : JsonEncodedText.Encode(value, System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping).Value;

    [GeneratedRegex(@"\{\{\s*([a-zA-Z.]+)\s*\}\}")]
    private static partial Regex PlaceholderPattern();

    [GeneratedRegex("^[A-Za-z0-9-]{1,64}$")]
    private static partial Regex HeaderName();
}

/// <summary>
/// WhatsApp's message templates (product owner decision 2026-11-25): messages more than 24 hours
/// after the person last wrote must use a template Meta approved. Each template has one body
/// variable, <c>{{1}}</c>, which gets the notification's text. Administrators name the template per
/// notification event, or one for every event, and Meta's language code per Zelvo language.
/// </summary>
public sealed partial record WhatsAppSettings
{
    /// <summary>Template for any event without its own.</summary>
    public string? DefaultTemplate { get; init; }

    /// <summary>Templates per notification event.</summary>
    public IReadOnlyDictionary<string, string> Templates { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Meta's language code of the template for English (<c>en</c>, <c>en_US</c>, <c>en_GB</c>).</summary>
    public string EnglishCode { get; init; } = "en";

    /// <summary>Meta's language code for Arabic.</summary>
    public string ArabicCode { get; init; } = "ar";

    /// <summary>The template for <paramref name="eventType"/>, if any.</summary>
    public string? TemplateFor(string eventType) => Templates.TryGetValue(eventType, out var name) ? name : DefaultTemplate;

    /// <summary>Meta's language code for a Zelvo language.</summary>
    public string CodeFor(string language) => language == "ar" ? ArabicCode : EnglishCode;

    /// <summary>What is wrong with these settings.</summary>
    public IEnumerable<(string Field, string Message)> Problems()
    {
        foreach (var name in Templates.Values.Append(DefaultTemplate).Where(n => n is not null))
        {
            if (!TemplateName().IsMatch(name!))
            {
                yield return ("whatsApp.templates", $"\"{name}\" is not a template name: lower-case letters, digits and underscores.");
            }
        }

        if (!LanguageCode().IsMatch(EnglishCode) || !LanguageCode().IsMatch(ArabicCode))
        {
            yield return ("whatsApp.languages", "Language codes look like en, en_US or ar.");
        }
    }

    [GeneratedRegex("^[a-z0-9_]{1,512}$")]
    private static partial Regex TemplateName();

    [GeneratedRegex("^[a-z]{2,3}(_[A-Z]{2})?$")]
    private static partial Regex LanguageCode();
}

/// <summary>The non-secret settings of a messaging channel, stored as JSON in <c>messaging_channels.settings</c>.</summary>
public sealed record MessagingSettings
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    /// <summary>The generic SMS gateway.</summary>
    public GenericSmsSettings? Gateway { get; init; }

    /// <summary>WhatsApp templates.</summary>
    public WhatsAppSettings? WhatsApp { get; init; }

    /// <summary>Reads stored settings (empty when unreadable).</summary>
    public static MessagingSettings Read(string? json)
    {
        try
        {
            return string.IsNullOrWhiteSpace(json) ? new() : JsonSerializer.Deserialize<MessagingSettings>(json, Json) ?? new();
        }
        catch (JsonException)
        {
            return new();
        }
    }

    /// <summary>The settings as stored.</summary>
    public string Write() => JsonSerializer.Serialize(this, Json);
}

/// <summary>A channel's secrets, decrypted only to send or check a message.</summary>
/// <param name="Credential">Access token, auth token, gateway secret or client secret.</param>
/// <param name="WebhookSecret">WhatsApp: app secret and verify token; generic gateway: the incoming token.</param>
public sealed record MessagingSecrets(string? Credential, WebhookSecrets? WebhookSecret);

/// <summary>Secrets of incoming webhooks.</summary>
/// <param name="AppSecret">WhatsApp: the Meta app's secret (signatures, <c>X-Hub-Signature-256</c>).</param>
/// <param name="VerifyToken">WhatsApp: the verify token Meta echoes when the webhook is registered; generic gateway: the token in its address.</param>
public sealed record WebhookSecrets(string? AppSecret, string VerifyToken);
