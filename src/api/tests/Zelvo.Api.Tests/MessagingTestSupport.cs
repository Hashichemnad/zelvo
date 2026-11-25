using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Zelvo.Channels;
using Zelvo.Channels.Messaging;
using Zelvo.Data.Channels;
using Zelvo.Data.Tenancy;

namespace Zelvo.Api.Tests;

/// <summary>WhatsApp, SMS and Teams providers in memory (task E3).</summary>
internal sealed class FakeMessagingClients : IMessagingClients
{
    private int _next;

    public ConcurrentQueue<(MessagingChannel Channel, OutgoingMessage Message)> Sent { get; } = new();

    /// <summary>Teams members by their conversation id: email.</summary>
    public ConcurrentDictionary<string, string> TeamsEmails { get; } = new(StringComparer.Ordinal);

    /// <summary>Files WhatsApp would hand out, by media id.</summary>
    public ConcurrentDictionary<string, MessagingMedia> Media { get; } = new(StringComparer.Ordinal);

    /// <summary>When set, sending fails with this error.</summary>
    public MessagingException? Failure { get; set; }

    public (MessagingChannel Channel, OutgoingMessage Message)[] SentTo(string address) => [.. Sent.Where(s => s.Message.Address == address)];

    public Task<string?> SendAsync(MessagingChannel channel, MessagingSecrets secrets, OutgoingMessage message, CancellationToken cancellationToken)
    {
        if (Failure is { } failure)
        {
            throw failure;
        }

        Assert.False(string.IsNullOrEmpty(secrets.Credential));
        Sent.Enqueue((channel, message));
        return Task.FromResult<string?>($"out-{Interlocked.Increment(ref _next)}");
    }

    public Task<MessagingMedia?> DownloadAsync(MessagingChannel channel, MessagingSecrets secrets, string mediaId, long maxBytes, CancellationToken cancellationToken) =>
        Task.FromResult(Media.TryGetValue(mediaId, out var media) ? media : null);

    public Task<string> TestAsync(MessagingChannel channel, MessagingSecrets secrets, CancellationToken cancellationToken) =>
        Task.FromResult($"Test account {channel.AccountId}");

    public Task<TeamsMember?> TeamsMemberAsync(MessagingChannel channel, MessagingSecrets secrets, string serviceUrl, string conversationId, string memberId, CancellationToken cancellationToken) =>
        Task.FromResult(TeamsEmails.TryGetValue(memberId, out var email) ? new TeamsMember(email, email, null, null) : null);
}

/// <summary>Bot Framework in tests: a signing key made at run time.</summary>
internal sealed class TestTeamsKeys : ITeamsSigningKeys
{
    public static readonly RsaSecurityKey Key = new(RSA.Create(2048)) { KeyId = "teams-test" };

    public Task<ICollection<SecurityKey>> KeysAsync(CancellationToken cancellationToken) => Task.FromResult<ICollection<SecurityKey>>([Key]);

    public static string Token(string appId, string serviceUrl, SecurityKey? key = null) =>
        new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = "https://api.botframework.com",
            Audience = appId,
            Expires = DateTime.UtcNow.AddMinutes(5),
            Claims = new Dictionary<string, object>(StringComparer.Ordinal) { ["serviceurl"] = serviceUrl },
            SigningCredentials = new SigningCredentials(key ?? Key, SecurityAlgorithms.RsaSha256),
        });
}

