using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;
using Zelvo.Api.Catalog;
using Zelvo.Api.Conventions;
using Zelvo.Api.Forms;
using Zelvo.Api.Identity;
using Zelvo.Api.Incidents;
using Zelvo.Api.Integrations;
using Zelvo.Api.ServiceDesk;
using Zelvo.Api.Settings;
using Zelvo.Api.Tenancy;
using Zelvo.Api.Workflows;
using Zelvo.Data;
using Zelvo.Data.Auditing;
using Zelvo.Data.Catalog;
using Zelvo.Data.Changes;
using Zelvo.Data.Localization;
using Zelvo.Data.Tenancy;
using Zelvo.Data.Tickets;
using Zelvo.Search;
using Zelvo.Tickets;

namespace Zelvo.Api.Changes;

/// <summary>Where a change can be moved by <c>POST /changes/{id}/transitions</c>.</summary>
public enum ChangeTarget
{
    /// <summary>Back to planning; a pending approval is stopped.</summary>
    Replan,

    /// <summary>Start implementing (needs an assignee); its tasks open.</summary>
    StartImplementation,

    /// <summary>Implemented, with notes (its tasks done); the review is due.</summary>
    Implemented,

    /// <summary>Withdrawn before implementation, with a reason.</summary>
    Cancel,
}

/// <summary>
/// Change management (module 17, task H3). A record type <c>Change</c> (<c>CHG-000123</c>) on the
/// shared ticket core: standard changes from the catalog of pre-approved templates, normal changes
/// assessed with the risk questionnaire, and emergency changes, each with its approval path (C3);
/// affected configuration items (G1); implementation, test and back-out plans; tasks for each team
/// (C4's task model); the planned window with conflict detection and blackout periods; the
/// post-implementation review; links to the problems a change fixes and the incidents it caused.
/// </summary>
internal static class ChangeEndpoints
{
    /// <summary>Most configuration items given in one call.</summary>
    public const int MaxItems = 50;

    /// <summary>Most tasks of a change.</summary>
    public const int MaxTasks = 50;

    public static RouteGroupBuilder MapChangeEndpoints(this RouteGroupBuilder group)
    {
        var changes = group.MapGroup("/changes").WithTags("Changes").RequireTenant();
        changes.MapPost("/", CreateAsync).WithName("CreateChange")
            .WithSummary("Raise a change: Standard (from an active standard change template, whose plans, category, group and tasks it takes), Normal or Emergency, with its plans, planned window and configuration items. Routed to the template's group or by the assignment rules. Needs changes.edit; audited.")
            .RequirePerson().RequirePermission(Permissions.ChangesEdit).Produces<ChangeResponse>(StatusCodes.Status201Created).ProducesValidationProblem();
        changes.MapGet("/", ListAsync).WithName("ListChanges")
            .WithSummary("Changes, newest first. Filters stage, kind, open, number, riskLevel, assignee (me, none or an id), group (mine, none or an id), configurationItemId and the planned window (from, to). Needs changes.read.")
            .RequirePermission(Permissions.ChangesRead).Produces<PageResult<ChangeResponse>>();
        changes.MapGet("/assignees", AssigneesAsync).WithName("ListChangeAssignees")
            .WithSummary("Active users who can be assigned changes (their roles hold changes.edit); with groupId, only that group's members. Needs changes.read.")
            .RequirePermission(Permissions.ChangesRead).Produces<IReadOnlyList<PersonResponse>>();
        changes.MapGet("/{id:guid}", GetAsync).WithName("GetChange")
            .WithSummary("One change with its conflicts (other open changes on the same items in an overlapping window, blackout periods) and approval. Returns an ETag to send as If-Match with changes. Needs changes.read.")
            .RequirePermission(Permissions.ChangesRead).Produces<ChangeResponse>();
        changes.MapPatch("/{id:guid}", UpdateAsync).WithName("UpdateChange")
            .WithSummary("Change the title, description or category; while the change is planned (New) also its plans and planned window. Needs changes.edit; audited.")
            .RequirePermission(Permissions.ChangesEdit).Produces<ChangeResponse>().ProducesValidationProblem();
        changes.MapPut("/{id:guid}/risk", RiskAsync).WithName("SetChangeRisk")
            .WithSummary("Answer the risk and impact questionnaire (question key → answer key); the score and risk level follow from Settings → Changes. Normal and emergency changes while planned. Needs changes.edit; audited.")
            .RequirePermission(Permissions.ChangesEdit).Produces<ChangeResponse>().ProducesValidationProblem();
        changes.MapPost("/{id:guid}/submit", SubmitAsync).WithName("SubmitChange")
            .WithSummary("Submit a planned change: it needs a window, implementation and back-out plans and (normal changes) every active question answered. Standard changes are scheduled at once, except in a blackout period (409 blackout); normal and emergency changes start the approval path of their risk level or the emergency path. Needs changes.edit; audited.")
            .RequirePerson().RequirePermission(Permissions.ChangesEdit).Produces<ChangeResponse>().ProducesValidationProblem();
        changes.MapPost("/{id:guid}/transitions", TransitionAsync).WithName("MoveChange")
            .WithSummary("Replan (back to planning, stopping its approval), StartImplementation (Scheduled; needs an assignee; its tasks open), Implemented (with notes; every task done or cancelled) or Cancel (before implementation, with a reason). Needs changes.edit; audited.")
            .RequirePerson().RequirePermission(Permissions.ChangesEdit).Produces<ChangeResponse>().ProducesValidationProblem();
        changes.MapPost("/{id:guid}/review", ReviewAsync).WithName("ReviewChange")
            .WithSummary("Record the post-implementation review of an implemented change (outcome Successful, SuccessfulWithProblems, Failed or BackedOut; notes needed unless successful) and close it. Needs changes.manage; audited.")
            .RequirePerson().RequirePermission(Permissions.ChangesManage).Produces<ChangeResponse>().ProducesValidationProblem();
        changes.MapPut("/{id:guid}/assignment", AssignAsync).WithName("SetChangeAssignment")
            .WithSummary("Set the group and assignee (a member of the group whose roles hold changes.edit). Needs changes.manage; audited.")
            .RequirePermission(Permissions.ChangesManage).Produces<ChangeResponse>().ProducesValidationProblem();
        changes.MapPost("/{id:guid}/route", RouteAgainAsync).WithName("RouteChange")
            .WithSummary("Run the assignment rules again. Open changes only; needs changes.manage; audited.")
            .RequirePermission(Permissions.ChangesManage).Produces<ChangeResponse>();
        changes.MapGet("/{id:guid}/approvals", ApprovalsAsync).WithName("ListChangeApprovals")
            .WithSummary("The change's approval processes with their levels and decisions. Needs changes.read.")
            .RequirePermission(Permissions.ChangesRead).Produces<IReadOnlyList<ApprovalProcessResponse>>();
        changes.MapGet("/{id:guid}/comments", CommentsAsync).WithName("ListChangeComments")
            .WithSummary("The change's notes, oldest first. Needs changes.read.")
            .RequirePermission(Permissions.ChangesRead).Produces<IReadOnlyList<ChangeCommentResponse>>();
        changes.MapPost("/{id:guid}/comments", CommentAsync).WithName("CreateChangeComment")
            .WithSummary("Add an internal note. Needs changes.edit; audited.")
            .RequirePerson().RequirePermission(Permissions.ChangesEdit).Produces<ChangeCommentResponse>(StatusCodes.Status201Created).ProducesValidationProblem();
        changes.MapGet("/{id:guid}/tasks", TasksAsync).WithName("ListChangeTasks")
            .WithSummary("The change's tasks for each team in order (C4's task model). They wait until implementation starts. Needs changes.read.")
            .RequirePermission(Permissions.ChangesRead).Produces<IReadOnlyList<RequestTaskResponse>>();
        changes.MapPost("/{id:guid}/tasks", AddTaskAsync).WithName("AddChangeTask")
            .WithSummary("Add a task for a team (after the others, or together with the last one). Before the review; needs changes.edit; audited.")
            .RequirePermission(Permissions.ChangesEdit).Produces<IReadOnlyList<RequestTaskResponse>>().ProducesValidationProblem();
        changes.MapDelete("/{id:guid}/tasks/{taskId:guid}", RemoveTaskAsync).WithName("RemoveChangeTask")
            .WithSummary("Remove a task that has not opened yet. Needs changes.edit; audited.")
            .RequirePermission(Permissions.ChangesEdit).Produces(StatusCodes.Status204NoContent);
        changes.MapGet("/{id:guid}/links", LinksAsync).WithName("ListChangeLinks")
            .WithSummary("Problems the change fixes and incidents it caused. Needs changes.read.")
            .RequirePermission(Permissions.ChangesRead).Produces<IReadOnlyList<ChangeLinkResponse>>();
        changes.MapPost("/{id:guid}/links", LinkAsync).WithName("LinkChangeTicket")
            .WithSummary("Link a problem (the change fixes it) or an incident (the change caused it) by its number. Needs changes.edit; audited on both.")
            .RequirePermission(Permissions.ChangesEdit).Produces<IReadOnlyList<ChangeLinkResponse>>().ProducesValidationProblem();
        changes.MapDelete("/{id:guid}/links/{ticketId:guid}", UnlinkAsync).WithName("UnlinkChangeTicket")
            .WithSummary("Unlink a problem or incident. Needs changes.edit; audited.")
            .RequirePermission(Permissions.ChangesEdit).Produces(StatusCodes.Status204NoContent);

        group.MapGet("/problems/{id:guid}/changes", (Guid id, ZelvoDbContext db, CurrentUser user, CancellationToken ct) => OfTicketAsync(TicketType.Problem, id, db, user, ct))
            .WithName("ListProblemChanges").WithTags("Changes").RequireTenant()
            .WithSummary("The changes that fix a problem. Needs problems.read and changes.read.")
            .RequirePermission(Permissions.ProblemsRead).RequirePermission(Permissions.ChangesRead).Produces<IReadOnlyList<ChangeResponse>>();
        group.MapGet("/incidents/{id:guid}/changes", (Guid id, ZelvoDbContext db, CurrentUser user, CancellationToken ct) => OfTicketAsync(TicketType.Incident, id, db, user, ct))
            .WithName("ListIncidentChanges").WithTags("Changes").RequireTenant()
            .WithSummary("The changes an incident was caused by. Needs incidents.read and changes.read.")
            .RequirePermission(Permissions.IncidentsRead).RequirePermission(Permissions.ChangesRead).Produces<IReadOnlyList<ChangeResponse>>();
        return group;
    }

