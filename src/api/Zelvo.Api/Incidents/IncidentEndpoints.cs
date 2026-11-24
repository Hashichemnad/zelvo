using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Zelvo.Api.Conventions;
using Zelvo.Api.ServiceDesk;
using Zelvo.Api.Identity;
using Zelvo.Api.Integrations;
using Zelvo.Api.Settings;
using Zelvo.Api.Sla;
using Zelvo.Api.Tenancy;
using Zelvo.Data;
using Zelvo.Data.Auditing;
using Zelvo.Data.Directory;
using Zelvo.Data.Identity;
using Zelvo.Data.Localization;
using Zelvo.Data.Sla;
using Zelvo.Data.Tenancy;
using Zelvo.Data.Tickets;
using Zelvo.Search;
using Zelvo.Tickets;
using Zelvo.Tickets.Sla;

namespace Zelvo.Api.Incidents;

/// <summary>
/// Incidents (module 1). Staff with <c>incidents.read</c> see every incident, filtered by field
/// rules; requesters see their own incidents through a smaller view and may report incidents and
/// reopen resolved ones. Every change is audited field by field.
/// </summary>
internal static partial class IncidentEndpoints
{
    /// <summary>Fields left out of the requester's view of their own incident.</summary>
    private static readonly string[] RequesterHidden = ["assignee", "impact", "urgency", "channel", "parent", "children", "nextStates", "group", "routingRule", "sla"];

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    public static RouteGroupBuilder MapIncidentEndpoints(this RouteGroupBuilder group)
    {
        var incidents = group.MapGroup("/incidents").WithTags("Incidents").RequireTenant();

        incidents.MapGet("/", ListAsync)
            .WithName("ListIncidents")
            .WithSummary("Incidents, newest first. Holders of incidents.read see all (fields filtered by their roles); everyone else sees the incidents they requested.")
            .RequireAuthorization()
            .Produces<PageResult<IncidentResponse>>();

        incidents.MapPost("/", CreateAsync)
            .WithName("CreateIncident")
            // These record the person who acts; integrations cannot do them yet (E1).
            .RequirePerson()
            .WithSummary("Report an incident. Requesters report their own (channel Portal). Holders of incidents.create can log one for someone else from any channel, set impact and urgency, assign it (incidents.assign), link a parent or follow up a closed incident. The number (for example INC-000123) and the priority come from the tenant's settings; audited.")
            .RequireAuthorization()
            .Produces<IncidentResponse>(StatusCodes.Status201Created)
            .ProducesValidationProblem();

        incidents.MapGet("/assignees", AssigneesAsync)
            .WithName("ListIncidentAssignees")
            .WithSummary("Active users who can be assigned incidents (their roles hold incidents.edit); with groupId, only that group's members.")
            .RequirePermission(Permissions.IncidentsAssign)
            .Produces<IReadOnlyList<PersonResponse>>();

        incidents.MapGet("/{id:guid}", GetAsync)
            .WithName("GetIncident")
            .WithSummary("One incident with its parent and children. Returns an ETag to send as If-Match with changes.")
            .RequireAuthorization()
            .Produces<IncidentResponse>();

        incidents.MapPatch("/{id:guid}", UpdateAsync)
            .WithName("UpdateIncident")
            .WithSummary("Change title, description, impact or urgency (the priority follows the matrix). Needs incidents.edit and edit access to each field; If-Match is checked when sent; audited.")
            .RequirePermission(Permissions.IncidentsEdit)
            .Produces<IncidentResponse>()
            .ProducesValidationProblem();

        incidents.MapPut("/{id:guid}/assignee", AssignAsync)
            .WithName("AssignIncident")
            .WithSummary("Assign the incident to an agent, or to nobody (null). A new incident becomes Assigned. Needs incidents.assign; audited.")
            .RequirePermission(Permissions.IncidentsAssign)
            .Produces<IncidentResponse>()
            .ProducesValidationProblem();

        incidents.MapPut("/{id:guid}/assignment", SetAssignmentAsync)
            .WithName("SetIncidentAssignment")
            .WithSummary("Set the incident's group and assignee together (task B2). The assignee must be a member of the group when a group is given; null removes either. Needs incidents.assign; audited.")
            .RequirePermission(Permissions.IncidentsAssign)
            .Produces<IncidentResponse>()
            .ProducesValidationProblem();

        incidents.MapPost("/{id:guid}/route", RouteAgainAsync)
            .WithName("RouteIncident")
            .WithSummary("Run the assignment rules again (\"Route again\") and set the group and assignee they give. Open incidents only; needs incidents.assign; audited.")
            .RequirePermission(Permissions.IncidentsAssign)
            .Produces<IncidentResponse>();

        incidents.MapPut("/{id:guid}/classification", SetClassificationAsync)
            .WithName("SetIncidentClassification")
            .WithSummary("Set the incident's category and location (task B2; null removes either). Does not route again. Needs incidents.edit and edit access to both fields; audited.")
            .RequirePermission(Permissions.IncidentsEdit)
            .Produces<IncidentResponse>()
            .ProducesValidationProblem();

        incidents.MapPost("/{id:guid}/transitions", TransitionAsync)
            .WithName("ChangeIncidentState")
            // These record the person who acts; integrations cannot do them yet (E1).
            .RequirePerson()
            .WithSummary("Move the incident to another state: InProgress, OnHold (with an active on-hold reason), Resolved (with resolution notes; resolves its open children too), Closed or Reopened. Requesters may reopen their own resolved incidents; everything else needs incidents.edit. Closed is final; audited.")
            .RequireAuthorization()
            .Produces<IncidentResponse>()
            .ProducesValidationProblem();

        incidents.MapPut("/{id:guid}/parent", SetParentAsync)
            .WithName("SetIncidentParent")
            .WithSummary("Make the incident a child of another incident, or remove the link (null). One level only: a parent has no parent, a child has no children. Needs incidents.edit; audited.")
            .RequirePermission(Permissions.IncidentsEdit)
            .Produces<IncidentResponse>()
            .ProducesValidationProblem();

        incidents.MapPut("/{id:guid}/major", SetMajorAsync)
            .WithName("SetMajorIncident")
            // These record the person who acts; integrations cannot do them yet (E1).
            .RequirePerson()
            .WithSummary("Declare the incident a major incident, or stand it down. Needs incidents.major; audited.")
            .RequirePermission(Permissions.IncidentsMajor)
            .Produces<IncidentResponse>();

        incidents.MapGet("/{id:guid}/updates", ListUpdatesAsync)
            .WithName("ListMajorIncidentUpdates")
            .WithSummary("The communication timeline of the incident and of its parent, newest first. Requesters see the updates meant for requesters.")
            .RequireAuthorization()
            .Produces<IReadOnlyList<MajorIncidentUpdateResponse>>();

        incidents.MapPost("/{id:guid}/updates", PostUpdateAsync)
            .WithName("PostMajorIncidentUpdate")
            // These record the person who acts; integrations cannot do them yet (E1).
            .RequirePerson()
            .WithSummary("Post an update to a major incident's timeline, in English and Arabic, for staff only or also for requesters (of the incident and its children). Needs incidents.major; audited.")
            .RequirePermission(Permissions.IncidentsMajor)
            .Produces<MajorIncidentUpdateResponse>(StatusCodes.Status201Created)
            .ProducesValidationProblem();

        incidents.MapGet("/{id:guid}/history", HistoryAsync)
            .WithName("GetIncidentHistory")
            .WithSummary("Every change of the incident from the audit log, oldest first, with old and new values of the fields the caller may see. Needs incidents.read.")
            .RequirePermission(Permissions.IncidentsRead)
            .Produces<IReadOnlyList<HistoryEntryResponse>>();

        incidents.MapIncidentAttachmentEndpoints();
        return group;
    }

