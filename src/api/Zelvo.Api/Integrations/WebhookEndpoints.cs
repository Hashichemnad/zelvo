using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;
using Zelvo.Api.Conventions;
using Zelvo.Api.Identity;
using Zelvo.Api.Tenancy;
using Zelvo.Data;
using Zelvo.Data.Auditing;
using Zelvo.Data.Integrations;
using Zelvo.Data.Tenancy;
using Zelvo.Integrations;
using Zelvo.Licensing;
using Zelvo.Secrets;

namespace Zelvo.Api.Integrations;

/// <summary>
/// Settings → Integrations → Webhooks (module 14): subscriptions to events, signed with a secret
/// shown once; the webhook tester; the delivery log with "Redeliver"; and the integration log.
/// </summary>
internal static class WebhookEndpoints
{
    public static RouteGroupBuilder MapWebhookEndpoints(this RouteGroupBuilder group)
    {
        var integrations = group.MapGroup("/integrations").WithTags("Integrations").RequireTenant();
        var webhooks = integrations.MapGroup("/webhooks");

        webhooks.MapGet("/", ListAsync)
            .WithName("ListWebhooks")
            .WithSummary("Webhook subscriptions of the tenant. Needs integrations.manage.")
            .RequirePermission(Permissions.IntegrationsManage)
            .Produces<PageResult<WebhookResponse>>();

        webhooks.MapGet("/{id:guid}", GetAsync)
            .WithName("GetWebhook")
            .WithSummary("One webhook subscription. Needs integrations.manage.")
            .RequirePermission(Permissions.IntegrationsManage)
            .Produces<WebhookResponse>();

        webhooks.MapPost("/", CreateAsync)
            .WithName("CreateWebhook")
            .WithSummary("Subscribes an HTTPS URL to events. Payloads are signed in the Standard Webhooks format (webhook-id, webhook-timestamp, webhook-signature) with the secret in this response, shown only once. Internal addresses are refused unless the installation allows them. Needs integrations.manage; audited.")
            .RequirePermission(Permissions.IntegrationsManage)
            .Produces<WebhookSecretResponse>(StatusCodes.Status201Created)
            .ProducesValidationProblem();

        webhooks.MapPatch("/{id:guid}", UpdateAsync)
            .WithName("UpdateWebhook")
            .WithSummary("Changes a subscription's name, URL or events, or pauses and resumes it (Active turns a disabled subscription on again). Needs integrations.manage; audited.")
            .RequirePermission(Permissions.IntegrationsManage)
            .Produces<WebhookResponse>()
            .ProducesValidationProblem();

        webhooks.MapDelete("/{id:guid}", DeleteAsync)
            .WithName("DeleteWebhook")
            .WithSummary("Removes a subscription and its delivery log. Needs integrations.manage; audited.")
            .RequirePermission(Permissions.IntegrationsManage)
            .Produces(StatusCodes.Status204NoContent);

        webhooks.MapPost("/{id:guid}/rotate-secret", RotateSecretAsync)
            .WithName("RotateWebhookSecret")
            .WithSummary("Gives the subscription a new signing secret (in this response only). For 24 hours each webhook carries signatures with both the new and the previous secret, so the receiver can switch without missing events. Needs integrations.manage; audited.")
            .RequirePermission(Permissions.IntegrationsManage)
            .Produces<WebhookSecretResponse>();

        webhooks.MapPost("/{id:guid}/test", TestAsync)
            .WithName("TestWebhook")
            .WithSummary("Webhook tester: sends a signed webhook.test event to the subscription now and answers with what the receiver did (status, time, the start of its answer with personal data masked). Works for paused and disabled subscriptions too. Needs integrations.manage.")
            .RequirePermission(Permissions.IntegrationsManage)
            .Produces<WebhookTestResponse>();

        webhooks.MapGet("/{id:guid}/deliveries", DeliveriesAsync)
            .WithName("ListWebhookDeliveries")
            .WithSummary("Delivery log of a subscription, newest first: event, attempts, last status, error and the payload with personal data masked. Needs integrations.manage.")
            .RequirePermission(Permissions.IntegrationsManage)
            .Produces<PageResult<WebhookDeliveryResponse>>();

        webhooks.MapPost("/{id:guid}/deliveries/{deliveryId:long}/redeliver", RedeliverAsync)
            .WithName("RedeliverWebhook")
            .WithSummary("Sends a delivery again (same event id, so receivers can drop duplicates). The worker sends it within seconds. Needs integrations.manage; audited.")
            .RequirePermission(Permissions.IntegrationsManage)
            .Produces<WebhookDeliveryResponse>(StatusCodes.Status202Accepted);

        var log = integrations.MapGroup("/log");
        log.MapGet("/", LogAsync)
            .WithName("ListIntegrationLog")
            .WithSummary("Integration log, newest first: every API call made by an integration and every webhook sent, with status and time. Filter by direction, integration, subscription or failures. Needs integrations.manage.")
            .RequirePermission(Permissions.IntegrationsManage)
            .Produces<PageResult<IntegrationLogResponse>>();

        log.MapGet("/{id:long}", LogEntryAsync)
            .WithName("GetIntegrationLogEntry")
            .WithSummary("One integration log entry with its request and response bodies (personal data masked). Needs integrations.manage.")
            .RequirePermission(Permissions.IntegrationsManage)
            .Produces<IntegrationLogDetailResponse>();

        return group;
    }

