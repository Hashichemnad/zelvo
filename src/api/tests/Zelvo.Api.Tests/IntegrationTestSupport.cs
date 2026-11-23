using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Npgsql;
using Zelvo.Data.Tenancy;
using Zelvo.Integrations;

namespace Zelvo.Api.Tests;

/// <summary>A webhook receiver on this computer that records what it gets and answers with <see cref="Status"/>.</summary>
internal sealed class TestWebhookReceiver : IAsyncDisposable
{
    private readonly WebApplication _app;

    private TestWebhookReceiver(WebApplication app) => _app = app;

    public ConcurrentQueue<ReceivedWebhook> Received { get; } = new();

    public int Status { get; set; } = StatusCodes.Status200OK;

    public Uri Url { get; private set; } = null!;

    public static async Task<TestWebhookReceiver> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        var receiver = new TestWebhookReceiver(app);
        app.MapPost("/hook", async (HttpRequest request) =>
        {
            using var reader = new StreamReader(request.Body);
            var body = await reader.ReadToEndAsync();
            receiver.Received.Enqueue(new ReceivedWebhook(
                request.Headers["webhook-id"].ToString(),
                request.Headers["webhook-timestamp"].ToString(),
                request.Headers["webhook-signature"].ToString(),
                request.Headers["Zelvo-Event"].ToString(),
                request.Headers["Zelvo-Environment"].ToString(),
                body));
            return Results.Text("""{"received":true,"contact":"owner@example.ae"}""", "application/json", statusCode: receiver.Status);
        });
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        receiver.Url = new Uri(new Uri(address), "/hook");
        return receiver;
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}

/// <summary>A webhook as the receiver got it.</summary>
internal sealed record ReceivedWebhook(string Id, string Timestamp, string Signature, string Event, string Environment, string Body)
{
    public bool SignedWith(string secret) =>
        WebhookSigner.Verify(Id, DateTimeOffset.FromUnixTimeSeconds(long.Parse(Timestamp, System.Globalization.CultureInfo.InvariantCulture)), Body, Signature, secret);

    public JsonNode Json => JsonNode.Parse(Body)!;
}

/// <summary>Helpers for module 14 tests.</summary>
internal static class IntegrationTestSupport
{
    private static readonly Lazy<string> MasterKeyFile = new(() =>
    {
        var path = Path.Combine(Path.GetTempPath(), $"zelvo-test-master-{Guid.NewGuid():N}.key");
        File.WriteAllText(path, Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)));
        return path;
    });

    /// <summary>A master key file for stored secrets (webhook signing secrets).</summary>
    public static ApiFactory WithMasterKey(this ApiFactory factory) => factory.WithSetting("Secrets:File:Path", MasterKeyFile.Value);

    /// <summary>Lets webhooks reach test receivers on this computer over plain HTTP, and retries at once.</summary>
    public static ApiFactory WithLocalWebhooks(this ApiFactory factory, int retries = 2, int disableAfter = 5)
    {
        factory.WithMasterKey().WithSetting("Integrations:Webhooks:AllowInsecureHttp", "true")
            .WithSetting("Integrations:Webhooks:DisableAfterFailures", disableAfter.ToString(System.Globalization.CultureInfo.InvariantCulture));
        for (var i = 0; i < retries; i++)
        {
            factory.WithSetting($"Integrations:Webhooks:RetryDelays:{i}", "00:00:00");
        }

        return factory.WithServices(services =>
        {
            services.RemoveAll<OutboundAddressPolicy>();
            services.AddSingleton(sp => new OutboundAddressPolicy(sp.GetRequiredService<IntegrationOptions>().Webhooks) { AllowLoopbackForTests = true });
        });
    }

    /// <summary>Runs the worker's webhook job once against the API's services.</summary>
    public static Task<int> DeliverWebhooksAsync(this ApiFactory factory) =>
        new WebhookDispatcher(
            factory.Services.GetRequiredService<IServiceScopeFactory>(),
            factory.Services.GetRequiredService<IntegrationOptions>(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<WebhookDispatcher>.Instance).RunOnceAsync(TestContext.Current.CancellationToken);

    /// <summary>Creates an API key as <paramref name="admin"/>; returns the key and the integration's id.</summary>
    public static async Task<(string Key, Guid Id)> CreateApiKeyAsync(this HttpClient admin, string role, params string[] scopes)
    {
        using var response = await admin.PostAsJsonAsync(new Uri("/api/v1/integrations/clients", UriKind.Relative),
            new { kind = "ApiKey", name = "CRM sync", role, scopes }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!;
        return ((string)body["apiKey"]!, Guid.Parse((string)body["client"]!["id"]!));
    }

    public static HttpClient WithApiKey(this HttpClient client, string key)
    {
        client.DefaultRequestHeaders.Remove("X-Api-Key");
        client.DefaultRequestHeaders.Add("X-Api-Key", key);
        return client;
    }

    public static async Task<JsonNode> JsonAsync(this HttpResponseMessage response) =>
        JsonNode.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!;

    /// <summary>Audit entries of a tenant (action, actor type, actor id), oldest first.</summary>
    public static async Task<List<(string Action, string ActorType, string? ActorId, string? Metadata)>> AuditAsync(this DatabaseFixture database, Tenant tenant)
    {
        await using var connection = new NpgsqlConnection(database.OwnerConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(
            "SELECT action, actor_type, actor_id, metadata::text FROM audit_events WHERE tenant_id = @t ORDER BY sequence", connection);
        command.Parameters.AddWithValue("t", tenant.Id);
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        var rows = new List<(string, string, string?, string?)>();
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            rows.Add((reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3)));
        }

        return rows;
    }

    /// <summary>Runs a scalar query as the owner.</summary>
    public static async Task<T> ScalarAsync<T>(this DatabaseFixture database, string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(database.OwnerConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return (T)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }
}
