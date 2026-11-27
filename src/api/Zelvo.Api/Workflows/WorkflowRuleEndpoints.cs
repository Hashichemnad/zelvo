using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Zelvo.Api.Conventions;
using Zelvo.Api.Forms;
using Zelvo.Api.Identity;
using Zelvo.Api.ServiceDesk;
using Zelvo.Api.Tenancy;
using Zelvo.Data;
using Zelvo.Data.Auditing;
using Zelvo.Data.Localization;
using Zelvo.Data.Tickets;
using Zelvo.Data.Workflows;
using Zelvo.Tickets.Forms;
using Zelvo.Workflows.Rules;

namespace Zelvo.Api.Workflows;

/// <summary>
/// The no-code workflow builder (module 10, task C3; product owner decision 2026-11-24): ordered
/// when / if / then rules that assign tickets, ask for approval (service requests) and notify
/// people in English and Arabic. Needs workflows.manage; every change is audited.
/// </summary>
internal static class WorkflowRuleEndpoints
{
    public static RouteGroupBuilder MapWorkflowRuleEndpoints(this RouteGroupBuilder group)
    {
        var rules = group.MapGroup("/workflow-rules").WithTags("Workflows").RequireTenant().RequirePermission(Permissions.WorkflowsManage);
        rules.MapGet("/", ListAsync).WithName("ListWorkflowRules")
            .WithSummary("Workflow rules in the order they are tried, optionally of one trigger.").Produces<IReadOnlyList<WorkflowRuleResponse>>();
        rules.MapGet("/{id:guid}", GetAsync).WithName("GetWorkflowRule")
            .WithSummary("One rule; returns an ETag to send as If-Match with changes.").Produces<WorkflowRuleResponse>();
        rules.MapPost("/", CreateAsync).WithName("CreateWorkflowRule")
            .WithSummary("Add a rule; it is tried last. Conditions: [{ field, op, value }]; actions: [{ type: assign | notify | approval, ... }]. Problems answer 400 with a problems list. Audited.")
            .Produces<WorkflowRuleResponse>(StatusCodes.Status201Created).ProducesValidationProblem();
        rules.MapPut("/order", ReorderAsync).WithName("OrderWorkflowRules")
            .WithSummary("Set the order of all rules (every rule exactly once). Audited.").Produces<IReadOnlyList<WorkflowRuleResponse>>().ProducesValidationProblem();
        rules.MapPost("/test", TestAsync).WithName("TestWorkflowRules")
            .WithSummary("Which active rules of a trigger hold for an existing ticket (by number) now, and what they would do. Changes nothing.")
            .Produces<IReadOnlyList<RuleTestResult>>().ProducesValidationProblem();
        rules.MapPut("/{id:guid}", UpdateAsync).WithName("UpdateWorkflowRule")
            .WithSummary("Replace a rule. If-Match is checked when sent. Audited.").Produces<WorkflowRuleResponse>().ProducesValidationProblem();
        rules.MapDelete("/{id:guid}", DeleteAsync).WithName("DeleteWorkflowRule")
            .WithSummary("Delete a rule. What it already did stays. Audited.").Produces(StatusCodes.Status204NoContent);
        rules.MapPost("/{id:guid}/approve", ApproveAsync).WithName("ApproveWorkflowRule")
            .WithSummary("Task K1: approve a draft made from a plain-language description, so it goes live. Checked again first; If-Match is checked when sent. Audited with the original text.")
            .Produces<WorkflowRuleResponse>().ProducesValidationProblem();
        return group;
    }

    internal static async Task<IResult> ListAsync(WorkflowTrigger? trigger, ZelvoDbContext db, CancellationToken cancellationToken)
    {
        var query = db.WorkflowRules.AsNoTracking();
        if (trigger is { } t)
        {
            query = query.Where(r => r.Trigger == t);
        }

        var rules = await query.OrderBy(r => r.Position).ThenBy(r => r.Id).ToListAsync(cancellationToken);
        return TypedResults.Ok(rules.Select(Response).ToList());
    }

    internal static async Task<IResult> GetAsync(Guid id, HttpContext http, ZelvoDbContext db, CancellationToken cancellationToken)
    {
        var rule = await db.WorkflowRules.AsNoTracking().SingleOrDefaultAsync(r => r.Id == id, cancellationToken);
        if (rule is null)
        {
            return FormsCommon.NotFound("workflow-rule");
        }

        http.Response.Headers.ETag = $"\"{rule.Version}\"";
        return TypedResults.Ok(Response(rule));
    }

