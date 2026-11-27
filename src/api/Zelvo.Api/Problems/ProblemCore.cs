using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Zelvo.Api.Conventions;
using Zelvo.Api.Forms;
using Zelvo.Api.Identity;
using Zelvo.Api.Incidents;
using Zelvo.Api.ServiceDesk;
using Zelvo.Api.Settings;
using Zelvo.Api.Sla;
using Zelvo.Data;
using Zelvo.Data.Identity;
using Zelvo.Data.Problems;
using Zelvo.Data.Tenancy;
using Zelvo.Data.Tickets;
using Zelvo.Search;
using Zelvo.Tickets;
using Zelvo.Tickets.Sla;

namespace Zelvo.Api.Problems;

/// <summary>Where a problem can be moved by a person (task H1): the stages of module 16, plus on hold and reopened of the ticket core.</summary>
public enum ProblemTarget
{
    /// <summary>Start or go back to assessing it.</summary>
    Assessment,

    /// <summary>Analyse the root cause.</summary>
    RootCauseAnalysis,

    /// <summary>Cause known, workaround recorded (problems.manage; needs an active known error).</summary>
    KnownError,

    /// <summary>Wait, with an on-hold reason; clocks pause when the reason says so.</summary>
    OnHold,

    /// <summary>Fixed for good (problems.manage; resolution notes; optionally resolves its open incidents).</summary>
    Resolved,

    /// <summary>Final (problems.manage; from Resolved).</summary>
    Closed,

    /// <summary>Not fixed after all (problems.manage; from Resolved).</summary>
    Reopened,
}

/// <summary>What a new problem is made of.</summary>
internal sealed record ProblemDraft(string Title, string? Description, Guid CreatedById, Guid? CategoryId, int Priority, IReadOnlyList<Guid> IncidentIds);

/// <summary>Shared pieces of the problem endpoints (module 16, task H1).</summary>
internal static class ProblemCore
{
    /// <summary>Most incidents linked in one call.</summary>
    public const int MaxIncidentsPerCall = 100;

    /// <summary>Problems with their details.</summary>
    public static IQueryable<Ticket> Problems(ZelvoDbContext db) =>
        db.Tickets.Include(t => t.Problem).Where(t => t.Type == TicketType.Problem);

    /// <summary>Active people whose roles may work on problems.</summary>
    public static IQueryable<UserAccount> Assignable(ZelvoDbContext db) =>
        db.Users.Where(u => u.Status == UserStatus.Active
            && u.Roles.Any(r => r.Role!.Permissions.Any(p => p.Permission == Permissions.ProblemsEdit)));

    public static ProblemHttpResult NotFound() => FormsCommon.NotFound("problem");

    /// <summary>The stage of a problem.</summary>
    public static ProblemStage StageOf(Ticket ticket) => ProblemStages.Of(ticket.State, ticket.Problem?.Phase ?? ProblemPhase.Assessment);

    /// <summary>Where the caller can move the problem: problems.edit moves through the investigation, problems.manage records the known error, resolves, closes and reopens.</summary>
    public static IReadOnlyList<ProblemTarget> NextTargets(Ticket ticket, bool edit, bool manage, bool hasKnownError)
    {
        if (!edit && !manage)
        {
            return [];
        }

        var stage = StageOf(ticket);
        var targets = new List<ProblemTarget>();
        switch (ticket.State)
        {
            case TicketState.Closed:
                return [];
            case TicketState.Resolved:
                if (manage)
                {
                    targets.AddRange([ProblemTarget.Closed, ProblemTarget.Reopened]);
                }

                return targets;
        }

        if (ticket.State == TicketState.OnHold || stage != ProblemStage.Assessment)
        {
            targets.Add(ProblemTarget.Assessment);
        }

        if (ticket.State == TicketState.OnHold || stage != ProblemStage.RootCauseAnalysis)
        {
            targets.Add(ProblemTarget.RootCauseAnalysis);
        }

        if (manage && hasKnownError && (ticket.State == TicketState.OnHold || stage != ProblemStage.KnownError))
        {
            targets.Add(ProblemTarget.KnownError);
        }

        if (ticket.State != TicketState.OnHold)
        {
            targets.Add(ProblemTarget.OnHold);
        }

        if (manage)
        {
            targets.Add(ProblemTarget.Resolved);
        }

        return targets;
    }

