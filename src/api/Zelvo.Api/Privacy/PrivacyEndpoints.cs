using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Zelvo.Api.Conventions;
using Zelvo.Api.Identity;
using Zelvo.Api.Integrations;
using Zelvo.Api.Settings;
using Zelvo.Api.Tenancy;
using Zelvo.Data;
using Zelvo.Data.Auditing;
using Zelvo.Data.Identity;
using Zelvo.Data.Localization;
using Zelvo.Data.Privacy;
using Zelvo.Data.Tickets;
using Zelvo.Notifications;

namespace Zelvo.Api.Privacy;

/// <summary>The published privacy notice.</summary>
internal sealed record NoticeResponse(Guid Id, int Version, LocalizedTextDto Title, LocalizedTextDto Body, PrivacyNoticeState State,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, DateTimeOffset? PublishedAt)
{
    public static NoticeResponse From(PrivacyNotice n) =>
        new(n.Id, n.Version, LocalizedTextDto.From(n.Title), LocalizedTextDto.From(n.Body), n.State, n.CreatedAt, n.UpdatedAt, n.PublishedAt);
}

/// <summary>Bilingual text in a privacy request body; lengths are checked by each endpoint.</summary>
internal sealed record PrivacyTextInput(string? En, string? Ar)
{
    public LocalizedText ToText() => new(string.IsNullOrWhiteSpace(En) ? null : En.Trim(), string.IsNullOrWhiteSpace(Ar) ? null : Ar.Trim());
}

/// <summary>A privacy notice draft.</summary>
internal sealed record NoticeRequest(PrivacyTextInput? Title, PrivacyTextInput? Body);

/// <summary>An optional purpose.</summary>
internal sealed record PurposeResponse(Guid Id, string Key, LocalizedTextDto Name, LocalizedTextDto Description, bool Active, int Position)
{
    public static PurposeResponse From(ConsentPurpose p) => new(p.Id, p.Key, LocalizedTextDto.From(p.Name), LocalizedTextDto.From(p.Description), p.Active, p.Position);
}

/// <summary>A new or changed optional purpose. The key is set once.</summary>
internal sealed record PurposeRequest(string? Key, PrivacyTextInput? Name, PrivacyTextInput? Description, bool? Active, int? Position);

/// <summary>A purpose with the person's choice.</summary>
internal sealed record MyPurposeResponse(Guid Id, string Key, LocalizedTextDto Name, LocalizedTextDto Description, bool Granted, DateTimeOffset? ChangedAt);

/// <summary>An entry of the consent history.</summary>
internal sealed record ConsentRecordResponse(Guid Id, ConsentAction Action, int? NoticeVersion, string? Purpose, ConsentChannel Channel, bool RecordedByStaff, DateTimeOffset At);

/// <summary>The person's privacy: the notice to accept, optional purposes and the history.</summary>
internal sealed record MyPrivacyResponse(
    NoticeSummary? Notice, bool NoticeAccepted, DateTimeOffset? NoticeAcceptedAt, IReadOnlyList<MyPurposeResponse> Purposes, IReadOnlyList<ConsentRecordResponse> History);

/// <summary>Which notice is current.</summary>
internal sealed record NoticeSummary(Guid Id, int Version, LocalizedTextDto Title, DateTimeOffset? PublishedAt);

/// <summary>Accepting a notice.</summary>
internal sealed record AcceptNoticeRequest(Guid? NoticeId);

/// <summary>Granting or withdrawing a purpose.</summary>
internal sealed record PurposeChoiceRequest(bool? Granted);

/// <summary>A person's consent as integrations read it.</summary>
internal sealed record ConsentStateResponse(Guid UserId, int? AcceptedNoticeVersion, bool AcceptedCurrentNotice, IReadOnlyList<ConsentPurposeState> Purposes);

/// <summary>One purpose's state for a person.</summary>
internal sealed record ConsentPurposeState(string Key, bool Granted, DateTimeOffset? ChangedAt);

/// <summary>Settings → Privacy.</summary>
internal sealed record PrivacySettingsResponse(int RequestDueDays, IReadOnlyDictionary<TicketType, int> TicketRetentionDays, int? InactiveAccountDays);

/// <summary>Changing Settings → Privacy; values left out are kept, an empty map or null clears periods.</summary>
internal sealed record PrivacySettingsRequest(int? RequestDueDays, IReadOnlyDictionary<TicketType, int>? TicketRetentionDays, int? InactiveAccountDays, bool? ClearInactiveAccountDays);