    internal static async Task<IResult> CreateAsync(WorkflowRuleInput request, ZelvoDbContext db, CurrentUser user, CancellationToken cancellationToken)
    {
        var (parsed, problem) = await ParseAsync(request, db, cancellationToken);
        if (problem is not null)
        {
            return problem;
        }

        var rule = new WorkflowRule { Name = parsed!.Name, CreatedById = user.Account?.Id };
        Apply(rule, parsed);
        rule.Position = await db.WorkflowRules.AnyAsync(cancellationToken) ? await db.WorkflowRules.MaxAsync(r => r.Position, cancellationToken) + 1 : 0;
        db.WorkflowRules.Add(rule);
        await db.SaveChangesAsync(cancellationToken);
        var saved = await db.WorkflowRules.AsNoTracking().SingleAsync(r => r.Id == rule.Id, cancellationToken);
        return TypedResults.Created($"/api/v1/workflow-rules/{rule.Id}", Response(saved));
    }

    internal static async Task<IResult> UpdateAsync(Guid id, WorkflowRuleInput request, HttpContext http, ZelvoDbContext db, CancellationToken cancellationToken)
    {
        var rule = await db.WorkflowRules.SingleOrDefaultAsync(r => r.Id == id, cancellationToken);
        if (rule is null)
        {
            return FormsCommon.NotFound("workflow-rule");
        }

        if (IfMatchProblem(http, db, rule) is { } versionProblem)
        {
            return versionProblem;
        }

        var (parsed, problem) = await ParseAsync(request, db, cancellationToken);
        if (problem is not null)
        {
            return problem;
        }

        Apply(rule, parsed!);
        rule.UpdatedAt = DateTimeOffset.UtcNow;
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return FormsCommon.VersionMismatch();
        }

