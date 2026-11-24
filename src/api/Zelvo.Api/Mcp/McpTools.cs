using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Zelvo.Api.Conventions;
using Zelvo.Api.Identity;
using Zelvo.Api.Incidents;
using Zelvo.Api.Knowledge;
using Zelvo.Api.Settings;
using Zelvo.Api.Sla;
using Zelvo.Data;
using Zelvo.Data.Identity;
using Zelvo.Data.Knowledge;
using Zelvo.Data.Tenancy;
using Zelvo.Data.Tickets;
using Zelvo.Search;

namespace Zelvo.Api.Mcp;

/// <summary>What a tool gave back: data for the agent, or a problem with a stable code.</summary>
/// <param name="Data">The answer (JSON) when it succeeded.</param>
/// <param name="Code">Problem code when it did not, for example <c>not-found</c> or <c>validation-failed</c>.</param>
/// <param name="Message">Explanation of the problem.</param>
internal sealed record McpToolResult(JsonNode? Data, string? Code = null, string? Message = null)
{
    public bool Succeeded => Code is null;

    public static McpToolResult Ok(JsonNode? data) => new(data);

    public static McpToolResult Problem(string code, string message) => new(null, code, message);
}

/// <summary>A change checked before it is queued for approval: what the approver reads, and the ticket it is about.</summary>
internal sealed record McpPreparedChange(string Summary, Guid? TicketId);

/// <summary>What a tool runs with: the agent's own scope (its permissions, the AI-agent actor in the audit log).</summary>
internal sealed class McpToolContext(IServiceProvider services, HttpContext http, CurrentUser user, CancellationToken cancellationToken)
{
    public IServiceProvider Services { get; } = services;

    /// <summary>A request of the agent's scope; endpoint code reads services and If-Match from it.</summary>
    public HttpContext Http { get; } = http;

    public CurrentUser User { get; } = user;

    public CancellationToken CancellationToken { get; } = cancellationToken;

    public ZelvoDbContext Db => Services.GetRequiredService<ZelvoDbContext>();

    public T Get<T>()
        where T : notnull => Services.GetRequiredService<T>();
}

/// <summary>A tool of Zelvo's built-in MCP server (module 9, task E6).</summary>
/// <param name="Name">Tool name, for example <c>search_knowledge</c>.</param>
/// <param name="Description">What the tool does, for the AI agent (English, as MCP tools are described to models).</param>
/// <param name="Permission">Permission the agent needs; null for tools every agent may use.</param>
/// <param name="ChangesData">The tool changes data: it is recorded as an action and needs a person's approval unless turned off for the agent.</param>
/// <param name="InputSchema">JSON Schema of the arguments.</param>
/// <param name="Run">Carries out the call.</param>
/// <param name="Prepare">Change tools: checks the arguments and describes the change for the approver, before anything is queued.</param>
internal sealed record McpTool(
    string Name,
    string Description,
    string? Permission,
    bool ChangesData,
    JsonObject InputSchema,
    Func<McpToolContext, JsonElement, Task<McpToolResult>> Run,
    Func<McpToolContext, JsonElement, Task<(McpPreparedChange? Change, McpToolResult? Problem)>>? Prepare = null);

