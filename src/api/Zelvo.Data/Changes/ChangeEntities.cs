using Zelvo.Data.Auditing;
using Zelvo.Data.Localization;
using Zelvo.Data.Tenancy;

namespace Zelvo.Data.Changes;

/// <summary>The three change types of module 17 (task H3), each with its own approval path.</summary>
public enum ChangeKind
{
    /// <summary>Pre-approved, from the catalog of standard change templates; no board.</summary>
    Standard,

    /// <summary>Assessed with the risk questionnaire; the risk level decides the approval path.</summary>
    Normal,

    /// <summary>Urgent; decided by the emergency path (the emergency board, task H4).</summary>
    Emergency,
}

/// <summary>
/// Where an open change is (task H3). The ticket core's state decides Review (Resolved: implemented,
/// waiting for the post-implementation review) and Closed; before that the phase names the step.
/// </summary>
public enum ChangePhase
{
    /// <summary>Being planned: plans, window, affected items, risk and tasks.</summary>
    Planning,

    /// <summary>Submitted and waiting for its approval path (C3 approvals).</summary>
    Authorisation,

    /// <summary>Approved (or pre-approved) and waiting for its window.</summary>
    Scheduled,

    /// <summary>Being carried out; its tasks are open.</summary>
    Implementation,
}

/// <summary>Risk level from the questionnaire's score (task H3).</summary>
public enum ChangeRiskLevel
{
    /// <summary>Low risk.</summary>
    Low,

    /// <summary>Medium risk.</summary>
    Medium,

    /// <summary>High risk.</summary>
    High,
}

/// <summary>Outcome recorded by the post-implementation review (task H3); task H5 learns from it.</summary>
public enum ChangeOutcome
{
    /// <summary>Done as planned.</summary>
    Successful,

    /// <summary>Done, with problems along the way.</summary>
    SuccessfulWithProblems,

    /// <summary>Did not achieve its aim.</summary>
    Failed,

    /// <summary>Undone with the back-out plan.</summary>
    BackedOut,
}

/// <summary>How a closed change ended.</summary>
public enum ChangeClosure
{
    /// <summary>Implemented and reviewed.</summary>
    Completed,

    /// <summary>Rejected by its approval path.</summary>
    Rejected,

    /// <summary>Withdrawn before implementation.</summary>
    Cancelled,
}

/// <summary>
/// Change details of a ticket of record type <c>Change</c> (module 17, task H3): its type and
/// phase, the risk questionnaire's answers, score and level, the implementation, test and back-out
/// plans, the planned and actual windows and the post-implementation review.
/// </summary>
public sealed class ChangeRecord : ITenantOwned, IAuditedEntity
{
    /// <summary>Maximum length of each plan and of the review notes.</summary>
    public const int PlanMaxLength = 20_000;

    /// <summary>The ticket.</summary>
    public Guid TicketId { get; init; }

    /// <inheritdoc />
    public Guid TenantId { get; set; }

    /// <summary>Standard, normal or emergency.</summary>
    public ChangeKind Kind { get; init; }

    /// <summary>The step while the change is open.</summary>
    public ChangePhase Phase { get; set; } = ChangePhase.Planning;

    /// <summary>The standard change template it was made from.</summary>
    public Guid? TemplateId { get; init; }

    /// <summary>The questionnaire's answers (JSON object: question key → option key).</summary>
    public string RiskAnswers { get; set; } = "{}";

    /// <summary>Sum of the chosen options' weights; <see langword="null"/> until assessed.</summary>
    public int? RiskScore { get; set; }

    /// <summary>Level from the score (standard changes are always low).</summary>
    public ChangeRiskLevel? RiskLevel { get; set; }

    /// <summary>How the change is carried out.</summary>
    public string? ImplementationPlan { get; set; }

    /// <summary>How it is tested.</summary>
    public string? TestPlan { get; set; }

    /// <summary>How it is undone if it fails.</summary>
    public string? BackoutPlan { get; set; }

    /// <summary>Planned start of the window.</summary>
    public DateTimeOffset? PlannedStart { get; set; }

    /// <summary>Planned end of the window.</summary>
    public DateTimeOffset? PlannedEnd { get; set; }

    /// <summary>When implementation started.</summary>
    public DateTimeOffset? ActualStart { get; set; }

    /// <summary>When implementation ended.</summary>
    public DateTimeOffset? ActualEnd { get; set; }

    /// <summary>The post-implementation review's outcome.</summary>
    public ChangeOutcome? Outcome { get; set; }

    /// <summary>What the review found.</summary>
    public string? ReviewNotes { get; set; }

    /// <summary>Who reviewed it.</summary>
    public Guid? ReviewedById { get; set; }

