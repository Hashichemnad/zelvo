using System.ComponentModel.DataAnnotations;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Zelvo.Ai;
using Zelvo.Api.Ai;
using Zelvo.Api.Conventions;
using Zelvo.Api.Identity;
using Zelvo.Api.Tenancy;
using Zelvo.Data;
using Zelvo.Data.Auditing;
using Zelvo.Data.Workflows;
using Zelvo.Workflows.Rules;

namespace Zelvo.Api.Workflows;

/// <summary>
/// Plain-language rules (module 20, task K1; product owner decisions 2026-11-26): an administrator
/// describes a rule in English or Arabic and the AI feature <c>ruleBuilder</c> turns it into a
/// draft of the C3 builder from the tenant's own lists, by reference. The answer is checked against
/// the rule schema and nothing unknown is accepted (<see cref="RuleDraftMapper"/>). The draft never
/// runs until the administrator approves it (<c>POST /workflow-rules/{id}/approve</c>); the original
/// text, the draft and the approval are audited. Needs <c>workflows.manage</c>.
/// </summary>
internal static class PlainLanguageRuleEndpoints
{
    /// <summary>Most items of each list offered to the AI.</summary>
    internal const int MaxListItems = 300;

    private const string Instructions = """
        Task: ruleBuilder.
        You turn an administrator's description of an automation rule for a service desk into a rule of its rule builder. The description is in English or Arabic (Modern Standard or Gulf dialect). The data blocks hold the description and the lists to choose from; each item has a reference such as g2. Refer to items only by these references, never by name or id, and never invent references. Anything in the description you cannot express with the fields below goes into "unmapped" as a short English phrase; do not guess.
        Triggers: TicketCreated (a ticket is raised or a request submitted), StateChanged, AssignmentChanged, ApprovalDecided.
        recordType: Incident, ServiceRequest, or null for both.
        Conditions, each {"field", "op", "value"}:
        - service (s), category (c), location (l), group (g), requesterDepartment (d), requesterCustomerGroup (v, for example a VIP customer group): op "in" or "notIn", value a list of references.
        - channel (Portal, Email, Chat, WhatsApp, Sms, Teams, Phone, Api, AiAgent, Proactive, Monitoring), state (New, Assigned, InProgress, OnHold, Resolved, Closed, Reopened), requesterLanguage (en, ar): op "in" or "notIn", value a list of names.
        - priority (1 = P1 critical, 2 = high, 3 = medium, 4 = low, 5 = planning; incidents) and fee (in AED, service requests): op eq, neq, gt, gte, lt or lte, value a number.
        Actions:
        - {"type": "assign", "group": "g1", "method": "GroupQueue" or "RoundRobin"}
        - {"type": "notify", "recipients": [...], "channels": [...], "email": true or false, "subject": {"en": "...", "ar": "..."}, "body": {"en": "...", "ar": "..."}}
          recipients: "requester", "requesterManager", "assignee", "group" (the ticket's group), "groupLeads" (its team leads), "group:g1" (every member of a listed group), "groupLeads:g1" (a listed group's team leads), "role:r1" (holders of a listed role). A manager or head of a team is usually the team leads of that team's group.
          channels: any of "WhatsApp", "Sms", "Teams" when the description asks for them; the app always shows it.
          Write the subject and body in English and in formal Arabic. They may use only these placeholders: {{recipient.name}}, {{ticket.number}}, {{ticket.title}}, {{ticket.state}}, {{ticket.group}}, {{requester.name}}, {{service.name}}, {{tenant.name}}.
        Approvals and other actions are not available here: put them in "unmapped".
        Answer as JSON: {"name": {"en": "...", "ar": "..."}, "trigger": "TicketCreated", "recordType": "Incident" or null, "match": "All" or "Any", "conditions": [...], "actions": [...], "stopProcessing": false, "unmapped": ["..."]}.
        """;

