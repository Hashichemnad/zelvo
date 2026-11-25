using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Zelvo.Api.Conventions;
using Zelvo.Api.Identity;
using Zelvo.Api.Integrations;
using Zelvo.Api.Settings;
using Zelvo.Api.Tenancy;
using Zelvo.Data;
using Zelvo.Data.Auditing;
using Zelvo.Data.Identity;
using Zelvo.Data.Privacy;
using Zelvo.Data.Tenancy;
using Zelvo.Notifications;

namespace Zelvo.Api.Privacy;

/// <summary>The person a request is about, as privacy officers see them.</summary>
internal sealed record SubjectSummary(Guid Id, string? Name, string? Email, bool Erased, bool Staff);

/// <summary>What Zelvo holds about a person, in counts, so the officer knows what a copy or an erasure covers.</summary>
internal sealed record HoldingsResponse(int Tickets, int OpenTickets, int Messages, int Files, int Chats, int Notifications, int Payments, int Consents, string ErasureRefusal);

/// <summary>A data subject request.</summary>
internal sealed record DataSubjectRequestResponse(
    Guid Id, string Number, DataSubjectRequestType Type, DataSubjectRequestState State, DataSubjectRequestChannel Channel, string? Details,
    DateTimeOffset ReceivedAt, DateTimeOffset DueAt, bool Overdue, DateTimeOffset? ClosedAt, string? Resolution, DateTimeOffset? ExportedAt, DateTimeOffset? ErasedAt,
    SubjectSummary? Subject, string? IdentityCheck, string? Handler, HoldingsResponse? Holdings);

/// <summary>A request the person makes for themselves.</summary>
internal sealed record MyRequestCreate(DataSubjectRequestType? Type, string? Details);

/// <summary>A request logged by a privacy officer for a person, after checking who they are.</summary>
internal sealed record OfficerRequestCreate(Guid? SubjectId, DataSubjectRequestType? Type, string? Details, string? IdentityCheck);

/// <summary>Completing or refusing a request: what was done or why not, shown to the person.</summary>
internal sealed record ResolveRequest(string? Resolution);

/// <summary>Erasing the person a request is about: the request's number typed again, and a note for the person's file.</summary>
internal sealed record EraseRequest(string? ConfirmNumber, string? Resolution);

