using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;
using Zelvo.Api.Catalog;
using Zelvo.Api.Conventions;
using Zelvo.Api.Identity;
using Zelvo.Api.Incidents;
using Zelvo.Api.Integrations;
using Zelvo.Api.Knowledge;
using Zelvo.Api.ServiceDesk;
using Zelvo.Api.Tenancy;
using Zelvo.Data;
using Zelvo.Data.Catalog;
using Zelvo.Data.Localization;
using Zelvo.Data.Tickets;
using Zelvo.Search;
using Zelvo.Tickets;

namespace Zelvo.Api.Portal;

/// <summary>
/// The self-service portal (module 4, task D2): one search across the catalog, the knowledge base
/// and the caller's own tickets for the search-first home, and "My tickets": every ticket the
/// caller asked for (incidents, service requests, complaints, inquiries, suggestions) with where
/// it is and which team has it. Approvals come with task C3 (decided 2026-11-24).
/// </summary>
internal static class PortalEndpoints
{
    /// <summary>Most hits per group in the portal search.</summary>
    public const int HitsPerGroup = 5;

    private static readonly string[] TicketTypes = [IncidentSearch.Type, RequestSearch.Type, VoiceSearch.Type];

    public static RouteGroupBuilder MapPortalEndpoints(this RouteGroupBuilder group)
    {
        var portal = group.MapGroup("/portal").WithTags("Portal").RequireTenant();
        portal.MapGet("/search", SearchAsync).WithName("SearchPortal")
            .WithSummary("The portal's \"How can we help?\" search in English and Arabic: services of the catalog and knowledge articles the caller may see, and the caller's own tickets (signed in; only tickets they asked for, also as staff). At most 5 of each, with the portal page to open.")
            .AllowAnonymousAccess().Produces<PortalSearchResponse>().ProducesValidationProblem().ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        group.MapGet("/me/tickets", MyTicketsAsync).WithTags("Portal").RequireTenant().WithName("ListMyTickets")
            .WithSummary("Every ticket the caller asked for, of every record type, recently changed first (open=true: open ones only; type: one record type), with its progress, the team that has it and the portal page to open. Approvals come with task C3.")
            .RequirePerson().RequireAuthorization().Produces<PageResult<PortalTicket>>();
        return group;
    }

    internal static async Task<IResult> SearchAsync(
        [AsParameters] PortalSearchQuery query, ZelvoDbContext db, CurrentUser user, SearchService search, CancellationToken cancellationToken)
    {
        var q = query.Q!.Trim();
        if (q.Length == 0)
        {
            return UserEndpoints.Validation("q", "Type what you are looking for.");
        }

        var readers = (await CatalogScope.OfAsync(db, user, cancellationToken)).Readers;
        try
        {
            var services = await search.SearchAsync(new SearchQuery(q, [CatalogSearch.Type], readers, null, 1, HitsPerGroup), cancellationToken);
            var articles = await search.SearchAsync(new SearchQuery(q, [KnowledgeSearch.Type], readers, null, 1, HitsPerGroup), cancellationToken);
            var tickets = new List<PortalTicket>();
            if (user.Account is { } me)
            {
                // Only the caller's own tickets: their user reader, not the roles staff search with.
                var hits = await search.SearchAsync(new SearchQuery(q, TicketTypes, [SearchReaders.User(me.Id)], null, 1, HitsPerGroup * 2), cancellationToken);
                var ids = hits.Items.Select(h => Guid.TryParse(h.Id, out var id) ? id : Guid.Empty).Where(id => id != Guid.Empty).ToList();
                var rows = await db.Tickets.AsNoTracking().Include(t => t.Voice).Where(t => ids.Contains(t.Id) && t.RequesterId == me.Id).ToListAsync(cancellationToken);
                var context = await PortalTicketContext.LoadAsync(db, rows, cancellationToken);
                // In the order search ranked them; open ones first.
                tickets = [.. ids.Select(id => rows.FirstOrDefault(r => r.Id == id)).OfType<Ticket>()
                    .OrderBy(t => TicketWorkflow.IsOpen(t.State) ? 0 : 1).Take(HitsPerGroup).Select(context.From)];
            }

            return TypedResults.Ok(new PortalSearchResponse(
                [.. services.Items.Select(h => PortalHit.From(h, $"/catalog/{h.Id}"))],
                [.. articles.Items.Select(h => PortalHit.From(h, h.Url ?? $"/help/articles/{h.Id}"))],
                tickets));
        }
        catch (OpenSearchException)
        {
            return ApiProblem.Create(StatusCodes.Status503ServiceUnavailable, "search-unavailable", "Search is not available right now. Try again shortly.");
        }
    }

