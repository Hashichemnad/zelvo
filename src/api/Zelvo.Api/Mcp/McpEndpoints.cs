using System.ComponentModel.DataAnnotations;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Zelvo.Api.Conventions;
using Zelvo.Api.Identity;
using Zelvo.Api.Integrations;
using Zelvo.Api.Settings;
using Zelvo.Api.Tenancy;
using Zelvo.Data;
using Zelvo.Data.Integrations;
using Zelvo.Data.Mcp;

namespace Zelvo.Api.Mcp;

/// <summary>
/// Settings → AI → MCP server (module 9, task E6): the server switch, which integrations are AI
/// agents with which tools and approvals, and what the agents did; and the approval queue where
/// people approve or reject the changes agents asked for.
/// </summary>
internal static class McpEndpoints
{
    public static RouteGroupBuilder MapMcpEndpoints(this RouteGroupBuilder group)
    {
        var mcp = group.MapGroup("/ai/mcp").WithTags("AI").RequireTenant();
        mcp.MapGet("/", GetAsync)
            .WithName("GetMcpServer")
            .WithSummary("The built-in MCP server: whether it is on, its endpoint, the tools it has (with the permission each needs and whether it changes data) and the integrations set up as AI agents. Needs ai.manage.")
            .RequirePermission(Permissions.AiManage)
            .Produces<McpServerResponse>();

        mcp.MapPut("/settings", UpdateSettingsAsync)
            .WithName("UpdateMcpServerSettings")
            .WithSummary("Turns the MCP server on or off for the tenant. Off by default. Needs ai.manage; audited.")
            .RequirePermission(Permissions.AiManage)
            .Produces<McpServerResponse>()
            .ProducesValidationProblem();

        mcp.MapPut("/agents/{clientId:guid}", SetAgentAsync)
            .WithName("SetMcpAgent")
            .WithSummary("Makes an active integration (API key or OAuth client) an AI agent of the MCP server, or changes its tools. Each tool needs its permission in the integration's role and scopes. Tools that change data need a person's approval unless listed in toolsWithoutApproval. Needs ai.manage; audited.")
            .RequirePermission(Permissions.AiManage)
            .Produces<McpAgentResponse>()
            .ProducesValidationProblem();

        mcp.MapDelete("/agents/{clientId:guid}", RemoveAgentAsync)
            .WithName("RemoveMcpAgent")
            .WithSummary("The integration is no longer an AI agent: the MCP server refuses it at once (the integration itself stays). Needs ai.manage; audited.")
            .RequirePermission(Permissions.AiManage)
            .Produces(StatusCodes.Status204NoContent);

        mcp.MapGet("/actions", ListActionsAsync)
            .WithName("ListMcpActions")
            .WithSummary("Every change AI agents asked for through the MCP server, newest first, with its approval and result. Filter by status or agent. Needs ai.manage.")
            .RequirePermission(Permissions.AiManage)
            .Produces<PageResult<McpActionResponse>>();

        var approvals = group.MapGroup("/ai/approvals").WithTags("AI").RequireTenant();
        approvals.MapGet("/", ListApprovalsAsync)
            .WithName("ListAiApprovals")
            .WithSummary("Changes AI agents asked for that the caller may decide: those whose permission the caller holds (for example incidents.edit). Waiting ones by default, oldest first; status=all for every one, newest first. People only.")
            .RequirePerson()
            .Produces<PageResult<McpActionResponse>>();

        approvals.MapGet("/{id:guid}", GetApprovalAsync)
            .WithName("GetAiApproval")
            .WithSummary("One change an AI agent asked for, with its arguments, if the caller holds its permission. People only.")
            .RequirePerson()
            .Produces<McpActionResponse>();

        approvals.MapPost("/{id:guid}/approve", ApproveAsync)
            .WithName("ApproveAiAction")
            .WithSummary("Approves a waiting change: it is carried out now, as the AI agent (its permissions; the audit log names the agent and the approver). Needs the change's permission; people only; audited.")
            .RequirePerson()
            .Produces<McpActionResponse>();

        approvals.MapPost("/{id:guid}/reject", RejectAsync)
            .WithName("RejectAiAction")
            .WithSummary("Rejects a waiting change, with an optional note the agent can read; nothing is changed. Needs the change's permission; people only; audited.")
            .RequirePerson()
            .Produces<McpActionResponse>();

        return group;
    }

