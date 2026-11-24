using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Zelvo.Data;
using Zelvo.Data.Catalog;
using Zelvo.Data.Localization;
using Zelvo.Data.Tickets;
using Zelvo.Data.Workflows;

namespace Zelvo.Notifications;

/// <summary>
/// Notifications other parts of a save decided on before the notification interceptor runs (for
/// example the notify actions of workflow rules, task C3). Scoped: one list per database context.
/// </summary>
public sealed class PendingNotifications
{
    private readonly List<NotificationRequest> _requests = [];

    /// <summary>Queues <paramref name="request"/> with the current save.</summary>
    public void Add(NotificationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        _requests.Add(request);
    }

    /// <summary>Takes everything queued so far.</summary>
    public IReadOnlyList<NotificationRequest> Take()
    {
        var taken = _requests.ToList();
        _requests.Clear();
        return taken;
    }
}

/// <summary>Hands <see cref="PendingNotifications"/> to the notification interceptor.</summary>
public sealed class PendingNotificationSource(PendingNotifications pending) : INotificationSource
{
    /// <inheritdoc />
    public Task<IReadOnlyList<NotificationRequest>> CollectAsync(ZelvoDbContext db, Guid? actorId, CancellationToken cancellationToken) =>
        Task.FromResult(pending.Take());
}

/// <summary>Paths and names of service requests in notifications.</summary>
public static class RequestLookups
{
    /// <summary>Path of a request for its requester.</summary>
    public static string RequesterPath(Guid ticketId) => $"/my-requests/{ticketId}";

    /// <summary>Path of a request for staff.</summary>
    public static string StaffPath(Guid ticketId) => $"/requests/{ticketId}";

    /// <summary>Path of an approval for its approver.</summary>
    public static string ApprovalPath(Guid decisionId) => $"/approvals/{decisionId}";

    /// <summary>The service's name of request tickets (from the card version they were made with).</summary>
    public static async Task<Dictionary<Guid, PlaceholderValue>> ServiceNamesAsync(ZelvoDbContext db, IReadOnlyCollection<Guid> tickets, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (tickets.Count == 0)
        {
            return [];
        }

        var details = db.ChangeTracker.Entries<ServiceRequestDetails>().Where(e => tickets.Contains(e.Entity.TicketId)).Select(e => e.Entity).ToList();
        var missing = tickets.Where(t => details.All(d => d.TicketId != t)).ToList();
        if (missing.Count > 0)
        {
            details.AddRange(await db.ServiceRequests.AsNoTracking().Where(r => missing.Contains(r.TicketId)).ToListAsync(cancellationToken).ConfigureAwait(false));
        }

        var serviceIds = details.Select(d => d.ServiceId).Distinct().ToList();
        var names = await db.CatalogServices.AsNoTracking().Where(s => serviceIds.Contains(s.Id))
            .ToDictionaryAsync(s => s.Id, s => s.Name, cancellationToken).ConfigureAwait(false);
        return details.Where(d => names.ContainsKey(d.ServiceId))
            .ToDictionary(d => d.TicketId, d => PlaceholderValue.OfText(names[d.ServiceId]));
    }
}