    internal static async Task<IResult> CreateAsync(
        CreateChangeRequest request, HttpContext http, ZelvoDbContext db, CurrentUser user, ITenantContext tenant, TenantSettingsStore settings,
        IAuditLog audit, ISearchIndexer indexer, CancellationToken cancellationToken)
    {
        var kind = request.Kind!.Value;
        ChangeTemplate? template = null;
        if (kind == ChangeKind.Standard)
        {
            template = request.TemplateId is { } templateId ? await db.ChangeTemplates.AsNoTracking().SingleOrDefaultAsync(t => t.Id == templateId && t.Active, cancellationToken) : null;
            if (template is null)
            {
                return UserEndpoints.Validation("templateId", "A standard change comes from an active standard change template.");
            }
        }

        if (request.CategoryId is { } category && !await TreeEndpoints.IsSelectableAsync(db.Categories, category, cancellationToken))
        {
            return UserEndpoints.Validation("categoryId", "Choose an active category.");
        }

        if (WindowProblem(request.PlannedStart, request.PlannedEnd) is { } windowProblem)
        {
            return windowProblem;
        }

        var items = (request.ConfigurationItemIds ?? []).Distinct().ToList();
        if (await db.ConfigurationItems.CountAsync(i => items.Contains(i.Id), cancellationToken) != items.Count)
        {
            return UserEndpoints.Validation("configurationItemIds", "Every configuration item must exist.");
        }

        var title = string.IsNullOrWhiteSpace(request.Title) ? template?.Name.En ?? template?.Name.Ar : request.Title;
        if (string.IsNullOrWhiteSpace(title))
        {
            return UserEndpoints.Validation("title", "Give a title.");
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var me = user.Account!.Id;
        var ticket = await ChangeCore.CreateAsync(db, settings, tenant, indexer, new ChangeDraft(
            title, request.Description ?? template?.Description.En ?? template?.Description.Ar, kind, me, request.CategoryId, template,
            request.PlannedStart, request.PlannedEnd, request.ImplementationPlan, request.TestPlan, request.BackoutPlan), cancellationToken);
        foreach (var item in items)
        {
            db.TicketConfigurationItems.Add(new Data.Cmdb.TicketConfigurationItem { TicketId = ticket.Id, ConfigurationItemId = item, AddedById = me });
        }

        audit.Add(AuditCategory.Change, "changes.created", "ticket", ticket.Id.ToString(), new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["number"] = ticket.Number,
            ["kind"] = kind.ToString(),
            ["templateId"] = template?.Id.ToString(),
            ["items"] = items.Count,
        });
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return TypedResults.Created($"/api/v1/changes/{ticket.Id}", await ChangeCore.ResponseAsync(http, db, user, ticket.Id, cancellationToken));
    }

