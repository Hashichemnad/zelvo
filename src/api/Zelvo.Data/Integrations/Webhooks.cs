using Zelvo.Data.Auditing;
using Zelvo.Data.Tenancy;

namespace Zelvo.Data.Integrations;

/// <summary>Whether a webhook subscription receives events.</summary>
public enum WebhookStatus
{
    /// <summary>Events are delivered.</summary>
    Active,

    /// <summary>Paused by an administrator: no new deliveries are queued.</summary>
    Paused,

    /// <summary>Switched off by Zelvo after repeated failed deliveries; an administrator turns it on again.</summary>
    Disabled,
}

/// <summary>
/// An outgoing webhook: Zelvo posts the chosen events to <see cref="Url"/>, signed with the
/// subscription's secret (Standard Webhooks format). The secret is stored encrypted
/// (<c>Zelvo.Secrets</c>) because it is needed to sign.
/// </summary>
public sealed class WebhookSubscription : ITenantOwned, IAuditedEntity
{
    /// <summary>Maximum length of <see cref="Name"/>.</summary>
    public const int NameMaxLength = 100;

    /// <summary>Maximum length of <see cref="Url"/>.</summary>
    public const int UrlMaxLength = 2000;

    /// <summary>Maximum length of <see cref="DisabledReason"/>.</summary>
    public const int ReasonMaxLength = 500;

    /// <summary>Maximum number of events per subscription.</summary>
    public const int MaxEvents = 100;

    /// <summary>Unique id (UUID version 7).</summary>
    public Guid Id { get; init; } = Guid.CreateVersion7();

    /// <inheritdoc />
    public Guid TenantId { get; set; }

    /// <summary>Name shown to administrators.</summary>
    public required string Name { get; set; }

    /// <summary>Where events are posted (HTTPS).</summary>
    public required string Url { get; set; }

    /// <summary>Event types delivered, for example <c>user.created</c>.</summary>
    public string[] Events { get; set; } = [];

    /// <summary>Active, paused or disabled.</summary>
    public WebhookStatus Status { get; set; } = WebhookStatus.Active;

    /// <summary>Why Zelvo disabled it.</summary>
    public string? DisabledReason { get; set; }

    /// <summary>The signing secret, encrypted (<c>zv1.…</c>).</summary>
    [SensitiveData]
    public required string SecretCiphertext { get; set; }

    /// <summary>After a rotation: the previous secret, still used to sign until <see cref="PreviousSecretExpiresAt"/>.</summary>
    [SensitiveData]
    public string? PreviousSecretCiphertext { get; set; }

    /// <summary>When the previous secret stops being used.</summary>
    public DateTimeOffset? PreviousSecretExpiresAt { get; set; }

    /// <summary>Deliveries in a row that failed for good; reset by a successful delivery (not audited).</summary>
    public int ConsecutiveFailures { get; set; }

    /// <summary>When it was created.</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>When it was last changed.</summary>
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Optimistic concurrency token (PostgreSQL <c>xmin</c>).</summary>
    public uint Version { get; init; }
}

/// <summary>Where a webhook delivery stands.</summary>
public enum WebhookDeliveryStatus
{
    /// <summary>Waiting for its first or next attempt.</summary>
    Pending,

    /// <summary>The receiver answered with a 2xx status.</summary>
    Succeeded,

    /// <summary>Given up after the last retry, or the subscription was disabled or removed.</summary>
    Failed,
}

/// <summary>
/// One event for one subscription: queued in the same transaction as the change it describes,
/// sent by the worker with retries, and kept as the subscription's delivery log.
/// </summary>
public sealed class WebhookDelivery : ITenantOwned
{
    /// <summary>Maximum length of <see cref="EventType"/>.</summary>
    public const int EventTypeMaxLength = 100;

    /// <summary>Maximum length of <see cref="LastError"/>.</summary>
    public const int ErrorMaxLength = 1000;

    /// <summary>Sequence number.</summary>
    public long Id { get; init; }

    /// <inheritdoc />
    public Guid TenantId { get; set; }

    /// <summary>The subscription.</summary>
    public Guid SubscriptionId { get; init; }

    /// <summary>Id of the event (the <c>webhook-id</c> header); the same for redeliveries, so receivers can drop duplicates.</summary>
    public Guid EventId { get; init; }

    /// <summary>Event type, for example <c>user.created</c>.</summary>
    public required string EventType { get; init; }

    /// <summary>The JSON body that is sent.</summary>
    public required string Payload { get; init; }

    /// <summary>Sent by the webhook tester or "Redeliver" rather than by an event.</summary>
    public bool Manual { get; init; }

    /// <summary>Pending, succeeded or failed.</summary>
    public WebhookDeliveryStatus Status { get; set; } = WebhookDeliveryStatus.Pending;

    /// <summary>Attempts made.</summary>
    public int Attempts { get; set; }

    /// <summary>When the next attempt is due.</summary>
    public DateTimeOffset? NextAttemptAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>A worker is sending it until this time; others leave it alone.</summary>
    public DateTimeOffset? LeaseUntil { get; set; }

    /// <summary>When the last attempt was made.</summary>
    public DateTimeOffset? LastAttemptAt { get; set; }

    /// <summary>HTTP status of the last attempt, if the receiver answered.</summary>
    public int? LastStatusCode { get; set; }

    /// <summary>Why the last attempt failed (never holds the payload).</summary>
    public string? LastError { get; set; }

    /// <summary>Duration of the last attempt in milliseconds.</summary>
    public int? LastDurationMs { get; set; }

    /// <summary>When it was queued.</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>When it succeeded or was given up on.</summary>
    public DateTimeOffset? CompletedAt { get; set; }
}