    /// <summary>When it was reviewed.</summary>
    public DateTimeOffset? ReviewedAt { get; set; }

    /// <summary>How it ended, once closed.</summary>
    public ChangeClosure? Closure { get; set; }

    /// <summary>When it was last submitted for approval.</summary>
    public DateTimeOffset? SubmittedAt { get; set; }

    /// <summary>When its approval path approved it (or when a standard change was scheduled).</summary>
    public DateTimeOffset? ApprovedAt { get; set; }
}

/// <summary>
/// A standard change template (task H3): a pre-approved change of a known kind, with its plans and
/// tasks, that people pick from the catalog of standard changes.
/// </summary>
public sealed class ChangeTemplate : ITenantOwned, IAuditedEntity
{
    /// <summary>Maximum length of each language of <see cref="Name"/>.</summary>
    public const int NameMaxLength = 200;

    /// <summary>Maximum length of each language of <see cref="Description"/>.</summary>
    public const int DescriptionMaxLength = 4000;

    /// <summary>Most tasks of a template.</summary>
    public const int MaxTasks = 30;

    /// <summary>Unique id (UUID version 7).</summary>
    public Guid Id { get; init; } = Guid.CreateVersion7();

    /// <inheritdoc />
    public Guid TenantId { get; set; }

    /// <summary>Name in English and Arabic (at least one).</summary>
    public required LocalizedText Name { get; set; }

    /// <summary>What the change does and when to use it.</summary>
    public LocalizedText Description { get; set; } = new(null, null);

    /// <summary>Category of the changes made from it.</summary>
    public Guid? CategoryId { get; set; }

    /// <summary>The group that carries it out.</summary>
    public Guid? GroupId { get; set; }

    /// <summary>Implementation plan copied into each change.</summary>
    public string? ImplementationPlan { get; set; }

    /// <summary>Test plan copied into each change.</summary>
    public string? TestPlan { get; set; }

    /// <summary>Back-out plan copied into each change.</summary>
    public string? BackoutPlan { get; set; }

    /// <summary>The tasks (JSON array of task templates: key, bilingual name, group, target minutes, parallel with the previous one).</summary>
    public string Tasks { get; set; } = "[]";

    /// <summary>Offered in the catalog of standard changes.</summary>
    public bool Active { get; set; } = true;

    /// <summary>When it was made.</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>When it last changed.</summary>
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Optimistic concurrency token (PostgreSQL <c>xmin</c>).</summary>
    public uint Version { get; init; }
}

/// <summary>
/// A blackout period (task H3): a window in which changes should not run, for the whole tenant or
/// for one configuration item (a business service blacks out the items it depends on).
/// </summary>
public sealed class ChangeBlackout : ITenantOwned, IAuditedEntity
{
    /// <summary>Maximum length of each language of <see cref="Name"/>.</summary>
    public const int NameMaxLength = 200;

    /// <summary>Unique id (UUID version 7).</summary>
    public Guid Id { get; init; } = Guid.CreateVersion7();

    /// <inheritdoc />
    public Guid TenantId { get; set; }

    /// <summary>Name in English and Arabic, for example "Year-end closing".</summary>
    public required LocalizedText Name { get; set; }

    /// <summary>Start of the period.</summary>
    public DateTimeOffset StartsAt { get; set; }

    /// <summary>End of the period.</summary>
    public DateTimeOffset EndsAt { get; set; }

    /// <summary>The item it applies to; <see langword="null"/>: every change of the tenant.</summary>
    public Guid? ConfigurationItemId { get; set; }

    /// <summary>When it was made.</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Optimistic concurrency token (PostgreSQL <c>xmin</c>).</summary>
    public uint Version { get; init; }
}

/// <summary>How a ticket is linked to a change.</summary>
public enum ChangeLinkKind
{
    /// <summary>The change fixes the problem (the problem's timeline shows it).</summary>
    Fixes,

    /// <summary>The incident was caused by the change.</summary>
    CausedBy,
}

/// <summary>A problem the change fixes or an incident it caused (task H3).</summary>
public sealed class ChangeLink : ITenantOwned, IAuditedEntity
{
    /// <inheritdoc />
    public Guid TenantId { get; set; }

    /// <summary>The change's ticket.</summary>
    public Guid ChangeId { get; init; }

    /// <summary>The problem or incident.</summary>
    public Guid TicketId { get; init; }

    /// <summary>Fixes (problems) or caused by (incidents).</summary>
    public ChangeLinkKind Kind { get; init; }

    /// <summary>Who linked it.</summary>
    public Guid? LinkedById { get; init; }

    /// <summary>When it was linked.</summary>
    public DateTimeOffset LinkedAt { get; init; } = DateTimeOffset.UtcNow;
}