/// <summary>
/// Data subject requests (GOV-5, task F5): people ask under Account → Privacy, or privacy officers
/// log requests that came by letter, phone or at the counter. Officers (<c>privacy.requests</c>)
/// download a copy of the person's data, complete or refuse with a reason, and carry out an
/// erasure. The person is told by notification at each step (except after an erasure: their
/// address is gone). Every step is audited.
/// </summary>
internal static class DataSubjectRequestEndpoints
{
    public static RouteGroupBuilder MapDataSubjectRequestEndpoints(this RouteGroupBuilder group)
    {
        var me = group.MapGroup("/me/privacy/requests").WithTags("Privacy").RequireTenant().RequirePerson();
        me.MapGet("/", ListMineAsync).WithName("ListMyPrivacyRequests")
            .WithSummary("The signed-in person's data subject requests, newest first.")
            .RequireAuthorization().Produces<IReadOnlyList<DataSubjectRequestResponse>>();
        me.MapPost("/", CreateMineAsync).WithName("CreateMyPrivacyRequest")
            .WithSummary("Ask for a copy of your data, its correction or erasure, or to restrict or stop its processing. Numbered DSR-000123, due after the organisation's period; audited.")
            .RequireAuthorization().Produces<DataSubjectRequestResponse>(StatusCodes.Status201Created).ProducesValidationProblem();
        me.MapPost("/{id:guid}/withdraw", WithdrawAsync).WithName("WithdrawMyPrivacyRequest")
            .WithSummary("Withdraw your own request before it is done. Audited.")
            .RequireAuthorization().Produces<DataSubjectRequestResponse>();

        var officers = group.MapGroup("/privacy/requests").WithTags("Privacy").RequireTenant();
        officers.MapGet("/", ListAsync).WithName("ListPrivacyRequests")
            .WithSummary("Data subject requests, soonest due first, by state, type and whether they are overdue. Needs privacy.requests.")
            .RequirePermission(Permissions.PrivacyRequests).Produces<PageResult<DataSubjectRequestResponse>>();
        officers.MapPost("/", CreateAsync).WithName("CreatePrivacyRequest")
            .WithSummary("Log a request for a person (letter, phone, counter), saying how their identity was checked. Needs privacy.requests; audited.")
            .RequirePermission(Permissions.PrivacyRequests).Produces<DataSubjectRequestResponse>(StatusCodes.Status201Created).ProducesValidationProblem();
        officers.MapGet("/{id:guid}", GetAsync).WithName("GetPrivacyRequest")
            .WithSummary("A request with the person and what Zelvo holds about them (counts), and whether they can be erased. Needs privacy.requests; audited as a view of personal data.")
            .RequirePermission(Permissions.PrivacyRequests).Produces<DataSubjectRequestResponse>();
        officers.MapPost("/{id:guid}/start", StartAsync).WithName("StartPrivacyRequest")
            .WithSummary("Start work on a request; the caller becomes its handler. Needs privacy.requests; audited.")
            .RequirePermission(Permissions.PrivacyRequests).Produces<DataSubjectRequestResponse>();
        officers.MapPost("/{id:guid}/complete", CompleteAsync).WithName("CompletePrivacyRequest")
            .WithSummary("Complete a request with what was done; the person is told. Erasure requests are completed by erasing. Needs privacy.requests; audited.")
            .RequirePermission(Permissions.PrivacyRequests).Produces<DataSubjectRequestResponse>().ProducesValidationProblem();
        officers.MapPost("/{id:guid}/reject", RejectAsync).WithName("RejectPrivacyRequest")
            .WithSummary("Refuse a request with the reason (for example a legal obligation to keep the data); the person is told. Needs privacy.requests; audited.")
            .RequirePermission(Permissions.PrivacyRequests).Produces<DataSubjectRequestResponse>().ProducesValidationProblem();
        officers.MapGet("/{id:guid}/export", ExportAsync).WithName("ExportPrivacyRequestData")
            .WithSummary("A copy of the person's data (ZIP: personal-data.json and their files) to give them. Needs privacy.requests; audited.")
            .RequirePermission(Permissions.PrivacyRequests).Produces<byte[]>(contentType: "application/zip");
        officers.MapPost("/{id:guid}/erase", EraseAsync).WithName("ErasePrivacyRequestSubject")
            .WithSummary("Erase the person of an erasure request (the request's number typed again): their tickets anonymised, messages, files, chats and notifications removed, account emptied and disabled, Keycloak user deleted; the audit log and consent history are kept. Not for yourself, the last administrator or people with open tickets. Completes the request. Needs privacy.requests; audited.")
            .RequirePermission(Permissions.PrivacyRequests).Produces<DataSubjectRequestResponse>().ProducesValidationProblem();
        return group;
    }

    // ---- the person ----

    internal static async Task<IResult> ListMineAsync(ZelvoDbContext db, CurrentUser user, CancellationToken cancellationToken)
    {
        var id = user.Account!.Id;
        var rows = await db.DataSubjectRequests.AsNoTracking().Where(r => r.SubjectId == id).OrderByDescending(r => r.ReceivedAt).ToListAsync(cancellationToken);
        return TypedResults.Ok(rows.Select(r => Response(r, null, null, null)).ToList());
    }

