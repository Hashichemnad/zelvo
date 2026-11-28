using System.Text.Json;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Zelvo.Api.Catalog;
using Zelvo.Api.Conventions;
using Zelvo.Api.Forms;
using Zelvo.Api.Identity;
using Zelvo.Api.Incidents;
using Zelvo.Api.ServiceDesk;
using Zelvo.Api.Settings;
using Zelvo.Data;
using Zelvo.Data.Catalog;
using Zelvo.Data.Changes;
using Zelvo.Data.Identity;
using Zelvo.Data.Tenancy;
using Zelvo.Data.Tickets;
using Zelvo.Data.Workflows;
using Zelvo.Search;
using Zelvo.Tickets;

namespace Zelvo.Api.Changes;

/// <summary>What a person can do with a change next (task H3).</summary>
public enum ChangeAction
{
    /// <summary>Submit it to its approval path (New; changes.edit).</summary>
    Submit,

    /// <summary>Take it back to planning, stopping its approval (Authorisation or Scheduled; changes.edit).</summary>
    Replan,

    /// <summary>Start implementing it (Scheduled; changes.edit; needs an assignee).</summary>
    StartImplementation,

    /// <summary>Mark it implemented, with notes (Implementation; changes.edit; its tasks done).</summary>
    Implemented,

    /// <summary>Record the post-implementation review and close it (Review; changes.manage).</summary>
    Review,

    /// <summary>Withdraw it before implementation (New, Authorisation or Scheduled; changes.edit; a reason).</summary>
    Cancel,
}

/// <summary>What a new change is made of.</summary>
internal sealed record ChangeDraft(
    string Title, string? Description, ChangeKind Kind, Guid CreatedById, Guid? CategoryId, ChangeTemplate? Template,
    DateTimeOffset? PlannedStart, DateTimeOffset? PlannedEnd, string? ImplementationPlan, string? TestPlan, string? BackoutPlan);

