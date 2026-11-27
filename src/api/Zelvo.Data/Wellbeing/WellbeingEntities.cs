using Zelvo.Data.Auditing;
using Zelvo.Data.Tenancy;

namespace Zelvo.Data.Wellbeing;

/// <summary>
/// One agent's figures for one day (module 21, task K4; product owner decision 2026-11-26): what
/// workload balancing needs and nothing more. Worked out from what Zelvo keeps already (tickets,
/// comments, tasks and the audit log); never presence or keystrokes. Kept 90 days, only while the
/// tenant has wellbeing on, and part of the person's copy of their data (F5).
/// </summary>
public sealed class WellbeingDay : ITenantOwned
{
    /// <summary>How long daily figures are kept.</summary>
    public const int KeepDays = 90;

    /// <summary>Unique id (UUID version 7).</summary>
    public Guid Id { get; init; } = Guid.CreateVersion7();

    /// <inheritdoc />
    public Guid TenantId { get; set; }

    /// <summary>The agent.</summary>
    public Guid UserId { get; init; }

    /// <summary>The day, in the tenant's time zone.</summary>
    public DateOnly Day { get; init; }

    /// <summary>Open tickets assigned to the agent when the day was last worked out.</summary>
    public int OpenAssigned { get; set; }

    /// <summary>The same weighted by priority: P1 counts 5, P2 4, P3 3, P4 2, P5 1; without a priority 3.</summary>
    public int WeightedLoad { get; set; }

    /// <summary>Tickets assigned to the agent during the day.</summary>
    public int NewAssignments { get; set; }

    /// <summary>The agent's changes during the day: replies and notes, state and other ticket changes, work on tasks.</summary>
    public int Actions { get; set; }

    /// <summary>Of those, outside the working time of the agent's group's business calendar.</summary>
    public int AfterHoursActions { get; set; }

    /// <summary>Clock hours (in the calendar's time zone) with at least one change outside working time.</summary>
    public int AfterHoursHours { get; set; }

    /// <summary>Distinct tickets the agent worked on during the day.</summary>
    public int HandledTickets { get; set; }

    /// <summary>
    /// Of those, angry or escalated: a frustrated requester (E5), an SLA alert (B3), a major
    /// incident or a complaint of the highest severity (C6).
    /// </summary>
    public int DifficultTickets { get; set; }

    /// <summary>When the figures were last worked out.</summary>
    public DateTimeOffset ComputedAt { get; set; }
}

/// <summary>
/// A support group's wellbeing thresholds (task K4), set by its team leads; empty values take the
/// tenant's defaults (set by administrators).
/// </summary>
public sealed class WellbeingThreshold : ITenantOwned, IAuditedEntity
{
    /// <inheritdoc />
    public Guid TenantId { get; set; }

    /// <summary>The group.</summary>
    public Guid GroupId { get; init; }

    /// <summary>Most weighted open work per agent.</summary>
    public int? MaxWeightedLoad { get; set; }

    /// <summary>Most new assignments per agent and day, on average over the last 7 days.</summary>
    public int? MaxNewPerDay { get; set; }

    /// <summary>Most after-hours hours per agent in the last 7 days.</summary>
    public int? MaxAfterHoursHours { get; set; }

    /// <summary>Highest share of angry or escalated tickets, in percent, over the last 7 days.</summary>
    public int? MaxDifficultPercent { get; set; }

    /// <summary>Who last changed them.</summary>
    public Guid? UpdatedById { get; set; }

    /// <summary>When they were last changed.</summary>
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>What an alert is about.</summary>
public enum WellbeingAlertKind
{
    /// <summary>Weighted open work above the group's threshold.</summary>
    Workload,

    /// <summary>More new assignments per day than the threshold.</summary>
    NewAssignments,

    /// <summary>More after-hours work than the threshold.</summary>
    AfterHours,

    /// <summary>A higher share of angry or escalated tickets than the threshold.</summary>
    Difficult,
}

/// <summary>
/// An agent is past one of the group's thresholds (task K4). Raised and ended by the wellbeing job;
/// the agent and the group's team leads see it, with suggested reassignment the team lead applies.
/// </summary>
public sealed class WellbeingAlert : ITenantOwned
{
    /// <summary>Unique id (UUID version 7).</summary>
    public Guid Id { get; init; } = Guid.CreateVersion7();

    /// <inheritdoc />
    public Guid TenantId { get; set; }

    /// <summary>The agent.</summary>
    public Guid UserId { get; init; }

    /// <summary>The group whose threshold was passed.</summary>
    public Guid GroupId { get; init; }

    /// <summary>What.</summary>
    public WellbeingAlertKind Kind { get; init; }

    /// <summary>The latest value.</summary>
    public int Value { get; set; }

    /// <summary>The threshold it passed.</summary>
    public int Threshold { get; set; }

    /// <summary>When it was raised.</summary>
    public DateTimeOffset RaisedAt { get; init; }

    /// <summary>When the value was last looked at.</summary>
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>When the agent was back under the threshold; open while empty.</summary>
    public DateTimeOffset? EndedAt { get; set; }
}
