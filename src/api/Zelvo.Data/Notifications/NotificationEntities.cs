using Zelvo.Data.Auditing;
using Zelvo.Data.Localization;
using Zelvo.Data.Tenancy;

namespace Zelvo.Data.Notifications;

/// <summary>Where a notification stands.</summary>
public enum NotificationStatus
{
    /// <summary>Queued with the change that caused it; the worker has not rendered it yet.</summary>
    Pending,

    /// <summary>Rendered in the recipient's language and shown in their in-app list; email queued when wanted.</summary>
    Delivered,

    /// <summary>Not shown: the event is switched off, or the recipient is disabled or gone.</summary>
    Suppressed,
}

/// <summary>
/// One notification for one person (module 10, task B5): queued in the same transaction as the
/// change it describes, then rendered by the worker in the recipient's language (LANG-6) and shown
/// in their in-app list. Email copies are <see cref="NotificationDelivery"/> rows. Not audited:
/// Zelvo writes these itself, and the change that caused them is audited.
/// </summary>
public sealed class Notification : ITenantOwned
{
    /// <summary>Maximum length of <see cref="EventType"/>.</summary>
    public const int EventTypeMaxLength = 100;

    /// <summary>Maximum length of <see cref="LinkPath"/>.</summary>
    public const int LinkPathMaxLength = 500;

    /// <summary>Maximum length of <see cref="Title"/>.</summary>
    public const int TitleMaxLength = 300;

    /// <summary>Maximum length of <see cref="Text"/>.</summary>
    public const int TextMaxLength = 1000;

    /// <summary>Unique id (UUID version 7).</summary>
    public Guid Id { get; init; } = Guid.CreateVersion7();

    /// <inheritdoc />
    public Guid TenantId { get; set; }

    /// <summary>Event type, for example <c>incident.assigned</c>.</summary>
    public required string EventType { get; init; }

    /// <summary>The person notified.</summary>
    public Guid RecipientId { get; init; }

    /// <summary>Placeholder values captured with the change (JSON object; bilingual values keep both languages).</summary>
    public required string Data { get; init; }

    /// <summary>Path in the web app without the language, for example <c>/my-incidents/…</c>.</summary>
    public string? LinkPath { get; init; }

    /// <summary>The ticket it is about.</summary>
    public Guid? TicketId { get; init; }

    /// <summary>The comment it is about (task E2: a staff reply going out from the ticket's mailbox).</summary>
    public Guid? CommentId { get; init; }

    /// <summary>The knowledge article it is about.</summary>
    public Guid? ArticleId { get; init; }

    /// <summary>The SLA alert it delivers (task B3).</summary>
    public Guid? SlaAlertId { get; init; }

    /// <summary>Pending, delivered or suppressed.</summary>
    public NotificationStatus Status { get; set; } = NotificationStatus.Pending;

    /// <summary>Rendering attempts that failed.</summary>
    public int Attempts { get; set; }

    /// <summary>A worker is rendering it until this time.</summary>
    public DateTimeOffset? LeaseUntil { get; set; }

    /// <summary>When it was queued.</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>When the worker rendered or suppressed it.</summary>
    public DateTimeOffset? ProcessedAt { get; set; }

    /// <summary>Language it was rendered in (<c>en</c> or <c>ar</c>).</summary>
    public string? Language { get; set; }

    /// <summary>Title shown in the in-app list (the email subject).</summary>
    public string? Title { get; set; }

    /// <summary>Short text shown in the in-app list.</summary>
    public string? Text { get; set; }

    /// <summary>When the recipient marked it read.</summary>
    public DateTimeOffset? ReadAt { get; set; }
}

/// <summary>Where an email stands.</summary>
public enum NotificationDeliveryStatus
{
    /// <summary>Waiting for its first or next attempt.</summary>
    Pending,

    /// <summary>Accepted by the mail server.</summary>
    Sent,

    /// <summary>Given up after the last retry, or refused by the mail server for good.</summary>
    Failed,
}

/// <summary>
/// One email of a notification (or a test email): rendered by the worker, sent with retries and
/// kept as the delivery log for administrators for <c>Notifications:DeliveryRetention</c>.
/// </summary>
public sealed class NotificationDelivery : ITenantOwned
{
    /// <summary>Maximum length of <see cref="Subject"/>.</summary>
    public const int SubjectMaxLength = 300;

    /// <summary>Maximum length of each body.</summary>
    public const int BodyMaxLength = 200_000;

    /// <summary>Maximum length of <see cref="LastError"/>.</summary>
    public const int ErrorMaxLength = 1000;

    /// <summary>Sequence number.</summary>
    public long Id { get; init; }

    /// <inheritdoc />
    public Guid TenantId { get; set; }