        var saved = await db.WorkflowRules.AsNoTracking().SingleAsync(r => r.Id == id, cancellationToken);
        http.Response.Headers.ETag = $"\"{saved.Version}\"";
        return TypedResults.Ok(Response(saved));
    }

    internal static async Task<IResult> DeleteAsync(Guid id, ZelvoDbContext db, CancellationToken cancellationToken)
    {
        var rule = await db.WorkflowRules.SingleOrDefaultAsync(r => r.Id == id, cancellationToken);
        if (rule is null)
        {
            return FormsCommon.NotFound("workflow-rule");
        }

        db.WorkflowRules.Remove(rule);
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.NoContent();
    }

    internal static async Task<IResult> ReorderAsync(OrderWorkflowRulesRequest request, ZelvoDbContext db, CancellationToken cancellationToken)
    {
        var rules = await db.WorkflowRules.ToListAsync(cancellationToken);
        var ids = request.Ids!;
        if (ids.Count != rules.Count || ids.Distinct().Count() != ids.Count || !rules.All(r => ids.Contains(r.Id)))
        {
            return UserEndpoints.Validation("ids", "List every rule exactly once.");
        }

        var now = DateTimeOffset.UtcNow;
        foreach (var rule in rules)
        {
            var position = ids.IndexOf(rule.Id);
            if (rule.Position != position)
            {
                rule.Position = position;
                rule.UpdatedAt = now;
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        return await ListAsync(null, db, cancellationToken);
    }

    internal static async Task<IResult> TestAsync(RuleTestRequest request, ZelvoDbContext db, CancellationToken cancellationToken)
    {
        var number = request.Number!.Trim().ToUpperInvariant();
        var ticket = await db.Tickets.AsNoTracking().SingleOrDefaultAsync(t => t.Number == number, cancellationToken);
        if (ticket is null)
        {
            return UserEndpoints.Validation("number", "No ticket has this number.");
        }

        var facts = await RuleFacts.LoadAsync(db, ticket, cancellationToken);
        var drafts = request.IncludeDrafts ?? false;
        var rules = await db.WorkflowRules.AsNoTracking()
            .Where(r => r.Active && (drafts || !r.IsDraft) && r.Trigger == request.Trigger!.Value && (r.RecordType == null || r.RecordType == ticket.Type))
            .OrderBy(r => r.Position).ThenBy(r => r.Id).ToListAsync(cancellationToken);
        var results = new List<RuleTestResult>();
        foreach (var rule in rules)
        {
            var holds = RuleDocuments.Holds(RuleDocuments.ReadConditions(rule.Conditions), rule.Match, facts.Subject);
            results.Add(new RuleTestResult(rule.Id, LocalizedTextDto.From(rule.Name), holds,
                holds ? [.. RuleDocuments.ReadActions(rule.Actions).Select(TypeOf)] : [], rule.IsDraft));
            if (holds && rule.StopProcessing)
            {
                break;
            }
        }

        return TypedResults.Ok(results);
    }

    internal static async Task<IResult> ApproveAsync(Guid id, HttpContext http, ZelvoDbContext db, CurrentUser user, IAuditLog audit, CancellationToken cancellationToken)
    {
        var rule = await db.WorkflowRules.SingleOrDefaultAsync(r => r.Id == id, cancellationToken);
        if (rule is null)
        {
            return FormsCommon.NotFound("workflow-rule");
        }

        if (!rule.IsDraft)
        {
            return ApiProblem.Create(StatusCodes.Status409Conflict, "not-a-draft", "The rule is already live.");
        }

        if (IfMatchProblem(http, db, rule) is { } versionProblem)
        {
            return versionProblem;
        }

        // What the draft refers to may have changed since the AI made it: check it like any rule.
        var input = new WorkflowRuleInput
        {
            Name = new LocalizedTextInput { En = rule.Name.En, Ar = rule.Name.Ar },
            Description = new LocalizedTextInput { En = rule.Description.En, Ar = rule.Description.Ar },
            Active = rule.Active,
            Trigger = rule.Trigger,
            RecordType = rule.RecordType,
            Match = rule.Match,
            Conditions = JsonNode.Parse(rule.Conditions) as JsonArray,
            Actions = JsonNode.Parse(rule.Actions) as JsonArray,
            StopProcessing = rule.StopProcessing,
        };
        var (_, problem) = await ParseAsync(input, db, cancellationToken);
        if (problem is not null)
        {
            return problem;
        }

        var now = DateTimeOffset.UtcNow;
        rule.IsDraft = false;
        rule.ApprovedById = user.Account?.Id;
        rule.ApprovedAt = now;
        rule.UpdatedAt = now;
        audit.Add(AuditCategory.Admin, "workflows.rule.approved", "workflow-rule", rule.Id.ToString(), new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["sourceText"] = rule.SourceText,
            ["sourceLanguage"] = rule.SourceLanguage,
            ["aiCallId"] = rule.AiCallId,
            ["unmapped"] = RuleUnmapped(rule),
        });
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return FormsCommon.VersionMismatch();
        }

        var saved = await db.WorkflowRules.AsNoTracking().SingleAsync(r => r.Id == id, cancellationToken);
        http.Response.Headers.ETag = $"\"{saved.Version}\"";
        return TypedResults.Ok(Response(saved));
    }

    /// <summary>What the AI could not map, as stored on the draft.</summary>
    internal static IReadOnlyList<string> RuleUnmapped(WorkflowRule rule) =>
        JsonSerializer.Deserialize<List<string>>(string.IsNullOrWhiteSpace(rule.Unmapped) ? "[]" : rule.Unmapped) ?? [];

    // ---- helpers ----

    private sealed record ParsedRule(
        LocalizedText Name, LocalizedText Description, bool Active, WorkflowTrigger Trigger, TicketType? RecordType, ConditionMatch Match,
        IReadOnlyList<WorkflowCondition> Conditions, IReadOnlyList<WorkflowAction> Actions, bool StopProcessing);

    private static async Task<(ParsedRule? Rule, IResult? Problem)> ParseAsync(WorkflowRuleInput request, ZelvoDbContext db, CancellationToken cancellationToken)
    {
        var problems = new List<FormProblem>();
        var name = DirectoryProblems.Clean(request.Name!);
        var description = request.Description is null ? new LocalizedText(null, null) : DirectoryProblems.Clean(request.Description);
        if (!name.HasAnyValue)
        {
            problems.Add(new("name", "name-required", "Name the rule in English or Arabic."));
        }

        if (!DirectoryProblems.Fits(name, WorkflowRule.NameMaxLength))
        {
            problems.Add(new("name", "text-too-long", $"Use at most {WorkflowRule.NameMaxLength} characters in each language."));
        }

        if (!DirectoryProblems.Fits(description, WorkflowRule.DescriptionMaxLength))
        {
            problems.Add(new("description", "text-too-long", $"Use at most {WorkflowRule.DescriptionMaxLength} characters in each language."));
        }

        var trigger = request.Trigger!.Value;
        if (!Enum.IsDefined(trigger) || (request.RecordType is { } rt && !Enum.IsDefined(rt)) || (request.Match is { } m && !Enum.IsDefined(m)))
        {
            problems.Add(new("trigger", "value-invalid", "Choose a known trigger, record type and match."));
        }

        List<WorkflowCondition> conditions = [];
        List<WorkflowAction> actions = [];
        try
        {
            conditions = request.Conditions is null ? [] : request.Conditions.Deserialize<List<WorkflowCondition>>(RuleDocuments.Json) ?? [];
        }
        catch (JsonException)
        {
            problems.Add(new("conditions", "conditions-invalid", "The conditions are not in the expected shape."));
        }

        try
        {
            actions = request.Actions is null ? [] : request.Actions.Deserialize<List<WorkflowAction>>(RuleDocuments.Json) ?? [];
        }
        catch (Exception e) when (e is JsonException or NotSupportedException)
        {
            problems.Add(new("actions", "actions-invalid", "Each action has a known type (assign, notify, approval or connector) and the fields of that type."));
        }

        RuleDocuments.CheckConditions(conditions, "conditions", problems);
        if (actions.Count == 0 || actions.Count > RuleDocuments.MaxItems)
        {
            problems.Add(new("actions", "actions-required", $"Add 1 to {RuleDocuments.MaxItems} actions."));
        }

        for (var i = 0; i < actions.Count; i++)
        {
            var at = $"actions.{i}";
            switch (actions[i])
            {
                case AssignAction assign:
                    if (assign.GroupId is null || !Enum.IsDefined(assign.Method))
                    {
                        problems.Add(new($"{at}.groupId", "group-required", "Choose the group to assign to."));
                    }

                    break;
                case NotifyAction notify:
                    RuleDocuments.CheckNotify(notify, at, problems);
                    break;
                case ConnectorAction connector:
                    if (connector.ConnectorId is null || string.IsNullOrWhiteSpace(connector.Operation))
                    {
                        problems.Add(new($"{at}.connectorId", "connector-required", "Choose the connector and its operation."));
                    }

                    break;
                case ApprovalAction approval:
                    if (request.RecordType != TicketType.ServiceRequest || trigger != WorkflowTrigger.TicketCreated)
                    {
                        problems.Add(new(at, "approval-not-allowed", "Approvals are asked for by rules on service requests when they are submitted."));
                    }

                    if (approval.Levels.Count == 0)
                    {
                        problems.Add(new($"{at}.levels", "levels-required", "Add at least one approval level."));
                    }

                    RuleDocuments.CheckLevels(approval.Levels, $"{at}.levels", problems);
                    break;
                default:
                    problems.Add(new(at, "action-invalid", "Choose a known action."));
                    break;
            }
        }

        if (problems.Count == 0)
        {
            await WorkflowReferences.CheckConditionsAsync(db, conditions, "conditions", problems, cancellationToken);
            await WorkflowReferences.CheckActionsAsync(db, actions, "actions", problems, cancellationToken);
        }

        if (problems.Count > 0)
        {
            return (null, Problems(problems));
        }

        return (new ParsedRule(name, description, request.Active ?? true, trigger, request.RecordType, request.Match ?? ConditionMatch.All,
            conditions, actions, request.StopProcessing ?? false), null);
    }

    /// <summary>A 400 answer listing every problem, by field.</summary>
    internal static IResult Problems(List<FormProblem> problems) =>
        TypedResults.ValidationProblem(
            problems.GroupBy(p => p.Field ?? string.Empty).ToDictionary(g => g.Key, g => g.Select(p => p.Message).ToArray(), StringComparer.Ordinal),
            type: ApiProblem.TypeFor("validation-failed"),
            extensions: new Dictionary<string, object?>(StringComparer.Ordinal) { ["code"] = "validation-failed", ["problems"] = problems });

    private static void Apply(WorkflowRule rule, ParsedRule parsed)
    {
        rule.Name = parsed.Name;
        rule.Description = parsed.Description;
        rule.Active = parsed.Active;
        rule.Trigger = parsed.Trigger;
        rule.RecordType = parsed.RecordType;
        rule.Match = parsed.Match;
        rule.Conditions = RuleDocuments.Write(parsed.Conditions);
        rule.Actions = RuleDocuments.Write(parsed.Actions);
        rule.StopProcessing = parsed.StopProcessing;
    }

    internal static WorkflowRuleResponse Response(WorkflowRule r) => new(
        r.Id, LocalizedTextDto.From(r.Name), LocalizedTextDto.From(r.Description), r.Position, r.Active, r.Trigger, r.RecordType, r.Match,
        JsonNode.Parse(r.Conditions) ?? new JsonArray(), JsonNode.Parse(r.Actions) ?? new JsonArray(), r.StopProcessing,
        $"\"{r.Version}\"", r.CreatedAt, r.UpdatedAt, r.IsDraft, r.SourceText, r.SourceLanguage, RuleUnmapped(r), r.ApprovedAt);

    private static string TypeOf(WorkflowAction action) => action switch
    {
        AssignAction => "assign",
        NotifyAction => "notify",
        ConnectorAction => "connector",
        _ => "approval",
    };

    private static Microsoft.AspNetCore.Http.HttpResults.ProblemHttpResult? IfMatchProblem(HttpContext http, ZelvoDbContext db, WorkflowRule rule)
    {
        var ifMatch = http.Request.Headers.IfMatch.ToString();
        if (string.IsNullOrEmpty(ifMatch))
        {
            return null;
        }

        if (!uint.TryParse(ifMatch.Trim().Replace("W/", "", StringComparison.Ordinal).Trim('"'), out var version) || version != rule.Version)
        {
            return FormsCommon.VersionMismatch();
        }

        db.Entry(rule).Property(r => r.Version).OriginalValue = version;
        return null;
    }
}

