using System.ComponentModel.DataAnnotations;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Zelvo.Api.Catalog;
using Zelvo.Api.Conventions;
using Zelvo.Api.Forms;
using Zelvo.Api.Identity;
using Zelvo.Api.Incidents;
using Zelvo.Api.Integrations;
using Zelvo.Api.Tenancy;
using Zelvo.Data;
using Zelvo.Data.Auditing;
using Zelvo.Data.Tickets;
using Zelvo.Data.Workflows;
using Zelvo.Tickets.Forms;
using Zelvo.Workflows.Approvals;

namespace Zelvo.Api.Workflows;

/// <summary>
/// Approvals (task C3, module 2): approvers decide from the portal or from the email's Approve and
/// Reject buttons, which open the approval here after sign-in (product owner decision 2026-11-24).
/// Approvers see the request they are asked about; holders of approvals.read see every approval.
/// </summary>
internal static class ApprovalEndpoints
{
    public static RouteGroupBuilder MapApprovalEndpoints(this RouteGroupBuilder group)
    {
        var approvals = group.MapGroup("/approvals").WithTags("Approvals").RequireTenant();
        approvals.MapGet("/", ListAsync).WithName("ListApprovals")
            .WithSummary("Decisions asked of the caller, newest first (state=Pending for those waiting). With all=true, holders of approvals.read see everyone's.")
            .RequireAuthorization().Produces<PageResult<ApprovalSummaryResponse>>();
        approvals.MapGet("/{id:guid}", GetAsync).WithName("GetApproval")
            .WithSummary("One decision with the request it is about, its form answers and the process. The approver, or holders of approvals.read. Personal answers seen by others than the requester are audited.")
            .RequireAuthorization().Produces<ApprovalDetailResponse>();
        approvals.MapPost("/{id:guid}/decision", DecideAsync).WithName("DecideApproval")
            .WithSummary("Approve or reject (with a reason). Only the approver, while the decision waits; 409 already-decided otherwise. Audited.")
            .RequirePerson().RequireAuthorization().Produces<ApprovalDetailResponse>().ProducesValidationProblem();

        var requests = group.MapGroup("/service-requests/{id:guid}/approvals").WithTags("Approvals").RequireTenant();
        requests.MapGet("/", ForRequestAsync).WithName("ListServiceRequestApprovals")
            .WithSummary("The request's approval processes with their levels and decisions. Who may see the request.")
            .RequireAuthorization().Produces<IReadOnlyList<ApprovalProcessResponse>>();
        requests.MapPost("/cancel", CancelAsync).WithName("CancelServiceRequestApprovals")
            .WithSummary("Stop the request's pending approval. Before assignment the request is closed as withdrawn; a sign-off goes back to In progress. Needs requests.edit (or the requester, before assignment); audited.")
            .RequirePerson().RequireAuthorization().Produces<ServiceRequestApprovalState>().ProducesValidationProblem();
        return group;
    }

    internal static async Task<IResult> ListAsync(
        [AsParameters] PageQuery page, ApprovalDecisionState? state, bool? all, ZelvoDbContext db, CurrentUser user, CancellationToken cancellationToken)
    {
        var me = user.Account?.Id;
        var query = db.ApprovalDecisions.AsNoTracking();
        if (all != true || !user.Has(Permissions.ApprovalsRead))
        {
            query = query.Where(d => d.ApproverId == me);
        }

        if (state is { } s)
        {
            query = query.Where(d => d.State == s);
        }

        var ordered = query.OrderByDescending(d => d.CreatedAt).ThenByDescending(d => d.Id);
        var total = await ordered.LongCountAsync(cancellationToken);
        var rows = await ordered.Skip(page.Skip).Take(page.Size).ToListAsync(cancellationToken);
        var context = await SummaryContext.LoadAsync(db, rows, cancellationToken);
        return TypedResults.Ok(new PageResult<ApprovalSummaryResponse>([.. rows.Select(context.From)], page.Number, page.Size, total));
    }

