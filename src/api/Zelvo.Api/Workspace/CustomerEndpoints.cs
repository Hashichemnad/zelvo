using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Zelvo.Api.Conventions;
using Zelvo.Api.Identity;
using Zelvo.Api.Incidents;
using Zelvo.Api.ServiceDesk;
using Zelvo.Api.Tenancy;
using Zelvo.Data;
using Zelvo.Data.Auditing;
using Zelvo.Data.Identity;
using Zelvo.Data.Tickets;
using Zelvo.Tickets.Sla;

namespace Zelvo.Api.Workspace;

/// <summary>
/// The customer beside the ticket (module 5 story: "see the customer's previous tickets beside the
/// current one, so that I don't ask the same questions again"): who the requester is, their
/// customer groups and their other incidents.
/// </summary>
internal static class CustomerEndpoints
{
    /// <summary>How many of the requester's other incidents are listed.</summary>
    internal const int RecentCount = 10;

    private static readonly string[] EmailField = ["email"];

    public static RouteGroupBuilder MapCustomerEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/incidents/{id:guid}/customer", GetAsync)
            .WithTags("Agent workspace")
            .RequireTenant()
            .WithName("GetIncidentCustomer")
            .WithSummary("The incident's requester: name, language, email (when the caller's roles show it; the view is audited), customer groups, how many incidents they have open and in total, and their most recent other incidents. Needs incidents.read.")
            .RequirePermission(Permissions.IncidentsRead)
            .Produces<CustomerResponse>();
        return group;
    }

    internal static async Task<Results<Ok<CustomerResponse>, ProblemHttpResult>> GetAsync(
        Guid id, ZelvoDbContext db, CurrentUser user, IAuditLog audit, CancellationToken cancellationToken)
    {
        var ticket = await db.Tickets.AsNoTracking().Where(t => t.Id == id && t.Type == TicketType.Incident)
            .Select(t => new { t.Id, t.RequesterId }).SingleOrDefaultAsync(cancellationToken);
        if (ticket is null)
        {
            return IncidentEndpoints.NotFound();
        }

        var requester = await db.Users.AsNoTracking().SingleAsync(u => u.Id == ticket.RequesterId, cancellationToken);
        var others = db.Tickets.AsNoTracking().Where(t => t.RequesterId == requester.Id && t.Type == TicketType.Incident);
        var total = await others.CountAsync(cancellationToken);
        var open = await others.CountAsync(t => t.State != TicketState.Resolved && t.State != TicketState.Closed, cancellationToken);
        var recent = await others.Where(t => t.Id != ticket.Id).OrderByDescending(t => t.CreatedAt).ThenByDescending(t => t.Id).Take(RecentCount)
            .Select(t => new CustomerTicketResponse(t.Id, t.Number, t.Title, t.State, t.Incident!.Priority, t.CreatedAt, t.ResolvedAt))
            .ToListAsync(cancellationToken);

        var groups = (await db.CustomerGroups.AsNoTracking().Where(g => g.Active).Include(g => g.Members).ToListAsync(cancellationToken))
            .Where(g => SlaMatcher.InDomain(g, requester.Email) || g.Members.Any(m => m.UserId == requester.Id))
            .Select(g => new NamedRef(g.Id, LocalizedTextDto.From(g.Name)))
            .ToList();

        var showEmail = user.Has(Permissions.UsersRead) && FieldFilter.IsVisible(user, PermissionCatalog.Users, "email");
        if (showEmail && requester.Email is not null)
        {
            await audit.RecordAsync(AuditCategory.View, "users.personal-data.viewed", "user_account", requester.Id.ToString(),
                new Dictionary<string, object?>(StringComparer.Ordinal) { ["fields"] = EmailField, ["ticketId"] = ticket.Id.ToString() },
                cancellationToken);
        }

        // Task G3 (module 18 story): the requester's assets, their warranty and the tickets each is on.
        var assets = user.Has(Permissions.AssetsRead) || user.Has(Permissions.AssetsManage)
            ? await Assets.PersonAssetResponse.LoadAsync(db, requester.Id, withTickets: true, cancellationToken)
            : null;

        return TypedResults.Ok(new CustomerResponse(
            new PersonResponse(requester.Id, requester.DisplayName),
            showEmail ? requester.Email : null,
            requester.PreferredLanguage,
            requester.Status,
            groups,
            open,
            total,
            recent,
            assets));
    }
}

/// <summary>The requester of an incident.</summary>
/// <param name="Person">Id and name.</param>
/// <param name="Email">Email address, when the caller's roles show it.</param>
/// <param name="PreferredLanguage">Their own language (<c>en</c> or <c>ar</c>), when set.</param>
/// <param name="Status">Active or Disabled.</param>
/// <param name="CustomerGroups">Customer groups they belong to (by email domain or by name).</param>
/// <param name="OpenCount">Their incidents not resolved or closed, this one included.</param>
/// <param name="TotalCount">All their incidents, this one included.</param>
/// <param name="Recent">Their most recent other incidents, newest first.</param>
/// <param name="Assets">Task G3: the assets assigned to them, for callers with assets.read (null otherwise).</param>
internal sealed record CustomerResponse(
    PersonResponse Person, string? Email, string? PreferredLanguage, UserStatus Status, IReadOnlyList<NamedRef> CustomerGroups,
    int OpenCount, int TotalCount, IReadOnlyList<CustomerTicketResponse> Recent, IReadOnlyList<Assets.PersonAssetResponse>? Assets);

/// <summary>One of the requester's other incidents.</summary>
internal sealed record CustomerTicketResponse(
    Guid Id, string Number, string Title, TicketState State, int Priority, DateTimeOffset CreatedAt, DateTimeOffset? ResolvedAt);
