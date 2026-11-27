using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Zelvo.Api.Conventions;
using Zelvo.Api.Forms;
using Zelvo.Api.Identity;
using Zelvo.Api.Incidents;
using Zelvo.Api.ServiceDesk;
using Zelvo.Api.Sla;
using Zelvo.Data;
using Zelvo.Data.Identity;
using Zelvo.Data.Records;
using Zelvo.Data.Tickets;
using Zelvo.Search;
using Zelvo.Tickets;
using Zelvo.Tickets.Sla;

namespace Zelvo.Api.Records;

/// <summary>Shared pieces of the record endpoints (task K2).</summary>
internal static class RecordCore
{
    /// <summary>A record type by key.</summary>
    public static Task<RecordType?> TypeAsync(ZelvoDbContext db, string key, CancellationToken cancellationToken) =>
        db.RecordTypes.AsNoTracking().SingleOrDefaultAsync(t => t.Key == key, cancellationToken);

    /// <summary>Records of a type with their details.</summary>
    public static IQueryable<Ticket> Records(ZelvoDbContext db, Guid typeId) =>
        db.Tickets.Include(t => t.Custom).Where(t => t.Type == TicketType.Custom && t.RecordTypeId == typeId);

    /// <summary>Active people whose roles may work on records of the type.</summary>
    public static IQueryable<UserAccount> Assignable(ZelvoDbContext db, RecordType type)
    {
        var permission = RecordPermissions.Edit(type.Key);
        return db.Users.Where(u => u.Status == UserStatus.Active && u.Roles.Any(r => r.Role!.Permissions.Any(p => p.Permission == permission)));
    }

    public static ProblemHttpResult TypeNotFound() => FormsCommon.NotFound("record-type");

    public static ProblemHttpResult NotFound() => FormsCommon.NotFound("record");

    public static ProblemHttpResult Forbidden() =>
        (ProblemHttpResult)ApiProblem.Create(StatusCodes.Status403Forbidden, "forbidden", "You may not do this with records of this type.");

    /// <summary>A record the caller may see: staff with the type's read permission, the requester or who logged it; anyone else gets null (404).</summary>
    public static async Task<Ticket?> FindVisibleAsync(ZelvoDbContext db, CurrentUser user, RecordType type, Guid id, CancellationToken cancellationToken, bool tracked = false)
    {
        var query = tracked ? Records(db, type.Id) : Records(db, type.Id).AsNoTracking();
        var ticket = await query.SingleOrDefaultAsync(t => t.Id == id, cancellationToken).ConfigureAwait(false);
        var me = user.Account?.Id;
        return ticket is not null && (RecordAccess.Of(user, type).Read || (me is not null && (ticket.RequesterId == me || ticket.CreatedById == me))) ? ticket : null;
    }

    /// <summary>A tracked record for a staff action that needs <paramref name="allowed"/>, with If-Match checked; the problem otherwise.</summary>
    public static async Task<(RecordType? Type, Ticket? Ticket, IResult? Problem)> ForStaffAsync(
        HttpContext http, ZelvoDbContext db, CurrentUser user, string key, Guid id, Func<RecordAccess, bool> allowed, CancellationToken cancellationToken)
    {
        var type = await TypeAsync(db, key, cancellationToken).ConfigureAwait(false);
        if (type is null)
        {
            return (null, null, TypeNotFound());
        }

        var ticket = await FindVisibleAsync(db, user, type, id, cancellationToken, tracked: true).ConfigureAwait(false);
        if (ticket is null)
        {
            return (type, null, NotFound());
        }

        if (!allowed(RecordAccess.Of(user, type)))
        {
            return (type, null, Forbidden());
        }

        return IncidentEndpoints.VersionProblem(http, db, ticket) is { } versionProblem ? (type, null, versionProblem) : (type, ticket, null);
    }

    /// <summary>
    /// States the caller can move the record to. Staff: other states of the same category while it
    /// is open, and states of a category the ticket core allows from its state. The requester:
    /// back to work from Resolved (reopen). Nothing while it waits for approval.
    /// </summary>
    public static IReadOnlyList<RecordState> NextStates(Ticket ticket, IReadOnlyList<RecordState> states, bool staff, Guid? me)
    {
        if (ticket.AwaitingApproval || ticket.State == TicketState.Closed)
        {
            return [];
        }

        var current = RecordStates.CategoryOf(ticket.State);
        var key = ticket.Custom?.StateKey;
        if (!staff)
        {
            return ticket.State == TicketState.Resolved && ticket.RequesterId == me
                ? [.. states.Where(s => s.Category == RecordStateCategory.InProgress).Take(1)]
                : [];
        }

        var allowed = TicketWorkflow.NextStates(ticket.State);
        return [.. states.Where(s => s.Key != key && (s.Category == current
            ? TicketWorkflow.IsOpen(ticket.State)
            : s.Category != RecordStateCategory.New && allowed.Contains(RecordStates.TargetOf(s.Category, ticket.State))))];
    }