    internal static async Task<IResult> CreateMineAsync(MyRequestCreate request, ZelvoDbContext db, CurrentUser user, TenantSettingsStore settings, ITenantContext tenant,
        PendingNotifications notifications, IAuditLog audit, CancellationToken cancellationToken)
    {
        if (request.Type is not { } type || !Enum.IsDefined(type))
        {
            return UserEndpoints.Validation("type", "Choose what you ask for.");
        }

        if (request.Details?.Length > DataSubjectRequest.TextMaxLength)
        {
            return UserEndpoints.Validation("details", $"Use at most {DataSubjectRequest.TextMaxLength} characters.");
        }

        var id = user.Account!.Id;
        if (await db.DataSubjectRequests.CountAsync(r => r.SubjectId == id && (r.State == DataSubjectRequestState.Received || r.State == DataSubjectRequestState.InProgress), cancellationToken) >= 5)
        {
            return ApiProblem.Create(StatusCodes.Status409Conflict, "too-many-requests-open", "You already have five open requests. Please wait for them to be answered.");
        }

        var created = await CreateCoreAsync(db, settings, tenant, notifications, audit, id, type, request.Details, DataSubjectRequestChannel.Portal, id, null, cancellationToken);
        return TypedResults.Created($"/api/v1/me/privacy/requests/{created.Id}", Response(created, null, null, null));
    }

    internal static async Task<IResult> WithdrawAsync(Guid id, ZelvoDbContext db, CurrentUser user, IAuditLog audit, CancellationToken cancellationToken)
    {
        var me = user.Account!.Id;
        var request = await db.DataSubjectRequests.SingleOrDefaultAsync(r => r.Id == id && r.SubjectId == me, cancellationToken);
        if (request is null)
        {
            return PrivacyEndpoints.NotFound();
        }

        if (request.State is not (DataSubjectRequestState.Received or DataSubjectRequestState.InProgress))
        {
            return ApiProblem.Create(StatusCodes.Status409Conflict, "request-closed", "This request is already closed.");
        }

        request.State = DataSubjectRequestState.Withdrawn;
        request.ClosedAt = DateTimeOffset.UtcNow;
        request.UpdatedAt = DateTimeOffset.UtcNow;
        audit.Add(AuditCategory.Change, "privacy.request.withdrawn", "privacy-request", request.Id.ToString());
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Ok(Response(request, null, null, null));
    }

    // ---- privacy officers ----

    internal static async Task<IResult> ListAsync(
        [AsParameters] PageQuery page, DataSubjectRequestState? state, DataSubjectRequestType? type, bool? overdue, bool? open, ZelvoDbContext db, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var query = db.DataSubjectRequests.AsNoTracking();
        if (state is { } s)
        {
            query = query.Where(r => r.State == s);
        }

        if (type is { } t)
        {
            query = query.Where(r => r.Type == t);
        }

        if (open == true || overdue == true)
        {
            query = query.Where(r => r.State == DataSubjectRequestState.Received || r.State == DataSubjectRequestState.InProgress);
        }

        if (overdue == true)
        {
            query = query.Where(r => r.DueAt < now);
        }

        var ordered = query.OrderBy(r => r.State == DataSubjectRequestState.Received || r.State == DataSubjectRequestState.InProgress ? 0 : 1)
            .ThenBy(r => r.DueAt).ThenByDescending(r => r.Sequence);
        var total = await ordered.LongCountAsync(cancellationToken);
        var rows = await ordered.Skip(page.Skip).Take(page.Size).ToListAsync(cancellationToken);
        var people = await PeopleAsync(db, rows.Select(r => r.SubjectId).Concat(rows.Where(r => r.HandlerId != null).Select(r => r.HandlerId!.Value)), cancellationToken);
        return TypedResults.Ok(new PageResult<DataSubjectRequestResponse>(
            [.. rows.Select(r => Response(r, people.GetValueOrDefault(r.SubjectId), r.HandlerId is { } h ? people.GetValueOrDefault(h)?.Name : null, null) with { Details = null })],
            page.Number, page.Size, total));
    }