    internal static async Task<Ok<JsonObject>> ListAsync(
        [AsParameters] IncidentListQuery query, ZelvoDbContext db, CurrentUser user, CancellationToken cancellationToken)
    {
        // Integrations have no account: without incidents.read they see none.
        var me = user.Account?.Id;
        var staff = user.Has(Permissions.IncidentsRead);
        var tickets = Incidents(db);
        if (!staff || query.Mine == true)
        {
            tickets = tickets.Where(t => t.RequesterId == me);
        }

        if (query.Number is { Length: > 0 } number)
        {
            var wanted = number.Trim().ToUpperInvariant();
            tickets = tickets.Where(t => t.Number == wanted);
        }

        if (query.State is { } state)
        {
            tickets = tickets.Where(t => t.State == state);
        }
        else if (query.Open == true)
        {
            tickets = tickets.Where(t => t.State != TicketState.Resolved && t.State != TicketState.Closed);
        }

        if (query.Priority is { } priority)
        {
            tickets = tickets.Where(t => t.Incident!.Priority == priority);
        }

        if (query.Major is { } major)
        {
            tickets = tickets.Where(t => t.Incident!.IsMajor == major);
        }

        if (staff && query.ParentId is { } parentId)
        {
            tickets = tickets.Where(t => t.ParentId == parentId);
        }

        if (staff)
        {
            tickets = query.Assignee switch
            {
                "me" => tickets.Where(t => t.AssigneeId == me),
                "none" => tickets.Where(t => t.AssigneeId == null),
                { } other when Guid.TryParse(other, out var assigneeId) => tickets.Where(t => t.AssigneeId == assigneeId),
                _ => tickets,
            };
        }

        if (staff)
        {
            tickets = query.Group switch
            {
                "mine" => tickets.Where(t => db.SupportGroupMembers.Any(m => m.GroupId == t.GroupId && m.UserId == me)),
                "none" => tickets.Where(t => t.GroupId == null),
                { } other when Guid.TryParse(other, out var groupId) => tickets.Where(t => t.GroupId == groupId),
                _ => tickets,
            };
        }

        if (query.Category is { } categoryId)
        {
            var below = await DescendantsAsync(db.Categories, categoryId, cancellationToken);
            tickets = tickets.Where(t => t.CategoryId != null && below.Contains(t.CategoryId.Value));
        }

        if (query.Location is { } locationId)
        {
            var below = await DescendantsAsync(db.Locations, locationId, cancellationToken);
            tickets = tickets.Where(t => t.LocationId != null && below.Contains(t.LocationId.Value));
        }

        // Task B3: tickets past an escalation threshold, or past a target.
        if (staff)
        {
            tickets = query.Sla switch
            {
                "atRisk" => tickets.Where(t => db.TicketSlas.Any(c => c.TicketId == t.Id && c.State != SlaClockState.Stopped && c.BreachedAt == null && c.ReachedPercent > 0)),
                "breached" => tickets.Where(t => db.TicketSlas.Any(c => c.TicketId == t.Id && c.BreachedAt != null)),
                _ => tickets,
            };
        }

        var ordered = staff && query.Sort == "due"
            // Soonest running target first; tickets without one last.
            ? tickets.OrderBy(t => db.TicketSlas.Where(c => c.TicketId == t.Id && c.DueAt != null).Min(c => c.DueAt) ?? DateTimeOffset.MaxValue)
                .ThenByDescending(t => t.CreatedAt).ThenByDescending(t => t.Id)
            : tickets.OrderByDescending(t => t.CreatedAt).ThenByDescending(t => t.Id);
        var total = await ordered.LongCountAsync(cancellationToken);
        var page = await ordered.Skip(query.Skip).Take(query.Size).ToListAsync(cancellationToken);
        var parents = await LinksAsync(db, page.Where(t => t.ParentId is not null).Select(t => t.ParentId!.Value), cancellationToken);
        var refs = await DirectoryRefs.LoadAsync(db, cancellationToken);
        var slas = await TicketSlaView.LoadAsync(db, staff ? [.. page.Select(t => t.Id)] : [], cancellationToken);
        var now = DateTimeOffset.UtcNow;
        var json = new JsonObject
        {
            ["items"] = new JsonArray([.. page.Select(t => (JsonNode)View(user, t, IncidentResponse.From(t, user, parents.GetValueOrDefault(t.ParentId ?? Guid.Empty), null, [], refs, slas.For(t.Id, now))))]),
            ["page"] = query.PageNumber,
            ["pageSize"] = query.Size,
            ["totalCount"] = total,
        };
        return TypedResults.Ok(json);
    }

    internal static async Task<Results<Ok<JsonObject>, ProblemHttpResult>> GetAsync(
        Guid id, HttpContext http, ZelvoDbContext db, CurrentUser user, CancellationToken cancellationToken)
    {
        var ticket = await FindVisibleAsync(db, user, id, cancellationToken);
        if (ticket is null)
        {
            return NotFound();
        }

        return TypedResults.Ok(await DetailAsync(http, db, user, ticket, cancellationToken));
    }