    internal static async Task<IResult> ListAsync(
        [AsParameters] PageQuery page, [AsParameters] ChangeListQuery query, ZelvoDbContext db, CurrentUser user, CancellationToken cancellationToken)
    {
        var me = user.Account?.Id;
        var tickets = ChangeCore.Changes(db).AsNoTracking();
        if (query.Stage is { } stage)
        {
            tickets = stage switch
            {
                ChangeStage.Closed => tickets.Where(t => t.State == TicketState.Closed),
                ChangeStage.Review => tickets.Where(t => t.State == TicketState.Resolved),
                _ => PhaseFilter(tickets, stage switch
                {
                    ChangeStage.Authorisation => ChangePhase.Authorisation,
                    ChangeStage.Scheduled => ChangePhase.Scheduled,
                    ChangeStage.Implementation => ChangePhase.Implementation,
                    _ => ChangePhase.Planning,
                }),
            };
        }
        else if (query.Open == true)
        {
            tickets = tickets.Where(t => t.State != TicketState.Closed);
        }

        if (query.Kind is { } kind)
        {
            tickets = tickets.Where(t => t.Change!.Kind == kind);
        }

        if (query.RiskLevel is { } risk)
        {
            tickets = tickets.Where(t => t.Change!.RiskLevel == risk);
        }

        if (query.Number is { Length: > 0 } number)
        {
            var wanted = number.Trim().ToUpperInvariant();
            tickets = tickets.Where(t => t.Number == wanted);
        }

        if (query.ConfigurationItemId is { } item)
        {
            tickets = tickets.Where(t => db.TicketConfigurationItems.Any(l => l.TicketId == t.Id && l.ConfigurationItemId == item));
        }

        if (query.From is { } from)
        {
            tickets = tickets.Where(t => t.Change!.PlannedEnd > from);
        }

        if (query.To is { } to)
        {
            tickets = tickets.Where(t => t.Change!.PlannedStart < to);
        }

        tickets = query.Assignee switch
        {
            "me" => tickets.Where(t => t.AssigneeId == me),
            "none" => tickets.Where(t => t.AssigneeId == null),
            { } a when Guid.TryParse(a, out var assigneeId) => tickets.Where(t => t.AssigneeId == assigneeId),
            _ => tickets,
        };
        tickets = query.Group switch
        {
            "mine" => tickets.Where(t => db.SupportGroupMembers.Any(m => m.GroupId == t.GroupId && m.UserId == me)),
            "none" => tickets.Where(t => t.GroupId == null),
            { } g when Guid.TryParse(g, out var groupId) => tickets.Where(t => t.GroupId == groupId),
            _ => tickets,
        };

        var ordered = tickets.OrderByDescending(t => t.CreatedAt).ThenByDescending(t => t.Id);
        var total = await ordered.LongCountAsync(cancellationToken);
        var rows = await ordered.Skip(page.Skip).Take(page.Size).ToListAsync(cancellationToken);
        var context = await ChangeResponseContext.LoadAsync(db, user, rows, cancellationToken);
        return TypedResults.Ok(new PageResult<ChangeResponse>([.. rows.Select(context.From)], page.Number, page.Size, total));
    }

    internal static async Task<IResult> AssigneesAsync(Guid? groupId, ZelvoDbContext db, CancellationToken cancellationToken)
    {
        var people = ChangeCore.Assignable(db);
        if (groupId is { } g)
        {
            people = people.Where(u => db.SupportGroupMembers.Any(m => m.GroupId == g && m.UserId == u.Id));
        }

        return TypedResults.Ok(await people.OrderBy(u => u.DisplayName ?? u.Email).Take(500).Select(u => new PersonResponse(u.Id, u.DisplayName)).ToListAsync(cancellationToken));
    }

    internal static async Task<IResult> GetAsync(Guid id, HttpContext http, ZelvoDbContext db, CurrentUser user, CancellationToken cancellationToken) =>
        await db.Tickets.AnyAsync(t => t.Id == id && t.Type == TicketType.Change, cancellationToken)
            ? TypedResults.Ok(await ChangeCore.ResponseAsync(http, db, user, id, cancellationToken))
            : ChangeCore.NotFound();

    internal static async Task<IResult> UpdateAsync(
        Guid id, UpdateChangeRequest request, HttpContext http, ZelvoDbContext db, CurrentUser user, ISearchIndexer indexer, CancellationToken cancellationToken)
    {
        var (ticket, problem) = await LoadAsync(http, db, id, cancellationToken);
        if (problem is not null)
        {
            return problem;
        }

        var change = ticket!.Change!;
        var planning = request.ImplementationPlan is not null || request.TestPlan is not null || request.BackoutPlan is not null
            || request.PlannedStart is not null || request.PlannedEnd is not null || request.ClearWindow == true;
        if (planning && !ChangeCore.IsPlanning(ticket))
        {
            return NotPlanning();
        }

        var categoryId = request.ClearCategory == true ? null : request.CategoryId ?? ticket.CategoryId;
        if (categoryId is { } category && category != ticket.CategoryId && !await TreeEndpoints.IsSelectableAsync(db.Categories, category, cancellationToken))
        {
            return UserEndpoints.Validation("categoryId", "Choose an active category.");
        }

        var start = request.ClearWindow == true ? null : request.PlannedStart ?? change.PlannedStart;
        var end = request.ClearWindow == true ? null : request.PlannedEnd ?? change.PlannedEnd;
        if (WindowProblem(start, end) is { } windowProblem)
        {
            return windowProblem;
        }

        if (request.Title is { } title)
        {
            if (string.IsNullOrWhiteSpace(title))
            {
                return UserEndpoints.Validation("title", "Give a title.");
            }

            ticket.Title = title.Trim();
        }

        if (request.Description is { } description)
        {
            ticket.Description = ChangeCore.Clean(description);
        }

        if (request.ImplementationPlan is { } implementation)
        {
            change.ImplementationPlan = ChangeCore.Clean(implementation);
        }

        if (request.TestPlan is { } test)
        {
            change.TestPlan = ChangeCore.Clean(test);
        }

        if (request.BackoutPlan is { } backout)
        {
            change.BackoutPlan = ChangeCore.Clean(backout);
        }

        change.PlannedStart = start;
        change.PlannedEnd = end;
        ticket.CategoryId = categoryId;
        ticket.UpdatedAt = DateTimeOffset.UtcNow;
        return await ChangeCore.SaveAsync(http, db, user, indexer, ticket, cancellationToken);
    }