    internal static async Task<IResult> CreateAsync(OfficerRequestCreate request, ZelvoDbContext db, CurrentUser user, TenantSettingsStore settings, ITenantContext tenant,
        PendingNotifications notifications, IAuditLog audit, CancellationToken cancellationToken)
    {
        if (request.Type is not { } type || !Enum.IsDefined(type))
        {
            return UserEndpoints.Validation("type", "Choose the right the person uses.");
        }

        if (request.SubjectId is not { } subjectId || !await db.Users.AnyAsync(u => u.Id == subjectId && u.ErasedAt == null && !u.Subject.StartsWith("system:"), cancellationToken))
        {
            return UserEndpoints.Validation("subjectId", "Choose the person the request is about.");
        }

        if (string.IsNullOrWhiteSpace(request.IdentityCheck) || request.IdentityCheck.Length > DataSubjectRequest.TextMaxLength)
        {
            return UserEndpoints.Validation("identityCheck", "Say how the person's identity was checked (for example Emirates ID seen at the counter).");
        }

        if (request.Details?.Length > DataSubjectRequest.TextMaxLength)
        {
            return UserEndpoints.Validation("details", $"Use at most {DataSubjectRequest.TextMaxLength} characters.");
        }

        var created = await CreateCoreAsync(db, settings, tenant, notifications, audit, subjectId, type, request.Details, DataSubjectRequestChannel.Staff,
            user.Account?.Id, request.IdentityCheck.Trim(), cancellationToken);
        return TypedResults.Created($"/api/v1/privacy/requests/{created.Id}", await DetailAsync(db, created, user, cancellationToken));
    }

    internal static async Task<IResult> GetAsync(Guid id, ZelvoDbContext db, CurrentUser user, IAuditLog audit, CancellationToken cancellationToken)
    {
        var request = await db.DataSubjectRequests.AsNoTracking().SingleOrDefaultAsync(r => r.Id == id, cancellationToken);
        if (request is null)
        {
            return PrivacyEndpoints.NotFound();
        }

        audit.Add(AuditCategory.View, "privacy.request.viewed", "privacy-request", request.Id.ToString());
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Ok(await DetailAsync(db, request, user, cancellationToken));
    }

    internal static async Task<IResult> StartAsync(Guid id, ZelvoDbContext db, CurrentUser user, IAuditLog audit, CancellationToken cancellationToken)
    {
        var request = await db.DataSubjectRequests.SingleOrDefaultAsync(r => r.Id == id, cancellationToken);
        if (request is null)
        {
            return PrivacyEndpoints.NotFound();
        }

        if (request.State is not (DataSubjectRequestState.Received or DataSubjectRequestState.InProgress))
        {
            return ApiProblem.Create(StatusCodes.Status409Conflict, "request-closed", "This request is already closed.");
        }

        request.State = DataSubjectRequestState.InProgress;
        request.HandlerId = user.Account?.Id;
        request.UpdatedAt = DateTimeOffset.UtcNow;
        audit.Add(AuditCategory.Change, "privacy.request.started", "privacy-request", request.Id.ToString());
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Ok(await DetailAsync(db, request, user, cancellationToken));
    }

    internal static Task<IResult> CompleteAsync(Guid id, ResolveRequest body, ZelvoDbContext db, CurrentUser user, PendingNotifications notifications, IAuditLog audit,
        CancellationToken cancellationToken) =>
        CloseAsync(id, body, DataSubjectRequestState.Completed, db, user, notifications, audit, cancellationToken);

    internal static Task<IResult> RejectAsync(Guid id, ResolveRequest body, ZelvoDbContext db, CurrentUser user, PendingNotifications notifications, IAuditLog audit,
        CancellationToken cancellationToken) =>
        CloseAsync(id, body, DataSubjectRequestState.Rejected, db, user, notifications, audit, cancellationToken);

