using Zelvo.Data.Auditing;
using Zelvo.Data.Localization;
using Zelvo.Data.Tenancy;
using Zelvo.Data.Tickets;

namespace Zelvo.Data.Sla;

/// <summary>
/// Business hours of a tenant or a team (module 8, GOV-7): a time zone, working periods per day of
/// the week (so a Friday half-day or a Saturday–Sunday weekend is just a pattern) and public
/// holidays. SLA clocks count only working time. One calendar per tenant is the default.
/// </summary>
public sealed class BusinessCalendar : ITenantOwned, IAuditedEntity
{
    /// <summary>Maximum length of each language of <see cref="Name"/>.</summary>
    public const int NameMaxLength = 150;

    /// <summary>Maximum length of <see cref="TimeZone"/>.</summary>
    public const int TimeZoneMaxLength = 64;

    /// <summary>The UAE's time zone, the default of new calendars.</summary>
    public const string DefaultTimeZone = "Asia/Dubai";

    /// <summary>Unique id (UUID version 7).</summary>
    public Guid Id { get; init; } = Guid.CreateVersion7();

    /// <inheritdoc />
    public Guid TenantId { get; set; }

    /// <summary>Name in English and Arabic.</summary>
    public required LocalizedText Name { get; set; }

    /// <summary>IANA time zone the working periods are in, for example <c>Asia/Dubai</c>.</summary>
    public string TimeZone { get; set; } = DefaultTimeZone;

    /// <summary>The tenant's default calendar: used when neither the ticket's group nor the SLA policy names one.</summary>
    public bool IsDefault { get; set; }

    /// <summary>Working periods of the week.</summary>
    public List<BusinessCalendarPeriod> Periods { get; init; } = [];

    /// <summary>Public holidays and other days off.</summary>
    public List<BusinessCalendarHoliday> Holidays { get; init; } = [];

    /// <summary>When the calendar was created.</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>When the calendar was last changed.</summary>
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Optimistic concurrency token (PostgreSQL <c>xmin</c>).</summary>
    public uint Version { get; init; }
}

/// <summary>A working period on one day of the week, for example Friday 07:30–12:00.</summary>
public sealed class BusinessCalendarPeriod : ITenantOwned, IAuditedEntity
{
    /// <summary>Minutes in a day: an end of 1440 is midnight at the end of the day.</summary>
    public const int MinutesPerDay = 24 * 60;

    /// <summary>Unique id (UUID version 7).</summary>
    public Guid Id { get; init; } = Guid.CreateVersion7();

    /// <inheritdoc />
    public Guid TenantId { get; set; }

    /// <summary>The calendar.</summary>
    public Guid CalendarId { get; init; }

    /// <summary>Day of the week.</summary>
    public DayOfWeek Day { get; init; }

    /// <summary>Start, in minutes after local midnight (0–1439).</summary>
    public int StartMinute { get; init; }

    /// <summary>End, in minutes after local midnight (1–1440), after <see cref="StartMinute"/>.</summary>
    public int EndMinute { get; init; }
}

/// <summary>A public holiday or other day off (one day or a range of days, both included).</summary>
public sealed class BusinessCalendarHoliday : ITenantOwned, IAuditedEntity
{
    /// <summary>Maximum length of each language of <see cref="Name"/>.</summary>
    public const int NameMaxLength = 150;

    /// <summary>Unique id (UUID version 7).</summary>
    public Guid Id { get; init; } = Guid.CreateVersion7();

    /// <inheritdoc />
    public Guid TenantId { get; set; }

    /// <summary>The calendar.</summary>
    public Guid CalendarId { get; init; }

    /// <summary>First day off.</summary>
    public DateOnly StartDate { get; set; }

    /// <summary>Last day off (the same as <see cref="StartDate"/> for a single day).</summary>
    public DateOnly EndDate { get; set; }

    /// <summary>Name in English and Arabic, for example "National Day".</summary>
    public required LocalizedText Name { get; set; }
}

/// <summary>
/// A group of requesters with their own SLA policies (module 8; product owner decision
/// 2026-11-24), for example "VIP" or a ministry. Requesters belong to it when an administrator adds
/// them or when their email domain is one of <see cref="EmailDomains"/>.
/// </summary>
public sealed class CustomerGroup : ITenantOwned, IAuditedEntity
{
    /// <summary>Maximum length of each language of <see cref="Name"/>.</summary>
    public const int NameMaxLength = 150;

    /// <summary>Most email domains of a group.</summary>
    public const int MaxDomains = 50;

    /// <summary>Unique id (UUID version 7).</summary>
    public Guid Id { get; init; } = Guid.CreateVersion7();

    /// <inheritdoc />
    public Guid TenantId { get; set; }