    internal static async Task<IResult> ListAsync([AsParameters] PageQuery page, ZelvoDbContext db, CancellationToken cancellationToken)
    {
        var query = db.WebhookSubscriptions.AsNoTracking().OrderByDescending(s => s.CreatedAt).ThenByDescending(s => s.Id);
        var total = await query.LongCountAsync(cancellationToken);
        var items = await query.Skip(page.Skip).Take(page.Size).ToListAsync(cancellationToken);
        return TypedResults.Ok(new PageResult<WebhookResponse>([.. items.Select(WebhookResponse.From)], page.Number, page.Size, total));
    }

    internal static async Task<IResult> GetAsync(Guid id, ZelvoDbContext db, CancellationToken cancellationToken) =>
        await db.WebhookSubscriptions.AsNoTracking().SingleOrDefaultAsync(s => s.Id == id, cancellationToken) is { } subscription
            ? TypedResults.Ok(WebhookResponse.From(subscription))
            : NotFound();

    internal static async Task<IResult> CreateAsync(
        CreateWebhookRequest request, ZelvoDbContext db, OutboundAddressPolicy policy, ISecretProtector secrets, IAuditLog audit,
        CancellationToken cancellationToken)
    {
        if (CheckUrl(policy, request.Url, out var url) is { } invalidUrl)
        {
            return invalidUrl;
        }

        if (CheckEvents(request.Events!) is { } invalidEvents)
        {
            return invalidEvents;
        }

        var id = Guid.CreateVersion7();
        var secret = WebhookSigner.NewSecret();
        var subscription = new WebhookSubscription
        {
            Id = id,
            Name = request.Name!.Trim(),
            Url = url!.AbsoluteUri,
            Events = [.. request.Events!.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)],
            SecretCiphertext = await secrets.ProtectAsync(WebhookDeliveryService.SecretPurpose(id), secret, cancellationToken),
        };
        db.WebhookSubscriptions.Add(subscription);
        audit.Add(AuditCategory.Admin, "integrations.webhook.created", "webhook_subscription", id.ToString());
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Created($"/api/v1/integrations/webhooks/{id}", new WebhookSecretResponse(WebhookResponse.From(subscription), secret));
    }

    internal static async Task<IResult> UpdateAsync(
        Guid id, UpdateWebhookRequest request, ZelvoDbContext db, OutboundAddressPolicy policy, CancellationToken cancellationToken)
    {
        var subscription = await db.WebhookSubscriptions.SingleOrDefaultAsync(s => s.Id == id, cancellationToken);
        if (subscription is null)
        {
            return NotFound();
        }

        if (request.Url is not null)
        {
            if (CheckUrl(policy, request.Url, out var url) is { } invalidUrl)
            {
                return invalidUrl;
            }

            subscription.Url = url!.AbsoluteUri;
        }

        if (request.Events is not null)
        {
            if (CheckEvents(request.Events) is { } invalidEvents)
            {
                return invalidEvents;
            }

            subscription.Events = [.. request.Events.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
        }

        if (request.Name is not null)
        {
            subscription.Name = request.Name.Trim();
        }

        if (request.Status is { } status)
        {
            if (status == WebhookStatus.Disabled)
            {
                return UserEndpoints.Validation("status", "Use Active or Paused; Zelvo disables subscriptions itself after repeated failures.");
            }

            if (status == WebhookStatus.Active && subscription.Status != WebhookStatus.Active)
            {
                subscription.DisabledReason = null;
                subscription.ConsecutiveFailures = 0;
            }

            subscription.Status = status;
        }

        subscription.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Ok(WebhookResponse.From(subscription));
    }

    internal static async Task<IResult> DeleteAsync(Guid id, ZelvoDbContext db, IAuditLog audit, CancellationToken cancellationToken)
    {
        var subscription = await db.WebhookSubscriptions.SingleOrDefaultAsync(s => s.Id == id, cancellationToken);
        if (subscription is null)
        {
            return NotFound();
        }

        db.WebhookSubscriptions.Remove(subscription);
        audit.Add(AuditCategory.Admin, "integrations.webhook.deleted", "webhook_subscription", id.ToString());
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.NoContent();
    }

    internal static async Task<IResult> RotateSecretAsync(
        Guid id, ZelvoDbContext db, ISecretProtector secrets, IntegrationOptions options, IAuditLog audit, CancellationToken cancellationToken)
    {
        var subscription = await db.WebhookSubscriptions.SingleOrDefaultAsync(s => s.Id == id, cancellationToken);
        if (subscription is null)
        {
            return NotFound();
        }

        var secret = WebhookSigner.NewSecret();
        var now = DateTimeOffset.UtcNow;
        subscription.PreviousSecretCiphertext = subscription.SecretCiphertext;
        subscription.PreviousSecretExpiresAt = now + options.Webhooks.SecretRotationOverlap;
        subscription.SecretCiphertext = await secrets.ProtectAsync(WebhookDeliveryService.SecretPurpose(id), secret, cancellationToken);
        subscription.UpdatedAt = now;
        audit.Add(AuditCategory.Security, "integrations.webhook.secret-rotated", "webhook_subscription", id.ToString());
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Ok(new WebhookSecretResponse(WebhookResponse.From(subscription), secret));
    }

    internal static async Task<IResult> TestAsync(
        Guid id, ZelvoDbContext db, ITenantContext tenant, ILicenseService licenses, WebhookDeliveryService deliveries,
        CancellationToken cancellationToken)
    {
        if (!await db.WebhookSubscriptions.AnyAsync(s => s.Id == id, cancellationToken))
        {
            return NotFound();
        }

        var eventId = Guid.CreateVersion7();
        var payload = new WebhookPayload(eventId, WebhookEventTypes.Test, DateTimeOffset.UtcNow, tenant.Slug!, licenses.EnvironmentName(),
            new { message = "Test event from Settings → Integrations → Webhooks.", subscriptionId = id }).Serialize();
        var delivery = new WebhookDelivery
        {
            SubscriptionId = id,
            EventId = eventId,
            EventType = WebhookEventTypes.Test,
            Payload = payload,
            Manual = true,
            // Sent below by this request, never by the worker.
            NextAttemptAt = null,
            LeaseUntil = DateTimeOffset.UtcNow.AddMinutes(2),
        };
        db.WebhookDeliveries.Add(delivery);
        await db.SaveChangesAsync(cancellationToken);
        db.ChangeTracker.Clear();

        var result = await deliveries.AttemptAsync(delivery.Id, cancellationToken);
        return result is null
            ? NotFound()
            : TypedResults.Ok(new WebhookTestResponse(result.Succeeded, result.StatusCode, result.DurationMs, result.Error,
                PersonalDataMasker.MaskBody(result.ResponseBody, result.ResponseContentType, WebhookSender.MaxResponseBytes), delivery.Id));
    }

    internal static async Task<IResult> DeliveriesAsync(
        Guid id, [AsParameters] PageQuery page, [AsParameters] DeliveryListQuery filter, ZelvoDbContext db, CancellationToken cancellationToken)
    {
        if (!await db.WebhookSubscriptions.AnyAsync(s => s.Id == id, cancellationToken))
        {
            return NotFound();
        }

        var query = db.WebhookDeliveries.AsNoTracking().Where(d => d.SubscriptionId == id);
        if (filter.Status is { } status)
        {
            query = query.Where(d => d.Status == status);
        }

        query = query.OrderByDescending(d => d.Id);
        var total = await query.LongCountAsync(cancellationToken);
        var items = await query.Skip(page.Skip).Take(page.Size).ToListAsync(cancellationToken);
        return TypedResults.Ok(new PageResult<WebhookDeliveryResponse>([.. items.Select(WebhookDeliveryResponse.From)], page.Number, page.Size, total));
    }

    internal static async Task<IResult> RedeliverAsync(
        Guid id, long deliveryId, ZelvoDbContext db, IAuditLog audit, CancellationToken cancellationToken)
    {
        var original = await db.WebhookDeliveries.AsNoTracking()
            .SingleOrDefaultAsync(d => d.Id == deliveryId && d.SubscriptionId == id, cancellationToken);
        if (original is null)
        {
            return ApiProblem.Create(StatusCodes.Status404NotFound, "delivery-not-found", "The delivery does not exist.");
        }

        if (original.EventType == WebhookEventTypes.Test)
        {
            return ApiProblem.Create(StatusCodes.Status409Conflict, "delivery-is-test", "Use the webhook tester to send a test event again.");
        }

        var copy = new WebhookDelivery
        {
            SubscriptionId = id,
            EventId = original.EventId,
            EventType = original.EventType,
            Payload = original.Payload,
            Manual = true,
        };
        db.WebhookDeliveries.Add(copy);
        audit.Add(AuditCategory.Admin, "integrations.webhook.redelivered", "webhook_subscription", id.ToString(),
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["deliveryId"] = deliveryId, ["eventId"] = original.EventId.ToString() });
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Accepted($"/api/v1/integrations/webhooks/{id}/deliveries", WebhookDeliveryResponse.From(copy));
    }

    internal static async Task<IResult> LogAsync(
        [AsParameters] PageQuery page, [AsParameters] IntegrationLogQuery filter, ZelvoDbContext db, CancellationToken cancellationToken)
    {
        var query = db.IntegrationLog.AsNoTracking();
        if (filter.Direction is { } direction)
        {
            query = query.Where(e => e.Direction == direction);
        }

        if (filter.ApiClientId is { } clientId)
        {
            query = query.Where(e => e.ApiClientId == clientId);
        }

        if (filter.SubscriptionId is { } subscriptionId)
        {
            query = query.Where(e => e.SubscriptionId == subscriptionId);
        }

        if (filter.Failed == true)
        {
            query = query.Where(e => e.StatusCode == null || e.StatusCode >= 400);
        }

        query = query.OrderByDescending(e => e.Id);
        var total = await query.LongCountAsync(cancellationToken);
        var items = await query.Skip(page.Skip).Take(page.Size).ToListAsync(cancellationToken);
        var names = await NamesAsync(db, items, cancellationToken);
        return TypedResults.Ok(new PageResult<IntegrationLogResponse>(
            [.. items.Select(e => IntegrationLogResponse.From(e, names))], page.Number, page.Size, total));
    }

    internal static async Task<IResult> LogEntryAsync(long id, ZelvoDbContext db, CancellationToken cancellationToken)
    {
        var entry = await db.IntegrationLog.AsNoTracking().SingleOrDefaultAsync(e => e.Id == id, cancellationToken);
        if (entry is null)
        {
            return ApiProblem.Create(StatusCodes.Status404NotFound, "log-entry-not-found", "The log entry does not exist or is past its retention.");
        }

        var names = await NamesAsync(db, [entry], cancellationToken);
        return TypedResults.Ok(new IntegrationLogDetailResponse(IntegrationLogResponse.From(entry, names), entry.RequestBody, entry.ResponseBody));
    }

    private static async Task<Dictionary<Guid, string>> NamesAsync(ZelvoDbContext db, IReadOnlyList<IntegrationLogEntry> entries, CancellationToken cancellationToken)
    {
        var clientIds = entries.Where(e => e.ApiClientId is not null).Select(e => e.ApiClientId!.Value).Distinct().ToList();
        var subscriptionIds = entries.Where(e => e.SubscriptionId is not null).Select(e => e.SubscriptionId!.Value).Distinct().ToList();
        var names = await db.ApiClients.AsNoTracking().Where(c => clientIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Name, cancellationToken);
        foreach (var (key, name) in await db.WebhookSubscriptions.AsNoTracking().Where(s => subscriptionIds.Contains(s.Id))
                     .ToDictionaryAsync(s => s.Id, s => s.Name, cancellationToken))
        {
            names[key] = name;
        }

        return names;
    }

    private static Microsoft.AspNetCore.Http.HttpResults.ValidationProblem? CheckUrl(OutboundAddressPolicy policy, string? value, out Uri? url) =>
        policy.CheckUrl(value, out url) switch
        {
            null => null,
            "url-not-https" => UrlProblem("url-not-https", "Use an https:// address."),
            "address-not-allowed" => UrlProblem("address-not-allowed", "Webhooks cannot be sent to this address (this server, internal or reserved networks)."),
            _ => UrlProblem("url-invalid", "Use a full https:// address without user name, password or #fragment."),
        };

    private static Microsoft.AspNetCore.Http.HttpResults.ValidationProblem UrlProblem(string code, string message) =>
        TypedResults.ValidationProblem(
            new Dictionary<string, string[]>(StringComparer.Ordinal) { ["url"] = [message] },
            type: ApiProblem.TypeFor("validation-failed"),
            extensions: new Dictionary<string, object?>(StringComparer.Ordinal) { ["code"] = "validation-failed", ["urlProblem"] = code });

    private static Microsoft.AspNetCore.Http.HttpResults.ValidationProblem? CheckEvents(IReadOnlyList<string> events)
    {
        var unknown = events.Where(e => !WebhookEventTypes.IsSubscribable(e)).ToList();
        return events.Count == 0
            ? UserEndpoints.Validation("events", "Choose at least one event.")
            : unknown.Count > 0
                ? UserEndpoints.Validation("events", $"Unknown events: {string.Join(", ", unknown)}.")
                : null;
    }

    private static IResult NotFound() =>
        ApiProblem.Create(StatusCodes.Status404NotFound, "webhook-not-found", "The webhook subscription does not exist.");
}