/// <summary>How many records the retention periods would change now.</summary>
internal sealed record RetentionPreviewResponse(IReadOnlyDictionary<TicketType, int> Tickets, int Accounts);

/// <summary>
/// Privacy and the UAE Personal Data Protection Law (GOV-5, task F5): the privacy notice and its
/// acceptance, optional consent purposes, Settings → Privacy with retention periods, and each
/// person's Account → Privacy. Data subject requests are in <see cref="DataSubjectRequestEndpoints"/>.
/// </summary>
internal static partial class PrivacyEndpoints
{
    public static RouteGroupBuilder MapPrivacyEndpoints(this RouteGroupBuilder group)
    {
        var settings = group.MapGroup("/settings/privacy").WithTags("Privacy").RequireTenant();
        settings.MapGet("/", GetSettingsAsync).WithName("GetPrivacySettings")
            .WithSummary("Retention periods per record type, the period of inactive requester accounts and the days to answer data subject requests. Needs privacy.manage.")
            .RequirePermission(Permissions.PrivacyManage).Produces<PrivacySettingsResponse>();
        settings.MapPut("/", PutSettingsAsync).WithName("UpdatePrivacySettings")
            .WithSummary("Change the retention periods (30 days to 50 years; anonymised by the privacy jobs when they end) and the days to answer requests (1–90). Needs privacy.manage; audited.")
            .RequirePermission(Permissions.PrivacyManage).Produces<PrivacySettingsResponse>().ProducesValidationProblem();
        settings.MapGet("/retention-preview", PreviewAsync).WithName("PreviewPrivacyRetention")
            .WithSummary("How many closed tickets per record type and requester accounts the given periods (or the saved ones) would anonymise today. Needs privacy.manage.")
            .RequirePermission(Permissions.PrivacyManage).Produces<RetentionPreviewResponse>();

        var notices = group.MapGroup("/privacy").WithTags("Privacy").RequireTenant();
        notices.MapGet("/notice", GetPublishedAsync).WithName("GetPrivacyNotice")
            .WithSummary("The published privacy notice in English and Arabic (Markdown), or 404 when the tenant has none. Open to visitors.")
            .AllowAnonymousAccess().Produces<NoticeResponse>();
        notices.MapGet("/notices", ListNoticesAsync).WithName("ListPrivacyNotices")
            .WithSummary("Every version of the privacy notice, newest first: the draft, the published version and those it replaced. Needs privacy.manage.")
            .RequirePermission(Permissions.PrivacyManage).Produces<IReadOnlyList<NoticeResponse>>();
        notices.MapPut("/notices/draft", SaveDraftAsync).WithName("SavePrivacyNoticeDraft")
            .WithSummary("Write the next version as a draft (title and Markdown body in both languages; created from the published version when there is no draft). Needs privacy.manage; audited by length.")
            .RequirePermission(Permissions.PrivacyManage).Produces<NoticeResponse>().ProducesValidationProblem();
        notices.MapDelete("/notices/draft", DeleteDraftAsync).WithName("DeletePrivacyNoticeDraft")
            .WithSummary("Discard the draft. Needs privacy.manage; audited.")
            .RequirePermission(Permissions.PrivacyManage).Produces(StatusCodes.Status204NoContent);
        notices.MapPost("/notices/draft/publish", PublishAsync).WithName("PublishPrivacyNotice")
            .WithSummary("Publish the draft: it becomes the notice everyone sees, and signed-in people accept it at their next visit. Both languages are needed. Needs privacy.manage; audited.")
            .RequirePermission(Permissions.PrivacyManage).Produces<NoticeResponse>().ProducesValidationProblem();

        notices.MapGet("/purposes", ListPurposesAsync).WithName("ListConsentPurposes")
            .WithSummary("Optional purposes people agree to or not, with inactive ones. Needs privacy.manage or privacy.consents.read.")
            .RequireAuthorization().Produces<IReadOnlyList<PurposeResponse>>();
        notices.MapPost("/purposes", CreatePurposeAsync).WithName("CreateConsentPurpose")
            .WithSummary("Add an optional purpose (key, bilingual name and description). Needs privacy.manage; audited.")
            .RequirePermission(Permissions.PrivacyManage).Produces<PurposeResponse>(StatusCodes.Status201Created).ProducesValidationProblem();
        notices.MapPut("/purposes/{id:guid}", UpdatePurposeAsync).WithName("UpdateConsentPurpose")
            .WithSummary("Change a purpose's wording, order or whether people are asked (inactive purposes keep their history). Needs privacy.manage; audited.")
            .RequirePermission(Permissions.PrivacyManage).Produces<PurposeResponse>().ProducesValidationProblem();
        notices.MapGet("/consents/{userId:guid}", ConsentStateAsync).WithName("GetConsentState")
            .WithSummary("A person's consent: the notice version they accepted and each purpose granted or not. Needs privacy.consents.read or privacy.requests.")
            .RequireAuthorization().Produces<ConsentStateResponse>();
        notices.MapGet("/purposes/{key}/granted", GrantedAsync).WithName("ListConsentGranted")
            .WithSummary("People who currently agree to a purpose (ids, paged), for example for a survey tool. Needs privacy.consents.read; audited.")
            .RequirePermission(Permissions.PrivacyConsentsRead).Produces<PageResult<ConsentPurposeHolder>>();

        var me = group.MapGroup("/me/privacy").WithTags("Privacy").RequireTenant().RequirePerson();
        me.MapGet("/", MineAsync).WithName("GetMyPrivacy")
            .WithSummary("The signed-in person's privacy: the current notice and whether they accepted it, the optional purposes with their choices, and their consent history.")
            .RequireAuthorization().Produces<MyPrivacyResponse>();
        me.MapPost("/notice-acceptance", AcceptAsync).WithName("AcceptPrivacyNotice")
            .WithSummary("Accept the published privacy notice (its id). Recorded in the consent history.")
            .RequireAuthorization().Produces<MyPrivacyResponse>().ProducesValidationProblem();
        me.MapPut("/purposes/{id:guid}", ChooseAsync).WithName("ChooseConsentPurpose")
            .WithSummary("Grant or withdraw an optional purpose. Recorded in the consent history; audited.")
            .RequireAuthorization().Produces<MyPrivacyResponse>().ProducesValidationProblem();
        me.MapGet("/export", ExportMineAsync).WithName("ExportMyData")
            .WithSummary("A copy of the person's data: a ZIP with personal-data.json (machine-readable) and the files they uploaded. Audited.")
            .RequireAuthorization().Produces<byte[]>(contentType: "application/zip");
        return group;
    }

