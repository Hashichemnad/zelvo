using Zelvo.Data.Auditing;
using Zelvo.Data.Identity;
using Zelvo.Data.Tenancy;

namespace Zelvo.Data.Tickets;

/// <summary>
/// The shared ticket core (module 1, task B1): numbering, state, requester, assignee and
/// parent/child links. Incidents add <see cref="Incident"/>; later record types (service requests,
/// complaints, inquiries, suggestions) add their own details. Every field change is audited.
/// </summary>
public sealed class Ticket : ITenantOwned, IAuditedEntity
{
    /// <summary>Maximum length of <see cref="Number"/>.</summary>
    public const int NumberMaxLength = 30;

    /// <summary>Maximum length of <see cref="Title"/>.</summary>
    public const int TitleMaxLength = 300;

    /// <summary>Maximum length of <see cref="Description"/>.</summary>
    public const int DescriptionMaxLength = 20_000;

    /// <summary>Maximum length of <see cref="ResolutionNotes"/>.</summary>
    public const int ResolutionNotesMaxLength = 10_000;

    /// <summary>Maximum length of an on-hold reason key.</summary>
    public const int ReasonKeyMaxLength = 50;

    /// <summary>Unique id (UUID version 7).</summary>
    public Guid Id { get; init; } = Guid.CreateVersion7();

    /// <inheritdoc />
    public Guid TenantId { get; set; }

    /// <summary>Record type.</summary>
    public TicketType Type { get; init; }

    /// <summary>Number shown to people, for example <c>INC-000123</c>; never reused.</summary>
    public required string Number { get; init; }

    /// <summary>Position in the record type's number series.</summary>
    public long Sequence { get; init; }

    /// <summary>Short summary, in the language the requester wrote it.</summary>
    public required string Title { get; set; }

    /// <summary>What happened, in the language the requester wrote it.</summary>
    public string? Description { get; set; }

    /// <summary>Where the ticket is in its life cycle.</summary>
    public TicketState State { get; set; } = TicketState.New;

    /// <summary>How the ticket reached the service desk.</summary>
    public TicketChannel Channel { get; init; }

    /// <summary>The person the ticket is for.</summary>
    public Guid RequesterId { get; set; }

    /// <summary>The requester (navigation).</summary>
    public UserAccount? Requester { get; init; }

    /// <summary>The agent working on it.</summary>
    public Guid? AssigneeId { get; set; }

    /// <summary>The assignee (navigation).</summary>
    public UserAccount? Assignee { get; init; }

    /// <summary>What the ticket is about (task B2).</summary>
    public Guid? CategoryId { get; set; }

    /// <summary>The category (navigation).</summary>
    public Directory.Category? Category { get; init; }

    /// <summary>Where the requester is (task B2).</summary>
    public Guid? LocationId { get; set; }

    /// <summary>The location (navigation).</summary>
    public Directory.Location? Location { get; init; }

    /// <summary>The group working on it; the assignee, when set, is one of its members (task B2).</summary>
    public Guid? GroupId { get; set; }

    /// <summary>The group (navigation).</summary>
    public Directory.SupportGroup? Group { get; init; }

    /// <summary>The assignment rule that last routed the ticket; <see langword="null"/> when the tenant default or a person placed it.</summary>
    public Guid? RoutingRuleId { get; set; }

    /// <summary>Parent ticket; children are resolved with it.</summary>
    public Guid? ParentId { get; set; }

    /// <summary>The closed ticket this one follows up (a closed ticket is never reopened).</summary>
    public Guid? FollowUpOfId { get; init; }

    /// <summary>Key of the on-hold reason while <see cref="State"/> is <see cref="TicketState.OnHold"/>.</summary>
    public string? OnHoldReason { get; set; }

    /// <summary>How it was resolved; required to resolve.</summary>
    public string? ResolutionNotes { get; set; }

    /// <summary>How many times it was reopened.</summary>
    public int ReopenCount { get; set; }