    internal static async Task<IResult> GetAsync(ZelvoDbContext db, TenantSettingsStore settings, CancellationToken cancellationToken) =>
        TypedResults.Ok(await ServerAsync(db, settings, cancellationToken));

    internal static async Task<IResult> UpdateSettingsAsync(UpdateMcpSettingsRequest request, ZelvoDbContext db, TenantSettingsStore settings, CancellationToken cancellationToken)
    {
        await settings.SetAsync(McpSettings.Enabled, request.Enabled!.Value, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Ok(await ServerAsync(db, settings, cancellationToken));
    }

    internal static async Task<IResult> SetAgentAsync(Guid clientId, SetMcpAgentRequest request, ZelvoDbContext db, CancellationToken cancellationToken)
    {
        var client = await db.ApiClients.AsNoTracking().SingleOrDefaultAsync(c => c.Id == clientId, cancellationToken);
        if (client is null)
        {
            return ApiProblem.Create(StatusCodes.Status404NotFound, "not-found", "There is no such integration.");
        }

        if (client.Status != ApiClientStatus.Active)
        {
            return ApiProblem.Create(StatusCodes.Status409Conflict, "integration-revoked", "A revoked integration cannot be an AI agent.");
        }

        var allowed = await AllowedToolsAsync(db, client, cancellationToken);
        var tools = request.Tools!.Distinct(StringComparer.Ordinal).ToList();
        if (tools.FirstOrDefault(t => McpTools.Find(t) is null || t == McpTools.ActionStatusTool) is { } unknown)
        {
            return UserEndpoints.Validation("tools", $"Unknown tool '{unknown}'.");
        }

        if (tools.FirstOrDefault(t => !allowed.Contains(t)) is { } notAllowed)
        {
            return UserEndpoints.Validation("tools", $"The integration's role and scopes do not allow '{notAllowed}' (it needs {McpTools.Find(notAllowed)!.Permission}).");
        }

        var withoutApproval = (request.ToolsWithoutApproval ?? []).Distinct(StringComparer.Ordinal).ToList();
        if (withoutApproval.FirstOrDefault(t => !tools.Contains(t) || McpTools.Find(t) is not { ChangesData: true }) is { } wrong)
        {
            return UserEndpoints.Validation("toolsWithoutApproval", $"'{wrong}' is not a turned-on tool that changes data.");
        }

        var agent = await db.McpAgents.SingleOrDefaultAsync(a => a.ApiClientId == clientId, cancellationToken);
        if (agent is null)
        {
            agent = new McpAgent { ApiClientId = clientId };
            db.McpAgents.Add(agent);
        }

        agent.Tools = [.. tools.Order(StringComparer.Ordinal)];
        agent.ToolsWithoutApproval = [.. withoutApproval.Order(StringComparer.Ordinal)];
        agent.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Ok(await AgentResponseAsync(db, client, agent, cancellationToken));
    }

    internal static async Task<IResult> RemoveAgentAsync(Guid clientId, ZelvoDbContext db, CancellationToken cancellationToken)
    {
        var agent = await db.McpAgents.SingleOrDefaultAsync(a => a.ApiClientId == clientId, cancellationToken);
        if (agent is null)
        {
            return ApiProblem.Create(StatusCodes.Status404NotFound, "not-found", "This integration is not an AI agent.");
        }

        db.McpAgents.Remove(agent);
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.NoContent();
    }

    internal static async Task<IResult> ListActionsAsync(
        [AsParameters] PageQuery page, [AsParameters] McpActionListQuery filter, ZelvoDbContext db, CancellationToken cancellationToken)
    {
        var query = Filter(db.McpActions.AsNoTracking(), filter.Status);
        if (filter.AgentId is { } agentId)
        {
            query = query.Where(a => a.ApiClientId == agentId);
        }

        return TypedResults.Ok(await PageAsync(db, query.OrderByDescending(a => a.RequestedAt).ThenByDescending(a => a.Id), page, cancellationToken));
    }

    internal static async Task<IResult> ListApprovalsAsync(
        [AsParameters] PageQuery page, [AsParameters] McpApprovalListQuery filter, ZelvoDbContext db, CurrentUser user, CancellationToken cancellationToken)
    {
        var permissions = user.Permissions.ToList();
        var query = db.McpActions.AsNoTracking().Where(a => a.ApprovalRequired && permissions.Contains(a.Permission));
        if (filter.Status == "all")
        {
            return TypedResults.Ok(await PageAsync(db, query.OrderByDescending(a => a.RequestedAt).ThenByDescending(a => a.Id), page, cancellationToken));
        }

        var waiting = Filter(query, McpActionStatus.Pending).Where(a => a.DecidedAt == null);
        return TypedResults.Ok(await PageAsync(db, waiting.OrderBy(a => a.RequestedAt).ThenBy(a => a.Id), page, cancellationToken));
    }

    internal static async Task<IResult> GetApprovalAsync(Guid id, ZelvoDbContext db, CurrentUser user, CancellationToken cancellationToken)
    {
        var action = await DecidableAsync(db, user, id, cancellationToken);
        return action is null ? NotFound() : TypedResults.Ok(await ResponseAsync(db, action, cancellationToken));
    }

    internal static Task<IResult> ApproveAsync(Guid id, DecideAiActionRequest? request, ZelvoDbContext db, CurrentUser user, McpRunner runner, CancellationToken cancellationToken) =>
        DecideAsync(id, request, db, user, (action, approver) => runner.ApproveAsync(action, approver, request?.Note, cancellationToken), cancellationToken);

    internal static Task<IResult> RejectAsync(Guid id, DecideAiActionRequest? request, ZelvoDbContext db, CurrentUser user, McpRunner runner, CancellationToken cancellationToken) =>
        DecideAsync(id, request, db, user, (action, approver) => runner.RejectAsync(action, approver, request?.Note, cancellationToken), cancellationToken);

    private static async Task<IResult> DecideAsync(
        Guid id, DecideAiActionRequest? request, ZelvoDbContext db, CurrentUser user,
        Func<Guid, Data.Identity.UserAccount, Task<McpActionDecision>> decide, CancellationToken cancellationToken)
    {
        if (request?.Note is { Length: > McpAction.NoteMaxLength })
        {
            return UserEndpoints.Validation("note", $"Use at most {McpAction.NoteMaxLength} characters.");
        }

        if (await DecidableAsync(db, user, id, cancellationToken) is null)
        {
            return NotFound();
        }

        var decision = await decide(id, user.Account!);
        if (decision == McpActionDecision.NotFound)
        {
            return NotFound();
        }

        if (decision == McpActionDecision.NotPending)
        {
            return ApiProblem.Create(StatusCodes.Status409Conflict, "action-not-pending", "This change was already decided, is being decided, or has expired.");
        }

        var action = await db.McpActions.AsNoTracking().SingleAsync(a => a.Id == id, cancellationToken);
        return TypedResults.Ok(await ResponseAsync(db, action, cancellationToken));
    }

    private static Task<McpAction?> DecidableAsync(ZelvoDbContext db, CurrentUser user, Guid id, CancellationToken cancellationToken)
    {
        var permissions = user.Permissions.ToList();
        return db.McpActions.AsNoTracking().SingleOrDefaultAsync(a => a.Id == id && a.ApprovalRequired && permissions.Contains(a.Permission), cancellationToken);
    }

    private static IQueryable<McpAction> Filter(IQueryable<McpAction> query, McpActionStatus? status)
    {
        var now = DateTimeOffset.UtcNow;
        return status switch
        {
            null => query,
            McpActionStatus.Pending => query.Where(a => a.Status == McpActionStatus.Pending && (a.ExpiresAt == null || a.ExpiresAt > now)),
            McpActionStatus.Expired => query.Where(a => a.Status == McpActionStatus.Expired
                || (a.Status == McpActionStatus.Pending && a.DecidedAt == null && a.ExpiresAt != null && a.ExpiresAt <= now)),
            { } s => query.Where(a => a.Status == s),
        };
    }

    private static async Task<PageResult<McpActionResponse>> PageAsync(ZelvoDbContext db, IQueryable<McpAction> query, PageQuery page, CancellationToken cancellationToken)
    {
        var total = await query.LongCountAsync(cancellationToken);
        var items = await query.Skip(page.Skip).Take(page.Size).ToListAsync(cancellationToken);
        var lookups = await LookupsAsync(db, items, cancellationToken);
        return new PageResult<McpActionResponse>([.. items.Select(a => McpActionResponse.From(a, lookups))], page.Number, page.Size, total);
    }

    private static async Task<McpActionResponse> ResponseAsync(ZelvoDbContext db, McpAction action, CancellationToken cancellationToken) =>
        McpActionResponse.From(action, await LookupsAsync(db, [action], cancellationToken));

    private static async Task<ActionLookups> LookupsAsync(ZelvoDbContext db, IReadOnlyCollection<McpAction> actions, CancellationToken cancellationToken)
    {
        var clientIds = actions.Select(a => a.ApiClientId).Distinct().ToList();
        var userIds = actions.Where(a => a.DecidedById is not null).Select(a => a.DecidedById!.Value).Distinct().ToList();
        var ticketIds = actions.Where(a => a.TicketId is not null).Select(a => a.TicketId!.Value).Distinct().ToList();
        return new ActionLookups(
            await db.ApiClients.AsNoTracking().Where(c => clientIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Name, cancellationToken),
            await db.Users.AsNoTracking().Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName ?? u.Email, cancellationToken),
            await db.Tickets.AsNoTracking().Where(t => ticketIds.Contains(t.Id)).ToDictionaryAsync(t => t.Id, t => t.Number, cancellationToken));
    }