    /// <summary>Name in English and Arabic.</summary>
    public required LocalizedText Name { get; set; }

    /// <summary>Inactive groups match no SLA policy.</summary>
    public bool Active { get; set; } = true;

    /// <summary>Requesters with an email address in one of these domains (lower case, without <c>@</c>) belong to the group.</summary>
    public List<string> EmailDomains { get; set; } = [];

    /// <summary>Requesters added by hand.</summary>
    public List<CustomerGroupMember> Members { get; init; } = [];

    /// <summary>When the group was created.</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>When the group was last changed.</summary>
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Optimistic concurrency token (PostgreSQL <c>xmin</c>).</summary>
    public uint Version { get; init; }
}

/// <summary>A requester added to a customer group by hand.</summary>
public sealed class CustomerGroupMember : ITenantOwned, IAuditedEntity
{
    /// <inheritdoc />
    public Guid TenantId { get; set; }

    /// <summary>The group.</summary>
    public Guid GroupId { get; init; }

    /// <summary>The requester.</summary>
    public Guid UserId { get; init; }

    /// <summary>The requester (navigation).</summary>
    public Identity.UserAccount? User { get; init; }

    /// <summary>When they were added.</summary>
    public DateTimeOffset AddedAt { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// An SLA policy (module 8): optional conditions on record type, category (or below) and customer
/// group, response and resolution targets per priority, a calendar and escalation thresholds.
/// Policies are tried in <see cref="Position"/> order and the first match wins.
/// </summary>
public sealed class SlaPolicy : ITenantOwned, IAuditedEntity
{
    /// <summary>Maximum length of each language of <see cref="Name"/>.</summary>
    public const int NameMaxLength = 150;

    /// <summary>Unique id (UUID version 7).</summary>
    public Guid Id { get; init; } = Guid.CreateVersion7();

    /// <inheritdoc />
    public Guid TenantId { get; set; }

    /// <summary>Name in English and Arabic.</summary>
    public required LocalizedText Name { get; set; }

    /// <summary>Order in which policies are tried (0 first).</summary>
    public int Position { get; set; }

    /// <summary>Inactive policies are skipped.</summary>
    public bool Active { get; set; } = true;

    /// <summary>Matches tickets of this record type; <see langword="null"/>: any.</summary>
    public TicketType? RecordType { get; set; }

    /// <summary>Matches tickets in this category or below it; <see langword="null"/>: any.</summary>
    public Guid? CategoryId { get; set; }

    /// <summary>Matches tickets whose requester is in this customer group; <see langword="null"/>: any.</summary>
    public Guid? CustomerGroupId { get; set; }

    /// <summary>Business hours when the ticket's group has no calendar; <see langword="null"/>: the tenant default.</summary>
    public Guid? CalendarId { get; set; }

    /// <summary>Targets per priority.</summary>
    public List<SlaPolicyTarget> Targets { get; init; } = [];

    /// <summary>Who is alerted at which share of a target.</summary>
    public List<SlaEscalation> Escalations { get; init; } = [];

    /// <summary>When the policy was created.</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>When the policy was last changed.</summary>
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Optimistic concurrency token (PostgreSQL <c>xmin</c>).</summary>
    public uint Version { get; init; }
}

/// <summary>Response and resolution targets of one priority, in business minutes; missing means no target.</summary>
public sealed class SlaPolicyTarget : ITenantOwned, IAuditedEntity
{
    /// <summary>Longest target: a year of business minutes is far more than any service desk needs.</summary>
    public const int MaxMinutes = 525_600;

    /// <inheritdoc />
    public Guid TenantId { get; set; }

    /// <summary>The policy.</summary>
    public Guid PolicyId { get; init; }

    /// <summary>Priority, 1 (highest) to 5.</summary>
    public int Priority { get; init; }

    /// <summary>Business minutes until the first response.</summary>
    public int? ResponseMinutes { get; set; }

    /// <summary>Business minutes until resolution.</summary>
    public int? ResolutionMinutes { get; set; }
}

/// <summary>
/// An escalation threshold (product owner decision 2026-11-24): at <see cref="Percent"/> of a target
/// Zelvo records an alert on the ticket for the people named here. Notifications deliver it (B5).
/// </summary>
public sealed class SlaEscalation : ITenantOwned, IAuditedEntity
{
    /// <summary>Highest threshold: three times the target.</summary>
    public const int MaxPercent = 300;

    /// <summary>Unique id (UUID version 7).</summary>
    public Guid Id { get; init; } = Guid.CreateVersion7();

    /// <inheritdoc />
    public Guid TenantId { get; set; }

    /// <summary>The policy.</summary>
    public Guid PolicyId { get; init; }

    /// <summary>Which targets the threshold applies to; <see langword="null"/>: both.</summary>
    public SlaMetric? Metric { get; set; }

    /// <summary>Share of the target, 1–300 %. 100 % is the breach.</summary>
    public int Percent { get; set; }

    /// <summary>Alert the ticket's assignee.</summary>
    public bool NotifyAssignee { get; set; }

    /// <summary>Alert the team leads of the ticket's group.</summary>
    public bool NotifyGroupLeads { get; set; }

    /// <summary>Alert the members of this group too.</summary>
    public Guid? NotifyGroupId { get; set; }
}

/// <summary>What an SLA clock measures.</summary>
public enum SlaMetric
{
    /// <summary>Until a staff member starts work (In progress) or, from B4, sends a public reply.</summary>
    Response,

    /// <summary>Until the ticket is resolved.</summary>
    Resolution,
}

/// <summary>Whether an SLA clock is counting.</summary>
public enum SlaClockState
{
    /// <summary>Counting business time.</summary>
    Running,

    /// <summary>On hold with a reason that pauses SLAs (for example waiting for the requester).</summary>
    Paused,

    /// <summary>Stopped: responded to, or resolved. A reopened ticket's resolution clock runs again.</summary>
    Stopped,
}

/// <summary>
/// An SLA clock of a ticket: one for response and one for resolution. Kept for every ticket, even
/// without a target, so a policy that applies later counts the time that already passed. Not audited
/// row by row (the worker moves it on every minute); policy changes, breaches and alerts are.
/// </summary>
public sealed class TicketSla : ITenantOwned
{
    /// <summary>Unique id (UUID version 7).</summary>
    public Guid Id { get; init; } = Guid.CreateVersion7();

    /// <inheritdoc />
    public Guid TenantId { get; set; }

    /// <summary>The ticket.</summary>
    public Guid TicketId { get; init; }

    /// <summary>Response or resolution.</summary>
    public SlaMetric Metric { get; init; }

    /// <summary>The policy that gave the target; <see langword="null"/> when none applies.</summary>
    public Guid? PolicyId { get; set; }

    /// <summary>The target in business minutes; <see langword="null"/>: no target.</summary>
    public int? TargetMinutes { get; set; }

    /// <summary>The calendar the clock counts on; <see langword="null"/>: around the clock (the tenant has no calendar).</summary>
    public Guid? CalendarId { get; set; }

    /// <summary>Running, paused or stopped.</summary>
    public SlaClockState State { get; set; }

    /// <summary>Business seconds counted up to <see cref="CountedUntil"/>.</summary>
    public long ElapsedSeconds { get; set; }

    /// <summary>When <see cref="ElapsedSeconds"/> was last brought up to date.</summary>
    public DateTimeOffset CountedUntil { get; set; }

    /// <summary>While running with a target: when the target is reached.</summary>
    public DateTimeOffset? DueAt { get; set; }

    /// <summary>When the target was passed; stays set after the clock stops.</summary>
    public DateTimeOffset? BreachedAt { get; set; }

    /// <summary>When the clock last stopped.</summary>
    public DateTimeOffset? StoppedAt { get; set; }

    /// <summary>Highest threshold (percent of the target) reached so far.</summary>
    public int ReachedPercent { get; set; }

    /// <summary>While running with a target: when the worker looks at it next (next threshold or the breach).</summary>
    public DateTimeOffset? NextCheckAt { get; set; }
}

/// <summary>
/// A threshold a ticket reached (product owner decision 2026-11-24): recorded once per clock and
/// threshold, audited, sent as a webhook event and delivered to its recipients by notifications (B5).
/// </summary>
public sealed class SlaAlert : ITenantOwned, IAuditedEntity
{
    /// <summary>Unique id (UUID version 7).</summary>
    public Guid Id { get; init; } = Guid.CreateVersion7();

    /// <inheritdoc />
    public Guid TenantId { get; set; }

    /// <summary>The ticket.</summary>
    public Guid TicketId { get; init; }

    /// <summary>The clock.</summary>
    public Guid TicketSlaId { get; init; }

    /// <summary>Response or resolution.</summary>
    public SlaMetric Metric { get; init; }

    /// <summary>Share of the target reached, in percent; 100 is the breach.</summary>
    public int Percent { get; init; }

    /// <summary>When the threshold was reached.</summary>
    public DateTimeOffset ReachedAt { get; init; }

    /// <summary>People to alert, resolved when the threshold was reached.</summary>
    public List<Guid> RecipientIds { get; init; } = [];

    /// <summary>When notifications delivered the alert (B5); <see langword="null"/> until then.</summary>
    public DateTimeOffset? DeliveredAt { get; set; }
}