/// <summary>Helpers for messaging tests.</summary>
internal static class MessagingApi
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public const string TeamsServiceUrl = "https://smba.trafficmanager.net/emea/";

    public static ApiFactory WithMessaging(this ApiFactory factory, FakeMessagingClients clients) =>
        factory.WithMasterKey().WithServices(s =>
        {
            s.RemoveAll<IMessagingClients>();
            s.AddSingleton<IMessagingClients>(clients);
            s.RemoveAll<ITeamsSigningKeys>();
            s.AddSingleton<ITeamsSigningKeys, TestTeamsKeys>();
        });

    /// <summary>Runs the worker's messaging job once for <paramref name="tenant"/>.</summary>
    public static Task<int> RunMessagingAsync(this ApiFactory factory, Tenant tenant) =>
        new MessagingJob(factory.Services.GetRequiredService<IServiceScopeFactory>(), factory.Services.GetRequiredService<ChannelOptions>(), NullLogger<MessagingJob>.Instance)
            .RunTenantAsync(tenant.Id, tenant.Slug, Ct);

    public static string Secret(string prefix) => $"{prefix}-{Guid.NewGuid():N}";

    /// <summary>Posts a WhatsApp webhook body signed with <paramref name="appSecret"/> (null: unsigned).</summary>
    public static async Task<HttpResponseMessage> WhatsAppAsync(this HttpClient client, Guid channel, JsonObject body, string? appSecret)
    {
        var bytes = Encoding.UTF8.GetBytes(body.ToJsonString());
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/hooks/whatsapp/{channel}") { Content = new ByteArrayContent(bytes) };
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        if (appSecret is not null)
        {
            request.Headers.Add("X-Hub-Signature-256", "sha256=" + Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(appSecret), bytes)));
        }

        return await client.SendAsync(request, Ct);
    }

    /// <summary>A WhatsApp webhook body with one message.</summary>
    public static JsonObject WhatsAppMessage(string phoneNumberId, string from, string id, JsonObject content, string? name = "Layla Ahmed") => new()
    {
        ["object"] = "whatsapp_business_account",
        ["entry"] = new JsonArray(new JsonObject
        {
            ["id"] = "waba",
            ["changes"] = new JsonArray(new JsonObject
            {
                ["field"] = "messages",
                ["value"] = new JsonObject
                {
                    ["messaging_product"] = "whatsapp",
                    ["metadata"] = new JsonObject { ["phone_number_id"] = phoneNumberId },
                    ["contacts"] = new JsonArray(new JsonObject { ["wa_id"] = from, ["profile"] = new JsonObject { ["name"] = name } }),
                    ["messages"] = new JsonArray(Merge(new JsonObject { ["from"] = from, ["id"] = id, ["timestamp"] = "1764000000" }, content)),
                },
            }),
        }),
    };

    public static JsonObject WhatsAppStatus(string phoneNumberId, string id, string status) => new()
    {
        ["object"] = "whatsapp_business_account",
        ["entry"] = new JsonArray(new JsonObject
        {
            ["changes"] = new JsonArray(new JsonObject
            {
                ["value"] = new JsonObject
                {
                    ["metadata"] = new JsonObject { ["phone_number_id"] = phoneNumberId },
                    ["statuses"] = new JsonArray(new JsonObject { ["id"] = id, ["status"] = status }),
                },
            }),
        }),
    };

    public static JsonObject TextContent(string text) => new() { ["type"] = "text", ["text"] = new JsonObject { ["body"] = text } };

    /// <summary>A Teams activity posted with a Bot Framework token for <paramref name="appId"/>.</summary>
    public static async Task<HttpResponseMessage> TeamsAsync(this HttpClient client, Guid channel, string appId, JsonObject activity, string? token = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/hooks/teams/{channel}")
        {
            Content = new StringContent(activity.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token ?? TestTeamsKeys.Token(appId, TeamsServiceUrl));
        return await client.SendAsync(request, Ct);
    }

    public static JsonObject TeamsActivity(string directory, string objectId, string userId, string? text, JsonObject? value = null, string type = "message") => new()
    {
        ["type"] = type,
        ["id"] = Guid.NewGuid().ToString("N"),
        ["serviceUrl"] = TeamsServiceUrl,
        ["channelId"] = "msteams",
        ["from"] = new JsonObject { ["id"] = userId, ["aadObjectId"] = objectId, ["name"] = "Teams person" },
        ["recipient"] = new JsonObject { ["id"] = "28:bot" },
        ["conversation"] = new JsonObject { ["id"] = $"a:{userId}", ["conversationType"] = "personal", ["tenantId"] = directory },
        ["channelData"] = new JsonObject { ["tenant"] = new JsonObject { ["id"] = directory } },
        ["text"] = text,
        ["value"] = value,
        ["membersAdded"] = type == "conversationUpdate" ? new JsonArray(new JsonObject { ["id"] = "28:bot" }) : null,
    };

    public static async Task<HttpStatusCode> StatusAsync(this Task<HttpResponseMessage> call)
    {
        using var response = await call;
        return response.StatusCode;
    }

    private static JsonObject Merge(JsonObject target, JsonObject more)
    {
        foreach (var (key, value) in more.ToList())
        {
            more.Remove(key);
            target[key] = value;
        }

        return target;
    }
}