    internal static async Task<IResult> RiskAsync(
        Guid id, ChangeRiskRequest request, HttpContext http, ZelvoDbContext db, CurrentUser user, TenantSettingsStore settings, ISearchIndexer indexer,
        CancellationToken cancellationToken)
    {
        var (ticket, problem) = await LoadAsync(http, db, id, cancellationToken);
        if (problem is not null)
        {
            return problem;
        }

        var change = ticket!.Change!;
        if (!ChangeCore.IsPlanning(ticket))
        {
            return NotPlanning();
        }

        if (change.Kind == ChangeKind.Standard)
        {
            return ApiProblem.Create(StatusCodes.Status409Conflict, "standard-change", "Standard changes are pre-approved and low risk; they are not assessed.");
        }

        var value = await settings.GetAsync(ChangeSettings.Changes, cancellationToken);
        var answers = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, option) in request.Answers ?? new Dictionary<string, string>())
        {
            var question = value.Questions.FirstOrDefault(q => q.Key == key && q.Active);
            if (question is null || question.Options.All(o => o.Key != option))
            {
                return UserEndpoints.Validation($"answers.{key}", "Answer with one of the question's options.");
            }

            answers[key] = option;
        }

        var (score, level, _) = value.Assess(answers);
        change.RiskAnswers = ChangeCore.WriteAnswers(answers);
        change.RiskScore = score;
        change.RiskLevel = level;
        ticket.UpdatedAt = DateTimeOffset.UtcNow;
        return await ChangeCore.SaveAsync(http, db, user, indexer, ticket, cancellationToken);
    }

    internal static async Task<IResult> SubmitAsync(
        Guid id, HttpContext http, ZelvoDbContext db, CurrentUser user, TenantSettingsStore settings, ApprovalService approvals, IAuditLog audit,
        ISearchIndexer indexer, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var (ticket, problem) = await LoadAsync(http, db, id, cancellationToken);
        if (problem is not null)
        {
            return problem;
        }

        var change = ticket!.Change!;
        if (!ChangeCore.IsPlanning(ticket))
        {
            return NotPlanning();
        }

        if (change.PlannedStart is null || change.PlannedEnd is null)
        {
            return UserEndpoints.Validation("plannedStart", "Plan the change's window first.");
        }

        if (change.ImplementationPlan is null)
        {
            return UserEndpoints.Validation("implementationPlan", "Write the implementation plan first.");
        }

        if (change.BackoutPlan is null)
        {
            return UserEndpoints.Validation("backoutPlan", "Write the back-out plan first.");
        }

        var value = await settings.GetAsync(ChangeSettings.Changes, cancellationToken);
        if (change.Kind != ChangeKind.Standard)
        {
            var (score, level, unanswered) = value.Assess(ChangeCore.ReadAnswers(change.RiskAnswers));
            if (change.Kind == ChangeKind.Normal && unanswered.Count > 0)
            {
                return UserEndpoints.Validation("answers", $"Answer every question of the risk questionnaire first ({string.Join(", ", unanswered)}).");
            }

            change.RiskScore = score;
            change.RiskLevel = level;
        }

        var conflicts = await ChangeConflicts.ForAsync(db, ticket, cancellationToken);
        var now = DateTimeOffset.UtcNow;
        if (await ChangeApprovals.SubmitAsync(db, approvals, audit, ticket, value, conflicts, user.Account!.Id, now, cancellationToken) == SubmitError.Blackout)
        {
            return ApiProblem.Create(StatusCodes.Status409Conflict, "blackout",
                "The window falls in a blackout period. Plan another window, or raise a normal or emergency change so that it is reviewed.");
        }

        return await ChangeCore.SaveAsync(http, db, user, indexer, ticket, cancellationToken);
    }

    internal static async Task<IResult> TransitionAsync(
        Guid id, ChangeTransitionRequest request, HttpContext http, ZelvoDbContext db, CurrentUser user, ApprovalService approvals, ISearchIndexer indexer,
        CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var (ticket, problem) = await LoadAsync(http, db, id, cancellationToken);
        if (problem is not null)
        {
            return problem;
        }

        var to = request.To!.Value;
        var action = to switch
        {
            ChangeTarget.Replan => ChangeAction.Replan,
            ChangeTarget.StartImplementation => ChangeAction.StartImplementation,
            ChangeTarget.Implemented => ChangeAction.Implemented,
            _ => ChangeAction.Cancel,
        };
        var stage = ChangeCore.StageOf(ticket!);
        if (!ChangeCore.NextActions(ticket!, true, true).Contains(action))
        {
            return ApiProblem.Create(StatusCodes.Status409Conflict, "transition-not-allowed", $"The change cannot move from {stage} to {to}.");
        }

        var change = ticket!.Change!;
        var now = DateTimeOffset.UtcNow;
        switch (to)
        {
            case ChangeTarget.Replan:
                await approvals.CancelAsync(ticket, "Replanned", cancellationToken);
                change.Phase = ChangePhase.Planning;
                change.ApprovedAt = null;
                // Task H5: a new approval sees a new simulation.
                ChangeSimulator.Forget(change);
                break;
            case ChangeTarget.StartImplementation:
                if (TicketWorkflow.Check(ticket, TicketState.InProgress, null, null) == TransitionError.AssigneeRequired)
                {
                    return ApiProblem.Create(StatusCodes.Status409Conflict, "assignee-required", "Assign the change before implementing it.");
                }

                TicketWorkflow.Apply(ticket, TicketState.InProgress, null, null, now);
                change.Phase = ChangePhase.Implementation;
                change.ActualStart = now;
                await RequestTasks.OpenNextStageAsync(db, ticket.Id, now, cancellationToken);
                break;
            case ChangeTarget.Implemented:
                if (string.IsNullOrWhiteSpace(request.Notes))
                {
                    return UserEndpoints.Validation("notes", "Say how the implementation went.");
                }

                if (await db.RequestTasks.AnyAsync(t => t.TicketId == ticket.Id
                    && (t.State == RequestTaskState.Waiting || t.State == RequestTaskState.Open || t.State == RequestTaskState.InProgress), cancellationToken))
                {
                    return ApiProblem.Create(StatusCodes.Status409Conflict, "tasks-open", "Finish the change's tasks first.");
                }

                TicketWorkflow.Apply(ticket, TicketState.Resolved, null, request.Notes.Trim(), now);
                change.ActualEnd = now;
                break;
            case ChangeTarget.Cancel:
                if (string.IsNullOrWhiteSpace(request.Notes))
                {
                    return UserEndpoints.Validation("notes", "Say why the change is cancelled.");
                }

                await approvals.CancelAsync(ticket, "Cancelled", cancellationToken);
                await RequestTasks.CancelOpenAsync(db, ticket.Id, now, cancellationToken);
                ticket.State = TicketState.Closed;
                ticket.ResolutionNotes = request.Notes.Trim();
                ticket.ClosedAt = now;
                change.Closure = ChangeClosure.Cancelled;
                break;
        }

        ticket.UpdatedAt = now;
        return await ChangeCore.SaveAsync(http, db, user, indexer, ticket, cancellationToken);
    }

    internal static async Task<IResult> ReviewAsync(
        Guid id, ChangeReviewRequest request, HttpContext http, ZelvoDbContext db, CurrentUser user, ISearchIndexer indexer, CancellationToken cancellationToken)
    {
        var (ticket, problem) = await LoadAsync(http, db, id, cancellationToken);
        if (problem is not null)
        {
            return problem;
        }

        if (ChangeCore.StageOf(ticket!) != ChangeStage.Review)
        {
            return ApiProblem.Create(StatusCodes.Status409Conflict, "not-implemented", "Only implemented changes are reviewed.");
        }

        var outcome = request.Outcome!.Value;
        if (outcome != ChangeOutcome.Successful && string.IsNullOrWhiteSpace(request.Notes))
        {
            return UserEndpoints.Validation("notes", "Say what went wrong and what was learned.");
        }

        var now = DateTimeOffset.UtcNow;
        var change = ticket!.Change!;
        change.Outcome = outcome;
        change.ReviewNotes = ChangeCore.Clean(request.Notes);
        change.ReviewedById = user.Account!.Id;
        change.ReviewedAt = now;
        change.Closure = ChangeClosure.Completed;
        TicketWorkflow.Apply(ticket, TicketState.Closed, null, null, now);
        ticket.UpdatedAt = now;
        return await ChangeCore.SaveAsync(http, db, user, indexer, ticket, cancellationToken);
    }

    internal static async Task<IResult> AssignAsync(
        Guid id, ChangeAssignmentRequest request, HttpContext http, ZelvoDbContext db, CurrentUser user, ISearchIndexer indexer, CancellationToken cancellationToken)
    {
        var (ticket, problem) = await LoadAsync(http, db, id, cancellationToken);
        if (problem is not null)
        {
            return problem;
        }

        if (request.GroupId is { } groupId && groupId != ticket!.GroupId && !await db.SupportGroups.AnyAsync(g => g.Id == groupId && g.Active, cancellationToken))
        {
            return UserEndpoints.Validation("groupId", "Choose an active group.");
        }

        if (request.AssigneeId is { } assigneeId && !await ChangeCore.Assignable(db).AnyAsync(u => u.Id == assigneeId, cancellationToken))
        {
            return UserEndpoints.Validation("assigneeId", "The assignee cannot be assigned changes.");
        }

        if (request.GroupId is { } group && request.AssigneeId is { } member
            && !await db.SupportGroupMembers.AnyAsync(m => m.GroupId == group && m.UserId == member, cancellationToken))
        {
            return UserEndpoints.Validation("assigneeId", "The assignee is not a member of the group.");
        }

        var now = DateTimeOffset.UtcNow;
        ticket!.GroupId = request.GroupId;
        ticket.RoutingRuleId = null;
        // Assigning moves the ticket core between New and Assigned only; implementation starts by hand.
        var state = ticket.State;
        TicketWorkflow.Assign(ticket, request.AssigneeId, now);
        if (state == TicketState.InProgress)
        {
            ticket.State = TicketState.InProgress;
        }

        return await ChangeCore.SaveAsync(http, db, user, indexer, ticket, cancellationToken);
    }

    internal static async Task<IResult> RouteAgainAsync(
        Guid id, HttpContext http, ZelvoDbContext db, CurrentUser user, TenantSettingsStore settings, ISearchIndexer indexer, CancellationToken cancellationToken)
    {
        var ticket = await ChangeCore.Changes(db).SingleOrDefaultAsync(t => t.Id == id, cancellationToken);
        if (ticket is null)
        {
            return ChangeCore.NotFound();
        }

        if (!TicketWorkflow.IsOpen(ticket.State))
        {
            return ApiProblem.Create(StatusCodes.Status409Conflict, "ticket-not-open", "Only open changes are routed.");
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await IncidentEndpoints.RouteAsync(db, settings, ticket, DateTimeOffset.UtcNow, cancellationToken);
        return await ChangeCore.SaveAsync(http, db, user, indexer, ticket, cancellationToken);
    }

    internal static async Task<IResult> ApprovalsAsync(Guid id, ZelvoDbContext db, CancellationToken cancellationToken) =>
        await db.Tickets.AnyAsync(t => t.Id == id && t.Type == TicketType.Change, cancellationToken)
            ? TypedResults.Ok(await ApprovalEndpoints.ProcessesAsync(db, id, cancellationToken))
            : ChangeCore.NotFound();

    internal static async Task<IResult> CommentsAsync(Guid id, ZelvoDbContext db, CancellationToken cancellationToken)
    {
        if (!await db.Tickets.AnyAsync(t => t.Id == id && t.Type == TicketType.Change, cancellationToken))
        {
            return ChangeCore.NotFound();
        }

        var rows = await db.TicketComments.AsNoTracking().Where(c => c.TicketId == id).OrderBy(c => c.CreatedAt).ThenBy(c => c.Id).Take(500).ToListAsync(cancellationToken);
        var names = await IncidentEndpoints.Names(db, rows.Select(c => c.AuthorId), cancellationToken);
        return TypedResults.Ok(rows.Select(c => new ChangeCommentResponse(c.Id, c.Body, c.Language, new PersonResponse(c.AuthorId, names.GetValueOrDefault(c.AuthorId)), c.CreatedAt)).ToList());
    }

    internal static async Task<IResult> CommentAsync(
        Guid id, ChangeCommentRequest request, ZelvoDbContext db, CurrentUser user, TenantSettingsStore settings, CancellationToken cancellationToken)
    {
        var ticket = await db.Tickets.SingleOrDefaultAsync(t => t.Id == id && t.Type == TicketType.Change, cancellationToken);
        if (ticket is null)
        {
            return ChangeCore.NotFound();
        }

        if (ticket.State == TicketState.Closed)
        {
            return IncidentEndpoints.Closed();
        }

        var body = request.Body?.Trim() ?? "";
        if (body.Length == 0)
        {
            return UserEndpoints.Validation("body", "Write the note.");
        }

        var me = user.Account!;
        var language = LocalizationSettings.IsLanguage(request.Language) ? request.Language!
            : LocalizationSettings.IsLanguage(me.PreferredLanguage) ? me.PreferredLanguage!
            : await settings.GetAsync(LocalizationSettings.DefaultLanguage, cancellationToken);
        var comment = new TicketComment
        {
            TicketId = ticket.Id,
            Visibility = CommentVisibility.Internal,
            Body = body,
            Language = language,
            AuthorId = me.Id,
            ByStaff = true,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.TicketComments.Add(comment);
        ticket.UpdatedAt = comment.CreatedAt;
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Created($"/api/v1/changes/{id}/comments",
            new ChangeCommentResponse(comment.Id, comment.Body, comment.Language, new PersonResponse(me.Id, me.DisplayName), comment.CreatedAt));
    }

    internal static async Task<IResult> TasksAsync(Guid id, ZelvoDbContext db, CurrentUser user, CancellationToken cancellationToken)
    {
        if (!await db.Tickets.AnyAsync(t => t.Id == id && t.Type == TicketType.Change, cancellationToken))
        {
            return ChangeCore.NotFound();
        }

        var rows = await db.RequestTasks.AsNoTracking().Where(t => t.TicketId == id).OrderBy(t => t.Position).ToListAsync(cancellationToken);
        return TypedResults.Ok(await RequestTaskEndpoints.ResponsesAsync(db, user, rows, cancellationToken));
    }

    internal static async Task<IResult> AddTaskAsync(Guid id, ChangeTaskRequest request, ZelvoDbContext db, CurrentUser user, IAuditLog audit, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.Database.ExecuteSqlAsync($"SELECT 1 FROM tickets WHERE id = {id} FOR UPDATE", cancellationToken);
        var ticket = await ChangeCore.Changes(db).SingleOrDefaultAsync(t => t.Id == id, cancellationToken);
        if (ticket is null)
        {
            return ChangeCore.NotFound();
        }

        if (!TicketWorkflow.IsOpen(ticket.State))
        {
            return ApiProblem.Create(StatusCodes.Status409Conflict, "change-not-open", "Tasks are added before the change is implemented.");
        }

        var name = new LocalizedText(request.Name?.En, request.Name?.Ar);
        if (!name.HasAnyValue)
        {
            return UserEndpoints.Validation("name", "Name the task in English or Arabic.");
        }

        if (request.GroupId is { } group && !await db.SupportGroups.AnyAsync(g => g.Id == group && g.Active, cancellationToken))
        {
            return UserEndpoints.Validation("groupId", "Choose an active group.");
        }

        var count = await db.RequestTasks.CountAsync(t => t.TicketId == id, cancellationToken);
        if (count >= MaxTasks)
        {
            return UserEndpoints.Validation("name", $"A change has at most {MaxTasks} tasks.");
        }

        var parallel = request.ParallelWithPrevious == true && count > 0;
        var template = new FulfilmentTask
        {
            Key = $"task{count + 1}",
            Name = name,
            Description = new LocalizedText(request.Description?.En, request.Description?.Ar),
            GroupId = request.GroupId,
            TargetMinutes = request.TargetMinutes,
            ParallelWithPrevious = parallel,
        };
        var now = DateTimeOffset.UtcNow;
        await RequestTasks.AddWaitingAsync(db, ticket, [template], now, cancellationToken);

        // A task added while the change is being implemented opens with the stage it joins.
        if (ChangeCore.StageOf(ticket) == ChangeStage.Implementation)
        {
            await db.SaveChangesAsync(cancellationToken);
            await RequestTasks.OpenNextStageAsync(db, id, now, cancellationToken);
        }

        audit.Add(AuditCategory.Change, "changes.task.added", "ticket", id.ToString(),
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["number"] = ticket.Number, ["task"] = template.Key });
        ticket.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        var rows = await db.RequestTasks.AsNoTracking().Where(t => t.TicketId == id).OrderBy(t => t.Position).ToListAsync(cancellationToken);
        return TypedResults.Ok(await RequestTaskEndpoints.ResponsesAsync(db, user, rows, cancellationToken));
    }

    internal static async Task<IResult> RemoveTaskAsync(Guid id, Guid taskId, ZelvoDbContext db, IAuditLog audit, CancellationToken cancellationToken)
    {
        var task = await db.RequestTasks.SingleOrDefaultAsync(t => t.Id == taskId && t.TicketId == id, cancellationToken);
        if (task is null || !await db.Tickets.AnyAsync(t => t.Id == id && t.Type == TicketType.Change, cancellationToken))
        {
            return FormsCommon.NotFound("task");
        }

        if (task.State != RequestTaskState.Waiting)
        {
            return ApiProblem.Create(StatusCodes.Status409Conflict, "task-opened", "Only tasks that have not opened yet can be removed.");
        }

        db.RequestTasks.Remove(task);
        audit.Add(AuditCategory.Change, "changes.task.removed", "ticket", id.ToString(),
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["task"] = task.Key });
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.NoContent();
    }

    internal static async Task<IResult> LinksAsync(Guid id, ZelvoDbContext db, CancellationToken cancellationToken) =>
        await db.Tickets.AnyAsync(t => t.Id == id && t.Type == TicketType.Change, cancellationToken)
            ? TypedResults.Ok(await LinkResponsesAsync(db, id, cancellationToken))
            : ChangeCore.NotFound();

    internal static async Task<IResult> LinkAsync(Guid id, LinkChangeRequest request, ZelvoDbContext db, CurrentUser user, IAuditLog audit, CancellationToken cancellationToken)
    {
        var change = await db.Tickets.SingleOrDefaultAsync(t => t.Id == id && t.Type == TicketType.Change, cancellationToken);
        if (change is null)
        {
            return ChangeCore.NotFound();
        }

        var number = request.Number!.Trim().ToUpperInvariant();
        var target = await db.Tickets.AsNoTracking().SingleOrDefaultAsync(t => t.Number == number && (t.Type == TicketType.Problem || t.Type == TicketType.Incident), cancellationToken);
        if (target is null)
        {
            return UserEndpoints.Validation("number", "No problem or incident has this number.");
        }

        var kind = target.Type == TicketType.Problem ? ChangeLinkKind.Fixes : ChangeLinkKind.CausedBy;
        if (!await db.ChangeLinks.AnyAsync(l => l.ChangeId == id && l.TicketId == target.Id, cancellationToken))
        {
            db.ChangeLinks.Add(new ChangeLink { ChangeId = id, TicketId = target.Id, Kind = kind, LinkedById = user.Account?.Id });
            var data = new Dictionary<string, object?>(StringComparer.Ordinal) { ["changeId"] = id, ["change"] = change.Number, ["ticket"] = target.Number, ["kind"] = kind.ToString() };
            audit.Add(AuditCategory.Change, "changes.link.added", "ticket", id.ToString(), data);
            audit.Add(AuditCategory.Change, "changes.link.added", "ticket", target.Id.ToString(), data);
            change.UpdatedAt = DateTimeOffset.UtcNow;
            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException)
            {
                // Linked at the same moment by someone else.
            }
        }

        return TypedResults.Ok(await LinkResponsesAsync(db, id, cancellationToken));
    }

    internal static async Task<IResult> UnlinkAsync(Guid id, Guid ticketId, ZelvoDbContext db, IAuditLog audit, CancellationToken cancellationToken)
    {
        var link = await db.ChangeLinks.SingleOrDefaultAsync(l => l.ChangeId == id && l.TicketId == ticketId, cancellationToken);
        if (link is null)
        {
            return FormsCommon.NotFound("link");
        }

        db.ChangeLinks.Remove(link);
        var data = new Dictionary<string, object?>(StringComparer.Ordinal) { ["changeId"] = id, ["ticketId"] = ticketId };
        audit.Add(AuditCategory.Change, "changes.link.removed", "ticket", id.ToString(), data);
        audit.Add(AuditCategory.Change, "changes.link.removed", "ticket", ticketId.ToString(), data);
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.NoContent();
    }

    internal static async Task<IResult> OfTicketAsync(TicketType type, Guid id, ZelvoDbContext db, CurrentUser user, CancellationToken cancellationToken)
    {
        if (!await db.Tickets.AnyAsync(t => t.Id == id && t.Type == type, cancellationToken))
        {
            return FormsCommon.NotFound(type == TicketType.Problem ? "problem" : "incident");
        }

        var rows = await ChangeCore.Changes(db).AsNoTracking().Where(t => db.ChangeLinks.Any(l => l.ChangeId == t.Id && l.TicketId == id))
            .OrderByDescending(t => t.CreatedAt).Take(50).ToListAsync(cancellationToken);
        var context = await ChangeResponseContext.LoadAsync(db, user, rows, cancellationToken);
        return TypedResults.Ok(rows.Select(context.From).ToList());
    }

    // ---- helpers ----

    /// <summary>The change for a change: tracked, version checked, not closed.</summary>
    private static async Task<(Ticket? Ticket, IResult? Problem)> LoadAsync(HttpContext http, ZelvoDbContext db, Guid id, CancellationToken cancellationToken)
    {
        if (db.Database.CurrentTransaction is not null)
        {
            await db.Database.ExecuteSqlAsync($"SELECT 1 FROM tickets WHERE id = {id} AND type = 'Change' FOR UPDATE", cancellationToken);
        }

        var ticket = await ChangeCore.Changes(db).SingleOrDefaultAsync(t => t.Id == id, cancellationToken);
        if (ticket?.Change is null)
        {
            return (null, ChangeCore.NotFound());
        }

        if (ChangeCore.VersionProblem(http, db, ticket) is { } versionProblem)
        {
            return (null, versionProblem);
        }

        return ticket.State == TicketState.Closed ? (null, IncidentEndpoints.Closed()) : (ticket, null);
    }

    private static IQueryable<Ticket> PhaseFilter(IQueryable<Ticket> tickets, ChangePhase phase) =>
        tickets.Where(t => t.State != TicketState.Resolved && t.State != TicketState.Closed && t.Change!.Phase == phase);

    private static IResult NotPlanning() =>
        ApiProblem.Create(StatusCodes.Status409Conflict, "change-not-planning", "The plans, window and risk change only while the change is planned; replan it first.");

    private static Microsoft.AspNetCore.Http.HttpResults.ValidationProblem? WindowProblem(DateTimeOffset? start, DateTimeOffset? end) =>
        (start is null) != (end is null)
            ? UserEndpoints.Validation("plannedEnd", "Give both the start and the end of the window.")
            : start is { } s && end is { } e && e <= s ? UserEndpoints.Validation("plannedEnd", "The window ends after it starts.") : null;

    private static async Task<List<ChangeLinkResponse>> LinkResponsesAsync(ZelvoDbContext db, Guid changeId, CancellationToken cancellationToken) =>
        await db.ChangeLinks.AsNoTracking().Where(l => l.ChangeId == changeId).OrderBy(l => l.LinkedAt)
            .Join(db.Tickets.AsNoTracking(), l => l.TicketId, t => t.Id, (l, t) => new ChangeLinkResponse(t.Id, t.Number, t.Title, t.Type, t.State, l.Kind, l.LinkedAt))
            .ToListAsync(cancellationToken);
}