    /// <summary>The notification; <see langword="null"/> for test emails.</summary>
    public Guid? NotificationId { get; init; }

    /// <summary>The person it was sent to.</summary>
    public Guid? RecipientId { get; init; }

    /// <summary>Event type (<c>notifications.test</c> for test emails).</summary>
    public required string EventType { get; init; }

    /// <summary>The email address.</summary>
    [SensitiveData]
    public required string Address { get; init; }

    /// <summary>Language of the email.</summary>
    public required string Language { get; init; }

    /// <summary>Subject line.</summary>
    public required string Subject { get; init; }

    /// <summary>HTML body.</summary>
    public required string HtmlBody { get; init; }

    /// <summary>Plain-text body.</summary>
    public required string TextBody { get; init; }

    /// <summary>Queued by an administrator ("Send again" or a test email).</summary>
    public bool Manual { get; init; }

    /// <summary>Pending, sent or failed.</summary>
    public NotificationDeliveryStatus Status { get; set; } = NotificationDeliveryStatus.Pending;

    /// <summary>Attempts made.</summary>
    public int Attempts { get; set; }

    /// <summary>When the next attempt is due.</summary>
    public DateTimeOffset? NextAttemptAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>A worker is sending it until this time.</summary>
    public DateTimeOffset? LeaseUntil { get; set; }

    /// <summary>When the last attempt was made.</summary>
    public DateTimeOffset? LastAttemptAt { get; set; }

    /// <summary>Why the last attempt failed (the mail server's answer, never the body).</summary>
    public string? LastError { get; set; }

    /// <summary>When it was queued.</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>When the mail server accepted it.</summary>
    public DateTimeOffset? SentAt { get; set; }
}

/// <summary>
/// A tenant's own wording of one notification (LANG-5, GOV-8): subject, email body, in-app text and
/// the label of the link, in English and Arabic. Events without a row use Zelvo's default wording. Audited.
/// </summary>
public sealed class NotificationTemplate : ITenantOwned, IAuditedEntity
{
    /// <summary>Maximum length of each language of <see cref="Subject"/>.</summary>
    public const int SubjectMaxLength = 200;

    /// <summary>Maximum length of each language of <see cref="Body"/>.</summary>
    public const int BodyMaxLength = 4000;

    /// <summary>Maximum length of each language of <see cref="Action"/>.</summary>
    public const int ActionMaxLength = 100;

    /// <summary>Maximum length of each language of <see cref="InApp"/>.</summary>
    public const int InAppMaxLength = 500;

    /// <inheritdoc />
    public Guid TenantId { get; set; }

    /// <summary>Event type.</summary>
    public required string EventType { get; init; }

    /// <summary>Subject line, also the in-app title.</summary>
    public required LocalizedText Subject { get; set; }

    /// <summary>Email body: plain text with <c>{{placeholders}}</c>; blank lines separate paragraphs.</summary>
    public required LocalizedText Body { get; set; }

    /// <summary>Short text of the in-app notification.</summary>
    public required LocalizedText InApp { get; set; }

    /// <summary>Label of the link to the ticket or article.</summary>
    public required LocalizedText Action { get; set; }

    /// <summary>When it was last changed.</summary>
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Optimistic concurrency token (PostgreSQL <c>xmin</c>).</summary>
    public uint Version { get; init; }
}

/// <summary>
/// A person's choice for the email of one event (product owner decision 2026-11-24: email can be
/// turned off per event, in-app always stays on, SLA alerts cannot be turned off). Only events the
/// person changed have a row. Audited.
/// </summary>
public sealed class NotificationPreference : ITenantOwned, IAuditedEntity
{
    /// <inheritdoc />
    public Guid TenantId { get; set; }

    /// <summary>The person.</summary>
    public Guid UserId { get; init; }

    /// <summary>Event type.</summary>
    public required string EventType { get; init; }

    /// <summary>Whether they get an email for it.</summary>
    public bool Email { get; set; }

    /// <summary>Task J2: whether it also comes by web push to their devices (on unless they turned it off).</summary>
    public bool Push { get; set; } = true;

    /// <summary>When it was last changed.</summary>
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// The tenant's web push key pair (module 19, task J2; RFC 8292 VAPID): made by Zelvo the first time
/// someone subscribes a device, the private key encrypted with <c>Zelvo.Secrets</c>. Browsers tie
/// their subscriptions to the public key, so it is never replaced while subscriptions exist.
/// </summary>
public sealed class WebPushKey : ITenantOwned, IAuditedEntity
{
    /// <inheritdoc />
    public Guid TenantId { get; set; }

    /// <summary>Public key: the uncompressed P-256 point, base64url (the browser's <c>applicationServerKey</c>).</summary>
    public required string PublicKey { get; init; }

