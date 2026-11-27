using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;
using Zelvo.Api.Conventions;
using Zelvo.Api.Forms;
using Zelvo.Api.Identity;
using Zelvo.Api.Incidents;
using Zelvo.Api.Integrations;
using Zelvo.Api.ServiceDesk;
using Zelvo.Api.Settings;
using Zelvo.Api.Tenancy;
using Zelvo.Data;
using Zelvo.Data.Auditing;
using Zelvo.Data.Problems;
using Zelvo.Data.Tenancy;
using Zelvo.Data.Tickets;
using Zelvo.Search;
using Zelvo.Tickets;

namespace Zelvo.Api.Problems;

/// <summary>
/// Problem management (module 16, task H1). A record type <c>Problem</c> (<c>PRB-000123</c>) on the
/// shared ticket core: B2 routing, B3 clocks (Settings → SLA policies) and the conversation (B4,
/// internal notes); the stages New → Assessment → Root-cause analysis → Known error → Resolved →
/// Closed; raised from one or many incidents or by hand; the root-cause tools 5 Whys and fishbone
/// as structured data and the timeline of linked incidents; links to incidents, configuration
/// items (G1) and knowledge articles; resolving a problem can resolve its open incidents.
/// </summary>
internal static class ProblemEndpoints
{
    public static RouteGroupBuilder MapProblemEndpoints(this RouteGroupBuilder group)
    {
        var problems = group.MapGroup("/problems").WithTags("Problems").RequireTenant();
        problems.MapPost("/", CreateAsync).WithName("CreateProblem")
            .WithSummary("Raise a problem by hand or from one or many incidents (incidentIds, at most 100; the category and priority follow them when not given). Routed by the assignment rules; SLA clocks start. Needs problems.edit; audited.")
            .RequirePerson().RequirePermission(Permissions.ProblemsEdit).Produces<ProblemResponse>(StatusCodes.Status201Created).ProducesValidationProblem();
        problems.MapGet("/", ListAsync).WithName("ListProblems")
            .WithSummary("Problems, newest first. Filters stage, open, number, assignee (me, none or an id), group (mine, none or an id), category and incidentId (the problems an incident is linked to). Needs problems.read.")
            .RequirePermission(Permissions.ProblemsRead).Produces<PageResult<ProblemResponse>>();
        problems.MapGet("/assignees", AssigneesAsync).WithName("ListProblemAssignees")
            .WithSummary("Active users who can be assigned problems (their roles hold problems.edit); with groupId, only that group's members. Needs problems.manage.")
            .RequirePermission(Permissions.ProblemsManage).Produces<IReadOnlyList<PersonResponse>>();
        problems.MapGet("/{id:guid}", GetAsync).WithName("GetProblem")
            .WithSummary("One problem with its analysis and known error. Returns an ETag to send as If-Match with changes. Needs problems.read.")
            .RequirePermission(Permissions.ProblemsRead).Produces<ProblemResponse>();
        problems.MapPatch("/{id:guid}", UpdateAsync).WithName("UpdateProblem")
            .WithSummary("Change the title, description, category, priority (1–5, for SLA targets) or root cause. Needs problems.edit; audited.")
            .RequirePermission(Permissions.ProblemsEdit).Produces<ProblemResponse>().ProducesValidationProblem();
        problems.MapPut("/{id:guid}/analysis", AnalysisAsync).WithName("SetProblemAnalysis")
            .WithSummary("Save the 5 Whys (statement, up to seven why / because steps, conclusion) and the fishbone (effect, causes under the categories of Settings → Problems). Needs problems.edit; audited.")
            .RequirePermission(Permissions.ProblemsEdit).Produces<ProblemResponse>().ProducesValidationProblem();
        problems.MapPost("/{id:guid}/transitions", TransitionAsync).WithName("ChangeProblemStage")
            .WithSummary("Move the problem: Assessment and RootCauseAnalysis (problems.edit; needs an assignee), OnHold (an active on-hold reason), KnownError (problems.manage; needs an active known error), Resolved (problems.manage; resolution notes; resolveIncidents also resolves its open incidents and their children), Closed and Reopened (problems.manage, from Resolved). Audited.")
            .RequirePermission(Permissions.ProblemsEdit).Produces<ProblemResponse>().ProducesValidationProblem();
        problems.MapPut("/{id:guid}/assignment", AssignAsync).WithName("SetProblemAssignment")
            .WithSummary("Set the group and assignee (a member of the group when one is given, whose roles hold problems.edit). Needs problems.manage; audited.")
            .RequirePermission(Permissions.ProblemsManage).Produces<ProblemResponse>().ProducesValidationProblem();
        problems.MapPost("/{id:guid}/route", RouteAgainAsync).WithName("RouteProblem")
            .WithSummary("Run the assignment rules again. Open problems only; needs problems.manage; audited.")
            .RequirePermission(Permissions.ProblemsManage).Produces<ProblemResponse>();
        problems.MapGet("/{id:guid}/comments", CommentsAsync).WithName("ListProblemComments")
            .WithSummary("The problem's notes, oldest first. Needs problems.read.")
            .RequirePermission(Permissions.ProblemsRead).Produces<IReadOnlyList<ProblemCommentResponse>>();
        problems.MapPost("/{id:guid}/comments", CommentAsync).WithName("CreateProblemComment")
            .WithSummary("Add an internal note (problems have no requester to reply to). Needs problems.edit; audited.")
            .RequirePerson().RequirePermission(Permissions.ProblemsEdit).Produces<ProblemCommentResponse>(StatusCodes.Status201Created).ProducesValidationProblem();
        problems.MapGet("/{id:guid}/incidents", IncidentsAsync).WithName("ListProblemIncidents")
            .WithSummary("Incidents linked to the problem, oldest link first. Needs problems.read.")
            .RequirePermission(Permissions.ProblemsRead).Produces<IReadOnlyList<ProblemIncidentResponse>>();
        problems.MapPost("/{id:guid}/incidents", LinkIncidentsAsync).WithName("LinkProblemIncidents")
            .WithSummary("Link incidents (at most 100 per call; already linked ones are skipped). Needs problems.edit; audited on the problem and each incident.")
            .RequirePermission(Permissions.ProblemsEdit).Produces<IReadOnlyList<ProblemIncidentResponse>>().ProducesValidationProblem();
        problems.MapDelete("/{id:guid}/incidents/{incidentId:guid}", UnlinkIncidentAsync).WithName("UnlinkProblemIncident")
            .WithSummary("Unlink an incident. Needs problems.edit; audited.")
            .RequirePermission(Permissions.ProblemsEdit).Produces(StatusCodes.Status204NoContent);
        problems.MapGet("/{id:guid}/articles", ArticlesAsync).WithName("ListProblemArticles")
            .WithSummary("Knowledge articles linked to the problem. Needs problems.read.")
            .RequirePermission(Permissions.ProblemsRead).Produces<IReadOnlyList<ProblemArticleResponse>>();
        problems.MapPost("/{id:guid}/articles", async (Guid id, LinkArticleRequest request, ZelvoDbContext db, CurrentUser user, IAuditLog audit, CancellationToken ct) =>
                await db.KbArticles.AsNoTracking().Where(a => a.Number == request.Number!.Trim().ToUpper()).Select(a => (Guid?)a.Id).SingleOrDefaultAsync(ct) is { } articleId
                    ? await LinkArticleAsync(id, articleId, db, user, audit, ct)
                    : FormsCommon.NotFound("article"))
            .WithName("LinkProblemArticleByNumber")
            .WithSummary("Link a knowledge article by its number (KB-000123). Needs problems.edit; audited.")
            .RequirePermission(Permissions.ProblemsEdit).Produces<IReadOnlyList<ProblemArticleResponse>>();
        problems.MapPut("/{id:guid}/articles/{articleId:guid}", LinkArticleAsync).WithName("LinkProblemArticle")
            .WithSummary("Link a knowledge article. Needs problems.edit; audited.")
            .RequirePermission(Permissions.ProblemsEdit).Produces<IReadOnlyList<ProblemArticleResponse>>();
        problems.MapDelete("/{id:guid}/articles/{articleId:guid}", UnlinkArticleAsync).WithName("UnlinkProblemArticle")
            .WithSummary("Unlink a knowledge article. Needs problems.edit; audited.")
            .RequirePermission(Permissions.ProblemsEdit).Produces(StatusCodes.Status204NoContent);
        problems.MapGet("/{id:guid}/timeline", TimelineAsync).WithName("GetProblemTimeline")
            .WithSummary("The timeline of the problem and its linked incidents (raised, linked, resolved, closed; the known error), oldest first. Changes join it with task H3. Needs problems.read.")
            .RequirePermission(Permissions.ProblemsRead).Produces<IReadOnlyList<TimelineEntryResponse>>();

        group.MapGet("/incidents/{id:guid}/problems", ProblemsOfIncidentAsync).WithName("ListIncidentProblems").WithTags("Problems").RequireTenant()
            .WithSummary("The problems an incident is linked to. Needs incidents.read and problems.read.")
            .RequirePermission(Permissions.IncidentsRead).RequirePermission(Permissions.ProblemsRead).Produces<IReadOnlyList<ProblemResponse>>();
        return group;
    }