/// <summary>A new webhook subscription.</summary>
public sealed class CreateWebhookRequest
{
    /// <summary>Name, 1 to 100 characters.</summary>
    [Required]
    [StringLength(WebhookSubscription.NameMaxLength, MinimumLength = 1)]
    public string? Name { get; init; }

    /// <summary>Receiver URL (https).</summary>
    [Required]
    [StringLength(WebhookSubscription.UrlMaxLength, MinimumLength = 1)]
    public string? Url { get; init; }

    /// <summary>Event types, for example <c>["user.created"]</c>.</summary>
    [Required]
    [MaxLength(WebhookSubscription.MaxEvents)]
    public IReadOnlyList<string>? Events { get; init; }
}

/// <summary>Changes to a subscription. Leave a field out to keep it.</summary>
public sealed class UpdateWebhookRequest
{
    /// <summary>Name, 1 to 100 characters.</summary>
    [StringLength(WebhookSubscription.NameMaxLength, MinimumLength = 1)]
    public string? Name { get; init; }

    /// <summary>Receiver URL (https).</summary>
    [StringLength(WebhookSubscription.UrlMaxLength, MinimumLength = 1)]
    public string? Url { get; init; }

    /// <summary>The complete list of event types.</summary>
    [MaxLength(WebhookSubscription.MaxEvents)]
    public IReadOnlyList<string>? Events { get; init; }

