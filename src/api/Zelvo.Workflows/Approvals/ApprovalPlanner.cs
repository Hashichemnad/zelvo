using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Zelvo.Data;
using Zelvo.Data.Identity;
using Zelvo.Data.Localization;
using Zelvo.Data.Tickets;
using Zelvo.Data.Workflows;
using Zelvo.Workflows.Rules;

namespace Zelvo.Workflows.Approvals;

/// <summary>A level of an approval process with the people asked, as planned when the process starts.</summary>
/// <param name="Name">Name shown to requesters and approvers.</param>
/// <param name="Mode">Any or all approvers.</param>
/// <param name="ApproverIds">Who is asked.</param>
/// <param name="Fallback">
/// Nobody named by the level could be asked (for example the requester has no manager), so the
/// tenant's workflow administrators were asked instead.
/// </param>
/// <param name="Board">
/// Task H4: <c>Advisory</c> when the level is the change advisory board (decided in a meeting by its
/// chair), <c>Emergency</c> for the emergency board; <see langword="null"/> otherwise.
/// </param>
public sealed record PlannedLevel(LocalizedText Name, ApprovalMode Mode, IReadOnlyList<Guid> ApproverIds, bool Fallback, string? Board = null);

/// <summary>
/// Plans approval processes (task C3): the service's levels for the stage, then the levels of
/// approval actions of matching rules (trigger "ticket created", in rule order). Levels whose
/// conditions do not hold are left out (approval by amount or answer). Approvers are worked out
/// now: the requester's manager, named people, a group's members or team leads, holders of a role,
/// or the person picked in a form answer; never the requester themselves.
/// </summary>
public static class ApprovalPlanner
{
    /// <summary>The permission whose holders approve when a level names nobody who can (<c>workflows.manage</c>).</summary>
    public const string FallbackPermission = "workflows.manage";

    /// <summary>Task H4: who decides for the change advisory board while it has no chair (<c>changes.manage</c>).</summary>
    public const string ChangeManagerPermission = "changes.manage";

    /// <summary>Task H4: who decides emergency changes while the emergency board has no members (<c>changes.approve</c>).</summary>
    public const string BoardMemberPermission = "changes.approve";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    /// <summary>Plans the levels of <paramref name="stage"/> for <paramref name="ticket"/>.</summary>
    public static async Task<List<PlannedLevel>> PlanAsync(
        ZelvoDbContext db, Ticket ticket, ApprovalStage stage, IReadOnlyList<ApprovalLevelDefinition> serviceLevels, RuleFacts facts,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(ticket);
        ArgumentNullException.ThrowIfNull(serviceLevels);
        ArgumentNullException.ThrowIfNull(facts);
        var definitions = new List<ApprovalLevelDefinition>(serviceLevels);
        var rules = await db.WorkflowRules.AsNoTracking()
            .Where(r => r.Active && r.Trigger == WorkflowTrigger.TicketCreated && (r.RecordType == null || r.RecordType == ticket.Type)
                && (r.CustomRecordTypeId == null || r.CustomRecordTypeId == ticket.RecordTypeId))
            .OrderBy(r => r.Position).ThenBy(r => r.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var rule in rules)
        {
            if (!RuleDocuments.Holds(RuleDocuments.ReadConditions(rule.Conditions), rule.Match, facts.Subject))
            {
                continue;
            }

            definitions.AddRange(RuleDocuments.ReadActions(rule.Actions).OfType<ApprovalAction>().Where(a => a.Stage == stage).SelectMany(a => a.Levels));
            if (rule.StopProcessing)
            {
                break;
            }
        }

        var planned = new List<PlannedLevel>();
        List<Guid>? fallback = null;
        foreach (var level in definitions.Where(l => RuleDocuments.Holds(l.Conditions, ConditionMatch.All, facts.Subject)))
        {
            var people = new List<Guid>();
            foreach (var approver in level.Approvers)
            {
                people.AddRange(await ApproversAsync(db, approver, facts, cancellationToken).ConfigureAwait(false));
            }

            // Task H4: a level naming a board is that board's.
            var board = level.Approvers.Any(a => a.Kind == ApproverKind.ChangeBoard) ? nameof(Data.Changes.ChangeBoardKind.Advisory)
                : level.Approvers.Any(a => a.Kind == ApproverKind.EmergencyBoard) ? nameof(Data.Changes.ChangeBoardKind.Emergency) : null;

            people = [.. people.Distinct().Where(p => p != ticket.RequesterId)];
            var usedFallback = false;
            if (people.Count == 0)
            {
                fallback ??= await FallbackAsync(db, ticket.RequesterId, cancellationToken).ConfigureAwait(false);
                people = fallback;
                usedFallback = true;
            }

            if (people.Count > 0)
            {
                planned.Add(new PlannedLevel(level.Name, level.Mode, people, usedFallback, board));
            }
        }

        return planned;
    }

