using System.ComponentModel.DataAnnotations;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Zelvo.Api.Catalog;
using Zelvo.Api.Conventions;
using Zelvo.Api.Forms;
using Zelvo.Api.Identity;
using Zelvo.Api.Incidents;
using Zelvo.Api.Integrations;
using Zelvo.Api.ServiceDesk;
using Zelvo.Api.Settings;
using Zelvo.Api.Sla;
using Zelvo.Api.Tenancy;
using Zelvo.Data;
using Zelvo.Data.Auditing;
using Zelvo.Data.Catalog;
using Zelvo.Data.Identity;
using Zelvo.Data.Localization;
using Zelvo.Data.Tenancy;
using Zelvo.Data.Tickets;
using Zelvo.Data.Voice;
using Zelvo.Search;
using Zelvo.Tickets;
using Zelvo.Tickets.Forms;
using Zelvo.Tickets.Sla;

namespace Zelvo.Api.Voice;

/// <summary>
/// Complaints, inquiries and suggestions (module 3, task C6; GOV-2, GOV-3). Three record types on
/// the shared ticket core (numbers <c>CMP-</c>, <c>INQ-</c>, <c>SUG-</c>, states, B2 routing, B3 SLA
/// clocks, the B4 conversation) with their own forms (C1 bindings per record type and category) and
/// closure flows: complaints need a root cause and a resolution summary and close when the
/// customer confirms (or, unanswered, after the auto-close period); inquiries are answered and
/// can be converted to a complaint or a service request; suggestions are reviewed (Under review,
/// Accepted, Implemented, Declined) with feedback to the submitter.
/// </summary>
internal static class VoiceEndpoints
{
    public static RouteGroupBuilder MapVoiceEndpoints(this RouteGroupBuilder group)
    {
        var voice = group.MapGroup("/customer-voice").WithTags("Customer voice").RequireTenant();
        voice.MapPost("/", CreateAsync).WithName("CreateCustomerVoiceRecord")
            .WithSummary("Send a complaint, inquiry or suggestion (type) with the form of its record type and category (C1). Staff with voice.edit may log one for someone else (requesterId, channel). Routed by the assignment rules; SLA clocks start; audited.")
            .RequirePerson().RequireAuthorization().Produces<VoiceResponse>(StatusCodes.Status201Created).ProducesValidationProblem();
        voice.MapGet("/", ListAsync).WithName("ListCustomerVoiceRecords")
            .WithSummary("Complaints, inquiries and suggestions, newest first: all for holders of voice.read (mine=true: the caller's own), otherwise the caller's own. Filters type, open, state, number, assignee, group, service.")
            .RequireAuthorization().Produces<PageResult<VoiceResponse>>();
        voice.MapGet("/assignees", AssigneesAsync).WithName("ListCustomerVoiceAssignees")
            .WithSummary("Active users who can be assigned customer voice records (their roles hold voice.edit); with groupId, only that group's members.")
            .RequirePermission(Permissions.VoiceAssign).Produces<IReadOnlyList<PersonResponse>>();
        voice.MapGet("/{id:guid}", GetAsync).WithName("GetCustomerVoiceRecord")
            .WithSummary("One complaint, inquiry or suggestion. Requesters see a smaller view. Returns an ETag to send as If-Match with changes.")
            .RequireAuthorization().Produces<VoiceResponse>();
        voice.MapPatch("/{id:guid}", UpdateAsync).WithName("UpdateCustomerVoiceRecord")
            .WithSummary("Staff (voice.edit) change the title, a complaint's severity (its SLA priority follows) and root cause, the category and the service it is about. Audited.")
            .RequirePermission(Permissions.VoiceEdit).Produces<VoiceResponse>().ProducesValidationProblem();
        voice.MapGet("/{id:guid}/form", FormAsync).WithName("GetCustomerVoiceAnswers")
            .WithSummary("The form answers given with it, with the fields as they were then. Staff viewing personal answers is audited.")
            .RequireAuthorization().Produces<TicketFormView>();
        voice.MapGet("/{id:guid}/attachments/{attachmentId:guid}/content", DownloadAsync).WithName("DownloadCustomerVoiceAttachment")
            .WithSummary("Download a file of the form answers once scanned clean; audited.")
            .RequireAuthorization().Produces<byte[]>(contentType: "application/octet-stream");
        voice.MapGet("/{id:guid}/comments", CommentsAsync).WithName("ListCustomerVoiceComments")
            .WithSummary("The conversation, oldest first: staff see public replies and internal notes, the requester the public replies.")
            .RequireAuthorization().Produces<IReadOnlyList<VoiceCommentResponse>>();
        voice.MapPost("/{id:guid}/comments", CommentAsync).WithName("CreateCustomerVoiceComment")
            .WithSummary("Post a public reply (staff with voice.edit, or the requester) or an internal note (staff). A staff member's first public reply stops the response clock; the requester's answer moves a record waiting for them back to work. Audited.")
            .RequirePerson().RequireAuthorization().Produces<VoiceCommentResponse>(StatusCodes.Status201Created).ProducesValidationProblem();
        voice.MapPost("/{id:guid}/transitions", TransitionAsync).WithName("ChangeCustomerVoiceState")
            .WithSummary("Staff (voice.edit) move it to InProgress, OnHold (with an active reason), Resolved (with notes; a complaint also needs a root cause and then waits for the customer's confirmation) or Reopened. Suggestions are resolved by their review, resolved complaints close by the customer's confirmation (or after the auto-close period). Requesters may reopen their resolved inquiries. Audited.")
            .RequirePerson().RequireAuthorization().Produces<VoiceResponse>().ProducesValidationProblem();
        voice.MapPut("/{id:guid}/assignment", AssignAsync).WithName("SetCustomerVoiceAssignment")
            .WithSummary("Set the group and assignee (a member of the group when one is given). Needs voice.assign; audited.")
            .RequirePermission(Permissions.VoiceAssign).Produces<VoiceResponse>().ProducesValidationProblem();
        voice.MapPost("/{id:guid}/route", RouteAgainAsync).WithName("RouteCustomerVoiceRecord")
            .WithSummary("Run the assignment rules again. Open records only; needs voice.assign; audited.")
            .RequirePermission(Permissions.VoiceAssign).Produces<VoiceResponse>();
        voice.MapPost("/{id:guid}/confirmation", ConfirmAsync).WithName("ConfirmComplaintResolution")
            .WithSummary("The requester of a resolved complaint accepts the resolution (it closes) or rejects it with a comment (it is reopened). Audited.")
            .RequirePerson().RequireAuthorization().Produces<VoiceResponse>().ProducesValidationProblem();
        voice.MapPost("/{id:guid}/review", ReviewAsync).WithName("ReviewSuggestion")
            .WithSummary("Staff (voice.edit) set a suggestion's review: UnderReview, Accepted, Implemented or Declined, with feedback to the submitter (needed for Implemented and Declined, which resolve it). Audited.")
            .RequirePermission(Permissions.VoiceEdit).Produces<VoiceResponse>().ProducesValidationProblem();
        voice.MapPost("/{id:guid}/conversion", ConvertAsync).WithName("ConvertInquiry")
            .WithSummary("Staff (voice.edit) convert an open inquiry to a complaint or to a service request for a service offered to the requester. The new record is routed and linked; the inquiry is resolved with a note. Audited.")
            .RequirePermission(Permissions.VoiceEdit).Produces<VoiceResponse>().ProducesValidationProblem();
        return group;
    }

