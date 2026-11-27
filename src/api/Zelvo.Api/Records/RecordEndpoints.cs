using System.ComponentModel.DataAnnotations;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Zelvo.Api.Conventions;
using Zelvo.Api.Forms;
using Zelvo.Api.Identity;
using Zelvo.Api.Incidents;
using Zelvo.Api.Integrations;
using Zelvo.Api.ServiceDesk;
using Zelvo.Api.Settings;
using Zelvo.Api.Sla;
using Zelvo.Api.Tenancy;
using Zelvo.Api.Workflows;
using Zelvo.Data;
using Zelvo.Data.Auditing;
using Zelvo.Data.Identity;
using Zelvo.Data.Records;
using Zelvo.Data.Tickets;
using Zelvo.Data.Workflows;
using Zelvo.Search;
using Zelvo.Tickets;
using Zelvo.Tickets.Forms;
using Zelvo.Tickets.Sla;
using Zelvo.Workflows.Approvals;
using Zelvo.Workflows.Rules;

namespace Zelvo.Api.Records;

/// <summary>
/// Records of custom record types (module 20, task K2): the same generic endpoints for every type,
/// <c>/records/{type}</c>, documented once in the OpenAPI document. Records are tickets of type
/// <c>Custom</c>: numbered in the type's series, with the C1 form of the type and category, routed by
/// B2's rules (after C3's approvals when rules ask for them), timed by B3's policies for the type,
/// in the B4 conversation, notified, searched and reported. Staff hold the type's permissions;
/// requesters see and answer their own.
/// </summary>
internal static class RecordEndpoints
{
    public static RouteGroupBuilder MapRecordEndpoints(this RouteGroupBuilder group)
    {
        var records = group.MapGroup("/records/{type}").WithTags("Records").RequireTenant().RequireAuthorization();
        records.MapPost("/", CreateAsync).WithName("CreateRecord")
            .WithSummary("Raise a record of the type (by its key) with the type's form for the category. Anyone signed in when the type has a portal entry; staff with records.<type>.edit also for someone else (requesterId, channel) and with a priority. Routed by the assignment rules, or held for approval when rules ask for it; SLA clocks start; audited.")
            .RequirePerson().Produces<RecordResponse>(StatusCodes.Status201Created).ProducesValidationProblem();
        records.MapGet("/", ListAsync).WithName("ListRecords")
            .WithSummary("Records of the type, newest first: all with records.<type>.read (mine=true: the caller's own), otherwise the caller's own. Filters state (a state key), open, number, assignee (me, none or an id), group (mine, none or an id).")
            .Produces<PageResult<RecordResponse>>();
        records.MapGet("/assignees", AssigneesAsync).WithName("ListRecordAssignees")
            .WithSummary("Active users who can be assigned records of the type (their roles hold records.<type>.edit); with groupId, only that group's members. Needs records.<type>.assign.")
            .Produces<IReadOnlyList<PersonResponse>>();
        records.MapGet("/{id:guid}", GetAsync).WithName("GetRecord")
            .WithSummary("One record. Requesters see their own, without the assignee and SLA clocks. Returns an ETag to send as If-Match with changes.")
            .Produces<RecordResponse>();
        records.MapPatch("/{id:guid}", UpdateAsync).WithName("UpdateRecord")
            .WithSummary("Change the title, description, category or priority (1–5). Needs records.<type>.edit; audited.")
            .Produces<RecordResponse>().ProducesValidationProblem();
        records.MapGet("/{id:guid}/form", FormAsync).WithName("GetRecordAnswers")
            .WithSummary("The form answers given with the record.")
            .Produces<TicketFormView>();
        records.MapGet("/{id:guid}/attachments/{attachmentId:guid}/content", DownloadAsync).WithName("DownloadRecordAttachment")
            .WithSummary("Download a file of the form answers once scanned clean; audited.")
            .Produces<byte[]>(contentType: "application/octet-stream");
        records.MapPost("/{id:guid}/transitions", TransitionAsync).WithName("ChangeRecordState")
            .WithSummary("Move the record to a state of its type (stateKey). Its category decides the core's state: InProgress needs an assignee, OnHold an active on-hold reason, Resolved resolution notes. Staff with records.<type>.edit; the requester may reopen their resolved record. Audited.")
            .RequirePerson().Produces<RecordResponse>().ProducesValidationProblem();
        records.MapPut("/{id:guid}/assignment", AssignAsync).WithName("SetRecordAssignment")
            .WithSummary("Set the group and assignee (a member of the group). Needs records.<type>.assign; audited.")
            .Produces<RecordResponse>().ProducesValidationProblem();
        records.MapPost("/{id:guid}/route", RouteAgainAsync).WithName("RouteRecord")
            .WithSummary("Run the assignment rules again. Open records only; needs records.<type>.assign; audited.")
            .Produces<RecordResponse>();
        records.MapGet("/{id:guid}/comments", CommentsAsync).WithName("ListRecordComments")
            .WithSummary("The conversation: staff see public replies and internal notes, the requester the public replies.")
            .Produces<IReadOnlyList<RecordCommentResponse>>();
        records.MapPost("/{id:guid}/comments", CommentAsync).WithName("CreateRecordComment")
            .WithSummary("Post a public reply (staff with records.<type>.edit, or the requester) or an internal note (staff). The first staff reply stops the response clock; the requester's answer moves a record waiting for them back to work. Audited.")
            .RequirePerson().Produces<RecordCommentResponse>(StatusCodes.Status201Created).ProducesValidationProblem();
        return group;
    }