/// <summary>
/// The tools of Zelvo's MCP server: search knowledge, find and read incidents with their status
/// and SLA clocks, read SLA policies, and raise, change and move incidents. They call the same code
/// as the REST API, as the agent, so its role, scopes and field rules apply and every change is
/// audited. Raising catalog requests is added with the service catalog (task C4).
/// </summary>
internal static class McpTools
{
    /// <summary>Name of the tool an agent uses to check on a change waiting for approval.</summary>
    public const string ActionStatusTool = "get_action_status";

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static IReadOnlyList<McpTool> All { get; } =
    [
        new("search_knowledge",
            "Searches published knowledge base articles in English and Arabic by text in the title or summary. Returns article numbers (KB-000123), titles and summaries. Internal articles are included only when the agent may read them.",
            null, false,
            Schema(new() { ["text"] = Str("Words to look for.", 200), ["page"] = Int("Page number, from 1.", 1, 1000) }, "text"),
            SearchKnowledgeAsync),
        new("get_knowledge_article",
            "Reads one published knowledge article (Markdown body in English and/or Arabic) by its number, for example KB-000123, or its id.",
            null, false,
            Schema(new() { ["article"] = Str("Article number (KB-000123) or id.", 64) }, "article"),
            GetArticleAsync),
        new("search_incidents",
            "Finds incidents: by number, state, priority, open only, or the requester's email address. Newest first. Returns number, title, state, priority and SLA state.",
            Permissions.IncidentsRead, false,
            Schema(new()
            {
                ["number"] = Str("Incident number, for example INC-000123.", Ticket.NumberMaxLength),
                ["state"] = Choice("Only this state.", Enum.GetNames<TicketState>()),
                ["open"] = Bool("Only incidents still being worked on."),
                ["priority"] = Int("Only this priority (1 is the highest).", 1, 9),
                ["requesterEmail"] = Str("Only incidents of the requester with this email address.", UserAccount.EmailMaxLength),
                ["page"] = Int("Page number, from 1.", 1, 1000),
            }),
            SearchIncidentsAsync),
        new("get_incident",
            "Reads one incident by its number (INC-000123) or id: state (its status), priority, requester, assignee, group, category, resolution notes, and its SLA clocks.",
            Permissions.IncidentsRead, false,
            Schema(new() { ["incident"] = Str("Incident number (INC-000123) or id.", 64) }, "incident"),
            GetIncidentAsync),
        new("get_incident_sla",
            "Reads the response and resolution SLA clocks of an incident: target, time left, paused, at risk or breached.",
            Permissions.IncidentsRead, false,
            Schema(new() { ["incident"] = Str("Incident number (INC-000123) or id.", 64) }, "incident"),
            GetIncidentSlaAsync),
        new("list_sla_policies",
            "Lists the SLA policies in the order they are matched (first match wins): record type, category, customer group, response and resolution targets per priority in business minutes, and the business calendar.",
            Permissions.DirectoryRead, false,
            Schema(new()),
            ListSlaPoliciesAsync),
        new("create_incident",
            "Raises an incident for an existing requester, named by email address. Impact and urgency (1 highest to 3) set the priority; without them the tenant's defaults apply. The assignment rules then place it. May need a person's approval: the answer then says pending-approval with an action id to check with get_action_status.",
            Permissions.IncidentsCreate, true,
            Schema(new()
            {
                ["title"] = Str("Short summary, 3 to 300 characters.", Ticket.TitleMaxLength),
                ["description"] = Str("What happened.", Ticket.DescriptionMaxLength),
                ["requesterEmail"] = Str("Email address of the person the incident is for; they must be a user of the organisation.", UserAccount.EmailMaxLength),
                ["impact"] = Int("Impact, 1 (highest) to 3.", 1, 3),
                ["urgency"] = Int("Urgency, 1 (highest) to 3.", 1, 3),
            }, "title", "requesterEmail"),
            CreateIncidentAsync, PrepareCreateIncidentAsync),
        new("update_incident",
            "Changes an incident's title, description, impact or urgency (the priority follows). May need a person's approval.",
            Permissions.IncidentsEdit, true,
            Schema(new()
            {
                ["incident"] = Str("Incident number (INC-000123) or id.", 64),
                ["title"] = Str("New title, 3 to 300 characters.", Ticket.TitleMaxLength),
                ["description"] = Str("New description.", Ticket.DescriptionMaxLength),
                ["impact"] = Int("New impact, 1 (highest) to 3.", 1, 3),
                ["urgency"] = Int("New urgency, 1 (highest) to 3.", 1, 3),
            }, "incident"),
            UpdateIncidentAsync, PrepareUpdateIncidentAsync),
        new("change_incident_state",
            "Moves an incident to InProgress, OnHold (with an on-hold reason key), Resolved (with resolution notes) or Reopened. Closed is final and set by people or the automatic close. May need a person's approval.",
            Permissions.IncidentsEdit, true,
            Schema(new()
            {
                ["incident"] = Str("Incident number (INC-000123) or id.", 64),
                ["state"] = Choice("The new state.", ["InProgress", "OnHold", "Resolved", "Reopened"]),
                ["onHoldReason"] = Str("Key of an active on-hold reason, for OnHold.", Ticket.ReasonKeyMaxLength),
                ["resolutionNotes"] = Str("How it was resolved, for Resolved.", Ticket.ResolutionNotesMaxLength),
            }, "incident", "state"),
            ChangeStateAsync, PrepareChangeStateAsync),
        new(ActionStatusTool,
            "Checks on a change this agent asked for that needed a person's approval: Pending, Rejected (with the approver's note), Expired, Succeeded (with the result) or Failed.",
            null, false,
            Schema(new() { ["actionId"] = Str("The action id from the pending-approval answer.", 36) }, "actionId"),
            (_, _) => throw new InvalidOperationException("Answered by the MCP server itself.")),
    ];

