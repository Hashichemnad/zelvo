using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Zelvo.Data;
using Zelvo.Data.Changes;
using Zelvo.Data.Localization;
using Zelvo.Data.Tickets;
using Zelvo.Search;

namespace Zelvo.Tickets;

/// <summary>Permission keys of change management (module 17, task H3). The API's permission catalog lists them.</summary>
public static class ChangePermissions
{
    /// <summary>See changes, their plans, risk, tasks, links and the change calendar.</summary>
    public const string Read = "changes.read";

    /// <summary>Raise and plan changes: plans, window, affected items, risk questionnaire, tasks; submit, implement and cancel them.</summary>
    public const string Edit = "changes.edit";

    /// <summary>
    /// The change manager (product owner decision 11 of 2026-11-26): assign changes, record the
    /// post-implementation review and close them, and set up change management (questionnaire,
    /// approval paths, blackout periods, standard change templates).
    /// </summary>
    public const string Manage = "changes.manage";

    /// <summary>A member of the change advisory board (decision 11): approves changes on the board's approval path.</summary>
    public const string Approve = "changes.approve";
}

/// <summary>The stages of a change shown to people (task H3).</summary>
public enum ChangeStage
{
    /// <summary>Being planned.</summary>
    New,

    /// <summary>Waiting for its approval path.</summary>
    Authorisation,

    /// <summary>Approved and waiting for its window.</summary>
    Scheduled,

    /// <summary>Being carried out.</summary>
    Implementation,

    /// <summary>Implemented; the post-implementation review is due.</summary>
    Review,

    /// <summary>Final: completed, rejected or cancelled.</summary>
    Closed,
}

/// <summary>How a change's stage follows from its ticket state and phase.</summary>
public static class ChangeStages
{
    /// <summary>The stage of a change.</summary>
    public static ChangeStage Of(TicketState state, ChangePhase phase) => state switch
    {
        TicketState.Closed => ChangeStage.Closed,
        TicketState.Resolved => ChangeStage.Review,
        _ => phase switch
        {
            ChangePhase.Authorisation => ChangeStage.Authorisation,
            ChangePhase.Scheduled => ChangeStage.Scheduled,
            ChangePhase.Implementation => ChangeStage.Implementation,
            _ => ChangeStage.New,
        },
    };
}

/// <summary>Changes in the search index (task H3): found by the roles with <see cref="ChangePermissions.Read"/> and the assignee.</summary>
public static class ChangeSearch
{
    /// <summary>Document type.</summary>
    public const string Type = "change";

    /// <summary>Keys of the roles that see changes.</summary>
    public static async Task<IReadOnlyList<string>> StaffRolesAsync(ZelvoDbContext db, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        return await db.Roles.Where(r => r.Permissions.Any(p => p.Permission == ChangePermissions.Read))
            .Select(r => r.Key).OrderBy(k => k).ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The search document of a change: number, title, description and plans.</summary>
    public static SearchDocument Document(Ticket ticket, IReadOnlyList<string> staffRoles)
    {
        ArgumentNullException.ThrowIfNull(ticket);
        ArgumentNullException.ThrowIfNull(staffRoles);
        var readers = staffRoles.Select(SearchReaders.Role).ToList();
        if (ticket.AssigneeId is { } assignee)
        {
            readers.Add(SearchReaders.User(assignee));
        }

        var title = $"{ticket.Number} {ticket.Title}";
        var text = new[] { ticket.Description, ticket.Change?.ImplementationPlan }.OfType<string>().Where(t => t.Length > 0).ToList();
        var change = ticket.Change;
        return new SearchDocument
        {
            Type = Type,
            Id = ticket.Id.ToString(),
            Title = new LocalizedText(title, title),
            Text = text,
            Keywords = [ticket.Number],
            Readers = [.. readers.Distinct(StringComparer.Ordinal)],
            Attributes = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["number"] = ticket.Number,
                ["state"] = ticket.State.ToString(),
                ["stage"] = ChangeStages.Of(ticket.State, change?.Phase ?? ChangePhase.Planning).ToString(),
                ["kind"] = (change?.Kind ?? ChangeKind.Normal).ToString(),
            },
            Url = $"/changes/{ticket.Id}",
            UpdatedAt = ticket.UpdatedAt,
        };
    }
}

/// <summary>Every change of the tenant, for rebuilding the search index.</summary>
public sealed class ChangeSearchSource(ZelvoDbContext db) : ISearchDocumentSource
{
    /// <inheritdoc />
    public string Type => ChangeSearch.Type;

    /// <inheritdoc />
    public async IAsyncEnumerable<SearchDocument> ReadAllAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var staff = await ChangeSearch.StaffRolesAsync(db, cancellationToken).ConfigureAwait(false);
        var tickets = db.Tickets.AsNoTracking().Include(t => t.Change).Where(t => t.Type == TicketType.Change).OrderBy(t => t.Id).AsAsyncEnumerable();
        await foreach (var ticket in tickets.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            yield return ChangeSearch.Document(ticket, staff);
        }
    }
}
