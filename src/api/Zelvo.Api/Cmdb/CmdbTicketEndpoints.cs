using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Zelvo.Api.Forms;
using Zelvo.Api.Identity;
using Zelvo.Api.Tenancy;
using Zelvo.Data;
using Zelvo.Data.Auditing;
using Zelvo.Data.Cmdb;
using Zelvo.Data.Tickets;

namespace Zelvo.Api.Cmdb;

/// <summary>
/// Configuration items on incidents and service requests (task G1). Staff who read the ticket see
/// its items; staff who change the ticket add and remove them. Problems (H1) and changes (H3) use
/// the same table. Each link is audited on the ticket and on the item.
/// </summary>
internal static class CmdbTicketEndpoints
{
    private sealed record Kind(string Path, string Tag, TicketType Type, string Read, string Edit);

    private static readonly Kind[] Kinds =
    [
        new("incidents", "Incidents", TicketType.Incident, Permissions.IncidentsRead, Permissions.IncidentsEdit),
        new("service-requests", "Service requests", TicketType.ServiceRequest, Permissions.RequestsRead, Permissions.RequestsEdit),
        // Task H1: configuration items a problem is about.
        new("problems", "Problems", TicketType.Problem, Permissions.ProblemsRead, Permissions.ProblemsEdit),
        // Task H3: the configuration items a change affects.
        new("changes", "Changes", TicketType.Change, Permissions.ChangesRead, Permissions.ChangesEdit),
    ];

    public static RouteGroupBuilder MapCmdbTicketEndpoints(this RouteGroupBuilder group)
    {
        foreach (var kind in Kinds)
        {
            var links = group.MapGroup($"/{kind.Path}/{{id:guid}}/configuration-items").WithTags(kind.Tag).RequireTenant();
            var singular = kind.Type.ToString();

            links.MapGet("/", (Guid id, ZelvoDbContext db, CancellationToken ct) => ListAsync(kind.Type, id, db, ct))
                .WithName($"List{singular}ConfigurationItems")
                .WithSummary($"Configuration items the ticket is about. Needs {kind.Read}.")
                .RequirePermission(kind.Read)
                .Produces<IReadOnlyList<TicketCiResponse>>();

            links.MapPut("/{itemId:guid}", (Guid id, Guid itemId, ZelvoDbContext db, CurrentUser user, IAuditLog audit, CancellationToken ct) =>
                    AddAsync(kind.Type, id, itemId, db, user, audit, ct))
                .WithName($"Add{singular}ConfigurationItem")
                .WithSummary($"Link a configuration item that is not retired to the ticket (nothing happens when it is already linked). Needs {kind.Edit}; audited on the ticket and the item.")
                .RequirePermission(kind.Edit)
                .Produces<IReadOnlyList<TicketCiResponse>>();

            links.MapDelete("/{itemId:guid}", (Guid id, Guid itemId, ZelvoDbContext db, IAuditLog audit, CancellationToken ct) =>
                    RemoveAsync(kind.Type, id, itemId, db, audit, ct))
                .WithName($"Remove{singular}ConfigurationItem")
                .WithSummary($"Unlink a configuration item from the ticket. Needs {kind.Edit}; audited on the ticket and the item.")
                .RequirePermission(kind.Edit)
                .Produces(StatusCodes.Status204NoContent);
        }

        return group;
    }

    /// <summary>Record types whose tickets the caller may read in full (staff permissions).</summary>
    internal static HashSet<TicketType> ReadableTypes(CurrentUser user)
    {
        var types = new HashSet<TicketType>();
        if (user.Has(Permissions.IncidentsRead))
        {
            types.Add(TicketType.Incident);
        }

        if (user.Has(Permissions.RequestsRead))
        {
            types.Add(TicketType.ServiceRequest);
        }

        if (user.Has(Permissions.VoiceRead))
        {
            types.UnionWith([TicketType.Complaint, TicketType.Inquiry, TicketType.Suggestion]);
        }

        if (user.Has(Permissions.ProblemsRead))
        {
            types.Add(TicketType.Problem);
        }

        if (user.Has(Permissions.ChangesRead))
        {
            types.Add(TicketType.Change);
        }

        return types;
    }

    internal static async Task<Results<Ok<List<TicketCiResponse>>, ProblemHttpResult>> ListAsync(
        TicketType type, Guid id, ZelvoDbContext db, CancellationToken cancellationToken)
    {
        if (!await db.Tickets.AnyAsync(t => t.Id == id && t.Type == type, cancellationToken))
        {
            return TicketNotFound();
        }

        return TypedResults.Ok(await LinksAsync(db, id, cancellationToken));
    }

