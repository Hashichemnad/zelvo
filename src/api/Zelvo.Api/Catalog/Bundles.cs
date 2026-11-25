using Microsoft.EntityFrameworkCore;
using Zelvo.Data;
using Zelvo.Data.Auditing;
using Zelvo.Data.Catalog;
using Zelvo.Data.Localization;
using Zelvo.Data.Tickets;
using Zelvo.Notifications;
using Zelvo.Tickets;
using Zelvo.Tickets.Sla;

namespace Zelvo.Api.Catalog;

/// <summary>
/// Bundles (task C5, module 2): a bundle request holds one child request per service. Parts of a
/// stage run in parallel; a stage starts when every part before it is complete (resolved or
/// closed). The bundle follows its parts: in progress while any is open, resolved when every part
/// is complete (the requester is told), closed when every part is closed, reopened when a part is.
/// </summary>
internal sealed class BundleProgress(
    ZelvoDbContext db, ServiceRequestCreator creator, SlaTracker sla, Zelvo.Search.ISearchIndexer indexer, IAuditLog audit)
{
    /// <summary>Moves the bundle of <paramref name="partId"/> on, if it is a part of one. Call after the part's change is committed.</summary>
    public async Task AfterPartChangedAsync(Guid partId, CancellationToken cancellationToken)
    {
        var bundleId = await db.ServiceRequests.AsNoTracking().Where(r => r.TicketId == partId).Select(r => r.BundleId).SingleOrDefaultAsync(cancellationToken);
        if (bundleId is { } id)
        {
            await AdvanceAsync(id, cancellationToken);
        }
    }

    /// <summary>
    /// Releases the parts whose turn has come and brings the bundle's state in line with its parts.
    /// Locks the bundle's row, so parts finishing at the same time start the next stage once.
    /// Returns whether anything changed.
    /// </summary>
    public async Task<bool> AdvanceAsync(Guid bundleId, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var bundle = await db.Tickets.FromSql($"SELECT *, xmin FROM tickets WHERE id = {bundleId} FOR UPDATE").SingleOrDefaultAsync(cancellationToken);
        if (bundle is null || !await db.ServiceRequests.AnyAsync(r => r.TicketId == bundleId && r.IsBundle, cancellationToken))
        {
            return false;
        }

        var details = await db.ServiceRequests.Where(r => r.BundleId == bundleId).ToListAsync(cancellationToken);
        var ids = details.Select(d => d.TicketId).ToList();
        var parts = await db.Tickets.Where(t => ids.Contains(t.Id)).ToDictionaryAsync(t => t.Id, cancellationToken);
        var now = DateTimeOffset.UtcNow;
        var changed = new List<Ticket>();

        // The next stage starts when every part of the stages before it is complete.
        var waiting = details.Where(d => d.Hold == ServiceRequestHold.Bundle).ToList();
        if (waiting.Count > 0)
        {
            var next = waiting.Min(d => d.BundleStage!.Value);
            var before = details.Where(d => d.BundleStage < next).ToList();
            if (before.All(d => Complete(parts[d.TicketId])))
            {
                foreach (var part in waiting.Where(d => d.BundleStage == next))
                {
                    var ticket = parts[part.TicketId];
                    part.Hold = ServiceRequestHold.None;
                    ticket.Held = false;
                    ticket.UpdatedAt = now;
                    var card = await ServiceRequestEndpoints.CardOfAsync(db, ticket.Id, cancellationToken);
                    await creator.StartAsync(ticket, card, now, cancellationToken);
                    changed.Add(ticket);
                    audit.Add(AuditCategory.Change, "catalog.bundle.part-started", "ticket", ticket.Id.ToString(),
                        new Dictionary<string, object?>(StringComparer.Ordinal) { ["bundleId"] = bundleId.ToString(), ["stage"] = next });
                }
            }
        }

        // The bundle's own state follows its parts.
        var all = parts.Values.ToList();
        var stateBefore = bundle.State;
        if (all.Count > 0 && all.All(t => t.State == TicketState.Closed))
        {
            if (bundle.State != TicketState.Closed)
            {
                bundle.ResolutionNotes ??= await NotesAsync(bundle, all, cancellationToken);
                bundle.State = TicketState.Closed;
                bundle.OnHoldReason = null;
                bundle.ResolvedAt ??= now;
                bundle.ClosedAt = now;
            }
        }
        else if (all.Count > 0 && all.All(Complete))
        {
            if (bundle.State is not (TicketState.Resolved or TicketState.Closed))
            {
                bundle.State = TicketState.Resolved;
                bundle.ResolutionNotes = await NotesAsync(bundle, all, cancellationToken);
                bundle.ResolvedAt = now;
            }
        }
        else if (bundle.State == TicketState.Resolved)
        {
            // A part was reopened.
            bundle.State = TicketState.Reopened;
            bundle.ReopenCount++;
            bundle.ResolvedAt = null;
        }
        else if (bundle.State == TicketState.New)
        {
            bundle.State = TicketState.InProgress;
        }

        if (bundle.State != stateBefore)
        {
            bundle.UpdatedAt = now;
            changed.Add(bundle);
        }

        if (changed.Count == 0)
        {
            return false;
        }

        await sla.UpdateAsync(changed, now, cancellationToken);
        await RequestSearch.IndexAsync(db, indexer, changed, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    /// <summary>A part is complete when it is resolved or closed.</summary>
    public static bool Complete(Ticket part) => part.State is TicketState.Resolved or TicketState.Closed;

    private async Task<string> NotesAsync(Ticket bundle, IReadOnlyList<Ticket> parts, CancellationToken cancellationToken)
    {
        var language = await db.Users.AsNoTracking().Where(u => u.Id == bundle.RequesterId).Select(u => u.PreferredLanguage).SingleOrDefaultAsync(cancellationToken);
        language = language is LocalizedText.Arabic ? LocalizedText.Arabic : LocalizedText.English;
        var names = string.Join(language == LocalizedText.Arabic ? "، " : ", ", parts.OrderBy(p => p.Sequence).Select(p => $"{p.Number} {p.Title}"));
        var notes = NotificationTexts.For(language).Terms["request.bundle-done"].Replace("{parts}", names, StringComparison.Ordinal);
        return notes[..Math.Min(notes.Length, Ticket.ResolutionNotesMaxLength)];
    }
}