    /// <summary>Private key (PKCS #8, base64url) encrypted with the tenant's data key (<c>Zelvo.Secrets</c>, purpose <c>web-push.vapid</c>).</summary>
    [SensitiveData]
    public required string PrivateKey { get; init; }

    /// <summary>When it was made.</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// One browser or installed app of one person that receives web push (task J2): the push
/// service's address and the browser's keys (RFC 8291). Removed when the person removes the
/// device, signs out on it, or the push service says the subscription is gone (404 or 410).
/// </summary>
public sealed class PushSubscription : ITenantOwned, IAuditedEntity
{
    /// <summary>Maximum length of <see cref="Endpoint"/>.</summary>
    public const int EndpointMaxLength = 2048;

    /// <summary>Maximum length of <see cref="DeviceName"/>.</summary>
    public const int DeviceNameMaxLength = 100;

    /// <summary>Unique id (UUID version 7).</summary>
    public Guid Id { get; init; } = Guid.CreateVersion7();

    /// <inheritdoc />
    public Guid TenantId { get; set; }

    /// <summary>The person.</summary>
    public Guid UserId { get; init; }

    /// <summary>The push service's address for this browser (a capability: whoever has it can send to the device).</summary>
    [SensitiveData]
    public required string Endpoint { get; init; }

    /// <summary>The browser's P-256 public key, base64url.</summary>
    [SensitiveData]
    public required string P256dh { get; set; }

    /// <summary>The browser's 16-byte auth secret, base64url.</summary>
    [SensitiveData]
    public required string Auth { get; set; }

    /// <summary>A name the person recognises, for example "Chrome on Android", from the browser.</summary>
    public required string DeviceName { get; set; }

    /// <summary>When it was added.</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>When a message last reached the push service for it.</summary>
    public DateTimeOffset? LastSuccessAt { get; set; }
}

/// <summary>Where a web push message stands.</summary>
public enum PushDeliveryStatus
{
    /// <summary>Waiting to be sent, or to be tried again.</summary>
    Pending,

    /// <summary>The push service accepted it.</summary>
    Sent,

    /// <summary>Given up: refused, or still failing after the last retry.</summary>
    Failed,
}

/// <summary>
/// One web push message for one device (task J2): the queue and the delivery log, kept 30 days
/// like emails. The text holds no personal data beyond the event and the reference number
/// (product owner decision 2026-11-26).
/// </summary>
public sealed class PushDelivery : ITenantOwned
{
    /// <summary>Maximum length of <see cref="Title"/>.</summary>
    public const int TitleMaxLength = 200;

    /// <summary>Maximum length of <see cref="Body"/>.</summary>
    public const int BodyMaxLength = 200;

    /// <summary>Maximum length of <see cref="LastError"/>.</summary>
    public const int ErrorMaxLength = 1000;

    /// <summary>Id.</summary>
    public long Id { get; init; }

    /// <inheritdoc />
    public Guid TenantId { get; set; }

    /// <summary>The notification it carries (none for a test).</summary>
    public Guid? NotificationId { get; init; }

    /// <summary>The device.</summary>
    public Guid? SubscriptionId { get; set; }

    /// <summary>The person.</summary>
    public Guid? RecipientId { get; init; }

    /// <summary>Event type, or <c>push.test</c>.</summary>
    public required string EventType { get; init; }

    /// <summary>Language of the text.</summary>
    public required string Language { get; init; }

    /// <summary>Title: what happened, in the recipient's language.</summary>
    public required string Title { get; init; }

    /// <summary>Body: the reference number, or the test text.</summary>
    public required string Body { get; init; }

    /// <summary>Page in the web app it opens, with the language (for example <c>/en/my-incidents/…</c>).</summary>
    public string? Url { get; init; }

    /// <summary>Messages with the same tag replace each other on the device (one per ticket).</summary>
    public string? Tag { get; init; }

    /// <summary>Pending, sent or failed.</summary>
    public PushDeliveryStatus Status { get; set; } = PushDeliveryStatus.Pending;

    /// <summary>Attempts made.</summary>
    public int Attempts { get; set; }

    /// <summary>When it is tried next.</summary>
    public DateTimeOffset? NextAttemptAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>A worker is sending it until this time.</summary>
    public DateTimeOffset? LeaseUntil { get; set; }

    /// <summary>Last attempt.</summary>
    public DateTimeOffset? LastAttemptAt { get; set; }

    /// <summary>Why the last attempt failed.</summary>
    public string? LastError { get; set; }

    /// <summary>When it was queued.</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>When the push service accepted it.</summary>
    public DateTimeOffset? SentAt { get; set; }
}