/// <summary>A change's note.</summary>
internal sealed record ChangeCommentResponse(Guid Id, string Body, string Language, PersonResponse Author, DateTimeOffset CreatedAt);

/// <summary>A problem a change fixes or an incident it caused.</summary>
internal sealed record ChangeLinkResponse(Guid Id, string Number, string Title, TicketType Type, TicketState State, ChangeLinkKind Kind, DateTimeOffset LinkedAt);

/// <summary>Filters of the change list.</summary>
public sealed class ChangeListQuery
{
    /// <summary>Only changes in this stage.</summary>
    public ChangeStage? Stage { get; init; }

    /// <summary>Only open changes (not closed).</summary>
    public bool? Open { get; init; }

    /// <summary>Only this type.</summary>
    public ChangeKind? Kind { get; init; }

    /// <summary>Only this risk level.</summary>
    public ChangeRiskLevel? RiskLevel { get; init; }

    /// <summary>The change with this number.</summary>
    [StringLength(30)]
    public string? Number { get; init; }

    /// <summary><c>me</c>, <c>none</c> or a user id.</summary>
    [StringLength(40)]
    public string? Assignee { get; init; }

    /// <summary><c>mine</c>, <c>none</c> or a group id.</summary>
    [StringLength(40)]
    public string? Group { get; init; }

