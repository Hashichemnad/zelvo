using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using Zelvo.Ai;

namespace Zelvo.Api.Tests;

/// <summary>
/// Customers' own MCP servers (module 9, task E6): Settings → AI → MCP servers and Zelvo's AI
/// using their tools through the gateway, with read-only tools run on their own and tools that
/// change data held for a person's approval.
/// </summary>
public sealed class McpClientTests(DatabaseFixture database) : IAsyncDisposable
{
    private static readonly Uri Servers = new("/api/v1/ai/mcp-servers", UriKind.Relative);
    private readonly ApiFactory _factory = ApiFactory.ForDatabase(database).WithLocalAi().WithSetting("Ai:AllowInsecureHttp", "true");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    [Fact]
    public async Task Servers_are_added_with_an_encrypted_credential_and_their_tools_start_off()
    {
        var tenant = await database.CreateTenantAsync("mc");
        using var admin = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, "tenant-admin");
        await using var hr = await TestMcpServer.StartAsync();
        var credential = Guid.NewGuid().ToString("N");

        foreach (var url in new[] { "http://169.254.169.254/mcp", "ftp://hr.example.test/mcp", "not a url" })
        {
            using var refused = await admin.PostAsJsonAsync(Servers, new { name = "HR", url }, Ct);
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        }

        using var created = await admin.PostAsJsonAsync(Servers, new { name = "HR system", url = hr.Url.ToString(), authHeader = "Authorization", secret = credential }, Ct);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var server = await created.JsonAsync();
        Assert.True((bool)server["secretSet"]!);
        Assert.DoesNotContain(credential, server.ToJsonString(), StringComparison.Ordinal);
        var id = Guid.Parse((string)server["id"]!);
        var stored = await database.ScalarAsync<string>("SELECT secret_ciphertext FROM mcp_servers WHERE id = @id", ("id", id));
        Assert.StartsWith("zv1.", stored, StringComparison.Ordinal);

        var refreshed = await (await admin.PostAsync(new Uri($"{Servers}/{id}/refresh", UriKind.Relative), null, Ct)).JsonAsync();
        Assert.True((bool)refreshed["lastTestSucceeded"]!);
        Assert.Equal("Bearer " + credential, hr.Authorizations.Last());
        var tools = refreshed["tools"]!.AsArray();
        Assert.Equal(["lookup_employee", "update_address"], tools.Select(t => (string)t!["name"]!).Order(StringComparer.Ordinal));
        Assert.All(tools, t => Assert.False((bool)t!["enabled"]!));
        Assert.False((bool)tools.Single(t => (string?)t!["name"] == "lookup_employee")!["changesData"]!);
        Assert.True((bool)tools.Single(t => (string?)t!["name"] == "update_address")!["changesData"]!);

        // A new address needs the credential again.
        using var moved = await admin.PatchAsJsonAsync(new Uri($"{Servers}/{id}", UriKind.Relative), new { url = "https://elsewhere.example.test/mcp" }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, moved.StatusCode);

        // A wrong credential is reported by the test.
        using var wrong = await admin.PatchAsJsonAsync(new Uri($"{Servers}/{id}", UriKind.Relative), new { secret = Guid.NewGuid().ToString("N") }, Ct);
        Assert.Equal(HttpStatusCode.OK, wrong.StatusCode);
        hr.RequiredAuthorization = "Bearer " + credential;
        var failed = await (await admin.PostAsync(new Uri($"{Servers}/{id}/refresh", UriKind.Relative), null, Ct)).JsonAsync();
        Assert.False((bool)failed["lastTestSucceeded"]!);
        Assert.StartsWith("server-auth-failed", (string?)failed["lastTestError"], StringComparison.Ordinal);
        Assert.Equal(2, failed["tools"]!.AsArray().Count);