    internal static async Task<IResult> CreateAsync(
        CreateProblemRequest request, HttpContext http, ZelvoDbContext db, CurrentUser user, ITenantContext tenant, TenantSettingsStore settings,
        IAuditLog audit, ISearchIndexer indexer, CancellationToken cancellationToken)
    {
        var (incidentIds, problem) = await ProblemCore.CheckIncidentsAsync(db, request.IncidentIds, cancellationToken);
        if (problem is not null)
        {
            return problem;
        }

        if (request.CategoryId is { } category && !await TreeEndpoints.IsSelectableAsync(db.Categories, category, cancellationToken))
        {
            return UserEndpoints.Validation("categoryId", "Choose an active category.");
        }

        // From incidents: their shared category and their highest priority, unless given.
        var incidents = await db.Tickets.AsNoTracking().Include(t => t.Incident).Where(t => incidentIds.Contains(t.Id)).ToListAsync(cancellationToken);
        var categoryId = request.CategoryId ?? (incidents.Select(i => i.CategoryId).Distinct().ToList() is [{ } only] ? only : null);
        var priority = request.Priority ?? incidents.Select(i => i.Incident?.Priority).OfType<int>().DefaultIfEmpty(3).Min();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var ticket = await ProblemCore.CreateAsync(http, db, settings, tenant, indexer,
            new ProblemDraft(request.Title!, request.Description, user.Account!.Id, categoryId, Math.Clamp(priority, 1, 5), incidentIds), cancellationToken);
        foreach (var incident in incidents)
        {
            audit.Add(AuditCategory.Change, "problems.incident.linked", "ticket", incident.Id.ToString(),
                new Dictionary<string, object?>(StringComparer.Ordinal) { ["problemId"] = ticket.Id, ["problem"] = ticket.Number, ["incident"] = incident.Number });
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return TypedResults.Created($"/api/v1/problems/{ticket.Id}", await ProblemCore.ResponseAsync(http, db, user, ticket.Id, cancellationToken));
    }

    internal static async Task<IResult> ListAsync(
        [AsParameters] PageQuery page, [AsParameters] ProblemListQuery query, ZelvoDbContext db, CurrentUser user, CancellationToken cancellationToken)
    {
        var me = user.Account?.Id;
        var tickets = ProblemCore.Problems(db).AsNoTracking();
        if (query.Stage is { } stage)
        {
            tickets = stage switch
            {
                ProblemStage.New => tickets.Where(t => t.State == TicketState.New || t.State == TicketState.Assigned),
                ProblemStage.Resolved => tickets.Where(t => t.State == TicketState.Resolved),
                ProblemStage.Closed => tickets.Where(t => t.State == TicketState.Closed),
                _ => tickets.Where(t => t.State != TicketState.New && t.State != TicketState.Assigned && t.State != TicketState.Resolved && t.State != TicketState.Closed
                    && t.Problem!.Phase == (stage == ProblemStage.RootCauseAnalysis ? ProblemPhase.RootCauseAnalysis : stage == ProblemStage.KnownError ? ProblemPhase.KnownError : ProblemPhase.Assessment)),
            };
        }
        else if (query.Open == true)
        {
            tickets = tickets.Where(t => t.State != TicketState.Resolved && t.State != TicketState.Closed);
        }

        if (query.Number is { Length: > 0 } number)
        {
            var wanted = number.Trim().ToUpperInvariant();
            tickets = tickets.Where(t => t.Number == wanted);
        }

        if (query.CategoryId is { } categoryId)
        {
            tickets = tickets.Where(t => t.CategoryId == categoryId);
        }

        if (query.IncidentId is { } incidentId)
        {
            tickets = tickets.Where(t => db.ProblemIncidents.Any(l => l.ProblemId == t.Id && l.IncidentId == incidentId));
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
        var context = await ProblemResponseContext.LoadAsync(db, user, rows, cancellationToken);
        return TypedResults.Ok(new PageResult<ProblemResponse>([.. rows.Select(context.From)], page.Number, page.Size, total));
    }

    internal static async Task<IResult> AssigneesAsync(Guid? groupId, ZelvoDbContext db, CancellationToken cancellationToken)
    {
        var people = ProblemCore.Assignable(db);
        if (groupId is { } g)
        {
            people = people.Where(u => db.SupportGroupMembers.Any(m => m.GroupId == g && m.UserId == u.Id));
        }

        return TypedResults.Ok(await people.OrderBy(u => u.DisplayName ?? u.Email).Take(500).Select(u => new PersonResponse(u.Id, u.DisplayName)).ToListAsync(cancellationToken));
    }

    internal static async Task<IResult> GetAsync(Guid id, HttpContext http, ZelvoDbContext db, CurrentUser user, CancellationToken cancellationToken) =>
        await db.Tickets.AnyAsync(t => t.Id == id && t.Type == TicketType.Problem, cancellationToken)
            ? TypedResults.Ok(await ProblemCore.ResponseAsync(http, db, user, id, cancellationToken))
            : ProblemCore.NotFound();

    internal static async Task<IResult> UpdateAsync(
        Guid id, UpdateProblemRequest request, HttpContext http, ZelvoDbContext db, CurrentUser user, ISearchIndexer indexer, CancellationToken cancellationToken)
    {
        var ticket = await ProblemCore.Problems(db).SingleOrDefaultAsync(t => t.Id == id, cancellationToken);
        if (ticket is null)
        {
            return ProblemCore.NotFound();
        }

        if (ProblemCore.VersionProblem(http, db, ticket) is { } versionProblem)
        {
            return versionProblem;
        }

        if (ticket.State == TicketState.Closed)
        {
            return IncidentEndpoints.Closed();
        }

        var categoryId = request.ClearCategory == true ? null : request.CategoryId ?? ticket.CategoryId;
        if (categoryId is { } category && category != ticket.CategoryId && !await TreeEndpoints.IsSelectableAsync(db.Categories, category, cancellationToken))
        {
            return UserEndpoints.Validation("categoryId", "Choose an active category.");
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
            ticket.Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        }

        if (request.Priority is { } priority)
        {
            ticket.Problem!.Priority = priority;
        }

        if (request.RootCause is { } rootCause)
        {
            ticket.Problem!.RootCause = string.IsNullOrWhiteSpace(rootCause) ? null : rootCause.Trim();
        }

        ticket.CategoryId = categoryId;
        ticket.UpdatedAt = DateTimeOffset.UtcNow;
        return await ProblemCore.SaveAsync(http, db, user, indexer, ticket, cancellationToken);
    }

    internal static async Task<IResult> AnalysisAsync(
        Guid id, ProblemAnalysisRequest request, HttpContext http, ZelvoDbContext db, CurrentUser user, TenantSettingsStore settings, ISearchIndexer indexer,
        CancellationToken cancellationToken)
    {
        var ticket = await ProblemCore.Problems(db).SingleOrDefaultAsync(t => t.Id == id, cancellationToken);
        if (ticket is null)
        {
            return ProblemCore.NotFound();
        }

        if (ProblemCore.VersionProblem(http, db, ticket) is { } versionProblem)
        {
            return versionProblem;
        }

        if (ticket.State == TicketState.Closed)
        {
            return IncidentEndpoints.Closed();
        }

        var problem = ticket.Problem!;
        if (request.FiveWhys is { } whys)
        {
            var (value, error) = ProblemAnalysis.Check(whys);
            if (value is null)
            {
                return UserEndpoints.Validation("fiveWhys", error!);
            }

            problem.FiveWhys = ProblemAnalysis.Write(value);
        }

        if (request.Fishbone is { } fishbone)
        {
            var (value, error) = ProblemAnalysis.Check(fishbone, await settings.GetAsync(ProblemSettings.Problems, cancellationToken), ProblemAnalysis.ReadFishbone(problem.Fishbone));
            if (value is null)
            {
                return UserEndpoints.Validation("fishbone", error!);
            }

            problem.Fishbone = ProblemAnalysis.Write(value);
        }

        ticket.UpdatedAt = DateTimeOffset.UtcNow;
        return await ProblemCore.SaveAsync(http, db, user, indexer, ticket, cancellationToken);
    }

    internal static async Task<IResult> TransitionAsync(
        Guid id, ProblemTransitionRequest request, HttpContext http, ZelvoDbContext db, CurrentUser user, TenantSettingsStore settings,
        ISearchIndexer indexer, CancellationToken cancellationToken)
    {
        var ticket = await ProblemCore.Problems(db).SingleOrDefaultAsync(t => t.Id == id, cancellationToken);
        if (ticket is null)
        {
            return ProblemCore.NotFound();
        }

        if (ProblemCore.VersionProblem(http, db, ticket) is { } versionProblem)
        {
            return versionProblem;
        }

        var to = request.To!.Value;
        var manage = user.Has(Permissions.ProblemsManage);
        if (!manage && to is ProblemTarget.KnownError or ProblemTarget.Resolved or ProblemTarget.Closed or ProblemTarget.Reopened)
        {
            return ApiProblem.Create(StatusCodes.Status403Forbidden, "forbidden", "Recording a known error, resolving, closing and reopening problems needs problems.manage.");
        }

        var hasKnownError = await db.KnownErrors.AnyAsync(e => e.ProblemId == id && e.Status == KnownErrorStatus.Active, cancellationToken);
        if (!ProblemCore.NextTargets(ticket, true, manage, hasKnownError).Contains(to))
        {
            return ticket.State == TicketState.Closed ? IncidentEndpoints.Closed()
                : to == ProblemTarget.KnownError && !hasKnownError
                    ? ApiProblem.Create(StatusCodes.Status409Conflict, "known-error-required", "Record the known error with its workaround first.")
                : ApiProblem.Create(StatusCodes.Status409Conflict, "transition-not-allowed", $"The problem cannot move from {ProblemCore.StageOf(ticket)} to {to}.");
        }

        var now = DateTimeOffset.UtcNow;
        List<Ticket> incidents = [];
        switch (to)
        {
            case ProblemTarget.Assessment or ProblemTarget.RootCauseAnalysis or ProblemTarget.KnownError:
                if (ticket.State != TicketState.InProgress)
                {
                    switch (TicketWorkflow.Check(ticket, TicketState.InProgress, null, null))
                    {
                        case TransitionError.AssigneeRequired:
                            return ApiProblem.Create(StatusCodes.Status409Conflict, "assignee-required", "Assign the problem before working on it.");
                        case not TransitionError.None:
                            return ApiProblem.Create(StatusCodes.Status409Conflict, "transition-not-allowed", $"The problem cannot move from {ProblemCore.StageOf(ticket)} to {to}.");
                    }

                    TicketWorkflow.Apply(ticket, TicketState.InProgress, null, null, now);
                }

                ticket.Problem!.Phase = to switch
                {
                    ProblemTarget.RootCauseAnalysis => ProblemPhase.RootCauseAnalysis,
                    ProblemTarget.KnownError => ProblemPhase.KnownError,
                    _ => ProblemPhase.Assessment,
                };
                ticket.UpdatedAt = now;
                break;
            case ProblemTarget.OnHold:
                var reasons = await settings.GetAsync(IncidentSettings.OnHoldReasons, cancellationToken);
                if (!reasons.Any(r => r.Active && r.Key == request.OnHoldReason))
                {
                    return UserEndpoints.Validation("onHoldReason", "Choose one of the active on-hold reasons.");
                }

                TicketWorkflow.Apply(ticket, TicketState.OnHold, request.OnHoldReason, null, now);
                break;
            case ProblemTarget.Resolved:
                if (string.IsNullOrWhiteSpace(request.ResolutionNotes))
                {
                    return UserEndpoints.Validation("resolutionNotes", "Say how the problem was fixed.");
                }

                TicketWorkflow.Apply(ticket, TicketState.Resolved, null, request.ResolutionNotes, now);
                if (request.ResolveIncidents == true)
                {
                    incidents = await ResolveIncidentsAsync(db, ticket, now, cancellationToken);
                }

                break;
            case ProblemTarget.Closed:
                TicketWorkflow.Apply(ticket, TicketState.Closed, null, null, now);
                break;
            case ProblemTarget.Reopened:
                TicketWorkflow.Apply(ticket, TicketState.Reopened, null, null, now);
                break;
        }

        return await ProblemCore.SaveAsync(http, db, user, indexer, ticket, cancellationToken, incidents);
    }

    /// <summary>Resolves the problem's open incidents (and their open children) with the problem's notes.</summary>
    private static async Task<List<Ticket>> ResolveIncidentsAsync(ZelvoDbContext db, Ticket problem, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var linked = db.ProblemIncidents.Where(l => l.ProblemId == problem.Id).Select(l => l.IncidentId);
        var open = await IncidentEndpoints.Incidents(db)
            .Where(t => (linked.Contains(t.Id) || (t.ParentId != null && linked.Contains(t.ParentId.Value))) && t.State != TicketState.Resolved && t.State != TicketState.Closed)
            .ToListAsync(cancellationToken);
        var notes = $"{problem.Number}: {problem.ResolutionNotes}";
        if (notes.Length > Ticket.ResolutionNotesMaxLength)
        {
            notes = notes[..Ticket.ResolutionNotesMaxLength];
        }

        var resolved = new List<Ticket>();
        foreach (var incident in open.Where(i => TicketWorkflow.Check(i, TicketState.Resolved, null, notes) == TransitionError.None))
        {
            TicketWorkflow.Apply(incident, TicketState.Resolved, null, notes, now);
            resolved.Add(incident);
        }

        return resolved;
    }

    internal static async Task<IResult> AssignAsync(
        Guid id, ProblemAssignmentRequest request, HttpContext http, ZelvoDbContext db, CurrentUser user, ISearchIndexer indexer, CancellationToken cancellationToken)
    {
        var ticket = await ProblemCore.Problems(db).SingleOrDefaultAsync(t => t.Id == id, cancellationToken);
        if (ticket is null)
        {
            return ProblemCore.NotFound();
        }

        if (ProblemCore.VersionProblem(http, db, ticket) is { } versionProblem)
        {
            return versionProblem;
        }

        if (ticket.State == TicketState.Closed)
        {
            return IncidentEndpoints.Closed();
        }

        if (request.GroupId is { } groupId && groupId != ticket.GroupId && !await db.SupportGroups.AnyAsync(g => g.Id == groupId && g.Active, cancellationToken))
        {
            return UserEndpoints.Validation("groupId", "Choose an active group.");
        }

        if (request.AssigneeId is { } assigneeId && !await ProblemCore.Assignable(db).AnyAsync(u => u.Id == assigneeId, cancellationToken))
        {
            return UserEndpoints.Validation("assigneeId", "The assignee cannot be assigned problems.");
        }

        if (request.GroupId is { } group && request.AssigneeId is { } member
            && !await db.SupportGroupMembers.AnyAsync(m => m.GroupId == group && m.UserId == member, cancellationToken))
        {
            return UserEndpoints.Validation("assigneeId", "The assignee is not a member of the group.");
        }

        ticket.GroupId = request.GroupId;
        ticket.RoutingRuleId = null;
        TicketWorkflow.Assign(ticket, request.AssigneeId, DateTimeOffset.UtcNow);
        return await ProblemCore.SaveAsync(http, db, user, indexer, ticket, cancellationToken);
    }

    internal static async Task<IResult> RouteAgainAsync(
        Guid id, HttpContext http, ZelvoDbContext db, CurrentUser user, TenantSettingsStore settings, ISearchIndexer indexer, CancellationToken cancellationToken)
    {
        var ticket = await ProblemCore.Problems(db).SingleOrDefaultAsync(t => t.Id == id, cancellationToken);
        if (ticket is null)
        {
            return ProblemCore.NotFound();
        }

        if (!TicketWorkflow.IsOpen(ticket.State))
        {
            return ApiProblem.Create(StatusCodes.Status409Conflict, "ticket-not-open", "Only open problems are routed.");
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await IncidentEndpoints.RouteAsync(db, settings, ticket, DateTimeOffset.UtcNow, cancellationToken);
        return await ProblemCore.SaveAsync(http, db, user, indexer, ticket, cancellationToken);
    }

    internal static async Task<IResult> CommentsAsync(Guid id, ZelvoDbContext db, CancellationToken cancellationToken)
    {
        if (!await db.Tickets.AnyAsync(t => t.Id == id && t.Type == TicketType.Problem, cancellationToken))
        {
            return ProblemCore.NotFound();
        }

        var rows = await db.TicketComments.AsNoTracking().Where(c => c.TicketId == id).OrderBy(c => c.CreatedAt).ThenBy(c => c.Id).Take(500).ToListAsync(cancellationToken);
        var names = await IncidentEndpoints.Names(db, rows.Select(c => c.AuthorId), cancellationToken);
        return TypedResults.Ok(rows.Select(c => new ProblemCommentResponse(c.Id, c.Body, c.Language, new PersonResponse(c.AuthorId, names.GetValueOrDefault(c.AuthorId)), c.CreatedAt)).ToList());
    }

    internal static async Task<IResult> CommentAsync(
        Guid id, ProblemCommentRequest request, ZelvoDbContext db, CurrentUser user, TenantSettingsStore settings, CancellationToken cancellationToken)
    {
        var ticket = await db.Tickets.SingleOrDefaultAsync(t => t.Id == id && t.Type == TicketType.Problem, cancellationToken);
        if (ticket is null)
        {
            return ProblemCore.NotFound();
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
        return TypedResults.Created($"/api/v1/problems/{id}/comments",
            new ProblemCommentResponse(comment.Id, comment.Body, comment.Language, new PersonResponse(me.Id, me.DisplayName), comment.CreatedAt));
    }

    internal static async Task<IResult> IncidentsAsync(Guid id, ZelvoDbContext db, CancellationToken cancellationToken) =>
        await db.Tickets.AnyAsync(t => t.Id == id && t.Type == TicketType.Problem, cancellationToken)
            ? TypedResults.Ok(await LinkedIncidentsAsync(db, id, cancellationToken))
            : ProblemCore.NotFound();

    internal static async Task<IResult> LinkIncidentsAsync(
        Guid id, LinkIncidentsRequest request, ZelvoDbContext db, CurrentUser user, IAuditLog audit, CancellationToken cancellationToken)
    {
        var problem = await db.Tickets.SingleOrDefaultAsync(t => t.Id == id && t.Type == TicketType.Problem, cancellationToken);
        if (problem is null)
        {
            return ProblemCore.NotFound();
        }

        if (problem.State == TicketState.Closed)
        {
            return IncidentEndpoints.Closed();
        }

        var (ids, invalid) = await ProblemCore.CheckIncidentsAsync(db, request.IncidentIds, cancellationToken);
        if (invalid is not null)
        {
            return invalid;
        }

        var existing = await db.ProblemIncidents.Where(l => l.ProblemId == id && ids.Contains(l.IncidentId)).Select(l => l.IncidentId).ToListAsync(cancellationToken);
        var numbers = await db.Tickets.AsNoTracking().Where(t => ids.Contains(t.Id)).ToDictionaryAsync(t => t.Id, t => t.Number, cancellationToken);
        foreach (var incidentId in ids.Except(existing))
        {
            db.ProblemIncidents.Add(new ProblemIncident { ProblemId = id, IncidentId = incidentId, LinkedById = user.Account?.Id });
            audit.Add(AuditCategory.Change, "problems.incident.linked", "ticket", incidentId.ToString(),
                new Dictionary<string, object?>(StringComparer.Ordinal) { ["problemId"] = id, ["problem"] = problem.Number, ["incident"] = numbers[incidentId] });
        }

        problem.UpdatedAt = DateTimeOffset.UtcNow;
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Linked at the same moment by someone else.
        }

        return TypedResults.Ok(await LinkedIncidentsAsync(db, id, cancellationToken));
    }

    internal static async Task<IResult> UnlinkIncidentAsync(Guid id, Guid incidentId, ZelvoDbContext db, IAuditLog audit, CancellationToken cancellationToken)
    {
        var link = await db.ProblemIncidents.SingleOrDefaultAsync(l => l.ProblemId == id && l.IncidentId == incidentId, cancellationToken);
        if (link is null)
        {
            return FormsCommon.NotFound("incident");
        }

        db.ProblemIncidents.Remove(link);
        var numbers = await db.Tickets.AsNoTracking().Where(t => t.Id == id || t.Id == incidentId).ToDictionaryAsync(t => t.Id, t => t.Number, cancellationToken);
        audit.Add(AuditCategory.Change, "problems.incident.unlinked", "ticket", incidentId.ToString(),
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["problemId"] = id, ["problem"] = numbers.GetValueOrDefault(id), ["incident"] = numbers.GetValueOrDefault(incidentId) });
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.NoContent();
    }

    internal static async Task<List<ProblemIncidentResponse>> LinkedIncidentsAsync(ZelvoDbContext db, Guid problemId, CancellationToken cancellationToken)
    {
        var rows = await db.ProblemIncidents.AsNoTracking().Where(l => l.ProblemId == problemId)
            .Join(db.Tickets.AsNoTracking().Include(t => t.Incident), l => l.IncidentId, t => t.Id, (l, t) => new { Link = l, Ticket = t })
            .OrderBy(x => x.Link.LinkedAt).ThenBy(x => x.Ticket.Sequence).Take(1000).ToListAsync(cancellationToken);
        var names = await IncidentEndpoints.Names(db, rows.SelectMany(r => new[] { r.Ticket.AssigneeId ?? Guid.Empty, r.Link.LinkedById ?? Guid.Empty }), cancellationToken);
        var refs = await DirectoryRefs.LoadAsync(db, cancellationToken);
        return [.. rows.Select(r => new ProblemIncidentResponse(
            r.Ticket.Id, r.Ticket.Number, r.Ticket.Title, r.Ticket.State, r.Ticket.Incident?.Priority, refs.Categories.RefOf(r.Ticket.CategoryId),
            r.Ticket.AssigneeId is { } a ? new PersonResponse(a, names.GetValueOrDefault(a)) : null, r.Ticket.CreatedAt, r.Ticket.ResolvedAt,
            r.Link.LinkedAt, r.Link.LinkedById is { } by ? new PersonResponse(by, names.GetValueOrDefault(by)) : null))];
    }

    internal static async Task<IResult> ArticlesAsync(Guid id, ZelvoDbContext db, CancellationToken cancellationToken) =>
        await db.Tickets.AnyAsync(t => t.Id == id && t.Type == TicketType.Problem, cancellationToken)
            ? TypedResults.Ok(await LinkedArticlesAsync(db, id, cancellationToken))
            : ProblemCore.NotFound();

    internal static async Task<IResult> LinkArticleAsync(Guid id, Guid articleId, ZelvoDbContext db, CurrentUser user, IAuditLog audit, CancellationToken cancellationToken)
    {
        var problem = await db.Tickets.AsNoTracking().SingleOrDefaultAsync(t => t.Id == id && t.Type == TicketType.Problem, cancellationToken);
        if (problem is null)
        {
            return ProblemCore.NotFound();
        }

        var article = await db.KbArticles.AsNoTracking().SingleOrDefaultAsync(a => a.Id == articleId, cancellationToken);
        if (article is null)
        {
            return FormsCommon.NotFound("article");
        }

        if (!await db.ProblemArticles.AnyAsync(l => l.ProblemId == id && l.ArticleId == articleId, cancellationToken))
        {
            db.ProblemArticles.Add(new ProblemArticle { ProblemId = id, ArticleId = articleId, LinkedById = user.Account?.Id });
            audit.Add(AuditCategory.Change, "problems.article.linked", "ticket", id.ToString(),
                new Dictionary<string, object?>(StringComparer.Ordinal) { ["articleId"] = articleId, ["article"] = article.Number });
            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException)
            {
                // Linked at the same moment by someone else.
            }
        }

        return TypedResults.Ok(await LinkedArticlesAsync(db, id, cancellationToken));
    }

    internal static async Task<IResult> UnlinkArticleAsync(Guid id, Guid articleId, ZelvoDbContext db, IAuditLog audit, CancellationToken cancellationToken)
    {
        var link = await db.ProblemArticles.SingleOrDefaultAsync(l => l.ProblemId == id && l.ArticleId == articleId, cancellationToken);
        if (link is null)
        {
            return FormsCommon.NotFound("article");
        }

        db.ProblemArticles.Remove(link);
        audit.Add(AuditCategory.Change, "problems.article.unlinked", "ticket", id.ToString(),
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["articleId"] = articleId });
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.NoContent();
    }

