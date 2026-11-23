using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Zelvo.Data;
using Zelvo.Data.Integrations;
using Zelvo.Data.Tenancy;
using Zelvo.Licensing;

namespace Zelvo.Integrations;

/// <summary>
/// Event types webhooks can subscribe to. Each module adds its own (tickets with B1, SLA breaches
/// with B3, approvals with C3); the web app has a translated description for each.
/// </summary>
public static class WebhookEventTypes
{
    /// <summary>A user was created (first sign-in, import or an administrator).</summary>
    public const string UserCreated = "user.created";

    /// <summary>A user's name, email, status, Emirates ID or roles changed.</summary>
    public const string UserUpdated = "user.updated";

    /// <summary>A user was disabled.</summary>
    public const string UserDisabled = "user.disabled";

    /// <summary>A role was added.</summary>
    public const string RoleCreated = "role.created";

    /// <summary>A role's name, second-factor rule, permissions or field rules changed.</summary>
    public const string RoleUpdated = "role.updated";

    /// <summary>A role was removed.</summary>
    public const string RoleDeleted = "role.deleted";

    /// <summary>Sent only by the webhook tester; cannot be subscribed to.</summary>
    public const string Test = "webhook.test";

    /// <summary>Every event type a subscription can choose.</summary>
    public static IReadOnlyList<string> All { get; } = [UserCreated, UserUpdated, UserDisabled, RoleCreated, RoleUpdated, RoleDeleted];

    /// <summary>Whether a subscription can choose <paramref name="type"/>.</summary>
    public static bool IsSubscribable(string type) => All.Contains(type, StringComparer.Ordinal);
}

/// <summary>An event to deliver, found in the changes of one save.</summary>
/// <param name="Type">Event type (<see cref="WebhookEventTypes"/>).</param>
/// <param name="Data">The event's <c>data</c>: ids and non-personal values only; receivers fetch details through the API.</param>
public sealed record WebhookEvent(string Type, object Data);

/// <summary>Finds webhook events in the pending changes of a save. Each module registers one.</summary>
public interface IWebhookEventSource
{
    /// <summary>Events described by the pending changes of <paramref name="context"/>.</summary>
    Task<IReadOnlyList<WebhookEvent>> CollectAsync(ZelvoDbContext context, CancellationToken cancellationToken);
}

/// <summary>The JSON body of every webhook.</summary>
/// <param name="Id">Event id; also the <c>webhook-id</c> header (with <c>msg_</c>).</param>
/// <param name="Type">Event type.</param>
/// <param name="OccurredAt">When the change was saved.</param>
/// <param name="Tenant">The tenant's slug.</param>
/// <param name="Environment"><c>production</c>, <c>test</c>, <c>demo</c> or <c>development</c>.</param>
/// <param name="Data">The event's data.</param>
public sealed record WebhookPayload(Guid Id, string Type, DateTimeOffset OccurredAt, string Tenant, string Environment, object Data)
{
    /// <summary>JSON options for payloads: camelCase, enums as strings.</summary>
    public static JsonSerializerOptions Json { get; } = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>The body as sent.</summary>
    public string Serialize() => JsonSerializer.Serialize(this, Json);
}

/// <summary>
/// Before each save: asks the registered <see cref="IWebhookEventSource"/>s which events the
/// changes describe, and queues one delivery per active subscription of the tenant for each, in
/// the same transaction (so an event is sent if and only if its change was saved).
/// </summary>
public sealed class WebhookEventInterceptor(
    IEnumerable<IWebhookEventSource> sources, ITenantContext tenantContext, ILicenseService licenses)
    : SaveChangesInterceptor, IZelvoSaveChangesInterceptor
{
    private bool _collecting;

    /// <inheritdoc />
    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        ArgumentNullException.ThrowIfNull(eventData);
        CollectAsync(eventData.Context, CancellationToken.None).GetAwaiter().GetResult();
        return result;
    }

    /// <inheritdoc />
    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(eventData);
        await CollectAsync(eventData.Context, cancellationToken).ConfigureAwait(false);
        return result;
    }

    private async Task CollectAsync(DbContext? context, CancellationToken cancellationToken)
    {
        if (_collecting || context is not ZelvoDbContext db || tenantContext.TenantId is not { } tenantId || tenantContext.Slug is not { } slug)
        {
            return;
        }

        _collecting = true;
        try
        {
            db.ChangeTracker.DetectChanges();
            var events = new List<WebhookEvent>();
            foreach (var source in sources)
            {
                events.AddRange(await source.CollectAsync(db, cancellationToken).ConfigureAwait(false));
            }

            if (events.Count == 0)
            {
                return;
            }

            var subscriptions = await db.WebhookSubscriptions.AsNoTracking()
                .Where(s => s.TenantId == tenantId && s.Status == WebhookStatus.Active)
                .Select(s => new { s.Id, s.Events })
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            if (subscriptions.Count == 0)
            {
                return;
            }

            var environment = licenses.EnvironmentName();
            var now = DateTimeOffset.UtcNow;
            foreach (var webhookEvent in events)
            {
                var eventId = Guid.CreateVersion7();
                var payload = new WebhookPayload(eventId, webhookEvent.Type, now, slug, environment, webhookEvent.Data).Serialize();
                foreach (var subscription in subscriptions.Where(s => s.Events.Contains(webhookEvent.Type)))
                {
                    db.WebhookDeliveries.Add(new WebhookDelivery
                    {
                        TenantId = tenantId,
                        SubscriptionId = subscription.Id,
                        EventId = eventId,
                        EventType = webhookEvent.Type,
                        Payload = payload,
                        NextAttemptAt = now,
                    });
                }
            }
        }
        finally
        {
            _collecting = false;
        }
    }
}
