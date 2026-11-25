using Zelvo.Data.Auditing;
using Zelvo.Data.Tenancy;

namespace Zelvo.Data.Reports;

/// <summary>How often a scheduled report goes out.</summary>
public enum ReportFrequency
{
    /// <summary>Every day; covers the day before.</summary>
    Daily,

    /// <summary>Every week on a day; covers the seven days before.</summary>
    Weekly,

    /// <summary>Every month on a day; covers the month before.</summary>
    Monthly,
}

/// <summary>
/// A scheduled report (module 10, task F2): a dashboard with its filters, emailed as a branded PDF
/// to tenant users who may see reports, each built with the recipient's own access and in their
/// language (product owner decision 2026-11-25). Audited.
/// </summary>
public sealed class ReportSchedule : ITenantOwned, IAuditedEntity
{
    /// <summary>Maximum length of <see cref="Name"/>.</summary>
    public const int NameMaxLength = 150;

    /// <summary>Most recipients of a schedule.</summary>
    public const int MaxRecipients = 50;

    /// <summary>Unique id (UUID version 7).</summary>
    public Guid Id { get; init; } = Guid.CreateVersion7();

    /// <inheritdoc />
    public Guid TenantId { get; set; }

    /// <summary>Name shown in the list and the email subject.</summary>
    public required string Name { get; set; }

    /// <summary>The dashboard (<c>ReportKind</c> of Zelvo.Tickets: Overview, Volumes, Backlog, Sla, Agents, Happiness).</summary>
    public required string Report { get; set; }

    /// <summary>Record types, groups and categories as JSON (the period follows the frequency).</summary>
    public required string Filters { get; set; }

    /// <summary>How often.</summary>
    public ReportFrequency Frequency { get; set; }

    /// <summary>Weekly: ISO day of the week, 1 Monday to 7 Sunday.</summary>
    public int? DayOfWeek { get; set; }

    /// <summary>Monthly: day of the month, 1–28.</summary>
    public int? DayOfMonth { get; set; }

    /// <summary>Time of day in the tenant's time zone, in minutes after midnight.</summary>
    public int TimeOfDay { get; set; }

    /// <summary>Whether it goes out.</summary>
    public bool Active { get; set; } = true;

    /// <summary>Who made it; named in the email.</summary>
    public Guid OwnerId { get; set; }

    /// <summary>When it goes out next; null while inactive.</summary>
    public DateTimeOffset? NextRunAt { get; set; }

    /// <summary>When it last went out.</summary>
    public DateTimeOffset? LastRunAt { get; set; }

    /// <summary>A worker holds it until then.</summary>
    public DateTimeOffset? LeaseUntil { get; set; }

    /// <summary>Recipients.</summary>
    public List<ReportScheduleRecipient> Recipients { get; init; } = [];

    /// <summary>When it was made.</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>When it last changed.</summary>
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Optimistic concurrency token (PostgreSQL <c>xmin</c>).</summary>
    public uint Version { get; init; }
}

/// <summary>A recipient of a scheduled report: a tenant user. Audited.</summary>
public sealed class ReportScheduleRecipient : ITenantOwned, IAuditedEntity
{
    /// <inheritdoc />
    public Guid TenantId { get; set; }

    /// <summary>The schedule.</summary>
    public Guid ScheduleId { get; init; }

    /// <summary>The person.</summary>
    public Guid UserId { get; init; }
}

/// <summary>Where one email of a scheduled report is.</summary>
public enum ReportDeliveryStatus
{
    /// <summary>Waiting to be made and sent, or to be tried again.</summary>
    Pending,

    /// <summary>Accepted by the mail server.</summary>
    Sent,

    /// <summary>Given up after its attempts, or refused for good.</summary>
    Failed,

    /// <summary>Not sent: the recipient is no longer active or may no longer see reports.</summary>
    Skipped,
}

/// <summary>One scheduled report email to one recipient (the delivery log, kept 30 days).</summary>
public sealed class ReportDelivery : ITenantOwned
{
    /// <summary>Maximum length of <see cref="Error"/>.</summary>
    public const int ErrorMaxLength = 500;

    /// <summary>Unique id (UUID version 7).</summary>
    public Guid Id { get; init; } = Guid.CreateVersion7();

    /// <inheritdoc />
    public Guid TenantId { get; set; }

    /// <summary>The schedule.</summary>
    public Guid ScheduleId { get; init; }

    /// <summary>The recipient.</summary>
    public Guid RecipientId { get; init; }

    /// <summary>The run it belongs to (the time it was due, or when "Send now" was pressed).</summary>
    public DateTimeOffset RunAt { get; init; }

    /// <summary>First day of the period it covers (tenant time zone).</summary>
    public DateOnly PeriodFrom { get; init; }

    /// <summary>Last day of the period it covers.</summary>
    public DateOnly PeriodTo { get; init; }

    /// <summary>Where it is.</summary>
    public ReportDeliveryStatus Status { get; set; } = ReportDeliveryStatus.Pending;

    /// <summary>Attempts made.</summary>
    public int Attempts { get; set; }

    /// <summary>When to try (again).</summary>
    public DateTimeOffset? NextAttemptAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>A worker holds it until then.</summary>
    public DateTimeOffset? LeaseUntil { get; set; }

    /// <summary>Why the last attempt failed or why it was skipped; never the content.</summary>
    public string? Error { get; set; }

    /// <summary>When the mail server accepted it.</summary>
    public DateTimeOffset? SentAt { get; set; }

    /// <summary>When it was queued.</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}
