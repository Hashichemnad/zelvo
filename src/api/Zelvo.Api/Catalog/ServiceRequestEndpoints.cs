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
using Zelvo.Data;
using Zelvo.Data.Auditing;
using Zelvo.Data.Catalog;
using Zelvo.Data.Identity;
using Zelvo.Data.Tenancy;
using Zelvo.Data.Tickets;
using Zelvo.Tickets;
using Zelvo.Tickets.Forms;
using Zelvo.Tickets.Sla;

namespace Zelvo.Api.Catalog;

/// <summary>
/// Service requests (task C2, product owner decision 2026-11-24): requesting a catalog service with
/// its form and required documents, for yourself or on behalf of someone else when the service
/// allows your role. Requests share the ticket core (numbers <c>REQ-000123</c>, states, B2 routing
/// and B3 SLA clocks). Fulfilment (initial group, service SLAs, tasks, the step journey) comes with
/// task C4. Requesters see their own requests; staff with requests.read see all.
/// </summary>
internal static class ServiceRequestEndpoints
{
    /// <summary>Most files per required document.</summary>
    public const int MaxFilesPerDocument = 10;

    public static RouteGroupBuilder MapServiceRequestEndpoints(this RouteGroupBuilder group)
    {
        var requests = group.MapGroup("/service-requests").WithTags("Service requests").RequireTenant();
        requests.MapPost("/", CreateAsync).WithName("CreateServiceRequest")
            .WithSummary("Request a published, active, internal service the caller may see: its form's answers (formVersionId, formAnswers) and required documents (document key → ids of files uploaded with /form-uploads, field document<Key>). People whose roles the service allows may name requesterId to request on behalf of someone, who must be allowed the service too. Prerequisite services must be completed first. Routed by the assignment rules; audited.")
            .RequirePerson().RequireAuthorization().Produces<ServiceRequestResponse>(StatusCodes.Status201Created).ProducesValidationProblem();
        requests.MapGet("/", ListAsync).WithName("ListServiceRequests")
            .WithSummary("Service requests, newest first: all for holders of requests.read (mine=true: the caller's own), otherwise the caller's own (also those made for them by others).")
            .RequireAuthorization().Produces<PageResult<ServiceRequestResponse>>();
        requests.MapGet("/assignees", AssigneesAsync).WithName("ListServiceRequestAssignees")
            .WithSummary("Active users who can be assigned service requests (their roles hold requests.edit); with groupId, only that group's members.")
            .RequirePermission(Permissions.RequestsAssign).Produces<IReadOnlyList<PersonResponse>>();
        requests.MapGet("/{id:guid}", GetAsync).WithName("GetServiceRequest")
            .WithSummary("One service request with its service, steps and current step. Requesters see a smaller view. Returns an ETag to send as If-Match with changes.")
            .RequireAuthorization().Produces<ServiceRequestResponse>();
        requests.MapGet("/{id:guid}/form", FormAsync).WithName("GetServiceRequestAnswers")
            .WithSummary("The form answers given with the request, with the fields as they were then. Staff viewing personal answers is audited.")
            .RequireAuthorization().Produces<TicketFormView>();
        requests.MapGet("/{id:guid}/attachments", AttachmentsAsync).WithName("ListServiceRequestAttachments")
            .WithSummary("Documents and files sent with the request, with the required document each belongs to.")
            .RequireAuthorization().Produces<IReadOnlyList<ServiceRequestAttachmentResponse>>();
        requests.MapGet("/{id:guid}/attachments/{attachmentId:guid}/content", DownloadAsync).WithName("DownloadServiceRequestAttachment")
            .WithSummary("Download a file of the request once scanned clean; audited.")
            .RequireAuthorization().Produces<byte[]>(contentType: "application/octet-stream");
        requests.MapPost("/{id:guid}/transitions", TransitionAsync).WithName("ChangeServiceRequestState")
            .WithSummary("Move the request to InProgress, OnHold (with an active on-hold reason), Resolved (with notes), Closed or Reopened. Requesters may reopen their resolved requests; everything else needs requests.edit. Closed is final; audited.")
            .RequirePerson().RequireAuthorization().Produces<ServiceRequestResponse>().ProducesValidationProblem();
        requests.MapPut("/{id:guid}/assignment", SetAssignmentAsync).WithName("SetServiceRequestAssignment")
            .WithSummary("Set the request's group and assignee (the assignee is a member of the group when one is given; null removes either). Needs requests.assign; audited.")
            .RequirePermission(Permissions.RequestsAssign).Produces<ServiceRequestResponse>().ProducesValidationProblem();
        requests.MapPost("/{id:guid}/route", RouteAgainAsync).WithName("RouteServiceRequest")
            .WithSummary("Run the assignment rules again. Open requests only; needs requests.assign; audited.")
            .RequirePermission(Permissions.RequestsAssign).Produces<ServiceRequestResponse>();
        return group;
    }