    public static McpTool? Find(string name) => All.FirstOrDefault(t => t.Name == name);

    /// <summary>Tools an administrator can turn on for agents (the action status tool is always there).</summary>
    public static IEnumerable<McpTool> Configurable => All.Where(t => t.Name != ActionStatusTool);

    // ---- Read tools ----

    private static async Task<McpToolResult> SearchKnowledgeAsync(McpToolContext context, JsonElement args)
    {
        var page = new PageQuery { Page = OptionalInt(args, "page") ?? 1, PageSize = 10 };
        var filter = new PublishedListQuery { Text = OptionalString(args, "text") };
        if (Validate(filter) is { } invalid)
        {
            return invalid;
        }

        var result = await KnowledgePortalEndpoints.ListAsync(page, filter, context.Db, context.User, context.Get<TenantSettingsStore>(), context.CancellationToken);
        return From(result);
    }

    private static async Task<McpToolResult> GetArticleAsync(McpToolContext context, JsonElement args)
    {
        var key = OptionalString(args, "article")?.Trim() ?? string.Empty;
        Guid? id = Guid.TryParse(key, out var parsed) ? parsed : null;
        if (id is null)
        {
            var number = key.ToUpperInvariant();
            id = await context.Db.KbArticles.AsNoTracking().Where(a => a.Number == number).Select(a => (Guid?)a.Id).SingleOrDefaultAsync(context.CancellationToken);
        }

        if (id is null)
        {
            return McpToolResult.Problem("not-found", "There is no published article with this number.");
        }

        return From(await KnowledgePortalEndpoints.GetAsync(id.Value, context.Db, context.User, context.Get<TenantSettingsStore>(), context.CancellationToken));
    }

    private static async Task<McpToolResult> SearchIncidentsAsync(McpToolContext context, JsonElement args)
    {
        var query = new IncidentListQuery
        {
            Number = OptionalString(args, "number"),
            State = OptionalEnum<TicketState>(args, "state"),
            Open = OptionalBool(args, "open"),
            Priority = OptionalInt(args, "priority"),
            Page = OptionalInt(args, "page") ?? 1,
            PageSize = 20,
        };
        if (Validate(query) is { } invalid)
        {
            return invalid;
        }

        var result = (await IncidentEndpoints.ListAsync(query, context.Db, context.User, context.CancellationToken)).Value!;
        if (OptionalString(args, "requesterEmail") is { Length: > 0 } email)
        {
            // Filtered here rather than in the list query: the list filter by requester is for B4.
            var requester = await RequesterIdAsync(context, email);
            var items = result["items"]!.AsArray().Where(i => requester is { } r && (string?)i?["requester"]?["id"] == r.ToString()).Select(i => i!.DeepClone()).ToArray();
            result["items"] = new JsonArray(items);
            result["totalCount"] = items.Length;
        }

        return McpToolResult.Ok(Compact(result));
    }

    private static async Task<McpToolResult> GetIncidentAsync(McpToolContext context, JsonElement args)
    {
        var (id, problem) = await IncidentIdAsync(context, args);
        return problem ?? From(await IncidentEndpoints.GetAsync(id, context.Http, context.Db, context.User, context.CancellationToken));
    }

