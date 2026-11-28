using Zelvo.Data.Auditing;
using Zelvo.Data.Localization;
using Zelvo.Data.Tenancy;
using Zelvo.Data.Tickets;

namespace Zelvo.Data.Workflows;

/// <summary>
/// A workflow rule of the no-code builder (module 10, task C3; product owner decision 2026-11-24):
/// <b>when</b> something happens to a ticket, <b>if</b> its conditions hold, <b>then</b> its
/// actions run (assign, ask for approval, notify). Rules are tried in order; a rule can stop the
/// rules after it. Conditions and actions are JSON documents checked by the API
/// (<c>Zelvo.Workflows.RuleDocuments</c>).
/// </summary>
public sealed class WorkflowRule : ITenantOwned, IAuditedEntity
{
    /// <summary>Maximum length of each language of <see cref="Name"/>.</summary>
    public const int NameMaxLength = 150;

    /// <summary>Maximum length of each language of <see cref="Description"/>.</summary>
    public const int DescriptionMaxLength = 1000;

    /// <summary>Unique id (UUID version 7).</summary>
    public Guid Id { get; init; } = Guid.CreateVersion7();

    /// <inheritdoc />
    public Guid TenantId { get; set; }

    /// <summary>Name in English and Arabic.</summary>
    public required LocalizedText Name { get; set; }

    /// <summary>What the rule is for.</summary>
    public LocalizedText Description { get; set; } = new(null, null);

    /// <summary>Order in which rules are tried (lowest first).</summary>
    public int Position { get; set; }

    /// <summary>Inactive rules are kept but never run.</summary>
    public bool Active { get; set; } = true;

    /// <summary>When the rule is looked at.</summary>
    public WorkflowTrigger Trigger { get; set; }

    /// <summary>The record type it applies to; <see langword="null"/> for every type.</summary>
    public TicketType? RecordType { get; set; }

    /// <summary>The custom record type when <see cref="RecordType"/> is <c>Custom</c> (task K2); none means every custom type.</summary>
    public Guid? CustomRecordTypeId { get; set; }

    /// <summary>Whether all or any of the conditions must hold.</summary>
    public ConditionMatch Match { get; set; } = ConditionMatch.All;

    /// <summary>Conditions as JSON (an array of <c>{ field, op, value }</c>); no conditions always hold.</summary>
    public string Conditions { get; set; } = "[]";

    /// <summary>Actions as JSON (an array of <c>{ type, ... }</c>).</summary>
    public string Actions { get; set; } = "[]";

    /// <summary>When the rule matched, later rules of the same trigger are not tried.</summary>
    public bool StopProcessing { get; set; }

    /// <summary>Maximum length of <see cref="SourceText"/>.</summary>
    public const int SourceTextMaxLength = 2000;

    /// <summary>
    /// Task K1 (module 20): a draft made by the AI from a plain-language description. Drafts never
    /// run; they go live only when an administrator approves them.
    /// </summary>
    public bool IsDraft { get; set; }

    /// <summary>The administrator's own words the draft was made from (kept after approval, audited).</summary>
    public string? SourceText { get; set; }

    /// <summary>The language of <see cref="SourceText"/> (<c>en</c> or <c>ar</c>), when known.</summary>
    public string? SourceLanguage { get; set; }

    /// <summary>What the AI could not express with the builder, as a JSON array of short phrases.</summary>
    public string Unmapped { get; set; } = "[]";

    /// <summary>The call in the AI log that made the draft.</summary>
    public long? AiCallId { get; set; }

    /// <summary>Who approved the draft.</summary>
    public Guid? ApprovedById { get; set; }

    /// <summary>When the draft was approved.</summary>
    public DateTimeOffset? ApprovedAt { get; set; }

    /// <summary>Who created it.</summary>
    public Guid? CreatedById { get; init; }

    /// <summary>When it was created.</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>When it was last changed.</summary>
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Optimistic concurrency token (PostgreSQL <c>xmin</c>).</summary>
    public uint Version { get; init; }
}

/// <summary>When a workflow rule is looked at.</summary>
public enum WorkflowTrigger
{
    /// <summary>A ticket was logged (for service requests: submitted).</summary>
    TicketCreated,

    /// <summary>A ticket moved to another state.</summary>
    StateChanged,

    /// <summary>A ticket's group or assignee changed.</summary>
    AssignmentChanged,

    /// <summary>An approval process of a ticket ended (approved or rejected).</summary>
    ApprovalDecided,
}

/// <summary>Whether all or any conditions must hold.</summary>
public enum ConditionMatch
{
    /// <summary>Every condition.</summary>
    All,

    /// <summary>At least one condition.</summary>
    Any,
}

