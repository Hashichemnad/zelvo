using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Zelvo.Api.Identity;
using Zelvo.Api.Settings;
using Zelvo.Data;
using Zelvo.Data.Auditing;
using Zelvo.Data.Identity;
using Zelvo.Data.Integrations;
using Zelvo.Data.Mcp;
using Zelvo.Data.Tenancy;

namespace Zelvo.Api.Mcp;

/// <summary>Tenant settings of the built-in MCP server.</summary>
internal static class McpSettings
{
    /// <summary>The MCP server answers this tenant's AI agents. Off until an administrator turns it on (AI stays off until set up).</summary>
    public static SettingDefinition<bool> Enabled { get; } = new("mcp.server.enabled", false, Permissions.AiManage);
}

/// <summary>An AI agent as the MCP server sees it: the integration, its role and its MCP settings.</summary>
internal sealed record McpAgentIdentity(ApiClient Client, Role Role, McpAgent Agent)
{
    /// <summary>Tools the agent is offered now: turned on for it and allowed by its role and scopes.</summary>
    public IEnumerable<McpTool> Tools(CurrentUser user) => McpTools.All.Where(t =>
        (t.Name == McpTools.ActionStatusTool || Agent.Tools.Contains(t.Name, StringComparer.Ordinal))
        && (t.Permission is null || user.Has(t.Permission)));

    public bool NeedsApproval(McpTool tool) => tool.ChangesData && !Agent.ToolsWithoutApproval.Contains(tool.Name, StringComparer.Ordinal);
}

/// <summary>What the agent gets back from a tool call.</summary>
/// <param name="IsError">The call did not do what was asked.</param>
/// <param name="Content">The answer (JSON).</param>
internal sealed record McpCallOutcome(bool IsError, JsonObject Content);

/// <summary>
/// Carries out MCP tool calls as the AI agent: in a scope of its own with the agent's permissions
/// (its role narrowed by its scopes) and the audit actor <see cref="AuditActorType.AiAgent"/>, so
/// every change is marked as done by an AI agent. Tools that change data are recorded as
/// <see cref="McpAction"/>s; unless approval is turned off for the agent and tool, they wait for a
/// person who holds the tool's permission (product owner decision 2026-11-24) and run when approved.
/// </summary>
internal sealed partial class McpRunner(IServiceScopeFactory scopes, ITenantContext tenant, IAuditContext caller, TimeProvider time, ILogger<McpRunner> logger)
{
    /// <summary>Loads the agent of an integration, or null when the integration is not an AI agent.</summary>
    public static async Task<McpAgentIdentity?> LoadAgentAsync(ZelvoDbContext db, Guid clientId, CancellationToken cancellationToken)
    {
        var agent = await db.McpAgents.AsNoTracking().SingleOrDefaultAsync(a => a.ApiClientId == clientId, cancellationToken).ConfigureAwait(false);
        var client = agent is null ? null : await db.ApiClients.AsNoTracking().SingleOrDefaultAsync(c => c.Id == clientId, cancellationToken).ConfigureAwait(false);
        if (agent is null || client is null)
        {
            return null;
        }

        var role = await db.Roles.AsNoTracking().Include(r => r.Permissions).Include(r => r.FieldRules)
            .AsSplitQuery().SingleAsync(r => r.Id == client.RoleId, cancellationToken).ConfigureAwait(false);
        return new McpAgentIdentity(client, role, agent);
    }