/// <summary>A workflow rule as sent by clients.</summary>
internal sealed class WorkflowRuleInput
{
    /// <summary>Name in English and Arabic.</summary>
    [Required]
    public LocalizedTextInput? Name { get; init; }

    /// <summary>What it is for.</summary>
    public LocalizedTextInput? Description { get; init; }

    /// <summary>Whether it runs (default true).</summary>
    public bool? Active { get; init; }

    /// <summary>When it is looked at.</summary>
    [Required]
    public WorkflowTrigger? Trigger { get; init; }

    /// <summary>Incident, ServiceRequest, or null for both.</summary>
    public TicketType? RecordType { get; init; }

    /// <summary>All (default) or Any.</summary>
    public ConditionMatch? Match { get; init; }

    /// <summary>Conditions: [{ field, op, value }].</summary>
    public JsonArray? Conditions { get; init; }

    /// <summary>Actions: [{ type: "assign", groupId, method, assigneeId } | { type: "notify", recipients, subject, body, email } | { type: "approval", stage, levels }].</summary>
    public JsonArray? Actions { get; init; }

    /// <summary>When it matched, later rules of the trigger are not tried.</summary>
    public bool? StopProcessing { get; init; }
}

/// <summary>The order of all rules.</summary>
internal sealed class OrderWorkflowRulesRequest
{
    /// <summary>Every rule's id, in the order to try them.</summary>
    [Required]
    public List<Guid>? Ids { get; init; }
}

