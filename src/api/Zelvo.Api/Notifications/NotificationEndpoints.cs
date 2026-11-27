using Microsoft.EntityFrameworkCore;
using Zelvo.Api.Conventions;
using Zelvo.Api.Identity;
using Zelvo.Api.Integrations;
using Zelvo.Api.Settings;
using Zelvo.Api.Tenancy;
using Zelvo.Data;
using Zelvo.Data.Auditing;
using Zelvo.Data.Localization;
using Zelvo.Data.Notifications;
using Zelvo.Notifications;
using Zelvo.Tickets;

namespace Zelvo.Api.Notifications;

/// <summary>
/// Notifications (module 10, task B5): each person's in-app list and email choices, and
/// Settings → Notifications for administrators: events on or off, their wording in English and
/// Arabic with a preview, a test email and the email delivery log.
/// </summary>
internal static class NotificationEndpoints
{
    public static SettingDefinition<IReadOnlyDictionary<string, EventSetting>> Events { get; } =
        new(NotificationSettings.EventsKey, NotificationSettings.Default, Permissions.NotificationsManage, NotificationSettings.IsValid);

    public static RouteGroupBuilder MapNotificationEndpoints(this RouteGroupBuilder group)
    {
        var mine = group.MapGroup("/me/notifications").WithTags("Notifications").RequireTenant().RequirePerson();

        mine.MapGet("/", ListMineAsync)
            .WithName("ListMyNotifications")
            .WithSummary("Your in-app notifications, newest first, in your language. Set unreadOnly=true for unread ones.")
            .Produces<PageResult<NotificationResponse>>();

        mine.MapGet("/unread-count", UnreadCountAsync)
            .WithName("CountMyUnreadNotifications")
            .WithSummary("How many of your notifications are unread.")
            .Produces<UnreadCountResponse>();

        mine.MapPost("/{id:guid}/read", MarkReadAsync)
            .WithName("MarkNotificationRead")
            .WithSummary("Marks one of your notifications read; answers how many are still unread.")
            .Produces<UnreadCountResponse>();

        mine.MapPost("/read-all", MarkAllReadAsync)
            .WithName("MarkAllNotificationsRead")
            .WithSummary("Marks all your notifications read; answers how many were marked.")
            .Produces<UnreadCountResponse>();

        var preferences = group.MapGroup("/me/notification-preferences").WithTags("Notifications").RequireTenant().RequirePerson();

        preferences.MapGet("/", PreferencesAsync)
            .WithName("GetMyNotificationPreferences")
            .WithSummary("The notifications that can reach you and whether you get their email. In-app notifications are always on; SLA alerts always send email (product owner decision 2026-11-24).")
            .Produces<IReadOnlyList<NotificationPreferenceResponse>>();

        preferences.MapPut("/", UpdatePreferencesAsync)
            .WithName("UpdateMyNotificationPreferences")
            .WithSummary("Turns the email of events on or off for you. Events whose email cannot be turned off are refused. Audited.")
            .Produces<IReadOnlyList<NotificationPreferenceResponse>>()
            .ProducesValidationProblem();

        var settings = group.MapGroup("/notifications").WithTags("Notifications").RequireTenant();

        settings.MapGet("/settings", SettingsAsync)
            .WithName("GetNotificationSettings")
            .WithSummary("Settings → Notifications: every event with its switches, placeholders, the tenant's wording (with missing languages) and Zelvo's default wording, and whether the installation has a mail server. Needs notifications.manage.")
            .RequirePermission(Permissions.NotificationsManage)
            .Produces<NotificationSettingsResponse>();

        settings.MapPut("/settings/events/{eventType}", UpdateEventAsync)
            .WithName("UpdateNotificationEvent")
            .WithSummary("Switches an event on or off, and its email. Needs notifications.manage; audited.")
            .RequirePermission(Permissions.NotificationsManage)
            .Produces<NotificationEventResponse>()
            .ProducesValidationProblem();

        settings.MapPut("/templates/{eventType}", SaveTemplateAsync)
            .WithName("SaveNotificationTemplate")
            .WithSummary("Saves the tenant's wording of an event: subject, email body, in-app text and link label, each in English and Arabic (a missing language uses Zelvo's default and is listed under Missing translations). Only the event's placeholders are allowed. Needs notifications.manage; audited.")
            .RequirePermission(Permissions.NotificationsManage)
            .Produces<NotificationEventResponse>()
            .ProducesValidationProblem();

        settings.MapDelete("/templates/{eventType}", ResetTemplateAsync)
            .WithName("ResetNotificationTemplate")
            .WithSummary("Goes back to Zelvo's default wording of an event. Needs notifications.manage; audited.")
            .RequirePermission(Permissions.NotificationsManage)
            .Produces<NotificationEventResponse>();

        settings.MapPost("/templates/{eventType}/preview", PreviewAsync)
            .WithName("PreviewNotificationTemplate")
            .WithSummary("Renders wording (the saved one, or the one in the request) with sample values in a language, as you would receive it: subject, in-app text, email HTML and plain text. Needs notifications.manage.")
            .RequirePermission(Permissions.NotificationsManage)
            .Produces<NotificationPreviewResponse>()
            .ProducesValidationProblem();

        settings.MapPost("/test-email", TestEmailAsync)
            .WithName("SendTestNotificationEmail")
            .WithSummary("Queues a test email to your own address in your language; the worker sends it within seconds and the delivery log shows the result. Needs notifications.manage; audited.")
            .RequirePermission(Permissions.NotificationsManage)
            .Produces<NotificationDeliveryResponse>(StatusCodes.Status202Accepted);

        settings.MapGet("/deliveries", DeliveriesAsync)
            .WithName("ListNotificationDeliveries")
            .WithSummary("Email delivery log, newest first, with addresses masked; filter by status or event. Kept 30 days. Needs notifications.manage.")
            .RequirePermission(Permissions.NotificationsManage)
            .Produces<PageResult<NotificationDeliveryResponse>>();

        settings.MapGet("/deliveries/{id:long}", DeliveryAsync)
            .WithName("GetNotificationDelivery")
            .WithSummary("One email with its address and content. Viewing it is audited (personal data). Needs notifications.manage.")
            .RequirePermission(Permissions.NotificationsManage)
            .Produces<NotificationDeliveryDetailResponse>();

        settings.MapPost("/deliveries/{id:long}/resend", ResendAsync)
            .WithName("ResendNotificationEmail")
            .WithSummary("Queues the same email again; the worker sends it within seconds. Needs notifications.manage; audited.")
            .RequirePermission(Permissions.NotificationsManage)
            .Produces<NotificationDeliveryResponse>(StatusCodes.Status202Accepted);

        return group;
    }