    internal static async Task<IResult> CreateAsync(
        string type, CreateRecordRequest request, HttpContext http, ZelvoDbContext db, CurrentUser user, TenantSettingsStore settings,
        IAuditLog audit, ISearchIndexer indexer, CancellationToken cancellationToken)
    {
        var recordType = await RecordCore.TypeAsync(db, type, cancellationToken);
        if (recordType is null || !recordType.Active)
        {
            return RecordCore.TypeNotFound();
        }

        var me = user.Account!;
        var access = RecordAccess.Of(user, recordType);
        if (!access.Edit && !recordType.PortalEntry)
        {
            return ApiProblem.Create(StatusCodes.Status403Forbidden, "forbidden", "Only staff raise records of this type.");
        }

        var onBehalf = request.RequesterId is { } other && other != me.Id;
        if ((onBehalf || request.Priority is not null || request.Channel is not null) && !access.Edit)
        {
            return ApiProblem.Create(StatusCodes.Status403Forbidden, "forbidden", "Only staff log records for someone else or set their priority.");
        }

        var requester = me;
        if (onBehalf)
        {
            requester = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == request.RequesterId && u.Status == UserStatus.Active, cancellationToken);
            if (requester is null || AnonymousAccounts.IsSystem(requester.Subject))
            {
                return UserEndpoints.Validation("requesterId", "The requester is not an active user of this organisation.");
            }
        }

        if (request.CategoryId is { } category && !await TreeEndpoints.IsSelectableAsync(db.Categories, category, cancellationToken))
        {
            return UserEndpoints.Validation("categoryId", "Choose an active category.");
        }