    private static async Task<List<ProblemArticleResponse>> LinkedArticlesAsync(ZelvoDbContext db, Guid problemId, CancellationToken cancellationToken)
    {
        var rows = await db.ProblemArticles.AsNoTracking().Where(l => l.ProblemId == problemId)
            .Join(db.KbArticles.AsNoTracking(), l => l.ArticleId, a => a.Id, (l, a) => new { Link = l, Article = a })
            .OrderBy(x => x.Link.LinkedAt).ToListAsync(cancellationToken);
        var versionIds = rows.Select(r => r.Article.PublishedVersionId ?? r.Article.WorkingVersionId).OfType<Guid>().ToList();
        var titles = await db.KbArticleVersions.AsNoTracking().Where(v => versionIds.Contains(v.Id)).ToDictionaryAsync(v => v.Id, v => v.Title, cancellationToken);
        return [.. rows.Select(r => new ProblemArticleResponse(r.Article.Id, r.Article.Number, r.Article.State, r.Article.Visibility,
            (r.Article.PublishedVersionId ?? r.Article.WorkingVersionId) is { } v && titles.TryGetValue(v, out var title) ? LocalizedTextDto.From(title) : null, r.Link.LinkedAt))];
    }

    internal static async Task<IResult> TimelineAsync(Guid id, ZelvoDbContext db, CancellationToken cancellationToken)
    {
        var problem = await db.Tickets.AsNoTracking().SingleOrDefaultAsync(t => t.Id == id && t.Type == TicketType.Problem, cancellationToken);
        if (problem is null)
        {
            return ProblemCore.NotFound();
        }

        var entries = new List<TimelineEntryResponse>();
        var self = new TimelineTicket(problem.Id, problem.Number, problem.Type, problem.Title);
        entries.Add(new(problem.CreatedAt, "problem.created", self));
        AddEnds(entries, problem, self, "problem");
        if (await db.KnownErrors.AsNoTracking().SingleOrDefaultAsync(e => e.ProblemId == id, cancellationToken) is { } error)
        {
            entries.Add(new(error.CreatedAt, "problem.known-error", self, error.Number));
        }

        var links = await db.ProblemIncidents.AsNoTracking().Where(l => l.ProblemId == id)
            .Join(db.Tickets.AsNoTracking(), l => l.IncidentId, t => t.Id, (l, t) => new { l.LinkedAt, Ticket = t }).ToListAsync(cancellationToken);
        foreach (var link in links)
        {
            var incident = new TimelineTicket(link.Ticket.Id, link.Ticket.Number, link.Ticket.Type, link.Ticket.Title);
            entries.Add(new(link.Ticket.CreatedAt, "incident.created", incident));
            entries.Add(new(link.LinkedAt, "incident.linked", incident));
            AddEnds(entries, link.Ticket, incident, "incident");
        }

        return TypedResults.Ok(entries.OrderBy(e => e.At).ThenBy(e => e.Kind, StringComparer.Ordinal).ToList());

        static void AddEnds(List<TimelineEntryResponse> entries, Ticket ticket, TimelineTicket reference, string kind)
        {
            if (ticket.ResolvedAt is { } resolved)
            {
                entries.Add(new(resolved, $"{kind}.resolved", reference));
            }

            if (ticket.ClosedAt is { } closed)
            {
                entries.Add(new(closed, $"{kind}.closed", reference));
            }
        }
    }