    /// <summary>Only changes on this configuration item.</summary>
    public Guid? ConfigurationItemId { get; init; }

    /// <summary>Planned window ending after.</summary>
    public DateTimeOffset? From { get; init; }

    /// <summary>Planned window starting before.</summary>
    public DateTimeOffset? To { get; init; }
}

/// <summary>A new change.</summary>
public sealed class CreateChangeRequest
{
    /// <summary>Standard, Normal or Emergency.</summary>
    [Required]
    public ChangeKind? Kind { get; init; }

    /// <summary>The standard change template (Standard).</summary>
    public Guid? TemplateId { get; init; }

    /// <summary>Short summary; a standard change takes its template's name when not given.</summary>
    [StringLength(Ticket.TitleMaxLength)]
    public string? Title { get; init; }

    /// <summary>What and why.</summary>
    [StringLength(Ticket.DescriptionMaxLength)]
    public string? Description { get; init; }

    /// <summary>Category.</summary>
    public Guid? CategoryId { get; init; }

    /// <summary>Planned start.</summary>
    public DateTimeOffset? PlannedStart { get; init; }

    /// <summary>Planned end.</summary>
    public DateTimeOffset? PlannedEnd { get; init; }

    /// <summary>Implementation plan.</summary>
    [StringLength(ChangeRecord.PlanMaxLength)]
    public string? ImplementationPlan { get; init; }