    internal static async Task<IResult> CreateAsync(
        CreateServiceRequestRequest request, HttpContext http, ZelvoDbContext db, CurrentUser user, ITenantContext tenant,
        TenantSettingsStore settings, IAuditLog audit, CancellationToken cancellationToken)
    {
        var me = user.Account!;
        var scope = await CatalogScope.OfAsync(db, user, cancellationToken);
        var found = await PublishedServices.FindAsync(db, scope, request.ServiceId!.Value, cancellationToken);
        if (found is null)
        {
            return CatalogBrowseEndpoints.NotFound();
        }

        var card = found.Card;
        if (found.Service.State == ServiceState.Suspended)
        {
            return ApiProblem.Create(StatusCodes.Status409Conflict, "service-suspended", "The service is suspended and cannot be requested now.");
        }

        if (card.Type == ServiceType.External)
        {
            return ApiProblem.Create(StatusCodes.Status409Conflict, "service-external", "The service is requested on the provider's site.");
        }

        // On behalf of someone else: only roles the service allows, for someone the service is for.
        var requester = me;
        if (request.RequesterId is { } requesterId && requesterId != me.Id)
        {
            if (!CatalogAccess.MayRequestOnBehalf(card, scope))
            {
                return ApiProblem.Create(StatusCodes.Status403Forbidden, "forbidden", "Your roles may not request this service on behalf of someone else.");
            }

            requester = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == requesterId && u.Status == UserStatus.Active, cancellationToken);
            if (requester is null)
            {
                return UserEndpoints.Validation("requesterId", "The requester is not an active user of this organisation.");
            }

            if (!CatalogAccess.MaySee(card.Access, await CatalogScope.OfUserAsync(db, requester, cancellationToken)))
            {
                return UserEndpoints.Validation("requesterId", "This service is not offered to the person you are requesting for.");
            }
        }

        var completed = await CatalogBrowseEndpoints.CompletedAsync(db, requester.Id, card.PrerequisiteServiceIds, cancellationToken);
        var missing = card.PrerequisiteServiceIds.Where(p => !completed.Contains(p)).ToList();
        if (missing.Count > 0)
        {
            var codes = await db.CatalogServices.AsNoTracking().Where(s => missing.Contains(s.Id)).Select(s => s.Code).ToListAsync(cancellationToken);
            return TypedResults.Problem(statusCode: StatusCodes.Status409Conflict, title: "Complete the prerequisite services first.",
                type: ApiProblem.TypeFor("prerequisite-missing"),
                extensions: new Dictionary<string, object?>(StringComparer.Ordinal) { ["code"] = "prerequisite-missing", ["services"] = codes });
        }

        var policy = await settings.GetAsync(IncidentSettings.Attachments, cancellationToken);
        var form = card.FormId is { } formId ? await FormResolver.PublishedAsync(db, formId, cancellationToken) : null;
        if (card.FormId is not null && form is null)
        {
            return ApiProblem.Create(StatusCodes.Status409Conflict, "service-unavailable", "The service's form is not available. Try again later.");
        }

        var (formResponse, formProblem) = await FormSubmission.PrepareAsync(
            db, user, audit, form, requester.Id, request.FormVersionId, request.FormAnswers, policy, cancellationToken);
        if (formProblem is not null)
        {
            return formProblem;
        }