    // ---- settings ----

    internal static async Task<IResult> GetSettingsAsync(TenantSettingsStore store, CancellationToken cancellationToken)
    {
        var retention = await store.GetAsync(PrivacySettings.Retention, cancellationToken);
        return TypedResults.Ok(new PrivacySettingsResponse(await store.GetAsync(PrivacySettings.RequestDueDays, cancellationToken), retention.TicketDays, retention.InactiveAccountDays));
    }

    internal static async Task<IResult> PutSettingsAsync(PrivacySettingsRequest request, TenantSettingsStore store, ZelvoDbContext db, IAuditLog audit, CancellationToken cancellationToken)
    {
        var current = await store.GetAsync(PrivacySettings.Retention, cancellationToken);
        var due = request.RequestDueDays ?? await store.GetAsync(PrivacySettings.RequestDueDays, cancellationToken);
        if (!PrivacySettings.RequestDueDays.IsValid(due))
        {
            return UserEndpoints.Validation("requestDueDays", "Use 1 to 90 days.");
        }

        var retention = new RetentionSettings(
            request.TicketRetentionDays is { } days ? new Dictionary<TicketType, int>(days) : current.TicketDays,
            request.ClearInactiveAccountDays == true ? null : request.InactiveAccountDays ?? current.InactiveAccountDays);
        if (!retention.IsValid())
        {
            return UserEndpoints.Validation("retention", $"Use {RetentionSettings.MinimumDays} days to 50 years.");
        }

        await store.SetAsync(PrivacySettings.RequestDueDays, due, cancellationToken);
        await store.SetAsync(PrivacySettings.Retention, retention, cancellationToken);
        audit.Add(AuditCategory.Admin, "privacy.settings.changed", "settings", "privacy", new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["requestDueDays"] = due,
            ["ticketRetentionDays"] = retention.TicketDays.ToDictionary(p => p.Key.ToString(), p => (object?)p.Value, StringComparer.Ordinal),
            ["inactiveAccountDays"] = retention.InactiveAccountDays,
        });
        await db.SaveChangesAsync(cancellationToken);
        return await GetSettingsAsync(store, cancellationToken);
    }

    internal static async Task<IResult> PreviewAsync(
        int? incident, int? serviceRequest, int? complaint, int? inquiry, int? suggestion, int? inactiveAccounts,
        TenantSettingsStore store, PrivacyRetention retention, CancellationToken cancellationToken)
    {
        var saved = await store.GetAsync(PrivacySettings.Retention, cancellationToken);
        var given = new Dictionary<TicketType, int?>
        {
            [TicketType.Incident] = incident, [TicketType.ServiceRequest] = serviceRequest, [TicketType.Complaint] = complaint,
            [TicketType.Inquiry] = inquiry, [TicketType.Suggestion] = suggestion,
        };
        var any = given.Values.Any(v => v is not null) || inactiveAccounts is not null;
        var settings = any
            ? new RetentionSettings(given.Where(p => p.Value is not null).ToDictionary(p => p.Key, p => p.Value!.Value), inactiveAccounts)
            : saved;
        if (!settings.IsValid())
        {
            return UserEndpoints.Validation("retention", $"Use {RetentionSettings.MinimumDays} days to 50 years.");
        }

        var now = DateTimeOffset.UtcNow;
        var tickets = new Dictionary<TicketType, int>();
        foreach (var (type, days) in settings.TicketDays)
        {
            tickets[type] = await retention.DueTickets(type, days, now).CountAsync(cancellationToken);
        }

        var accounts = settings.InactiveAccountDays is { } d ? await retention.DueAccounts(d, now).CountAsync(cancellationToken) : 0;
        return TypedResults.Ok(new RetentionPreviewResponse(tickets, accounts));
    }

    // ---- the notice ----

    internal static async Task<IResult> GetPublishedAsync(ZelvoDbContext db, CancellationToken cancellationToken) =>
        await db.PrivacyNotices.AsNoTracking().SingleOrDefaultAsync(n => n.State == PrivacyNoticeState.Published, cancellationToken) is { } notice
            ? TypedResults.Ok(NoticeResponse.From(notice))
            : ApiProblem.Create(StatusCodes.Status404NotFound, "no-privacy-notice", "The organisation has not published a privacy notice.");

    internal static async Task<IResult> ListNoticesAsync(ZelvoDbContext db, CancellationToken cancellationToken) =>
        TypedResults.Ok((await db.PrivacyNotices.AsNoTracking().OrderByDescending(n => n.Version).ToListAsync(cancellationToken)).Select(NoticeResponse.From).ToList());

    internal static async Task<IResult> SaveDraftAsync(NoticeRequest request, ZelvoDbContext db, CurrentUser user, CancellationToken cancellationToken)
    {
        var draft = await db.PrivacyNotices.SingleOrDefaultAsync(n => n.State == PrivacyNoticeState.Draft, cancellationToken);
        var title = request.Title?.ToText();
        var body = request.Body?.ToText();
        if (title is not null && (title.En?.Length > PrivacyNotice.TitleMaxLength || title.Ar?.Length > PrivacyNotice.TitleMaxLength))
        {
            return UserEndpoints.Validation("title", $"Use at most {PrivacyNotice.TitleMaxLength} characters.");
        }

        if (body is not null && (body.En?.Length > PrivacyNotice.BodyMaxLength || body.Ar?.Length > PrivacyNotice.BodyMaxLength))
        {
            return UserEndpoints.Validation("body", $"Use at most {PrivacyNotice.BodyMaxLength} characters.");
        }

        if (draft is null)
        {
            var published = await db.PrivacyNotices.AsNoTracking().SingleOrDefaultAsync(n => n.State == PrivacyNoticeState.Published, cancellationToken);
            var version = (await db.PrivacyNotices.MaxAsync(n => (int?)n.Version, cancellationToken) ?? 0) + 1;
            draft = new PrivacyNotice
            {
                Version = version,
                Title = title ?? published?.Title ?? new LocalizedText("Privacy notice", "إشعار الخصوصية"),
                Body = body ?? published?.Body ?? new LocalizedText(null, null),
                CreatedById = user.Account?.Id,
            };
            db.PrivacyNotices.Add(draft);
        }
        else
        {
            draft.Title = title ?? draft.Title;
            draft.Body = body ?? draft.Body;
            draft.UpdatedAt = DateTimeOffset.UtcNow;
        }

        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Ok(NoticeResponse.From(draft));
    }

    internal static async Task<IResult> DeleteDraftAsync(ZelvoDbContext db, CancellationToken cancellationToken)
    {
        var draft = await db.PrivacyNotices.SingleOrDefaultAsync(n => n.State == PrivacyNoticeState.Draft, cancellationToken);
        if (draft is not null)
        {
            db.PrivacyNotices.Remove(draft);
            await db.SaveChangesAsync(cancellationToken);
        }

        return TypedResults.NoContent();
    }

    internal static async Task<IResult> PublishAsync(ZelvoDbContext db, CurrentUser user, IAuditLog audit, CancellationToken cancellationToken)
    {
        var draft = await db.PrivacyNotices.SingleOrDefaultAsync(n => n.State == PrivacyNoticeState.Draft, cancellationToken);
        if (draft is null)
        {
            return ApiProblem.Create(StatusCodes.Status409Conflict, "no-draft", "Write a draft first.");
        }

        // Everyone must be able to read the notice in their language before accepting it.
        if (string.IsNullOrWhiteSpace(draft.Title.En) || string.IsNullOrWhiteSpace(draft.Title.Ar)
            || string.IsNullOrWhiteSpace(draft.Body.En) || string.IsNullOrWhiteSpace(draft.Body.Ar))
        {
            return UserEndpoints.Validation("body", "The notice needs a title and text in both English and Arabic.");
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.PrivacyNotices.Where(n => n.State == PrivacyNoticeState.Published)
            .ExecuteUpdateAsync(u => u.SetProperty(n => n.State, PrivacyNoticeState.Superseded), cancellationToken);
        draft.State = PrivacyNoticeState.Published;
        draft.PublishedAt = DateTimeOffset.UtcNow;
        draft.PublishedById = user.Account?.Id;
        audit.Add(AuditCategory.Admin, "privacy.notice.published", "privacy-notice", draft.Id.ToString(),
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["version"] = draft.Version });
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return TypedResults.Ok(NoticeResponse.From(draft));
    }

    // ---- purposes ----

    [GeneratedRegex("^[a-z][a-z0-9-]{1,49}$", RegexOptions.CultureInvariant)]
    private static partial Regex PurposeKey();

    internal static async Task<IResult> ListPurposesAsync(ZelvoDbContext db, CurrentUser user, CancellationToken cancellationToken)
    {
        if (!user.Has(Permissions.PrivacyManage) && !user.Has(Permissions.PrivacyConsentsRead) && !user.Has(Permissions.PrivacyRequests))
        {
            return ApiProblem.Create(StatusCodes.Status403Forbidden, "forbidden", "This needs privacy.manage or privacy.consents.read.");
        }

        return TypedResults.Ok((await db.ConsentPurposes.AsNoTracking().OrderBy(p => p.Position).ThenBy(p => p.Key).ToListAsync(cancellationToken)).Select(PurposeResponse.From).ToList());
    }

    internal static async Task<IResult> CreatePurposeAsync(PurposeRequest request, ZelvoDbContext db, CancellationToken cancellationToken)
    {
        var key = request.Key?.Trim().ToLowerInvariant();
        if (key is null || !PurposeKey().IsMatch(key))
        {
            return UserEndpoints.Validation("key", "Use 2 to 50 lowercase letters, digits and hyphens, starting with a letter.");
        }

        if (PurposeProblem(request.Name?.ToText(), request.Description?.ToText()) is { } problem)
        {
            return problem;
        }

        if (await db.ConsentPurposes.AnyAsync(p => p.Key == key, cancellationToken))
        {
            return UserEndpoints.Validation("key", "Another purpose has this key.");
        }

        var purpose = new ConsentPurpose
        {
            Key = key,
            Name = request.Name!.ToText(),
            Description = request.Description!.ToText(),
            Active = request.Active ?? true,
            Position = request.Position ?? (await db.ConsentPurposes.MaxAsync(p => (int?)p.Position, cancellationToken) ?? 0) + 1,
        };
        db.ConsentPurposes.Add(purpose);
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Created($"/api/v1/privacy/purposes/{purpose.Id}", PurposeResponse.From(purpose));
    }

    internal static async Task<IResult> UpdatePurposeAsync(Guid id, PurposeRequest request, ZelvoDbContext db, CancellationToken cancellationToken)
    {
        var purpose = await db.ConsentPurposes.SingleOrDefaultAsync(p => p.Id == id, cancellationToken);
        if (purpose is null)
        {
            return NotFound();
        }

        if (request.Key is not null && request.Key != purpose.Key)
        {
            return UserEndpoints.Validation("key", "A purpose's key cannot change: integrations use it.");
        }

        var name = request.Name?.ToText() ?? purpose.Name;
        var description = request.Description?.ToText() ?? purpose.Description;
        if (PurposeProblem(name, description) is { } problem)
        {
            return problem;
        }

        purpose.Name = name;
        purpose.Description = description;
        purpose.Active = request.Active ?? purpose.Active;
        purpose.Position = request.Position ?? purpose.Position;
        purpose.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Ok(PurposeResponse.From(purpose));
    }

    private static Microsoft.AspNetCore.Http.HttpResults.ValidationProblem? PurposeProblem(LocalizedText? name, LocalizedText? description)
    {
        if (name is null || string.IsNullOrWhiteSpace(name.En) || string.IsNullOrWhiteSpace(name.Ar)
            || name.En.Length > ConsentPurpose.NameMaxLength || name.Ar.Length > ConsentPurpose.NameMaxLength)
        {
            return UserEndpoints.Validation("name", $"Give the name in English and Arabic, at most {ConsentPurpose.NameMaxLength} characters each.");
        }

        if (description is null || string.IsNullOrWhiteSpace(description.En) || string.IsNullOrWhiteSpace(description.Ar)
            || description.En.Length > ConsentPurpose.DescriptionMaxLength || description.Ar.Length > ConsentPurpose.DescriptionMaxLength)
        {
            return UserEndpoints.Validation("description", $"Say what agreeing means in English and Arabic, at most {ConsentPurpose.DescriptionMaxLength} characters each.");
        }

        return null;
    }

    // ---- consent ----

    internal static async Task<IResult> ConsentStateAsync(Guid userId, ZelvoDbContext db, CurrentUser user, IAuditLog audit, CancellationToken cancellationToken)
    {
        if (!user.Has(Permissions.PrivacyConsentsRead) && !user.Has(Permissions.PrivacyRequests))
        {
            return ApiProblem.Create(StatusCodes.Status403Forbidden, "forbidden", "This needs privacy.consents.read.");
        }

        if (!await db.Users.AnyAsync(u => u.Id == userId, cancellationToken))
        {
            return NotFound();
        }

        var state = await PrivacyConsents.StateAsync(db, userId, cancellationToken);
        var current = await db.PrivacyNotices.AsNoTracking().Where(n => n.State == PrivacyNoticeState.Published).Select(n => (int?)n.Version).SingleOrDefaultAsync(cancellationToken);
        audit.Add(AuditCategory.View, "privacy.consents.viewed", "user", userId.ToString());
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Ok(new ConsentStateResponse(userId, state.AcceptedVersion, current is not null && state.AcceptedVersion == current,
            [.. state.Purposes.Select(p => new ConsentPurposeState(p.Key, p.Granted, p.ChangedAt))]));
    }

    internal static async Task<IResult> GrantedAsync(string key, [AsParameters] PageQuery page, ZelvoDbContext db, IAuditLog audit, CancellationToken cancellationToken)
    {
        var purpose = await db.ConsentPurposes.AsNoTracking().SingleOrDefaultAsync(p => p.Key == key, cancellationToken);
        if (purpose is null)
        {
            return NotFound();
        }

        // The newest record per person for this purpose decides: a grant with no later record.
        var purposeId = purpose.Id;
        var granted = db.ConsentRecords.AsNoTracking()
            .Where(r => r.PurposeId == purposeId && r.UserId != null && r.Action == ConsentAction.Granted)
            .Where(r => !db.ConsentRecords.Any(o => o.PurposeId == purposeId && o.UserId == r.UserId && o.CreatedAt > r.CreatedAt))
            .Where(r => db.Users.Any(u => u.Id == r.UserId && u.ErasedAt == null && u.Status == UserStatus.Active))
            .OrderBy(r => r.UserId).ThenBy(r => r.CreatedAt);
        var total = await granted.LongCountAsync(cancellationToken);
        var rows = await granted.Skip(page.Skip).Take(page.Size).Select(r => new ConsentPurposeHolder(r.UserId!.Value, r.CreatedAt)).ToListAsync(cancellationToken);
        audit.Add(AuditCategory.View, "privacy.consents.listed", "consent-purpose", purpose.Id.ToString(),
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["count"] = rows.Count });
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Ok(new PageResult<ConsentPurposeHolder>(rows, page.Number, page.Size, total));
    }

    internal static async Task<IResult> MineAsync(ZelvoDbContext db, CurrentUser user, CancellationToken cancellationToken) =>
        TypedResults.Ok(await MyPrivacyAsync(db, user.Account!.Id, cancellationToken));

    internal static async Task<MyPrivacyResponse> MyPrivacyAsync(ZelvoDbContext db, Guid userId, CancellationToken cancellationToken)
    {
        var notice = await db.PrivacyNotices.AsNoTracking().SingleOrDefaultAsync(n => n.State == PrivacyNoticeState.Published, cancellationToken);
        var state = await PrivacyConsents.StateAsync(db, userId, cancellationToken);
        var history = await db.ConsentRecords.AsNoTracking().Where(r => r.UserId == userId).OrderByDescending(r => r.CreatedAt).ThenByDescending(r => r.Id).Take(100).ToListAsync(cancellationToken);
        var keys = state.Purposes.ToDictionary(p => p.Id, p => p.Key);
        var accepted = notice is not null ? history.FirstOrDefault(r => r.Action == ConsentAction.NoticeAccepted && r.NoticeId == notice.Id) : null;
        return new MyPrivacyResponse(
            notice is null ? null : new NoticeSummary(notice.Id, notice.Version, LocalizedTextDto.From(notice.Title), notice.PublishedAt),
            notice is null || accepted is not null || await db.ConsentRecords.AnyAsync(r => r.UserId == userId && r.NoticeId == notice.Id, cancellationToken),
            accepted?.CreatedAt,
            [.. state.Purposes.Where(p => p.Active || p.Granted).Select(p => new MyPurposeResponse(p.Id, p.Key, LocalizedTextDto.From(p.Name), LocalizedTextDto.From(p.Description), p.Granted, p.ChangedAt))],
            [.. history.Select(r => new ConsentRecordResponse(r.Id, r.Action, r.NoticeVersion, r.PurposeId is { } p && keys.TryGetValue(p, out var k) ? k : null, r.Channel,
                r.RecordedById is not null && r.RecordedById != userId, r.CreatedAt))]);
    }

    internal static async Task<IResult> AcceptAsync(AcceptNoticeRequest request, ZelvoDbContext db, CurrentUser user, IAuditLog audit, CancellationToken cancellationToken)
    {
        var notice = await db.PrivacyNotices.AsNoTracking().SingleOrDefaultAsync(n => n.State == PrivacyNoticeState.Published, cancellationToken);
        if (notice is null || request.NoticeId != notice.Id)
        {
            return UserEndpoints.Validation("noticeId", "Accept the privacy notice that is published now.");
        }

        var userId = user.Account!.Id;
        if (!await db.ConsentRecords.AnyAsync(r => r.UserId == userId && r.NoticeId == notice.Id && r.Action == ConsentAction.NoticeAccepted, cancellationToken))
        {
            db.ConsentRecords.Add(new ConsentRecord { UserId = userId, Action = ConsentAction.NoticeAccepted, NoticeId = notice.Id, NoticeVersion = notice.Version, Channel = ConsentChannel.Web });
            audit.Add(AuditCategory.Create, "privacy.notice.accepted", "privacy-notice", notice.Id.ToString(),
                new Dictionary<string, object?>(StringComparer.Ordinal) { ["version"] = notice.Version });
            await db.SaveChangesAsync(cancellationToken);
        }

        return TypedResults.Ok(await MyPrivacyAsync(db, userId, cancellationToken));
    }

    internal static async Task<IResult> ChooseAsync(Guid id, PurposeChoiceRequest request, ZelvoDbContext db, CurrentUser user, IAuditLog audit, CancellationToken cancellationToken)
    {
        if (request.Granted is not { } granted)
        {
            return UserEndpoints.Validation("granted", "Say whether you agree.");
        }

        var purpose = await db.ConsentPurposes.AsNoTracking().SingleOrDefaultAsync(p => p.Id == id, cancellationToken);
        if (purpose is null || (!purpose.Active && granted))
        {
            return NotFound();
        }

        var userId = user.Account!.Id;
        var state = await PrivacyConsents.StateAsync(db, userId, cancellationToken);
        if (state.Purposes.First(p => p.Id == id).Granted != granted)
        {
            await PrivacyConsents.RecordPurposeAsync(db, userId, purpose.Id, granted, ConsentChannel.Web, null, cancellationToken);
            audit.Add(AuditCategory.Change, granted ? "privacy.consent.granted" : "privacy.consent.withdrawn", "consent-purpose", purpose.Id.ToString(),
                new Dictionary<string, object?>(StringComparer.Ordinal) { ["purpose"] = purpose.Key });
            await db.SaveChangesAsync(cancellationToken);
        }

        return TypedResults.Ok(await MyPrivacyAsync(db, userId, cancellationToken));
    }

    internal static async Task<IResult> ExportMineAsync(HttpContext context, CurrentUser user, PersonalDataExport export, IAuditLog audit, ZelvoDbContext db, CancellationToken cancellationToken)
    {
        audit.Add(AuditCategory.View, "privacy.export.downloaded", "user", user.Account!.Id.ToString(),
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["by"] = "self" });
        await db.SaveChangesAsync(cancellationToken);
        return await ZipAsync(export, user.Account.Id, "my-data", cancellationToken);
    }

    /// <summary>Builds the ZIP in a temporary file (removed when sent) and returns it.</summary>
    internal static async Task<IResult> ZipAsync(PersonalDataExport export, Guid personId, string name, CancellationToken cancellationToken)
    {
        var file = new FileStream(Path.Combine(Path.GetTempPath(), $"zelvo-export-{Guid.NewGuid():N}.zip"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
            81920, FileOptions.DeleteOnClose | FileOptions.Asynchronous);
        try
        {
            await export.WriteAsync(personId, file, cancellationToken);
            file.Position = 0;
            return TypedResults.File(file, "application/zip", $"{name}-{DateTimeOffset.UtcNow:yyyyMMdd}.zip");
        }
        catch
        {
            await file.DisposeAsync();
            throw;
        }
    }

    internal static IResult NotFound() => ApiProblem.Create(StatusCodes.Status404NotFound, "not-found", "Not found.");
}