    internal static async Task<IResult> CreateAsync(
        CreateVoiceRequest request, HttpContext http, ZelvoDbContext db, CurrentUser user, ITenantContext tenant, TenantSettingsStore settings,
        IAuditLog audit, ISearchIndexer indexer, CancellationToken cancellationToken)
    {
        var me = user.Account!;
        var type = request.Type!.Value;
        if (!VoiceTypes.Is(type))
        {
            return UserEndpoints.Validation("type", "Choose Complaint, Inquiry or Suggestion.");
        }

        var onBehalf = request.RequesterId is { } other && other != me.Id;
        if (onBehalf && !user.Has(Permissions.VoiceEdit))
        {
            return ApiProblem.Create(StatusCodes.Status403Forbidden, "forbidden", "Only staff may log complaints, inquiries and suggestions for someone else.");
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

        var scope = onBehalf ? await CatalogScope.OfUserAsync(db, requester, cancellationToken) : await CatalogScope.OfAsync(db, user, cancellationToken);
        var problem = await VoiceCore.CheckClassificationAsync(db, scope, request.CategoryId, request.ServiceId, null, cancellationToken);
        if (problem is not null)
        {
            return problem;
        }

        if (request.Severity is { } severity && (type != TicketType.Complaint || severity is < 1 or > VoiceSettingsValue.SeverityLevels))
        {
            return UserEndpoints.Validation("severity", "Only complaints have a severity, from 1 (highest) to 3.");
        }

        var (formResponse, formProblem) = await FormSubmission.PrepareAsync(
            db, user, audit, type, request.CategoryId, requester.Id, request.FormVersionId, request.FormAnswers,
            await settings.GetAsync(IncidentSettings.Attachments, cancellationToken), cancellationToken);
        if (formProblem is not null)
        {
            return formProblem;
        }

        var channel = onBehalf ? request.Channel ?? TicketChannel.Phone : TicketChannel.Portal;
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var ticket = await VoiceCore.CreateAsync(http, db, settings, tenant, indexer,
            new VoiceDraft(type, request.Title!, request.Description, requester.Id, me.Id, channel, request.CategoryId, requester.LocationId,
                request.ServiceId, request.Severity, Anonymous: false, TrackingCodeHash: null), cancellationToken);
        formResponse?.Attach(db, ticket);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return TypedResults.Created($"/api/v1/customer-voice/{ticket.Id}", await VoiceCore.ResponseAsync(http, db, user, ticket.Id, cancellationToken));
    }

    internal static async Task<IResult> ListAsync(
        [AsParameters] PageQuery page, [AsParameters] VoiceListQuery query, ZelvoDbContext db, CurrentUser user, TenantSettingsStore settings,
        CancellationToken cancellationToken)
    {
        var me = user.Account?.Id;
        var staff = user.Has(Permissions.VoiceRead);
        var tickets = VoiceCore.Records(db).AsNoTracking();
        if (!staff || query.Mine == true)
        {
            tickets = tickets.Where(t => t.RequesterId == me || (query.Mine != true && t.CreatedById == me));
        }

        if (query.Type is { } type)
        {
            tickets = tickets.Where(t => t.Type == type);
        }

        if (query.State is { } state)
        {
            tickets = tickets.Where(t => t.State == state);
        }
        else if (query.Open == true)
        {
            tickets = tickets.Where(t => t.State != TicketState.Resolved && t.State != TicketState.Closed);
        }

        if (query.ServiceId is { } serviceId)
        {
            tickets = tickets.Where(t => t.Voice!.ServiceId == serviceId);
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
        var context = await VoiceResponseContext.LoadAsync(db, user, settings, rows, cancellationToken);
        return TypedResults.Ok(new PageResult<VoiceResponse>([.. rows.Select(t => context.From(t))], page.Number, page.Size, total));
    }

    internal static async Task<IResult> AssigneesAsync(Guid? groupId, ZelvoDbContext db, CancellationToken cancellationToken)
    {
        var people = VoiceCore.Assignable(db);
        if (groupId is { } g)
        {
            people = people.Where(u => db.SupportGroupMembers.Any(m => m.GroupId == g && m.UserId == u.Id));
        }

        return TypedResults.Ok(await people.OrderBy(u => u.DisplayName ?? u.Email).Take(500).Select(u => new PersonResponse(u.Id, u.DisplayName)).ToListAsync(cancellationToken));
    }

    internal static async Task<IResult> GetAsync(Guid id, HttpContext http, ZelvoDbContext db, CurrentUser user, CancellationToken cancellationToken) =>
        await VoiceCore.FindVisibleAsync(db, user, id, cancellationToken) is null
            ? VoiceCore.NotFound()
            : TypedResults.Ok(await VoiceCore.ResponseAsync(http, db, user, id, cancellationToken));

    internal static async Task<IResult> UpdateAsync(
        Guid id, UpdateVoiceRequest request, HttpContext http, ZelvoDbContext db, CurrentUser user, TenantSettingsStore settings,
        ISearchIndexer indexer, CancellationToken cancellationToken)
    {
        var ticket = await VoiceCore.Records(db).SingleOrDefaultAsync(t => t.Id == id, cancellationToken);
        if (ticket is null)
        {
            return VoiceCore.NotFound();
        }

        if (VoiceCore.VersionProblem(http, db, ticket) is { } versionProblem)
        {
            return versionProblem;
        }

        if (ticket.State == TicketState.Closed)
        {
            return IncidentEndpoints.Closed();
        }

        var voice = ticket.Voice!;
        var categoryId = request.ClearCategory == true ? null : request.CategoryId ?? ticket.CategoryId;
        var serviceId = request.ClearService == true ? null : request.ServiceId ?? voice.ServiceId;
        var problem = await VoiceCore.CheckClassificationAsync(db, null, categoryId == ticket.CategoryId ? null : categoryId,
            serviceId == voice.ServiceId ? null : serviceId, ticket, cancellationToken);
        if (problem is not null)
        {
            return problem;
        }

        var voiceSettings = await settings.GetAsync(VoiceSettings.Voice, cancellationToken);
        if (request.Severity is { } severity)
        {
            if (ticket.Type != TicketType.Complaint || severity is < 1 or > VoiceSettingsValue.SeverityLevels)
            {
                return UserEndpoints.Validation("severity", "Only complaints have a severity, from 1 (highest) to 3.");
            }

            voice.Severity = severity;
            voice.Priority = voiceSettings.PriorityOf(ticket.Type, severity);
        }

        if (request.RootCauseKey is { } rootCause)
        {
            if (ticket.Type != TicketType.Complaint || (rootCause != voice.RootCauseKey && !voiceSettings.RootCauses.Any(r => r.Active && r.Key == rootCause)))
            {
                return UserEndpoints.Validation("rootCauseKey", "Choose one of the active root causes (complaints only).");
            }

            voice.RootCauseKey = rootCause;
        }

        if (request.Title is { } title)
        {
            if (string.IsNullOrWhiteSpace(title))
            {
                return UserEndpoints.Validation("title", "Give a title.");
            }

            ticket.Title = title.Trim();
        }

        ticket.CategoryId = categoryId;
        voice.ServiceId = serviceId;
        ticket.UpdatedAt = DateTimeOffset.UtcNow;
        return await VoiceCore.SaveAsync(http, db, user, indexer, ticket, cancellationToken);
    }

    internal static async Task<IResult> FormAsync(Guid id, ZelvoDbContext db, CurrentUser user, IAuditLog audit, CancellationToken cancellationToken)
    {
        var ticket = await VoiceCore.FindVisibleAsync(db, user, id, cancellationToken);
        if (ticket is null)
        {
            return VoiceCore.NotFound();
        }

        var view = await FormSubmission.ViewAsync(db, user, audit, ticket, cancellationToken);
        return TypedResults.Ok(view is null
            ? new JsonObject { ["form"] = null }
            : (JsonObject)System.Text.Json.JsonSerializer.SerializeToNode(view, FormJson.Options)!);
    }

    internal static async Task<IResult> DownloadAsync(
        Guid id, Guid attachmentId, HttpContext http, ZelvoDbContext db, CurrentUser user, IAttachmentStore store, IAuditLog audit,
        CancellationToken cancellationToken)
    {
        var ticket = await VoiceCore.FindVisibleAsync(db, user, id, cancellationToken);
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

    internal static async Task<IResult> CommentsAsync(Guid id, ZelvoDbContext db, CurrentUser user, CancellationToken cancellationToken)
    {
        var ticket = await VoiceCore.FindVisibleAsync(db, user, id, cancellationToken);
        return ticket is null ? VoiceCore.NotFound() : TypedResults.Ok(await VoiceCore.CommentsAsync(db, ticket.Id, user.Has(Permissions.VoiceRead), cancellationToken));
    }

    internal static async Task<IResult> CommentAsync(
        Guid id, VoiceCommentRequest request, HttpContext http, ZelvoDbContext db, CurrentUser user, TenantSettingsStore settings,
        CancellationToken cancellationToken)
    {
        var ticket = await VoiceCore.FindVisibleAsync(db, user, id, cancellationToken, tracked: true);
        if (ticket is null)
        {
            return VoiceCore.NotFound();
        }

        var me = user.Account!;
        var byStaff = user.Has(Permissions.VoiceEdit);
        if (!byStaff && ticket.RequesterId != me.Id)
        {
            return ApiProblem.Create(StatusCodes.Status403Forbidden, "forbidden", "Only staff and the requester may write on it.");
        }

        var visibility = request.Visibility ?? CommentVisibility.Public;
        if (!byStaff && visibility == CommentVisibility.Internal)
        {
            return ApiProblem.Create(StatusCodes.Status403Forbidden, "forbidden", "Requesters post public replies only.");
        }

        var language = LocalizationSettings.IsLanguage(request.Language) ? request.Language!
            : LocalizationSettings.IsLanguage(me.PreferredLanguage) ? me.PreferredLanguage!
            : await settings.GetAsync(LocalizationSettings.DefaultLanguage, cancellationToken);
        return await VoiceCore.AddCommentAsync(http, db, ticket, me.Id, byStaff && ticket.RequesterId != me.Id, visibility, request.Body, language, cancellationToken);
    }

    internal static async Task<IResult> TransitionAsync(
        Guid id, ChangeIncidentStateRequest request, HttpContext http, ZelvoDbContext db, CurrentUser user, TenantSettingsStore settings,
        ISearchIndexer indexer, CancellationToken cancellationToken)
    {
        var ticket = await VoiceCore.FindVisibleAsync(db, user, id, cancellationToken, tracked: true);
        if (ticket is null)
        {
            return VoiceCore.NotFound();
        }

        var to = request.State!.Value;
        var staff = user.Has(Permissions.VoiceEdit);
        var requesterReopens = to == TicketState.Reopened && ticket.RequesterId == user.Account?.Id && ticket.Type == TicketType.Inquiry;
        if (!staff && !requesterReopens)
        {
            return ApiProblem.Create(StatusCodes.Status403Forbidden, "forbidden", "Requesters may only reopen their resolved inquiries; a complaint's resolution is confirmed or rejected.");
        }

        if (VoiceCore.VersionProblem(http, db, ticket) is { } versionProblem)
        {
            return versionProblem;
        }

        if (!VoiceCore.NextStates(ticket, staff, user.Account?.Id).Contains(to))
        {
            return ticket.State == TicketState.Closed ? IncidentEndpoints.Closed()
                : ticket.Type == TicketType.Suggestion && to == TicketState.Resolved
                    ? ApiProblem.Create(StatusCodes.Status409Conflict, "review-required", "Suggestions are resolved by their review: Implemented or Declined, with feedback.")
                : ticket.Type == TicketType.Complaint && to == TicketState.Closed
                    ? ApiProblem.Create(StatusCodes.Status409Conflict, "confirmation-pending", "A resolved complaint closes when the customer confirms the resolution, or after the auto-close period.")
                : ApiProblem.Create(StatusCodes.Status409Conflict, "transition-not-allowed", $"It cannot move from {ticket.State} to {to}.");
        }

        if (to == TicketState.OnHold)
        {
            var reasons = await settings.GetAsync(IncidentSettings.OnHoldReasons, cancellationToken);
            if (!reasons.Any(r => r.Active && r.Key == request.OnHoldReason))
            {
                return UserEndpoints.Validation("onHoldReason", "Choose one of the active on-hold reasons.");
            }
        }

        if (to == TicketState.Resolved && ticket.Type == TicketType.Complaint && ticket.Voice!.RootCauseKey is null)
        {
            return UserEndpoints.Validation("rootCauseKey", "Choose the complaint's root cause before resolving it.");
        }

        switch (TicketWorkflow.Check(ticket, to, request.OnHoldReason, request.ResolutionNotes))
        {
            case TransitionError.OnHoldReasonRequired:
                return UserEndpoints.Validation("onHoldReason", "Choose why it is on hold.");
            case TransitionError.ResolutionNotesRequired:
                return UserEndpoints.Validation("resolutionNotes", ticket.Type == TicketType.Complaint ? "Write the resolution summary." : "Write the answer.");
            case TransitionError.AssigneeRequired:
                return ApiProblem.Create(StatusCodes.Status409Conflict, "assignee-required", "Assign it before starting work on it.");
            case TransitionError.NotAllowed:
                return ApiProblem.Create(StatusCodes.Status409Conflict, "transition-not-allowed", $"It cannot move from {ticket.State} to {to}.");
        }

        var now = DateTimeOffset.UtcNow;
        TicketWorkflow.Apply(ticket, to, request.OnHoldReason, request.ResolutionNotes, now);
        if (ticket.Type == TicketType.Complaint && to == TicketState.Resolved)
        {
            ticket.Voice!.Confirmation = ComplaintConfirmation.Pending;
            ticket.Voice.ConfirmationAt = null;
            ticket.Voice.ConfirmationComment = null;
        }

        return await VoiceCore.SaveAsync(http, db, user, indexer, ticket, cancellationToken);
    }

    internal static async Task<IResult> AssignAsync(
        Guid id, SetAssignmentRequest request, HttpContext http, ZelvoDbContext db, CurrentUser user, ISearchIndexer indexer, CancellationToken cancellationToken)
    {
        var ticket = await VoiceCore.Records(db).SingleOrDefaultAsync(t => t.Id == id, cancellationToken);
        if (ticket is null)
        {
            return VoiceCore.NotFound();
        }

        if (VoiceCore.VersionProblem(http, db, ticket) is { } versionProblem)
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

        if (request.AssigneeId is { } assigneeId && !await VoiceCore.Assignable(db).AnyAsync(u => u.Id == assigneeId, cancellationToken))
        {
            return UserEndpoints.Validation("assigneeId", "The assignee cannot be assigned complaints, inquiries and suggestions.");
        }

        if (request.GroupId is { } group && request.AssigneeId is { } member
            && !await db.SupportGroupMembers.AnyAsync(m => m.GroupId == group && m.UserId == member, cancellationToken))
        {
            return UserEndpoints.Validation("assigneeId", "The assignee is not a member of the group.");
        }

        ticket.GroupId = request.GroupId;
        ticket.RoutingRuleId = null;
        TicketWorkflow.Assign(ticket, request.AssigneeId, DateTimeOffset.UtcNow);
        return await VoiceCore.SaveAsync(http, db, user, indexer, ticket, cancellationToken);
    }

    internal static async Task<IResult> RouteAgainAsync(
        Guid id, HttpContext http, ZelvoDbContext db, CurrentUser user, TenantSettingsStore settings, ISearchIndexer indexer, CancellationToken cancellationToken)
    {
        var ticket = await VoiceCore.Records(db).SingleOrDefaultAsync(t => t.Id == id, cancellationToken);
        if (ticket is null)
        {
            return VoiceCore.NotFound();
        }

        if (!TicketWorkflow.IsOpen(ticket.State))
        {
            return ApiProblem.Create(StatusCodes.Status409Conflict, "ticket-not-open", "Only open records are routed.");
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await IncidentEndpoints.RouteAsync(db, settings, ticket, DateTimeOffset.UtcNow, cancellationToken);
        return await VoiceCore.SaveAsync(http, db, user, indexer, ticket, cancellationToken);
    }

    internal static async Task<IResult> ConfirmAsync(
        Guid id, ConfirmComplaintRequest request, HttpContext http, ZelvoDbContext db, CurrentUser user, TenantSettingsStore settings,
        ISearchIndexer indexer, CancellationToken cancellationToken)
    {
        var ticket = await VoiceCore.FindVisibleAsync(db, user, id, cancellationToken, tracked: true);
        if (ticket is null)
        {
            return VoiceCore.NotFound();
        }

        if (ticket.RequesterId != user.Account!.Id)
        {
            return ApiProblem.Create(StatusCodes.Status403Forbidden, "forbidden", "Only the person who made the complaint confirms its resolution.");
        }

        var language = LocalizationSettings.IsLanguage(user.Account.PreferredLanguage) ? user.Account.PreferredLanguage!
            : await settings.GetAsync(LocalizationSettings.DefaultLanguage, cancellationToken);
        return await VoiceCore.ConfirmAsync(http, db, user, indexer, ticket, request, user.Account.Id, language, cancellationToken);
    }

    internal static async Task<IResult> ReviewAsync(
        Guid id, ReviewSuggestionRequest request, HttpContext http, ZelvoDbContext db, CurrentUser user, ISearchIndexer indexer, CancellationToken cancellationToken)
    {
        var ticket = await VoiceCore.Records(db).SingleOrDefaultAsync(t => t.Id == id && t.Type == TicketType.Suggestion, cancellationToken);
        if (ticket is null)
        {
            return VoiceCore.NotFound();
        }

        if (VoiceCore.VersionProblem(http, db, ticket) is { } versionProblem)
        {
            return versionProblem;
        }

        if (!TicketWorkflow.IsOpen(ticket.State))
        {
            return ApiProblem.Create(StatusCodes.Status409Conflict, "ticket-not-open", "Only open suggestions are reviewed; reopen it first.");
        }

        var review = request.Review!.Value;
        var feedback = string.IsNullOrWhiteSpace(request.Feedback) ? null : request.Feedback.Trim();
        var resolves = review is SuggestionReview.Implemented or SuggestionReview.Declined;
        if (resolves && feedback is null)
        {
            return UserEndpoints.Validation("feedback", "Tell the submitter what was decided.");
        }

        var voice = ticket.Voice!;
        voice.Review = review;
        voice.ReviewFeedback = feedback ?? voice.ReviewFeedback;
        var now = DateTimeOffset.UtcNow;
        if (resolves)
        {
            TicketWorkflow.Apply(ticket, TicketState.Resolved, null, feedback, now);
        }

        ticket.UpdatedAt = now;
        return await VoiceCore.SaveAsync(http, db, user, indexer, ticket, cancellationToken);
    }

    internal static async Task<IResult> ConvertAsync(
        Guid id, ConvertInquiryRequest request, HttpContext http, ZelvoDbContext db, CurrentUser user, ITenantContext tenant,
        TenantSettingsStore settings, ISearchIndexer indexer, CancellationToken cancellationToken)
    {
        var inquiry = await VoiceCore.Records(db).SingleOrDefaultAsync(t => t.Id == id && t.Type == TicketType.Inquiry, cancellationToken);
        if (inquiry is null)
        {
            return VoiceCore.NotFound();
        }

        if (VoiceCore.VersionProblem(http, db, inquiry) is { } versionProblem)
        {
            return versionProblem;
        }

        if (!TicketWorkflow.IsOpen(inquiry.State))
        {
            return ApiProblem.Create(StatusCodes.Status409Conflict, "ticket-not-open", "Only open inquiries are converted.");
        }

        var to = request.To!.Value;
        var now = DateTimeOffset.UtcNow;
        PublishedService? found = null;
        if (to == TicketType.Complaint)
        {
            if (request.Severity is { } s && s is < 1 or > VoiceSettingsValue.SeverityLevels)
            {
                return UserEndpoints.Validation("severity", "Use a severity from 1 (highest) to 3.");
            }
        }
        else if (to == TicketType.ServiceRequest)
        {
            if (inquiry.Voice!.Anonymous)
            {
                return ApiProblem.Create(StatusCodes.Status409Conflict, "anonymous-request", "Anonymous inquiries cannot become service requests; ask the person to sign in and request the service.");
            }

            var requester = await db.Users.AsNoTracking().SingleAsync(u => u.Id == inquiry.RequesterId, cancellationToken);
            found = request.ServiceId is { } serviceId
                ? await PublishedServices.FindAsync(db, await CatalogScope.OfUserAsync(db, requester, cancellationToken), serviceId, cancellationToken)
                : null;
            if (found is null || found.Service.State != ServiceState.Active || found.Card.Type == ServiceType.External)
            {
                return UserEndpoints.Validation("serviceId", "Choose an active internal service offered to the requester.");
            }
        }
        else
        {
            return UserEndpoints.Validation("to", "Convert to Complaint or ServiceRequest.");
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        Ticket created;
        var sla = http.RequestServices.GetRequiredService<SlaTracker>();
        if (found is null)
        {
            var voice = inquiry.Voice!;
            created = await VoiceCore.CreateAsync(http, db, settings, tenant, indexer,
                new VoiceDraft(TicketType.Complaint, inquiry.Title, inquiry.Description, inquiry.RequesterId, user.Account?.Id, inquiry.Channel,
                    inquiry.CategoryId, inquiry.LocationId, voice.ServiceId, request.Severity, voice.Anonymous, voice.TrackingCodeHash), cancellationToken);
        }
        else
        {
            var prefixes = await settings.GetAsync(IncidentSettings.NumberPrefixes, cancellationToken);
            var sequence = await TicketNumbers.NextAsync(db, tenant.TenantId!.Value, TicketType.ServiceRequest, cancellationToken);
            created = new Ticket
            {
                Type = TicketType.ServiceRequest,
                Number = TicketNumbers.Format(TicketNumbers.PrefixOf(prefixes, TicketType.ServiceRequest), sequence),
                Sequence = sequence,
                Title = inquiry.Title,
                Description = inquiry.Description,
                Channel = inquiry.Channel,
                RequesterId = inquiry.RequesterId,
                CreatedById = user.Account?.Id,
                CategoryId = found.Card.CategoryIds.Count > 0 ? found.Card.CategoryIds[0] : null,
                LocationId = inquiry.LocationId,
                CreatedAt = now,
                UpdatedAt = now,
            };
            await IncidentEndpoints.RouteAsync(db, settings, created, now, cancellationToken);
            db.Tickets.Add(created);
            db.ServiceRequests.Add(new ServiceRequestDetails { TicketId = created.Id, ServiceId = found.Service.Id, ServiceVersionId = found.Version.Id });
            await sla.UpdateAsync([created], now, cancellationToken);
        }

        inquiry.Voice!.ConvertedToId = created.Id;
        TicketWorkflow.Apply(inquiry, TicketState.Resolved, null, $"Converted to {created.Number}.", now);
        await sla.UpdateAsync([inquiry], now, cancellationToken);
        indexer.Upsert(VoiceSearch.Document(inquiry, await VoiceSearch.StaffRolesAsync(db, cancellationToken)));
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return TypedResults.Ok(await VoiceCore.ResponseAsync(http, db, user, inquiry.Id, cancellationToken));
    }
}

/// <summary>A new complaint, inquiry or suggestion.</summary>
internal sealed class CreateVoiceRequest
{
    /// <summary>Complaint, Inquiry or Suggestion.</summary>
    [Required]
    public TicketType? Type { get; init; }

    /// <summary>Short summary.</summary>
    [Required]
    [StringLength(Ticket.TitleMaxLength, MinimumLength = 1)]
    public string? Title { get; init; }

    /// <summary>Details.</summary>
    [StringLength(Ticket.DescriptionMaxLength)]
    public string? Description { get; init; }

    /// <summary>Category (the form follows it).</summary>
    public Guid? CategoryId { get; init; }

    /// <summary>The catalog service it is about.</summary>
    public Guid? ServiceId { get; init; }

    /// <summary>A complaint's severity 1–3; Medium (2) when left out.</summary>
    public int? Severity { get; init; }

    /// <summary>Staff only: who it is for.</summary>
    public Guid? RequesterId { get; init; }

    /// <summary>Staff only: how it reached them (Phone by default).</summary>
    public TicketChannel? Channel { get; init; }

    /// <summary>The form version filled.</summary>
    public Guid? FormVersionId { get; init; }

    /// <summary>Answers by field key.</summary>
    public JsonObject? FormAnswers { get; init; }
}

/// <summary>Staff changes; parts left out are kept.</summary>
internal sealed class UpdateVoiceRequest
{
    /// <summary>New title.</summary>
    [StringLength(Ticket.TitleMaxLength)]
    public string? Title { get; init; }

    /// <summary>Complaint severity 1–3.</summary>
    public int? Severity { get; init; }

    /// <summary>Complaint root cause key.</summary>
    [StringLength(CustomerVoiceRecord.KeyMaxLength)]
    public string? RootCauseKey { get; init; }

    /// <summary>New category.</summary>
    public Guid? CategoryId { get; init; }

    /// <summary>Remove the category.</summary>
    public bool? ClearCategory { get; init; }

    /// <summary>New service.</summary>
    public Guid? ServiceId { get; init; }

    /// <summary>Remove the service.</summary>
    public bool? ClearService { get; init; }
}

/// <summary>Filters of the list.</summary>
internal sealed class VoiceListQuery
{
    /// <summary>Only the caller's own.</summary>
    public bool? Mine { get; init; }

    /// <summary>Only this record type.</summary>
    public TicketType? Type { get; init; }

    /// <summary>Only open ones.</summary>
    public bool? Open { get; init; }

    /// <summary>Only in this state.</summary>
    public TicketState? State { get; init; }

    /// <summary>Only about this service.</summary>
    public Guid? ServiceId { get; init; }

    /// <summary>A number, for example CMP-000123.</summary>
    [StringLength(Ticket.NumberMaxLength)]
    public string? Number { get; init; }

    /// <summary>me, none or a user id (staff).</summary>
    [StringLength(40)]
    public string? Assignee { get; init; }

    /// <summary>mine, none or a group id (staff).</summary>
    [StringLength(40)]
    public string? Group { get; init; }
}

/// <summary>A reply or note.</summary>
internal sealed class VoiceCommentRequest
{
    /// <summary>The text.</summary>
    [Required]
    [StringLength(TicketComment.BodyMaxLength, MinimumLength = 1)]
    public string? Body { get; init; }

    /// <summary>Public (default) or Internal (staff).</summary>
    public CommentVisibility? Visibility { get; init; }

    /// <summary>en or ar; the writer's language when left out.</summary>
    [StringLength(2)]
    public string? Language { get; init; }
}

/// <summary>The customer's answer to a resolved complaint.</summary>
internal sealed class ConfirmComplaintRequest
{
    /// <summary>True: the resolution is accepted and the complaint closes; false: rejected and reopened.</summary>
    [Required]
    public bool? Accept { get; init; }

    /// <summary>Why it is rejected (needed then).</summary>
    [StringLength(CustomerVoiceRecord.TextMaxLength)]
    public string? Comment { get; init; }
}

/// <summary>A suggestion's review.</summary>
internal sealed class ReviewSuggestionRequest
{
    /// <summary>The review state.</summary>
    [Required]
    public SuggestionReview? Review { get; init; }

    /// <summary>Feedback to the submitter.</summary>
    [StringLength(CustomerVoiceRecord.TextMaxLength)]
    public string? Feedback { get; init; }
}

/// <summary>Converting an inquiry.</summary>
internal sealed class ConvertInquiryRequest
{
    /// <summary>Complaint or ServiceRequest.</summary>
    [Required]
    public TicketType? To { get; init; }

    /// <summary>The service, for a service request.</summary>
    public Guid? ServiceId { get; init; }

    /// <summary>The complaint's severity.</summary>
    public int? Severity { get; init; }
}