    /// <summary>Saves a changed record with its SLA clocks and search document; 412 when it changed meanwhile.</summary>
    public static async Task<IResult> SaveAsync(
        HttpContext http, ZelvoDbContext db, CurrentUser user, ISearchIndexer indexer, RecordType type, Ticket ticket, CancellationToken cancellationToken)
    {
        var own = db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false) : null;
        try
        {
            await http.RequestServices.GetRequiredService<SlaTracker>().UpdateAsync([ticket], DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);
            await RecordSearch.IndexAsync(db, indexer, [ticket], cancellationToken).ConfigureAwait(false);
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

        return TypedResults.Ok(await ResponseAsync(http, db, user, type, ticket.Id, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>The response for <paramref name="id"/> as the caller sees it, with the ETag header.</summary>
    public static async Task<RecordResponse> ResponseAsync(HttpContext http, ZelvoDbContext db, CurrentUser user, RecordType type, Guid id, CancellationToken cancellationToken)
    {
        var ticket = await Records(db, type.Id).AsNoTracking().SingleAsync(t => t.Id == id, cancellationToken).ConfigureAwait(false);
        var context = await RecordResponseContext.LoadAsync(db, user, type, [ticket], cancellationToken).ConfigureAwait(false);
        http.Response.Headers.ETag = $"\"{ticket.Version}\"";
        return context.From(ticket);
    }
}

/// <summary>A state as a record shows it.</summary>
internal sealed record RecordStateRef(string Key, LocalizedTextDto Name, RecordStateCategory Category);

/// <summary>
/// A custom record. Requesters see the team (group) that has it, never the assignee (decision
/// 2026-11-24), and no SLA clocks.
/// </summary>
internal sealed record RecordResponse(
    Guid Id, string Number, string Type, LocalizedTextDto TypeName, string Title, string? Description, TicketState State, RecordStateRef StateOfType,
    int Priority, TicketChannel Channel, PersonResponse? Requester, PersonResponse? LoggedBy, PersonResponse? Assignee, NamedRef? Group, NodeRef? Category,
    bool AwaitingApproval, string? OnHoldReason, string? ResolutionNotes, IReadOnlyList<RecordStateRef> NextStates, IReadOnlyList<SlaClockResponse> Sla,
    string Etag, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, DateTimeOffset? ResolvedAt, DateTimeOffset? ClosedAt);

/// <summary>What responses need, loaded once for a page of records.</summary>
internal sealed class RecordResponseContext
{
    private Dictionary<Guid, string?> _names = [];
    private DirectoryRefs _refs = null!;
    private TicketSlaView _slas = null!;
    private CurrentUser _user = null!;
    private RecordType _type = null!;
    private IReadOnlyList<RecordState> _states = [];
    private RecordAccess _access;

    public static async Task<RecordResponseContext> LoadAsync(ZelvoDbContext db, CurrentUser user, RecordType type, IReadOnlyList<Ticket> tickets, CancellationToken cancellationToken)
    {
        var access = RecordAccess.Of(user, type);
        return new RecordResponseContext
        {
            _names = await IncidentEndpoints.Names(db, tickets.SelectMany(t => new[] { t.RequesterId, t.AssigneeId ?? Guid.Empty, t.CreatedById ?? Guid.Empty }), cancellationToken).ConfigureAwait(false),
            _refs = await DirectoryRefs.LoadAsync(db, cancellationToken).ConfigureAwait(false),
            _slas = await TicketSlaView.LoadAsync(db, access.Read ? tickets.Select(t => t.Id).ToList() : [], cancellationToken).ConfigureAwait(false),
            _user = user,
            _type = type,
            _states = RecordStates.Read(type.States),
            _access = access,
        };
    }

    public RecordResponse From(Ticket t)
    {
        var record = t.Custom ?? new CustomRecord { TicketId = t.Id, RecordTypeId = _type.Id, StateKey = RecordStates.Follow(_states, "", t.State) };
        var staff = _access.Read;
        RecordStateRef Ref(RecordState s) => new(s.Key, LocalizedTextDto.From(s.Name.ToText()), s.Category);
        var state = _states.FirstOrDefault(s => s.Key == record.StateKey) is { } known
            ? Ref(known)
            : new RecordStateRef(record.StateKey, LocalizedTextDto.From(new Data.Localization.LocalizedText(record.StateKey, record.StateKey)), RecordStates.CategoryOf(t.State));
        return new RecordResponse(
            t.Id, t.Number, _type.Key, LocalizedTextDto.From(_type.Name), t.Title, t.Description, t.State, state, record.Priority, t.Channel,
            new PersonResponse(t.RequesterId, _names.GetValueOrDefault(t.RequesterId)),
            t.CreatedById is { } by && by != t.RequesterId && staff ? new PersonResponse(by, _names.GetValueOrDefault(by)) : null,
            staff && t.AssigneeId is { } a ? new PersonResponse(a, _names.GetValueOrDefault(a)) : null,
            _refs.Group(t.GroupId), _refs.Categories.RefOf(t.CategoryId), t.AwaitingApproval, t.OnHoldReason, t.ResolutionNotes,
            [.. RecordCore.NextStates(t, _states, _access.Edit, _user.Account?.Id).Select(Ref)],
            staff ? _slas.For(t.Id, DateTimeOffset.UtcNow) : [],
            $"\"{t.Version}\"", t.CreatedAt, t.UpdatedAt, t.ResolvedAt, t.ClosedAt);
    }
}