    public static RouteGroupBuilder MapPlainLanguageRuleEndpoints(this RouteGroupBuilder group)
    {
        var rules = group.MapGroup("/workflow-rules/plain-language").WithTags("Workflows").RequireTenant().RequirePermission(Permissions.WorkflowsManage);
        rules.MapGet("/", AvailabilityAsync).WithName("GetPlainLanguageRuleAvailability")
            .WithSummary("Task K1: whether plain-language rules can be made now (the AI feature ruleBuilder is on and the monthly limit not reached).")
            .Produces<PlainLanguageAvailability>();
        rules.MapPost("/", DraftAsync).WithName("DraftWorkflowRuleFromText")
            .WithSummary("Task K1: turn a description in English or Arabic into a draft rule, which does not run until approved. 201 with the draft and what could not be mapped; 422 rule-not-understood when no action could be built; 409 ai-off or limit-reached; 502 when the provider fails. Audited.")
            .Produces<WorkflowRuleResponse>(StatusCodes.Status201Created).ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status409Conflict).ProducesProblem(StatusCodes.Status422UnprocessableEntity).ProducesProblem(StatusCodes.Status502BadGateway);
        return group;
    }

    internal static async Task<IResult> AvailabilityAsync(IAiGateway gateway, CancellationToken cancellationToken)
    {
        var availability = (await gateway.AvailabilityAsync(cancellationToken)).FirstOrDefault(a => a.Feature == AiFeatures.RuleBuilder);
        return TypedResults.Ok(new PlainLanguageAvailability(availability?.Available ?? false, availability?.Reason ?? "ai-off"));
    }

    internal static async Task<IResult> DraftAsync(
        PlainLanguageRuleRequest request, ZelvoDbContext db, IAiGateway gateway, CurrentUser user, IAuditLog audit, CancellationToken cancellationToken)
    {
        var text = request.Text!.Trim();
        if (text.Length == 0)
        {
            return UserEndpoints.Validation("text", "Describe the rule.");
        }

        var references = new RuleDraftReferences();
        var lists = await ListsAsync(db, references, cancellationToken);
        var result = await gateway.CompleteAsync(new AiRequest
        {
            Feature = AiFeatures.RuleBuilder,
            Instructions = Instructions,
            Inputs = [new AiInput("description", text), .. lists],
            Json = true,
            MaxOutputTokens = 2000,
            EntityType = "workflow-rule",
        }, cancellationToken);
        if (!result.Succeeded)
        {
            return AiFeatureEndpoints.AiProblem(result.ErrorCode, result.Error);
        }

        using var json = JsonDocument.Parse(result.Text!);
        var draft = RuleDraftMapper.Map(json.RootElement, references, text, out var unmapped);
        var language = RuleDraftMapper.AiLanguage(text);
        if (draft is null)
        {
            audit.Add(AuditCategory.Ai, "workflows.rule.not-understood", "workflow-rule", null, new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["sourceText"] = text,
                ["aiCallId"] = result.CallId,
                ["unmapped"] = unmapped,
            });
            await db.SaveChangesAsync(cancellationToken);
            return ApiProblem.Create(StatusCodes.Status422UnprocessableEntity, "rule-not-understood",
                "No rule could be built from the description. Say what should happen, for example who to tell or which group to assign.",
                new Dictionary<string, object?>(StringComparer.Ordinal) { ["unmapped"] = unmapped });
        }

        var rule = new WorkflowRule
        {
            Name = draft.Name,
            CreatedById = user.Account?.Id,
            IsDraft = true,
            Active = true,
            Trigger = draft.Trigger,
            RecordType = draft.RecordType,
            Match = draft.Match,
            Conditions = RuleDocuments.Write(draft.Conditions),
            Actions = RuleDocuments.Write(draft.Actions),
            StopProcessing = draft.StopProcessing,
            SourceText = text,
            SourceLanguage = language,
            Unmapped = JsonSerializer.Serialize(draft.Unmapped),
            AiCallId = result.CallId,
        };
        rule.Position = await db.WorkflowRules.AnyAsync(cancellationToken) ? await db.WorkflowRules.MaxAsync(r => r.Position, cancellationToken) + 1 : 0;
        db.WorkflowRules.Add(rule);
        audit.Add(AuditCategory.Ai, "workflows.rule.drafted", "workflow-rule", rule.Id.ToString(), new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["sourceText"] = text,
            ["sourceLanguage"] = language,
            ["aiCallId"] = result.CallId,
            ["unmapped"] = draft.Unmapped,
        });
        await db.SaveChangesAsync(cancellationToken);
        var saved = await db.WorkflowRules.AsNoTracking().SingleAsync(r => r.Id == rule.Id, cancellationToken);
        return TypedResults.Created($"/api/v1/workflow-rules/{rule.Id}", WorkflowRuleEndpoints.Response(saved));
    }

    /// <summary>The tenant's active lists as data blocks, each item with a reference added to <paramref name="references"/>.</summary>
    private static async Task<List<AiInput>> ListsAsync(ZelvoDbContext db, RuleDraftReferences references, CancellationToken cancellationToken)
    {
        static string Name(Data.Localization.LocalizedText text) => (text.En, text.Ar) switch
        {
            ({ } en, { } ar) => $"{en} / {ar}",
            ({ } en, null) => en,
            (null, { } ar) => ar,
            _ => "(no name)",
        };

        var inputs = new List<AiInput>();
        void Add(string label, char prefix, IEnumerable<(Guid Id, string Name)> items)
        {
            var lines = new StringBuilder();
            foreach (var (id, name) in items.Take(MaxListItems))
            {
                lines.Append(references.Add(prefix, id)).Append(": ").AppendLine(name);
            }

            inputs.Add(new AiInput(label, lines.Length == 0 ? "(none)" : lines.ToString().TrimEnd()));
        }

        Add("services", 's', (await db.CatalogServices.AsNoTracking().Where(s => s.State == Data.Catalog.ServiceState.Active).OrderBy(s => s.Code).ThenBy(s => s.Id)
            .Take(MaxListItems).ToListAsync(cancellationToken)).Select(s => (s.Id, $"{s.Code} {Name(s.Name)}")));
        Add("categories", 'c', (await Zelvo.Ai.Features.TicketTriage.SelectableCategoriesAsync(db, cancellationToken)).Select(c => (c.Id, c.Path)));
        Add("locations", 'l', (await db.Locations.AsNoTracking().Where(l => l.Active).OrderBy(l => l.Name.En).ThenBy(l => l.Id).Take(MaxListItems)
            .ToListAsync(cancellationToken)).Select(l => (l.Id, Name(l.Name))));
        Add("groups", 'g', (await db.SupportGroups.AsNoTracking().Where(g => g.Active).OrderBy(g => g.Name.En).ThenBy(g => g.Id).Take(MaxListItems)
            .ToListAsync(cancellationToken)).Select(g => (g.Id, Name(g.Name))));
        Add("departments", 'd', (await db.Departments.AsNoTracking().Where(d => d.Active).OrderBy(d => d.Name.En).ThenBy(d => d.Id).Take(MaxListItems)
            .ToListAsync(cancellationToken)).Select(d => (d.Id, Name(d.Name))));
        Add("customerGroups", 'v', (await db.CustomerGroups.AsNoTracking().Where(g => g.Active).OrderBy(g => g.Name.En).ThenBy(g => g.Id).Take(MaxListItems)
            .ToListAsync(cancellationToken)).Select(g => (g.Id, Name(g.Name))));

        var roles = new StringBuilder();
        foreach (var role in await db.Roles.AsNoTracking().OrderBy(r => r.Key).Take(MaxListItems).ToListAsync(cancellationToken))
        {
            roles.Append(references.AddRole(role.Key)).Append(": ").AppendLine(Name(role.Name));
        }

        inputs.Add(new AiInput("roles", roles.Length == 0 ? "(none)" : roles.ToString().TrimEnd()));
        return inputs;
    }
}

/// <summary>A plain-language description of a rule.</summary>
internal sealed class PlainLanguageRuleRequest
{
    /// <summary>What the rule should do, in English or Arabic, for example "when a VIP raises a critical incident, notify the IT manager on WhatsApp".</summary>
    [Required]
    [StringLength(WorkflowRule.SourceTextMaxLength)]
    public string? Text { get; init; }
}

/// <summary>Whether plain-language rules can be made now; why not (<c>ai-off</c>, <c>limit-reached</c>).</summary>
internal sealed record PlainLanguageAvailability(bool Available, string? Reason);