    /// <summary>A tool call from the agent through the MCP server.</summary>
    public async Task<McpCallOutcome> CallAsync(McpAgentIdentity agent, string toolName, JsonElement args, CancellationToken cancellationToken)
    {
        await using var scope = AgentScope(agent, null, null);
        var context = ToolContext(scope, agent, cancellationToken);
        var tool = agent.Tools(context.User).FirstOrDefault(t => t.Name == toolName);
        if (tool is null)
        {
            return Error("tool-not-available", $"The tool '{toolName}' is not available to this agent.");
        }

        if (tool.Name == McpTools.ActionStatusTool)
        {
            return await ActionStatusAsync(context.Db, agent, args, cancellationToken).ConfigureAwait(false);
        }

        if (!tool.ChangesData)
        {
            var read = await tool.Run(context, args).ConfigureAwait(false);
            McpTelemetry.Record(tool.Name, read.Succeeded ? "succeeded" : "failed");
            return Outcome(read);
        }

        var (change, problem) = await tool.Prepare!(context, args).ConfigureAwait(false);
        if (problem is not null)
        {
            return Outcome(problem);
        }

        var now = time.GetUtcNow();
        var needsApproval = agent.NeedsApproval(tool);
        var action = new McpAction
        {
            ApiClientId = agent.Client.Id,
            Tool = tool.Name,
            Permission = tool.Permission!,
            Arguments = Cut(args.GetRawText(), McpAction.JsonMaxLength),
            Summary = change!.Summary,
            TicketId = change.TicketId,
            ApprovalRequired = needsApproval,
            RequestedAt = now,
            ExpiresAt = needsApproval ? now + McpAction.ApprovalWindow : null,
        };

        if (needsApproval)
        {
            context.Db.McpActions.Add(action);
            await context.Db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            McpTelemetry.Record(tool.Name, "pending-approval");
            return new McpCallOutcome(false, new JsonObject
            {
                ["status"] = "pending-approval",
                ["actionId"] = action.Id.ToString(),
                ["expiresAt"] = action.ExpiresAt,
                ["summary"] = action.Summary,
                ["message"] = $"A person must approve this change. Check on it with {McpTools.ActionStatusTool} and actionId {action.Id}.",
            });
        }

        // Without approval: carried out now and recorded with its result.
        var result = await tool.Run(context, args).ConfigureAwait(false);
        await RecordResultAsync(agent, action, result, cancellationToken).ConfigureAwait(false);
        var outcome = Outcome(result);
        outcome.Content["actionId"] = action.Id.ToString();
        return outcome;
    }

    /// <summary>A person approves a waiting change: it is carried out now, as the agent, with the approver recorded.</summary>
    public async Task<McpActionDecision> ApproveAsync(Guid actionId, UserAccount approver, string? note, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        SetTenant(scope);
        var audit = scope.ServiceProvider.GetRequiredService<AuditContext>();
        CopyCaller(audit);
        var db = scope.ServiceProvider.GetRequiredService<ZelvoDbContext>();
        var action = await db.McpActions.SingleOrDefaultAsync(a => a.Id == actionId, cancellationToken).ConfigureAwait(false);
        var now = time.GetUtcNow();
        if (action is null)
        {
            return McpActionDecision.NotFound;
        }

        if (action.StatusAt(now) != McpActionStatus.Pending || action.DecidedAt is not null)
        {
            return McpActionDecision.NotPending;
        }

        var agent = await LoadAgentAsync(db, action.ApiClientId, cancellationToken).ConfigureAwait(false);
        var tool = McpTools.Find(action.Tool);

        // The decision is saved first: the row version stops a second approver from running it again.
        action.DecidedById = approver.Id;
        action.DecidedAt = now;
        action.DecisionNote = Note(note);
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            return McpActionDecision.NotPending;
        }

        McpToolResult result;
        if (agent is null || tool is null || !agent.Client.IsUsable(now))
        {
            result = McpToolResult.Problem("agent-not-available", "The AI agent was revoked, expired or is no longer set up for the MCP server.");
        }
        else
        {
            await using var agentScope = AgentScope(agent, action.Id, approver);
            var context = ToolContext(agentScope, agent, cancellationToken);
            using var json = JsonDocument.Parse(action.Arguments);
            result = tool.Permission is { } permission && !context.User.Has(permission)
                ? McpToolResult.Problem("forbidden", $"The AI agent no longer has {permission}.")
                : await tool.Run(context, json.RootElement).ConfigureAwait(false);
        }