    private static async Task<McpServerResponse> ServerAsync(ZelvoDbContext db, TenantSettingsStore settings, CancellationToken cancellationToken)
    {
        var agents = await db.McpAgents.AsNoTracking().ToListAsync(cancellationToken);
        var clientIds = agents.Select(a => a.ApiClientId).ToList();
        var clients = await db.ApiClients.AsNoTracking().Where(c => clientIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, cancellationToken);
        var responses = new List<McpAgentResponse>();
        foreach (var agent in agents)
        {
            responses.Add(await AgentResponseAsync(db, clients[agent.ApiClientId], agent, cancellationToken));
        }

        var pending = await Filter(db.McpActions.AsNoTracking(), McpActionStatus.Pending).CountAsync(a => a.DecidedAt == null, cancellationToken);
        return new McpServerResponse(
            await settings.GetAsync(McpSettings.Enabled, cancellationToken),
            McpServerSetup.Path,
            [.. McpTools.Configurable.Select(t => new McpToolResponse(t.Name, t.Description, t.Permission, t.ChangesData))],
            [.. responses.OrderBy(r => r.Name, StringComparer.CurrentCulture)],
            pending,
            (int)McpAction.ApprovalWindow.TotalHours);
    }

    private static async Task<McpAgentResponse> AgentResponseAsync(ZelvoDbContext db, ApiClient client, McpAgent agent, CancellationToken cancellationToken)
    {
        var role = await db.Roles.AsNoTracking().Where(r => r.Id == client.RoleId).Select(r => r.Key).SingleAsync(cancellationToken);
        return new McpAgentResponse(client.Id, client.Name, client.Kind, client.Status, client.IsUsable(DateTimeOffset.UtcNow), role,
            agent.Tools, agent.ToolsWithoutApproval, [.. (await AllowedToolsAsync(db, client, cancellationToken)).Order(StringComparer.Ordinal)], agent.UpdatedAt);
    }