/// <summary>Shared pieces of the change endpoints (module 17, task H3).</summary>
internal static class ChangeCore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };

    /// <summary>Changes with their details.</summary>
    public static IQueryable<Ticket> Changes(ZelvoDbContext db) =>
        db.Tickets.Include(t => t.Change).Where(t => t.Type == TicketType.Change);

    /// <summary>Active people whose roles may work on changes.</summary>
    public static IQueryable<UserAccount> Assignable(ZelvoDbContext db) =>
        db.Users.Where(u => u.Status == UserStatus.Active
            && u.Roles.Any(r => r.Role!.Permissions.Any(p => p.Permission == Permissions.ChangesEdit)));

    public static ProblemHttpResult NotFound() => FormsCommon.NotFound("change");

    /// <summary>The stage of a change.</summary>
    public static ChangeStage StageOf(Ticket ticket) => ChangeStages.Of(ticket.State, ticket.Change?.Phase ?? ChangePhase.Planning);

    /// <summary>Whether the plans, window, risk and tasks can still be changed (while it is planned).</summary>
    public static bool IsPlanning(Ticket ticket) => StageOf(ticket) == ChangeStage.New;

    /// <summary>What the caller can do next.</summary>
    public static IReadOnlyList<ChangeAction> NextActions(Ticket ticket, bool edit, bool manage) => StageOf(ticket) switch
    {
        ChangeStage.New when edit => [ChangeAction.Submit, ChangeAction.Cancel],
        ChangeStage.Authorisation when edit => [ChangeAction.Replan, ChangeAction.Cancel],
        ChangeStage.Scheduled when edit => [ChangeAction.StartImplementation, ChangeAction.Replan, ChangeAction.Cancel],
        ChangeStage.Implementation when edit => [ChangeAction.Implemented],
        ChangeStage.Review when manage => [ChangeAction.Review],
        _ => [],
    };

    /// <summary>The If-Match header against the ticket's version, when sent.</summary>
    public static ProblemHttpResult? VersionProblem(HttpContext http, ZelvoDbContext db, Ticket ticket) => IncidentEndpoints.VersionProblem(http, db, ticket);

    /// <summary>The questionnaire's answers of a change.</summary>
    public static Dictionary<string, string> ReadAnswers(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json, Json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>The answers as stored.</summary>
    public static string WriteAnswers(IReadOnlyDictionary<string, string> answers) => JsonSerializer.Serialize(answers, Json);

    /// <summary>The task templates of a standard change template.</summary>
    public static List<FulfilmentTask> ReadTasks(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<FulfilmentTask>>(json, Json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>The task templates as stored.</summary>
    public static string WriteTasks(IReadOnlyList<FulfilmentTask> tasks) => JsonSerializer.Serialize(tasks, Json);

    /// <summary>
    /// Stages a new change inside the caller's transaction: its number, details, the standard
    /// template's tasks (waiting until implementation starts), routing (the template's group, else
    /// the assignment rules, B2) and the search document.
    /// </summary>
    public static async Task<Ticket> CreateAsync(
        ZelvoDbContext db, TenantSettingsStore settings, ITenantContext tenant, ISearchIndexer indexer, ChangeDraft draft, CancellationToken cancellationToken)
    {
        var prefixes = await settings.GetAsync(IncidentSettings.NumberPrefixes, cancellationToken).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        var sequence = await TicketNumbers.NextAsync(db, tenant.TenantId!.Value, TicketType.Change, cancellationToken).ConfigureAwait(false);
        var title = draft.Title.Trim();
        var template = draft.Template;
        var ticket = new Ticket
        {
            Type = TicketType.Change,
            Number = TicketNumbers.Format(TicketNumbers.PrefixOf(prefixes, TicketType.Change), sequence),
            Sequence = sequence,
            Title = title[..Math.Min(title.Length, Ticket.TitleMaxLength)],
            Description = Clean(draft.Description),
            Channel = TicketChannel.Portal,
            // Changes have no customer: the person who raised it is the change's requester.
            RequesterId = draft.CreatedById,
            CreatedById = draft.CreatedById,
            CategoryId = draft.CategoryId ?? template?.CategoryId,
            CreatedAt = now,
            UpdatedAt = now,
        };
        ticket.Change = new ChangeRecord
        {
            TicketId = ticket.Id,
            Kind = draft.Kind,
            TemplateId = template?.Id,
            ImplementationPlan = Clean(draft.ImplementationPlan) ?? template?.ImplementationPlan,
            TestPlan = Clean(draft.TestPlan) ?? template?.TestPlan,
            BackoutPlan = Clean(draft.BackoutPlan) ?? template?.BackoutPlan,
            PlannedStart = draft.PlannedStart,
            PlannedEnd = draft.PlannedEnd,
            // Standard changes are pre-approved and low risk by definition.
            RiskScore = draft.Kind == ChangeKind.Standard ? 0 : null,
            RiskLevel = draft.Kind == ChangeKind.Standard ? ChangeRiskLevel.Low : null,
        };

        if (template?.GroupId is { } group && await db.SupportGroups.AnyAsync(g => g.Id == group && g.Active, cancellationToken).ConfigureAwait(false))
        {
            ticket.GroupId = group;
        }
        else
        {
            await IncidentEndpoints.RouteAsync(db, settings, ticket, now, cancellationToken).ConfigureAwait(false);
        }

        db.Tickets.Add(ticket);
        if (template is not null)
        {
            await RequestTasks.AddWaitingAsync(db, ticket, ReadTasks(template.Tasks), now, cancellationToken).ConfigureAwait(false);
        }

        indexer.Upsert(ChangeSearch.Document(ticket, await ChangeSearch.StaffRolesAsync(db, cancellationToken).ConfigureAwait(false)));
        return ticket;
    }

    /// <summary>Saves a changed change with its search document; 412 when it changed meanwhile.</summary>
    public static async Task<IResult> SaveAsync(HttpContext http, ZelvoDbContext db, CurrentUser user, ISearchIndexer indexer, Ticket ticket, CancellationToken cancellationToken)
    {
        var own = db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false) : null;
        try
        {
            indexer.Upsert(ChangeSearch.Document(ticket, await ChangeSearch.StaffRolesAsync(db, cancellationToken).ConfigureAwait(false)));
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await (own ?? db.Database.CurrentTransaction!).CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            return FormsCommon.VersionMismatch();
        }
        finally
        {
            if (own is not null)
            {
                await own.DisposeAsync().ConfigureAwait(false);
            }
        }

        return TypedResults.Ok(await ResponseAsync(http, db, user, ticket.Id, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>The response for <paramref name="id"/> with the ETag header, its conflicts included.</summary>
    public static async Task<ChangeResponse> ResponseAsync(HttpContext http, ZelvoDbContext db, CurrentUser user, Guid id, CancellationToken cancellationToken)
    {
        var ticket = await Changes(db).AsNoTracking().SingleAsync(t => t.Id == id, cancellationToken).ConfigureAwait(false);
        var context = await ChangeResponseContext.LoadAsync(db, user, [ticket], cancellationToken).ConfigureAwait(false);
        http.Response.Headers.ETag = $"\"{ticket.Version}\"";
        var conflicts = await ChangeConflicts.ForAsync(db, ticket, cancellationToken).ConfigureAwait(false);
        return context.From(ticket) with { Conflicts = conflicts };
    }

    /// <summary>Text trimmed, or <see langword="null"/> when blank.</summary>
    public static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>Where a change's approval is.</summary>
/// <param name="Board">Task H4: <c>Advisory</c> while the change waits for the change advisory board, <c>Emergency</c> for the emergency board.</param>
/// <param name="DecideBy">Task H4: when the emergency board is expected to have decided.</param>
internal sealed record ChangeApprovalSummary(
    Guid ProcessId, ApprovalProcessState State, int CurrentLevel, int Levels, LocalizedTextDto? LevelName, string? Reason, DateTimeOffset CreatedAt, DateTimeOffset? EndedAt,
    string? Board = null, DateTimeOffset? DecideBy = null);

/// <summary>A change (module 17).</summary>
internal sealed record ChangeResponse(
    Guid Id, string Number, string Title, string? Description, ChangeKind Kind, ChangeStage Stage, TicketState State, ChangePhase Phase, ChangeClosure? Closure,
    NamedRef? Template, IReadOnlyDictionary<string, string> RiskAnswers, int? RiskScore, ChangeRiskLevel? RiskLevel,
    string? ImplementationPlan, string? TestPlan, string? BackoutPlan,
    DateTimeOffset? PlannedStart, DateTimeOffset? PlannedEnd, DateTimeOffset? ActualStart, DateTimeOffset? ActualEnd,
    ChangeOutcome? Outcome, string? ReviewNotes, PersonResponse? ReviewedBy, DateTimeOffset? ReviewedAt,
    NodeRef? Category, NamedRef? Group, PersonResponse? Assignee, PersonResponse? CreatedBy, string? ResolutionNotes,
    ChangeApprovalSummary? Approval, int TaskCount, int OpenTaskCount, int ItemCount, IReadOnlyList<ChangeAction> NextActions, string Etag,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, DateTimeOffset? SubmittedAt, DateTimeOffset? ApprovedAt, DateTimeOffset? ResolvedAt, DateTimeOffset? ClosedAt)
{
    /// <summary>Other changes on the same items in the same window, and blackout periods (detail only).</summary>
    public IReadOnlyList<ChangeConflictResponse>? Conflicts { get; init; }
}

/// <summary>What responses need, loaded once for a page of changes.</summary>
internal sealed class ChangeResponseContext
{
    private Dictionary<Guid, string?> _names = [];
    private Dictionary<Guid, Data.Localization.LocalizedText> _templates = [];
    private Dictionary<Guid, (int All, int Open)> _tasks = [];
    private Dictionary<Guid, int> _items = [];
    private Dictionary<Guid, ApprovalProcess> _approvals = [];
    private DirectoryRefs _refs = null!;
    private bool _edit;
    private bool _manage;
    private int _emergencyHours = 4;

    public static async Task<ChangeResponseContext> LoadAsync(ZelvoDbContext db, CurrentUser user, IReadOnlyList<Ticket> tickets, CancellationToken cancellationToken)
    {
        var ids = tickets.Select(t => t.Id).ToList();
        var templateIds = tickets.Select(t => t.Change?.TemplateId).OfType<Guid>().Distinct().ToList();
        var approvals = await db.ApprovalProcesses.AsNoTracking().Where(p => ids.Contains(p.TicketId) && p.Stage == ApprovalStage.ChangeAuthorisation)
            .OrderByDescending(p => p.CreatedAt).ToListAsync(cancellationToken).ConfigureAwait(false);
        return new ChangeResponseContext
        {
            _names = await IncidentEndpoints.Names(db, tickets.SelectMany(t => new[] { t.AssigneeId ?? Guid.Empty, t.CreatedById ?? Guid.Empty, t.Change?.ReviewedById ?? Guid.Empty }), cancellationToken).ConfigureAwait(false),
            _templates = await db.ChangeTemplates.AsNoTracking().Where(x => templateIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.Name, cancellationToken).ConfigureAwait(false),
            _tasks = await db.RequestTasks.AsNoTracking().Where(x => ids.Contains(x.TicketId)).GroupBy(x => x.TicketId)
                .Select(g => new { g.Key, All = g.Count(x => x.State != RequestTaskState.Cancelled), Open = g.Count(x => x.State == RequestTaskState.Waiting || x.State == RequestTaskState.Open || x.State == RequestTaskState.InProgress) })
                .ToDictionaryAsync(g => g.Key, g => (g.All, g.Open), cancellationToken).ConfigureAwait(false),
            _items = await db.TicketConfigurationItems.AsNoTracking().Where(x => ids.Contains(x.TicketId)).GroupBy(x => x.TicketId)
                .Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(g => g.Key, g => g.Count, cancellationToken).ConfigureAwait(false),
            _approvals = approvals.GroupBy(p => p.TicketId).ToDictionary(g => g.Key, g => g.First()),
            _refs = await DirectoryRefs.LoadAsync(db, cancellationToken).ConfigureAwait(false),
            _edit = user.Has(Permissions.ChangesEdit),
            _manage = user.Has(Permissions.ChangesManage),
            _emergencyHours = await db.ChangeBoards.AsNoTracking().Where(b => b.Kind == ChangeBoardKind.Emergency).Select(b => (int?)b.DecideWithinHours)
                .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false) ?? 4,
        };
    }

    public ChangeResponse From(Ticket t)
    {
        var c = t.Change ?? new ChangeRecord { TicketId = t.Id };
        var (all, open) = _tasks.GetValueOrDefault(t.Id);
        PersonResponse? Person(Guid? id) => id is { } p ? new PersonResponse(p, _names.GetValueOrDefault(p)) : null;
        ChangeApprovalSummary? approval = null;
        if (_approvals.TryGetValue(t.Id, out var process))
        {
            var levels = Zelvo.Workflows.Approvals.ApprovalPlanner.Read(process.Levels);
            var current = process.CurrentLevel < levels.Count ? levels[process.CurrentLevel] : null;
            var board = process.State == ApprovalProcessState.Pending ? current?.Board : null;
            approval = new(process.Id, process.State, process.CurrentLevel, levels.Count, current is null ? null : LocalizedTextDto.From(current.Name), process.Reason,
                process.CreatedAt, process.EndedAt, board,
                board == nameof(ChangeBoardKind.Emergency) ? process.CreatedAt.AddHours(_emergencyHours) : null);
        }

        return new ChangeResponse(
            t.Id, t.Number, t.Title, t.Description, c.Kind, ChangeCore.StageOf(t), t.State, c.Phase, c.Closure,
            c.TemplateId is { } tid && _templates.TryGetValue(tid, out var name) ? NamedRef.From(tid, name) : null,
            ChangeCore.ReadAnswers(c.RiskAnswers), c.RiskScore, c.RiskLevel,
            c.ImplementationPlan, c.TestPlan, c.BackoutPlan, c.PlannedStart, c.PlannedEnd, c.ActualStart, c.ActualEnd,
            c.Outcome, c.ReviewNotes, Person(c.ReviewedById), c.ReviewedAt,
            _refs.Categories.RefOf(t.CategoryId), _refs.Group(t.GroupId), Person(t.AssigneeId), Person(t.CreatedById), t.ResolutionNotes,
            approval, all, open, _items.GetValueOrDefault(t.Id),
            ChangeCore.NextActions(t, _edit, _manage), $"\"{t.Version}\"",
            t.CreatedAt, t.UpdatedAt, c.SubmittedAt, c.ApprovedAt, t.ResolvedAt, t.ClosedAt);
    }
}