    private static async Task<McpToolResult> GetIncidentSlaAsync(McpToolContext context, JsonElement args)
    {
        var detail = await GetIncidentAsync(context, args);
        if (!detail.Succeeded || detail.Data is not JsonObject incident)
        {
            return detail;
        }

        return McpToolResult.Ok(new JsonObject
        {
            ["number"] = incident["number"]?.DeepClone(),
            ["state"] = incident["state"]?.DeepClone(),
            ["priority"] = incident["priority"]?.DeepClone(),
            ["sla"] = incident["sla"]?.DeepClone(),
        });
    }

    private static async Task<McpToolResult> ListSlaPoliciesAsync(McpToolContext context, JsonElement args) =>
        From(await SlaEndpoints.ListPoliciesAsync(context.Db, context.CancellationToken));

    // ---- Change tools ----

    private static async Task<(McpPreparedChange?, McpToolResult?)> PrepareCreateIncidentAsync(McpToolContext context, JsonElement args)
    {
        var (request, problem) = await CreateRequestAsync(context, args);
        if (problem is not null)
        {
            return (null, problem);
        }

        var email = OptionalString(args, "requesterEmail")!.Trim();
        return (new McpPreparedChange(Cut($"Raise an incident for {email}: \"{request!.Title}\"" + Levels(request.Impact, request.Urgency)), null), null);
    }

    private static async Task<McpToolResult> CreateIncidentAsync(McpToolContext context, JsonElement args)
    {
        var (request, problem) = await CreateRequestAsync(context, args);
        if (problem is not null)
        {
            return problem;
        }

        return From(await IncidentEndpoints.CreateAsync(request!, context.Http, context.Db, context.User, context.Get<ITenantContext>(),
            context.Get<TenantSettingsStore>(), context.Get<ISearchIndexer>(), context.CancellationToken));
    }

    private static async Task<(CreateIncidentRequest?, McpToolResult?)> CreateRequestAsync(McpToolContext context, JsonElement args)
    {
        var email = OptionalString(args, "requesterEmail");
        if (string.IsNullOrWhiteSpace(email))
        {
            return (null, McpToolResult.Problem("validation-failed", "requesterEmail: name the person the incident is for."));
        }

        var requester = await RequesterIdAsync(context, email);
        if (requester is null)
        {
            return (null, McpToolResult.Problem("validation-failed", "requesterEmail: there is no active user with this email address."));
        }

        var request = new CreateIncidentRequest
        {
            Title = OptionalString(args, "title")?.Trim(),
            Description = OptionalString(args, "description"),
            RequesterId = requester,
            Impact = OptionalInt(args, "impact"),
            Urgency = OptionalInt(args, "urgency"),
        };
        return Validate(request) is { } invalid ? (null, invalid) : (request, null);
    }

    private static async Task<(McpPreparedChange?, McpToolResult?)> PrepareUpdateIncidentAsync(McpToolContext context, JsonElement args)
    {
        var (ticket, problem) = await VisibleIncidentAsync(context, args);
        if (problem is not null)
        {
            return (null, problem);
        }

        var request = UpdateRequest(args);
        if (Validate(request) is { } invalid)
        {
            return (null, invalid);
        }

        var changes = new List<string>();
        if (request.Title is not null) changes.Add($"title \"{request.Title}\"");
        if (request.Description is not null) changes.Add("description");
        if (request.Impact is not null) changes.Add($"impact {request.Impact}");
        if (request.Urgency is not null) changes.Add($"urgency {request.Urgency}");
        if (changes.Count == 0)
        {
            return (null, McpToolResult.Problem("validation-failed", "Give at least one of title, description, impact or urgency."));
        }

        return (new McpPreparedChange(Cut($"Change {ticket!.Number}: {string.Join(", ", changes)}"), ticket.Id), null);
    }

    private static async Task<McpToolResult> UpdateIncidentAsync(McpToolContext context, JsonElement args)
    {
        var (id, problem) = await IncidentIdAsync(context, args);
        if (problem is not null)
        {
            return problem;
        }

        var request = UpdateRequest(args);
        return Validate(request) ?? From(await IncidentEndpoints.UpdateAsync(id, request, context.Http, context.Db, context.User,
            context.Get<TenantSettingsStore>(), context.Get<ISearchIndexer>(), context.CancellationToken));
    }

