using Microsoft.EntityFrameworkCore;
using Zelvo.Data;
using Zelvo.Data.Changes;
using Zelvo.Data.Tickets;
using Zelvo.Data.Workflows;

namespace Zelvo.Notifications;

/// <summary>
/// Change management notifications (module 17, task H3): assignment and the group's queue as for
/// other record types; the requester and assignee hear when the change's approval path approved or
/// rejected it (a standard change is approved when it is scheduled); the requester and the group's
/// team leads hear when it was implemented and its review is due. Approvers are asked by
/// <see cref="ApprovalNotificationSource"/>. Nobody hears about their own action.
/// </summary>
public sealed class ChangeNotificationSource : INotificationSource
{
    /// <summary>Path of a change.</summary>
    public static string ChangePath(Guid ticketId) => $"/changes/{ticketId}";

    /// <summary>The risk level's name in both languages.</summary>
    public static PlaceholderValue RiskName(ChangeRiskLevel? level) => level switch
    {
        ChangeRiskLevel.Low => new PlaceholderValue { En = "Low", Ar = "منخفض" },
        ChangeRiskLevel.Medium => new PlaceholderValue { En = "Medium", Ar = "متوسط" },
        ChangeRiskLevel.High => new PlaceholderValue { En = "High", Ar = "مرتفع" },
        _ => new PlaceholderValue { En = "Not assessed", Ar = "لم يُقيَّم" },
    };

    /// <summary>The values every change notification has.</summary>
    public static Dictionary<string, PlaceholderValue> Values(Ticket ticket, ChangeRecord? change) => new(StringComparer.Ordinal)
    {
        [Placeholders.TicketNumber] = PlaceholderValue.OfText(ticket.Number),
        [Placeholders.TicketTitle] = PlaceholderValue.OfText(ticket.Title),
        [Placeholders.TicketResolutionNotes] = PlaceholderValue.OfText(ticket.ResolutionNotes),
        [Placeholders.ChangeStart] = change?.PlannedStart is { } start ? PlaceholderValue.OfDate(start) : PlaceholderValue.OfText(string.Empty),
        [Placeholders.ChangeEnd] = change?.PlannedEnd is { } end ? PlaceholderValue.OfDate(end) : PlaceholderValue.OfText(string.Empty),
        [Placeholders.ChangeRisk] = RiskName(change?.RiskLevel),
    };

    /// <inheritdoc />
    public async Task<IReadOnlyList<NotificationRequest>> CollectAsync(ZelvoDbContext db, Guid? actorId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        var entries = db.ChangeTracker.Entries<Ticket>()
            .Where(e => e.Entity.Type == TicketType.Change && e.State is EntityState.Added or EntityState.Modified)
            .ToList();
        var details = db.ChangeTracker.Entries<ChangeRecord>().Where(e => e.State is EntityState.Added or EntityState.Modified).ToList();
        if (entries.Count == 0 && details.Count == 0)
        {
            return [];
        }

        var result = new List<NotificationRequest>();
        var groups = await SourceLookups.GroupNamesAsync(db, entries.Select(e => e.Entity.GroupId), cancellationToken).ConfigureAwait(false);
        foreach (var entry in entries)
        {
            var ticket = entry.Entity;
            var change = ticket.Change ?? details.FirstOrDefault(d => d.Entity.TicketId == ticket.Id)?.Entity;
            var values = Values(ticket, change);
            values[Placeholders.TicketGroup] = ticket.GroupId is { } g && groups.TryGetValue(g, out var name) ? name : PlaceholderValue.OfText(string.Empty);
            var assigned = entry.State == EntityState.Added ? ticket.AssigneeId is not null : SourceLookups.Changed(entry, t => t.AssigneeId);
            if (assigned && ticket.AssigneeId is { } assignee && assignee != actorId)
            {
                result.Add(new(NotificationEvents.ChangeAssigned, [assignee], values, ChangePath(ticket.Id), TicketId: ticket.Id));
            }
            else if (ticket.AssigneeId is null && ticket.GroupId is { } group && (entry.State == EntityState.Added || SourceLookups.Changed(entry, t => t.GroupId)))
            {
                var members = await db.SupportGroupMembers.AsNoTracking().Where(m => m.GroupId == group).Select(m => m.UserId).ToListAsync(cancellationToken).ConfigureAwait(false);
                if (members.Where(m => m != actorId).ToList() is { Count: > 0 } to)
                {
                    result.Add(new(NotificationEvents.ChangeQueued, to, values, ChangePath(ticket.Id), TicketId: ticket.Id));
                }
            }

            if (entry.State == EntityState.Modified && SourceLookups.Changed(entry, t => t.State) && ticket.State == TicketState.Resolved)
            {
                var leads = ticket.GroupId is { } lg
                    ? await db.SupportGroupMembers.AsNoTracking().Where(m => m.GroupId == lg && m.IsLead).Select(m => m.UserId).ToListAsync(cancellationToken).ConfigureAwait(false)
                    : [];
                var to = leads.Append(ticket.RequesterId).Where(p => p != actorId).Distinct().ToList();
                if (to.Count > 0)
                {
                    result.Add(new(NotificationEvents.ChangeImplemented, to, values, ChangePath(ticket.Id), TicketId: ticket.Id));
                }
            }
        }

        // Approved (scheduled) or rejected by the approval path.
        var rejected = db.ChangeTracker.Entries<ApprovalProcess>()
            .Where(e => e.Entity.Stage == ApprovalStage.ChangeAuthorisation && e.State == EntityState.Modified && e.Entity.State == ApprovalProcessState.Rejected
                && e.Property(p => p.State).OriginalValue == ApprovalProcessState.Pending)
            .Select(e => e.Entity).ToList();
        foreach (var entry in details.Where(d => d.State == EntityState.Modified && d.Property(c => c.Phase).IsModified
            && d.Entity.Phase == ChangePhase.Scheduled && d.Property(c => c.Phase).OriginalValue != ChangePhase.Scheduled))
        {
            if (await SourceLookups.TicketAsync(db, entry.Entity.TicketId, cancellationToken).ConfigureAwait(false) is { } ticket)
            {
                Tell(result, NotificationEvents.ChangeApproved, ticket, Values(ticket, entry.Entity), actorId);
            }
        }

        foreach (var process in rejected)
        {
            if (await SourceLookups.TicketAsync(db, process.TicketId, cancellationToken).ConfigureAwait(false) is { } ticket)
            {
                var values = Values(ticket, details.FirstOrDefault(d => d.Entity.TicketId == ticket.Id)?.Entity);
                values[Placeholders.ApprovalReason] = PlaceholderValue.OfText(process.Reason);
                Tell(result, NotificationEvents.ChangeRejected, ticket, values, actorId);
            }
        }

        return result;
    }

    private static void Tell(List<NotificationRequest> result, string type, Ticket ticket, Dictionary<string, PlaceholderValue> values, Guid? actorId)
    {
        var to = new[] { (Guid?)ticket.RequesterId, ticket.AssigneeId }.OfType<Guid>().Where(p => p != actorId).Distinct().ToList();
        if (to.Count > 0)
        {
            result.Add(new(type, to, values, ChangePath(ticket.Id), TicketId: ticket.Id));
        }
    }
}