    internal static async Task<IResult> ListMineAsync(
        [AsParameters] PageQuery page, bool? unreadOnly, CurrentUser user, ZelvoDbContext db, CancellationToken cancellationToken)
    {
        var me = user.Account!.Id;
        var query = db.Notifications.AsNoTracking().Where(n => n.RecipientId == me && n.Status == NotificationStatus.Delivered);
        if (unreadOnly == true)
        {
            query = query.Where(n => n.ReadAt == null);
        }

        var total = await query.LongCountAsync(cancellationToken);
        var items = await query.OrderByDescending(n => n.Id).Skip(page.Skip).Take(page.Size).ToListAsync(cancellationToken);
        return TypedResults.Ok(new PageResult<NotificationResponse>([.. items.Select(NotificationResponse.From)], page.Number, page.Size, total));
    }

    internal static async Task<IResult> UnreadCountAsync(CurrentUser user, ZelvoDbContext db, CancellationToken cancellationToken)
    {
        var me = user.Account!.Id;
        var count = await db.Notifications.CountAsync(n => n.RecipientId == me && n.Status == NotificationStatus.Delivered && n.ReadAt == null, cancellationToken);
        return TypedResults.Ok(new UnreadCountResponse(count));
    }

    internal static async Task<IResult> MarkReadAsync(Guid id, CurrentUser user, ZelvoDbContext db, CancellationToken cancellationToken)
    {
        var me = user.Account!.Id;
        var notification = await db.Notifications.SingleOrDefaultAsync(n => n.Id == id && n.RecipientId == me && n.Status == NotificationStatus.Delivered, cancellationToken);
        if (notification is null)
        {
            return ApiProblem.Create(StatusCodes.Status404NotFound, "notification-not-found", "The notification does not exist.");
        }

        if (notification.ReadAt is null)
        {
            notification.ReadAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
        }

        return await UnreadCountAsync(user, db, cancellationToken);
    }