    /// <summary><c>Active</c> or <c>Paused</c>.</summary>
    public WebhookStatus? Status { get; init; }
}

/// <summary>Filters of the delivery log.</summary>
public sealed class DeliveryListQuery
{
    /// <summary>Only deliveries with this status.</summary>
    public WebhookDeliveryStatus? Status { get; init; }
}

/// <summary>Filters of the integration log.</summary>
public sealed class IntegrationLogQuery
{
    /// <summary><c>Incoming</c> (API calls) or <c>Outgoing</c> (webhooks).</summary>
    public IntegrationDirection? Direction { get; init; }

    /// <summary>Calls of one integration.</summary>
    public Guid? ApiClientId { get; init; }

    /// <summary>Webhooks of one subscription.</summary>
    public Guid? SubscriptionId { get; init; }

    /// <summary>True: only calls that failed (no answer, or status 400 and above).</summary>
    public bool? Failed { get; init; }
}

/// <summary>A webhook subscription. The secret is never included.</summary>
/// <param name="Id">Id.</param>
/// <param name="Name">Name.</param>
/// <param name="Url">Receiver URL.</param>
/// <param name="Events">Event types.</param>
/// <param name="Status"><c>Active</c>, <c>Paused</c> or <c>Disabled</c>.</param>
/// <param name="DisabledReason">Why Zelvo disabled it.</param>
/// <param name="ConsecutiveFailures">Deliveries in a row that failed for good.</param>
/// <param name="SecretRotating">A previous secret is still used to sign, until <paramref name="PreviousSecretExpiresAt"/>.</param>
/// <param name="PreviousSecretExpiresAt">When the previous secret stops being used.</param>
/// <param name="CreatedAt">When it was created.</param>
/// <param name="UpdatedAt">When it was last changed.</param>
internal sealed record WebhookResponse(
    Guid Id, string Name, string Url, IReadOnlyList<string> Events, WebhookStatus Status, string? DisabledReason, int ConsecutiveFailures,
    bool SecretRotating, DateTimeOffset? PreviousSecretExpiresAt, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt)
{
    public static WebhookResponse From(WebhookSubscription s)
    {
        var rotating = s.PreviousSecretCiphertext is not null && s.PreviousSecretExpiresAt > DateTimeOffset.UtcNow;
        return new(s.Id, s.Name, s.Url, s.Events, s.Status, s.DisabledReason, s.ConsecutiveFailures,
            rotating, rotating ? s.PreviousSecretExpiresAt : null, s.CreatedAt, s.UpdatedAt);
    }
}