/// <summary>A person who agrees to a purpose.</summary>
internal sealed record ConsentPurposeHolder(Guid UserId, DateTimeOffset GrantedAt);

/// <summary>A purpose and a person's choice.</summary>
internal sealed record PurposeChoice(Guid Id, string Key, LocalizedText Name, LocalizedText Description, bool Active, bool Granted, DateTimeOffset? ChangedAt);

/// <summary>A person's consent worked out from the history.</summary>
internal sealed record ConsentState(int? AcceptedVersion, IReadOnlyList<PurposeChoice> Purposes);

/// <summary>Reading and writing the consent history (task F5).</summary>
internal static class PrivacyConsents
{
    public static async Task<ConsentState> StateAsync(ZelvoDbContext db, Guid userId, CancellationToken cancellationToken)
    {
        var purposes = await db.ConsentPurposes.AsNoTracking().OrderBy(p => p.Position).ThenBy(p => p.Key).ToListAsync(cancellationToken);
        var records = await db.ConsentRecords.AsNoTracking().Where(r => r.UserId == userId).OrderBy(r => r.CreatedAt).ThenBy(r => r.Id).ToListAsync(cancellationToken);
        var accepted = records.LastOrDefault(r => r.Action == ConsentAction.NoticeAccepted)?.NoticeVersion;
        return new ConsentState(accepted, [.. purposes.Select(p =>
        {
            var last = records.LastOrDefault(r => r.PurposeId == p.Id);
            return new PurposeChoice(p.Id, p.Key, p.Name, p.Description, p.Active, last?.Action == ConsentAction.Granted, last?.CreatedAt);
        })]);
    }