/// <summary>
/// Approval notifications (task C3): approvers hear when a decision is asked of them (the email has
/// Approve and Reject buttons); requesters hear when their request was approved or rejected before
/// assignment; the assignee hears when the sign-off of their work was rejected.
/// </summary>
public sealed class ApprovalNotificationSource : INotificationSource
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<NotificationRequest>> CollectAsync(ZelvoDbContext db, Guid? actorId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        var asked = db.ChangeTracker.Entries<ApprovalDecision>()
            .Where(e => e.State == EntityState.Added && e.Entity.State == ApprovalDecisionState.Pending)
            .Select(e => e.Entity).ToList();
        var ended = db.ChangeTracker.Entries<ApprovalProcess>()
            .Where(e => e.State == EntityState.Modified && e.Property(p => p.State).IsModified
                && e.Property(p => p.State).OriginalValue == ApprovalProcessState.Pending
                && e.Entity.State is ApprovalProcessState.Approved or ApprovalProcessState.Rejected)
            .Select(e => e.Entity).ToList();
        if (asked.Count == 0 && ended.Count == 0)
        {
            return [];
        }

        var ticketIds = asked.Select(a => a.TicketId).Concat(ended.Select(e => e.TicketId)).Distinct().ToList();
        var services = await RequestLookups.ServiceNamesAsync(db, ticketIds, cancellationToken).ConfigureAwait(false);
        var tickets = new Dictionary<Guid, Ticket>();
        foreach (var id in ticketIds)
        {
            if (await SourceLookups.TicketAsync(db, id, cancellationToken).ConfigureAwait(false) is { } ticket)
            {
                tickets[id] = ticket;
            }
        }

        var names = await SourceLookups.NamesAsync(db, tickets.Values.Select(t => (Guid?)t.RequesterId), cancellationToken).ConfigureAwait(false);
        Dictionary<string, PlaceholderValue> Values(Ticket ticket) => new(StringComparer.Ordinal)
        {
            [Placeholders.TicketNumber] = PlaceholderValue.OfText(ticket.Number),
            [Placeholders.TicketTitle] = PlaceholderValue.OfText(ticket.Title),
            [Placeholders.RequesterName] = PlaceholderValue.OfText(names.GetValueOrDefault(ticket.RequesterId)),
            [Placeholders.ServiceName] = services.GetValueOrDefault(ticket.Id) ?? PlaceholderValue.OfText(string.Empty),
        };

        var result = new List<NotificationRequest>();
        var processes = new Dictionary<Guid, ApprovalProcess?>();
        foreach (var decision in asked.Where(d => d.ApproverId != actorId))
        {
            if (!tickets.TryGetValue(decision.TicketId, out var ticket))
            {
                continue;
            }

            if (!processes.TryGetValue(decision.ProcessId, out var process))
            {
                process = db.ChangeTracker.Entries<ApprovalProcess>().FirstOrDefault(e => e.Entity.Id == decision.ProcessId)?.Entity
                    ?? await db.ApprovalProcesses.AsNoTracking().FirstOrDefaultAsync(p => p.Id == decision.ProcessId, cancellationToken).ConfigureAwait(false);
                processes[decision.ProcessId] = process;
            }

            var values = Values(ticket);
            values[Placeholders.ApprovalLevel] = PlaceholderValue.OfText(LevelName(process, decision.Level));
            result.Add(new(NotificationEvents.ApprovalRequested, [decision.ApproverId], values, RequestLookups.ApprovalPath(decision.Id), TicketId: ticket.Id));
        }

        foreach (var process in ended)
        {
            if (!tickets.TryGetValue(process.TicketId, out var ticket))
            {
                continue;
            }

            var values = Values(ticket);
            values[Placeholders.ApprovalReason] = PlaceholderValue.OfText(process.Reason);
            if (process.Stage == ApprovalStage.BeforeAssignment && ticket.RequesterId != actorId)
            {
                var type = process.State == ApprovalProcessState.Approved ? NotificationEvents.RequestApproved : NotificationEvents.RequestRejected;
                result.Add(new(type, [ticket.RequesterId], values, RequestLookups.RequesterPath(ticket.Id), TicketId: ticket.Id));
            }
            else if (process.Stage == ApprovalStage.AfterFulfilment && process.State == ApprovalProcessState.Rejected
                && ticket.AssigneeId is { } assignee && assignee != actorId)
            {
                result.Add(new(NotificationEvents.RequestSignOffRejected, [assignee], values, RequestLookups.StaffPath(ticket.Id), TicketId: ticket.Id));
            }
        }

        return result;
    }

    /// <summary>The bilingual name of level <paramref name="level"/> of a process, from its planned levels.</summary>
    private static LocalizedText LevelName(ApprovalProcess? process, int level)
    {
        if (process is null)
        {
            return new(null, null);
        }

        using var document = JsonDocument.Parse(process.Levels);
        if (document.RootElement.ValueKind != JsonValueKind.Array || level >= document.RootElement.GetArrayLength())
        {
            return new(null, null);
        }

        var name = document.RootElement[level].TryGetProperty("name", out var n) ? n : default;
        string? Get(string language) => name.ValueKind == JsonValueKind.Object && name.TryGetProperty(language, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        return new(Get(LocalizedText.English), Get(LocalizedText.Arabic));
    }
}