        var (formResponse, formProblem) = await FormSubmission.PrepareAsync(
            db, user, audit, TicketType.Custom, request.CategoryId, requester.Id, request.FormVersionId, request.FormAnswers,
            await settings.GetAsync(IncidentSettings.Attachments, cancellationToken), cancellationToken, recordType.Id);
        if (formProblem is not null)
        {
            return formProblem;
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow;
        var sequence = (await db.Database.SqlQuery<long>($"""
            UPDATE record_types SET last_number = last_number + 1 WHERE id = {recordType.Id} RETURNING last_number AS "Value"
            """).ToListAsync(cancellationToken)).Single();
        var title = request.Title!.Trim();
        var ticket = new Ticket
        {
            Type = TicketType.Custom,
            RecordTypeId = recordType.Id,
            Number = TicketNumbers.Format(recordType.NumberPrefix, sequence),
            Sequence = sequence,
            Title = title[..Math.Min(title.Length, Ticket.TitleMaxLength)],
            Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim(),
            Channel = onBehalf ? request.Channel ?? TicketChannel.Phone : request.Channel ?? TicketChannel.Portal,
            RequesterId = requester.Id,
            CreatedById = me.Id,
            CategoryId = request.CategoryId,
            LocationId = requester.LocationId,
            CreatedAt = now,
            UpdatedAt = now,
        };
        ticket.Custom = new CustomRecord
        {
            TicketId = ticket.Id,
            RecordTypeId = recordType.Id,
            StateKey = RecordStates.Initial(RecordStates.Read(recordType.States)).Key,
            Priority = request.Priority ?? recordType.DefaultPriority,
        };
        db.Tickets.Add(ticket);
        formResponse?.Attach(db, ticket);

        // C3: approval rules on custom records hold the record back until they are decided; otherwise B2 routes it.
        var facts = await RuleFacts.LoadAsync(db, ticket, cancellationToken);
        var levels = await ApprovalPlanner.PlanAsync(db, ticket, ApprovalStage.BeforeAssignment, [], facts, cancellationToken);
        if (levels.Count > 0)
        {
            await http.RequestServices.GetRequiredService<ApprovalService>().StartAsync(ticket, ApprovalStage.BeforeAssignment, levels, me.Id, null, cancellationToken);
        }
        else
        {
            await IncidentEndpoints.RouteAsync(db, settings, ticket, now, cancellationToken);
        }

        await RecordSearch.IndexAsync(db, indexer, [ticket], cancellationToken);
        await http.RequestServices.GetRequiredService<SlaTracker>().UpdateAsync([ticket], now, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return TypedResults.Created($"/api/v1/records/{recordType.Key}/{ticket.Id}", await RecordCore.ResponseAsync(http, db, user, recordType, ticket.Id, cancellationToken));
    }

    internal static async Task<IResult> ListAsync(
        string type, [AsParameters] PageQuery page, [AsParameters] RecordListQuery query, ZelvoDbContext db, CurrentUser user, CancellationToken cancellationToken)
    {
        var recordType = await RecordCore.TypeAsync(db, type, cancellationToken);
        if (recordType is null)
        {
            return RecordCore.TypeNotFound();
        }

        var me = user.Account?.Id;
        var staff = RecordAccess.Of(user, recordType).Read;
        var tickets = RecordCore.Records(db, recordType.Id).AsNoTracking();
        if (!staff || query.Mine == true)
        {
            tickets = tickets.Where(t => t.RequesterId == me || (query.Mine != true && t.CreatedById == me));
        }

        if (query.State is { Length: > 0 } state)
        {
            tickets = tickets.Where(t => t.Custom!.StateKey == state);
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

        if (staff)
        {
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
        }

        var ordered = tickets.OrderByDescending(t => t.CreatedAt).ThenByDescending(t => t.Id);
        var total = await ordered.LongCountAsync(cancellationToken);
        var rows = await ordered.Skip(page.Skip).Take(page.Size).ToListAsync(cancellationToken);
        var context = await RecordResponseContext.LoadAsync(db, user, recordType, rows, cancellationToken);
        return TypedResults.Ok(new PageResult<RecordResponse>([.. rows.Select(context.From)], page.Number, page.Size, total));
    }

    internal static async Task<IResult> AssigneesAsync(string type, Guid? groupId, ZelvoDbContext db, CurrentUser user, CancellationToken cancellationToken)
    {
        var recordType = await RecordCore.TypeAsync(db, type, cancellationToken);
        if (recordType is null)
        {
            return RecordCore.TypeNotFound();
        }

        if (!RecordAccess.Of(user, recordType).Assign)
        {
            return RecordCore.Forbidden();
        }

        var people = RecordCore.Assignable(db, recordType);
        if (groupId is { } g)
        {
            people = people.Where(u => db.SupportGroupMembers.Any(m => m.GroupId == g && m.UserId == u.Id));
        }

        return TypedResults.Ok(await people.OrderBy(u => u.DisplayName ?? u.Email).Take(500).Select(u => new PersonResponse(u.Id, u.DisplayName)).ToListAsync(cancellationToken));
    }

    internal static async Task<IResult> GetAsync(string type, Guid id, HttpContext http, ZelvoDbContext db, CurrentUser user, CancellationToken cancellationToken)
    {
        var recordType = await RecordCore.TypeAsync(db, type, cancellationToken);
        return recordType is null || await RecordCore.FindVisibleAsync(db, user, recordType, id, cancellationToken) is null
            ? RecordCore.NotFound()
            : TypedResults.Ok(await RecordCore.ResponseAsync(http, db, user, recordType, id, cancellationToken));
    }

    internal static async Task<IResult> UpdateAsync(
        string type, Guid id, UpdateRecordRequest request, HttpContext http, ZelvoDbContext db, CurrentUser user, ISearchIndexer indexer, CancellationToken cancellationToken)
    {
        var (recordType, ticket, problem) = await RecordCore.ForStaffAsync(http, db, user, type, id, a => a.Edit, cancellationToken);
        if (problem is not null)
        {
            return problem;
        }

        if (ticket!.State == TicketState.Closed)
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
            ticket.Custom!.Priority = priority;
        }

        ticket.CategoryId = categoryId;
        ticket.UpdatedAt = DateTimeOffset.UtcNow;
        return await RecordCore.SaveAsync(http, db, user, indexer, recordType!, ticket, cancellationToken);
    }

    internal static async Task<IResult> FormAsync(string type, Guid id, ZelvoDbContext db, CurrentUser user, IAuditLog audit, CancellationToken cancellationToken)
    {
        var recordType = await RecordCore.TypeAsync(db, type, cancellationToken);
        var ticket = recordType is null ? null : await RecordCore.FindVisibleAsync(db, user, recordType, id, cancellationToken);
        if (ticket is null)
        {
            return RecordCore.NotFound();
        }

        var view = await FormSubmission.ViewAsync(db, user, audit, ticket, cancellationToken);
        return TypedResults.Ok(view is null
            ? new JsonObject { ["form"] = null }
            : (JsonObject)System.Text.Json.JsonSerializer.SerializeToNode(view, FormJson.Options)!);
    }

    internal static async Task<IResult> DownloadAsync(
        string type, Guid id, Guid attachmentId, HttpContext http, ZelvoDbContext db, CurrentUser user, IAttachmentStore store, IAuditLog audit,
        CancellationToken cancellationToken)
    {
        var recordType = await RecordCore.TypeAsync(db, type, cancellationToken);
        var ticket = recordType is null ? null : await RecordCore.FindVisibleAsync(db, user, recordType, id, cancellationToken);
        var file = ticket is null ? null : await db.TicketAttachments.AsNoTracking().SingleOrDefaultAsync(a => a.Id == attachmentId && a.TicketId == id, cancellationToken);
        if (file is null)
        {
            return FormsCommon.NotFound("attachment");
        }

        if (!IncidentAttachmentEndpoints.IsDownloadable(file))
        {
            return ApiProblem.Create(StatusCodes.Status409Conflict, "attachment-not-available",
                file.ScanStatus == AttachmentScanStatus.Infected ? "The file was rejected: malware was found in it." : "The file is still being checked for malware. Try again in a moment.");
        }

        var content = await store.ReadAsync(file.Id, cancellationToken);
        if (content is null)
        {
            return FormsCommon.NotFound("attachment");
        }

        await audit.RecordAsync(AuditCategory.View, "tickets.attachment.downloaded", "ticket", id.ToString(),
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["attachmentId"] = file.Id.ToString(), ["fileName"] = file.FileName }, cancellationToken);
        http.Response.Headers.ContentSecurityPolicy = "default-src 'none'; sandbox";
        http.Response.Headers.XContentTypeOptions = "nosniff";
        http.Response.Headers.CacheControl = "private, no-store";
        return TypedResults.File(content, file.ContentType, file.FileName);
    }