    /// <summary>The If-Match header against the ticket's version, when sent.</summary>
    public static ProblemHttpResult? VersionProblem(HttpContext http, ZelvoDbContext db, Ticket ticket) => IncidentEndpoints.VersionProblem(http, db, ticket);

    /// <summary>
    /// Stages a new problem inside the caller's transaction: its number, details, the incident
    /// links, routing by the assignment rules (B2), the search document and the SLA clocks (B3).
    /// </summary>
    public static async Task<Ticket> CreateAsync(
        HttpContext http, ZelvoDbContext db, TenantSettingsStore settings, ITenantContext tenant, ISearchIndexer indexer, ProblemDraft draft,
        CancellationToken cancellationToken)
    {
        var prefixes = await settings.GetAsync(IncidentSettings.NumberPrefixes, cancellationToken).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        var sequence = await TicketNumbers.NextAsync(db, tenant.TenantId!.Value, TicketType.Problem, cancellationToken).ConfigureAwait(false);
        var title = draft.Title.Trim();
        var ticket = new Ticket
        {
            Type = TicketType.Problem,
            Number = TicketNumbers.Format(TicketNumbers.PrefixOf(prefixes, TicketType.Problem), sequence),
            Sequence = sequence,
            Title = title[..Math.Min(title.Length, Ticket.TitleMaxLength)],
            Description = string.IsNullOrWhiteSpace(draft.Description) ? null : draft.Description.Trim(),
            Channel = TicketChannel.Portal,
            // Problems have no customer: the person who raised it stands in as requester.
            RequesterId = draft.CreatedById,
            CreatedById = draft.CreatedById,
            CategoryId = draft.CategoryId,
            CreatedAt = now,
            UpdatedAt = now,
        };
        ticket.Problem = new ProblemRecord { TicketId = ticket.Id, Priority = draft.Priority };
        foreach (var incidentId in draft.IncidentIds.Distinct())
        {
            db.ProblemIncidents.Add(new ProblemIncident { ProblemId = ticket.Id, IncidentId = incidentId, LinkedById = draft.CreatedById, LinkedAt = now });
        }

        await IncidentEndpoints.RouteAsync(db, settings, ticket, now, cancellationToken).ConfigureAwait(false);
        db.Tickets.Add(ticket);
        indexer.Upsert(ProblemSearch.Document(ticket, await ProblemSearch.StaffRolesAsync(db, cancellationToken).ConfigureAwait(false)));
        await http.RequestServices.GetRequiredService<SlaTracker>().UpdateAsync([ticket], now, cancellationToken).ConfigureAwait(false);
        return ticket;
    }