    // Tools whose permission the integration's role holds and its scopes include (never the blocked ones).
    private static async Task<HashSet<string>> AllowedToolsAsync(ZelvoDbContext db, ApiClient client, CancellationToken cancellationToken)
    {
        var role = await db.Roles.AsNoTracking().Include(r => r.Permissions).SingleAsync(r => r.Id == client.RoleId, cancellationToken);
        var user = new CurrentUser();
        user.SetClient(client, role);
        return [.. McpTools.Configurable.Where(t => t.Permission is null || user.Has(t.Permission)).Select(t => t.Name)];
    }

    private static IResult NotFound() => ApiProblem.Create(StatusCodes.Status404NotFound, "not-found", "There is no such change waiting for you.");
}

internal sealed record ActionLookups(Dictionary<Guid, string> Agents, Dictionary<Guid, string?> People, Dictionary<Guid, string> Tickets);

/// <summary>Turns the MCP server on or off.</summary>
public sealed class UpdateMcpSettingsRequest
{
    /// <summary>Whether the tenant's AI agents may use the MCP server.</summary>
    [Required]
    public bool? Enabled { get; init; }
}

/// <summary>An AI agent's tools.</summary>
public sealed class SetMcpAgentRequest
{
    /// <summary>Tools turned on for the agent.</summary>
    [Required]
    [MaxLength(50)]
    public IReadOnlyList<string>? Tools { get; init; }

