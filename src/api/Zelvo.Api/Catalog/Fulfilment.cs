using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Zelvo.Api.Settings;
using Zelvo.Api.Workflows;
using Zelvo.Data;
using Zelvo.Data.Auditing;
using Zelvo.Data.Catalog;
using Zelvo.Data.Directory;
using Zelvo.Data.Localization;
using Zelvo.Data.Tickets;
using Zelvo.Data.Workflows;
using Zelvo.Notifications;
using Zelvo.Tickets;
using Zelvo.Tickets.Sla;
using Zelvo.Workflows.Approvals;
using Zelvo.Workflows.Rules;

namespace Zelvo.Api.Catalog;

/// <summary>
/// How a service request reaches the service desk (task C4, module 2 fulfilment): the service's
/// initial group, or another group when the form answers match an override (first match wins);
/// without one, the B2 assignment rules decide. When the request is released (on submission, or
/// once approved), the service's ownership and completion SLAs are copied to it and its task
/// template is created, the first stage open.
/// </summary>
internal static class ServiceRequestRouting
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    /// <summary>Releases a request to the service desk: routes it, applies the service's SLA and creates its tasks.</summary>
    public static async Task ReleaseAsync(
        ZelvoDbContext db, TenantSettingsStore settings, Ticket ticket, ServiceCard? card, DateTimeOffset now, CancellationToken cancellationToken)
    {
        card ??= await ServiceRequestEndpoints.CardOfAsync(db, ticket.Id, cancellationToken);
        await RouteAsync(db, settings, ticket, card, now, cancellationToken);
        var details = db.ChangeTracker.Entries<ServiceRequestDetails>().FirstOrDefault(e => e.Entity.TicketId == ticket.Id)?.Entity
            ?? await db.ServiceRequests.SingleAsync(r => r.TicketId == ticket.Id, cancellationToken);
        var f = card.Fulfilment;
        details.OwnershipMinutes = f.OwnershipMinutes;
        details.CompletionMinutes = f.CompletionMinutes ?? await DaysToMinutesAsync(db, ticket, card.Days, cancellationToken);
        details.Escalations = JsonSerializer.Serialize(f.Escalations, Json);
        await RequestTasks.CreateAsync(db, ticket, card, now, cancellationToken);
    }

    /// <summary>Routes <paramref name="ticket"/>: an override whose conditions hold, else the service's group, else the assignment rules.</summary>
    public static async Task RouteAsync(ZelvoDbContext db, TenantSettingsStore settings, Ticket ticket, ServiceCard card, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var f = card.Fulfilment;
        (Guid? Group, AssignmentMethod Method) target = (f.GroupId, f.Method);
        if (f.Overrides.Count > 0)
        {
            var facts = await RuleFacts.LoadAsync(db, ticket, cancellationToken);
            if (f.Overrides.FirstOrDefault(o => o.GroupId is not null && RuleDocuments.Holds(o.Conditions, ConditionMatch.All, facts.Subject)) is { } match)
            {
                target = (match.GroupId, match.Method);
            }
        }

        if (target.Group is { } group && await db.SupportGroups.AnyAsync(g => g.Id == group && g.Active, cancellationToken))
        {
            ticket.GroupId = group;
            ticket.RoutingRuleId = null;
            var assignee = target.Method == AssignmentMethod.RoundRobin
                ? await TicketRouter.RoundRobinAsync(db, group, null, null, now, commit: true, cancellationToken)
                : null;
            TicketWorkflow.Assign(ticket, assignee, now);
            return;
        }

        await Incidents.IncidentEndpoints.RouteAsync(db, settings, ticket, now, cancellationToken);
    }

    /// <summary>
    /// The business minutes from submission to the end of the service's Nth working day (product
    /// owner decision 2026-11-24), on the request's group calendar or the tenant default.
    /// </summary>
    private static async Task<int?> DaysToMinutesAsync(ZelvoDbContext db, Ticket ticket, int? days, CancellationToken cancellationToken)
    {
        if (days is not { } n || n < 1)
        {
            return null;
        }

        var setup = await SlaSetup.LoadAsync(db, cancellationToken);
        var calendar = setup.Calendar(setup.CalendarIdFor(ticket.GroupId, null));
        var due = calendar.EndOfWorkingDay(ticket.CreatedAt, n);
        if (due == DateTimeOffset.MaxValue)
        {
            return null;
        }

        var minutes = (int)Math.Ceiling(calendar.SecondsBetween(ticket.CreatedAt, due) / 60.0);
        return Math.Clamp(minutes, 1, Data.Sla.SlaPolicyTarget.MaxMinutes);
    }
}

