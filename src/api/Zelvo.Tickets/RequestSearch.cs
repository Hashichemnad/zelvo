using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Zelvo.Data;
using Zelvo.Data.Localization;
using Zelvo.Data.Tickets;
using Zelvo.Search;

namespace Zelvo.Tickets;

/// <summary>
/// Service requests in the search index (task D2, for the portal's search-first home): the
/// requester, whoever made it for them, the assignee and roles with requests.read find them.
/// </summary>
public static class RequestSearch
{
    /// <summary>Document type.</summary>
    public const string Type = "request";

    /// <summary>Permission of staff who see every service request (the API's catalog lists it).</summary>
    public const string ReadPermission = "requests.read";

    /// <summary>Keys of the roles that see every service request.</summary>
    public static async Task<IReadOnlyList<string>> StaffRolesAsync(ZelvoDbContext db, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        return await db.Roles.Where(r => r.Permissions.Any(p => p.Permission == ReadPermission))
            .Select(r => r.Key).OrderBy(k => k).ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The search document of a service request.</summary>
    public static SearchDocument Document(Ticket ticket, IReadOnlyList<string> staffRoles)
    {
        ArgumentNullException.ThrowIfNull(ticket);
        ArgumentNullException.ThrowIfNull(staffRoles);
        var readers = new List<string> { SearchReaders.User(ticket.RequesterId) };
        if (ticket.CreatedById is { } by)
        {
            readers.Add(SearchReaders.User(by));
        }

        if (ticket.AssigneeId is { } assignee)
        {
            readers.Add(SearchReaders.User(assignee));
        }

        readers.AddRange(staffRoles.Select(SearchReaders.Role));
        var title = $"{ticket.Number} {ticket.Title}";
        return new SearchDocument
        {
            Type = Type,
            Id = ticket.Id.ToString(),
            Title = new LocalizedText(title, title),
            Text = ticket.Description is { Length: > 0 } description ? [description] : [],
            Keywords = [ticket.Number],
            Readers = [.. readers.Distinct(StringComparer.Ordinal)],
            Attributes = new Dictionary<string, string>(StringComparer.Ordinal) { ["number"] = ticket.Number, ["state"] = ticket.State.ToString() },
            Url = $"/requests/{ticket.Id}",
            UpdatedAt = ticket.UpdatedAt,
        };
    }

    /// <summary>Queues the documents of <paramref name="tickets"/> (saved with the caller's next save).</summary>
    public static async Task IndexAsync(ZelvoDbContext db, ISearchIndexer indexer, IEnumerable<Ticket> tickets, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(indexer);
        var staff = await StaffRolesAsync(db, cancellationToken).ConfigureAwait(false);
        foreach (var ticket in tickets)
        {
            indexer.Upsert(Document(ticket, staff));
        }
    }
}

/// <summary>Every service request of the tenant, for rebuilding the search index.</summary>
public sealed class RequestSearchSource(ZelvoDbContext db) : ISearchDocumentSource
{
    /// <inheritdoc />
    public string Type => RequestSearch.Type;

    /// <inheritdoc />
    public async IAsyncEnumerable<SearchDocument> ReadAllAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var staff = await RequestSearch.StaffRolesAsync(db, cancellationToken).ConfigureAwait(false);
        var tickets = db.Tickets.AsNoTracking().Where(t => t.Type == TicketType.ServiceRequest).OrderBy(t => t.Id).AsAsyncEnumerable();
        await foreach (var ticket in tickets.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            yield return RequestSearch.Document(ticket, staff);
        }
    }
}