    /// <summary>Test plan.</summary>
    [StringLength(ChangeRecord.PlanMaxLength)]
    public string? TestPlan { get; init; }

    /// <summary>Back-out plan.</summary>
    [StringLength(ChangeRecord.PlanMaxLength)]
    public string? BackoutPlan { get; init; }

    /// <summary>Affected configuration items, at most 50.</summary>
    [MaxLength(ChangeEndpoints.MaxItems)]
    public IReadOnlyList<Guid>? ConfigurationItemIds { get; init; }
}

/// <summary>Changes to a change.</summary>
public sealed class UpdateChangeRequest
{
    /// <summary>New title.</summary>
    [StringLength(Ticket.TitleMaxLength)]
    public string? Title { get; init; }

    /// <summary>New description (empty clears it).</summary>
    [StringLength(Ticket.DescriptionMaxLength)]
    public string? Description { get; init; }

    /// <summary>New category.</summary>
    public Guid? CategoryId { get; init; }

    /// <summary>Remove the category.</summary>
    public bool? ClearCategory { get; init; }

    /// <summary>Implementation plan (empty clears it).</summary>
    [StringLength(ChangeRecord.PlanMaxLength)]
    public string? ImplementationPlan { get; init; }

    /// <summary>Test plan (empty clears it).</summary>
    [StringLength(ChangeRecord.PlanMaxLength)]
    public string? TestPlan { get; init; }