    /// <summary>The planned levels as stored on the process.</summary>
    public static string Write(IReadOnlyList<PlannedLevel> levels) => JsonSerializer.Serialize(levels, Json);

    /// <summary>The planned levels of a process.</summary>
    public static IReadOnlyList<PlannedLevel> Read(string? json) =>
        string.IsNullOrWhiteSpace(json) ? [] : JsonSerializer.Deserialize<List<PlannedLevel>>(json, Json) ?? [];

    private static async Task<IReadOnlyList<Guid>> ApproversAsync(ZelvoDbContext db, ApproverRef approver, RuleFacts facts, CancellationToken cancellationToken)
    {
        IQueryable<UserAccount> active = db.Users.AsNoTracking().Where(u => u.Status == UserStatus.Active);
        switch (approver.Kind)
        {
            case ApproverKind.RequesterManager when facts.Requester?.ManagerId is { } manager:
                return await active.Where(u => u.Id == manager).Select(u => u.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
            case ApproverKind.User when approver.Id is { } user:
                return await active.Where(u => u.Id == user).Select(u => u.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
            case ApproverKind.Group or ApproverKind.GroupLeads when approver.Id is { } group:
                var leads = approver.Kind == ApproverKind.GroupLeads;
                return await db.SupportGroupMembers.AsNoTracking()
                    .Where(m => m.GroupId == group && (!leads || m.IsLead) && m.User!.Status == UserStatus.Active)
                    .Select(m => m.UserId).ToListAsync(cancellationToken).ConfigureAwait(false);
            case ApproverKind.Role when approver.Key is { } role:
                return await active.Where(u => u.Roles.Any(r => r.Role!.Key == role)).Select(u => u.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
            case ApproverKind.AnswerUser when approver.Key is { } key:
                var answer = facts.Subject.Answers[key];
                var picked = answer switch
                {
                    System.Text.Json.Nodes.JsonValue v when v.TryGetValue<string>(out var s) && Guid.TryParse(s, out var g) => [g],
                    System.Text.Json.Nodes.JsonArray a => a.Select(x => x is System.Text.Json.Nodes.JsonValue xv && xv.TryGetValue<string>(out var xs) && Guid.TryParse(xs, out var xg) ? xg : Guid.Empty)
                        .Where(x => x != Guid.Empty).ToList(),
                    _ => new List<Guid>(),
                };
                return await active.Where(u => picked.Contains(u.Id)).Select(u => u.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
            case ApproverKind.Permission when approver.Key is { } permission:
                return await active.Where(u => u.Roles.Any(r => db.RoleGrants.Any(g => g.RoleId == r.RoleId && g.Permission == permission)))
                    .Select(u => u.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
            case ApproverKind.ChangeBoard:
                var advisory = await db.ChangeBoards.AsNoTracking().Where(b => b.Kind == Data.Changes.ChangeBoardKind.Advisory)
                    .Select(b => new { b.ChairId, b.DeputyChairId }).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
                var chairs = new[] { advisory?.ChairId, advisory?.DeputyChairId }.OfType<Guid>().ToList();
                var found = await active.Where(u => chairs.Contains(u.Id)).Select(u => u.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
                return found.Count > 0 ? found : await ApproversAsync(db, new ApproverRef { Kind = ApproverKind.Permission, Key = ChangeManagerPermission }, facts, cancellationToken).ConfigureAwait(false);
            case ApproverKind.EmergencyBoard:
                var members = await db.ChangeBoardMembers.AsNoTracking()
                    .Where(m => db.ChangeBoards.Any(b => b.Id == m.BoardId && b.Kind == Data.Changes.ChangeBoardKind.Emergency) && db.Users.Any(u => u.Id == m.UserId && u.Status == UserStatus.Active))
                    .Select(m => m.UserId).ToListAsync(cancellationToken).ConfigureAwait(false);
                return members.Count > 0 ? members : await ApproversAsync(db, new ApproverRef { Kind = ApproverKind.Permission, Key = BoardMemberPermission }, facts, cancellationToken).ConfigureAwait(false);
            default:
                return [];
        }
    }

    private static Task<List<Guid>> FallbackAsync(ZelvoDbContext db, Guid requesterId, CancellationToken cancellationToken) =>
        db.Users.AsNoTracking()
            .Where(u => u.Status == UserStatus.Active && u.Id != requesterId
                && u.Roles.Any(r => db.RoleGrants.Any(g => g.RoleId == r.RoleId && g.Permission == FallbackPermission)))
            .Select(u => u.Id).ToListAsync(cancellationToken);
}
