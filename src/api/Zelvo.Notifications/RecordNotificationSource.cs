using Microsoft.EntityFrameworkCore;
using Zelvo.Data;
using Zelvo.Data.Tickets;
using Zelvo.Tickets;

namespace Zelvo.Notifications;

/// <summary>
/// Notifications of custom records (module 20, task K2), the same events for every custom type with
/// the type's own name in both languages as <c>{{ticket.type}}</c>: the requester hears that their
/// record was received, answered by staff and resolved; staff hear about assignment and their
/// group's queue. Nobody hears about their own action.
/// </summary>
public sealed class RecordNotificationSource : INotificationSource
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<NotificationRequest>> CollectAsync(ZelvoDbContext db, Guid? actorId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        var entries = db.ChangeTracker.Entries<Ticket>()
            .Where(e => e.Entity.Type == TicketType.Custom && e.State is EntityState.Added or EntityState.Modified)
            .ToList();
        var comments = db.ChangeTracker.Entries<TicketComment>()
            .Where(e => e.State == EntityState.Added && e.Entity.Visibility == CommentVisibility.Public && e.Entity.ByStaff)
            .Select(e => e.Entity).ToList();
        if (entries.Count == 0 && comments.Count == 0)
        {
            return [];
        }

        var requests = new List<(string Event, Ticket Ticket, IReadOnlyCollection<Guid> To, TicketComment? Comment)>();
        foreach (var entry in entries)
        {
            var ticket = entry.Entity;
            if (entry.State == EntityState.Added)
            {
                requests.Add((NotificationEvents.RecordReceived, ticket, [ticket.RequesterId], null));
            }
            else if (SourceLookups.Changed(entry, t => t.State) && ticket.State == TicketState.Resolved && ticket.RequesterId != actorId)
            {
                requests.Add((NotificationEvents.RecordResolved, ticket, [ticket.RequesterId], null));
            }

            var assigned = entry.State == EntityState.Added ? ticket.AssigneeId is not null : SourceLookups.Changed(entry, t => t.AssigneeId);
            if (assigned && ticket.AssigneeId is { } assignee && assignee != actorId)
            {
                requests.Add((NotificationEvents.RecordAssigned, ticket, [assignee], null));
            }
            else if (ticket.AssigneeId is null && ticket.GroupId is { } group && (entry.State == EntityState.Added || SourceLookups.Changed(entry, t => t.GroupId)))
            {
                var members = await db.SupportGroupMembers.AsNoTracking().Where(m => m.GroupId == group && m.UserId != actorId).Select(m => m.UserId)
                    .ToListAsync(cancellationToken).ConfigureAwait(false);
                if (members.Count > 0)
                {
                    requests.Add((NotificationEvents.RecordQueued, ticket, members, null));
                }
            }
        }

        foreach (var comment in comments)
        {
            if (await SourceLookups.TicketAsync(db, comment.TicketId, cancellationToken).ConfigureAwait(false) is { Type: TicketType.Custom } ticket
                && ticket.RequesterId != comment.AuthorId && ticket.RequesterId != actorId)
            {
                requests.Add((NotificationEvents.RecordReplied, ticket, [ticket.RequesterId], comment));
            }
        }

        if (requests.Count == 0)
        {
            return [];
        }

        var typeIds = requests.Select(r => r.Ticket.RecordTypeId).OfType<Guid>().Distinct().ToList();
        var types = await db.RecordTypes.AsNoTracking().Where(t => typeIds.Contains(t.Id)).ToDictionaryAsync(t => t.Id, cancellationToken).ConfigureAwait(false);
        var groups = await SourceLookups.GroupNamesAsync(db, requests.Select(r => r.Ticket.GroupId), cancellationToken).ConfigureAwait(false);
        var names = await SourceLookups.NamesAsync(db, requests.Select(r => (Guid?)r.Ticket.RequesterId).Concat(requests.Select(r => r.Comment?.AuthorId)), cancellationToken)
            .ConfigureAwait(false);
        var result = new List<NotificationRequest>();
        foreach (var (eventType, ticket, to, comment) in requests)
        {
            if (ticket.RecordTypeId is not { } typeId || !types.TryGetValue(typeId, out var type))
            {
                continue;
            }

            var values = new Dictionary<string, PlaceholderValue>(StringComparer.Ordinal)
            {
                [Placeholders.TicketNumber] = PlaceholderValue.OfText(ticket.Number),
                [Placeholders.TicketTitle] = PlaceholderValue.OfText(ticket.Title),
                // The type's own name; the other language stands in when one is missing.
                [Placeholders.TicketType] = new PlaceholderValue { En = type.Name.En ?? type.Name.Ar, Ar = type.Name.Ar ?? type.Name.En },
                [Placeholders.TicketCreatedAt] = PlaceholderValue.OfDate(ticket.CreatedAt),
                [Placeholders.TicketResolutionNotes] = PlaceholderValue.OfText(ticket.ResolutionNotes),
                [Placeholders.TicketGroup] = ticket.GroupId is { } g && groups.TryGetValue(g, out var name) ? name : PlaceholderValue.OfText(string.Empty),
                [Placeholders.RequesterName] = PlaceholderValue.OfText(names.GetValueOrDefault(ticket.RequesterId)),
            };
            if (comment is not null)
            {
                var text = comment.Body.Length <= CommentNotificationSource.TextLimit ? comment.Body : string.Concat(comment.Body.AsSpan(0, CommentNotificationSource.TextLimit - 1), "…");
                values[Placeholders.CommentText] = PlaceholderValue.OfText(text);
                values[Placeholders.CommentAuthor] = PlaceholderValue.OfText(names.GetValueOrDefault(comment.AuthorId));
            }

            result.Add(new(eventType, to, values, $"/records/{type.Key}/{ticket.Id}", TicketId: ticket.Id, CommentId: comment?.Id));
        }

        return result;
    }
}