    internal static async Task<Results<Created<JsonObject>, ValidationProblem, ProblemHttpResult>> CreateAsync(
        CreateIncidentRequest request, HttpContext http, ZelvoDbContext db, CurrentUser user, ITenantContext tenant,
        TenantSettingsStore settings, ISearchIndexer indexer, CancellationToken cancellationToken)
    {
        // A person, or an AI agent through Zelvo's MCP server (task E6): the agent names the
        // requester and the ticket records the agent (product owner decision 2026-11-24).
        var me = user.Account?.Id;
        var agent = me is null ? user.ApiClient?.Id : null;
        var onBehalf = user.Has(Permissions.IncidentsCreate);
        if (me is null && (agent is null || !onBehalf))
        {
            return (ProblemHttpResult)ApiProblem.Create(StatusCodes.Status403Forbidden, "forbidden", "Only people and AI agents with incidents.create may log incidents.");
        }

        if (agent is null && request.Channel == TicketChannel.AiAgent)
        {
            return UserEndpoints.Validation("channel", "Only AI agents raise incidents through the AI agent channel.");
        }

        if (me is null && request.RequesterId is null)
        {
            return UserEndpoints.Validation("requesterId", "An AI agent names the requester of the incident.");
        }
        var asked = new List<string>();
        if (request.RequesterId is not null && request.RequesterId != me) asked.Add("requesterId");
        if (request.Channel is not null && request.Channel != TicketChannel.Portal) asked.Add("channel");
        if (request.Impact is not null) asked.Add("impact");
        if (request.Urgency is not null) asked.Add("urgency");
        if (request.ParentId is not null) asked.Add("parentId");
        if (!onBehalf && asked.Count > 0)
        {
            return (ProblemHttpResult)ApiProblem.Create(StatusCodes.Status403Forbidden, "forbidden",
                $"Only staff who log incidents for others may set {string.Join(", ", asked)}.");
        }

        if ((request.AssigneeId is not null || request.GroupId is not null) && !user.Has(Permissions.IncidentsAssign))
        {
            return (ProblemHttpResult)ApiProblem.Create(StatusCodes.Status403Forbidden, "forbidden", "You may not assign incidents.");
        }

        if (await ClassificationProblemAsync(db, request.CategoryId, request.LocationId, null, cancellationToken) is { } classificationProblem)
        {
            return classificationProblem;
        }

        if (request.GroupId is { } wantedGroup && !await db.SupportGroups.AnyAsync(g => g.Id == wantedGroup && g.Active, cancellationToken))
        {
            return UserEndpoints.Validation("groupId", "Choose an active group.");
        }

        if (request.GroupId is { } inGroup && request.AssigneeId is { } member && !await IsMemberAsync(db, inGroup, member, cancellationToken))
        {
            return NotInGroup();
        }

        var requesterId = request.RequesterId ?? me!.Value;
        if (requesterId != me && !await db.Users.AnyAsync(u => u.Id == requesterId && u.Status == UserStatus.Active, cancellationToken))
        {
            return UserEndpoints.Validation("requesterId", "The requester is not an active user of this organisation.");
        }

        if (request.AssigneeId is { } assigneeId && !await Assignable(db).AnyAsync(u => u.Id == assigneeId, cancellationToken))
        {
            return UserEndpoints.Validation("assigneeId", "The assignee cannot be assigned incidents.");
        }

        var impact = request.Impact ?? DefaultLevel;
        var urgency = request.Urgency ?? DefaultLevel;
        if (!PriorityMatrix.IsLevel(impact) || !PriorityMatrix.IsLevel(urgency))
        {
            return UserEndpoints.Validation(PriorityMatrix.IsLevel(impact) ? "urgency" : "impact", $"Use a level from 1 to {PriorityMatrix.Levels}.");
        }

        Ticket? followUpOf = null;
        if (request.FollowUpOfId is { } followUpOfId)
        {
            followUpOf = await FindVisibleAsync(db, user, followUpOfId, cancellationToken);
            if (followUpOf is null || followUpOf.State != TicketState.Closed)
            {
                return UserEndpoints.Validation("followUpOfId", "A follow-up refers to a closed incident you can see.");
            }
        }

        Ticket? parent = null;
        if (request.ParentId is { } parentId)
        {
            parent = await Incidents(db).SingleOrDefaultAsync(t => t.Id == parentId, cancellationToken);
            if (parent is null)
            {
                return UserEndpoints.Validation("parentId", "The parent incident does not exist.");
            }

            if (ParentProblem(parent) is { } problem)
            {
                return problem;
            }
        }

        var matrix = await settings.GetAsync(IncidentSettings.PriorityMatrix, cancellationToken);
        var prefixes = await settings.GetAsync(IncidentSettings.NumberPrefixes, cancellationToken);
        var now = DateTimeOffset.UtcNow;

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var sequence = await TicketNumbers.NextAsync(db, tenant.TenantId!.Value, TicketType.Incident, cancellationToken);
        var ticket = new Ticket
        {
            Type = TicketType.Incident,
            Number = TicketNumbers.Format(prefixes[TicketType.Incident], sequence),
            Sequence = sequence,
            Title = request.Title!.Trim(),
            Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim(),
            Channel = agent is not null ? TicketChannel.AiAgent : onBehalf ? request.Channel ?? TicketChannel.Phone : TicketChannel.Portal,
            RequesterId = requesterId,
            CreatedById = me,
            CreatedByApiClientId = agent,
            ParentId = parent?.Id,
            FollowUpOfId = followUpOf?.Id,
            CategoryId = request.CategoryId,
            LocationId = request.LocationId,
            GroupId = request.GroupId,
            CreatedAt = now,
            UpdatedAt = now,
        };
        ticket.Incident = new Incident { TicketId = ticket.Id, Impact = impact, Urgency = urgency, Priority = matrix.PriorityOf(impact, urgency) };
        if (request.AssigneeId is { } assignee)
        {
            TicketWorkflow.Assign(ticket, assignee, now);
        }
        else if (request.GroupId is null)
        {
            // Task B2: placed by the assignment rules (product owner decision 2026-11-23).
            await RouteAsync(db, settings, ticket, now, cancellationToken);
        }

        db.Tickets.Add(ticket);
        await IndexAsync(db, indexer, [ticket], cancellationToken);
        // Task B3: the SLA clocks start with the ticket.
        await http.RequestServices.GetRequiredService<SlaTracker>().UpdateAsync([ticket], now, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        var created = await Incidents(db).AsNoTracking().SingleAsync(t => t.Id == ticket.Id, cancellationToken);
        return TypedResults.Created($"/api/v1/incidents/{ticket.Id}", await DetailAsync(http, db, user, created, cancellationToken));
    }

    internal static async Task<Ok<List<PersonResponse>>> AssigneesAsync(Guid? groupId, ZelvoDbContext db, CancellationToken cancellationToken)
    {
        var people = Assignable(db);
        if (groupId is { } group)
        {
            people = people.Where(u => db.SupportGroupMembers.Any(m => m.GroupId == group && m.UserId == u.Id));
        }

        return TypedResults.Ok(await people.OrderBy(u => u.DisplayName ?? u.Email).Take(500)
            .Select(u => new PersonResponse(u.Id, u.DisplayName)).ToListAsync(cancellationToken));
    }

    internal static async Task<Results<Ok<JsonObject>, ValidationProblem, ProblemHttpResult>> UpdateAsync(
        Guid id, UpdateIncidentRequest request, HttpContext http, ZelvoDbContext db, CurrentUser user,
        TenantSettingsStore settings, ISearchIndexer indexer, CancellationToken cancellationToken)
    {
        var changed = new List<string>();
        if (request.Title is not null) changed.Add("title");
        if (request.Description is not null) changed.Add("description");
        if (request.Impact is not null) changed.Add("impact");
        if (request.Urgency is not null) changed.Add("urgency");
        var refused = FieldFilter.NotEditable(user, PermissionCatalog.Incidents, changed);
        if (refused.Count > 0)
        {
            return FieldsReadOnly(refused);
        }

        if (request.Impact is { } i && !PriorityMatrix.IsLevel(i) || request.Urgency is { } u && !PriorityMatrix.IsLevel(u))
        {
            return UserEndpoints.Validation(request.Impact is { } level && !PriorityMatrix.IsLevel(level) ? "impact" : "urgency", $"Use a level from 1 to {PriorityMatrix.Levels}.");
        }

        var ticket = await Incidents(db).SingleOrDefaultAsync(t => t.Id == id, cancellationToken);
        if (ticket is null)
        {
            return NotFound();
        }

        if (VersionProblem(http, db, ticket) is { } versionProblem)
        {
            return versionProblem;
        }

        if (ticket.State == TicketState.Closed)
        {
            return Closed();
        }

        if (request.Title is not null) ticket.Title = request.Title.Trim();
        if (request.Description is not null) ticket.Description = request.Description.Trim() is { Length: > 0 } d ? d : null;
        var incident = ticket.Incident!;
        incident.Impact = request.Impact ?? incident.Impact;
        incident.Urgency = request.Urgency ?? incident.Urgency;
        incident.Priority = (await settings.GetAsync(IncidentSettings.PriorityMatrix, cancellationToken)).PriorityOf(incident.Impact, incident.Urgency);
        ticket.UpdatedAt = DateTimeOffset.UtcNow;
        return await SaveAsync(http, db, user, indexer, ticket, [ticket], cancellationToken);
    }

    internal static async Task<Results<Ok<JsonObject>, ValidationProblem, ProblemHttpResult>> AssignAsync(
        Guid id, AssignIncidentRequest request, HttpContext http, ZelvoDbContext db, CurrentUser user,
        ISearchIndexer indexer, CancellationToken cancellationToken)
    {
        var ticket = await Incidents(db).SingleOrDefaultAsync(t => t.Id == id, cancellationToken);
        if (ticket is null)
        {
            return NotFound();
        }

        if (VersionProblem(http, db, ticket) is { } versionProblem)
        {
            return versionProblem;
        }

        if (ticket.State == TicketState.Closed)
        {
            return Closed();
        }

        if (request.AssigneeId is { } assigneeId && !await Assignable(db).AnyAsync(u => u.Id == assigneeId, cancellationToken))
        {
            return UserEndpoints.Validation("assigneeId", "The assignee cannot be assigned incidents.");
        }

        if (ticket.GroupId is { } group && request.AssigneeId is { } member && !await IsMemberAsync(db, group, member, cancellationToken))
        {
            return NotInGroup();
        }

        TicketWorkflow.Assign(ticket, request.AssigneeId, DateTimeOffset.UtcNow);
        ticket.RoutingRuleId = null;
        return await SaveAsync(http, db, user, indexer, ticket, [ticket], cancellationToken);
    }

    internal static async Task<Results<Ok<JsonObject>, ValidationProblem, ProblemHttpResult>> SetAssignmentAsync(
        Guid id, SetAssignmentRequest request, HttpContext http, ZelvoDbContext db, CurrentUser user,
        ISearchIndexer indexer, CancellationToken cancellationToken)
    {
        var ticket = await Incidents(db).SingleOrDefaultAsync(t => t.Id == id, cancellationToken);
        if (ticket is null)
        {
            return NotFound();
        }

        if (VersionProblem(http, db, ticket) is { } versionProblem)
        {
            return versionProblem;
        }

        if (ticket.State == TicketState.Closed)
        {
            return Closed();
        }

        // Keeping the current group is fine even if it became inactive; a new one must be active.
        if (request.GroupId is { } groupId && groupId != ticket.GroupId && !await db.SupportGroups.AnyAsync(g => g.Id == groupId && g.Active, cancellationToken))
        {
            return UserEndpoints.Validation("groupId", "Choose an active group.");
        }

        if (request.AssigneeId is { } assigneeId && !await Assignable(db).AnyAsync(u => u.Id == assigneeId, cancellationToken))
        {
            return UserEndpoints.Validation("assigneeId", "The assignee cannot be assigned incidents.");
        }

        if (request.GroupId is { } group && request.AssigneeId is { } member && !await IsMemberAsync(db, group, member, cancellationToken))
        {
            return NotInGroup();
        }

        ticket.GroupId = request.GroupId;
        ticket.RoutingRuleId = null;
        TicketWorkflow.Assign(ticket, request.AssigneeId, DateTimeOffset.UtcNow);
        return await SaveAsync(http, db, user, indexer, ticket, [ticket], cancellationToken);
    }

    internal static async Task<Results<Ok<JsonObject>, ValidationProblem, ProblemHttpResult>> RouteAgainAsync(
        Guid id, HttpContext http, ZelvoDbContext db, CurrentUser user, TenantSettingsStore settings,
        ISearchIndexer indexer, CancellationToken cancellationToken)
    {
        var ticket = await Incidents(db).SingleOrDefaultAsync(t => t.Id == id, cancellationToken);
        if (ticket is null)
        {
            return NotFound();
        }

        if (VersionProblem(http, db, ticket) is { } versionProblem)
        {
            return versionProblem;
        }

        if (ticket.State is TicketState.Resolved or TicketState.Closed)
        {
            return (ProblemHttpResult)ApiProblem.Create(StatusCodes.Status409Conflict, "ticket-not-open", "Only open incidents can be routed again.");
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await RouteAsync(db, settings, ticket, DateTimeOffset.UtcNow, cancellationToken);
        var result = await SaveAsync(http, db, user, indexer, ticket, [ticket], cancellationToken);
        if (result.Result is Ok<JsonObject>)
        {
            await transaction.CommitAsync(cancellationToken);
        }

        return result;
    }

    internal static async Task<Results<Ok<JsonObject>, ValidationProblem, ProblemHttpResult>> SetClassificationAsync(
        Guid id, SetClassificationRequest request, HttpContext http, ZelvoDbContext db, CurrentUser user,
        ISearchIndexer indexer, CancellationToken cancellationToken)
    {
        if (FieldFilter.NotEditable(user, PermissionCatalog.Incidents, ["category", "location"]) is { Count: > 0 } refused)
        {
            return FieldsReadOnly(refused);
        }

        var ticket = await Incidents(db).SingleOrDefaultAsync(t => t.Id == id, cancellationToken);
        if (ticket is null)
        {
            return NotFound();
        }

        if (VersionProblem(http, db, ticket) is { } versionProblem)
        {
            return versionProblem;
        }

        if (ticket.State == TicketState.Closed)
        {
            return Closed();
        }

        if (await ClassificationProblemAsync(db, request.CategoryId, request.LocationId, ticket, cancellationToken) is { } problem)
        {
            return problem;
        }

        ticket.CategoryId = request.CategoryId;
        ticket.LocationId = request.LocationId;
        ticket.UpdatedAt = DateTimeOffset.UtcNow;
        return await SaveAsync(http, db, user, indexer, ticket, [ticket], cancellationToken);
    }

    /// <summary>
    /// Places the ticket by the assignment rules (task B2). Call inside the transaction that saves
    /// it: round-robin locks the group until the commit.
    /// </summary>
    internal static async Task RouteAsync(ZelvoDbContext db, TenantSettingsStore settings, Ticket ticket, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var requesterLanguage = await db.Users.AsNoTracking().Where(u => u.Id == ticket.RequesterId).Select(u => u.PreferredLanguage).SingleOrDefaultAsync(cancellationToken);
        var language = LocalizationSettings.IsLanguage(requesterLanguage)
            ? requesterLanguage!
            : await settings.GetAsync(LocalizationSettings.DefaultLanguage, cancellationToken);
        var fallback = await settings.GetAsync(AssignmentSettings.Default, cancellationToken);
        var decision = await TicketRouter.RouteAsync(db, new RoutingRequest(ticket.CategoryId, ticket.LocationId, language), fallback, now, cancellationToken);
        ticket.GroupId = decision.GroupId;
        ticket.RoutingRuleId = decision.Rule?.Id;
        TicketWorkflow.Assign(ticket, decision.AssigneeId, now);
    }

    private static Task<bool> IsMemberAsync(ZelvoDbContext db, Guid groupId, Guid userId, CancellationToken cancellationToken) =>
        db.SupportGroupMembers.AnyAsync(m => m.GroupId == groupId && m.UserId == userId, cancellationToken);

    private static ValidationProblem NotInGroup() => UserEndpoints.Validation("assigneeId", "The assignee is not a member of the group.");

    /// <summary>New categories and locations must be selectable; keeping the current ones is fine.</summary>
    private static async Task<ValidationProblem?> ClassificationProblemAsync(
        ZelvoDbContext db, Guid? categoryId, Guid? locationId, Ticket? current, CancellationToken cancellationToken)
    {
        if (categoryId is { } category && category != current?.CategoryId && !await TreeEndpoints.IsSelectableAsync(db.Categories, category, cancellationToken))
        {
            return UserEndpoints.Validation("categoryId", "Choose an active category.");
        }

        if (locationId is { } location && location != current?.LocationId && !await TreeEndpoints.IsSelectableAsync(db.Locations, location, cancellationToken))
        {
            return UserEndpoints.Validation("locationId", "Choose an active location.");
        }

        return null;
    }

    /// <summary>The node and every node below it.</summary>
    private static async Task<List<Guid>> DescendantsAsync<TNode>(DbSet<TNode> set, Guid id, CancellationToken cancellationToken)
        where TNode : TreeNode
    {
        var all = await set.AsNoTracking().Select(n => new { n.Id, n.ParentId }).ToListAsync(cancellationToken);
        var found = new List<Guid> { id };
        for (var i = 0; i < found.Count; i++)
        {
            found.AddRange(all.Where(n => n.ParentId == found[i]).Select(n => n.Id));
        }

        return found;
    }

    internal static async Task<Results<Ok<JsonObject>, ValidationProblem, ProblemHttpResult>> TransitionAsync(
        Guid id, ChangeIncidentStateRequest request, HttpContext http, ZelvoDbContext db, CurrentUser user,
        TenantSettingsStore settings, ISearchIndexer indexer, CancellationToken cancellationToken)
    {
        var ticket = await FindVisibleAsync(db, user, id, cancellationToken);
        if (ticket is null)
        {
            return NotFound();
        }

        var to = request.State!.Value;
        var mayEdit = user.Has(Permissions.IncidentsEdit);
        var requesterReopens = to == TicketState.Reopened && user.Account is not null && ticket.RequesterId == user.Account.Id;
        if (!mayEdit && !requesterReopens)
        {
            return (ProblemHttpResult)ApiProblem.Create(StatusCodes.Status403Forbidden, "forbidden",
                "Requesters may only reopen their resolved incidents.");
        }

        if (VersionProblem(http, db, ticket) is { } versionProblem)
        {
            return versionProblem;
        }

        if (to == TicketState.OnHold)
        {
            var reasons = await settings.GetAsync(IncidentSettings.OnHoldReasons, cancellationToken);
            if (!reasons.Any(r => r.Active && r.Key == request.OnHoldReason))
            {
                return UserEndpoints.Validation("onHoldReason", "Choose one of the active on-hold reasons.");
            }
        }

        switch (TicketWorkflow.Check(ticket, to, request.OnHoldReason, request.ResolutionNotes))
        {
            case TransitionError.NotAllowed:
                return ticket.State == TicketState.Closed ? Closed() : (ProblemHttpResult)ApiProblem.Create(StatusCodes.Status409Conflict,
                    "transition-not-allowed", $"An incident cannot move from {ticket.State} to {to}.");
            case TransitionError.OnHoldReasonRequired:
                return UserEndpoints.Validation("onHoldReason", "Choose why the incident is on hold.");
            case TransitionError.ResolutionNotesRequired:
                return UserEndpoints.Validation("resolutionNotes", "Say how the incident was resolved.");
            case TransitionError.AssigneeRequired:
                return (ProblemHttpResult)ApiProblem.Create(StatusCodes.Status409Conflict, "assignee-required", "Assign the incident before starting work on it.");
        }

        var now = DateTimeOffset.UtcNow;
        TicketWorkflow.Apply(ticket, to, request.OnHoldReason, request.ResolutionNotes, now);
        var changedTickets = new List<Ticket> { ticket };

        // Story (module 1): related incidents under one parent are resolved together.
        if (to == TicketState.Resolved)
        {
            var children = await Incidents(db).Where(t => t.ParentId == ticket.Id && t.State != TicketState.Resolved && t.State != TicketState.Closed)
                .ToListAsync(cancellationToken);
            foreach (var child in children.Where(c => TicketWorkflow.Check(c, TicketState.Resolved, null, ticket.ResolutionNotes) == TransitionError.None))
            {
                TicketWorkflow.Apply(child, TicketState.Resolved, null, ticket.ResolutionNotes, now);
                changedTickets.Add(child);
            }
        }

        return await SaveAsync(http, db, user, indexer, ticket, changedTickets, cancellationToken);
    }

    internal static async Task<Results<Ok<JsonObject>, ValidationProblem, ProblemHttpResult>> SetParentAsync(
        Guid id, SetIncidentParentRequest request, HttpContext http, ZelvoDbContext db, CurrentUser user,
        ISearchIndexer indexer, CancellationToken cancellationToken)
    {
        var ticket = await Incidents(db).SingleOrDefaultAsync(t => t.Id == id, cancellationToken);
        if (ticket is null)
        {
            return NotFound();
        }

        if (VersionProblem(http, db, ticket) is { } versionProblem)
        {
            return versionProblem;
        }

        if (ticket.State == TicketState.Closed)
        {
            return Closed();
        }

        if (request.ParentId is { } parentId)
        {
            if (parentId == ticket.Id)
            {
                return UserEndpoints.Validation("parentId", "An incident cannot be its own parent.");
            }

            var parent = await Incidents(db).SingleOrDefaultAsync(t => t.Id == parentId, cancellationToken);
            if (parent is null)
            {
                return UserEndpoints.Validation("parentId", "The parent incident does not exist.");
            }

            if (ParentProblem(parent) is { } problem)
            {
                return problem;
            }

            if (await db.Tickets.AnyAsync(t => t.ParentId == ticket.Id, cancellationToken))
            {
                return (ProblemHttpResult)ApiProblem.Create(StatusCodes.Status409Conflict, "incident-has-children",
                    "This incident is a parent itself; incidents are grouped one level deep.");
            }
        }

        ticket.ParentId = request.ParentId;
        ticket.UpdatedAt = DateTimeOffset.UtcNow;
        return await SaveAsync(http, db, user, indexer, ticket, [ticket], cancellationToken);
    }

    internal static async Task<Results<Ok<JsonObject>, ValidationProblem, ProblemHttpResult>> SetMajorAsync(
        Guid id, SetMajorIncidentRequest request, HttpContext http, ZelvoDbContext db, CurrentUser user,
        ISearchIndexer indexer, CancellationToken cancellationToken)
    {
        var ticket = await Incidents(db).SingleOrDefaultAsync(t => t.Id == id, cancellationToken);
        if (ticket is null)
        {
            return NotFound();
        }

        if (ticket.State == TicketState.Closed)
        {
            return Closed();
        }

        var incident = ticket.Incident!;
        if (incident.IsMajor != request.IsMajor)
        {
            var now = DateTimeOffset.UtcNow;
            incident.IsMajor = request.IsMajor;
            if (request.IsMajor)
            {
                incident.MajorDeclaredAt = now;
                incident.MajorDeclaredById = user.Account!.Id;
            }

            ticket.UpdatedAt = now;
        }

        return await SaveAsync(http, db, user, indexer, ticket, [ticket], cancellationToken);
    }

    internal static async Task<Results<Ok<List<MajorIncidentUpdateResponse>>, ProblemHttpResult>> ListUpdatesAsync(
        Guid id, ZelvoDbContext db, CurrentUser user, CancellationToken cancellationToken)
    {
        var ticket = await FindVisibleAsync(db, user, id, cancellationToken);
        if (ticket is null)
        {
            return NotFound();
        }

        var staff = user.Has(Permissions.IncidentsRead);
        var ids = ticket.ParentId is { } parentId ? new[] { ticket.Id, parentId } : [ticket.Id];
        var updates = await db.MajorIncidentUpdates.AsNoTracking()
            .Where(u => ids.Contains(u.TicketId) && (staff || u.Audience == UpdateAudience.Requesters))
            .OrderByDescending(u => u.PostedAt).ThenByDescending(u => u.Id).Take(500)
            .ToListAsync(cancellationToken);
        var numbers = await db.Tickets.AsNoTracking().Where(t => ids.Contains(t.Id)).ToDictionaryAsync(t => t.Id, t => t.Number, cancellationToken);
        var posters = await Names(db, updates.Select(u => u.PostedById), cancellationToken);
        return TypedResults.Ok(updates.Select(u => MajorIncidentUpdateResponse.From(u, numbers[u.TicketId], posters)).ToList());
    }

    internal static async Task<Results<Created<MajorIncidentUpdateResponse>, ValidationProblem, ProblemHttpResult>> PostUpdateAsync(
        Guid id, PostMajorIncidentUpdateRequest request, ZelvoDbContext db, CurrentUser user, IAuditLog audit, CancellationToken cancellationToken)
    {
        var ticket = await Incidents(db).SingleOrDefaultAsync(t => t.Id == id, cancellationToken);
        if (ticket is null)
        {
            return NotFound();
        }

        if (!ticket.Incident!.IsMajor)
        {
            return (ProblemHttpResult)ApiProblem.Create(StatusCodes.Status409Conflict, "not-major-incident", "Declare a major incident before posting to its timeline.");
        }

        var body = new LocalizedText(request.En, request.Ar);
        if (!body.HasAnyValue)
        {
            return UserEndpoints.Validation("en", "Write the update in English or Arabic.");
        }

        var update = new MajorIncidentUpdate
        {
            TicketId = ticket.Id,
            Body = body,
            Audience = request.Audience ?? UpdateAudience.Internal,
            PostedById = user.Account!.Id,
        };
        db.MajorIncidentUpdates.Add(update);
        audit.Add(AuditCategory.Change, "incidents.update.posted", "ticket", ticket.Id.ToString(),
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["updateId"] = update.Id.ToString(), ["audience"] = update.Audience.ToString() });
        await db.SaveChangesAsync(cancellationToken);
        var posters = await Names(db, [update.PostedById], cancellationToken);
        return TypedResults.Created($"/api/v1/incidents/{ticket.Id}/updates", MajorIncidentUpdateResponse.From(update, ticket.Number, posters));
    }

    internal static async Task<Results<Ok<List<HistoryEntryResponse>>, ProblemHttpResult>> HistoryAsync(
        Guid id, ZelvoDbContext db, CurrentUser user, CancellationToken cancellationToken)
    {
        if (!await Incidents(db).AnyAsync(t => t.Id == id, cancellationToken))
        {
            return NotFound();
        }

        var entityId = id.ToString();
        var events = await db.AuditEvents.AsNoTracking()
            .Where(e => (e.EntityType == "ticket" || e.EntityType == "incident") && e.EntityId == entityId)
            .OrderByDescending(e => e.Sequence).Take(1000)
            .ToListAsync(cancellationToken);
        events.Reverse();

        // People and incidents are shown by name and number rather than by id.
        var entries = events.Select(e => HistoryEntryResponse.From(e, user)).ToList();
        var ids = entries.SelectMany(e => e.Changes ?? [])
            .Where(c => HistoryEntryResponse.ReferenceFields.Contains(c.Key))
            .SelectMany(c => new[] { c.Value?["old"], c.Value?["new"] })
            .Select(v => v?.GetValueKind() == JsonValueKind.String && Guid.TryParse(v.GetValue<string>(), out var g) ? g : Guid.Empty)
            .Where(g => g != Guid.Empty).Distinct().ToList();
        var people = await Names(db, ids, cancellationToken);
        var numbers = await db.Tickets.AsNoTracking().Where(t => ids.Contains(t.Id)).ToDictionaryAsync(t => t.Id, t => t.Number, cancellationToken);
        var refs = await DirectoryRefs.LoadAsync(db, cancellationToken);
        var language = user.Account?.PreferredLanguage ?? LocalizedText.English;
        foreach (var (key, value) in refs.Names(language))
        {
            numbers.TryAdd(key, value);
        }

        foreach (var change in entries.SelectMany(e => e.Changes ?? []).Where(c => HistoryEntryResponse.ReferenceFields.Contains(c.Key)).ToList())
        {
            foreach (var side in new[] { "old", "new" })
            {
                if (change.Value?[side] is JsonValue value && value.GetValueKind() == JsonValueKind.String && Guid.TryParse(value.GetValue<string>(), out var referenced))
                {
                    change.Value[side] = people.TryGetValue(referenced, out var name) ? name : numbers.GetValueOrDefault(referenced, referenced.ToString());
                }
            }
        }

        return TypedResults.Ok(entries);
    }

    // --- Shared helpers ---

    /// <summary>Impact and urgency of incidents reported without them: the middle level.</summary>
    internal const int DefaultLevel = 2;

    internal static IQueryable<Ticket> Incidents(ZelvoDbContext db) =>
        db.Tickets.Include(t => t.Incident).Include(t => t.Requester).Include(t => t.Assignee).Where(t => t.Type == TicketType.Incident);

    /// <summary>The incident if the caller may see it: staff see all, others their own. Others get null (404), never 403.</summary>
    internal static async Task<Ticket?> FindVisibleAsync(ZelvoDbContext db, CurrentUser user, Guid id, CancellationToken cancellationToken)
    {
        var ticket = await Incidents(db).SingleOrDefaultAsync(t => t.Id == id, cancellationToken).ConfigureAwait(false);
        return ticket is not null && (user.Has(Permissions.IncidentsRead) || ticket.RequesterId == user.Account?.Id) ? ticket : null;
    }

    private static IQueryable<UserAccount> Assignable(ZelvoDbContext db) =>
        db.Users.Where(u => u.Status == UserStatus.Active
            && u.Roles.Any(r => r.Role!.Permissions.Any(p => p.Permission == Permissions.IncidentsEdit)));

    private static ProblemHttpResult? ParentProblem(Ticket parent)
    {
        if (parent.ParentId is not null)
        {
            return (ProblemHttpResult)ApiProblem.Create(StatusCodes.Status409Conflict, "parent-has-parent",
                "The parent is a child of another incident; incidents are grouped one level deep.");
        }

        return parent.State == TicketState.Closed ? Closed() : null;
    }

    /// <summary>Checks If-Match (when sent) and makes the save check the version too.</summary>
    internal static ProblemHttpResult? VersionProblem(HttpContext http, ZelvoDbContext db, Ticket ticket)
    {
        var ifMatch = http.Request.Headers.IfMatch.ToString();
        if (string.IsNullOrEmpty(ifMatch))
        {
            return null;
        }

        if (!uint.TryParse(ifMatch.Trim().Trim('"').Replace("W/", "", StringComparison.Ordinal).Trim('"'), out var version) || version != ticket.Version)
        {
            return VersionMismatch();
        }

        db.Entry(ticket).Property(t => t.Version).OriginalValue = version;
        return null;
    }

    private static async Task<Results<Ok<JsonObject>, ValidationProblem, ProblemHttpResult>> SaveAsync(
        HttpContext http, ZelvoDbContext db, CurrentUser user, ISearchIndexer indexer, Ticket ticket, List<Ticket> changed, CancellationToken cancellationToken)
    {
        await IndexAsync(db, indexer, changed, cancellationToken);
        await using var transaction = db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(cancellationToken) : null;
        try
        {
            // Task B3: every change can move SLA clocks (state, priority, category, group).
            await http.RequestServices.GetRequiredService<SlaTracker>().UpdateAsync(changed, DateTimeOffset.UtcNow, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            if (transaction is not null)
            {
                await transaction.CommitAsync(cancellationToken);
            }
        }
        catch (DbUpdateConcurrencyException)
        {
            return VersionMismatch();
        }

        var saved = await Incidents(db).AsNoTracking().SingleAsync(t => t.Id == ticket.Id, cancellationToken);
        return TypedResults.Ok(await DetailAsync(http, db, user, saved, cancellationToken));
    }

    internal static async Task IndexAsync(ZelvoDbContext db, ISearchIndexer indexer, IEnumerable<Ticket> tickets, CancellationToken cancellationToken)
    {
        var staff = await IncidentSearch.StaffRolesAsync(db, cancellationToken).ConfigureAwait(false);
        foreach (var ticket in tickets)
        {
            indexer.Upsert(IncidentSearch.Document(ticket, staff));
        }
    }

    private static async Task<JsonObject> DetailAsync(HttpContext http, ZelvoDbContext db, CurrentUser user, Ticket ticket, CancellationToken cancellationToken)
    {
        var linkIds = new List<Guid>();
        if (ticket.ParentId is { } p) linkIds.Add(p);
        if (ticket.FollowUpOfId is { } f) linkIds.Add(f);
        var links = await LinksAsync(db, linkIds, cancellationToken).ConfigureAwait(false);
        var children = await db.Tickets.AsNoTracking().Where(t => t.ParentId == ticket.Id).OrderBy(t => t.Sequence)
            .Select(t => new TicketLinkResponse(t.Id, t.Number, t.Title, t.State)).ToListAsync(cancellationToken).ConfigureAwait(false);
        http.Response.Headers.ETag = $"\"{ticket.Version}\"";
        var refs = await DirectoryRefs.LoadAsync(db, cancellationToken).ConfigureAwait(false);
        var slas = await TicketSlaView.LoadAsync(db, user.Has(Permissions.IncidentsRead) ? [ticket.Id] : [], cancellationToken).ConfigureAwait(false);
        return View(user, ticket, IncidentResponse.From(ticket, user,
            links.GetValueOrDefault(ticket.ParentId ?? Guid.Empty), links.GetValueOrDefault(ticket.FollowUpOfId ?? Guid.Empty), children, refs,
            slas.For(ticket.Id, DateTimeOffset.UtcNow)));
    }

    private static async Task<Dictionary<Guid, TicketLinkResponse>> LinksAsync(ZelvoDbContext db, IEnumerable<Guid> ids, CancellationToken cancellationToken)
    {
        var list = ids.Distinct().ToList();
        if (list.Count == 0)
        {
            return [];
        }

        return await db.Tickets.AsNoTracking().Where(t => list.Contains(t.Id))
            .Select(t => new TicketLinkResponse(t.Id, t.Number, t.Title, t.State))
            .ToDictionaryAsync(t => t.Id, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<Dictionary<Guid, string?>> Names(ZelvoDbContext db, IEnumerable<Guid> ids, CancellationToken cancellationToken)
    {
        var list = ids.Distinct().ToList();
        return await db.Users.AsNoTracking().Where(u => list.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Staff see the full response with field rules applied; requesters a fixed smaller view.</summary>
    private static JsonObject View(CurrentUser user, Ticket ticket, IncidentResponse response)
    {
        if (user.Has(Permissions.IncidentsRead))
        {
            return FieldFilter.Apply(user, PermissionCatalog.Incidents, response);
        }

        var json = JsonSerializer.SerializeToNode(response, Json)!.AsObject();
        foreach (var field in RequesterHidden)
        {
            json.Remove(field);
        }

        return json;
    }

    internal static ProblemHttpResult NotFound() =>
        (ProblemHttpResult)ApiProblem.Create(StatusCodes.Status404NotFound, "incident-not-found", "The incident does not exist.");

    internal static ProblemHttpResult Closed() =>
        (ProblemHttpResult)ApiProblem.Create(StatusCodes.Status409Conflict, "ticket-closed",
            "The incident is closed. Closed incidents do not change; report a follow-up instead.");

    private static ProblemHttpResult VersionMismatch() =>
        (ProblemHttpResult)ApiProblem.Create(StatusCodes.Status412PreconditionFailed, "version-mismatch",
            "The incident changed since you loaded it. Load it again and repeat your change.");

    private static ProblemHttpResult FieldsReadOnly(IReadOnlyList<string> fields) =>
        TypedResults.Problem(
            statusCode: StatusCodes.Status403Forbidden,
            title: "You may not change some of these fields.",
            type: ApiProblem.TypeFor("fields-read-only"),
            extensions: new Dictionary<string, object?>(StringComparer.Ordinal) { ["code"] = "fields-read-only", ["fields"] = fields });
}

/// <summary>A person, by name.</summary>
internal sealed record PersonResponse(Guid Id, string? DisplayName);

/// <summary>A linked ticket.</summary>
internal sealed record TicketLinkResponse(Guid Id, string Number, string Title, TicketState State);

/// <summary>An incident. Staff see the fields their roles allow; requesters a smaller view of their own incidents.</summary>
internal sealed record IncidentResponse(
    Guid Id, string Number, string Title, string? Description, TicketState State, TicketChannel Channel,
    PersonResponse Requester, PersonResponse? Assignee, int Impact, int Urgency, int Priority,
    bool IsMajor, DateTimeOffset? MajorDeclaredAt, string? OnHoldReason, string? ResolutionNotes, int ReopenCount,
    TicketLinkResponse? Parent, TicketLinkResponse? FollowUpOf, IReadOnlyList<TicketLinkResponse> Children,
    IReadOnlyList<TicketState> NextStates, bool CanReopen,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, DateTimeOffset? ResolvedAt, DateTimeOffset? ClosedAt, uint Version,
    NodeRef? Category, NodeRef? Location, NamedRef? Group, NamedRef? RoutingRule, DateTimeOffset? RespondedAt, IReadOnlyList<SlaClockResponse> Sla)
{
    public static IncidentResponse From(
        Ticket t, CurrentUser user, TicketLinkResponse? parent, TicketLinkResponse? followUpOf, IReadOnlyList<TicketLinkResponse> children, DirectoryRefs refs,
        IReadOnlyList<SlaClockResponse> sla)
    {
        var incident = t.Incident!;
        var mayEdit = user.Has(Permissions.IncidentsEdit);
        return new(
            t.Id, t.Number, t.Title, t.Description, t.State, t.Channel,
            new PersonResponse(t.RequesterId, t.Requester?.DisplayName),
            t.AssigneeId is { } a ? new PersonResponse(a, t.Assignee?.DisplayName) : null,
            incident.Impact, incident.Urgency, incident.Priority, incident.IsMajor, incident.MajorDeclaredAt,
            t.OnHoldReason, t.ResolutionNotes, t.ReopenCount, parent, followUpOf, children,
            mayEdit ? TicketWorkflow.NextStates(t.State) : [],
            t.State == TicketState.Resolved && (mayEdit || t.RequesterId == user.Account?.Id),
            t.CreatedAt, t.UpdatedAt, t.ResolvedAt, t.ClosedAt, t.Version,
            refs.Categories.RefOf(t.CategoryId), refs.Locations.RefOf(t.LocationId), refs.Group(t.GroupId), refs.Rule(t.RoutingRuleId), t.RespondedAt, sla);
    }
}

/// <summary>An update on a major incident's timeline.</summary>
internal sealed record MajorIncidentUpdateResponse(
    Guid Id, Guid TicketId, string TicketNumber, LocalizedTextDto Body, UpdateAudience Audience, PersonResponse PostedBy, DateTimeOffset PostedAt)
{
    public static MajorIncidentUpdateResponse From(MajorIncidentUpdate u, string number, Dictionary<Guid, string?> names) =>
        new(u.Id, u.TicketId, number, LocalizedTextDto.From(u.Body), u.Audience, new PersonResponse(u.PostedById, names.GetValueOrDefault(u.PostedById)), u.PostedAt);
}

/// <summary>One entry of an incident's history (from the audit log).</summary>
/// <param name="Sequence">Position in the tenant's audit chain.</param>
/// <param name="OccurredAt">When it happened.</param>
/// <param name="Category">Create, Change, Delete, Admin, View or Security.</param>
/// <param name="Action">What happened, for example <c>ticket.updated</c> or <c>tickets.attachment.added</c>.</param>
/// <param name="EntityType"><c>ticket</c> (shared fields) or <c>incident</c> (impact, urgency, priority, major).</param>
/// <param name="ActorType">System, User, Anonymous, ApiClient or AiAgent.</param>
/// <param name="ActorName">Who did it.</param>
/// <param name="Changes">Fields with old and new values; fields hidden from the caller are left out.</param>
/// <param name="Details">Extra facts of non-field entries (for example the file name of an attachment).</param>
internal sealed record HistoryEntryResponse(
    long Sequence, DateTimeOffset OccurredAt, AuditCategory Category, string Action, string? EntityType,
    AuditActorType ActorType, string? ActorName, JsonObject? Changes, JsonObject? Details)
{
    /// <summary>Fields holding the id of a person or an incident.</summary>
    internal static readonly HashSet<string> ReferenceFields = new(StringComparer.Ordinal)
    {
        "assigneeId", "requesterId", "majorDeclaredById", "parentId", "followUpOfId", "createdById",
        "categoryId", "locationId", "groupId", "routingRuleId",
    };

    private static readonly Dictionary<string, string> FieldOf = new(StringComparer.Ordinal)
    {
        ["assigneeId"] = "assignee",
        ["requesterId"] = "requester",
        ["categoryId"] = "category",
        ["locationId"] = "location",
        ["groupId"] = "group",
        ["routingRuleId"] = "group",
    };

    public static HistoryEntryResponse From(AuditEvent e, CurrentUser user)
    {
        var changes = e.Changes is null ? null : JsonNode.Parse(e.Changes)?.AsObject();
        if (changes is not null)
        {
            foreach (var key in changes.Select(c => c.Key).ToList())
            {
                var field = FieldOf.GetValueOrDefault(key, key);
                if (key is "tenantId" or "ticketId" || (PermissionCatalog.Incidents.Field(field) is not null && !FieldFilter.IsVisible(user, PermissionCatalog.Incidents, field)))
                {
                    changes.Remove(key);
                }
            }
        }

        var details = e.Metadata is null ? null : JsonNode.Parse(e.Metadata)?["details"]?.DeepClone().AsObject();
        return new(e.Sequence, e.OccurredAt, e.Category, e.Action, e.EntityType, e.ActorType, e.ActorName, changes, details);
    }
}

/// <summary>Filters of the incident list. Requesters' lists are always their own incidents.</summary>
public sealed class IncidentListQuery
{
    /// <summary>Only the incident with this number, for example <c>INC-000123</c>.</summary>
    [MaxLength(Ticket.NumberMaxLength)]
    public string? Number { get; init; }

    /// <summary>Only this state.</summary>
    public TicketState? State { get; init; }

    /// <summary>Only incidents still being worked on (not resolved or closed). Ignored with <see cref="State"/>.</summary>
    public bool? Open { get; init; }

    /// <summary>Only this priority (1 is the highest).</summary>
    [Range(1, 9)]
    public int? Priority { get; init; }

    /// <summary>Only major incidents (true) or only others (false).</summary>
    public bool? Major { get; init; }

    /// <summary>Staff: <c>me</c>, <c>none</c> or a user id.</summary>
    [MaxLength(36)]
    public string? Assignee { get; init; }

    /// <summary>Staff: only children of this incident.</summary>
    public Guid? ParentId { get; init; }

    /// <summary>Only incidents the caller requested (always true for requesters).</summary>
    public bool? Mine { get; init; }

    /// <summary>Staff: <c>mine</c> (groups the caller belongs to), <c>none</c> or a group id.</summary>
    [MaxLength(36)]
    public string? Group { get; init; }

    /// <summary>Only incidents in this category or below it.</summary>
    public Guid? Category { get; init; }

    /// <summary>Only incidents at this location or below it.</summary>
    public Guid? Location { get; init; }

    /// <summary>Staff (task B3): <c>atRisk</c> (past an escalation threshold, not breached) or <c>breached</c> (past a target).</summary>
    [AllowedValues(null, "atRisk", "breached")]
    public string? Sla { get; init; }

    /// <summary>Staff: <c>due</c> lists the soonest running SLA target first; otherwise newest first.</summary>
    [AllowedValues(null, "due", "created")]
    public string? Sort { get; init; }

    /// <summary>Page number, starting at 1.</summary>
    [Range(1, 100_000)]
    public int? Page { get; init; }

    /// <summary>Items per page, 1 to 100.</summary>
    [Range(1, PageQuery.MaxPageSize)]
    public int? PageSize { get; init; }

    internal int PageNumber => Page ?? 1;

    internal int Size => PageSize ?? PageQuery.DefaultPageSize;

    internal int Skip => (PageNumber - 1) * Size;
}

/// <summary>A new incident.</summary>
public sealed class CreateIncidentRequest
{
    /// <summary>Short summary, 3 to 300 characters.</summary>
    [Required]
    [StringLength(Ticket.TitleMaxLength, MinimumLength = 3)]
    public string? Title { get; init; }

    /// <summary>What happened.</summary>
    [StringLength(Ticket.DescriptionMaxLength)]
    public string? Description { get; init; }

    /// <summary>Staff: who the incident is for (default: the caller).</summary>
    public Guid? RequesterId { get; init; }

    /// <summary>Staff: how it was reported (default Phone; requesters always Portal).</summary>
    public TicketChannel? Channel { get; init; }

    /// <summary>Staff: impact level 1–3 (default 2).</summary>
    public int? Impact { get; init; }

    /// <summary>Staff: urgency level 1–3 (default 2).</summary>
    public int? Urgency { get; init; }

    /// <summary>Needs incidents.assign: the agent to assign.</summary>
    public Guid? AssigneeId { get; init; }

    /// <summary>Staff: the parent incident.</summary>
    public Guid? ParentId { get; init; }

    /// <summary>The closed incident this one follows up.</summary>
    public Guid? FollowUpOfId { get; init; }

    /// <summary>What it is about (an active category).</summary>
    public Guid? CategoryId { get; init; }

    /// <summary>Where the requester is (an active location).</summary>
    public Guid? LocationId { get; init; }

    /// <summary>Needs incidents.assign: the group. Without a group or assignee, the assignment rules place the incident.</summary>
    public Guid? GroupId { get; init; }
}

/// <summary>Group and assignee of an incident.</summary>
public sealed class SetAssignmentRequest
{
    /// <summary>The group, or null for none.</summary>
    public Guid? GroupId { get; init; }

    /// <summary>The agent (a member of the group when one is given), or null for nobody.</summary>
    public Guid? AssigneeId { get; init; }
}

/// <summary>Category and location of an incident.</summary>
public sealed class SetClassificationRequest
{
    /// <summary>The category, or null for none.</summary>
    public Guid? CategoryId { get; init; }

    /// <summary>The location, or null for none.</summary>
    public Guid? LocationId { get; init; }
}

/// <summary>Changes to an incident. Leave a field out to keep it.</summary>
public sealed class UpdateIncidentRequest
{
    /// <summary>Short summary, 3 to 300 characters.</summary>
    [StringLength(Ticket.TitleMaxLength, MinimumLength = 3)]
    public string? Title { get; init; }

    /// <summary>What happened; empty removes it.</summary>
    [StringLength(Ticket.DescriptionMaxLength)]
    public string? Description { get; init; }

    /// <summary>Impact level 1–3.</summary>
    public int? Impact { get; init; }

    /// <summary>Urgency level 1–3.</summary>
    public int? Urgency { get; init; }
}

/// <summary>Who the incident is assigned to.</summary>
public sealed class AssignIncidentRequest
{
    /// <summary>The agent, or null for nobody.</summary>
    public Guid? AssigneeId { get; init; }
}

/// <summary>A state change.</summary>
public sealed class ChangeIncidentStateRequest
{
    /// <summary>InProgress, OnHold, Resolved, Closed or Reopened.</summary>
    [Required]
    public TicketState? State { get; init; }

    /// <summary>With OnHold: the key of an active on-hold reason.</summary>
    [StringLength(Ticket.ReasonKeyMaxLength)]
    public string? OnHoldReason { get; init; }

    /// <summary>With Resolved: how it was resolved.</summary>
    [StringLength(Ticket.ResolutionNotesMaxLength)]
    public string? ResolutionNotes { get; init; }
}

/// <summary>The parent of an incident.</summary>
public sealed class SetIncidentParentRequest
{
    /// <summary>The parent incident, or null to remove the link.</summary>
    public Guid? ParentId { get; init; }
}

/// <summary>Major-incident flag.</summary>
public sealed class SetMajorIncidentRequest
{
    /// <summary>True to declare a major incident, false to stand it down.</summary>
    public bool IsMajor { get; init; }
}

/// <summary>An update for a major incident's timeline.</summary>
public sealed class PostMajorIncidentUpdateRequest
{
    /// <summary>The update in English.</summary>
    [StringLength(MajorIncidentUpdate.BodyMaxLength)]
    public string? En { get; init; }

    /// <summary>The update in Arabic.</summary>
    [StringLength(MajorIncidentUpdate.BodyMaxLength)]
    public string? Ar { get; init; }

    /// <summary>Internal (default) or Requesters.</summary>
    public UpdateAudience? Audience { get; init; }
}