/// <summary>
/// Fulfilment tasks (task C4): the service's task template becomes the request's tasks; a task
/// with "parallel with the previous one" shares its stage, the others start a new stage. A stage
/// opens when every task before it is done; each opened task gets its due time on its group's
/// calendar. When the last task is done the request is resolved with a note naming the tasks, or
/// its sign-off starts (task C3).
/// </summary>
internal static class RequestTasks
{
    /// <summary>Creates the tasks of a released request (once) and opens the first stage.</summary>
    public static async Task CreateAsync(ZelvoDbContext db, Ticket ticket, ServiceCard card, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (card.Fulfilment.Tasks.Count == 0 || await db.RequestTasks.AnyAsync(t => t.TicketId == ticket.Id, cancellationToken))
        {
            return;
        }

        var stage = 0;
        var created = new List<RequestTask>();
        for (var i = 0; i < card.Fulfilment.Tasks.Count; i++)
        {
            var template = card.Fulfilment.Tasks[i];
            if (i > 0 && !template.ParallelWithPrevious)
            {
                stage++;
            }

            var task = new RequestTask
            {
                TicketId = ticket.Id,
                Key = template.Key,
                Name = template.Name,
                Description = template.Description,
                Position = i,
                Stage = stage,
                GroupId = template.GroupId ?? ticket.GroupId,
                TargetMinutes = template.TargetMinutes,
                CreatedAt = now,
            };
            created.Add(task);
            db.RequestTasks.Add(task);
        }

        await OpenStageAsync(db, created, 0, now, cancellationToken);
    }

    /// <summary>
    /// After a task changed: opens the next stage when the current one is done, and finishes the
    /// request when every task is done. Call with the ticket's row locked.
    /// </summary>
    public static async Task AdvanceAsync(
        ZelvoDbContext db, ApprovalService approvals, Ticket ticket, Guid? actorId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var tasks = await db.RequestTasks.Where(t => t.TicketId == ticket.Id).OrderBy(t => t.Position).ToListAsync(cancellationToken);
        if (tasks.Count == 0 || tasks.Any(t => t.State is RequestTaskState.Open or RequestTaskState.InProgress))
        {
            return;
        }

        if (tasks.Where(t => t.State == RequestTaskState.Waiting).Select(t => (int?)t.Stage).Min() is { } next)
        {
            await OpenStageAsync(db, tasks, next, now, cancellationToken);
            return;
        }

        if (!TicketWorkflow.IsOpen(ticket.State) || ticket.AwaitingApproval || tasks.All(t => t.State == RequestTaskState.Cancelled))
        {
            return;
        }

        // Every task is done: resolve the request (product owner decision 2026-11-24), or ask for its sign-off first.
        var language = await db.Users.AsNoTracking().Where(u => u.Id == ticket.RequesterId).Select(u => u.PreferredLanguage).SingleOrDefaultAsync(cancellationToken);
        language = language is LocalizedText.Arabic ? LocalizedText.Arabic : LocalizedText.English;
        var names = string.Join(language == LocalizedText.Arabic ? "، " : ", ",
            tasks.Where(t => t.State == RequestTaskState.Done).Select(t => t.Name.GetOrFallback(language) ?? t.Key));
        var notes = NotificationTexts.For(language).Terms["request.tasks-done"].Replace("{tasks}", names, StringComparison.Ordinal);
        notes = notes[..Math.Min(notes.Length, Ticket.ResolutionNotesMaxLength)];

        var card = await ServiceRequestEndpoints.CardOfAsync(db, ticket.Id, cancellationToken);
        var facts = await RuleFacts.LoadAsync(db, ticket, cancellationToken);
        var levels = await ApprovalPlanner.PlanAsync(db, ticket, ApprovalStage.AfterFulfilment, card.Approvals.AfterFulfilment, facts, cancellationToken);
        if (levels.Count > 0)
        {
            await approvals.StartAsync(ticket, ApprovalStage.AfterFulfilment, levels, actorId, notes, cancellationToken);
            return;
        }

        if (TicketWorkflow.Check(ticket, TicketState.Resolved, null, notes) == TransitionError.None)
        {
            TicketWorkflow.Apply(ticket, TicketState.Resolved, null, notes, now);
        }
    }

    /// <summary>Cancels tasks still waiting or open (the request was resolved or closed another way).</summary>
    public static async Task CancelOpenAsync(ZelvoDbContext db, Guid ticketId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        foreach (var task in await db.RequestTasks.Where(t => t.TicketId == ticketId
            && (t.State == RequestTaskState.Waiting || t.State == RequestTaskState.Open || t.State == RequestTaskState.InProgress)).ToListAsync(cancellationToken))
        {
            task.State = RequestTaskState.Cancelled;
            task.CompletedAt = now;
        }
    }

    private static async Task OpenStageAsync(ZelvoDbContext db, IReadOnlyList<RequestTask> tasks, int stage, DateTimeOffset now, CancellationToken cancellationToken)
    {
        SlaSetup? setup = null;
        foreach (var task in tasks.Where(t => t.Stage == stage && t.State == RequestTaskState.Waiting))
        {
            task.State = RequestTaskState.Open;
            task.OpenedAt = now;
            if (task.TargetMinutes is { } minutes)
            {
                setup ??= await SlaSetup.LoadAsync(db, cancellationToken);
                task.DueAt = setup.Calendar(setup.CalendarIdFor(task.GroupId, null)).Add(now, minutes * 60L);
            }
        }
    }
}