    /// <summary>Turned-on tools that change data and may run without a person's approval.</summary>
    [MaxLength(50)]
    public IReadOnlyList<string>? ToolsWithoutApproval { get; init; }
}

/// <summary>An approver's decision.</summary>
public sealed class DecideAiActionRequest
{
    /// <summary>Optional note; the agent can read it.</summary>
    public string? Note { get; init; }
}

/// <summary>Filter of the AI agent activity.</summary>
public sealed class McpActionListQuery
{
    /// <summary>Only this status.</summary>
    public McpActionStatus? Status { get; init; }

    /// <summary>Only this agent (integration id).</summary>
    public Guid? AgentId { get; init; }
}

/// <summary>Filter of the approval queue.</summary>
public sealed class McpApprovalListQuery
{
    /// <summary><c>pending</c> (default) or <c>all</c>.</summary>
    [AllowedValues(null, "pending", "all")]
    public string? Status { get; init; }
}

/// <summary>The MCP server as administrators see it.</summary>
internal sealed record McpServerResponse(
    bool Enabled, string Endpoint, IReadOnlyList<McpToolResponse> Tools, IReadOnlyList<McpAgentResponse> Agents, int PendingApprovals, int ApprovalWindowHours);

/// <summary>A tool of the MCP server.</summary>
internal sealed record McpToolResponse(string Name, string Description, string? Permission, bool ChangesData);

/// <summary>An integration set up as an AI agent.</summary>
internal sealed record McpAgentResponse(
    Guid ClientId, string Name, ApiClientKind Kind, ApiClientStatus Status, bool Usable, string Role,
    IReadOnlyList<string> Tools, IReadOnlyList<string> ToolsWithoutApproval, IReadOnlyList<string> AllowedTools, DateTimeOffset UpdatedAt);

/// <summary>A reference to a person, agent or ticket.</summary>
internal sealed record McpReference(Guid Id, string? Name);

/// <summary>A change an AI agent asked for.</summary>
internal sealed record McpActionResponse(
    Guid Id, McpReference Agent, string Tool, string Summary, JsonNode? Arguments, McpActionStatus Status, bool ApprovalRequired,
    DateTimeOffset RequestedAt, DateTimeOffset? ExpiresAt, McpReference? DecidedBy, DateTimeOffset? DecidedAt, string? Note,
    McpReference? Ticket, DateTimeOffset? CompletedAt, string? ErrorCode, string? Error)
{
    public static McpActionResponse From(McpAction a, ActionLookups lookups) => new(
        a.Id,
        new McpReference(a.ApiClientId, lookups.Agents.GetValueOrDefault(a.ApiClientId)),
        a.Tool,
        a.Summary,
        JsonNode.Parse(a.Arguments),
        a.StatusAt(DateTimeOffset.UtcNow),
        a.ApprovalRequired,
        a.RequestedAt,
        a.ExpiresAt,
        a.DecidedById is { } d ? new McpReference(d, lookups.People.GetValueOrDefault(d)) : null,
        a.DecidedAt,
        a.DecisionNote,
        a.TicketId is { } t ? new McpReference(t, lookups.Tickets.GetValueOrDefault(t)) : null,
        a.CompletedAt,
        a.ErrorCode,
        a.Error);
}