/// <summary>
/// An approval process of a ticket (task C3): the levels of approvers it waits for, in order. It
/// runs as an Elsa workflow instance (<see cref="EngineInstanceId"/>) that waits for each level's
/// decision; Zelvo keeps the approvers, decisions and outcome here, isolated by row-level security.
/// </summary>
public sealed class ApprovalProcess : ITenantOwned, IAuditedEntity
{
    /// <summary>Maximum length of <see cref="Reason"/> and <see cref="ResolutionNotes"/>.</summary>
    public const int TextMaxLength = Ticket.ResolutionNotesMaxLength;

    /// <summary>Unique id (UUID version 7).</summary>
    public Guid Id { get; init; } = Guid.CreateVersion7();

    /// <inheritdoc />
    public Guid TenantId { get; set; }

    /// <summary>The ticket it is for.</summary>
    public Guid TicketId { get; init; }

    /// <summary>Before assignment or after fulfilment.</summary>
    public ApprovalStage Stage { get; init; }

    /// <summary>Where it is.</summary>
    public ApprovalProcessState State { get; set; } = ApprovalProcessState.Pending;

    /// <summary>The levels as planned when it started (JSON array of <c>{ name, mode, approverIds, fallback }</c>).</summary>
    public string Levels { get; set; } = "[]";

    /// <summary>The level waiting for decisions (0-based); the number of levels once it ended.</summary>
    public int CurrentLevel { get; set; }

    /// <summary>The Elsa workflow instance running it.</summary>
    public string? EngineInstanceId { get; set; }

    /// <summary>The Elsa bookmark of the level waiting for decisions.</summary>
    public string? EngineBookmarkId { get; set; }

    /// <summary>Why it was rejected or cancelled.</summary>
    public string? Reason { get; set; }

    /// <summary>After fulfilment: the agent's resolution notes, used when the sign-off is approved.</summary>
    public string? ResolutionNotes { get; set; }

    /// <summary>Who started it (the requester, or the agent who asked for sign-off).</summary>
    public Guid? StartedById { get; init; }

    /// <summary>When it started.</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>When it ended.</summary>
    public DateTimeOffset? EndedAt { get; set; }

    /// <summary>Optimistic concurrency token (PostgreSQL <c>xmin</c>).</summary>
    public uint Version { get; init; }
}

/// <summary>When an approval is asked for (module 2).</summary>
public enum ApprovalStage
{
    /// <summary>The request goes to agents only after approval (for example manager or budget approval).</summary>
    BeforeAssignment,

    /// <summary>The completed work is signed off before the request is resolved (quality or supervisor sign-off).</summary>
    AfterFulfilment,

    /// <summary>A change's approval path, chosen by its type and risk level (module 17, task H3).</summary>
    ChangeAuthorisation,
}

/// <summary>States of an approval process.</summary>
public enum ApprovalProcessState
{
    /// <summary>Waiting for decisions.</summary>
    Pending,

    /// <summary>Every level approved.</summary>
    Approved,

    /// <summary>An approver rejected it.</summary>
    Rejected,

    /// <summary>Stopped because the ticket was closed or changed by staff.</summary>
    Cancelled,
}

/// <summary>One approver's part in one level of an approval process.</summary>
public sealed class ApprovalDecision : ITenantOwned, IAuditedEntity
{
    /// <summary>Maximum length of <see cref="Comment"/>.</summary>
    public const int CommentMaxLength = 2000;

    /// <summary>Unique id (UUID version 7); the approval link is <c>/approvals/{id}</c>.</summary>
    public Guid Id { get; init; } = Guid.CreateVersion7();

    /// <inheritdoc />
    public Guid TenantId { get; set; }

    /// <summary>The process.</summary>
    public Guid ProcessId { get; init; }

    /// <summary>The ticket (copied for lists and isolation).</summary>
    public Guid TicketId { get; init; }

    /// <summary>The level (0-based).</summary>
    public int Level { get; init; }

    /// <summary>Who is asked.</summary>
    public Guid ApproverId { get; init; }

    /// <summary>Where it is.</summary>
    public ApprovalDecisionState State { get; set; } = ApprovalDecisionState.Pending;

    /// <summary>The approver's comment; the reason when rejected.</summary>
    public string? Comment { get; set; }

    /// <summary>How the decision was made (portal or email link).</summary>
    public string? DecidedVia { get; set; }

    /// <summary>When it was asked.</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>When it was decided (or stopped being needed).</summary>
    public DateTimeOffset? DecidedAt { get; set; }

    /// <summary>Optimistic concurrency token (PostgreSQL <c>xmin</c>).</summary>
    public uint Version { get; init; }
}

/// <summary>States of an approver's decision.</summary>
public enum ApprovalDecisionState
{
    /// <summary>Waiting for the approver.</summary>
    Pending,

    /// <summary>Approved.</summary>
    Approved,

    /// <summary>Rejected, with a reason.</summary>
    Rejected,

    /// <summary>No longer needed: another approver decided the level, or the process ended.</summary>
    NotNeeded,
}