        // Only AI managers.
        using var agent = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, "agent");
        Assert.Equal(HttpStatusCode.Forbidden, (await agent.GetAsync(Servers, Ct)).StatusCode);
        Assert.True(await database.ScalarAsync<long>("SELECT count(*) FROM audit_events WHERE tenant_id = @t AND action = 'ai.mcpServer.secret-changed'", ("t", tenant.Id)) >= 2);
    }

    [Fact]
    public async Task Zelvo_ai_uses_read_only_tools_on_its_own_with_personal_data_masked_and_stops_for_tools_that_change_data()
    {
        var tenant = await database.CreateTenantAsync("mu");
        using var admin = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, "tenant-admin");
        await using var fake = await FakeAiProvider.StartAsync();
        await using var hr = await TestMcpServer.StartAsync();
        await EnableVirtualAgentAsync(admin, fake);
        var id = await AddServerAsync(admin, hr);
        using var tools = await admin.PutAsJsonAsync(new Uri($"{Servers}/{id}/tools", UriKind.Relative), new
        {
            tools = new[] { new { name = "lookup_employee", enabled = true, changesData = false }, new { name = "update_address", enabled = true, changesData = true } },
        }, Ct);
        Assert.Equal(HttpStatusCode.OK, tools.StatusCode);

        // The model looks someone up, then answers.
        fake.Script.Enqueue("""{"tool":"t1","arguments":{"email":"[EMAIL_1]"}}""");
        fake.Script.Enqueue("""{"answer":"Mariam works in Finance."}""");
        await using (var scope = _factory.ScopeFor(tenant))
        {
            var run = await scope.ServiceProvider.GetRequiredService<IAiToolRunner>().RunAsync(Request("Which department is mariam@example.test in?"), cancellationToken: Ct);
            Assert.Equal("Mariam works in Finance.", run.Answer);
            Assert.Null(run.ApprovalNeeded);
            var step = Assert.Single(run.Steps);
            Assert.Null(step.ErrorCode);
        }

        // The tool got the real address back; the provider saw only placeholders, also in the tool's answer.
        Assert.Equal("""{"email":"mariam@example.test"}""", hr.Calls.Single().Arguments);
        var sent = fake.Received.Select(r => r.Body).ToList();
        Assert.Equal(2, sent.Count);
        Assert.All(sent, body => Assert.DoesNotContain("mariam@example.test", body, StringComparison.Ordinal));
        Assert.DoesNotContain("+971 50 123 4567", sent[1], StringComparison.Ordinal);
        Assert.Contains("tool-result", sent[1], StringComparison.Ordinal);
        Assert.Contains("available-tools", sent[0], StringComparison.Ordinal);

        // A tool that changes data is not run: the feature asks a person first.
        fake.Script.Enqueue("""{"tool":"t2","arguments":{"employee":"E-17","address":"Al Reem Island"}}""");
        await using (var scope = _factory.ScopeFor(tenant))
        {
            var runner = scope.ServiceProvider.GetRequiredService<IAiToolRunner>();
            var run = await runner.RunAsync(Request("Change my address to Al Reem Island"), cancellationToken: Ct);
            Assert.Null(run.Answer);
            Assert.NotNull(run.ApprovalNeeded);
            Assert.Equal("update_address", run.ApprovalNeeded!.Tool);
            Assert.Single(hr.Calls);

            var approved = await runner.CallApprovedAsync(run.ApprovalNeeded, Ct);
            Assert.Null(approved.ErrorCode);
            Assert.Equal(2, hr.Calls.Count);
        }

        Assert.Equal(2L, await database.ScalarAsync<long>("SELECT count(*) FROM audit_events WHERE tenant_id = @t AND action = 'ai.mcp.tool.called'", ("t", tenant.Id)));

        // Turned-off tools and disabled servers are not offered.
        using var off = await admin.PatchAsJsonAsync(new Uri($"{Servers}/{id}", UriKind.Relative), new { status = "Disabled" }, Ct);
        Assert.Equal(HttpStatusCode.OK, off.StatusCode);
        await using (var scope = _factory.ScopeFor(tenant))
        {
            Assert.Empty(await scope.ServiceProvider.GetRequiredService<IAiToolRunner>().ToolsAsync(Ct));
        }
    }

    private static AiRequest Request(string question) => new()
    {
        Feature = AiFeatures.VirtualAgent,
        Instructions = "Answer the person's question.",
        Conversation = [new AiMessage(AiRole.User, question)],
    };

    private static async Task EnableVirtualAgentAsync(HttpClient admin, FakeAiProvider fake)
    {
        using var created = await admin.PostAsJsonAsync(new Uri("/api/v1/ai/providers", UriKind.Relative),
            new { name = "Local model", kind = "OpenAICompatible", endpoint = new Uri(fake.Url, "v1").ToString(), apiKey = Guid.NewGuid().ToString("N") }, Ct);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var providerId = Guid.Parse((string)(await created.JsonAsync())["id"]!);
        using var feature = await admin.PutAsJsonAsync(new Uri("/api/v1/ai/features/virtualAgent", UriKind.Relative), new { enabled = true, providerId, model = "model-a" }, Ct);
        Assert.Equal(HttpStatusCode.OK, feature.StatusCode);
    }

    private static async Task<Guid> AddServerAsync(HttpClient admin, TestMcpServer server)
    {
        using var created = await admin.PostAsJsonAsync(Servers, new { name = "HR system", url = server.Url.ToString() }, Ct);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = Guid.Parse((string)(await created.JsonAsync())["id"]!);
        using var refreshed = await admin.PostAsync(new Uri($"{Servers}/{id}/refresh", UriKind.Relative), null, Ct);
        Assert.True((bool)(await refreshed.JsonAsync())["lastTestSucceeded"]!);
        return id;
    }
}

