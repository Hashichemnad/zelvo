using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Zelvo.Data.Tenancy;

namespace Zelvo.Api.Tests;

/// <summary>
/// The built-in MCP server (module 9, task E6), driven by the official MCP client over Streamable
/// HTTP: agents with their own credentials, tools per agent and role, approvals for changes, the
/// AI-agent actor in the audit log.
/// </summary>
public sealed class McpServerTests(DatabaseFixture database) : IAsyncDisposable
{
    private static readonly Uri Mcp = new("/api/v1/ai/mcp", UriKind.Relative);
    private static readonly Uri Approvals = new("/api/v1/ai/approvals", UriKind.Relative);
    private readonly ApiFactory _factory = ApiFactory.ForDatabase(database);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    [Fact]
    public async Task Only_integrations_set_up_as_ai_agents_reach_the_server_and_only_while_it_is_on()
    {
        var tenant = await database.CreateTenantAsync("mo");
        using var admin = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, "tenant-admin");
        var (key, id) = await admin.CreateApiKeyAsync("agent", "incidents.read");

        // An ordinary integration, people and visitors are refused.
        Assert.Equal("mcp-off", await RawCodeAsync(_factory.ForTenant(tenant).WithApiKey(key)));
        await TurnOnAsync(admin);
        Assert.Equal("not-an-ai-agent", await RawCodeAsync(_factory.ForTenant(tenant).WithApiKey(key)));
        Assert.Equal("not-an-ai-agent", await RawCodeAsync(admin));
        Assert.Equal("unauthenticated", await RawCodeAsync(_factory.ForTenant(tenant)));

        await SetAgentAsync(admin, id, ["search_incidents"]);
        await using var client = await ConnectAsync(tenant, key);
        Assert.Equal(["get_action_status", "search_incidents"], (await client.ListToolsAsync(cancellationToken: Ct)).Select(t => t.Name).Order(StringComparer.Ordinal));