    private static async Task<IResult> CloseAsync(Guid id, ResolveRequest body, DataSubjectRequestState state, ZelvoDbContext db, CurrentUser user,
        PendingNotifications notifications, IAuditLog audit, CancellationToken cancellationToken)
    {
        var request = await db.DataSubjectRequests.SingleOrDefaultAsync(r => r.Id == id, cancellationToken);
        if (request is null)
        {
            return PrivacyEndpoints.NotFound();
        }

        if (request.State is not (DataSubjectRequestState.Received or DataSubjectRequestState.InProgress))
        {
            return ApiProblem.Create(StatusCodes.Status409Conflict, "request-closed", "This request is already closed.");
        }

        if (state == DataSubjectRequestState.Completed && request.Type == DataSubjectRequestType.Erasure)
        {
            return ApiProblem.Create(StatusCodes.Status409Conflict, "erase-to-complete", "An erasure request is completed by erasing the person, or refused with a reason.");
        }

        var resolution = body.Resolution?.Trim();
        if (string.IsNullOrEmpty(resolution) || resolution.Length > DataSubjectRequest.TextMaxLength)
        {
            return UserEndpoints.Validation("resolution", state == DataSubjectRequestState.Rejected
                ? "Give the reason the request is refused; the person is told."
                : "Say what was done; the person is told.");
        }

        request.State = state;
        request.Resolution = resolution;
        request.HandlerId ??= user.Account?.Id;
        request.ClosedAt = DateTimeOffset.UtcNow;
        request.UpdatedAt = DateTimeOffset.UtcNow;
        Notify(notifications, state == DataSubjectRequestState.Completed ? NotificationEvents.PrivacyRequestCompleted : NotificationEvents.PrivacyRequestRejected,
            [request.SubjectId], request, "/account/privacy");
        audit.Add(AuditCategory.Change, state == DataSubjectRequestState.Completed ? "privacy.request.completed" : "privacy.request.rejected", "privacy-request", request.Id.ToString());
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Ok(await DetailAsync(db, request, user, cancellationToken));
    }

