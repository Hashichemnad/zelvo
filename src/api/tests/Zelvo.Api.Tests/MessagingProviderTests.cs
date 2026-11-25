using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Zelvo.Channels;
using Zelvo.Channels.Messaging;
using Zelvo.Data.Channels;
using Zelvo.Integrations;

namespace Zelvo.Api.Tests;

/// <summary>WhatsApp Cloud API, Twilio, generic SMS gateways and the Teams connector (task E3), against a recording HTTP handler.</summary>
public sealed class MessagingProviderTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class Recorder(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Url, string? Authorization, IReadOnlyDictionary<string, string> Headers, string Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add((request.Method, request.RequestUri!.ToString(), request.Headers.Authorization?.ToString(),
                request.Headers.ToDictionary(h => h.Key, h => string.Join(',', h.Value), StringComparer.OrdinalIgnoreCase), body));
            return answer(request);
        }
    }

    private static HttpResponseMessage Json(object value, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };

    private static MessagingClients Clients(Recorder recorder)
    {
        var options = new ChannelOptions();
        return new MessagingClients(new ChannelHttp(options, new OutboundAddressPolicy(options.Network), recorder));
    }

    private static MessagingChannel Channel(MessagingProvider provider, MessagingKind kind, string? account = null, string? sender = null, MessagingSettings? settings = null) =>
        new() { Name = "Test", Kind = kind, Provider = provider, AccountId = account, Sender = sender, DirectoryId = Guid.NewGuid().ToString(), Settings = (settings ?? new()).Write() };

    [Fact]
    public async Task WhatsApp_sends_text_inside_the_window_and_templates_outside_it_with_the_access_token()
    {
        var token = MessagingApi.Secret("wa");
        var recorder = new Recorder(_ => Json(new { messages = new[] { new { id = "wamid.out" } } }));
        var clients = Clients(recorder);
        var channel = Channel(MessagingProvider.WhatsAppCloud, MessagingKind.WhatsApp, "1098765432");
        var keys = new MessagingSecrets(token, null);

        Assert.Equal("wamid.out", await clients.SendAsync(channel, keys, new OutgoingMessage(ChannelMessageKind.Text, "+971501234567", "Hello", null, null), Ct));
        await clients.SendAsync(channel, keys, new OutgoingMessage(ChannelMessageKind.Template, "+971501234567", "Line one\nLine two",
            """{"name":"ticket_update","language":"ar"}""", null), Ct);

        Assert.All(recorder.Requests, r => Assert.Equal(("https://graph.facebook.com/v23.0/1098765432/messages", $"Bearer {token}"), (r.Url, r.Authorization)));
        var text = JsonNode.Parse(recorder.Requests[0].Body)!;
        Assert.Equal(("971501234567", "text", "Hello"), ((string?)text["to"], (string?)text["type"], (string?)text["text"]!["body"]));
        var template = JsonNode.Parse(recorder.Requests[1].Body)!["template"]!;
        Assert.Equal(("ticket_update", "ar"), ((string?)template["name"], (string?)template["language"]!["code"]));
        // Template variables cannot hold line breaks.
        Assert.Equal("Line one Line two", (string?)template["components"]![0]!["parameters"]![0]!["text"]);
    }

    [Fact]
    public async Task Refusals_that_will_not_improve_fail_at_once_and_the_rest_is_tried_again()
    {
        var clients = Clients(new Recorder(request => request.RequestUri!.AbsolutePath.Contains("/1/", StringComparison.Ordinal)
            ? Json(new { error = new { code = 131047, message = "Re-engagement message" } }, HttpStatusCode.BadRequest)
            : Json(new { error = new { code = 2, message = "Temporarily unavailable" } }, HttpStatusCode.ServiceUnavailable)));
        var keys = new MessagingSecrets(MessagingApi.Secret("wa"), null);
        var window = await Assert.ThrowsAsync<MessagingException>(() => clients.SendAsync(Channel(MessagingProvider.WhatsAppCloud, MessagingKind.WhatsApp, "1"), keys,
            new OutgoingMessage(ChannelMessageKind.Text, "+971501234567", "x", null, null), Ct));
        Assert.Equal(("outside-window", true), (window.Code, window.Permanent));
        var down = await Assert.ThrowsAsync<MessagingException>(() => clients.SendAsync(Channel(MessagingProvider.WhatsAppCloud, MessagingKind.WhatsApp, "2"), keys,
            new OutgoingMessage(ChannelMessageKind.Text, "+971501234567", "x", null, null), Ct));
        Assert.Equal(("provider-error", false), (down.Code, down.Permanent));
    }

    [Fact]
    public async Task Twilio_posts_the_form_with_basic_authentication_and_a_messaging_service_when_named()
    {
        var sid = "AC" + Guid.NewGuid().ToString("N");
        var token = MessagingApi.Secret("tw");
        var recorder = new Recorder(_ => Json(new { sid = "SM123" }, HttpStatusCode.Created));
        var id = await Clients(recorder).SendAsync(Channel(MessagingProvider.Twilio, MessagingKind.Sms, sid, "MG" + Guid.NewGuid().ToString("N")),
            new MessagingSecrets(token, null), new OutgoingMessage(ChannelMessageKind.Text, "+971501234567", "Your ticket INC-000001 was received.", null, null), Ct);
        Assert.Equal("SM123", id);
        var request = Assert.Single(recorder.Requests);
        Assert.Equal($"https://api.twilio.com/2010-04-01/Accounts/{sid}/Messages.json", request.Url);
        Assert.Equal("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{sid}:{token}")), request.Authorization);
        Assert.Contains("MessagingServiceSid=MG", request.Body, StringComparison.Ordinal);
        Assert.Contains("To=%2B971501234567", request.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_generic_gateway_gets_its_template_with_values_escaped_and_its_own_header()
    {
        var secret = MessagingApi.Secret("gw");
        var settings = new MessagingSettings
        {
            Gateway = new GenericSmsSettings
            {
                Url = "https://sms.operator.example.ae/send",
                BodyTemplate = """{"msisdn":"{{to}}","sender":"{{from}}","text":"{{text}}"}""",
                Auth = GatewayAuth.Header,
                AuthHeader = "X-API-Key",
                MessageIdPath = "result.0.ref",
                NumbersWithoutPlus = true,
            },
        };
        var recorder = new Recorder(_ => Json(new { result = new[] { new { @ref = "r-9" } } }));
        var id = await Clients(recorder).SendAsync(Channel(MessagingProvider.GenericHttp, MessagingKind.Sms, sender: "ENTITY", settings: settings), new MessagingSecrets(secret, null),
            new OutgoingMessage(ChannelMessageKind.Text, "+971501234567", "Quote \" and\nline: تم الاستلام", null, null), Ct);
        Assert.Equal("r-9", id);
        var request = Assert.Single(recorder.Requests);
        Assert.Equal(secret, request.Headers["X-API-Key"]);
        var body = JsonNode.Parse(request.Body)!;
        Assert.Equal(("971501234567", "ENTITY", "Quote \" and\nline: تم الاستلام"), ((string?)body["msisdn"], (string?)body["sender"], (string?)body["text"]));

        Assert.NotEmpty(new GenericSmsSettings { Url = "ftp://x", BodyTemplate = "{{to}} {{nope}}" }.Problems());
        Assert.Empty(settings.Gateway.Problems());
    }

    [Fact]
    public async Task Teams_gets_a_token_from_the_customers_directory_and_answers_only_on_Microsofts_addresses()
    {
        var recorder = new Recorder(request => request.RequestUri!.AbsolutePath.EndsWith("/token", StringComparison.Ordinal)
            ? Json(new { access_token = "bot-token", expires_in = 3600 })
            : Json(new { id = "activity-1" }));
        var clients = Clients(recorder);
        var channel = Channel(MessagingProvider.TeamsBot, MessagingKind.Teams, Guid.NewGuid().ToString());
        var keys = new MessagingSecrets(MessagingApi.Secret("teams"), null);
        var contact = new MessagingContact { Address = "obj", TeamsServiceUrl = MessagingApi.TeamsServiceUrl, TeamsConversationId = "a:1" };
        await clients.SendAsync(channel, keys, new OutgoingMessage(ChannelMessageKind.Card, "obj", "Approval needed", """{"type":"AdaptiveCard"}""", contact), Ct);
        Assert.Equal(2, recorder.Requests.Count);
        Assert.Contains($"login.microsoftonline.com/{channel.DirectoryId}/oauth2/v2.0/token", recorder.Requests[0].Url, StringComparison.Ordinal);
        Assert.Contains("scope=https%3A%2F%2Fapi.botframework.com%2F.default", recorder.Requests[0].Body, StringComparison.Ordinal);
        Assert.Equal(("https://smba.trafficmanager.net/emea/v3/conversations/a%3A1/activities", "Bearer bot-token"), (recorder.Requests[1].Url, recorder.Requests[1].Authorization));
        Assert.Equal("application/vnd.microsoft.card.adaptive", (string?)JsonNode.Parse(recorder.Requests[1].Body)!["attachments"]![0]!["contentType"]);

        var foreign = new MessagingContact { Address = "obj", TeamsServiceUrl = "https://attacker.example/", TeamsConversationId = "a:1" };
        var refused = await Assert.ThrowsAsync<MessagingException>(() => clients.SendAsync(channel, keys, new OutgoingMessage(ChannelMessageKind.Text, "obj", "x", null, foreign), Ct));
        Assert.Equal("service-url-refused", refused.Code);
    }

    [Fact]
    public void Webhook_signatures_are_checked_with_the_providers_secrets()
    {
        var secret = MessagingApi.Secret("app");
        var body = "{\"object\":\"whatsapp_business_account\"}"u8.ToArray();
        var signature = "sha256=" + Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body));
        Assert.True(WebhookSignatures.WhatsAppValid(body, signature, secret));
        Assert.False(WebhookSignatures.WhatsAppValid(body, signature, MessagingApi.Secret("other")));
        Assert.False(WebhookSignatures.WhatsAppValid(body, "sha256=zz", secret));

        var url = new Uri("https://zelvo.example.ae/hooks/entity/sms/1");
        var form = new Dictionary<string, string>(StringComparer.Ordinal) { ["Body"] = "Hi", ["From"] = "+971501234567" };
        var token = MessagingApi.Secret("tw");
        Assert.True(WebhookSignatures.TwilioValid(url, form, WebhookSignatures.TwilioSign(url, form, token), token));
        Assert.False(WebhookSignatures.TwilioValid(new Uri("https://other.example/hooks"), form, WebhookSignatures.TwilioSign(url, form, token), token));
    }
}