    internal static async Task<IResult> MyTicketsAsync(
        [AsParameters] PageQuery page, [AsParameters] MyTicketsQuery query, ZelvoDbContext db, CurrentUser user, CancellationToken cancellationToken)
    {
        var me = user.Account!.Id;
        var tickets = db.Tickets.AsNoTracking().Include(t => t.Voice).Where(t => t.RequesterId == me);
        if (query.Open == true)
        {
            tickets = tickets.Where(t => t.State != TicketState.Resolved && t.State != TicketState.Closed);
        }

        if (query.Type is { } type)
        {
            tickets = tickets.Where(t => t.Type == type);
        }

        var ordered = tickets.OrderByDescending(t => t.UpdatedAt).ThenByDescending(t => t.Id);
        var total = await ordered.LongCountAsync(cancellationToken);
        var rows = await ordered.Skip(page.Skip).Take(page.Size).ToListAsync(cancellationToken);
        var context = await PortalTicketContext.LoadAsync(db, rows, cancellationToken);
        return TypedResults.Ok(new PageResult<PortalTicket>([.. rows.Select(context.From)], page.Number, page.Size, total));
    }
}

/// <summary>The portal search.</summary>
internal sealed class PortalSearchQuery
{
    /// <summary>What the person is looking for.</summary>
    [Required]
    [StringLength(200, MinimumLength = 1)]
    public string? Q { get; init; }
}

/// <summary>Filters of "My tickets".</summary>
internal sealed class MyTicketsQuery
{
    /// <summary>Only open ones.</summary>
    public bool? Open { get; init; }

    /// <summary>Only this record type.</summary>
    public TicketType? Type { get; init; }
}

/// <summary>A search hit with the portal page that opens it.</summary>
internal sealed record PortalHit(string Type, string Id, LocalizedTextDto Title, string Path, IReadOnlyDictionary<string, string> Attributes)
{
    public static PortalHit From(SearchHit hit, string path) => new(hit.Type, hit.Id, LocalizedTextDto.From(hit.Title), path, hit.Attributes);
}

/// <summary>A step of a ticket's progress: a generic step (<c>key</c>) or a service step (<c>name</c>).</summary>
internal sealed record PortalStep(string? Key, LocalizedTextDto? Name);

/// <summary>Where a ticket is: its steps and the current one (0-based).</summary>
internal sealed record PortalProgress(IReadOnlyList<PortalStep> Steps, int Current);