    internal static async Task<IResult> ProblemsOfIncidentAsync(Guid id, ZelvoDbContext db, CurrentUser user, CancellationToken cancellationToken)
    {
        if (!await db.Tickets.AnyAsync(t => t.Id == id && t.Type == TicketType.Incident, cancellationToken))
        {
            return IncidentEndpoints.NotFound();
        }

        var rows = await ProblemCore.Problems(db).AsNoTracking().Where(t => db.ProblemIncidents.Any(l => l.ProblemId == t.Id && l.IncidentId == id))
            .OrderByDescending(t => t.CreatedAt).Take(50).ToListAsync(cancellationToken);
        var context = await ProblemResponseContext.LoadAsync(db, user, rows, cancellationToken);
        return TypedResults.Ok(rows.Select(context.From).ToList());
    }
}

/// <summary>A problem's note.</summary>
internal sealed record ProblemCommentResponse(Guid Id, string Body, string Language, PersonResponse Author, DateTimeOffset CreatedAt);

/// <summary>A knowledge article linked to a problem.</summary>
internal sealed record ProblemArticleResponse(
    Guid Id, string Number, Data.Knowledge.ArticleState State, Data.Knowledge.ArticleVisibility Visibility, LocalizedTextDto? Title, DateTimeOffset LinkedAt);

