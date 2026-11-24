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

    /// <summary>The conversation message it is about (task E2: replies).</summary>
    public Guid? MessageId { get; init; }

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

    /// <summary>When it was last changed.</summary>
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