/// <summary>
/// One of the caller's tickets as the portal shows it: the team that has it (never the person,
/// as on the requester's own pages), its progress and the page to open.
/// </summary>
internal sealed record PortalTicket(
    Guid Id, string Number, TicketType Type, string Title, TicketState State, bool Open, NamedRef? Team, ServiceRefResponse? Service,
    PortalProgress Progress, string Path, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

/// <summary>What portal tickets need, loaded once for a page.</summary>
internal sealed class PortalTicketContext
{
    /// <summary>Received, being handled, resolved, closed: the steps of tickets without service steps.</summary>
    public static IReadOnlyList<string> GenericSteps { get; } = ["received", "handling", "resolved", "closed"];

    private Dictionary<Guid, (CatalogService Service, ServiceCard Card)> _services = [];
    private DirectoryRefs _refs = null!;

    public static async Task<PortalTicketContext> LoadAsync(ZelvoDbContext db, IReadOnlyList<Ticket> tickets, CancellationToken cancellationToken)
    {
        var requestIds = tickets.Where(t => t.Type == TicketType.ServiceRequest).Select(t => t.Id).ToList();
        var details = requestIds.Count == 0 ? [] : await db.ServiceRequests.AsNoTracking().Where(r => requestIds.Contains(r.TicketId)).ToListAsync(cancellationToken).ConfigureAwait(false);
        var versionIds = details.Select(d => d.ServiceVersionId).Distinct().ToList();
        var versions = versionIds.Count == 0 ? [] : await db.CatalogServiceVersions.AsNoTracking().Where(v => versionIds.Contains(v.Id))
            .ToDictionaryAsync(v => v.Id, v => v.Content, cancellationToken).ConfigureAwait(false);
        var serviceIds = details.Select(d => d.ServiceId).Concat(tickets.Select(t => t.Voice?.ServiceId).OfType<Guid>()).Distinct().ToList();
        var services = serviceIds.Count == 0 ? [] : await db.CatalogServices.AsNoTracking().Where(s => serviceIds.Contains(s.Id))
            .ToDictionaryAsync(s => s.Id, cancellationToken).ConfigureAwait(false);
        var map = new Dictionary<Guid, (CatalogService, ServiceCard)>();
        foreach (var d in details.Where(d => services.ContainsKey(d.ServiceId)))
        {
            map[d.TicketId] = (services[d.ServiceId], ServiceCards.Read(versions.GetValueOrDefault(d.ServiceVersionId)));
        }

        foreach (var t in tickets.Where(t => t.Voice?.ServiceId is { } id && services.ContainsKey(id)))
        {
            map[t.Id] = (services[t.Voice!.ServiceId!.Value], new ServiceCard());
        }

        return new PortalTicketContext { _services = map, _refs = await DirectoryRefs.LoadAsync(db, cancellationToken).ConfigureAwait(false) };
    }

    public PortalTicket From(Ticket t)
    {
        var hasService = _services.TryGetValue(t.Id, out var found);
        var (service, card) = hasService ? found : (null, new ServiceCard());
        PortalProgress progress;
        if (t.Type == TicketType.ServiceRequest && card.Steps.Count > 0)
        {
            // The service's own steps: the current one is the last whose stage the request reached (as on its page).
            var stage = StageOf(t.State);
            var current = -1;
            for (var i = 0; i < card.Steps.Count; i++)
            {
                if ((int)card.Steps[i].Stage <= (int)stage)
                {
                    current = i;
                }
            }

            progress = new PortalProgress([.. card.Steps.Select(s => new PortalStep(null, LocalizedTextDto.From(s.Name)))], Math.Max(current, 0));
        }
        else
        {
            progress = new PortalProgress([.. GenericSteps.Select(k => new PortalStep(k, null))], GenericStepOf(t.State));
        }

        var path = t.Type switch
        {
            TicketType.Incident => $"/my-incidents/{t.Id}",
            TicketType.ServiceRequest => $"/my-requests/{t.Id}",
            _ => $"/my-voice/{t.Id}",
        };
        return new PortalTicket(
            t.Id, t.Number, t.Type, t.Title, t.State, TicketWorkflow.IsOpen(t.State),
            _refs.Group(t.GroupId),
            service is null ? null : new ServiceRefResponse(service.Id, service.Code, LocalizedTextDto.From(card.Name.HasAnyValue ? card.Name : service.Name)),
            progress, path, t.CreatedAt, t.UpdatedAt);
    }

    /// <summary>Step of the generic journey for a state.</summary>
    public static int GenericStepOf(TicketState state) => state switch
    {
        TicketState.New => 0,
        TicketState.Resolved => 2,
        TicketState.Closed => 3,
        _ => 1,
    };

    private static ServiceStepStage StageOf(TicketState state) => state switch
    {
        TicketState.New => ServiceStepStage.Submitted,
        TicketState.Assigned => ServiceStepStage.Assigned,
        TicketState.InProgress or TicketState.Reopened => ServiceStepStage.InProgress,
        TicketState.OnHold => ServiceStepStage.OnHold,
        TicketState.Resolved => ServiceStepStage.Resolved,
        _ => ServiceStepStage.Closed,
    };
}

/// <summary>The portal search: services, articles and the caller's own tickets.</summary>
internal sealed record PortalSearchResponse(IReadOnlyList<PortalHit> Services, IReadOnlyList<PortalHit> Articles, IReadOnlyList<PortalTicket> Tickets);