/// <summary>A subscription with its new signing secret, shown this once.</summary>
/// <param name="Webhook">The subscription.</param>
/// <param name="Secret">Signing secret (<c>whsec_…</c>).</param>
internal sealed record WebhookSecretResponse(WebhookResponse Webhook, string Secret);

/// <summary>What the receiver did with the test event.</summary>
/// <param name="Succeeded">It answered with a 2xx status.</param>
/// <param name="StatusCode">Its status, if it answered.</param>
/// <param name="DurationMs">How long it took.</param>
/// <param name="Error">Why it failed: a code (<c>timeout</c>, <c>http-500</c>, <c>address-not-allowed</c>, <c>connection-failed</c>…) and an explanation.</param>
/// <param name="ResponseBody">The start of its answer, personal data masked.</param>
/// <param name="DeliveryId">The test delivery in the delivery log.</param>
internal sealed record WebhookTestResponse(bool Succeeded, int? StatusCode, int DurationMs, string? Error, string? ResponseBody, long DeliveryId);

/// <summary>One delivery of the delivery log.</summary>
/// <param name="Id">Id.</param>
/// <param name="EventId">Event id (the <c>webhook-id</c> header, with <c>msg_</c>).</param>
/// <param name="EventType">Event type.</param>
/// <param name="Status"><c>Pending</c>, <c>Succeeded</c> or <c>Failed</c>.</param>
/// <param name="Manual">Sent by the tester or "Redeliver".</param>
/// <param name="Attempts">Attempts made.</param>
/// <param name="NextAttemptAt">When it is tried next.</param>
/// <param name="LastAttemptAt">When it was last tried.</param>
/// <param name="LastStatusCode">Receiver's last status.</param>
/// <param name="LastDurationMs">Duration of the last attempt.</param>
/// <param name="LastError">Why the last attempt failed.</param>
/// <param name="CreatedAt">When it was queued.</param>
/// <param name="CompletedAt">When it succeeded or was given up on.</param>
/// <param name="Payload">The body sent, personal data masked.</param>
internal sealed record WebhookDeliveryResponse(
    long Id, Guid EventId, string EventType, WebhookDeliveryStatus Status, bool Manual, int Attempts, DateTimeOffset? NextAttemptAt,
    DateTimeOffset? LastAttemptAt, int? LastStatusCode, int? LastDurationMs, string? LastError, DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt, string? Payload)
{
    public static WebhookDeliveryResponse From(WebhookDelivery d) => new(
        d.Id, d.EventId, d.EventType, d.Status, d.Manual, d.Attempts, d.NextAttemptAt, d.LastAttemptAt, d.LastStatusCode, d.LastDurationMs,
        d.LastError, d.CreatedAt, d.CompletedAt, PersonalDataMasker.MaskBody(d.Payload, "application/json", IntegrationLogEntry.BodyMaxLength));
}