    internal static async Task<IResult> TransitionAsync(
        string type, Guid id, RecordTransitionRequest request, HttpContext http, ZelvoDbContext db, CurrentUser user, TenantSettingsStore settings,
        ISearchIndexer indexer, CancellationToken cancellationToken)
    {
        var recordType = await RecordCore.TypeAsync(db, type, cancellationToken);
        var ticket = recordType is null ? null : await RecordCore.FindVisibleAsync(db, user, recordType, id, cancellationToken, tracked: true);
        if (ticket is null)
        {
            return RecordCore.NotFound();
        }

        if (IncidentEndpoints.VersionProblem(http, db, ticket) is { } versionProblem)
        {
            return versionProblem;
        }

        var states = RecordStates.Read(recordType!.States);
        var staff = RecordAccess.Of(user, recordType).Edit;
        var next = RecordCore.NextStates(ticket, states, staff, user.Account?.Id);
        var target = states.FirstOrDefault(s => s.Key == request.StateKey);
        if (target is null || !next.Contains(target))
        {
            if (!staff && ticket.RequesterId != user.Account?.Id)
            {
                return RecordCore.Forbidden();
            }

            return ticket.State == TicketState.Closed ? IncidentEndpoints.Closed()
                : ticket.AwaitingApproval ? ApiProblem.Create(StatusCodes.Status409Conflict, "awaiting-approval", "The record waits for its approval.")
                : ApiProblem.Create(StatusCodes.Status409Conflict, "transition-not-allowed", $"The record cannot move to {request.StateKey} now.");
        }

        var now = DateTimeOffset.UtcNow;
        var core = RecordStates.TargetOf(target.Category, ticket.State);
        if (target.Category != RecordStates.CategoryOf(ticket.State))
        {
            if (core == TicketState.OnHold)
            {
                var reasons = await settings.GetAsync(IncidentSettings.OnHoldReasons, cancellationToken);
                if (!reasons.Any(r => r.Active && r.Key == request.OnHoldReason))
                {
                    return UserEndpoints.Validation("onHoldReason", "Choose one of the active on-hold reasons.");
                }
            }

            switch (TicketWorkflow.Check(ticket, core, request.OnHoldReason, request.ResolutionNotes))
            {
                case TransitionError.ResolutionNotesRequired:
                    return UserEndpoints.Validation("resolutionNotes", "Say how it was done.");
                case TransitionError.OnHoldReasonRequired:
                    return UserEndpoints.Validation("onHoldReason", "Choose why it is on hold.");
                case TransitionError.AssigneeRequired:
                    return ApiProblem.Create(StatusCodes.Status409Conflict, "assignee-required", "Assign it before starting work on it.");
                case TransitionError.NotAllowed:
                    return ApiProblem.Create(StatusCodes.Status409Conflict, "transition-not-allowed", $"The record cannot move to {request.StateKey} now.");
            }

            TicketWorkflow.Apply(ticket, core, request.OnHoldReason, request.ResolutionNotes, now);
        }

        ticket.Custom!.StateKey = target.Key;
        ticket.UpdatedAt = now;
        return await RecordCore.SaveAsync(http, db, user, indexer, recordType, ticket, cancellationToken);
    }