    /// <summary>Who logged it (the requester, or an agent on their behalf).</summary>
    public Guid? CreatedById { get; init; }

    /// <summary>The integration that logged it, when an AI agent raised it through MCP (module 9); <see cref="CreatedById"/> is then empty.</summary>
    public Guid? CreatedByApiClientId { get; init; }

    /// <summary>When it was logged.</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>When it last changed.</summary>
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// First response (task B3; product owner decision 2026-11-24): when a staff member first moved
    /// it to In progress (from B4 also a public reply), or resolved it before that. Stops the
    /// response clock.
    /// </summary>
    public DateTimeOffset? RespondedAt { get; set; }

    /// <summary>When it was last resolved.</summary>
    public DateTimeOffset? ResolvedAt { get; set; }

    /// <summary>When it was closed.</summary>
    public DateTimeOffset? ClosedAt { get; set; }

    /// <summary>Incident details when <see cref="Type"/> is <see cref="TicketType.Incident"/>.</summary>
    public Incident? Incident { get; set; }

    /// <summary>Optimistic concurrency token (PostgreSQL <c>xmin</c>).</summary>
    public uint Version { get; init; }
}

/// <summary>Record types sharing the ticket core. Each has its own number series and prefix.</summary>
public enum TicketType
{
    /// <summary>Something is broken or not working as it should (module 1).</summary>
    Incident,
}

/// <summary>Ticket states (module 1).</summary>
public enum TicketState
{
    /// <summary>Logged, nobody assigned yet.</summary>
    New,

    /// <summary>An agent is assigned but has not started.</summary>
    Assigned,

    /// <summary>Being worked on.</summary>
    InProgress,

    /// <summary>Waiting, with a reason.</summary>
    OnHold,

    /// <summary>Fixed; the requester can still reopen it until it closes.</summary>
    Resolved,

    /// <summary>Final.</summary>
    Closed,

    /// <summary>Resolved earlier, then reopened.</summary>
    Reopened,
}

/// <summary>How a ticket reached the service desk.</summary>
public enum TicketChannel
{
    /// <summary>Self-service portal.</summary>
    Portal,

    /// <summary>Email.</summary>
    Email,

    /// <summary>Live chat.</summary>
    Chat,

    /// <summary>WhatsApp.</summary>
    WhatsApp,

    /// <summary>Phone call, logged by an agent.</summary>
    Phone,

    /// <summary>An integration, through the API.</summary>
    Api,

    /// <summary>An AI agent, through Zelvo's MCP server (module 9).</summary>
    AiAgent,
}

/// <summary>Incident details of a ticket: impact, urgency, priority and the major-incident flag.</summary>
public sealed class Incident : ITenantOwned, IAuditedEntity
{
    /// <summary>The ticket.</summary>
    public Guid TicketId { get; init; }

    /// <inheritdoc />
    public Guid TenantId { get; set; }

    /// <summary>Impact level, 1 (highest) to the matrix's number of impact levels.</summary>
    public int Impact { get; set; }

    /// <summary>Urgency level, 1 (highest) to the matrix's number of urgency levels.</summary>
    public int Urgency { get; set; }

    /// <summary>Priority from the tenant's impact × urgency matrix, 1 (highest).</summary>
    public int Priority { get; set; }

    /// <summary>Declared a major incident, with its own communication timeline.</summary>
    public bool IsMajor { get; set; }

    /// <summary>When it was last declared a major incident.</summary>
    public DateTimeOffset? MajorDeclaredAt { get; set; }

    /// <summary>Who last declared it a major incident.</summary>
    public Guid? MajorDeclaredById { get; set; }
}

/// <summary>The last number given in a record type's series, per tenant. Numbers are never reset.</summary>
public sealed class TicketNumberSequence : ITenantOwned
{
    /// <inheritdoc />
    public Guid TenantId { get; set; }

    /// <summary>Record type.</summary>
    public TicketType Type { get; init; }

    /// <summary>Last number given.</summary>
    public long LastValue { get; set; }
}