    private static UpdateIncidentRequest UpdateRequest(JsonElement args) => new()
    {
        Title = OptionalString(args, "title")?.Trim(),
        Description = OptionalString(args, "description"),
        Impact = OptionalInt(args, "impact"),
        Urgency = OptionalInt(args, "urgency"),
    };

    private static readonly TicketState[] AgentStates = [TicketState.InProgress, TicketState.OnHold, TicketState.Resolved, TicketState.Reopened];

    private static async Task<(McpPreparedChange?, McpToolResult?)> PrepareChangeStateAsync(McpToolContext context, JsonElement args)
    {
        var (ticket, problem) = await VisibleIncidentAsync(context, args);
        if (problem is not null)
        {
            return (null, problem);
        }

        var (request, invalid) = StateRequest(args);
        if (invalid is not null)
        {
            return (null, invalid);
        }

        var detail = request!.State switch
        {
            TicketState.OnHold => $" (reason {request.OnHoldReason})",
            TicketState.Resolved => $": \"{request.ResolutionNotes}\"",
            _ => string.Empty,
        };
        return (new McpPreparedChange(Cut($"Move {ticket!.Number} from {ticket.State} to {request.State}{detail}"), ticket.Id), null);
    }

    private static async Task<McpToolResult> ChangeStateAsync(McpToolContext context, JsonElement args)
    {
        var (id, problem) = await IncidentIdAsync(context, args);
        if (problem is not null)
        {
            return problem;
        }

        var (request, invalid) = StateRequest(args);
        return invalid ?? From(await IncidentEndpoints.TransitionAsync(id, request!, context.Http, context.Db, context.User,
            context.Get<TenantSettingsStore>(), context.Get<ISearchIndexer>(), context.CancellationToken));
    }

    private static (ChangeIncidentStateRequest?, McpToolResult?) StateRequest(JsonElement args)
    {
        var state = OptionalEnum<TicketState>(args, "state");
        if (state is not { } wanted || !AgentStates.Contains(wanted))
        {
            return (null, McpToolResult.Problem("validation-failed", "state: use InProgress, OnHold, Resolved or Reopened."));
        }

        var request = new ChangeIncidentStateRequest
        {
            State = wanted,
            OnHoldReason = OptionalString(args, "onHoldReason"),
            ResolutionNotes = OptionalString(args, "resolutionNotes"),
        };
        return Validate(request) is { } invalid ? (null, invalid) : (request, null);
    }

    // ---- Helpers ----

