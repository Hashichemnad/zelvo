using Microsoft.EntityFrameworkCore;
using Zelvo.Api.Catalog;
using Zelvo.Api.Workflows;
using Zelvo.Data;
using Zelvo.Data.Auditing;
using Zelvo.Data.Changes;
using Zelvo.Data.Tickets;
using Zelvo.Data.Workflows;
using Zelvo.Search;
using Zelvo.Tickets;
using Zelvo.Workflows.Approvals;
using Zelvo.Workflows.Rules;

namespace Zelvo.Api.Changes;

/// <summary>Why a change could not be submitted.</summary>
internal enum SubmitError
{
    /// <summary>Submitted.</summary>
    None,

    /// <summary>A standard change's window falls in a blackout period.</summary>
    Blackout,
}

/// <summary>
/// A change's approval path (task H3; product owner decision 7 of 2026-11-26): a standard change is
/// pre-approved and scheduled at once; a normal change goes through the path of its risk level and
/// an emergency change through the emergency path, as C3 approval processes of stage
/// <see cref="ApprovalStage.ChangeAuthorisation"/>. Approved: scheduled. Rejected: closed with the reason.
/// </summary>
internal static class ChangeApprovals
{
    /// <summary>
    /// Submits <paramref name="ticket"/> inside the caller's transaction. Standard changes in a
    /// blackout period are refused (nobody reviews them); other conflicts are shown to approvers.
    /// </summary>
    public static async Task<SubmitError> SubmitAsync(
        ZelvoDbContext db, ApprovalService approvals, IAuditLog audit, Ticket ticket, ChangeSettingsValue settings, IReadOnlyList<ChangeConflictResponse> conflicts,
        Guid actorId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var change = ticket.Change!;
        if (change.Kind == ChangeKind.Standard && conflicts.Any(c => c.Kind == "blackout"))
        {
            return SubmitError.Blackout;
        }

        change.SubmittedAt = now;
        var definitions = settings.Paths.For(change.Kind, change.RiskLevel ?? ChangeRiskLevel.Medium);
        var levels = definitions.Count == 0 ? [] : await ApprovalPlanner.PlanAsync(
            db, ticket, ApprovalStage.ChangeAuthorisation, definitions, await RuleFacts.LoadAsync(db, ticket, cancellationToken), cancellationToken);
        if (levels.Count == 0)
        {
            await ScheduleAsync(db, ticket, now, cancellationToken);
        }
        else
        {
            change.Phase = ChangePhase.Authorisation;
            await approvals.StartAsync(ticket, ApprovalStage.ChangeAuthorisation, levels, actorId, null, cancellationToken);
        }

        ticket.UpdatedAt = now;
        audit.Add(AuditCategory.Change, "changes.submitted", "ticket", ticket.Id.ToString(), new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["number"] = ticket.Number,
            ["kind"] = change.Kind.ToString(),
            ["riskLevel"] = change.RiskLevel?.ToString(),
            ["levels"] = levels.Count,
            ["conflicts"] = conflicts.Count,
        });
        return SubmitError.None;
    }

    /// <summary>Applies the end of a change's approval process (called by the approval service with the ticket loaded).</summary>
    public static async Task ApplyOutcomeAsync(ZelvoDbContext db, ISearchIndexer indexer, Ticket ticket, ApprovalProcess process, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var change = ticket.Change ?? await db.Changes.SingleAsync(c => c.TicketId == ticket.Id, cancellationToken);
        if (process.State == ApprovalProcessState.Approved)
        {
            await ScheduleAsync(db, ticket, now, cancellationToken);
        }
        else
        {
            ticket.State = TicketState.Closed;
            ticket.ResolutionNotes = process.Reason;
            ticket.ClosedAt = now;
            change.Closure = ChangeClosure.Rejected;
            await RequestTasks.CancelOpenAsync(db, ticket.Id, now, cancellationToken);
        }

        indexer.Upsert(ChangeSearch.Document(ticket, await ChangeSearch.StaffRolesAsync(db, cancellationToken)));
    }

    /// <summary>Scheduled: approved now, and the what-if simulation kept with it (task H5).</summary>
    private static async Task ScheduleAsync(ZelvoDbContext db, Ticket ticket, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var change = ticket.Change!;
        change.Phase = ChangePhase.Scheduled;
        change.ApprovedAt = now;
        await ChangeSimulator.KeepAsync(db, ticket, cancellationToken);
    }
}