/// <summary>The ticket of a timeline entry.</summary>
internal sealed record TimelineTicket(Guid Id, string Number, TicketType Type, string Title);

/// <summary>An entry of a problem's timeline: <c>problem.created</c>, <c>incident.linked</c>, <c>incident.resolved</c>, <c>problem.known-error</c>…</summary>
internal sealed record TimelineEntryResponse(DateTimeOffset At, string Kind, TimelineTicket Ticket, string? Reference = null);

/// <summary>Filters of the problem list.</summary>
public sealed class ProblemListQuery
{
    /// <summary>Only problems in this stage.</summary>
    public ProblemStage? Stage { get; init; }

    /// <summary>Only open problems (not resolved or closed).</summary>
    public bool? Open { get; init; }

    /// <summary>The problem with this number.</summary>
    public string? Number { get; init; }

    /// <summary><c>me</c>, <c>none</c> or a user id.</summary>
    public string? Assignee { get; init; }

    /// <summary><c>mine</c>, <c>none</c> or a group id.</summary>
    public string? Group { get; init; }

    /// <summary>Only this category.</summary>
    public Guid? CategoryId { get; init; }

    /// <summary>Only problems this incident is linked to.</summary>
    public Guid? IncidentId { get; init; }
}

/// <summary>A new problem.</summary>
public sealed class CreateProblemRequest
{
    /// <summary>Short summary.</summary>
    [Required]
    [StringLength(Ticket.TitleMaxLength, MinimumLength = 1)]
    public string? Title { get; init; }