/// <summary>A customer's MCP server on this computer with a read-only and a changing tool; records what it got.</summary>
internal sealed class TestMcpServer : IAsyncDisposable
{
    private readonly WebApplication _app;

    private TestMcpServer(WebApplication app) => _app = app;

    public Uri Url { get; private set; } = null!;

    public ConcurrentQueue<string> Authorizations { get; } = new();

    public ConcurrentQueue<(string Tool, string Arguments)> Calls { get; } = new();

    /// <summary>When set, requests without this Authorization header are refused (401).</summary>
    public string? RequiredAuthorization { get; set; }

    public static async Task<TestMcpServer> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        TestMcpServer? server = null;
        builder.Services.AddMcpServer()
            .WithHttpTransport(o => o.Stateless = true)
            .WithListToolsHandler((_, _) => ValueTask.FromResult(new ListToolsResult
            {
                Tools =
                [
                    new Tool
                    {
                        Name = "lookup_employee",
                        Description = "Finds an employee by email address.",
                        InputSchema = JsonSerializer.SerializeToElement(new { type = "object", properties = new { email = new { type = "string" } } }),
                        Annotations = new ToolAnnotations { ReadOnlyHint = true },
                    },
                    new Tool
                    {
                        Name = "update_address",
                        Description = "Changes an employee's home address.",
                        InputSchema = JsonSerializer.SerializeToElement(new { type = "object", properties = new { employee = new { type = "string" }, address = new { type = "string" } } }),
                    },
                ],
            }))
            .WithCallToolHandler((request, _) =>
            {
                var args = JsonSerializer.Serialize(request.Params!.Arguments);
                server!.Calls.Enqueue((request.Params.Name, args));
                var text = request.Params.Name == "lookup_employee"
                    ? "Mariam Al Hashimi, Finance, mariam@example.test, +971 50 123 4567"
                    : "Address changed.";
                return ValueTask.FromResult(new CallToolResult { Content = [new TextContentBlock { Text = text }] });
            });
        var app = builder.Build();
        server = new TestMcpServer(app);
        app.Use(async (context, next) =>
        {
            var authorization = context.Request.Headers.Authorization.ToString();
            server.Authorizations.Enqueue(authorization);
            if (server.RequiredAuthorization is { } required && authorization != required)
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            await next(context);
        });
        app.MapMcp("/mcp");
        await app.StartAsync();
        server.Url = new Uri(app.Urls.First() + "/mcp");
        return server;
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