    private static async Task<Guid?> RequesterIdAsync(McpToolContext context, string email)
    {
        // Exact match, ignoring case (ILIKE without wildcards: they are escaped).
        var pattern = email.Trim().Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal);
        return await context.Db.Users.AsNoTracking()
            .Where(u => u.Email != null && EF.Functions.ILike(u.Email, pattern) && u.Status == UserStatus.Active)
            .Select(u => (Guid?)u.Id).FirstOrDefaultAsync(context.CancellationToken);
    }

    private static async Task<(Ticket?, McpToolResult?)> VisibleIncidentAsync(McpToolContext context, JsonElement args)
    {
        var (id, problem) = await IncidentIdAsync(context, args);
        if (problem is not null)
        {
            return (null, problem);
        }

        var ticket = await IncidentEndpoints.FindVisibleAsync(context.Db, context.User, id, context.CancellationToken);
        return ticket is null ? (null, NotFound()) : (ticket, null);
    }

    private static async Task<(Guid Id, McpToolResult? Problem)> IncidentIdAsync(McpToolContext context, JsonElement args)
    {
        var key = OptionalString(args, "incident")?.Trim() ?? string.Empty;
        if (Guid.TryParse(key, out var id))
        {
            return (id, null);
        }

        var number = key.ToUpperInvariant();
        var found = key.Length is > 0 and <= Ticket.NumberMaxLength
            ? await IncidentEndpoints.Incidents(context.Db).AsNoTracking().Where(t => t.Number == number).Select(t => (Guid?)t.Id).SingleOrDefaultAsync(context.CancellationToken)
            : null;
        return found is { } f ? (f, null) : (Guid.Empty, NotFound());
    }

    private static McpToolResult NotFound() => McpToolResult.Problem("not-found", "There is no incident with this number that the agent may see.");

    /// <summary>Turns an endpoint result into a tool result: its value, or its problem code and message.</summary>
    internal static McpToolResult From(IResult result)
    {
        while (result is INestedHttpResult nested)
        {
            result = nested.Result;
        }

        switch (result)
        {
            case ValidationProblem validation:
                return McpToolResult.Problem("validation-failed",
                    string.Join("; ", validation.ProblemDetails.Errors.Select(e => $"{e.Key}: {string.Join(" ", e.Value)}")));
            case ProblemHttpResult problem:
                var code = problem.ProblemDetails.Extensions.TryGetValue("code", out var c) && c is string s ? s : "failed";
                return McpToolResult.Problem(code, string.Join(" ", new[] { problem.ProblemDetails.Title, problem.ProblemDetails.Detail }.Where(t => !string.IsNullOrEmpty(t))));
            case IStatusCodeHttpResult { StatusCode: >= 400 } failed:
                return McpToolResult.Problem("failed", $"Status {failed.StatusCode}.");
            case IValueHttpResult { Value: var value }:
                return McpToolResult.Ok(value is JsonNode node ? node.DeepClone() : JsonSerializer.SerializeToNode(value, Json));
            default:
                return McpToolResult.Ok(null);
        }
    }

    // List answers without the parts agents do not need.
    private static JsonObject Compact(JsonObject page)
    {
        foreach (var item in page["items"]?.AsArray() ?? [])
        {
            if (item is JsonObject incident)
            {
                incident.Remove("nextStates");
                incident.Remove("children");
            }
        }

        return page;
    }

    private static McpToolResult? Validate(object request)
    {
        var errors = new List<ValidationResult>();
        return Validator.TryValidateObject(request, new ValidationContext(request), errors, validateAllProperties: true)
            ? null
            : McpToolResult.Problem("validation-failed", string.Join("; ", errors.Select(e => $"{string.Join(",", e.MemberNames.Select(JsonNamingPolicy.CamelCase.ConvertName))}: {e.ErrorMessage}")));
    }

    internal static string? OptionalString(JsonElement args, string name) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static int? OptionalInt(JsonElement args, string name) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var i) ? i : null;

    private static bool? OptionalBool(JsonElement args, string name) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : null;

    private static TEnum? OptionalEnum<TEnum>(JsonElement args, string name)
        where TEnum : struct, Enum =>
        OptionalString(args, name) is { } text && Enum.TryParse<TEnum>(text, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed) ? parsed : null;

    private static string Levels(int? impact, int? urgency) =>
        impact is null && urgency is null ? string.Empty : $" (impact {impact?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "default"}, urgency {urgency?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "default"})";

    private static string Cut(string text) => text.Length <= Data.Mcp.McpAction.SummaryMaxLength ? text : string.Concat(text.AsSpan(0, Data.Mcp.McpAction.SummaryMaxLength - 1), "…");

    // ---- JSON Schema ----

    private static JsonObject Schema(Dictionary<string, JsonObject> properties, params string[] required)
    {
        var props = new JsonObject();
        foreach (var (name, schema) in properties)
        {
            props[name] = schema;
        }

        var schemaObject = new JsonObject { ["type"] = "object", ["properties"] = props, ["additionalProperties"] = false };
        if (required.Length > 0)
        {
            schemaObject["required"] = new JsonArray([.. required.Select(r => (JsonNode)r)]);
        }

        return schemaObject;
    }

    private static JsonObject Str(string description, int maxLength) => new() { ["type"] = "string", ["description"] = description, ["maxLength"] = maxLength };

    private static JsonObject Int(string description, int min, int max) => new() { ["type"] = "integer", ["description"] = description, ["minimum"] = min, ["maximum"] = max };

    private static JsonObject Bool(string description) => new() { ["type"] = "boolean", ["description"] = description };

    private static JsonObject Choice(string description, IEnumerable<string> values) =>
        new() { ["type"] = "string", ["description"] = description, ["enum"] = new JsonArray([.. values.Select(v => (JsonNode)v)]) };
}