    /// <summary>Back-out plan (empty clears it).</summary>
    [StringLength(ChangeRecord.PlanMaxLength)]
    public string? BackoutPlan { get; init; }

    /// <summary>Planned start.</summary>
    public DateTimeOffset? PlannedStart { get; init; }

    /// <summary>Planned end.</summary>
    public DateTimeOffset? PlannedEnd { get; init; }

    /// <summary>Remove the planned window.</summary>
    public bool? ClearWindow { get; init; }
}

/// <summary>Answers of the risk questionnaire.</summary>
public sealed class ChangeRiskRequest
{
    /// <summary>Question key → answer key.</summary>
    [MaxLength(ChangeSettingsValue.MaxQuestions)]
    public Dictionary<string, string>? Answers { get; init; }
}

/// <summary>A move of a change.</summary>
public sealed class ChangeTransitionRequest
{
    /// <summary>Where to.</summary>
    [Required]
    public ChangeTarget? To { get; init; }

    /// <summary>How the implementation went (Implemented) or why it is cancelled (Cancel).</summary>
    [StringLength(Ticket.ResolutionNotesMaxLength)]
    public string? Notes { get; init; }
}

/// <summary>The post-implementation review.</summary>
public sealed class ChangeReviewRequest
{
    /// <summary>The outcome.</summary>
    [Required]
    public ChangeOutcome? Outcome { get; init; }

    /// <summary>What the review found (needed unless successful).</summary>
    [StringLength(ChangeRecord.PlanMaxLength)]
    public string? Notes { get; init; }
}

/// <summary>A change's group and assignee.</summary>
public sealed class ChangeAssignmentRequest
{
    /// <summary>Group, or none.</summary>
    public Guid? GroupId { get; init; }

    /// <summary>Assignee, or none.</summary>
    public Guid? AssigneeId { get; init; }
}

/// <summary>A note.</summary>
public sealed class ChangeCommentRequest
{
    /// <summary>The note.</summary>
    [Required]
    [StringLength(TicketComment.BodyMaxLength)]
    public string? Body { get; init; }

    /// <summary><c>en</c> or <c>ar</c>; the author's language when not given.</summary>
    [StringLength(2)]
    public string? Language { get; init; }
}

/// <summary>A task for a team.</summary>
public sealed class ChangeTaskRequest
{
    /// <summary>Name in English and Arabic.</summary>
    [Required]
    public TaskText? Name { get; init; }

    /// <summary>What to do.</summary>
    public TaskText? Description { get; init; }

    /// <summary>The group that does it; none: the change's group.</summary>
    public Guid? GroupId { get; init; }

    /// <summary>Target time in business minutes from when it opens.</summary>
    [Range(1, Data.Sla.SlaPolicyTarget.MaxMinutes)]
    public int? TargetMinutes { get; init; }

    /// <summary>Runs together with the last task instead of after it.</summary>
    public bool? ParallelWithPrevious { get; init; }
}

/// <summary>A task's bilingual text.</summary>
public sealed class TaskText
{
    /// <summary>English.</summary>
    [StringLength(1000)]
    public string? En { get; init; }

    /// <summary>Arabic.</summary>
    [StringLength(1000)]
    public string? Ar { get; init; }
}

/// <summary>A problem or incident to link, by number.</summary>
public sealed class LinkChangeRequest
{
    /// <summary>The number, for example <c>PRB-000123</c> or <c>INC-000123</c>.</summary>
    [Required]
    [StringLength(30, MinimumLength = 1)]
    public string? Number { get; init; }
}