    internal static async Task<IResult> AssignAsync(
        string type, Guid id, RecordAssignmentRequest request, HttpContext http, ZelvoDbContext db, CurrentUser user, ISearchIndexer indexer, CancellationToken cancellationToken)
    {
        var (recordType, ticket, problem) = await RecordCore.ForStaffAsync(http, db, user, type, id, a => a.Assign, cancellationToken);
        if (problem is not null)
        {
            return problem;
        }

        if (ticket!.State == TicketState.Closed)
        {
            return IncidentEndpoints.Closed();
        }

        if (request.GroupId is { } groupId && groupId != ticket.GroupId && !await db.SupportGroups.AnyAsync(g => g.Id == groupId && g.Active, cancellationToken))
        {
            return UserEndpoints.Validation("groupId", "Choose an active group.");
        }

        if (request.AssigneeId is { } assigneeId && !await RecordCore.Assignable(db, recordType!).AnyAsync(u => u.Id == assigneeId, cancellationToken))
        {
            return UserEndpoints.Validation("assigneeId", "The assignee cannot be assigned records of this type.");
        }

        if (request.GroupId is { } group && request.AssigneeId is { } member
            && !await db.SupportGroupMembers.AnyAsync(m => m.GroupId == group && m.UserId == member, cancellationToken))
        {
            return UserEndpoints.Validation("assigneeId", "The assignee is not a member of the group.");
        }

        ticket.GroupId = request.GroupId;
        ticket.RoutingRuleId = null;
        TicketWorkflow.Assign(ticket, request.AssigneeId, DateTimeOffset.UtcNow);
        return await RecordCore.SaveAsync(http, db, user, indexer, recordType!, ticket, cancellationToken);
    }