        var (documents, documentErrors) = await CheckDocumentsAsync(db, me.Id, card, request.Documents, policy, cancellationToken);
        if (documentErrors.Count > 0)
        {
            return TypedResults.ValidationProblem(
                documentErrors.GroupBy(e => e.Field).ToDictionary(g => g.Key, g => g.Select(e => e.Message).ToArray(), StringComparer.Ordinal),
                type: ApiProblem.TypeFor("validation-failed"),
                extensions: new Dictionary<string, object?>(StringComparer.Ordinal) { ["code"] = "validation-failed", ["documentErrors"] = documentErrors });
        }

        var language = LocalizationSettings.IsLanguage(requester.PreferredLanguage)
            ? requester.PreferredLanguage!
            : await settings.GetAsync(LocalizationSettings.DefaultLanguage, cancellationToken);
        var title = string.IsNullOrWhiteSpace(request.Title) ? card.Name.GetOrFallback(language) ?? found.Service.Code : request.Title.Trim();
        var prefixes = await settings.GetAsync(IncidentSettings.NumberPrefixes, cancellationToken);
        var now = DateTimeOffset.UtcNow;

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var sequence = await TicketNumbers.NextAsync(db, tenant.TenantId!.Value, TicketType.ServiceRequest, cancellationToken);
        var ticket = new Ticket
        {
            Type = TicketType.ServiceRequest,
            Number = TicketNumbers.Format(TicketNumbers.PrefixOf(prefixes, TicketType.ServiceRequest), sequence),
            Sequence = sequence,
            Title = title[..Math.Min(title.Length, Ticket.TitleMaxLength)],
            Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim(),
            Channel = TicketChannel.Portal,
            RequesterId = requester.Id,
            CreatedById = me.Id,
            CategoryId = card.CategoryIds.Count > 0 ? card.CategoryIds[0] : null,
            LocationId = requester.LocationId,
            CreatedAt = now,
            UpdatedAt = now,
        };

        // Task B2: placed by the assignment rules; C4 adds the service's initial group.
        await IncidentEndpoints.RouteAsync(db, settings, ticket, now, cancellationToken);
        db.Tickets.Add(ticket);
        db.ServiceRequests.Add(new ServiceRequestDetails { TicketId = ticket.Id, ServiceId = found.Service.Id, ServiceVersionId = found.Version.Id });
        formResponse?.Attach(db, ticket);
        foreach (var file in documents)
        {
            file.TicketId = ticket.Id;
        }

        await http.RequestServices.GetRequiredService<SlaTracker>().UpdateAsync([ticket], now, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        var saved = await db.Tickets.AsNoTracking().SingleAsync(t => t.Id == ticket.Id, cancellationToken);
        return TypedResults.Created($"/api/v1/service-requests/{ticket.Id}", await ResponseAsync(http, db, user, saved, cancellationToken));
    }