        await RecordResultAsync(agent, action, result, cancellationToken).ConfigureAwait(false);
        return result.Succeeded ? McpActionDecision.Done : McpActionDecision.Failed;
    }

    /// <summary>A person rejects a waiting change: nothing is changed.</summary>
    public async Task<McpActionDecision> RejectAsync(Guid actionId, UserAccount approver, string? note, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        SetTenant(scope);
        CopyCaller(scope.ServiceProvider.GetRequiredService<AuditContext>());
        var db = scope.ServiceProvider.GetRequiredService<ZelvoDbContext>();
        var action = await db.McpActions.SingleOrDefaultAsync(a => a.Id == actionId, cancellationToken).ConfigureAwait(false);
        var now = time.GetUtcNow();
        if (action is null)
        {
            return McpActionDecision.NotFound;
        }

        if (action.StatusAt(now) != McpActionStatus.Pending || action.DecidedAt is not null)
        {
            return McpActionDecision.NotPending;
        }

        action.Status = McpActionStatus.Rejected;
        action.DecidedById = approver.Id;
        action.DecidedAt = now;
        action.DecisionNote = Note(note);
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            return McpActionDecision.NotPending;
        }

        McpTelemetry.Record(action.Tool, "rejected");
        return McpActionDecision.Done;
    }

    private static async Task<McpCallOutcome> ActionStatusAsync(ZelvoDbContext db, McpAgentIdentity agent, JsonElement args, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(McpTools.OptionalString(args, "actionId"), out var id))
        {
            return Error("validation-failed", "actionId: give the action id from the pending-approval answer.");
        }

        // Agents see only their own actions.
        var action = await db.McpActions.AsNoTracking().SingleOrDefaultAsync(a => a.Id == id && a.ApiClientId == agent.Client.Id, cancellationToken).ConfigureAwait(false);
        if (action is null)
        {
            return Error("not-found", "There is no action with this id for this agent.");
        }

        var content = new JsonObject
        {
            ["actionId"] = action.Id.ToString(),
            ["tool"] = action.Tool,
            ["status"] = action.StatusAt(DateTimeOffset.UtcNow).ToString(),
            ["summary"] = action.Summary,
            ["requestedAt"] = action.RequestedAt,
            ["decidedAt"] = action.DecidedAt,
            ["note"] = action.DecisionNote,
            ["errorCode"] = action.ErrorCode,
            ["error"] = action.Error,
            ["result"] = action.Result is { } result ? JsonNode.Parse(result) : null,
        };
        return new McpCallOutcome(false, content);
    }

    private async Task RecordResultAsync(McpAgentIdentity? agent, McpAction action, McpToolResult result, CancellationToken cancellationToken)
    {
        // Saved through a scope of its own, so it is kept whatever happened to the change.
        await using var scope = scopes.CreateAsyncScope();
        SetTenant(scope);
        var audit = scope.ServiceProvider.GetRequiredService<AuditContext>();
        if (agent is not null)
        {
            audit.SetActor(AuditActorType.AiAgent, agent.Client.Id.ToString(), agent.Client.Name);
            CopyMetadata(audit);
        }
        else
        {
            CopyCaller(audit);
        }

        var db = scope.ServiceProvider.GetRequiredService<ZelvoDbContext>();
        var stored = await db.McpActions.SingleOrDefaultAsync(a => a.Id == action.Id, cancellationToken).ConfigureAwait(false);
        if (stored is null)
        {
            stored = action;
            db.McpActions.Add(stored);
        }

        stored.Status = result.Succeeded ? McpActionStatus.Succeeded : McpActionStatus.Failed;
        stored.CompletedAt = time.GetUtcNow();
        stored.Result = result.Data is null ? null : Cut(result.Data.ToJsonString(), McpAction.JsonMaxLength, json: true);
        stored.ErrorCode = result.Code is { } code ? Cut(code, McpAction.ErrorCodeMaxLength) : null;
        stored.Error = result.Message is { } message ? Cut(message, McpAction.ErrorMaxLength) : null;
        if (stored.TicketId is null && result.Data?["id"] is JsonValue idValue && Guid.TryParse(idValue.ToString(), out var ticketId)
            && await db.Tickets.AnyAsync(t => t.Id == ticketId, cancellationToken).ConfigureAwait(false))
        {
            stored.TicketId = ticketId;
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        McpTelemetry.Record(action.Tool, result.Succeeded ? "succeeded" : "failed");
        if (!result.Succeeded)
        {
            LogChangeFailed(logger, action.Tool, result.Code ?? "failed");
        }
    }

    // The agent's own scope: its permissions, and the AI-agent actor for the audit log.
    private AsyncServiceScope AgentScope(McpAgentIdentity agent, Guid? actionId, UserAccount? approver)
    {
        var scope = scopes.CreateAsyncScope();
        SetTenant(scope);
        var audit = scope.ServiceProvider.GetRequiredService<AuditContext>();
        audit.SetActor(AuditActorType.AiAgent, agent.Client.Id.ToString(), agent.Client.Name);
        CopyMetadata(audit);
        audit.SetMetadata("mcpActionId", actionId?.ToString());
        audit.SetMetadata("approvedBy", approver?.Id.ToString());
        audit.SetMetadata("approvedByName", approver is null ? null : approver.DisplayName ?? approver.Email);
        scope.ServiceProvider.GetRequiredService<CurrentUser>().SetClient(agent.Client, agent.Role);
        return scope;
    }

    private static McpToolContext ToolContext(AsyncServiceScope scope, McpAgentIdentity agent, CancellationToken cancellationToken)
    {
        var http = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        return new McpToolContext(scope.ServiceProvider, http, scope.ServiceProvider.GetRequiredService<CurrentUser>(), cancellationToken);
    }

    private void SetTenant(AsyncServiceScope scope) =>
        scope.ServiceProvider.GetRequiredService<TenantContext>().SetTenant(
            tenant.TenantId ?? throw new InvalidOperationException("MCP calls need a tenant."), tenant.Slug!);

    private void CopyCaller(AuditContext audit)
    {
        audit.SetActor(caller.ActorType, caller.ActorId, caller.ActorName);
        CopyMetadata(audit);
    }

    private void CopyMetadata(AuditContext audit)
    {
        foreach (var (key, value) in caller.Metadata)
        {
            audit.SetMetadata(key, value);
        }

        audit.SetMetadata("correlationId", Activity.Current?.TraceId.ToString());
    }

    private static McpCallOutcome Outcome(McpToolResult result) => result.Succeeded
        ? new McpCallOutcome(false, result.Data is JsonObject o ? o : new JsonObject { ["result"] = result.Data })
        : Error(result.Code!, result.Message ?? result.Code!);

    private static McpCallOutcome Error(string code, string message) => new(true, new JsonObject { ["code"] = code, ["message"] = message });

    private static string? Note(string? note) => string.IsNullOrWhiteSpace(note) ? null : Cut(note.Trim(), McpAction.NoteMaxLength);

    // JSON that is too long is replaced by a short note rather than cut into invalid JSON.
    private static string Cut(string value, int max, bool json = false) =>
        value.Length <= max ? value : json ? """{"truncated":true}""" : string.Concat(value.AsSpan(0, max - 1), "…");

    [LoggerMessage(Level = LogLevel.Information, Message = "An AI agent's change through MCP tool {Tool} was refused: {Code}.")]
    private static partial void LogChangeFailed(ILogger logger, string tool, string code);
}

/// <summary>What became of an approval or rejection.</summary>
internal enum McpActionDecision
{
    /// <summary>Approved and carried out, or rejected.</summary>
    Done,

    /// <summary>Approved, but Zelvo refused the change when it was carried out.</summary>
    Failed,

    /// <summary>No such action.</summary>
    NotFound,

    /// <summary>Already decided, expired, or being decided by someone else.</summary>
    NotPending,
}

/// <summary>Metrics of MCP tool calls (no arguments or answers).</summary>
internal static class McpTelemetry
{
    private static readonly System.Diagnostics.Metrics.Meter Meter = new("Zelvo.Mcp");
    private static readonly System.Diagnostics.Metrics.Counter<long> Calls = Meter.CreateCounter<long>("zelvo.mcp.tool.calls", description: "MCP tool calls by tool and outcome.");

    public static void Record(string tool, string outcome) => Calls.Add(1, new("zelvo.mcp.tool", tool), new("zelvo.mcp.outcome", outcome));
}