    public static async Task RecordPurposeAsync(ZelvoDbContext db, Guid userId, Guid purposeId, bool granted, ConsentChannel channel, Guid? recordedBy, CancellationToken cancellationToken)
    {
        var notice = await db.PrivacyNotices.AsNoTracking().Where(n => n.State == PrivacyNoticeState.Published).Select(n => new { n.Id, n.Version }).SingleOrDefaultAsync(cancellationToken);
        db.ConsentRecords.Add(new ConsentRecord
        {
            UserId = userId,
            Action = granted ? ConsentAction.Granted : ConsentAction.Withdrawn,
            PurposeId = purposeId,
            NoticeId = notice?.Id,
            NoticeVersion = notice?.Version,
            Channel = channel,
            RecordedById = recordedBy,
        });
    }

    /// <summary>
    /// For visitors who chat or send an anonymous message (decided 2026-11-25): when a notice is
    /// published they must have seen it. Returns a validation problem when they did not say so,
    /// else the published notice (null when there is none) to record with <see cref="RecordVisitor"/>.
    /// </summary>
    public static async Task<(IResult? Problem, PrivacyNotice? Notice)> VisitorCheckAsync(ZelvoDbContext db, bool? accepted, CancellationToken cancellationToken)
    {
        var notice = await db.PrivacyNotices.AsNoTracking().SingleOrDefaultAsync(n => n.State == PrivacyNoticeState.Published, cancellationToken);
        return notice is not null && accepted != true
            ? (UserEndpoints.Validation("privacyNoticeAccepted", "Please read and accept the privacy notice first."), null)
            : (null, notice);
    }

    /// <summary>Records that a visitor accepted <paramref name="notice"/> for what they did (<c>chat:…</c>, <c>ticket:…</c>); saved with the next save.</summary>
    public static void RecordVisitor(ZelvoDbContext db, PrivacyNotice? notice, string reference, ConsentChannel channel)
    {
        if (notice is null)
        {
            return;
        }

        db.ConsentRecords.Add(new ConsentRecord
        {
            VisitorReference = reference,
            Action = ConsentAction.NoticeAccepted,
            NoticeId = notice.Id,
            NoticeVersion = notice.Version,
            Channel = channel,
        });
    }
}
