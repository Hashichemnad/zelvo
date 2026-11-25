using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Zelvo.Data;
using Zelvo.Data.Ai;
using Zelvo.Data.Auditing;
using Zelvo.Data.Directory;
using Zelvo.Data.Tickets;
using Zelvo.Search;
using Zelvo.Tickets;
using Zelvo.Tickets.Sla;

namespace Zelvo.Ai.Features;

/// <summary>Tenant settings of the AI features that are not part of a feature's provider and model.</summary>
public static class AiFeatureSettingKeys
{
    /// <summary>
    /// Apply triage automatically to new incidents (product owner decision 2026-11-24: the rule an
    /// administrator approves, module 9). Off by default: suggestions wait for an agent.
    /// </summary>
    public const string TriageAutoApply = "ai.triage.autoApply";

    /// <summary>Whether triage applies on its own, read straight from <c>tenant_settings</c>.</summary>
    public static Task<bool> ReadTriageAutoApplyAsync(ZelvoDbContext db, CancellationToken cancellationToken) =>
        TicketSettingKeys.ReadAsync(db, TriageAutoApply, false, _ => true, cancellationToken);
}

/// <summary>What an agent decides about a triage suggestion.</summary>
/// <param name="CategoryId">The category to give the incident (the suggestion's, or a correction).</param>
/// <param name="Impact">The impact (1–3).</param>
/// <param name="Urgency">The urgency (1–3).</param>
/// <param name="GroupId">The group.</param>
public sealed record TriageChoice(Guid? CategoryId, int? Impact, int? Urgency, Guid? GroupId);

/// <summary>Why a triage choice cannot be applied.</summary>
public enum TriageProblem
{
    /// <summary>It can.</summary>
    None,

    /// <summary>The category is not active.</summary>
    Category,

    /// <summary>The group is not active.</summary>
    Group,

    /// <summary>A level is not 1–3.</summary>
    Level,

    /// <summary>The incident is closed.</summary>
    Closed,
}

/// <summary>
/// Auto-categorise, prioritise and route new incidents (module 9). The AI suggests a category,
/// impact and urgency (so a priority from the tenant's matrix) and a group; agents accept or
/// correct it. What agents finally chose is given to later suggestions as examples, so
/// corrections improve accuracy without training a model. The ticket changes only when a person
/// accepts, or when the administrator turned on "apply triage automatically" (product owner
/// decision 2026-11-24); every change is audited.
/// </summary>
public sealed class TicketTriage(ZelvoDbContext db, IAiGateway gateway, SlaTracker sla, ISearchIndexer indexer, IAuditLog audit, TimeProvider time)
{
    /// <summary>The last AI call this made, for the worker to tell a provider failure from "nothing to do".</summary>
    public AiResult? LastCall { get; private set; }

    /// <summary>How many decided suggestions are given as examples.</summary>
    public const int ExampleCount = 15;

    private const string Instructions = """
        Task: triage.
        You sort a new incident for an IT and service desk. The data blocks hold the incident (its title and description, in English or Arabic, Modern Standard or Gulf dialect), the categories and the support groups to choose from, each with a reference such as c3 or g2, and examples of how agents classified earlier incidents.
        Choose the category and the group that fit best, by their reference, or null when none fits.
        Impact: 1 = many people or a critical service are affected, 2 = a team or an important service, 3 = one person.
        Urgency: 1 = work has stopped now, 2 = work is slowed down, 3 = it can wait.
        The examples are agents' decisions, including their corrections: follow them for similar incidents.
        Answer as JSON: {"category": "c1" or null, "group": "g1" or null, "impact": 1, "urgency": 2, "confidence": 0 to 100, "reason": "one short sentence in English"}.
        """;