    internal static async Task<IResult> RouteAgainAsync(
        string type, Guid id, HttpContext http, ZelvoDbContext db, CurrentUser user, TenantSettingsStore settings, ISearchIndexer indexer, CancellationToken cancellationToken)
    {
        var (recordType, ticket, problem) = await RecordCore.ForStaffAsync(http, db, user, type, id, a => a.Assign, cancellationToken);
        if (problem is not null)
        {
            return problem;
        }

        if (!TicketWorkflow.IsOpen(ticket!.State) || ticket.AwaitingApproval)
        {
            return ApiProblem.Create(StatusCodes.Status409Conflict, "ticket-not-open", "Only open records that are not waiting for approval are routed.");
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await IncidentEndpoints.RouteAsync(db, settings, ticket, DateTimeOffset.UtcNow, cancellationToken);
        return await RecordCore.SaveAsync(http, db, user, indexer, recordType!, ticket, cancellationToken);
    }

    internal static async Task<IResult> CommentsAsync(string type, Guid id, ZelvoDbContext db, CurrentUser user, CancellationToken cancellationToken)
    {
        var recordType = await RecordCore.TypeAsync(db, type, cancellationToken);
        var ticket = recordType is null ? null : await RecordCore.FindVisibleAsync(db, user, recordType, id, cancellationToken);
        if (ticket is null)
        {
            return RecordCore.NotFound();
        }

        var staff = RecordAccess.Of(user, recordType!).Read;
        var rows = await db.TicketComments.AsNoTracking()
            .Where(c => c.TicketId == id && (staff || c.Visibility == CommentVisibility.Public))
            .OrderBy(c => c.CreatedAt).ThenBy(c => c.Id).Take(500).ToListAsync(cancellationToken);
        var names = await IncidentEndpoints.Names(db, rows.Select(c => c.AuthorId), cancellationToken);
        return TypedResults.Ok(rows.Select(c => new RecordCommentResponse(c.Id, c.Visibility, c.Body, c.Language,
            new PersonResponse(c.AuthorId, names.GetValueOrDefault(c.AuthorId)), c.ByStaff, c.CreatedAt)).ToList());
    }

    internal static async Task<IResult> CommentAsync(
        string type, Guid id, RecordCommentRequest request, HttpContext http, ZelvoDbContext db, CurrentUser user, TenantSettingsStore settings,
        CancellationToken cancellationToken)
    {
        var recordType = await RecordCore.TypeAsync(db, type, cancellationToken);
        var ticket = recordType is null ? null : await RecordCore.FindVisibleAsync(db, user, recordType, id, cancellationToken, tracked: true);
        if (ticket is null)
        {
            return RecordCore.NotFound();
        }

        var me = user.Account!;
        var byStaff = RecordAccess.Of(user, recordType!).Edit;
        if (!byStaff && ticket.RequesterId != me.Id)
        {
            return RecordCore.Forbidden();
        }

        var visibility = request.Visibility ?? CommentVisibility.Public;
        if (!byStaff && visibility == CommentVisibility.Internal)
        {
            return ApiProblem.Create(StatusCodes.Status403Forbidden, "forbidden", "Requesters post public replies only.");
        }

        if (ticket.State == TicketState.Closed)
        {
            return IncidentEndpoints.Closed();
        }

        var text = request.Body?.Trim() ?? "";
        if (text.Length == 0)
        {
            return UserEndpoints.Validation("body", "Write the reply or note.");
        }

        var language = LocalizationSettings.IsLanguage(request.Language) ? request.Language!
            : LocalizationSettings.IsLanguage(me.PreferredLanguage) ? me.PreferredLanguage!
            : await settings.GetAsync(LocalizationSettings.DefaultLanguage, cancellationToken);
        var now = DateTimeOffset.UtcNow;
        var staffReply = byStaff && ticket.RequesterId != me.Id;
        var comment = new TicketComment
        {
            TicketId = ticket.Id, Visibility = visibility, Body = text, Language = language, AuthorId = me.Id, ByStaff = staffReply, CreatedAt = now,
        };
        db.TicketComments.Add(comment);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var sla = http.RequestServices.GetRequiredService<SlaTracker>();
        if (staffReply && visibility == CommentVisibility.Public && ticket.RespondedAt is null)
        {
            ticket.RespondedAt = now;
            ticket.UpdatedAt = now;
            await sla.UpdateAsync([ticket], now, cancellationToken);
        }
        else if (!staffReply && ticket is { State: TicketState.OnHold, OnHoldReason: OnHoldReason.AwaitingRequester }
            && TicketWorkflow.Check(ticket, TicketState.InProgress, null, null) == TransitionError.None)
        {
            TicketWorkflow.Apply(ticket, TicketState.InProgress, null, null, now);
            await sla.UpdateAsync([ticket], now, cancellationToken);
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return TypedResults.Created($"/api/v1/records/{recordType!.Key}/{id}/comments",
            new RecordCommentResponse(comment.Id, comment.Visibility, comment.Body, comment.Language, new PersonResponse(me.Id, me.DisplayName), comment.ByStaff, comment.CreatedAt));
    }
}

/// <summary>A reply or note on a record.</summary>
internal sealed record RecordCommentResponse(Guid Id, CommentVisibility Visibility, string Body, string Language, PersonResponse Author, bool ByStaff, DateTimeOffset CreatedAt);

/// <summary>Filters of a record list.</summary>
public sealed class RecordListQuery
{
    /// <summary>Only the caller's own.</summary>
    public bool? Mine { get; init; }

    /// <summary>Only records in this state (key).</summary>
    public string? State { get; init; }

    /// <summary>Only open records.</summary>
    public bool? Open { get; init; }

    /// <summary>The record with this number.</summary>
    public string? Number { get; init; }

    /// <summary><c>me</c>, <c>none</c> or a user id.</summary>
    public string? Assignee { get; init; }

    /// <summary><c>mine</c>, <c>none</c> or a group id.</summary>
    public string? Group { get; init; }
}

/// <summary>A new record.</summary>
public sealed class CreateRecordRequest
{
    /// <summary>Short summary.</summary>
    [Required]
    [StringLength(Ticket.TitleMaxLength, MinimumLength = 1)]
    public string? Title { get; init; }

    /// <summary>What is needed.</summary>
    [StringLength(Ticket.DescriptionMaxLength)]
    public string? Description { get; init; }

    /// <summary>Category.</summary>
    public Guid? CategoryId { get; init; }

    /// <summary>Staff: priority 1–5 (default the type's).</summary>
    [Range(1, 5)]
    public int? Priority { get; init; }

    /// <summary>Staff: the person it is for.</summary>
    public Guid? RequesterId { get; init; }

    /// <summary>Staff: how it reached them.</summary>
    public TicketChannel? Channel { get; init; }

    /// <summary>The form version filled in.</summary>
    public Guid? FormVersionId { get; init; }

    /// <summary>The form's answers.</summary>
    public JsonObject? FormAnswers { get; init; }
}

/// <summary>Changes to a record.</summary>
public sealed class UpdateRecordRequest
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

    /// <summary>Priority 1–5.</summary>
    [Range(1, 5)]
    public int? Priority { get; init; }
}

/// <summary>A move to a state of the record's type.</summary>
public sealed class RecordTransitionRequest
{
    /// <summary>Key of the state.</summary>
    [Required]
    [StringLength(CustomRecord.StateKeyMaxLength)]
    public string? StateKey { get; init; }

    /// <summary>Key of an active on-hold reason (OnHold states).</summary>
    [StringLength(Ticket.ReasonKeyMaxLength)]
    public string? OnHoldReason { get; init; }

    /// <summary>How it was done (Resolved states).</summary>
    [StringLength(Ticket.ResolutionNotesMaxLength)]
    public string? ResolutionNotes { get; init; }
}

/// <summary>A record's group and assignee.</summary>
public sealed class RecordAssignmentRequest
{
    /// <summary>Group, or none.</summary>
    public Guid? GroupId { get; init; }

    /// <summary>Assignee, or none.</summary>
    public Guid? AssigneeId { get; init; }
}

/// <summary>A reply or note.</summary>
public sealed class RecordCommentRequest
{
    /// <summary>The text.</summary>
    [Required]
    [StringLength(TicketComment.BodyMaxLength)]
    public string? Body { get; init; }

    /// <summary>Public (default) or Internal (staff).</summary>
    public CommentVisibility? Visibility { get; init; }

    /// <summary><c>en</c> or <c>ar</c>.</summary>
    [StringLength(2)]
    public string? Language { get; init; }
}
