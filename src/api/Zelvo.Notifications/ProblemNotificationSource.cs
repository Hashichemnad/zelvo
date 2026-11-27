using Microsoft.EntityFrameworkCore;
using Zelvo.Data;
using Zelvo.Data.Problems;
using Zelvo.Data.Tickets;
using Zelvo.Tickets;

namespace Zelvo.Notifications;

/// <summary>
/// Problem management notifications (module 16, task H1): assignment and the group's queue as for
/// other record types; a recorded known error tells the assignees of the problem's open incidents
/// that a workaround is available; a resolved problem tells the assignees of its incidents and the
/// person who raised it. Nobody hears about their own action.
/// </summary>
public sealed class ProblemNotificationSource : INotificationSource
{
    /// <summary>Path of a problem.</summary>
    public static string ProblemPath(Guid ticketId) => $"/problems/{ticketId}";

    /// <inheritdoc />
    public async Task<IReadOnlyList<NotificationRequest>> CollectAsync(ZelvoDbContext db, Guid? actorId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        var entries = db.ChangeTracker.Entries<Ticket>()
            .Where(e => e.Entity.Type == TicketType.Problem && e.State is EntityState.Added or EntityState.Modified)
            .ToList();
        var errors = db.ChangeTracker.Entries<KnownError>()
            .Where(e => e.Entity.Status == KnownErrorStatus.Active
                && (e.State == EntityState.Added
                    || (e.State == EntityState.Modified && e.Property(k => k.Status).IsModified && e.Property(k => k.Status).OriginalValue != KnownErrorStatus.Active)))
            .Select(e => e.Entity).ToList();
        if (entries.Count == 0 && errors.Count == 0)
        {
            return [];
        }

        var result = new List<NotificationRequest>();
        var groups = await SourceLookups.GroupNamesAsync(db, entries.Select(e => e.Entity.GroupId), cancellationToken).ConfigureAwait(false);
        foreach (var entry in entries)
        {
            var ticket = entry.Entity;
            var values = Values(ticket);
            values[Placeholders.TicketGroup] = ticket.GroupId is { } g && groups.TryGetValue(g, out var name) ? name : PlaceholderValue.OfText(string.Empty);
            var assigned = entry.State == EntityState.Added ? ticket.AssigneeId is not null : SourceLookups.Changed(entry, t => t.AssigneeId);
            if (assigned && ticket.AssigneeId is { } assignee && assignee != actorId)
            {
                result.Add(new(NotificationEvents.ProblemAssigned, [assignee], values, ProblemPath(ticket.Id), TicketId: ticket.Id));
            }
            else if (ticket.AssigneeId is null && ticket.GroupId is { } group && (entry.State == EntityState.Added || SourceLookups.Changed(entry, t => t.GroupId)))
            {
                var members = await db.SupportGroupMembers.AsNoTracking().Where(m => m.GroupId == group).Select(m => m.UserId).ToListAsync(cancellationToken).ConfigureAwait(false);
                if (members.Where(m => m != actorId).ToList() is { Count: > 0 } to)
                {
                    result.Add(new(NotificationEvents.ProblemQueued, to, values, ProblemPath(ticket.Id), TicketId: ticket.Id));
                }
            }

            if (SourceLookups.Changed(entry, t => t.State) && ticket.State == TicketState.Resolved)
            {
                var incidents = await LinkedIncidentsAsync(db, ticket.Id, cancellationToken).ConfigureAwait(false);
                var to = incidents.Select(i => i.AssigneeId).Append(ticket.CreatedById).OfType<Guid>().Where(p => p != actorId).Distinct().ToList();
                if (to.Count > 0)
                {
                    result.Add(new(NotificationEvents.ProblemResolved, to, values, ProblemPath(ticket.Id), TicketId: ticket.Id));
                }
            }
        }

        foreach (var error in errors)
        {
            var problem = await SourceLookups.TicketAsync(db, error.ProblemId, cancellationToken).ConfigureAwait(false);
            if (problem is null)
            {
                continue;
            }

            foreach (var incident in (await LinkedIncidentsAsync(db, problem.Id, cancellationToken).ConfigureAwait(false))
                .Where(i => TicketWorkflow.IsOpen(i.State) && i.AssigneeId is { } a && a != actorId))
            {
                var values = Values(incident);
                values[Placeholders.ProblemNumber] = PlaceholderValue.OfText(problem.Number);
                values[Placeholders.KnownErrorNumber] = PlaceholderValue.OfText(error.Number);
                values[Placeholders.KnownErrorTitle] = new PlaceholderValue { En = error.Title.En ?? error.Title.Ar, Ar = error.Title.Ar ?? error.Title.En };
                result.Add(new(NotificationEvents.ProblemKnownError, [incident.AssigneeId!.Value], values, SourceLookups.WorkspacePath(incident.Id), TicketId: incident.Id));
            }
        }

        return result;
    }

    private static Dictionary<string, PlaceholderValue> Values(Ticket ticket) => new(StringComparer.Ordinal)
    {
        [Placeholders.TicketNumber] = PlaceholderValue.OfText(ticket.Number),
        [Placeholders.TicketTitle] = PlaceholderValue.OfText(ticket.Title),
        [Placeholders.TicketResolutionNotes] = PlaceholderValue.OfText(ticket.ResolutionNotes),
    };

    // Saved links and links added in this save.
    private static async Task<List<Ticket>> LinkedIncidentsAsync(ZelvoDbContext db, Guid problemId, CancellationToken cancellationToken)
    {
        var ids = await db.ProblemIncidents.AsNoTracking().Where(l => l.ProblemId == problemId).Select(l => l.IncidentId).ToListAsync(cancellationToken).ConfigureAwait(false);
        ids.AddRange(db.ChangeTracker.Entries<ProblemIncident>().Where(e => e.State == EntityState.Added && e.Entity.ProblemId == problemId).Select(e => e.Entity.IncidentId));
        var result = new List<Ticket>();
        foreach (var id in ids.Distinct())
        {
            if (await SourceLookups.TicketAsync(db, id, cancellationToken).ConfigureAwait(false) is { } incident)
            {
                result.Add(incident);
            }
        }

        return result;
    }
}