    internal static async Task<IResult> MarkAllReadAsync(CurrentUser user, ZelvoDbContext db, CancellationToken cancellationToken)
    {
        var me = user.Account!.Id;
        var now = DateTimeOffset.UtcNow;
        var count = await db.Notifications
            .Where(n => n.RecipientId == me && n.Status == NotificationStatus.Delivered && n.ReadAt == null)
            .ExecuteUpdateAsync(set => set.SetProperty(n => n.ReadAt, now), cancellationToken);
        return TypedResults.Ok(new UnreadCountResponse(count));
    }

    internal static async Task<IResult> PreferencesAsync(CurrentUser user, ZelvoDbContext db, NotificationOptions options, CancellationToken cancellationToken) =>
        TypedResults.Ok(await PreferenceListAsync(user, db, options, cancellationToken));

    internal static async Task<IResult> UpdatePreferencesAsync(
        UpdateNotificationPreferencesRequest request, CurrentUser user, ZelvoDbContext db, NotificationOptions options, CancellationToken cancellationToken)
    {
        var mine = await PreferenceListAsync(user, db, options, cancellationToken);
        var me = user.Account!.Id;
        var rows = await db.NotificationPreferences.Where(p => p.UserId == me).ToDictionaryAsync(p => p.EventType, StringComparer.Ordinal, cancellationToken);
        foreach (var change in request.Events ?? [])
        {
            var current = mine.FirstOrDefault(p => string.Equals(p.EventType, change.EventType, StringComparison.Ordinal));
            if (current is null)
            {
                return UserEndpoints.Validation("events", $"'{change.EventType}' is not a notification you can receive.");
            }

            if (!current.CanChange && !change.Email)
            {
                return UserEndpoints.Validation("events", $"The email of '{change.EventType}' cannot be turned off.");
            }

            // Task J2: push is everyone's own choice for every event (left out: unchanged).
            var email = current.CanChange ? change.Email : true;
            var push = change.Push ?? current.Push;
            if (rows.TryGetValue(change.EventType, out var row))
            {
                if (row.Email != email || row.Push != push)
                {
                    row.Email = email;
                    row.Push = push;
                    row.UpdatedAt = DateTimeOffset.UtcNow;
                }
            }
            else if (!email || !push)
            {
                var added = new NotificationPreference { UserId = me, EventType = change.EventType, Email = email, Push = push };
                db.NotificationPreferences.Add(added);
                rows[change.EventType] = added;
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Ok(await PreferenceListAsync(user, db, options, cancellationToken));
    }

    /// <summary>Events that can reach <paramref name="user"/>, by what they may do (including permissions waiting for their second factor).</summary>
    private static async Task<IReadOnlyList<NotificationPreferenceResponse>> PreferenceListAsync(
        CurrentUser user, ZelvoDbContext db, NotificationOptions options, CancellationToken cancellationToken)
    {
        var pushAvailable = options.Push.Enabled;
        bool May(string permission) => user.Has(permission) || user.PendingMfaPermissions.Contains(permission);
        var settings = await NotificationSettings.ReadAsync(db, cancellationToken);
        var me = user.Account!.Id;
        var choices = await db.NotificationPreferences.AsNoTracking().Where(p => p.UserId == me)
            .ToDictionaryAsync(p => p.EventType, p => (p.Email, p.Push), StringComparer.Ordinal, cancellationToken);
        return
        [
            .. NotificationEvents.All
                .Where(e => e.Audience switch
                {
                    NotificationAudience.Staff => May(Permissions.IncidentsEdit),
                    NotificationAudience.Publisher => May(Permissions.KnowledgePublish),
                    _ => true,
                })
                .Select(e =>
                {
                    var setting = NotificationSettings.For(settings, e.Type);
                    var choice = choices.TryGetValue(e.Type, out var c) ? c : (Email: true, Push: true);
                    return new NotificationPreferenceResponse(e.Type, e.Audience, !e.EmailOptional || choice.Email,
                        e.EmailOptional, setting.Enabled, setting.Enabled && setting.Email, choice.Push, setting.Enabled && setting.Push && pushAvailable);
                }),
        ];
    }

    internal static async Task<IResult> SettingsAsync(ZelvoDbContext db, NotificationOptions options, CancellationToken cancellationToken)
    {
        var settings = await NotificationSettings.ReadAsync(db, cancellationToken);
        var templates = await db.NotificationTemplates.AsNoTracking().ToDictionaryAsync(t => t.EventType, StringComparer.Ordinal, cancellationToken);
        return TypedResults.Ok(new NotificationSettingsResponse(
            options.Smtp.Configured,
            [.. NotificationEvents.All.Select(e => NotificationEventResponse.From(e, settings, templates.GetValueOrDefault(e.Type)))],
            options.Push.Enabled));
    }

    internal static async Task<IResult> UpdateEventAsync(
        string eventType, UpdateNotificationEventRequest request, ZelvoDbContext db, TenantSettingsStore store, CancellationToken cancellationToken)
    {
        if (NotificationEvents.Find(eventType) is not { } definition)
        {
            return EventNotFound();
        }

        var current = new Dictionary<string, EventSetting>(await store.GetAsync(Events, cancellationToken), StringComparer.Ordinal);
        var before = NotificationSettings.For(current, definition.Type);
        current[definition.Type] = new EventSetting(request.Enabled, request.Email, request.Messaging ?? true, request.Push ?? before.Push);
        await store.SetAsync<IReadOnlyDictionary<string, EventSetting>>(Events, current, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Ok(await EventAsync(definition, db, cancellationToken));
    }

    internal static async Task<IResult> SaveTemplateAsync(
        string eventType, NotificationTemplateRequest request, ZelvoDbContext db, CancellationToken cancellationToken)
    {
        if (NotificationEvents.Find(eventType) is not { } definition)
        {
            return EventNotFound();
        }

        if (request.Validate(definition) is { Count: > 0 } errors)
        {
            return TypedResults.ValidationProblem(errors);
        }

        var row = await db.NotificationTemplates.SingleOrDefaultAsync(t => t.EventType == definition.Type, cancellationToken);
        if (row is null)
        {
            db.NotificationTemplates.Add(new NotificationTemplate
            {
                EventType = definition.Type,
                Subject = request.Subject!.ToValue(),
                Body = request.Body!.ToValue(),
                InApp = request.InApp!.ToValue(),
                Action = request.Action!.ToValue(),
            });
        }
        else
        {
            row.Subject = request.Subject!.ToValue();
            row.Body = request.Body!.ToValue();
            row.InApp = request.InApp!.ToValue();
            row.Action = request.Action!.ToValue();
            row.UpdatedAt = DateTimeOffset.UtcNow;
        }

        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Ok(await EventAsync(definition, db, cancellationToken));
    }

    internal static async Task<IResult> ResetTemplateAsync(string eventType, ZelvoDbContext db, CancellationToken cancellationToken)
    {
        if (NotificationEvents.Find(eventType) is not { } definition)
        {
            return EventNotFound();
        }

        if (await db.NotificationTemplates.SingleOrDefaultAsync(t => t.EventType == definition.Type, cancellationToken) is { } row)
        {
            db.NotificationTemplates.Remove(row);
            await db.SaveChangesAsync(cancellationToken);
        }

        return TypedResults.Ok(await EventAsync(definition, db, cancellationToken));
    }

    internal static async Task<IResult> PreviewAsync(
        string eventType, NotificationPreviewRequest request, CurrentUser user, ZelvoDbContext db, NotificationComposer composer,
        CancellationToken cancellationToken)
    {
        if (NotificationEvents.Find(eventType) is not { } definition)
        {
            return EventNotFound();
        }

        var language = request.Language is LocalizedText.English or LocalizedText.Arabic ? request.Language : LocalizedText.English;
        if (request.Template is { } draft && draft.Validate(definition, requireAll: false) is { Count: > 0 } errors)
        {
            return TypedResults.ValidationProblem(errors);
        }

        var saved = await composer.TemplateAsync(definition.Type, language, cancellationToken);
        var template = request.Template is { } t
            ? new TemplateText(
                t.Subject?.ToValue().Get(language) ?? saved.Subject,
                t.Body?.ToValue().Get(language) ?? saved.Body,
                t.InApp?.ToValue().Get(language) ?? saved.InApp,
                t.Action?.ToValue().Get(language) ?? saved.Action)
            : saved;
        var account = user.Account!;
        var format = await composer.FormatAsync(account, cancellationToken, language);
        var matrix = await TicketSettingKeys.ReadAsync(db, TicketSettingKeys.PriorityMatrix, PriorityMatrix.Default, m => m.IsValid(), cancellationToken);
        var values = NotificationSamples.Values(DateTimeOffset.UtcNow, PlaceholderValue.OfText(matrix.Priorities[1].ToText()));
        var path = definition.Audience == NotificationAudience.Requester ? "/my-incidents" : definition.Audience == NotificationAudience.Publisher ? "/knowledge/review" : "/incidents";
        var rendered = await composer.RenderAsync(template, values, account, format, path, cancellationToken);
        return TypedResults.Ok(new NotificationPreviewResponse(language, rendered.Subject, rendered.InApp, rendered.Html, rendered.Text));
    }

    internal static async Task<IResult> TestEmailAsync(CurrentUser user, ZelvoDbContext db, NotificationComposer composer, IAuditLog audit, CancellationToken cancellationToken)
    {
        var account = user.Account!;
        if (account.Email is not { Length: > 0 } address)
        {
            return ApiProblem.Create(StatusCodes.Status409Conflict, "no-email-address", "Your account has no email address.");
        }

        var format = await composer.FormatAsync(account, cancellationToken);
        var template = NotificationTexts.For(format.Language).Templates[NotificationEvents.TestEmail];
        var rendered = await composer.RenderAsync(template, new Dictionary<string, PlaceholderValue>(), account, format, "/settings/notifications", cancellationToken);
        var delivery = new NotificationDelivery
        {
            RecipientId = account.Id,
            EventType = NotificationEvents.TestEmail,
            Address = address,
            Language = format.Language,
            Subject = rendered.Subject,
            HtmlBody = rendered.Html,
            TextBody = rendered.Text,
            Manual = true,
        };
        db.NotificationDeliveries.Add(delivery);
        audit.Add(AuditCategory.Admin, "notifications.test-email.queued", "user", account.Id.ToString());
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Accepted("/api/v1/notifications/deliveries", NotificationDeliveryResponse.From(delivery));
    }

    internal static async Task<IResult> DeliveriesAsync(
        [AsParameters] PageQuery page, NotificationDeliveryStatus? status, string? eventType, ZelvoDbContext db, CancellationToken cancellationToken)
    {
        var query = db.NotificationDeliveries.AsNoTracking();
        if (status is { } s)
        {
            query = query.Where(d => d.Status == s);
        }

        if (!string.IsNullOrWhiteSpace(eventType))
        {
            query = query.Where(d => d.EventType == eventType);
        }

        var total = await query.LongCountAsync(cancellationToken);
        var items = await query.OrderByDescending(d => d.Id).Skip(page.Skip).Take(page.Size).ToListAsync(cancellationToken);
        return TypedResults.Ok(new PageResult<NotificationDeliveryResponse>([.. items.Select(NotificationDeliveryResponse.From)], page.Number, page.Size, total));
    }

    internal static async Task<IResult> DeliveryAsync(long id, ZelvoDbContext db, IAuditLog audit, CancellationToken cancellationToken)
    {
        if (await db.NotificationDeliveries.AsNoTracking().SingleOrDefaultAsync(d => d.Id == id, cancellationToken) is not { } delivery)
        {
            return DeliveryNotFound();
        }

        await audit.RecordAsync(AuditCategory.View, "notifications.delivery.viewed", "notification_delivery", id.ToString(System.Globalization.CultureInfo.InvariantCulture),
            cancellationToken: cancellationToken);
        return TypedResults.Ok(NotificationDeliveryDetailResponse.From(delivery));
    }

    internal static async Task<IResult> ResendAsync(long id, ZelvoDbContext db, IAuditLog audit, CancellationToken cancellationToken)
    {
        if (await db.NotificationDeliveries.AsNoTracking().SingleOrDefaultAsync(d => d.Id == id, cancellationToken) is not { } original)
        {
            return DeliveryNotFound();
        }

        var copy = new NotificationDelivery
        {
            NotificationId = original.NotificationId,
            RecipientId = original.RecipientId,
            EventType = original.EventType,
            Address = original.Address,
            Language = original.Language,
            Subject = original.Subject,
            HtmlBody = original.HtmlBody,
            TextBody = original.TextBody,
            Manual = true,
        };
        db.NotificationDeliveries.Add(copy);
        audit.Add(AuditCategory.Admin, "notifications.delivery.resent", "notification_delivery", id.ToString(System.Globalization.CultureInfo.InvariantCulture));
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Accepted("/api/v1/notifications/deliveries", NotificationDeliveryResponse.From(copy));
    }

    private static async Task<NotificationEventResponse> EventAsync(NotificationEventDefinition definition, ZelvoDbContext db, CancellationToken cancellationToken)
    {
        var settings = await NotificationSettings.ReadAsync(db, cancellationToken);
        var row = await db.NotificationTemplates.AsNoTracking().SingleOrDefaultAsync(t => t.EventType == definition.Type, cancellationToken);
        return NotificationEventResponse.From(definition, settings, row);
    }

    private static IResult EventNotFound() =>
        ApiProblem.Create(StatusCodes.Status404NotFound, "notification-event-not-found", "There is no notification event with that name.");

    private static IResult DeliveryNotFound() =>
        ApiProblem.Create(StatusCodes.Status404NotFound, "delivery-not-found", "The email does not exist.");

    /// <summary>An address with most of its local part hidden, for lists: <c>f***@example.ae</c>.</summary>
    internal static string Mask(string address)
    {
        var at = address.IndexOf('@', StringComparison.Ordinal);
        return at <= 0 ? "***" : string.Concat(address.AsSpan(0, 1), "***", address.AsSpan(at));
    }
}

/// <summary>An in-app notification.</summary>
/// <param name="Id">Id.</param>
/// <param name="EventType">Event, for example <c>incident.assigned</c>.</param>
/// <param name="Title">Title in the language it was written in.</param>
/// <param name="Text">Short text.</param>
/// <param name="Language">en or ar.</param>
/// <param name="LinkPath">Page in the web app without the language (prefix it with /en or /ar).</param>
/// <param name="CreatedAt">When it happened.</param>
/// <param name="ReadAt">When you read it.</param>
internal sealed record NotificationResponse(Guid Id, string EventType, string? Title, string? Text, string? Language, string? LinkPath, DateTimeOffset CreatedAt, DateTimeOffset? ReadAt)
{
    public static NotificationResponse From(Notification n) => new(n.Id, n.EventType, n.Title, n.Text, n.Language, n.LinkPath, n.CreatedAt, n.ReadAt);
}

/// <summary>A number of notifications.</summary>
/// <param name="Count">How many.</param>
internal sealed record UnreadCountResponse(int Count);

/// <summary>One event as a person sees it under Account → Notifications.</summary>
/// <param name="EventType">Event.</param>
/// <param name="Audience">Requester, Staff or Publisher.</param>
/// <param name="Email">Whether you get its email.</param>
/// <param name="CanChange">Whether you may turn its email off.</param>
/// <param name="Enabled">Whether your organisation has it on.</param>
/// <param name="EmailEnabled">Whether your organisation sends its email.</param>
/// <param name="Push">Task J2: whether it also comes by web push to your devices.</param>
/// <param name="PushEnabled">Task J2: whether your organisation and this installation send it by web push.</param>
internal sealed record NotificationPreferenceResponse(
    string EventType, NotificationAudience Audience, bool Email, bool CanChange, bool Enabled, bool EmailEnabled, bool Push = true, bool PushEnabled = false);

/// <summary>Your email choices.</summary>
public sealed class UpdateNotificationPreferencesRequest
{
    /// <summary>Events to change; others stay as they are.</summary>
    public IReadOnlyList<NotificationPreferenceInput>? Events { get; init; }
}

/// <summary>One email choice.</summary>
public sealed class NotificationPreferenceInput
{
    /// <summary>Event.</summary>
    public required string EventType { get; init; }

    /// <summary>Whether you want its email.</summary>
    public bool Email { get; init; }

    /// <summary>Task J2: whether you want it by web push (unchanged when left out).</summary>
    public bool? Push { get; init; }
}

/// <summary>Settings → Notifications.</summary>
/// <param name="MailServerConfigured">Whether the installation has a mail server (Notifications:Smtp:Host); without one only in-app notifications work.</param>
/// <param name="Events">Every event.</param>
/// <param name="PushAvailable">Task J2: whether this installation offers web push (it needs outgoing internet to the browsers' push services).</param>
internal sealed record NotificationSettingsResponse(bool MailServerConfigured, IReadOnlyList<NotificationEventResponse> Events, bool PushAvailable = false);

/// <summary>Wording of a notification in both languages.</summary>
/// <param name="Subject">Subject and in-app title.</param>
/// <param name="Body">Email body.</param>
/// <param name="InApp">In-app text.</param>
/// <param name="Action">Link label.</param>
internal sealed record NotificationTemplateDto(LocalizedTextDto Subject, LocalizedTextDto Body, LocalizedTextDto InApp, LocalizedTextDto Action);

/// <summary>One event in Settings → Notifications.</summary>
/// <param name="EventType">Event.</param>
/// <param name="Audience">Who it is for.</param>
/// <param name="EmailOptional">Whether people may turn its email off.</param>
/// <param name="Enabled">On or off.</param>
/// <param name="Email">Whether it sends email.</param>
/// <param name="Placeholders">Placeholders its wording may use.</param>
/// <param name="Customised">Whether the tenant has its own wording.</param>
/// <param name="Template">The tenant's wording, or <see langword="null"/> when it uses Zelvo's.</param>
/// <param name="Default">Zelvo's default wording.</param>
/// <param name="Messaging">Task E3: whether it also goes by WhatsApp, SMS and Teams.</param>
/// <param name="Push">Task J2: whether it also goes by web push.</param>
internal sealed record NotificationEventResponse(
    string EventType, NotificationAudience Audience, bool EmailOptional, bool Enabled, bool Email, IReadOnlyList<string> Placeholders,
    bool Customised, NotificationTemplateDto? Template, NotificationTemplateDto Default, bool Messaging = true, bool Push = true)
{
    public static NotificationEventResponse From(NotificationEventDefinition definition, IReadOnlyDictionary<string, EventSetting> settings, NotificationTemplate? row)
    {
        var setting = NotificationSettings.For(settings, definition.Type);
        var (en, ar) = NotificationTexts.Default(definition.Type);
        var defaults = new NotificationTemplateDto(
            LocalizedTextDto.From(new(en.Subject, ar.Subject)), LocalizedTextDto.From(new(en.Body, ar.Body)),
            LocalizedTextDto.From(new(en.InApp, ar.InApp)), LocalizedTextDto.From(new(en.Action, ar.Action)));
        var template = row is null
            ? null
            : new NotificationTemplateDto(LocalizedTextDto.From(row.Subject), LocalizedTextDto.From(row.Body), LocalizedTextDto.From(row.InApp), LocalizedTextDto.From(row.Action));
        return new(definition.Type, definition.Audience, definition.EmailOptional, setting.Enabled, setting.Email, definition.AllPlaceholders,
            row is not null, template, defaults, setting.Messaging, setting.Push);
    }
}

/// <summary>Switches of an event.</summary>
public sealed class UpdateNotificationEventRequest
{
    /// <summary>Whether it notifies anyone.</summary>
    public bool Enabled { get; init; }

    /// <summary>Whether it also sends email.</summary>
    public bool Email { get; init; }

    /// <summary>Task E3: whether it also goes by WhatsApp, SMS and Teams to people who chose them (on when left out).</summary>
    public bool? Messaging { get; init; }

    /// <summary>Task J2: whether it also goes by web push to people's devices (unchanged when left out).</summary>
    public bool? Push { get; init; }
}

/// <summary>One field of a template in both languages (either may be empty).</summary>
public sealed class TemplateFieldInput
{
    /// <summary>English.</summary>
    public string? En { get; init; }

    /// <summary>Arabic.</summary>
    public string? Ar { get; init; }

    internal LocalizedText ToValue() => new(En, Ar);
}

/// <summary>The tenant's wording of an event.</summary>
public sealed class NotificationTemplateRequest
{
    /// <summary>Subject line and in-app title.</summary>
    public TemplateFieldInput? Subject { get; init; }

    /// <summary>Email body; blank lines separate paragraphs.</summary>
    public TemplateFieldInput? Body { get; init; }

    /// <summary>In-app text.</summary>
    public TemplateFieldInput? InApp { get; init; }

    /// <summary>Label of the link.</summary>
    public TemplateFieldInput? Action { get; init; }

    internal Dictionary<string, string[]> Validate(NotificationEventDefinition definition, bool requireAll = true)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        Check(errors, "subject", Subject, NotificationTemplate.SubjectMaxLength, definition, requireAll);
        Check(errors, "body", Body, NotificationTemplate.BodyMaxLength, definition, requireAll);
        Check(errors, "inApp", InApp, NotificationTemplate.InAppMaxLength, definition, requireAll);
        Check(errors, "action", Action, NotificationTemplate.ActionMaxLength, definition, requireAll);
        return errors;
    }

    private static void Check(Dictionary<string, string[]> errors, string field, TemplateFieldInput? input, int maxLength, NotificationEventDefinition definition, bool required)
    {
        var value = input?.ToValue() ?? new LocalizedText(null, null);
        var problems = new List<string>();
        if (required && !value.HasAnyValue)
        {
            problems.Add("A value in English or Arabic is required.");
        }

        foreach (var language in LocalizedText.Languages)
        {
            if (value.Get(language) is not { } text)
            {
                continue;
            }

            if (text.Length > maxLength)
            {
                problems.Add($"At most {maxLength} characters in each language.");
            }

            problems.AddRange(TemplateSyntax.Problems(text, definition.AllPlaceholders.ToHashSet(StringComparer.Ordinal)));
        }

        if (problems.Count > 0)
        {
            errors[field] = [.. problems.Distinct(StringComparer.Ordinal)];
        }
    }
}

/// <summary>What to preview.</summary>
public sealed class NotificationPreviewRequest
{
    /// <summary>en or ar.</summary>
    public string? Language { get; init; }

    /// <summary>Unsaved wording to preview; fields left out use the saved wording.</summary>
    public NotificationTemplateRequest? Template { get; init; }
}

/// <summary>A preview.</summary>
/// <param name="Language">Language rendered.</param>
/// <param name="Subject">Subject and in-app title.</param>
/// <param name="InApp">In-app text.</param>
/// <param name="Html">Email HTML.</param>
/// <param name="Text">Email plain text.</param>
internal sealed record NotificationPreviewResponse(string Language, string Subject, string InApp, string Html, string Text);

/// <summary>An email in the delivery log (address masked).</summary>
/// <param name="Id">Id.</param>
/// <param name="EventType">Event.</param>
/// <param name="Address">Address, masked.</param>
/// <param name="Language">en or ar.</param>
/// <param name="Subject">Subject.</param>
/// <param name="Status">Pending, Sent or Failed.</param>
/// <param name="Attempts">Attempts made.</param>
/// <param name="Manual">Queued by an administrator.</param>
/// <param name="LastError">Why the last attempt failed.</param>
/// <param name="CreatedAt">When it was queued.</param>
/// <param name="SentAt">When it was sent.</param>
/// <param name="NextAttemptAt">When it is tried again.</param>
internal sealed record NotificationDeliveryResponse(
    long Id, string EventType, string Address, string Language, string Subject, NotificationDeliveryStatus Status, int Attempts, bool Manual,
    string? LastError, DateTimeOffset CreatedAt, DateTimeOffset? SentAt, DateTimeOffset? NextAttemptAt)
{
    public static NotificationDeliveryResponse From(NotificationDelivery d) => new(
        d.Id, d.EventType, NotificationEndpoints.Mask(d.Address), d.Language, d.Subject, d.Status, d.Attempts, d.Manual, d.LastError, d.CreatedAt, d.SentAt,
        d.NextAttemptAt);
}

/// <summary>An email with its address and content.</summary>
/// <param name="Delivery">The log entry.</param>
/// <param name="Address">The address.</param>
/// <param name="Html">HTML body.</param>
/// <param name="Text">Plain-text body.</param>
internal sealed record NotificationDeliveryDetailResponse(NotificationDeliveryResponse Delivery, string Address, string Html, string Text)
{
    public static NotificationDeliveryDetailResponse From(NotificationDelivery d) =>
        new(NotificationDeliveryResponse.From(d), d.Address, d.HtmlBody, d.TextBody);
}

/// <summary>Missing translations of the tenant's notification wording (LANG-5).</summary>
internal sealed class NotificationMissingTranslations : IMissingTranslationSource
{
    public async Task<IReadOnlyList<MissingTranslation>> FindAsync(ZelvoDbContext db, CancellationToken cancellationToken)
    {
        var rows = await db.NotificationTemplates.AsNoTracking().ToListAsync(cancellationToken);
        var missing = new List<MissingTranslation>();
        foreach (var row in rows)
        {
            foreach (var (field, value) in new[] { ("subject", row.Subject), ("body", row.Body), ("inApp", row.InApp), ("action", row.Action) })
            {
                if (value.HasAnyValue && !value.IsComplete)
                {
                    missing.Add(new MissingTranslation("notification_template", row.EventType, field, value.MissingLanguages));
                }
            }
        }

        return missing;
    }
}

/// <summary>Registers notifications in the API.</summary>
internal static class NotificationSetup
{
    public static IServiceCollection AddZelvoApiNotifications(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddZelvoNotifications(configuration);
        services.AddSingleton<IMissingTranslationSource, NotificationMissingTranslations>();
        return services;
    }
}
