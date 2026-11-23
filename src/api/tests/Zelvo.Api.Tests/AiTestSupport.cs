using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Zelvo.Ai;
using Zelvo.Data.Auditing;
using Zelvo.Data.Tenancy;
using Zelvo.Integrations;

namespace Zelvo.Api.Tests;

/// <summary>A request the fake provider received.</summary>
internal sealed record ReceivedAiRequest(string Method, string Path, string Query, IReadOnlyDictionary<string, string> Headers, string Body)
{
    public JsonNode Json => JsonNode.Parse(Body)!;
}

/// <summary>
/// A fake AI provider on this computer that answers like Anthropic, OpenAI (also Azure and
/// OpenAI-compatible) and Gemini, and records what it received.
/// </summary>
internal sealed class FakeAiProvider : IAsyncDisposable
{
    private readonly WebApplication _app;

    private FakeAiProvider(WebApplication app) => _app = app;

    public ConcurrentQueue<ReceivedAiRequest> Received { get; } = new();

    /// <summary>What the model answers.</summary>
    public string Answer { get; set; } = "OK";

    /// <summary>Status to answer with (errors carry an error message).</summary>
    public int Status { get; set; } = StatusCodes.Status200OK;

    public int InputTokens { get; set; } = 1000;

    public int OutputTokens { get; set; } = 200;

    public Uri Url { get; private set; } = null!;

    public static async Task<FakeAiProvider> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        var fake = new FakeAiProvider(app);
        app.Run(async context => await fake.HandleAsync(context));
        await app.StartAsync();
        fake.Url = new Uri(app.Urls.First() + "/");
        return fake;
    }

    public ValueTask DisposeAsync() => _app.DisposeAsync();

    private async Task HandleAsync(HttpContext context)
    {
        using var reader = new StreamReader(context.Request.Body);
        var body = await reader.ReadToEndAsync();
        var path = context.Request.Path.Value ?? string.Empty;
        Received.Enqueue(new ReceivedAiRequest(context.Request.Method, path, context.Request.QueryString.Value ?? string.Empty,
            context.Request.Headers.ToDictionary(h => h.Key.ToLowerInvariant(), h => h.Value.ToString(), StringComparer.Ordinal), body));

        context.Response.StatusCode = Status;
        if (Status >= 400)
        {
            await context.Response.WriteAsJsonAsync(new { error = new { message = "Invalid key for test@example.com" } });
            return;
        }

        JsonNode answer = path switch
        {
            _ when path.EndsWith("/v1/messages", StringComparison.Ordinal) => new JsonObject
            {
                ["content"] = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = Answer } },
                ["usage"] = new JsonObject { ["input_tokens"] = InputTokens, ["output_tokens"] = OutputTokens },
                ["stop_reason"] = "end_turn",
            },
            _ when path.EndsWith(":generateContent", StringComparison.Ordinal) => new JsonObject
            {
                ["candidates"] = new JsonArray { new JsonObject
                {
                    ["content"] = new JsonObject { ["parts"] = new JsonArray { new JsonObject { ["text"] = Answer } }, ["role"] = "model" },
                    ["finishReason"] = "STOP",
                } },
                ["usageMetadata"] = new JsonObject { ["promptTokenCount"] = InputTokens, ["candidatesTokenCount"] = OutputTokens },
            },
            _ when path.EndsWith("/chat/completions", StringComparison.Ordinal) => new JsonObject
            {
                ["choices"] = new JsonArray { new JsonObject
                {
                    ["message"] = new JsonObject { ["role"] = "assistant", ["content"] = Answer },
                    ["finish_reason"] = "stop",
                } },
                ["usage"] = new JsonObject { ["prompt_tokens"] = InputTokens, ["completion_tokens"] = OutputTokens },
            },
            _ when path.EndsWith("/models", StringComparison.Ordinal) && path.Contains("v1beta", StringComparison.Ordinal) => new JsonObject
            {
                ["models"] = new JsonArray
                {
                    new JsonObject { ["name"] = "models/gemini-test", ["supportedGenerationMethods"] = new JsonArray { "generateContent" } },
                    new JsonObject { ["name"] = "models/embedding-test", ["supportedGenerationMethods"] = new JsonArray { "embedContent" } },
                },
            },
            _ when path.EndsWith("/models", StringComparison.Ordinal) => new JsonObject
            {
                ["data"] = new JsonArray { new JsonObject { ["id"] = "model-b" }, new JsonObject { ["id"] = "model-a" } },
            },
            _ => new JsonObject { ["error"] = "not found" },
        };
        await context.Response.WriteAsync(answer.ToJsonString());
    }
}

internal static class AiTestSupport
{
    /// <summary>A provider client that may reach test servers on this computer over plain HTTP.</summary>
    public static AiProviderClient LocalClient(TimeSpan? timeout = null)
    {
        var options = new AiOptions { Timeout = timeout ?? TimeSpan.FromSeconds(10) };
        options.Network.AllowInsecureHttp = true;
        return new AiProviderClient(options, new OutboundAddressPolicy(options.Network) { AllowLoopbackForTests = true });
    }

    /// <summary>Lets the API's AI gateway reach fake providers on this computer.</summary>
    public static ApiFactory WithLocalAi(this ApiFactory factory) =>
        factory.WithMasterKey().WithServices(services =>
        {
            services.RemoveAll<AiProviderClient>();
            services.AddSingleton(_ => LocalClient());
        });

    /// <summary>A scope of the API for <paramref name="tenant"/>, acting as <paramref name="user"/> (or the system).</summary>
    public static AsyncServiceScope ScopeFor(this ApiFactory factory, Tenant tenant, TestUser? user = null)
    {
        var scope = factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().SetTenant(tenant.Id, tenant.Slug);
        if (user is not null)
        {
            scope.ServiceProvider.GetRequiredService<AuditContext>().SetActor(AuditActorType.User, user.Id.ToString(), "Test user");
        }

        return scope;
    }
}
