using Microsoft.EntityFrameworkCore;
using Zelvo.Api.Settings;
using Zelvo.Data;
using Zelvo.Data.Auditing;
using Zelvo.Data.Localization;
using Zelvo.Data.Tenancy;
using Zelvo.Data.Tickets;
using Zelvo.Data.Workflows;
using Zelvo.Tickets;
using Zelvo.Workflows.Approvals;
using Zelvo.Workflows.Engine;
using Zelvo.Workflows.Rules;

namespace Zelvo.Api.Workflows;

/// <summary>Why a decision could not be recorded.</summary>
internal enum DecisionError
{
    /// <summary>Recorded.</summary>
    None,

    /// <summary>The decision was made already, or is no longer needed.</summary>
    AlreadyDecided,

    /// <summary>Rejecting needs a reason.</summary>
    ReasonRequired,
}

/// <summary>
/// Approval processes of tickets (task C3; product owner decisions 2026-11-24). Starts processes
/// with the planned levels on the Elsa engine, records approvers' decisions, moves to the next
/// level when a level is decided (any approver, or all of them) and applies the outcome:
/// <list type="bullet">
/// <item>before assignment, approved: the request is routed to the service desk;</item>
/// <item>before assignment, rejected: the request is closed with the reason;</item>
/// <item>after fulfilment, approved: the request is resolved with the agent's notes;</item>
/// <item>after fulfilment, rejected: the work goes back to the assignee with the reason as an internal note.</item>
/// </list>
/// The ticket waits (<see cref="Ticket.AwaitingApproval"/>, SLA clocks paused) while a process is
/// pending. Every step is audited. Callers save inside a transaction.
/// </summary>
internal sealed class ApprovalService(
    ZelvoDbContext db, IApprovalEngine engine, ITenantContext tenant, IAuditLog audit, TenantSettingsStore settings, Payments.PaymentService payments, Zelvo.Search.ISearchIndexer indexer)
{
    /// <summary>
    /// Starts a process of <paramref name="levels"/> (at least one) for <paramref name="ticket"/>:
    /// the engine waits at the first level and its approvers are asked.
    /// </summary>
    public async Task<ApprovalProcess> StartAsync(
        Ticket ticket, ApprovalStage stage, IReadOnlyList<PlannedLevel> levels, Guid? startedBy, string? resolutionNotes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ticket);
        ArgumentOutOfRangeException.ThrowIfZero(levels.Count);
        var process = new ApprovalProcess
        {
            TicketId = ticket.Id,
            Stage = stage,
            Levels = ApprovalPlanner.Write(levels),
            StartedById = startedBy,
            ResolutionNotes = resolutionNotes,
        };
        var step = await engine.StartAsync(tenant.TenantId!.Value, process.Id, levels.Count, cancellationToken);
        process.EngineInstanceId = step.InstanceId;
        process.EngineBookmarkId = step.BookmarkId;
        process.CurrentLevel = 0;
        db.ApprovalProcesses.Add(process);
        Ask(process, ticket, levels, 0);
        ticket.AwaitingApproval = true;
        ticket.UpdatedAt = DateTimeOffset.UtcNow;
        audit.Add(AuditCategory.Change, "approvals.started", "ticket", ticket.Id.ToString(), new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["processId"] = process.Id.ToString(),
            ["stage"] = stage.ToString(),
            ["levels"] = levels.Count,
            ["fallback"] = levels.Any(l => l.Fallback),
        });
        return process;
    }

    /// <summary>
    /// Records <paramref name="decision"/>'s approver approving or rejecting. Locks the process so
    /// that approvers deciding at the same time are taken one after the other.
    /// </summary>
    public async Task<DecisionError> DecideAsync(Guid decisionId, bool approve, string? comment, string via, CancellationToken cancellationToken)
    {
        if (!approve && string.IsNullOrWhiteSpace(comment))
        {
            return DecisionError.ReasonRequired;
        }

        var processId = await db.ApprovalDecisions.Where(d => d.Id == decisionId).Select(d => d.ProcessId).SingleAsync(cancellationToken);
        var process = await db.ApprovalProcesses.FromSql($"SELECT *, xmin FROM approval_processes WHERE id = {processId} FOR UPDATE").SingleAsync(cancellationToken);
        var decision = await db.ApprovalDecisions.SingleAsync(d => d.Id == decisionId, cancellationToken);
        if (process.State != ApprovalProcessState.Pending || decision.State != ApprovalDecisionState.Pending || decision.Level != process.CurrentLevel)
        {
            return DecisionError.AlreadyDecided;
        }

        var now = DateTimeOffset.UtcNow;
        decision.State = approve ? ApprovalDecisionState.Approved : ApprovalDecisionState.Rejected;
        decision.Comment = string.IsNullOrWhiteSpace(comment) ? null : comment.Trim();
        decision.DecidedAt = now;
        decision.DecidedVia = via;
        audit.Add(AuditCategory.Change, approve ? "approvals.approved" : "approvals.rejected", "ticket", decision.TicketId.ToString(),
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["processId"] = process.Id.ToString(), ["decisionId"] = decision.Id.ToString(), ["level"] = decision.Level, ["via"] = via });

        var levels = ApprovalPlanner.Read(process.Levels);
        var siblings = await db.ApprovalDecisions.Where(d => d.ProcessId == process.Id && d.Level == process.CurrentLevel && d.Id != decision.Id)
            .ToListAsync(cancellationToken);
        var mode = levels[process.CurrentLevel].Mode;
        var levelApproved = approve && (mode == ApprovalMode.Any || siblings.All(s => s.State == ApprovalDecisionState.Approved));
        var levelDecided = !approve || levelApproved;
        if (!levelDecided)
        {
            return DecisionError.None;
        }

        foreach (var sibling in siblings.Where(s => s.State == ApprovalDecisionState.Pending))
        {
            sibling.State = ApprovalDecisionState.NotNeeded;
            sibling.DecidedAt = now;
        }

        var ticket = await db.Tickets.Include(t => t.Incident).SingleAsync(t => t.Id == process.TicketId, cancellationToken);
        var step = await engine.CompleteLevelAsync(tenant.TenantId!.Value, process.EngineInstanceId!, process.EngineBookmarkId!, levelApproved, cancellationToken);
        if (levelApproved && step.BookmarkId is not null && process.CurrentLevel + 1 < levels.Count)
        {
            process.CurrentLevel++;
            process.EngineBookmarkId = step.BookmarkId;
            Ask(process, ticket, levels, process.CurrentLevel);
            return DecisionError.None;
        }

        process.State = levelApproved ? ApprovalProcessState.Approved : ApprovalProcessState.Rejected;
        process.Reason = levelApproved ? null : decision.Comment;
        process.CurrentLevel = levels.Count;
        process.EngineBookmarkId = null;
        process.EndedAt = now;
        await ApplyOutcomeAsync(process, ticket, decision, now, cancellationToken);
        return DecisionError.None;
    }

    /// <summary>Stops the pending processes of <paramref name="ticket"/> (for example when staff withdraw it); the ticket stops waiting.</summary>
    public async Task<bool> CancelAsync(Ticket ticket, string reason, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ticket);
        var processes = await db.ApprovalProcesses.FromSql($"SELECT *, xmin FROM approval_processes WHERE ticket_id = {ticket.Id} AND state = 'Pending' FOR UPDATE")
            .ToListAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow;
        foreach (var process in processes)
        {
            process.State = ApprovalProcessState.Cancelled;
            process.Reason = reason;
            process.EndedAt = now;
            process.EngineBookmarkId = null;
            foreach (var pending in await db.ApprovalDecisions.Where(d => d.ProcessId == process.Id && d.State == ApprovalDecisionState.Pending).ToListAsync(cancellationToken))
            {
                pending.State = ApprovalDecisionState.NotNeeded;
                pending.DecidedAt = now;
            }

            if (process.EngineInstanceId is { } instance)
            {
                await engine.CancelAsync(tenant.TenantId!.Value, instance, cancellationToken);
            }

            audit.Add(AuditCategory.Change, "approvals.cancelled", "ticket", ticket.Id.ToString(),
                new Dictionary<string, object?>(StringComparer.Ordinal) { ["processId"] = process.Id.ToString() });
        }

        ticket.AwaitingApproval = false;
        return processes.Count > 0;
    }

    private void Ask(ApprovalProcess process, Ticket ticket, IReadOnlyList<PlannedLevel> levels, int level)
    {
        foreach (var approver in levels[level].ApproverIds)
        {
            db.ApprovalDecisions.Add(new ApprovalDecision { ProcessId = process.Id, TicketId = ticket.Id, Level = level, ApproverId = approver });
        }
    }

    private async Task ApplyOutcomeAsync(ApprovalProcess process, Ticket ticket, ApprovalDecision decision, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ticket.AwaitingApproval = false;
        ticket.UpdatedAt = now;
        switch (process.Stage, process.State)
        {
            case (ApprovalStage.ChangeAuthorisation, _):
                // Task H3: approved changes are scheduled; rejected ones are closed with the reason.
                await Changes.ChangeApprovals.ApplyOutcomeAsync(db, indexer, ticket, process, now, cancellationToken);
                break;
            case (ApprovalStage.BeforeAssignment, ApprovalProcessState.Approved) when ticket.Type == TicketType.Custom:
                // Task K2: an approved custom record goes to the service desk by the assignment rules.
                await Incidents.IncidentEndpoints.RouteAsync(db, settings, ticket, now, cancellationToken);
                break;
            case (ApprovalStage.BeforeAssignment, ApprovalProcessState.Approved):
                // Task E8: a fee due after approval is paid before the request reaches the service desk.
                var card = await Catalog.ServiceRequestEndpoints.CardOfAsync(db, ticket.Id, cancellationToken);
                if (!await payments.HoldIfDueAsync(ticket, card, Data.Payments.PaymentTiming.AfterApproval, cancellationToken))
                {
                    await Catalog.ServiceRequestRouting.ReleaseAsync(db, settings, ticket, card, now, cancellationToken);
                }

                break;
            case (ApprovalStage.BeforeAssignment, _):
                // Rejected before assignment: closed with the reason (product owner decision 2026-11-24).
                ticket.State = TicketState.Closed;
                ticket.OnHoldReason = null;
                ticket.ResolutionNotes = process.Reason;
                ticket.ClosedAt = now;
                break;
            case (ApprovalStage.AfterFulfilment, ApprovalProcessState.Approved):
                if (TicketWorkflow.Check(ticket, TicketState.Resolved, null, process.ResolutionNotes) == TransitionError.None)
                {
                    TicketWorkflow.Apply(ticket, TicketState.Resolved, null, process.ResolutionNotes, now);
                }

                break;
            default:
                // Sign-off rejected: the work goes back to the assignee, with the reason in the conversation.
                db.TicketComments.Add(new TicketComment
                {
                    TicketId = ticket.Id,
                    Visibility = CommentVisibility.Internal,
                    Body = process.Reason ?? string.Empty,
                    Language = LanguageOf(process.Reason),
                    AuthorId = decision.ApproverId,
                    ByStaff = true,
                    CreatedAt = now,
                });
                break;
        }

        audit.Add(AuditCategory.Change, "approvals.ended", "ticket", ticket.Id.ToString(), new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["processId"] = process.Id.ToString(),
            ["stage"] = process.Stage.ToString(),
            ["state"] = process.State.ToString(),
        });
    }

    /// <summary>Arabic when the text has Arabic letters, else English.</summary>
    internal static string LanguageOf(string? text) =>
        text is not null && text.Any(c => c is >= '؀' and <= 'ۿ') ? LocalizedText.Arabic : LocalizedText.English;
}