    /// <summary>What is known so far.</summary>
    [StringLength(Ticket.DescriptionMaxLength)]
    public string? Description { get; init; }

    /// <summary>Category; when not given, the linked incidents' category if they share one.</summary>
    public Guid? CategoryId { get; init; }

    /// <summary>Priority 1 (highest) to 5; when not given, the linked incidents' highest, else 3.</summary>
    [Range(1, 5)]
    public int? Priority { get; init; }

    /// <summary>Incidents to link, at most 100.</summary>
    [MaxLength(ProblemCore.MaxIncidentsPerCall)]
    public IReadOnlyList<Guid>? IncidentIds { get; init; }
}

/// <summary>Changes to a problem.</summary>
public sealed class UpdateProblemRequest
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

    /// <summary>New priority 1–5.</summary>
    [Range(1, 5)]
    public int? Priority { get; init; }

    /// <summary>The root cause as found (empty clears it).</summary>
    [StringLength(ProblemRecord.RootCauseMaxLength)]
    public string? RootCause { get; init; }
}

/// <summary>The root-cause tools; each part given replaces the saved one.</summary>
public sealed class ProblemAnalysisRequest
{
    /// <summary>The 5 Whys.</summary>
    public FiveWhys? FiveWhys { get; init; }

    /// <summary>The fishbone.</summary>
    public Fishbone? Fishbone { get; init; }
}