        // Removing the agent refuses it at once.
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync(new Uri($"{Mcp}/agents/{id}", UriKind.Relative), Ct)).StatusCode);
        Assert.Equal("not-an-ai-agent", await RawCodeAsync(_factory.ForTenant(tenant).WithApiKey(key)));
    }

    [Fact]
    public async Task Tools_need_their_permission_in_the_role_and_scopes_and_only_turned_on_tools_are_offered()
    {
        var tenant = await database.CreateTenantAsync("mt");
        using var admin = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, "tenant-admin");
        await TurnOnAsync(admin);
        var (key, id) = await admin.CreateApiKeyAsync("agent", "incidents.read");

        using var refused = await admin.PutAsJsonAsync(new Uri($"{Mcp}/agents/{id}", UriKind.Relative), new { tools = new[] { "create_incident" } }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        using var approvalOnReadTool = await admin.PutAsJsonAsync(new Uri($"{Mcp}/agents/{id}", UriKind.Relative),
            new { tools = new[] { "get_incident" }, toolsWithoutApproval = new[] { "get_incident" } }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, approvalOnReadTool.StatusCode);

        var agent = await SetAgentAsync(admin, id, ["get_incident", "search_knowledge"]);
        Assert.Contains("search_incidents", agent["allowedTools"]!.AsArray().Select(t => (string?)t));
        Assert.DoesNotContain("create_incident", agent["allowedTools"]!.AsArray().Select(t => (string?)t));

        await using var client = await ConnectAsync(tenant, key);
        var tools = await client.ListToolsAsync(cancellationToken: Ct);
        Assert.Equal(["get_action_status", "get_incident", "search_knowledge"], tools.Select(t => t.Name).Order(StringComparer.Ordinal));
        Assert.True(tools.Single(t => t.Name == "get_incident").ProtocolTool.Annotations?.ReadOnlyHint);

        var notOn = await client.CallToolAsync("search_incidents", new Dictionary<string, object?>(), cancellationToken: Ct);
        Assert.True(notOn.IsError);
        Assert.Equal("tool-not-available", (string?)Content(notOn)["code"]);

        var missing = await client.CallToolAsync("get_incident", new Dictionary<string, object?> { ["incident"] = "INC-999999" }, cancellationToken: Ct);
        Assert.True(missing.IsError);
        Assert.Equal("not-found", (string?)Content(missing)["code"]);
    }

    [Fact]
    public async Task A_change_waits_for_a_person_with_its_permission_and_runs_as_the_agent_when_approved()
    {
        var tenant = await database.CreateTenantAsync("ma");
        using var admin = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, "tenant-admin");
        await TurnOnAsync(admin);
        var requester = await database.CreateUserAsync(tenant, "requester");
        var (key, id) = await admin.CreateApiKeyAsync("agent", "incidents.read", "incidents.create", "incidents.edit");
        await SetAgentAsync(admin, id, ["create_incident", "get_incident"]);

        await using var client = await ConnectAsync(tenant, key);
        var asked = await client.CallToolAsync("create_incident", new Dictionary<string, object?>
        {
            ["title"] = "Printer on floor 3 is offline",
            ["description"] = "Reported to the virtual assistant",
            ["requesterEmail"] = requester.Email.ToUpperInvariant(),
            ["impact"] = 2,
            ["urgency"] = 1,
        }, cancellationToken: Ct);
        Assert.False(asked.IsError);
        var pending = Content(asked);
        Assert.Equal("pending-approval", (string?)pending["status"]);
        var actionId = (string)pending["actionId"]!;
        Assert.Equal(0L, await database.ScalarAsync<long>("SELECT count(*) FROM tickets WHERE tenant_id = @t", ("t", tenant.Id)));

        // Bad arguments are refused before anything is queued.
        var invalid = await client.CallToolAsync("create_incident", new Dictionary<string, object?> { ["title"] = "No", ["requesterEmail"] = "nobody@example.test" }, cancellationToken: Ct);
        Assert.True(invalid.IsError);
        Assert.Equal("validation-failed", (string?)Content(invalid)["code"]);

        var status = await client.CallToolAsync("get_action_status", new Dictionary<string, object?> { ["actionId"] = actionId }, cancellationToken: Ct);
        Assert.Equal("Pending", (string?)Content(status)["status"]);

        // A requester cannot see it; an agent (incidents.create) can, and approves it.
        using var other = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, "requester");
        Assert.Equal(0, (int?)(await (await other.GetAsync(Approvals, Ct)).JsonAsync())["totalCount"]);
        Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsJsonAsync(new Uri($"{Approvals}/{actionId}/approve", UriKind.Relative), new { }, Ct)).StatusCode);

        var approverUser = await database.CreateUserAsync(tenant, "agent");
        using var approver = _factory.ForTenant(tenant).As(approverUser);
        var queue = await (await approver.GetAsync(Approvals, Ct)).JsonAsync();
        var item = queue["items"]!.AsArray().Single()!;
        Assert.Equal(actionId, (string?)item["id"]);
        Assert.Equal("CRM sync", (string?)item["agent"]!["name"]);
        Assert.Contains("Printer on floor 3", (string?)item["summary"], StringComparison.Ordinal);

        using var approved = await approver.PostAsJsonAsync(new Uri($"{Approvals}/{actionId}/approve", UriKind.Relative), new { note = "Fine" }, Ct);
        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
        var decided = await approved.JsonAsync();
        Assert.Equal("Succeeded", (string?)decided["status"]);
        Assert.StartsWith("INC-", (string?)decided["ticket"]!["name"], StringComparison.Ordinal);

        // Approving twice is refused.
        Assert.Equal(HttpStatusCode.Conflict, (await approver.PostAsJsonAsync(new Uri($"{Approvals}/{actionId}/approve", UriKind.Relative), new { }, Ct)).StatusCode);

        // The incident records the agent, the requester and the AI-agent channel.
        var ticketId = Guid.Parse((string)decided["ticket"]!["id"]!);
        Assert.Equal("AiAgent", await database.ScalarAsync<string>("SELECT channel FROM tickets WHERE id = @id", ("id", ticketId)));
        Assert.Equal(id, await database.ScalarAsync<Guid>("SELECT created_by_api_client_id FROM tickets WHERE id = @id", ("id", ticketId)));
        Assert.Equal(requester.Id, await database.ScalarAsync<Guid>("SELECT requester_id FROM tickets WHERE id = @id", ("id", ticketId)));
        Assert.Equal(1, await database.ScalarAsync<int>("SELECT priority FROM incidents WHERE ticket_id = @id", ("id", ticketId)) is > 0 ? 1 : 0);

        // The audit log marks the change as done by the AI agent and names the approver.
        var actor = await database.ScalarAsync<string>(
            "SELECT actor_type || '|' || actor_id || '|' || (metadata->>'approvedBy') FROM audit_events WHERE tenant_id = @t AND entity_type = 'ticket' AND entity_id = @id AND category = 'Create'",
            ("t", tenant.Id), ("id", ticketId.ToString()));
        Assert.Equal($"AiAgent|{id}|{approverUser.Id}", actor);

        var done = await client.CallToolAsync("get_action_status", new Dictionary<string, object?> { ["actionId"] = actionId }, cancellationToken: Ct);
        Assert.Equal("Succeeded", (string?)Content(done)["status"]);
        Assert.Equal("Fine", (string?)Content(done)["note"]);

        var read = await client.CallToolAsync("get_incident", new Dictionary<string, object?> { ["incident"] = (string)decided["ticket"]!["name"]! }, cancellationToken: Ct);
        Assert.False(read.IsError);
        Assert.Equal("AiAgent", (string?)Content(read)["channel"]);

        // Administrators see the agent's activity.
        var activity = await (await admin.GetAsync(new Uri($"{Mcp}/actions?agentId={id}", UriKind.Relative), Ct)).JsonAsync();
        Assert.Equal(1, (int?)activity["totalCount"]);
    }

    [Fact]
    public async Task Rejected_changes_change_nothing_and_changes_without_approval_run_at_once()
    {
        var tenant = await database.CreateTenantAsync("mr");
        using var admin = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, "tenant-admin");
        await TurnOnAsync(admin);
        var requester = await database.CreateUserAsync(tenant, "requester");
        var (key, id) = await admin.CreateApiKeyAsync("agent", "incidents.read", "incidents.create", "incidents.edit");
        await SetAgentAsync(admin, id, ["create_incident", "update_incident"]);
        await using var client = await ConnectAsync(tenant, key);

        var asked = await client.CallToolAsync("create_incident", new Dictionary<string, object?> { ["title"] = "VPN keeps dropping", ["requesterEmail"] = requester.Email }, cancellationToken: Ct);
        var actionId = (string)Content(asked)["actionId"]!;
        using var approver = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, "agent");
        using var rejected = await approver.PostAsJsonAsync(new Uri($"{Approvals}/{actionId}/reject", UriKind.Relative), new { note = "Duplicate of INC-000001" }, Ct);
        Assert.Equal("Rejected", (string?)(await rejected.JsonAsync())["status"]);
        Assert.Equal(0L, await database.ScalarAsync<long>("SELECT count(*) FROM tickets WHERE tenant_id = @t", ("t", tenant.Id)));
        var status = Content(await client.CallToolAsync("get_action_status", new Dictionary<string, object?> { ["actionId"] = actionId }, cancellationToken: Ct));
        Assert.Equal("Rejected", (string?)status["status"]);
        Assert.Equal("Duplicate of INC-000001", (string?)status["note"]);

        // Approval turned off for this agent and tool: the change runs now and is still recorded.
        await SetAgentAsync(admin, id, ["create_incident", "update_incident"], ["create_incident"]);
        var created = await client.CallToolAsync("create_incident", new Dictionary<string, object?> { ["title"] = "Laptop will not start", ["requesterEmail"] = requester.Email }, cancellationToken: Ct);
        Assert.False(created.IsError);
        var incident = Content(created);
        Assert.StartsWith("INC-", (string?)incident["number"], StringComparison.Ordinal);
        Assert.NotNull(incident["actionId"]);

        // Other change tools still wait.
        var update = await client.CallToolAsync("update_incident", new Dictionary<string, object?> { ["incident"] = (string)incident["number"]!, ["urgency"] = 1 }, cancellationToken: Ct);
        Assert.Equal("pending-approval", (string?)Content(update)["status"]);
        Assert.Contains((string)incident["number"]!, (string?)Content(update)["summary"], StringComparison.Ordinal);

        // Agents only see their own actions.
        var (otherKey, otherId) = await admin.CreateApiKeyAsync("agent", "incidents.read");
        await SetAgentAsync(admin, otherId, []);
        await using var otherAgent = await ConnectAsync(tenant, otherKey);
        var foreign = await otherAgent.CallToolAsync("get_action_status", new Dictionary<string, object?> { ["actionId"] = actionId }, cancellationToken: Ct);
        Assert.Equal("not-found", (string?)Content(foreign)["code"]);
    }

    [Fact]
    public async Task Expired_requests_cannot_be_approved()
    {
        var tenant = await database.CreateTenantAsync("me");
        using var admin = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, "tenant-admin");
        await TurnOnAsync(admin);
        var requester = await database.CreateUserAsync(tenant, "requester");
        var (key, id) = await admin.CreateApiKeyAsync("agent", "incidents.read", "incidents.create");
        await SetAgentAsync(admin, id, ["create_incident"]);
        await using var client = await ConnectAsync(tenant, key);
        var actionId = (string)Content(await client.CallToolAsync("create_incident",
            new Dictionary<string, object?> { ["title"] = "Screen flickers", ["requesterEmail"] = requester.Email }, cancellationToken: Ct))["actionId"]!;

        await database.ExecuteAsOwnerAsync("UPDATE mcp_actions SET expires_at = now() - interval '1 minute' WHERE id = @id", ("id", Guid.Parse(actionId)));
        using var approver = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, "agent");
        Assert.Equal(HttpStatusCode.Conflict, (await approver.PostAsJsonAsync(new Uri($"{Approvals}/{actionId}/approve", UriKind.Relative), new { }, Ct)).StatusCode);
        Assert.Equal("Expired", (string?)Content(await client.CallToolAsync("get_action_status", new Dictionary<string, object?> { ["actionId"] = actionId }, cancellationToken: Ct))["status"]);
        Assert.Equal(0, (int?)(await (await approver.GetAsync(Approvals, Ct)).JsonAsync())["totalCount"]);
    }

    private async Task<McpClient> ConnectAsync(Tenant tenant, string key)
    {
        var http = _factory.CreateClient();
        http.DefaultRequestHeaders.Add("X-Zelvo-Tenant", tenant.Slug);
        http.DefaultRequestHeaders.Add("X-Api-Key", key);
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions { Endpoint = new Uri(http.BaseAddress!, "/api/v1/mcp"), TransportMode = HttpTransportMode.StreamableHttp },
            http, NullLoggerFactory.Instance, ownsHttpClient: true);
        return await McpClient.CreateAsync(transport, cancellationToken: Ct);
    }

    private static async Task<string?> RawCodeAsync(HttpClient client)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri("/api/v1/mcp", UriKind.Relative))
        {
            Content = JsonContent.Create(new { jsonrpc = "2.0", id = 1, method = "tools/list" }),
        };
        request.Headers.Accept.ParseAdd("application/json, text/event-stream");
        using var response = await client.SendAsync(request, Ct);
        Assert.True(response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden, $"Expected a refusal, got {response.StatusCode}.");
        return (await response.Content.ReadFromJsonAsync<ProblemBody>(Ct))?.Code;
    }

    private static async Task TurnOnAsync(HttpClient admin)
    {
        using var response = await admin.PutAsJsonAsync(new Uri($"{Mcp}/settings", UriKind.Relative), new { enabled = true }, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static async Task<JsonNode> SetAgentAsync(HttpClient admin, Guid clientId, string[] tools, string[]? withoutApproval = null)
    {
        using var response = await admin.PutAsJsonAsync(new Uri($"{Mcp}/agents/{clientId}", UriKind.Relative),
            new { tools, toolsWithoutApproval = withoutApproval ?? [] }, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.JsonAsync();
    }

    private static JsonObject Content(CallToolResult result) =>
        JsonNode.Parse(((TextContentBlock)result.Content.Single()).Text)!.AsObject();
}
