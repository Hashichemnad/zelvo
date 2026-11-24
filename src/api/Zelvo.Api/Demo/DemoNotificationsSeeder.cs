using Microsoft.EntityFrameworkCore;
using Zelvo.Data;
using Zelvo.Data.Notifications;
using Zelvo.Data.Tickets;
using Zelvo.Notifications;
using Zelvo.Tickets;

namespace Zelvo.Api.Demo;

/// <summary>
/// Task B5: notifications for the sample incidents, so every demo persona finds something under
/// the header's bell: "received" for each requester and "assigned" for each assignee. The worker
/// writes them in each person's language and sends their emails (to Mailpit in demo
/// installations). When "Reset demo" runs in the API, the sample incidents already raised these
/// notifications as they were saved, and this seeder adds nothing.
/// </summary>
internal sealed class DemoNotificationsSeeder : IDemoSeeder
{
    public string Key => "b5-notifications";

    public int Order => 130;

    public async Task SeedAsync(DemoSeedContext context, CancellationToken cancellationToken)
    {
        var db = context.Get<ZelvoDbContext>();
        if (await db.Notifications.AnyAsync(cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        var matrix = await TicketSettingKeys.ReadAsync(db, TicketSettingKeys.PriorityMatrix, PriorityMatrix.Default, m => m.IsValid(), cancellationToken)
            .ConfigureAwait(false);
        var tickets = await db.Tickets.AsNoTracking().Include(t => t.Incident).Include(t => t.Group).Include(t => t.Requester)
            .Where(t => t.Type == TicketType.Incident)
            .OrderBy(t => t.Sequence)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var ticket in tickets)
        {
            var values = new Dictionary<string, PlaceholderValue>(StringComparer.Ordinal)
            {
                [Placeholders.TicketNumber] = PlaceholderValue.OfText(ticket.Number),
                [Placeholders.TicketTitle] = PlaceholderValue.OfText(ticket.Title),
                [Placeholders.TicketCreatedAt] = PlaceholderValue.OfDate(ticket.CreatedAt),
                [Placeholders.TicketPriority] = ticket.Incident is { Priority: >= 1 and <= PriorityMatrix.PriorityLevels } incident
                    ? PlaceholderValue.OfText(matrix.Priorities[incident.Priority - 1].ToText())
                    : PlaceholderValue.OfText(string.Empty),
                [Placeholders.TicketGroup] = ticket.Group is { } group ? PlaceholderValue.OfText(group.Name) : PlaceholderValue.OfText(string.Empty),
                [Placeholders.RequesterName] = PlaceholderValue.OfText(ticket.Requester?.DisplayName ?? ticket.Requester?.Email),
            };
            var data = NotificationData.Serialize(values);
            db.Notifications.Add(new Notification
            {
                EventType = NotificationEvents.IncidentReceived, RecipientId = ticket.RequesterId, Data = data,
                LinkPath = $"/my-incidents/{ticket.Id}", TicketId = ticket.Id, CreatedAt = ticket.CreatedAt,
            });
            if (ticket.AssigneeId is { } assignee)
            {
                db.Notifications.Add(new Notification
                {
                    EventType = NotificationEvents.IncidentAssigned, RecipientId = assignee, Data = data,
                    LinkPath = $"/incidents/{ticket.Id}", TicketId = ticket.Id, CreatedAt = ticket.UpdatedAt,
                });
            }
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