    /// <summary>Saves a changed problem (and other changed tickets) with SLA clocks and search documents; 412 when it changed meanwhile.</summary>
    public static async Task<IResult> SaveAsync(
        HttpContext http, ZelvoDbContext db, CurrentUser user, ISearchIndexer indexer, Ticket ticket, CancellationToken cancellationToken, IReadOnlyList<Ticket>? incidents = null)
    {
        var own = db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false) : null;
        try
        {
            var changed = new List<Ticket> { ticket };
            changed.AddRange(incidents ?? []);
            await http.RequestServices.GetRequiredService<SlaTracker>().UpdateAsync(changed, DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);
            indexer.Upsert(ProblemSearch.Document(ticket, await ProblemSearch.StaffRolesAsync(db, cancellationToken).ConfigureAwait(false)));
            if (incidents is { Count: > 0 })
            {
                await IncidentEndpoints.IndexAsync(db, indexer, incidents, cancellationToken).ConfigureAwait(false);
            }

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

    /// <summary>The response for <paramref name="id"/> with the ETag header.</summary>
    public static async Task<ProblemResponse> ResponseAsync(HttpContext http, ZelvoDbContext db, CurrentUser user, Guid id, CancellationToken cancellationToken)
    {
        var ticket = await Problems(db).AsNoTracking().SingleAsync(t => t.Id == id, cancellationToken).ConfigureAwait(false);
        var context = await ProblemResponseContext.LoadAsync(db, user, [ticket], cancellationToken, detail: true).ConfigureAwait(false);
        http.Response.Headers.ETag = $"\"{ticket.Version}\"";
        return context.From(ticket);
    }

    /// <summary>The incidents of <paramref name="incidentIds"/> that exist, or the problem that some do not.</summary>
    public static async Task<(List<Guid> Ids, IResult? Problem)> CheckIncidentsAsync(ZelvoDbContext db, IReadOnlyList<Guid>? incidentIds, CancellationToken cancellationToken)
    {
        var wanted = (incidentIds ?? []).Distinct().ToList();
        if (wanted.Count > MaxIncidentsPerCall)
        {
            return ([], UserEndpoints.Validation("incidentIds", $"Link at most {MaxIncidentsPerCall} incidents at a time."));
        }

        var found = await db.Tickets.AsNoTracking().Where(t => wanted.Contains(t.Id) && t.Type == TicketType.Incident).Select(t => t.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
        return found.Count == wanted.Count ? (wanted, null) : ([], UserEndpoints.Validation("incidentIds", "Every incident must exist."));
    }
}

/// <summary>A linked incident as a problem shows it.</summary>
internal sealed record ProblemIncidentResponse(
    Guid Id, string Number, string Title, TicketState State, int? Priority, NodeRef? Category, PersonResponse? Assignee, DateTimeOffset CreatedAt,
    DateTimeOffset? ResolvedAt, DateTimeOffset LinkedAt, PersonResponse? LinkedBy);

/// <summary>A known error.</summary>
internal sealed record KnownErrorResponse(
    Guid Id, string Number, Guid ProblemId, string ProblemNumber, LocalizedTextDto Title, LocalizedTextDto Symptoms, LocalizedTextDto Workaround,
    KnownErrorStatus Status, Guid? ArticleId, string? ArticleNumber, Data.Knowledge.ArticleState? ArticleState, string Etag, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

/// <summary>A problem (module 16).</summary>
internal sealed record ProblemResponse(
    Guid Id, string Number, string Title, string? Description, ProblemStage Stage, TicketState State, ProblemPhase Phase, int Priority,
    NodeRef? Category, NamedRef? Group, PersonResponse? Assignee, PersonResponse? CreatedBy, string? RootCause,
    FiveWhys? FiveWhys, Fishbone? Fishbone, KnownErrorResponse? KnownError, int IncidentCount, int OpenIncidentCount,
    string? OnHoldReason, string? ResolutionNotes, IReadOnlyList<ProblemTarget> NextTargets, IReadOnlyList<SlaClockResponse> Sla, string Etag,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, DateTimeOffset? ResolvedAt, DateTimeOffset? ClosedAt);

/// <summary>What responses need, loaded once for a page of problems.</summary>
internal sealed class ProblemResponseContext
{
    private Dictionary<Guid, string?> _names = [];
    private Dictionary<Guid, KnownErrorResponse> _errors = [];
    private Dictionary<Guid, (int All, int Open)> _counts = [];
    private DirectoryRefs _refs = null!;
    private TicketSlaView _slas = null!;
    private bool _edit;
    private bool _manage;
    private bool _detail;

    public static async Task<ProblemResponseContext> LoadAsync(ZelvoDbContext db, CurrentUser user, IReadOnlyList<Ticket> tickets, CancellationToken cancellationToken, bool detail = false)
    {
        var ids = tickets.Select(t => t.Id).ToList();
        var errors = await KnownErrorResponses.LoadAsync(db, db.KnownErrors.AsNoTracking().Where(e => ids.Contains(e.ProblemId)), cancellationToken).ConfigureAwait(false);
        var counts = await db.ProblemIncidents.AsNoTracking().Where(l => ids.Contains(l.ProblemId))
            .Join(db.Tickets.AsNoTracking(), l => l.IncidentId, t => t.Id, (l, t) => new { l.ProblemId, t.State })
            .GroupBy(x => x.ProblemId)
            .Select(g => new { g.Key, All = g.Count(), Open = g.Count(x => x.State != TicketState.Resolved && x.State != TicketState.Closed) })
            .ToDictionaryAsync(g => g.Key, g => (g.All, g.Open), cancellationToken).ConfigureAwait(false);
        return new ProblemResponseContext
        {
            _names = await IncidentEndpoints.Names(db, tickets.SelectMany(t => new[] { t.AssigneeId ?? Guid.Empty, t.CreatedById ?? Guid.Empty }), cancellationToken).ConfigureAwait(false),
            _errors = errors.ToDictionary(e => e.ProblemId),
            _counts = counts,
            _refs = await DirectoryRefs.LoadAsync(db, cancellationToken).ConfigureAwait(false),
            _slas = await TicketSlaView.LoadAsync(db, ids, cancellationToken).ConfigureAwait(false),
            _edit = user.Has(Permissions.ProblemsEdit),
            _manage = user.Has(Permissions.ProblemsManage),
            _detail = detail,
        };
    }

    public ProblemResponse From(Ticket t)
    {
        var problem = t.Problem ?? new ProblemRecord { TicketId = t.Id };
        var error = _errors.GetValueOrDefault(t.Id);
        var (all, open) = _counts.GetValueOrDefault(t.Id);
        return new ProblemResponse(
            t.Id, t.Number, t.Title, t.Description, ProblemCore.StageOf(t), t.State, problem.Phase, problem.Priority,
            _refs.Categories.RefOf(t.CategoryId), _refs.Group(t.GroupId),
            t.AssigneeId is { } a ? new PersonResponse(a, _names.GetValueOrDefault(a)) : null,
            t.CreatedById is { } by ? new PersonResponse(by, _names.GetValueOrDefault(by)) : null,
            problem.RootCause,
            _detail ? ProblemAnalysis.ReadFiveWhys(problem.FiveWhys) : null,
            _detail ? ProblemAnalysis.ReadFishbone(problem.Fishbone) : null,
            error, all, open, t.OnHoldReason, t.ResolutionNotes,
            ProblemCore.NextTargets(t, _edit, _manage, error is { Status: KnownErrorStatus.Active }),
            _slas.For(t.Id, DateTimeOffset.UtcNow), $"\"{t.Version}\"", t.CreatedAt, t.UpdatedAt, t.ResolvedAt, t.ClosedAt);
    }
}

/// <summary>Known error responses with their problem's number and article.</summary>
internal static class KnownErrorResponses
{
    public static async Task<List<KnownErrorResponse>> LoadAsync(ZelvoDbContext db, IQueryable<KnownError> query, CancellationToken cancellationToken)
    {
        var rows = await query.ToListAsync(cancellationToken).ConfigureAwait(false);
        var problemIds = rows.Select(r => r.ProblemId).Distinct().ToList();
        var numbers = await db.Tickets.AsNoTracking().Where(t => problemIds.Contains(t.Id)).ToDictionaryAsync(t => t.Id, t => t.Number, cancellationToken).ConfigureAwait(false);
        var articleIds = rows.Select(r => r.ArticleId).OfType<Guid>().ToList();
        var articles = await db.KbArticles.AsNoTracking().Where(a => articleIds.Contains(a.Id))
            .ToDictionaryAsync(a => a.Id, a => (a.Number, a.State), cancellationToken).ConfigureAwait(false);
        return [.. rows.Select(e => From(e, numbers.GetValueOrDefault(e.ProblemId) ?? "", e.ArticleId is { } id && articles.TryGetValue(id, out var article) ? article : null))];
    }

    public static KnownErrorResponse From(KnownError e, string problemNumber, (string Number, Data.Knowledge.ArticleState State)? article) => new(
        e.Id, e.Number, e.ProblemId, problemNumber, LocalizedTextDto.From(e.Title), LocalizedTextDto.From(e.Symptoms), LocalizedTextDto.From(e.Workaround),
        e.Status, e.ArticleId, article?.Number, article?.State, $"\"{e.Version}\"", e.CreatedAt, e.UpdatedAt);
}
