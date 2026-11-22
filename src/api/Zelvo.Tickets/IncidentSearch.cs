using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Zelvo.Data;
using Zelvo.Data.Localization;
using Zelvo.Data.Tickets;
using Zelvo.Search;

namespace Zelvo.Tickets;

/// <summary>Permission keys of incidents (module 1). The API's permission catalog lists them.</summary>
public static class IncidentPermissions
{
    /// <summary>See every incident of the tenant (requesters always see their own).</summary>
    public const string Read = "incidents.read";

    /// <summary>Log incidents on behalf of others, from any channel.</summary>
    public const string Create = "incidents.create";

    /// <summary>Change incidents: title, description, impact, urgency, state, parent and child links.</summary>
    public const string Edit = "incidents.edit";

    /// <summary>Assign incidents to agents.</summary>
    public const string Assign = "incidents.assign";

    /// <summary>Declare major incidents and post to their communication timeline.</summary>
    public const string Major = "incidents.major";
}

/// <summary>
/// Incidents in the search index (LANG-9). Title and description are in whichever language the
/// requester used, so both are analysed as Arabic and as English. The requester, the assignee and
/// holders of roles with <see cref="IncidentPermissions.Read"/> may find them; a role given that
/// permission later finds older incidents after a rebuild (Settings → Search).
/// </summary>
public static class IncidentSearch
{
    /// <summary>Document type.</summary>
    public const string Type = "incident";

    /// <summary>Keys of the roles that see every incident.</summary>
    public static async Task<IReadOnlyList<string>> StaffRolesAsync(ZelvoDbContext db, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        return await db.Roles.Where(r => r.Permissions.Any(p => p.Permission == IncidentPermissions.Read))
            .Select(r => r.Key).OrderBy(k => k).ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The search document of an incident.</summary>
    public static SearchDocument Document(Ticket ticket, IReadOnlyList<string> staffRoles)
    {
        ArgumentNullException.ThrowIfNull(ticket);
        ArgumentNullException.ThrowIfNull(staffRoles);
        var readers = new List<string> { SearchReaders.User(ticket.RequesterId) };
        if (ticket.AssigneeId is { } assignee)
        {
            readers.Add(SearchReaders.User(assignee));
        }

        readers.AddRange(staffRoles.Select(SearchReaders.Role));
        var attributes = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["number"] = ticket.Number,
            ["state"] = ticket.State.ToString(),
        };
        if (ticket.Incident is { } incident)
        {
            attributes["priority"] = incident.Priority.ToString(System.Globalization.CultureInfo.InvariantCulture);
            attributes["isMajor"] = incident.IsMajor ? "true" : "false";
        }

        var title = $"{ticket.Number} {ticket.Title}";
        return new SearchDocument
        {
            Type = Type,
            Id = ticket.Id.ToString(),
            Title = new LocalizedText(title, title),
            Text = ticket.Description is { Length: > 0 } description ? [description] : [],
            Keywords = [ticket.Number],
            Readers = [.. readers.Distinct(StringComparer.Ordinal)],
            Attributes = attributes,
            Url = $"/incidents/{ticket.Id}",
            UpdatedAt = ticket.UpdatedAt,
        };
    }
}

/// <summary>Every incident of the tenant, for rebuilding the search index.</summary>
public sealed class IncidentSearchSource(ZelvoDbContext db) : ISearchDocumentSource
{
    /// <inheritdoc />
    public string Type => IncidentSearch.Type;

    /// <inheritdoc />
    public async IAsyncEnumerable<SearchDocument> ReadAllAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var staff = await IncidentSearch.StaffRolesAsync(db, cancellationToken).ConfigureAwait(false);
        var tickets = db.Tickets.AsNoTracking().Include(t => t.Incident).Where(t => t.Type == TicketType.Incident)
            .OrderBy(t => t.Id).AsAsyncEnumerable();
        await foreach (var ticket in tickets.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            yield return IncidentSearch.Document(ticket, staff);
        }
    }
}
