using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;
using Zelvo.Api.Conventions;
using Zelvo.Api.Forms;
using Zelvo.Api.Identity;
using Zelvo.Api.Incidents;
using Zelvo.Api.Integrations;
using Zelvo.Api.ServiceDesk;
using Zelvo.Api.Tenancy;
using Zelvo.Api.Workflows;
using Zelvo.Data;
using Zelvo.Data.Catalog;
using Zelvo.Data.Identity;
using Zelvo.Data.Localization;
using Zelvo.Data.Tickets;
using Zelvo.Tickets;
using Zelvo.Tickets.Sla;

namespace Zelvo.Api.Catalog;

/// <summary>
/// Fulfilment tasks of service requests (task C4): staff who work on requests see their tasks and
/// their groups' tasks, start and complete them, and team leads and holders of requests.assign
/// give them to a group member. Completing the last task of a stage opens the next; completing the
/// last task resolves the request (or starts its sign-off). Every change is audited.
/// </summary>
internal static class RequestTaskEndpoints
{
    public static RouteGroupBuilder MapRequestTaskEndpoints(this RouteGroupBuilder group)
    {
        var tasks = group.MapGroup("/request-tasks").WithTags("Service requests").RequireTenant();
        tasks.MapGet("/", ListAsync).WithName("ListRequestTasks")
            .WithSummary("Open and in-progress tasks (state= for others): assignee=me (default) or none, group=mine or a group id, overdue=true. Needs requests.read.")
            .RequirePermission(Permissions.RequestsRead).Produces<PageResult<RequestTaskResponse>>();
        tasks.MapPost("/{id:guid}/start", StartAsync).WithName("StartRequestTask")
            .WithSummary("Start an open task; it is assigned to the caller when nobody has it. Its assignee or members of its group, with requests.edit; audited.")
            .RequirePerson().RequirePermission(Permissions.RequestsEdit).Produces<RequestTaskResponse>();
        tasks.MapPost("/{id:guid}/complete", CompleteAsync).WithName("CompleteRequestTask")
            .WithSummary("Mark an open or started task done, with notes. Opens the next stage, or resolves the request when every task is done (its sign-off first, if any). Needs requests.edit; audited.")
            .RequirePerson().RequirePermission(Permissions.RequestsEdit).Produces<RequestTaskResponse>().ProducesValidationProblem();
        tasks.MapPut("/{id:guid}/assignment", AssignAsync).WithName("AssignRequestTask")
            .WithSummary("Give a task to a member of its group (null: nobody). Holders of requests.assign, or the group's team leads; audited.")
            .RequirePerson().RequirePermission(Permissions.RequestsEdit).Produces<RequestTaskResponse>().ProducesValidationProblem();
        group.MapGet("/service-requests/{id:guid}/tasks", ForRequestAsync).WithName("ListServiceRequestTasks").WithTags("Service requests").RequireTenant()
            .WithSummary("The request's tasks in order, with their stage, group, assignee, state and due time. Needs requests.read.")
            .RequirePermission(Permissions.RequestsRead).Produces<IReadOnlyList<RequestTaskResponse>>();
        return group;
    }

    internal static async Task<IResult> ListAsync(
        [AsParameters] PageQuery page, [AsParameters] RequestTaskListQuery query, ZelvoDbContext db, CurrentUser user, CancellationToken cancellationToken)
    {
        var me = user.Account?.Id;
        var tasks = db.RequestTasks.AsNoTracking();
        tasks = query.State is { } state
            ? tasks.Where(t => t.State == state)
            : tasks.Where(t => t.State == RequestTaskState.Open || t.State == RequestTaskState.InProgress);
        if (query.Group is { } groupFilter)
        {
            tasks = groupFilter == "mine"
                ? tasks.Where(t => db.SupportGroupMembers.Any(m => m.GroupId == t.GroupId && m.UserId == me))
                : Guid.TryParse(groupFilter, out var groupId) ? tasks.Where(t => t.GroupId == groupId) : tasks.Where(_ => false);
        }
        else
        {
            tasks = query.Assignee switch
            {
                "none" => tasks.Where(t => t.AssigneeId == null),
                "any" => tasks,
                _ => tasks.Where(t => t.AssigneeId == me),
            };
        }

        if (query.Overdue == true)
        {
            var now = DateTimeOffset.UtcNow;
            tasks = tasks.Where(t => t.DueAt != null && t.DueAt < now);
        }

        var ordered = tasks.OrderBy(t => t.DueAt == null).ThenBy(t => t.DueAt).ThenBy(t => t.OpenedAt);
        var total = await ordered.LongCountAsync(cancellationToken);
        var rows = await ordered.Skip(page.Skip).Take(page.Size).ToListAsync(cancellationToken);
        return TypedResults.Ok(new PageResult<RequestTaskResponse>(await ResponsesAsync(db, user, rows, cancellationToken), page.Number, page.Size, total));
    }