    internal static async Task<IResult> GetAsync(Guid id, ZelvoDbContext db, CurrentUser user, IAuditLog audit, CancellationToken cancellationToken)
    {
        var decision = await VisibleAsync(db, user, id, cancellationToken);
        return decision is null ? NotFound() : TypedResults.Ok(await DetailAsync(db, user, audit, decision, cancellationToken));
    }

    internal static async Task<IResult> DecideAsync(
        Guid id, DecideApprovalRequest request, ZelvoDbContext db, CurrentUser user, ApprovalService approvals, IAuditLog audit, Zelvo.Tickets.Sla.SlaTracker sla,
        Catalog.BundleProgress bundles, CancellationToken cancellationToken)
    {
        var decision = await db.ApprovalDecisions.AsNoTracking().SingleOrDefaultAsync(d => d.Id == id, cancellationToken);
        if (decision is null || decision.ApproverId != user.Account?.Id)
        {
            // Others than the approver cannot decide, and do not learn the decision exists unless they may read approvals.
            return decision is not null && user.Has(Permissions.ApprovalsRead)
                ? ApiProblem.Create(StatusCodes.Status403Forbidden, "forbidden", "Only the person asked can decide.")
                : NotFound();
        }

        if (request.Comment is { Length: > ApprovalDecision.CommentMaxLength })
        {
            return UserEndpoints.Validation("comment", $"Use at most {ApprovalDecision.CommentMaxLength} characters.");
        }

        var approve = request.Decision == ApprovalChoice.Approve;
        var via = request.Via == "email" ? "email" : "portal";
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var error = await approvals.DecideAsync(id, approve, request.Comment, via, cancellationToken);
        switch (error)
        {
            case DecisionError.ReasonRequired:
                return UserEndpoints.Validation("comment", "Give the reason for rejecting.");
            case DecisionError.AlreadyDecided:
                return ApiProblem.Create(StatusCodes.Status409Conflict, "already-decided", "This approval has been decided already or is no longer needed.");
        }

        await SaveTicketsAsync(db, sla, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        var saved = await db.ApprovalDecisions.AsNoTracking().SingleAsync(d => d.Id == id, cancellationToken);
        // Task C5: a part of a bundle closed by a rejection, or resolved by a sign-off, moves its bundle on.
        await bundles.AfterPartChangedAsync(saved.TicketId, cancellationToken);
        return TypedResults.Ok(await DetailAsync(db, user, audit, saved, cancellationToken));
    }

    internal static async Task<IResult> ForRequestAsync(Guid id, ZelvoDbContext db, CurrentUser user, CancellationToken cancellationToken)
    {
        var ticket = await ServiceRequestEndpoints.FindVisibleAsync(db, user, id, cancellationToken);
        if (ticket is null && !await IsApproverAsync(db, user, id, cancellationToken))
        {
            return FormsCommon.NotFound("service-request");
        }

        return TypedResults.Ok(await ProcessesAsync(db, id, cancellationToken));
    }

    internal static async Task<IResult> CancelAsync(
        Guid id, CancelApprovalRequest request, ZelvoDbContext db, CurrentUser user, ApprovalService approvals, Zelvo.Tickets.Sla.SlaTracker sla,
        Catalog.BundleProgress bundles, CancellationToken cancellationToken)
    {
        var visible = await ServiceRequestEndpoints.FindVisibleAsync(db, user, id, cancellationToken);
        if (visible is null)
        {
            return FormsCommon.NotFound("service-request");
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var ticket = await db.Tickets.SingleAsync(t => t.Id == id, cancellationToken);
        var pending = await db.ApprovalProcesses.AsNoTracking().Where(p => p.TicketId == id && p.State == ApprovalProcessState.Pending).ToListAsync(cancellationToken);
        if (pending.Count == 0)
        {
            return ApiProblem.Create(StatusCodes.Status409Conflict, "no-pending-approval", "The request is not waiting for an approval.");
        }

        var beforeAssignment = pending.Any(p => p.Stage == ApprovalStage.BeforeAssignment);
        var requesterWithdraws = beforeAssignment && ticket.RequesterId == user.Account?.Id;
        if (!user.Has(Permissions.RequestsEdit) && !requesterWithdraws)
        {
            return ApiProblem.Create(StatusCodes.Status403Forbidden, "forbidden", "Only staff who work on requests, or the requester before assignment, may stop an approval.");
        }

        var reason = string.IsNullOrWhiteSpace(request.Reason) ? "Withdrawn" : request.Reason.Trim();
        await approvals.CancelAsync(ticket, reason, cancellationToken);
        var now = DateTimeOffset.UtcNow;
        if (beforeAssignment)
        {
            ticket.State = TicketState.Closed;
            ticket.ResolutionNotes = reason;
            ticket.ClosedAt = now;
        }

        ticket.UpdatedAt = now;
        await SaveTicketsAsync(db, sla, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        await bundles.AfterPartChangedAsync(ticket.Id, cancellationToken);
        return TypedResults.Ok(new ServiceRequestApprovalState(ticket.Id, ticket.State, ticket.AwaitingApproval));
    }

    // ---- helpers ----

    /// <summary>Keeps SLA clocks in step with tickets the approval changed (resumed, routed, closed or resolved).</summary>
    private static async Task SaveTicketsAsync(ZelvoDbContext db, Zelvo.Tickets.Sla.SlaTracker sla, CancellationToken cancellationToken)
    {
        db.ChangeTracker.DetectChanges();
        var tickets = db.ChangeTracker.Entries<Ticket>().Where(e => e.State == EntityState.Modified).Select(e => e.Entity).ToList();
        if (tickets.Count > 0)
        {
            await sla.UpdateAsync(tickets, DateTimeOffset.UtcNow, cancellationToken);
        }
    }

    private static async Task<ApprovalDecision?> VisibleAsync(ZelvoDbContext db, CurrentUser user, Guid id, CancellationToken cancellationToken)
    {
        var decision = await db.ApprovalDecisions.AsNoTracking().SingleOrDefaultAsync(d => d.Id == id, cancellationToken);
        return decision is not null && (decision.ApproverId == user.Account?.Id || user.Has(Permissions.ApprovalsRead)) ? decision : null;
    }

    private static Task<bool> IsApproverAsync(ZelvoDbContext db, CurrentUser user, Guid ticketId, CancellationToken cancellationToken) =>
        user.Account is { } me
            ? db.ApprovalDecisions.AnyAsync(d => d.TicketId == ticketId && d.ApproverId == me.Id, cancellationToken)
            : Task.FromResult(user.Has(Permissions.ApprovalsRead));

    private static async Task<ApprovalDetailResponse> DetailAsync(ZelvoDbContext db, CurrentUser user, IAuditLog audit, ApprovalDecision decision, CancellationToken cancellationToken)
    {
        var context = await SummaryContext.LoadAsync(db, [decision], cancellationToken);
        var ticket = await db.Tickets.AsNoTracking().SingleAsync(t => t.Id == decision.TicketId, cancellationToken);
        var form = await FormSubmission.ViewAsync(db, user, audit, ticket, cancellationToken);
        var processes = await ProcessesAsync(db, ticket.Id, cancellationToken);
        var mayDecide = decision.ApproverId == user.Account?.Id && decision.State == ApprovalDecisionState.Pending
            && processes.Any(p => p.Id == decision.ProcessId && p.State == ApprovalProcessState.Pending && p.CurrentLevel == decision.Level);
        return new ApprovalDetailResponse(
            context.From(decision), ticket.Description,
            form is null ? null : (JsonObject)System.Text.Json.JsonSerializer.SerializeToNode(form, FormJson.Options)!,
            processes.Single(p => p.Id == decision.ProcessId), mayDecide);
    }

    internal static async Task<List<ApprovalProcessResponse>> ProcessesAsync(ZelvoDbContext db, Guid ticketId, CancellationToken cancellationToken)
    {
        var processes = await db.ApprovalProcesses.AsNoTracking().Where(p => p.TicketId == ticketId).OrderBy(p => p.CreatedAt).ToListAsync(cancellationToken);
        var ids = processes.Select(p => p.Id).ToList();
        var decisions = await db.ApprovalDecisions.AsNoTracking().Where(d => ids.Contains(d.ProcessId)).OrderBy(d => d.CreatedAt).ToListAsync(cancellationToken);
        var names = await IncidentEndpoints.Names(db, decisions.Select(d => d.ApproverId), cancellationToken);
        return [.. processes.Select(p =>
        {
            var levels = ApprovalPlanner.Read(p.Levels);
            return new ApprovalProcessResponse(p.Id, p.Stage, p.State, p.CurrentLevel, p.Reason, p.CreatedAt, p.EndedAt,
                [.. levels.Select((level, index) =>
                {
                    var own = decisions.Where(d => d.ProcessId == p.Id && d.Level == index).ToList();
                    var state = own.Any(d => d.State == ApprovalDecisionState.Rejected) ? "Rejected"
                        : index < p.CurrentLevel || (p.State == ApprovalProcessState.Approved) ? "Approved"
                        : index == p.CurrentLevel && p.State == ApprovalProcessState.Pending ? "Waiting"
                        : p.State == ApprovalProcessState.Cancelled ? "Cancelled" : "NotStarted";
                    return new ApprovalLevelResponse(index, LocalizedTextDto.From(level.Name), level.Mode, state, level.Fallback,
                        [.. own.Select(d => new ApprovalDecisionResponse(d.Id, new PersonResponse(d.ApproverId, names.GetValueOrDefault(d.ApproverId)), d.State, d.Comment, d.DecidedAt))]);
                })]);
        })];
    }

    private static Microsoft.AspNetCore.Http.HttpResults.ProblemHttpResult NotFound() => FormsCommon.NotFound("approval");

    /// <summary>What summaries need, loaded once for a page.</summary>
    private sealed class SummaryContext
    {
        private Dictionary<Guid, Ticket> _tickets = [];
        private Dictionary<Guid, ApprovalProcess> _processes = [];
        private Dictionary<Guid, (Guid Id, string Code, Data.Localization.LocalizedText Name)> _services = [];
        private Dictionary<Guid, string?> _names = [];

        public static async Task<SummaryContext> LoadAsync(ZelvoDbContext db, IReadOnlyList<ApprovalDecision> decisions, CancellationToken cancellationToken)
        {
            var ticketIds = decisions.Select(d => d.TicketId).Distinct().ToList();
            var processIds = decisions.Select(d => d.ProcessId).Distinct().ToList();
            var tickets = await db.Tickets.AsNoTracking().Where(t => ticketIds.Contains(t.Id)).ToDictionaryAsync(t => t.Id, cancellationToken);
            var details = await db.ServiceRequests.AsNoTracking().Where(r => ticketIds.Contains(r.TicketId)).ToListAsync(cancellationToken);
            var serviceIds = details.Select(d => d.ServiceId).Distinct().ToList();
            var services = await db.CatalogServices.AsNoTracking().Where(s => serviceIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id, cancellationToken);
            return new SummaryContext
            {
                _tickets = tickets,
                _processes = await db.ApprovalProcesses.AsNoTracking().Where(p => processIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, cancellationToken),
                _services = details.Where(d => services.ContainsKey(d.ServiceId))
                    .ToDictionary(d => d.TicketId, d => (services[d.ServiceId].Id, services[d.ServiceId].Code, services[d.ServiceId].Name)),
                _names = await IncidentEndpoints.Names(db, tickets.Values.Select(t => t.RequesterId).Concat(decisions.Select(d => d.ApproverId)), cancellationToken),
            };
        }

        public ApprovalSummaryResponse From(ApprovalDecision d)
        {
            var ticket = _tickets[d.TicketId];
            var process = _processes[d.ProcessId];
            var levels = ApprovalPlanner.Read(process.Levels);
            var level = d.Level < levels.Count ? levels[d.Level].Name : new Data.Localization.LocalizedText(null, null);
            return new ApprovalSummaryResponse(
                d.Id, d.ProcessId, process.Stage, d.Level, LocalizedTextDto.From(level), d.State, d.Comment, d.CreatedAt, d.DecidedAt,
                new PersonResponse(d.ApproverId, _names.GetValueOrDefault(d.ApproverId)),
                new ApprovalTicketRef(ticket.Id, ticket.Number, ticket.Title, ticket.Type, ticket.State,
                    new PersonResponse(ticket.RequesterId, _names.GetValueOrDefault(ticket.RequesterId)),
                    _services.TryGetValue(ticket.Id, out var s) ? new ServiceRefResponse(s.Id, s.Code, LocalizedTextDto.From(s.Name)) : null,
                    ticket.CreatedAt));
        }
    }
}

/// <summary>Approve or reject.</summary>
internal enum ApprovalChoice
{
    /// <summary>Approve.</summary>
    Approve,

    /// <summary>Reject (with a reason).</summary>
    Reject,
}

/// <summary>A decision.</summary>
internal sealed class DecideApprovalRequest
{
    /// <summary>Approve or Reject.</summary>
    [Required]
    public ApprovalChoice? Decision { get; init; }

    /// <summary>A comment; the reason when rejecting (required then).</summary>
    [StringLength(ApprovalDecision.CommentMaxLength)]
    public string? Comment { get; init; }

    /// <summary>portal (default) or email, when the approver came from the email's buttons.</summary>
    [StringLength(10)]
    public string? Via { get; init; }
}

/// <summary>Stopping a pending approval.</summary>
internal sealed class CancelApprovalRequest
{
    /// <summary>Why.</summary>
    [StringLength(ApprovalProcess.TextMaxLength)]
    public string? Reason { get; init; }
}

/// <summary>The ticket an approval is about.</summary>
internal sealed record ApprovalTicketRef(
    Guid Id, string Number, string Title, TicketType Type, TicketState State, PersonResponse Requester, ServiceRefResponse? Service, DateTimeOffset CreatedAt);

/// <summary>A decision asked of someone.</summary>
internal sealed record ApprovalSummaryResponse(
    Guid Id, Guid ProcessId, ApprovalStage Stage, int Level, LocalizedTextDto LevelName, ApprovalDecisionState State, string? Comment,
    DateTimeOffset CreatedAt, DateTimeOffset? DecidedAt, PersonResponse Approver, ApprovalTicketRef Ticket);

/// <summary>A decision with what the approver needs to decide.</summary>
internal sealed record ApprovalDetailResponse(
    ApprovalSummaryResponse Approval, string? Description, JsonObject? Form, ApprovalProcessResponse Process, bool MayDecide);

/// <summary>An approver's decision in a level.</summary>
internal sealed record ApprovalDecisionResponse(Guid Id, PersonResponse Approver, ApprovalDecisionState State, string? Comment, DateTimeOffset? DecidedAt);

/// <summary>A level: Waiting, Approved, Rejected, NotStarted or Cancelled.</summary>
internal sealed record ApprovalLevelResponse(
    int Index, LocalizedTextDto Name, Zelvo.Workflows.Rules.ApprovalMode Mode, string State, bool Fallback, IReadOnlyList<ApprovalDecisionResponse> Decisions);

/// <summary>An approval process.</summary>
internal sealed record ApprovalProcessResponse(
    Guid Id, ApprovalStage Stage, ApprovalProcessState State, int CurrentLevel, string? Reason, DateTimeOffset CreatedAt, DateTimeOffset? EndedAt,
    IReadOnlyList<ApprovalLevelResponse> Levels);

/// <summary>A request's state after its approval was stopped.</summary>
internal sealed record ServiceRequestApprovalState(Guid Id, TicketState State, bool AwaitingApproval);