    /// <summary>
    /// Makes a suggestion for an open incident and, when the tenant applies triage automatically,
    /// applies it. Returns null when the AI is off, the call failed (see the AI log) or the
    /// incident is no longer open.
    /// </summary>
    public async Task<AiTriageSuggestion?> SuggestAsync(Guid ticketId, CancellationToken cancellationToken)
    {
        var ticket = await db.Tickets.Include(t => t.Incident).Include(t => t.Requester)
            .SingleOrDefaultAsync(t => t.Id == ticketId && t.Type == TicketType.Incident, cancellationToken).ConfigureAwait(false);
        if (ticket is null || !TicketWorkflow.IsOpen(ticket.State))
        {
            return null;
        }

        var categories = await SelectableCategoriesAsync(db, cancellationToken).ConfigureAwait(false);
        var groups = await db.SupportGroups.AsNoTracking().Where(g => g.Active).OrderBy(g => g.Name.En).ThenBy(g => g.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
        var categoryRefs = categories.Select((c, i) => (Ref: AiText.Ref('c', i), c.Id, c.Path)).ToList();
        var groupRefs = groups.Select((g, i) => (Ref: AiText.Ref('g', i), g.Id, Name: $"{g.Name.En} / {g.Name.Ar}")).ToList();

        var inputs = new List<AiInput>
        {
            new("incident", $"Title: {ticket.Title}\n\nDescription: {ticket.Description ?? "(none)"}"),
            new("categories", categoryRefs.Count == 0 ? "(none)" : string.Join('\n', categoryRefs.Select(c => $"{c.Ref}: {c.Path}"))),
            new("groups", groupRefs.Count == 0 ? "(none)" : string.Join('\n', groupRefs.Select(g => $"{g.Ref}: {g.Name}"))),
        };
        var examples = await ExamplesAsync(categoryRefs.ToDictionary(c => c.Id, c => c.Ref), groupRefs.ToDictionary(g => g.Id, g => g.Ref), ticket.Id, cancellationToken).ConfigureAwait(false);
        if (examples.Length > 0)
        {
            inputs.Add(new AiInput("examples", examples));
        }

        var result = await gateway.CompleteAsync(new AiRequest
        {
            Feature = AiFeatures.Triage,
            Instructions = Instructions,
            Inputs = inputs,
            PersonalValues = Personal(ticket.Requester),
            Json = true,
            MaxOutputTokens = 400,
            EntityType = "ticket",
            EntityId = ticket.Id.ToString(),
        }, cancellationToken).ConfigureAwait(false);
        LastCall = result;
        if (!result.Succeeded)
        {
            return null;
        }

        using var json = JsonDocument.Parse(result.Text!);
        var answer = json.RootElement;
        var category = AiText.Text(answer, "category") is { } c ? categoryRefs.FirstOrDefault(r => r.Ref == c).Id : Guid.Empty;
        var group = AiText.Text(answer, "group") is { } g ? groupRefs.FirstOrDefault(r => r.Ref == g).Id : Guid.Empty;
        var suggestion = new AiTriageSuggestion
        {
            TicketId = ticket.Id,
            CategoryId = category == Guid.Empty ? null : category,
            GroupId = group == Guid.Empty ? null : group,
            Impact = AiText.Number(answer, "impact") is { } impact && PriorityMatrix.IsLevel(impact) ? impact : null,
            Urgency = AiText.Number(answer, "urgency") is { } urgency && PriorityMatrix.IsLevel(urgency) ? urgency : null,
            Confidence = Math.Clamp(AiText.Number(answer, "confidence") ?? 50, 0, 100),
            Reason = AiText.Text(answer, "reason") is { } reason ? AiText.Cut(reason, AiTriageSuggestion.ReasonMaxLength) : null,
            AiCallId = result.CallId,
            CreatedAt = time.GetUtcNow(),
        };

        // A newer suggestion replaces any still waiting.
        foreach (var older in await db.AiTriageSuggestions.Where(s => s.TicketId == ticket.Id && s.Status == TriageStatus.Pending).ToListAsync(cancellationToken).ConfigureAwait(false))
        {
            older.Status = TriageStatus.Outdated;
        }

        db.AiTriageSuggestions.Add(suggestion);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        if (await AiFeatureSettingKeys.ReadTriageAutoApplyAsync(db, cancellationToken).ConfigureAwait(false)
            && (suggestion.CategoryId ?? suggestion.GroupId ?? (object?)suggestion.Impact ?? suggestion.Urgency) is not null)
        {
            // The approved rule fills in what the requester or agent did not choose: the category
            // when none was given, impact and urgency, and the group unless someone took the ticket.
            var choice = new TriageChoice(
                ticket.CategoryId is null ? suggestion.CategoryId : null,
                suggestion.Impact,
                suggestion.Urgency,
                ticket.AssigneeId is null ? suggestion.GroupId : null);
            if (await CheckAsync(db, ticket, choice, cancellationToken).ConfigureAwait(false) == TriageProblem.None)
            {
                await ApplyAsync(ticket, choice, cancellationToken).ConfigureAwait(false);
                suggestion.Status = TriageStatus.AutoApplied;
                suggestion.DecidedAt = time.GetUtcNow();
                Finalise(suggestion, ticket);
                audit.Add(AuditCategory.Ai, "ai.triage.auto-applied", "ticket", ticket.Id.ToString(), Details(suggestion));
            }
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return suggestion;
    }

    /// <summary>
    /// An agent's decision: the choice as suggested is <see cref="TriageStatus.Accepted"/>, with
    /// changes <see cref="TriageStatus.Corrected"/>. Applies it to the ticket in the caller's
    /// transaction; the caller saves.
    /// </summary>
    public async Task<TriageProblem> DecideAsync(AiTriageSuggestion suggestion, Ticket ticket, TriageChoice choice, Guid agentId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(suggestion);
        ArgumentNullException.ThrowIfNull(ticket);
        ArgumentNullException.ThrowIfNull(choice);
        if (await CheckAsync(db, ticket, choice, cancellationToken).ConfigureAwait(false) is not TriageProblem.None and var problem)
        {
            return problem;
        }

        await ApplyAsync(ticket, choice, cancellationToken).ConfigureAwait(false);
        var same = choice.CategoryId == suggestion.CategoryId && choice.GroupId == suggestion.GroupId
            && choice.Impact == suggestion.Impact && choice.Urgency == suggestion.Urgency;
        suggestion.Status = same ? TriageStatus.Accepted : TriageStatus.Corrected;
        suggestion.DecidedById = agentId;
        suggestion.DecidedAt = time.GetUtcNow();
        Finalise(suggestion, ticket);
        audit.Add(AuditCategory.Ai, same ? "ai.triage.accepted" : "ai.triage.corrected", "ticket", ticket.Id.ToString(), Details(suggestion));
        return TriageProblem.None;
    }

    /// <summary>Turns a suggestion down; the ticket stays as it is.</summary>
    public void Dismiss(AiTriageSuggestion suggestion, Guid agentId)
    {
        ArgumentNullException.ThrowIfNull(suggestion);
        suggestion.Status = TriageStatus.Dismissed;
        suggestion.DecidedById = agentId;
        suggestion.DecidedAt = time.GetUtcNow();
        audit.Add(AuditCategory.Ai, "ai.triage.dismissed", "ticket", suggestion.TicketId.ToString(), Details(suggestion));
    }

    /// <summary>Whether <paramref name="choice"/> can be applied to <paramref name="ticket"/>.</summary>
    public static async Task<TriageProblem> CheckAsync(ZelvoDbContext db, Ticket ticket, TriageChoice choice, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(ticket);
        ArgumentNullException.ThrowIfNull(choice);
        if (!TicketWorkflow.IsOpen(ticket.State))
        {
            return TriageProblem.Closed;
        }

        if (choice.Impact is { } i && !PriorityMatrix.IsLevel(i) || choice.Urgency is { } u && !PriorityMatrix.IsLevel(u))
        {
            return TriageProblem.Level;
        }

        if (choice.CategoryId is { } category && category != ticket.CategoryId
            && !(await SelectableCategoriesAsync(db, cancellationToken).ConfigureAwait(false)).Any(c => c.Id == category))
        {
            return TriageProblem.Category;
        }

        if (choice.GroupId is { } group && group != ticket.GroupId && !await db.SupportGroups.AnyAsync(g => g.Id == group && g.Active, cancellationToken).ConfigureAwait(false))
        {
            return TriageProblem.Group;
        }

        return TriageProblem.None;
    }

    /// <summary>
    /// Active categories whose parents are active too, with their path in English and Arabic
    /// (for example <c>Hardware › Printers / الأجهزة › الطابعات</c>).
    /// </summary>
    public static async Task<List<(Guid Id, string Path)>> SelectableCategoriesAsync(ZelvoDbContext db, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        var all = await db.Categories.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
        var byId = all.ToDictionary(c => c.Id);
        var selectable = new List<(Guid, string)>();
        foreach (var category in all.OrderBy(c => c.Depth).ThenBy(c => c.Position).ThenBy(c => c.Id))
        {
            var chain = new List<Category>();
            for (Category? node = category; node is not null; node = node.ParentId is { } p ? byId.GetValueOrDefault(p) : null)
            {
                chain.Insert(0, node);
            }

            if (chain.All(n => n.Active))
            {
                selectable.Add((category.Id, $"{string.Join(" › ", chain.Select(n => n.Name.En ?? n.Name.Ar))} / {string.Join(" › ", chain.Select(n => n.Name.Ar ?? n.Name.En))}"));
            }
        }

        return selectable;
    }

    // Changes the ticket: category, impact and urgency with the priority from the tenant's matrix,
    // and the group (an assignee who is not a member is taken off). SLA clocks and the search
    // index follow.
    private async Task ApplyAsync(Ticket ticket, TriageChoice choice, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        if (choice.CategoryId is { } category)
        {
            ticket.CategoryId = category;
        }

        if (ticket.Incident is { } incident && (choice.Impact is not null || choice.Urgency is not null))
        {
            incident.Impact = choice.Impact ?? incident.Impact;
            incident.Urgency = choice.Urgency ?? incident.Urgency;
            var matrix = await TicketSettingKeys.ReadAsync(db, TicketSettingKeys.PriorityMatrix, PriorityMatrix.Default, m => m.IsValid(), cancellationToken).ConfigureAwait(false);
            incident.Priority = matrix.PriorityOf(incident.Impact, incident.Urgency);
        }

        if (choice.GroupId is { } group && group != ticket.GroupId)
        {
            ticket.GroupId = group;
            ticket.RoutingRuleId = null;
            if (ticket.AssigneeId is { } assignee && !await db.SupportGroupMembers.AnyAsync(m => m.GroupId == group && m.UserId == assignee, cancellationToken).ConfigureAwait(false))
            {
                TicketWorkflow.Assign(ticket, null, now);
            }
        }

        ticket.UpdatedAt = now;
        await sla.UpdateAsync([ticket], now, cancellationToken).ConfigureAwait(false);
        indexer.Upsert(IncidentSearch.Document(ticket, await IncidentSearch.StaffRolesAsync(db, cancellationToken).ConfigureAwait(false)));
    }

    // Earlier decisions of agents as examples: the incident's title and what it finally got.
    private async Task<string> ExamplesAsync(Dictionary<Guid, string> categoryRefs, Dictionary<Guid, string> groupRefs, Guid except, CancellationToken cancellationToken)
    {
        var decided = await db.AiTriageSuggestions.AsNoTracking()
            .Where(s => s.TicketId != except && s.DecidedAt != null && (s.Status == TriageStatus.Accepted || s.Status == TriageStatus.Corrected))
            .OrderByDescending(s => s.DecidedAt).Take(ExampleCount)
            .Join(db.Tickets, s => s.TicketId, t => t.Id, (s, t) => new { s, t.Title })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var lines = new StringBuilder();
        foreach (var example in decided)
        {
            var s = example.s;
            lines.Append(CultureInfo.InvariantCulture, $"- \"{AiText.Cut(example.Title, 150)}\" → category {Ref(categoryRefs, s.FinalCategoryId)}, group {Ref(groupRefs, s.FinalGroupId)}, impact {Level(s.FinalImpact)}, urgency {Level(s.FinalUrgency)}");
            lines.Append(s.Status == TriageStatus.Corrected ? " (corrected by an agent)\n" : "\n");
        }

        return lines.ToString().TrimEnd();
    }

    private static void Finalise(AiTriageSuggestion suggestion, Ticket ticket)
    {
        suggestion.FinalCategoryId = ticket.CategoryId;
        suggestion.FinalGroupId = ticket.GroupId;
        suggestion.FinalImpact = ticket.Incident?.Impact;
        suggestion.FinalUrgency = ticket.Incident?.Urgency;
    }

    private static Dictionary<string, object?> Details(AiTriageSuggestion s) => new(StringComparer.Ordinal)
    {
        ["suggestionId"] = s.Id.ToString(),
        ["categoryId"] = s.FinalCategoryId?.ToString(),
        ["groupId"] = s.FinalGroupId?.ToString(),
        ["impact"] = s.FinalImpact,
        ["urgency"] = s.FinalUrgency,
        ["aiCallId"] = s.AiCallId,
    };

    private static string Ref(Dictionary<Guid, string> refs, Guid? id) => id is { } value && refs.TryGetValue(value, out var r) ? r : "null";

    private static string Level(int? level) => level?.ToString(CultureInfo.InvariantCulture) ?? "null";

    /// <summary>A person's name and email, masked before anything is sent.</summary>
    internal static IReadOnlyCollection<string> Personal(params Data.Identity.UserAccount?[] people) =>
        [.. people.Where(p => p is not null).SelectMany(p => new[] { p!.DisplayName, p.Email }).Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v!)];
}