    internal static async Task<IResult> ForRequestAsync(Guid id, ZelvoDbContext db, CurrentUser user, CancellationToken cancellationToken)
    {
        if (!await ServiceRequestEndpoints.Requests(db).AnyAsync(t => t.Id == id, cancellationToken))
        {
            return FormsCommon.NotFound("service-request");
        }

        var rows = await db.RequestTasks.AsNoTracking().Where(t => t.TicketId == id).OrderBy(t => t.Position).ToListAsync(cancellationToken);
        return TypedResults.Ok(await ResponsesAsync(db, user, rows, cancellationToken));
    }

    internal static async Task<IResult> StartAsync(Guid id, ZelvoDbContext db, CurrentUser user, SlaTracker sla, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var (task, ticket, problem) = await LockAsync(db, id, cancellationToken);
        if (problem is not null)
        {
            return problem;
        }

        var me = user.Account!.Id;
        if (!await MayWorkOnAsync(db, user, task!, cancellationToken))
        {
            return ApiProblem.Create(StatusCodes.Status403Forbidden, "forbidden", "Only members of the task's group work on it.");
        }

        if (task!.State != RequestTaskState.Open)
        {
            return ApiProblem.Create(StatusCodes.Status409Conflict, "task-not-open", "Only open tasks can be started.");
        }

        var now = DateTimeOffset.UtcNow;
        task.State = RequestTaskState.InProgress;
        task.StartedAt = now;
        task.AssigneeId ??= me;

        // Starting work on a task is starting work on the request (it stops the ownership clock).
        if (ticket!.AssigneeId is null && ticket.GroupId is { } group && await db.SupportGroupMembers.AnyAsync(m => m.GroupId == group && m.UserId == me, cancellationToken))
        {
            TicketWorkflow.Assign(ticket, me, now);
        }

        if (ticket.AssigneeId is not null && ticket.State is TicketState.New or TicketState.Assigned or TicketState.Reopened)
        {
            TicketWorkflow.Apply(ticket, TicketState.InProgress, null, null, now);
        }

        ticket.UpdatedAt = now;
        await sla.UpdateAsync([ticket], now, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return TypedResults.Ok((await ResponsesAsync(db, user, [task], cancellationToken)).Single());
    }

    internal static async Task<IResult> CompleteAsync(
        Guid id, CompleteTaskRequest? request, ZelvoDbContext db, CurrentUser user, ApprovalService approvals, SlaTracker sla, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var (task, ticket, problem) = await LockAsync(db, id, cancellationToken);
        if (problem is not null)
        {
            return problem;
        }

        if (!await MayWorkOnAsync(db, user, task!, cancellationToken))
        {
            return ApiProblem.Create(StatusCodes.Status403Forbidden, "forbidden", "Only members of the task's group work on it.");
        }

        if (task!.State is not (RequestTaskState.Open or RequestTaskState.InProgress))
        {
            return ApiProblem.Create(StatusCodes.Status409Conflict, "task-not-open", "Only open or started tasks can be completed.");
        }

        var now = DateTimeOffset.UtcNow;
        var me = user.Account!.Id;
        task.State = RequestTaskState.Done;
        task.Notes = string.IsNullOrWhiteSpace(request?.Notes) ? null : request.Notes.Trim();
        task.CompletedAt = now;
        task.CompletedById = me;
        task.AssigneeId ??= me;
        task.StartedAt ??= now;
        await db.SaveChangesAsync(cancellationToken);

        await RequestTasks.AdvanceAsync(db, approvals, ticket!, me, now, cancellationToken);
        ticket!.UpdatedAt = now;
        await sla.UpdateAsync([ticket], now, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return TypedResults.Ok((await ResponsesAsync(db, user, [task], cancellationToken)).Single());
    }

    internal static async Task<IResult> AssignAsync(Guid id, AssignTaskRequest request, ZelvoDbContext db, CurrentUser user, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var (task, _, problem) = await LockAsync(db, id, cancellationToken);
        if (problem is not null)
        {
            return problem;
        }

        var me = user.Account!.Id;
        var lead = task!.GroupId is { } g && await db.SupportGroupMembers.AnyAsync(m => m.GroupId == g && m.UserId == me && m.IsLead, cancellationToken);
        if (!user.Has(Permissions.RequestsAssign) && !lead)
        {
            return ApiProblem.Create(StatusCodes.Status403Forbidden, "forbidden", "Only team leads of the task's group or holders of requests.assign give tasks to people.");
        }

        if (task.State is RequestTaskState.Done or RequestTaskState.Cancelled)
        {
            return ApiProblem.Create(StatusCodes.Status409Conflict, "task-not-open", "Done or cancelled tasks cannot be given to someone.");
        }

        if (request.AssigneeId is { } assignee && (task.GroupId is not { } group
            || !await db.SupportGroupMembers.AnyAsync(m => m.GroupId == group && m.UserId == assignee && m.User!.Status == UserStatus.Active, cancellationToken)))
        {
            return UserEndpoints.Validation("assigneeId", "The assignee is not an active member of the task's group.");
        }

        task.AssigneeId = request.AssigneeId;
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return TypedResults.Ok((await ResponsesAsync(db, user, [task], cancellationToken)).Single());
    }

    // ---- helpers ----

    /// <summary>The task and its request, with the request's row locked so that tasks finishing at the same time advance the stages once.</summary>
    private static async Task<(RequestTask? Task, Ticket? Ticket, IResult? Problem)> LockAsync(ZelvoDbContext db, Guid id, CancellationToken cancellationToken)
    {
        var ticketId = await db.RequestTasks.Where(t => t.Id == id).Select(t => (Guid?)t.TicketId).SingleOrDefaultAsync(cancellationToken);
        if (ticketId is not { } tid)
        {
            return (null, null, FormsCommon.NotFound("request-task"));
        }

        await db.Database.ExecuteSqlAsync($"SELECT 1 FROM tickets WHERE id = {tid} FOR UPDATE", cancellationToken);
        var ticket = await db.Tickets.Include(t => t.Incident).SingleAsync(t => t.Id == tid, cancellationToken);
        var task = await db.RequestTasks.SingleAsync(t => t.Id == id, cancellationToken);
        if (ticket.State == TicketState.Closed)
        {
            return (null, null, IncidentEndpoints.Closed());
        }

        return (task, ticket, null);
    }

    private static async Task<bool> MayWorkOnAsync(ZelvoDbContext db, CurrentUser user, RequestTask task, CancellationToken cancellationToken)
    {
        // Doing a task is for its assignee and its group's members; requests.assign gives tasks to people.
        if (task.AssigneeId == user.Account?.Id)
        {
            return true;
        }

        var me = user.Account?.Id;
        return task.GroupId is { } group && await db.SupportGroupMembers.AnyAsync(m => m.GroupId == group && m.UserId == me, cancellationToken);
    }

    private static async Task<List<RequestTaskResponse>> ResponsesAsync(ZelvoDbContext db, CurrentUser user, IReadOnlyList<RequestTask> tasks, CancellationToken cancellationToken)
    {
        var ticketIds = tasks.Select(t => t.TicketId).Distinct().ToList();
        var tickets = await db.Tickets.AsNoTracking().Where(t => ticketIds.Contains(t.Id)).ToDictionaryAsync(t => t.Id, cancellationToken);
        var groupIds = tasks.Select(t => t.GroupId).OfType<Guid>().Distinct().ToList();
        var groups = await db.SupportGroups.AsNoTracking().Where(g => groupIds.Contains(g.Id)).ToDictionaryAsync(g => g.Id, g => g.Name, cancellationToken);
        var names = await IncidentEndpoints.Names(db, tasks.Select(t => t.AssigneeId ?? Guid.Empty).Concat(tasks.Select(t => t.CompletedById ?? Guid.Empty)), cancellationToken);
        var me = user.Account?.Id;
        var mine = await db.SupportGroupMembers.AsNoTracking().Where(m => m.UserId == me).Select(m => m.GroupId).ToListAsync(cancellationToken);
        var leads = await db.SupportGroupMembers.AsNoTracking().Where(m => m.UserId == me && m.IsLead).Select(m => m.GroupId).ToListAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow;
        return [.. tasks.Select(t =>
        {
            var ticket = tickets.GetValueOrDefault(t.TicketId);
            var mayWork = user.Has(Permissions.RequestsEdit) && (t.AssigneeId == me || (t.GroupId is { } g && mine.Contains(g)));
            var mayAssign = user.Has(Permissions.RequestsAssign) || (t.GroupId is { } lg && leads.Contains(lg));
            return new RequestTaskResponse(
                t.Id, t.TicketId, ticket?.Number ?? string.Empty, ticket?.Title ?? string.Empty, t.Key, LocalizedTextDto.From(t.Name), LocalizedTextDto.From(t.Description),
                t.Position, t.Stage, t.State,
                t.GroupId is { } gid && groups.TryGetValue(gid, out var gname) ? NamedRef.From(gid, gname) : null,
                t.AssigneeId is { } a ? new PersonResponse(a, names.GetValueOrDefault(a)) : null,
                t.TargetMinutes, t.DueAt, t.DueAt is { } due && due < now && t.State is RequestTaskState.Open or RequestTaskState.InProgress,
                t.Notes, t.OpenedAt, t.StartedAt, t.CompletedAt,
                t.CompletedById is { } c ? new PersonResponse(c, names.GetValueOrDefault(c)) : null,
                mayWork && t.State is RequestTaskState.Open or RequestTaskState.InProgress, mayAssign && t.State is not (RequestTaskState.Done or RequestTaskState.Cancelled),
                $"\"{t.Version}\"");
        })];
    }
}

/// <summary>Filters of the task list.</summary>
internal sealed class RequestTaskListQuery
{
    /// <summary>me (default), none or any.</summary>
    [StringLength(10)]
    public string? Assignee { get; init; }

    /// <summary>mine or a group id (then the assignee filter is not used).</summary>
    [StringLength(40)]
    public string? Group { get; init; }

    /// <summary>Only this state (default: open and in progress).</summary>
    public RequestTaskState? State { get; init; }

    /// <summary>Only tasks past their due time.</summary>
    public bool? Overdue { get; init; }
}

/// <summary>Completing a task.</summary>
internal sealed class CompleteTaskRequest
{
    /// <summary>What was done.</summary>
    [StringLength(RequestTask.NotesMaxLength)]
    public string? Notes { get; init; }
}

/// <summary>Giving a task to someone.</summary>
internal sealed class AssignTaskRequest
{
    /// <summary>A member of the task's group; null: nobody.</summary>
    public Guid? AssigneeId { get; init; }
}

/// <summary>A fulfilment task.</summary>
internal sealed record RequestTaskResponse(
    Guid Id, Guid RequestId, string RequestNumber, string RequestTitle, string Key, LocalizedTextDto Name, LocalizedTextDto Description, int Position, int Stage,
    RequestTaskState State, NamedRef? Group, PersonResponse? Assignee, int? TargetMinutes, DateTimeOffset? DueAt, bool Overdue, string? Notes,
    DateTimeOffset? OpenedAt, DateTimeOffset? StartedAt, DateTimeOffset? CompletedAt, PersonResponse? CompletedBy, bool MayWork, bool MayAssign, string Etag);