/// <summary>A move of a problem.</summary>
public sealed class ProblemTransitionRequest
{
    /// <summary>Where to.</summary>
    [Required]
    public ProblemTarget? To { get; init; }

    /// <summary>Key of an active on-hold reason (OnHold).</summary>
    [StringLength(Ticket.ReasonKeyMaxLength)]
    public string? OnHoldReason { get; init; }

    /// <summary>How it was fixed (Resolved).</summary>
    [StringLength(Ticket.ResolutionNotesMaxLength)]
    public string? ResolutionNotes { get; init; }

    /// <summary>Also resolve the open linked incidents and their children with these notes (Resolved).</summary>
    public bool? ResolveIncidents { get; init; }
}

/// <summary>A problem's group and assignee.</summary>
public sealed class ProblemAssignmentRequest
{
    /// <summary>Group, or none.</summary>
    public Guid? GroupId { get; init; }

    /// <summary>Assignee, or none.</summary>
    public Guid? AssigneeId { get; init; }
}

/// <summary>A note.</summary>
public sealed class ProblemCommentRequest
{
    /// <summary>The note.</summary>
    [Required]
    [StringLength(TicketComment.BodyMaxLength)]
    public string? Body { get; init; }

    /// <summary><c>en</c> or <c>ar</c>; the author's language when not given.</summary>
    [StringLength(2)]
    public string? Language { get; init; }
}

/// <summary>An article to link, by number.</summary>
public sealed class LinkArticleRequest
{
    /// <summary>The article's number, for example <c>KB-000123</c>.</summary>
    [Required]
    [StringLength(30, MinimumLength = 1)]
    public string? Number { get; init; }
}

/// <summary>Incidents to link.</summary>
public sealed class LinkIncidentsRequest
{
    /// <summary>Incident ids, at most 100.</summary>
    [Required]
    [MaxLength(ProblemCore.MaxIncidentsPerCall)]
    public IReadOnlyList<Guid>? IncidentIds { get; init; }
}