    internal static async Task<IResult> ExportAsync(Guid id, ZelvoDbContext db, PersonalDataExport export, IAuditLog audit, CancellationToken cancellationToken)
    {
        var request = await db.DataSubjectRequests.SingleOrDefaultAsync(r => r.Id == id, cancellationToken);
        if (request is null)
        {
            return PrivacyEndpoints.NotFound();
        }

        if (await db.Users.AnyAsync(u => u.Id == request.SubjectId && u.ErasedAt != null, cancellationToken))
        {
            return ApiProblem.Create(StatusCodes.Status409Conflict, "subject-erased", "This person's data was erased.");
        }

        request.ExportedAt = DateTimeOffset.UtcNow;
        request.UpdatedAt = DateTimeOffset.UtcNow;
        audit.Add(AuditCategory.View, "privacy.export.downloaded", "user", request.SubjectId.ToString(),
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["by"] = "officer", ["requestId"] = request.Id });
        await db.SaveChangesAsync(cancellationToken);
        return await PrivacyEndpoints.ZipAsync(export, request.SubjectId, request.Number, cancellationToken);
    }

    internal static async Task<IResult> EraseAsync(Guid id, EraseRequest body, ZelvoDbContext db, CurrentUser user, PersonalDataEraser eraser, IAuditLog audit,
        CancellationToken cancellationToken)
    {
        var request = await db.DataSubjectRequests.SingleOrDefaultAsync(r => r.Id == id, cancellationToken);
        if (request is null)
        {
            return PrivacyEndpoints.NotFound();
        }

        if (request.Type != DataSubjectRequestType.Erasure)
        {
            return ApiProblem.Create(StatusCodes.Status409Conflict, "not-an-erasure-request", "Only an erasure request erases the person.");
        }

        if (request.State is not (DataSubjectRequestState.Received or DataSubjectRequestState.InProgress))
        {
            return ApiProblem.Create(StatusCodes.Status409Conflict, "request-closed", "This request is already closed.");
        }

        if (!string.Equals(body.ConfirmNumber?.Trim(), request.Number, StringComparison.OrdinalIgnoreCase))
        {
            return UserEndpoints.Validation("confirmNumber", $"Type the request's number, {request.Number}, to confirm the erasure.");
        }

        var resolution = body.Resolution?.Trim();
        if (resolution?.Length > DataSubjectRequest.TextMaxLength)
        {
            return UserEndpoints.Validation("resolution", $"Use at most {DataSubjectRequest.TextMaxLength} characters.");
        }

        var person = await db.Users.AsNoTracking().SingleAsync(u => u.Id == request.SubjectId, cancellationToken);
        switch (await PersonalDataEraser.CheckAsync(db, person, user.Account?.Id, cancellationToken))
        {
            case ErasureRefusal.AlreadyErased:
                return ApiProblem.Create(StatusCodes.Status409Conflict, "subject-erased", "This person's data was already erased.");
            case ErasureRefusal.Self:
                return ApiProblem.Create(StatusCodes.Status409Conflict, "erase-self", "Another privacy officer must erase your own data.");
            case ErasureRefusal.LastAdministrator:
                return ApiProblem.Create(StatusCodes.Status409Conflict, "last-administrator", "This person is the last tenant administrator; give the role to someone else first.");
            case ErasureRefusal.OpenTickets:
                return ApiProblem.Create(StatusCodes.Status409Conflict, "open-tickets", "The person has tickets that are not resolved or closed yet. Resolve or close them first.");
        }

        var counts = await eraser.ErasePersonAsync(person, "request", request.Id, cancellationToken);
        // The erasure cleared the request's details in the database; continue from that row.
        await db.Entry(request).ReloadAsync(cancellationToken);
        request.State = DataSubjectRequestState.Completed;
        request.ErasedAt = DateTimeOffset.UtcNow;
        request.ClosedAt = request.ErasedAt;
        request.HandlerId ??= user.Account?.Id;
        request.Resolution = string.IsNullOrEmpty(resolution) ? null : resolution;
        request.Details = null;
        request.UpdatedAt = DateTimeOffset.UtcNow;
        audit.Add(AuditCategory.Change, "privacy.request.completed", "privacy-request", request.Id.ToString(), counts.ToMetadata());
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Ok(await DetailAsync(db, request, user, cancellationToken));
    }

    // ---- helpers ----

    private static async Task<DataSubjectRequest> CreateCoreAsync(ZelvoDbContext db, TenantSettingsStore settings, ITenantContext tenant, PendingNotifications notifications,
        IAuditLog audit, Guid subjectId, DataSubjectRequestType type, string? details, DataSubjectRequestChannel channel, Guid? submittedBy, string? identityCheck,
        CancellationToken cancellationToken)
    {
        var days = await settings.GetAsync(PrivacySettings.RequestDueDays, cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        // Numbers in order, never reused: one writer at a time per tenant.
        var tenantKey = tenant.TenantId!.Value.ToString();
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtext('zelvo.privacy-requests'), hashtext({tenantKey}))", cancellationToken);
        var sequence = (await db.DataSubjectRequests.MaxAsync(r => (long?)r.Sequence, cancellationToken) ?? 0) + 1;
        var now = DateTimeOffset.UtcNow;
        var request = new DataSubjectRequest
        {
            Number = "DSR-" + sequence.ToString("000000", CultureInfo.InvariantCulture),
            Sequence = sequence,
            Type = type,
            SubjectId = subjectId,
            Channel = channel,
            Details = string.IsNullOrWhiteSpace(details) ? null : details.Trim(),
            IdentityCheck = identityCheck,
            SubmittedById = submittedBy,
            ReceivedAt = now,
            DueAt = now.AddDays(days),
        };
        db.DataSubjectRequests.Add(request);
        Notify(notifications, NotificationEvents.PrivacyRequestReceived, [subjectId], request, "/account/privacy");
        var officers = await db.Users.AsNoTracking()
            .Where(u => u.Status == UserStatus.Active && u.ErasedAt == null && u.Id != submittedBy
                && u.Roles.Any(r => r.Role!.Permissions.Any(p => p.Permission == Permissions.PrivacyRequests)))
            .Select(u => u.Id).Take(50).ToListAsync(cancellationToken);
        if (officers.Count > 0)
        {
            Notify(notifications, NotificationEvents.PrivacyRequestNew, officers, request, $"/privacy-requests/{request.Id}");
        }

        audit.Add(AuditCategory.Create, "privacy.request.created", "privacy-request", request.Id.ToString(),
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["type"] = type.ToString(), ["channel"] = channel.ToString(), ["subject"] = subjectId });
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return request;
    }

    private static void Notify(PendingNotifications notifications, string eventType, IReadOnlyCollection<Guid> recipients, DataSubjectRequest request, string link)
    {
        var values = new Dictionary<string, PlaceholderValue>(StringComparer.Ordinal)
        {
            [Placeholders.PrivacyRequestNumber] = PlaceholderValue.OfText(request.Number),
            [Placeholders.PrivacyRequestType] = PlaceholderValue.OfTerm($"privacy.type.{request.Type}"),
            [Placeholders.PrivacyRequestDueAt] = PlaceholderValue.OfDate(request.DueAt),
            [Placeholders.PrivacyRequestResolution] = PlaceholderValue.OfText(request.Resolution ?? string.Empty),
        };
        notifications.Add(new NotificationRequest(eventType, recipients, values, link));
    }

    private static async Task<Dictionary<Guid, SubjectSummary>> PeopleAsync(ZelvoDbContext db, IEnumerable<Guid> ids, CancellationToken cancellationToken)
    {
        var list = ids.Distinct().ToList();
        return await db.Users.AsNoTracking().Where(u => list.Contains(u.Id))
            .Select(u => new SubjectSummary(u.Id, u.DisplayName, u.Email, u.ErasedAt != null, u.Roles.Any(r => r.Role!.RequiresMfa)))
            .ToDictionaryAsync(u => u.Id, cancellationToken);
    }

    private static async Task<DataSubjectRequestResponse> DetailAsync(ZelvoDbContext db, DataSubjectRequest request, CurrentUser user, CancellationToken cancellationToken)
    {
        var people = await PeopleAsync(db, request.HandlerId is { } h ? [request.SubjectId, h] : [request.SubjectId], cancellationToken);
        var subject = people.GetValueOrDefault(request.SubjectId);
        HoldingsResponse? holdings = null;
        if (subject is { Erased: false })
        {
            var id = request.SubjectId;
            var ticketIds = db.Tickets.Where(t => t.RequesterId == id).Select(t => t.Id);
            var person = await db.Users.AsNoTracking().SingleAsync(u => u.Id == id, cancellationToken);
            var refusal = await PersonalDataEraser.CheckAsync(db, person, user.Account?.Id, cancellationToken);
            holdings = new HoldingsResponse(
                await ticketIds.CountAsync(cancellationToken),
                await db.Tickets.CountAsync(t => t.RequesterId == id && t.State != Data.Tickets.TicketState.Resolved && t.State != Data.Tickets.TicketState.Closed, cancellationToken),
                await db.TicketComments.CountAsync(c => c.AuthorId == id && !c.ByStaff && c.ErasedAt == null, cancellationToken),
                await db.TicketAttachments.CountAsync(a => a.UploadedById == id, cancellationToken),
                await db.ChatSessions.CountAsync(s => s.VisitorUserId == id, cancellationToken),
                await db.Notifications.CountAsync(n => n.RecipientId == id, cancellationToken),
                await db.Payments.CountAsync(p => p.TicketId != null && ticketIds.Contains(p.TicketId.Value), cancellationToken),
                await db.ConsentRecords.CountAsync(r => r.UserId == id, cancellationToken),
                refusal.ToString());
        }

        return Response(request, subject, request.HandlerId is { } handler ? people.GetValueOrDefault(handler)?.Name : null, holdings);
    }

    private static DataSubjectRequestResponse Response(DataSubjectRequest r, SubjectSummary? subject, string? handler, HoldingsResponse? holdings) =>
        new(r.Id, r.Number, r.Type, r.State, r.Channel, r.Details, r.ReceivedAt, r.DueAt,
            r.State is DataSubjectRequestState.Received or DataSubjectRequestState.InProgress && r.DueAt < DateTimeOffset.UtcNow,
            r.ClosedAt, r.Resolution, r.ExportedAt, r.ErasedAt, subject, subject is null ? null : r.IdentityCheck, handler, holdings);
}