/// <summary>Which rules hold for a ticket.</summary>
internal sealed class RuleTestRequest
{
    /// <summary>The ticket's number, for example REQ-000123.</summary>
    [Required]
    [StringLength(Ticket.NumberMaxLength)]
    public string? Number { get; init; }

    /// <summary>The trigger to try.</summary>
    [Required]
    public WorkflowTrigger? Trigger { get; init; }

    /// <summary>Task K1: also try drafts waiting for approval (they never run on their own).</summary>
    public bool? IncludeDrafts { get; init; }
}

/// <summary>
/// A workflow rule. Task K1: <c>draft</c> rules were made by the AI from <c>sourceText</c> and do
/// not run until approved; <c>unmapped</c> lists what the AI could not express with the builder.
/// </summary>
internal sealed record WorkflowRuleResponse(
    Guid Id, LocalizedTextDto Name, LocalizedTextDto Description, int Position, bool Active, WorkflowTrigger Trigger, TicketType? RecordType,
    ConditionMatch Match, JsonNode Conditions, JsonNode Actions, bool StopProcessing, string Etag, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    bool Draft, string? SourceText, string? SourceLanguage, IReadOnlyList<string> Unmapped, DateTimeOffset? ApprovedAt);

/// <summary>Whether a rule holds for the tested ticket, and the actions it would run (task K1: whether it is a draft).</summary>
internal sealed record RuleTestResult(Guid Id, LocalizedTextDto Name, bool Holds, IReadOnlyList<string> Actions, bool Draft);