    /// <summary>Checks the files sent for each required document: the caller's own unsent uploads for that document, of its types and size.</summary>
    internal static async Task<(List<TicketAttachment> Files, List<FormAnswerError> Errors)> CheckDocumentsAsync(
        ZelvoDbContext db, Guid uploader, ServiceCard card, IReadOnlyDictionary<string, List<Guid>>? sent, AttachmentPolicy policy, CancellationToken cancellationToken)
    {
        var errors = new List<FormAnswerError>();
        var files = new List<TicketAttachment>();
        sent ??= new Dictionary<string, List<Guid>>();
        foreach (var key in sent.Keys.Where(k => card.Documents.All(d => d.Key != k)))
        {
            errors.Add(new FormAnswerError($"documents.{key}", "document-unknown", "The service asks for no such document."));
        }

        var ids = sent.Values.SelectMany(v => v).Distinct().ToList();
        var rows = ids.Count == 0 ? [] : await db.TicketAttachments.Where(a => ids.Contains(a.Id)).ToDictionaryAsync(a => a.Id, cancellationToken);
        foreach (var document in card.Documents)
        {
            var field = $"documents.{document.Key}";
            var given = sent.GetValueOrDefault(document.Key)?.Distinct().ToList() ?? [];
            if (given.Count == 0)
            {
                if (document.Required)
                {
                    errors.Add(new FormAnswerError(field, "document-required", "Upload this document."));
                }

                continue;
            }

            if (given.Count > MaxFilesPerDocument)
            {
                errors.Add(new FormAnswerError(field, "too-many-files", $"Upload at most {MaxFilesPerDocument} files for one document."));
                continue;
            }

            var allowed = document.FileTypes.Count > 0 ? document.FileTypes.Intersect(policy.AllowedExtensions, StringComparer.Ordinal).ToList() : policy.AllowedExtensions;
            var maxBytes = Math.Min(policy.MaxBytes, (document.MaxSizeMb ?? int.MaxValue) * 1024L * 1024L);
            foreach (var id in given)
            {
                if (!rows.TryGetValue(id, out var file) || file.TicketId is not null || file.ArticleId is not null || file.ServiceId is not null
                    || file.UploadedById != uploader || file.FormFieldKey != ServiceCards.UploadFieldOf(document.Key))
                {
                    errors.Add(new FormAnswerError(field, "file-unknown", "Upload the file for this document again."));
                }
                else if (file.ScanStatus == AttachmentScanStatus.Infected)
                {
                    errors.Add(new FormAnswerError(field, "file-infected", $"The file {file.FileName} was rejected by the malware scan."));
                }
                else if (!allowed.Contains(FileTypes.ExtensionOf(file.FileName), StringComparer.Ordinal))
                {
                    errors.Add(new FormAnswerError(field, "file-type", $"Accepted file types: {string.Join(", ", allowed)}."));
                }
                else if (file.Size > maxBytes)
                {
                    errors.Add(new FormAnswerError(field, "file-too-large", $"Files can be at most {maxBytes / 1024 / 1024} MB."));
                }
                else
                {
                    files.Add(file);
                }
            }
        }

        return (files, errors);
    }

    internal static async Task<IResult> ListAsync(
        [AsParameters] PageQuery page, [AsParameters] ServiceRequestListQuery query, HttpContext http, ZelvoDbContext db, CurrentUser user,
        CancellationToken cancellationToken)
    {
        var me = user.Account?.Id;
        var staff = user.Has(Permissions.RequestsRead);
        var tickets = Requests(db);
        if (!staff || query.Mine == true)
        {
            tickets = tickets.Where(t => t.RequesterId == me || (query.Mine != true && t.CreatedById == me));
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
            tickets = tickets.Where(t => db.ServiceRequests.Any(r => r.TicketId == t.Id && r.ServiceId == serviceId));
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
                { } other when Guid.TryParse(other, out var assigneeId) => tickets.Where(t => t.AssigneeId == assigneeId),
                _ => tickets,
            };
            tickets = query.Group switch
            {
                "mine" => tickets.Where(t => db.SupportGroupMembers.Any(m => m.GroupId == t.GroupId && m.UserId == me)),
                "none" => tickets.Where(t => t.GroupId == null),
                { } other when Guid.TryParse(other, out var groupId) => tickets.Where(t => t.GroupId == groupId),
                _ => tickets,
            };
        }