    internal static async Task<Results<Ok<List<TicketCiResponse>>, ProblemHttpResult>> AddAsync(
        TicketType type, Guid id, Guid itemId, ZelvoDbContext db, CurrentUser user, IAuditLog audit, CancellationToken cancellationToken)
    {
        var ticket = await db.Tickets.AsNoTracking().SingleOrDefaultAsync(t => t.Id == id && t.Type == type, cancellationToken);
        if (ticket is null)
        {
            return TicketNotFound();
        }

        var item = await db.ConfigurationItems.AsNoTracking().SingleOrDefaultAsync(i => i.Id == itemId, cancellationToken);
        if (item is null || item.Status == CiStatus.Retired)
        {
            return FormsCommon.NotFound("configuration-item");
        }

        if (!await db.TicketConfigurationItems.AnyAsync(l => l.TicketId == id && l.ConfigurationItemId == itemId, cancellationToken))
        {
            db.TicketConfigurationItems.Add(new TicketConfigurationItem { TicketId = id, ConfigurationItemId = itemId, AddedById = user.Account?.Id });
            Record(audit, "cmdb.ticket-link.added", ticket, item);
            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException)
            {
                // Linked at the same moment by someone else.
            }
        }

        return TypedResults.Ok(await LinksAsync(db, id, cancellationToken));
    }

    internal static async Task<Results<NoContent, ProblemHttpResult>> RemoveAsync(
        TicketType type, Guid id, Guid itemId, ZelvoDbContext db, IAuditLog audit, CancellationToken cancellationToken)
    {
        var ticket = await db.Tickets.AsNoTracking().SingleOrDefaultAsync(t => t.Id == id && t.Type == type, cancellationToken);
        if (ticket is null)
        {
            return TicketNotFound();
        }

        var link = await db.TicketConfigurationItems.SingleOrDefaultAsync(l => l.TicketId == id && l.ConfigurationItemId == itemId, cancellationToken);
        if (link is null)
        {
            return FormsCommon.NotFound("configuration-item");
        }

        var item = await db.ConfigurationItems.AsNoTracking().SingleAsync(i => i.Id == itemId, cancellationToken);
        db.TicketConfigurationItems.Remove(link);
        Record(audit, "cmdb.ticket-link.removed", ticket, item);
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.NoContent();
    }

    /// <summary>Links form answers' configuration items to a new ticket (task G1's picker field).</summary>
    internal static void LinkFromForm(ZelvoDbContext db, IAuditLog? audit, Ticket ticket, IEnumerable<Guid> itemIds, Guid? addedBy)
    {
        foreach (var itemId in itemIds.Distinct())
        {
            db.TicketConfigurationItems.Add(new TicketConfigurationItem { TicketId = ticket.Id, ConfigurationItemId = itemId, AddedById = addedBy });
            audit?.Add(AuditCategory.Change, "cmdb.ticket-link.added", CmdbAudit.ItemEntity, itemId.ToString(),
                new Dictionary<string, object?>(StringComparer.Ordinal) { ["ticketId"] = ticket.Id, ["ticket"] = ticket.Number, ["source"] = "form" });
        }
    }

    private static void Record(IAuditLog audit, string action, Ticket ticket, ConfigurationItem item)
    {
        var details = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["ticketId"] = ticket.Id,
            ["ticket"] = ticket.Number,
            ["configurationItemId"] = item.Id,
            ["configurationItem"] = $"{item.Number} {item.Name}",
        };
        audit.Add(AuditCategory.Change, action, "ticket", ticket.Id.ToString(), details);
        audit.Add(AuditCategory.Change, action, CmdbAudit.ItemEntity, item.Id.ToString(), details);
    }

    private static async Task<List<TicketCiResponse>> LinksAsync(ZelvoDbContext db, Guid ticketId, CancellationToken cancellationToken)
    {
        var links = await db.TicketConfigurationItems.AsNoTracking().Where(l => l.TicketId == ticketId)
            .Join(db.ConfigurationItems.AsNoTracking(), l => l.ConfigurationItemId, i => i.Id, (l, i) => new { Link = l, Item = i })
            .OrderBy(x => x.Link.AddedAt).ToListAsync(cancellationToken);
        var classes = await CiClassIndex.LoadAsync(db, cancellationToken);
        var names = await FormEndpoints.PeopleNamesAsync(db, links.Where(l => l.Link.AddedById is not null).Select(l => l.Link.AddedById!.Value), cancellationToken);
        return [.. links.Select(l => new TicketCiResponse(CiPickResponse.From(l.Item, classes), l.Link.AddedById,
            l.Link.AddedById is { } by ? names.GetValueOrDefault(by) : null, l.Link.AddedAt))];
    }

    private static ProblemHttpResult TicketNotFound() => FormsCommon.NotFound("ticket");
}