/// <summary>One call in or out.</summary>
/// <param name="Id">Id.</param>
/// <param name="OccurredAt">When it started.</param>
/// <param name="Direction"><c>Incoming</c> (API call by an integration) or <c>Outgoing</c> (webhook).</param>
/// <param name="ApiClientId">Incoming: the integration.</param>
/// <param name="SubscriptionId">Outgoing: the subscription.</param>
/// <param name="Name">Name of the integration or subscription.</param>
/// <param name="EventType">Outgoing: the event type.</param>
/// <param name="Method">HTTP method.</param>
/// <param name="Target">Path (query values masked) or receiver URL.</param>
/// <param name="StatusCode">HTTP status, if there was an answer.</param>
/// <param name="DurationMs">Duration.</param>
/// <param name="Error">Why it failed, when it did not reach the other side.</param>
/// <param name="CorrelationId">Trace id.</param>
internal sealed record IntegrationLogResponse(
    long Id, DateTimeOffset OccurredAt, IntegrationDirection Direction, Guid? ApiClientId, Guid? SubscriptionId, string? Name, string? EventType,
    string Method, string Target, int? StatusCode, int DurationMs, string? Error, string? CorrelationId)
{
    public static IntegrationLogResponse From(IntegrationLogEntry e, IReadOnlyDictionary<Guid, string> names) => new(
        e.Id, e.OccurredAt, e.Direction, e.ApiClientId, e.SubscriptionId,
        (e.ApiClientId ?? e.SubscriptionId) is { } owner ? names.GetValueOrDefault(owner) : null,
        e.EventType, e.Method, e.Target, e.StatusCode, e.DurationMs, e.Error, e.CorrelationId);
}

/// <summary>A log entry with its bodies.</summary>
/// <param name="Entry">The entry.</param>
/// <param name="RequestBody">Request body, personal data masked.</param>
/// <param name="ResponseBody">Response body, personal data masked.</param>
internal sealed record IntegrationLogDetailResponse(IntegrationLogResponse Entry, string? RequestBody, string? ResponseBody);