        var ordered = tickets.OrderByDescending(t => t.CreatedAt).ThenByDescending(t => t.Id);
        var total = await ordered.LongCountAsync(cancellationToken);
        var rows = await ordered.Skip(page.Skip).Take(page.Size).ToListAsync(cancellationToken);
        var context = await ResponseContext.LoadAsync(db, user, rows, cancellationToken);
        return TypedResults.Ok(new PageResult<ServiceRequestResponse>([.. rows.Select(context.From)], page.Number, page.Size, total));
    }

    internal static async Task<IResult> AssigneesAsync(Guid? groupId, ZelvoDbContext db, CancellationToken cancellationToken)
    {
        var people = Assignable(db);
        if (groupId is { } group)
        {
            people = people.Where(u => db.SupportGroupMembers.Any(m => m.GroupId == group && m.UserId == u.Id));
        }

        return TypedResults.Ok(await people.OrderBy(u => u.DisplayName).Select(u => new PersonResponse(u.Id, u.DisplayName)).ToListAsync(cancellationToken));
    }

    internal static async Task<IResult> GetAsync(Guid id, HttpContext http, ZelvoDbContext db, CurrentUser user, CancellationToken cancellationToken)
    {
        var ticket = await FindVisibleAsync(db, user, id, cancellationToken);
        return ticket is null ? NotFound() : TypedResults.Ok(await ResponseAsync(http, db, user, ticket, cancellationToken));
    }

    internal static async Task<IResult> FormAsync(Guid id, ZelvoDbContext db, CurrentUser user, IAuditLog audit, CancellationToken cancellationToken)
    {
        var ticket = await FindVisibleAsync(db, user, id, cancellationToken);
        if (ticket is null)
        {
            return NotFound();
        }

        var view = await FormSubmission.ViewAsync(db, user, audit, ticket, cancellationToken);
        return TypedResults.Ok(view is null
            ? new JsonObject { ["form"] = null }
            : (JsonObject)System.Text.Json.JsonSerializer.SerializeToNode(view, FormJson.Options)!);
    }

    internal static async Task<IResult> AttachmentsAsync(Guid id, ZelvoDbContext db, CurrentUser user, CancellationToken cancellationToken)
    {
        var ticket = await FindVisibleAsync(db, user, id, cancellationToken);
        if (ticket is null)
        {
            return NotFound();
        }

        var files = await db.TicketAttachments.AsNoTracking().Where(a => a.TicketId == id).OrderBy(a => a.UploadedAt).ToListAsync(cancellationToken);
        return TypedResults.Ok(files.Select(f => new ServiceRequestAttachmentResponse(
            f.Id, f.FileName, f.ContentType, f.Size, f.ScanStatus, ServiceCards.DocumentKeyOf(f.FormFieldKey), f.FormFieldKey, f.UploadedAt)).ToList());
    }

    internal static async Task<IResult> DownloadAsync(
        Guid id, Guid attachmentId, HttpContext http, ZelvoDbContext db, CurrentUser user, IAttachmentStore store, IAuditLog audit,
        CancellationToken cancellationToken)
    {
        var ticket = await FindVisibleAsync(db, user, id, cancellationToken);
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
        Guid id, ChangeIncidentStateRequest request, HttpContext http, ZelvoDbContext db, CurrentUser user, TenantSettingsStore settings,
        CancellationToken cancellationToken)
    {
        var ticket = await FindVisibleAsync(db, user, id, cancellationToken, tracked: true);
        if (ticket is null)
        {
            return NotFound();
        }

        var to = request.State!.Value;
        var requesterReopens = to == TicketState.Reopened && ticket.RequesterId == user.Account?.Id;
        if (!user.Has(Permissions.RequestsEdit) && !requesterReopens)
        {
            return ApiProblem.Create(StatusCodes.Status403Forbidden, "forbidden", "Requesters may only reopen their resolved requests.");
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
                return ticket.State == TicketState.Closed ? IncidentEndpoints.Closed()
                    : ApiProblem.Create(StatusCodes.Status409Conflict, "transition-not-allowed", $"A request cannot move from {ticket.State} to {to}.");
            case TransitionError.OnHoldReasonRequired:
                return UserEndpoints.Validation("onHoldReason", "Choose why the request is on hold.");
            case TransitionError.ResolutionNotesRequired:
                return UserEndpoints.Validation("resolutionNotes", "Say how the request was fulfilled.");
            case TransitionError.AssigneeRequired:
                return ApiProblem.Create(StatusCodes.Status409Conflict, "assignee-required", "Assign the request before starting work on it.");
        }

        TicketWorkflow.Apply(ticket, to, request.OnHoldReason, request.ResolutionNotes, DateTimeOffset.UtcNow);
        return await SaveAsync(http, db, user, ticket, cancellationToken);
    }

    internal static async Task<IResult> SetAssignmentAsync(
        Guid id, SetAssignmentRequest request, HttpContext http, ZelvoDbContext db, CurrentUser user, CancellationToken cancellationToken)
    {
        var ticket = await Requests(db).SingleOrDefaultAsync(t => t.Id == id, cancellationToken);
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
            return IncidentEndpoints.Closed();
        }

        if (request.GroupId is { } groupId && groupId != ticket.GroupId && !await db.SupportGroups.AnyAsync(g => g.Id == groupId && g.Active, cancellationToken))
        {
            return UserEndpoints.Validation("groupId", "Choose an active group.");
        }

        if (request.AssigneeId is { } assigneeId && !await Assignable(db).AnyAsync(u => u.Id == assigneeId, cancellationToken))
        {
            return UserEndpoints.Validation("assigneeId", "The assignee cannot be assigned service requests.");
        }

        if (request.GroupId is { } group && request.AssigneeId is { } member
            && !await db.SupportGroupMembers.AnyAsync(m => m.GroupId == group && m.UserId == member, cancellationToken))
        {
            return UserEndpoints.Validation("assigneeId", "The assignee is not a member of the group.");
        }

        ticket.GroupId = request.GroupId;
        ticket.RoutingRuleId = null;
        TicketWorkflow.Assign(ticket, request.AssigneeId, DateTimeOffset.UtcNow);
        return await SaveAsync(http, db, user, ticket, cancellationToken);
    }

    internal static async Task<IResult> RouteAgainAsync(
        Guid id, HttpContext http, ZelvoDbContext db, CurrentUser user, TenantSettingsStore settings, CancellationToken cancellationToken)
    {
        var ticket = await Requests(db).SingleOrDefaultAsync(t => t.Id == id, cancellationToken);
        if (ticket is null)
        {
            return NotFound();
        }

        if (!TicketWorkflow.IsOpen(ticket.State))
        {
            return ApiProblem.Create(StatusCodes.Status409Conflict, "ticket-not-open", "Only open requests are routed.");
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await IncidentEndpoints.RouteAsync(db, settings, ticket, DateTimeOffset.UtcNow, cancellationToken);
        return await SaveAsync(http, db, user, ticket, cancellationToken);
    }

    // ---- helpers ----

    internal static IQueryable<Ticket> Requests(ZelvoDbContext db) => db.Tickets.Where(t => t.Type == TicketType.ServiceRequest);

    private static IQueryable<UserAccount> Assignable(ZelvoDbContext db) =>
        db.Users.Where(u => u.Status == UserStatus.Active
            && u.Roles.Any(r => r.Role!.Permissions.Any(p => p.Permission == Permissions.RequestsEdit)));

    /// <summary>A request the caller may see: staff with requests.read, the requester, or who made it for them. Anyone else gets null (404).</summary>
    internal static async Task<Ticket?> FindVisibleAsync(ZelvoDbContext db, CurrentUser user, Guid id, CancellationToken cancellationToken, bool tracked = false)
    {
        var query = tracked ? Requests(db) : Requests(db).AsNoTracking();
        var ticket = await query.SingleOrDefaultAsync(t => t.Id == id, cancellationToken);
        if (ticket is null)
        {
            return null;
        }

        var me = user.Account?.Id;
        return user.Has(Permissions.RequestsRead) || (me is not null && (ticket.RequesterId == me || ticket.CreatedById == me)) ? ticket : null;
    }

    private static Microsoft.AspNetCore.Http.HttpResults.ProblemHttpResult? VersionProblem(HttpContext http, ZelvoDbContext db, Ticket ticket)
    {
        var ifMatch = http.Request.Headers.IfMatch.ToString();
        if (string.IsNullOrEmpty(ifMatch))
        {
            return null;
        }

        if (!uint.TryParse(ifMatch.Trim().Replace("W/", "", StringComparison.Ordinal).Trim('"'), out var version) || version != ticket.Version)
        {
            return FormsCommon.VersionMismatch();
        }

        db.Entry(ticket).Property(t => t.Version).OriginalValue = version;
        return null;
    }

    private static async Task<IResult> SaveAsync(HttpContext http, ZelvoDbContext db, CurrentUser user, Ticket ticket, CancellationToken cancellationToken)
    {
        await using var transaction = db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(cancellationToken) : null;
        try
        {
            await http.RequestServices.GetRequiredService<SlaTracker>().UpdateAsync([ticket], DateTimeOffset.UtcNow, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            if (transaction is not null)
            {
                await transaction.CommitAsync(cancellationToken);
            }
            else
            {
                await db.Database.CurrentTransaction!.CommitAsync(cancellationToken);
            }
        }
        catch (DbUpdateConcurrencyException)
        {
            return FormsCommon.VersionMismatch();
        }

        var saved = await db.Tickets.AsNoTracking().SingleAsync(t => t.Id == ticket.Id, cancellationToken);
        return TypedResults.Ok(await ResponseAsync(http, db, user, saved, cancellationToken));
    }

    private static async Task<ServiceRequestResponse> ResponseAsync(HttpContext http, ZelvoDbContext db, CurrentUser user, Ticket ticket, CancellationToken cancellationToken)
    {
        var context = await ResponseContext.LoadAsync(db, user, [ticket], cancellationToken);
        http.Response.Headers.ETag = $"\"{ticket.Version}\"";
        return context.From(ticket);
    }

    private static Microsoft.AspNetCore.Http.HttpResults.ProblemHttpResult NotFound() => FormsCommon.NotFound("service-request");

    /// <summary>What responses need, loaded once for a page of requests.</summary>
    private sealed class ResponseContext
    {
        private Dictionary<Guid, ServiceRequestDetails> _details = [];
        private Dictionary<Guid, (CatalogService Service, ServiceCard Card)> _services = [];
        private Dictionary<Guid, string?> _names = [];
        private DirectoryRefs _refs = null!;
        private TicketSlaView _slas = null!;
        private CurrentUser _user = null!;
        private bool _staff;

        public static async Task<ResponseContext> LoadAsync(ZelvoDbContext db, CurrentUser user, IReadOnlyList<Ticket> tickets, CancellationToken cancellationToken)
        {
            var ids = tickets.Select(t => t.Id).ToList();
            var details = await db.ServiceRequests.AsNoTracking().Where(r => ids.Contains(r.TicketId)).ToDictionaryAsync(r => r.TicketId, cancellationToken);
            var versionIds = details.Values.Select(d => d.ServiceVersionId).Distinct().ToList();
            var versions = await db.CatalogServiceVersions.AsNoTracking().Where(v => versionIds.Contains(v.Id)).ToDictionaryAsync(v => v.Id, v => v.Content, cancellationToken);
            var serviceIds = details.Values.Select(d => d.ServiceId).Distinct().ToList();
            var services = await db.CatalogServices.AsNoTracking().Where(s => serviceIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id, cancellationToken);
            var staff = user.Has(Permissions.RequestsRead);
            return new ResponseContext
            {
                _details = details,
                _services = details.Values.ToDictionary(d => d.TicketId, d => (services[d.ServiceId], ServiceCards.Read(versions.GetValueOrDefault(d.ServiceVersionId)))),
                _names = await IncidentEndpoints.Names(db, tickets.SelectMany(t => new[] { t.RequesterId, t.AssigneeId ?? Guid.Empty, t.CreatedById ?? Guid.Empty }), cancellationToken),
                _refs = await DirectoryRefs.LoadAsync(db, cancellationToken),
                _slas = await TicketSlaView.LoadAsync(db, staff ? ids : [], cancellationToken),
                _user = user,
                _staff = staff,
            };
        }

        public ServiceRequestResponse From(Ticket t)
        {
            var (service, card) = _services.TryGetValue(t.Id, out var s) ? s : (null!, new ServiceCard());
            var stage = StageOf(t.State);
            var steps = card.Steps.Select(x => new PublishedStep(LocalizedTextDto.From(x.Name), LocalizedTextDto.From(x.Description), x.Stage)).ToList();
            // The current step is the last step whose stage the request has reached.
            var current = -1;
            for (var i = 0; i < card.Steps.Count; i++)
            {
                if ((int)card.Steps[i].Stage <= (int)stage)
                {
                    current = i;
                }
            }

            var mine = t.RequesterId == _user.Account?.Id || t.CreatedById == _user.Account?.Id;
            IReadOnlyList<TicketState> next = _staff && _user.Has(Permissions.RequestsEdit)
                ? TicketWorkflow.NextStates(t.State)
                : t.State == TicketState.Resolved && t.RequesterId == _user.Account?.Id ? [TicketState.Reopened] : [];
            return new ServiceRequestResponse(
                t.Id, t.Number, t.Title, t.Description, t.State, t.Channel,
                new PersonResponse(t.RequesterId, _names.GetValueOrDefault(t.RequesterId)),
                t.CreatedById is { } by && by != t.RequesterId ? new PersonResponse(by, _names.GetValueOrDefault(by)) : null,
                _staff && t.AssigneeId is { } a ? new PersonResponse(a, _names.GetValueOrDefault(a)) : null,
                _staff ? _refs.Group(t.GroupId) : null,
                service is null ? null : new ServiceRefResponse(service.Id, service.Code, LocalizedTextDto.From(card.Name.HasAnyValue ? card.Name : service.Name)),
                steps, current, t.OnHoldReason, t.ResolutionNotes, next,
                _staff ? _slas.For(t.Id, DateTimeOffset.UtcNow) : [],
                $"\"{t.Version}\"", t.CreatedAt, t.UpdatedAt, t.ResolvedAt, t.ClosedAt);
        }

        private static ServiceStepStage StageOf(TicketState state) => state switch
        {
            TicketState.New => ServiceStepStage.Submitted,
            TicketState.Assigned => ServiceStepStage.Assigned,
            TicketState.InProgress or TicketState.Reopened => ServiceStepStage.InProgress,
            TicketState.OnHold => ServiceStepStage.OnHold,
            TicketState.Resolved => ServiceStepStage.Resolved,
            _ => ServiceStepStage.Closed,
        };
    }
}

/// <summary>A new service request.</summary>
internal sealed class CreateServiceRequestRequest
{
    /// <summary>The service.</summary>
    [Required]
    public Guid? ServiceId { get; init; }

    /// <summary>Who it is for; the caller when left out. Others only when the service allows the caller's role.</summary>
    public Guid? RequesterId { get; init; }

    /// <summary>A short title; the service's name when left out.</summary>
    [StringLength(Ticket.TitleMaxLength)]
    public string? Title { get; init; }

    /// <summary>More details.</summary>
    [StringLength(Ticket.DescriptionMaxLength)]
    public string? Description { get; init; }

    /// <summary>The form version filled (from the request form).</summary>
    public Guid? FormVersionId { get; init; }

    /// <summary>Answers by field key.</summary>
    public JsonObject? FormAnswers { get; init; }

    /// <summary>Required documents: document key → uploaded file ids.</summary>
    public Dictionary<string, List<Guid>>? Documents { get; init; }
}

/// <summary>Filters of the request list.</summary>
internal sealed class ServiceRequestListQuery
{
    /// <summary>Only the caller's own (as requester).</summary>
    public bool? Mine { get; init; }

    /// <summary>Only open ones.</summary>
    public bool? Open { get; init; }

    /// <summary>Only in this state.</summary>
    public TicketState? State { get; init; }

    /// <summary>Only of this service.</summary>
    public Guid? ServiceId { get; init; }

    /// <summary>A request number, for example REQ-000123.</summary>
    [StringLength(Ticket.NumberMaxLength)]
    public string? Number { get; init; }

    /// <summary>me, none or a user id (staff).</summary>
    [StringLength(40)]
    public string? Assignee { get; init; }

    /// <summary>mine, none or a group id (staff).</summary>
    [StringLength(40)]
    public string? Group { get; init; }
}

/// <summary>A service a request refers to.</summary>
internal sealed record ServiceRefResponse(Guid Id, string Code, LocalizedTextDto Name);

/// <summary>A file of a request and the required document it belongs to.</summary>
internal sealed record ServiceRequestAttachmentResponse(
    Guid Id, string FileName, string ContentType, long Size, AttachmentScanStatus ScanStatus, string? DocumentKey, string? FormFieldKey, DateTimeOffset UploadedAt);

/// <summary>A service request. Requesters do not see the assignee, group, SLA clocks or staff states.</summary>
internal sealed record ServiceRequestResponse(
    Guid Id, string Number, string Title, string? Description, TicketState State, TicketChannel Channel,
    PersonResponse Requester, PersonResponse? RequestedBy, PersonResponse? Assignee, NamedRef? Group, ServiceRefResponse? Service,
    IReadOnlyList<PublishedStep> Steps, int CurrentStep, string? OnHoldReason, string? ResolutionNotes, IReadOnlyList<TicketState> NextStates,
    IReadOnlyList<SlaClockResponse> Sla, string Etag, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, DateTimeOffset? ResolvedAt, DateTimeOffset? ClosedAt);
