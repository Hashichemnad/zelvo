using Zelvo.Data.Auditing;
using Zelvo.Data.Localization;
using Zelvo.Data.Tenancy;

namespace Zelvo.Data.Catalog;

/// <summary>
/// A service catalog (module 2): services grouped for a department or an audience, for example
/// "IT services for employees" or "Licensing services for businesses". A service can be in
/// several catalogs. Catalogs name editors (users or groups) who may write and submit the
/// services of that catalog (product owner decision 2026-11-24).
/// </summary>
public sealed class ServiceCatalog : ITenantOwned, IAuditedEntity
{
    /// <summary>Maximum length of <see cref="Key"/>.</summary>
    public const int KeyMaxLength = 60;

    /// <summary>Maximum length of each language of <see cref="Name"/>.</summary>
    public const int NameMaxLength = 150;

    /// <summary>Maximum length of each language of <see cref="Description"/>.</summary>
    public const int DescriptionMaxLength = 1000;

    /// <summary>Unique id (UUID version 7).</summary>
    public Guid Id { get; init; } = Guid.CreateVersion7();

    /// <inheritdoc />
    public Guid TenantId { get; set; }

    /// <summary>Stable key, unique in the tenant, for example <c>it-services</c>.</summary>
    public required string Key { get; set; }

    /// <summary>Name in English and Arabic.</summary>
    public required LocalizedText Name { get; set; }

    /// <summary>What the catalog offers and to whom.</summary>
    public LocalizedText Description { get; set; } = new(null, null);

    /// <summary>The department that owns the catalog, if any (C1 departments).</summary>
    public Guid? DepartmentId { get; set; }

    /// <summary>Order in lists.</summary>
    public int Position { get; set; }

    /// <summary>Inactive catalogs are not shown to requesters; their services stay reachable through their other catalogs.</summary>
    public bool Active { get; set; } = true;

    /// <summary>People and groups who may write and submit this catalog's services.</summary>
    public List<CatalogEditor> Editors { get; init; } = [];

    /// <summary>When it was created.</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>When it was last changed.</summary>
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Optimistic concurrency token (PostgreSQL <c>xmin</c>).</summary>
    public uint Version { get; init; }
}

/// <summary>A user or a group who may write the services of a catalog. Exactly one of the two is set.</summary>
public sealed class CatalogEditor : ITenantOwned, IAuditedEntity
{
    /// <summary>Unique id (UUID version 7).</summary>
    public Guid Id { get; init; } = Guid.CreateVersion7();

    /// <inheritdoc />
    public Guid TenantId { get; set; }

    /// <summary>The catalog.</summary>
    public Guid CatalogId { get; init; }

    /// <summary>A user.</summary>
    public Guid? UserId { get; init; }

    /// <summary>A support group (task B2): its members.</summary>
    public Guid? GroupId { get; init; }
}

/// <summary>Where a service stands (module 2 service card: status).</summary>
public enum ServiceState
{
    /// <summary>Being written; never published.</summary>
    Draft,

    /// <summary>Submitted and waiting for a reviewer; never published.</summary>
    UnderReview,

    /// <summary>Published and open for requests. Changes may be pending in a new version.</summary>
    Active,

    /// <summary>Published but closed for new requests; requesters see the suspension message.</summary>
    Suspended,

    /// <summary>Taken out of use: not shown, found or requested. Can be reopened as a draft.</summary>
    Retired,
}

/// <summary>Where one version of a service card stands.</summary>
public enum ServiceVersionState
{
    /// <summary>Being written or changed.</summary>
    Draft,

    /// <summary>Submitted for review.</summary>
    UnderReview,

    /// <summary>The live version.</summary>
    Published,

    /// <summary>Was live, replaced by a newer published version.</summary>
    Superseded,

    /// <summary>Was live when the service was retired.</summary>
    Retired,
}

/// <summary>
/// A service of the catalog (module 2). Its card lives in versions: the published version is what
/// requesters see; changes are made in a working version that goes through review and replaces
/// the published one only when a reviewer other than its author approves it (product owner
/// decision 2026-11-24, as for knowledge articles). The service row keeps copies of the values that
/// lists, the hierarchy and access checks need, from the published version (from the working
/// version until it is first published).
/// </summary>
public sealed class CatalogService : ITenantOwned, IAuditedEntity
{
    /// <summary>Maximum length of <see cref="Code"/>.</summary>
    public const int CodeMaxLength = 30;

    /// <summary>Maximum length of each language of <see cref="Name"/>.</summary>
    public const int NameMaxLength = 200;

    /// <summary>Maximum length of each language of <see cref="SuspendedMessage"/>.</summary>
    public const int SuspendedMessageMaxLength = 1000;

    /// <summary>Unique id (UUID version 7).</summary>
    public Guid Id { get; init; } = Guid.CreateVersion7();

    /// <inheritdoc />
    public Guid TenantId { get; set; }

    /// <summary>Unique service code, for example <c>IT-LAPTOP</c>; never reused within the tenant.</summary>
    public required string Code { get; set; }

    /// <summary>Lifecycle state.</summary>
    public ServiceState State { get; set; } = ServiceState.Draft;

    /// <summary>Name (copy from the card).</summary>
    public required LocalizedText Name { get; set; }

    /// <summary>The main service above this one (copy from the card).</summary>
    public Guid? ParentId { get; set; }

    /// <summary>Catalogs the service is in (copy from the card).</summary>
    public List<Guid> CatalogIds { get; set; } = [];

    /// <summary>Categories for browsing and search (copy from the card).</summary>
    public List<Guid> CategoryIds { get; set; } = [];

    /// <summary>Catalogs of the working version too, so editors of either may work on it.</summary>
    public List<Guid> EditorCatalogIds { get; set; } = [];

    /// <summary>The live version.</summary>
    public Guid? PublishedVersionId { get; set; }

    /// <summary>The version being written or reviewed, if any.</summary>
    public Guid? WorkingVersionId { get; set; }

    /// <summary>Shown to requesters while the service is suspended.</summary>
    public LocalizedText SuspendedMessage { get; set; } = new(null, null);

    /// <summary>Who created the service.</summary>
    public Guid? CreatedById { get; init; }

    /// <summary>When it was created.</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>When it was last changed.</summary>
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>When a version was last published.</summary>
    public DateTimeOffset? PublishedAt { get; set; }

    /// <summary>When it was retired.</summary>
    public DateTimeOffset? RetiredAt { get; set; }

    /// <summary>Optimistic concurrency token (PostgreSQL <c>xmin</c>).</summary>
    public uint Version { get; init; }
}

/// <summary>One version of a service card.</summary>
public sealed class CatalogServiceVersion : ITenantOwned, IAuditedEntity
{
    /// <summary>Maximum length of <see cref="ChangeNote"/> and <see cref="ReviewComment"/>.</summary>
    public const int NoteMaxLength = 2000;

    /// <summary>Unique id (UUID version 7).</summary>
    public Guid Id { get; init; } = Guid.CreateVersion7();

    /// <inheritdoc />
    public Guid TenantId { get; set; }

    /// <summary>The service.</summary>
    public Guid ServiceId { get; init; }

    /// <summary>1 for the first version, then counting up.</summary>
    public int Number { get; init; }

    /// <summary>Where it stands.</summary>
    public ServiceVersionState State { get; set; } = ServiceVersionState.Draft;

    /// <summary>The whole card as JSON (name, description, documents, steps, fee, access, FAQ…).</summary>
    [AuditLengthOnly]
    public required string Content { get; set; }

    /// <summary>What changed, from the editor.</summary>
    public string? ChangeNote { get; set; }

    /// <summary>Who started this version.</summary>
    public Guid? CreatedById { get; init; }

    /// <summary>Who last changed it (four-eyes: neither this person nor the creator may approve it).</summary>
    public Guid? LastEditedById { get; set; }

    /// <summary>Who sent it for review.</summary>
    public Guid? SubmittedById { get; set; }

    /// <summary>When it was sent for review.</summary>
    public DateTimeOffset? SubmittedAt { get; set; }

    /// <summary>Who approved it or sent it back.</summary>
    public Guid? ReviewedById { get; set; }

    /// <summary>Why it was sent back.</summary>
    public string? ReviewComment { get; set; }

    /// <summary>When it was published.</summary>
    public DateTimeOffset? PublishedAt { get; set; }

    /// <summary>When it was created.</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>When it was last changed.</summary>
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Optimistic concurrency token (PostgreSQL <c>xmin</c>).</summary>
    public uint Version { get; init; }
}

/// <summary>
/// A service template the tenant saved ("save as template"). Zelvo's own ready-made templates are
/// in code; both make a new draft service with the template's card.
/// </summary>
public sealed class CatalogServiceTemplate : ITenantOwned, IAuditedEntity
{
    /// <summary>Unique id (UUID version 7).</summary>
    public Guid Id { get; init; } = Guid.CreateVersion7();

    /// <inheritdoc />
    public Guid TenantId { get; set; }

    /// <summary>Name in English and Arabic.</summary>
    public required LocalizedText Name { get; set; }

    /// <summary>What the template is for.</summary>
    public LocalizedText Description { get; set; } = new(null, null);

    /// <summary>The card as JSON.</summary>
    [AuditLengthOnly]
    public required string Content { get; set; }

    /// <summary>Who saved it.</summary>
    public Guid? CreatedById { get; init; }

    /// <summary>When it was saved.</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// The service details of a service request ticket (task C2): which service and which version of
/// its card the request was made with. Fulfilment (initial group, service SLAs, tasks, steps
/// progress) comes with task C4.
/// </summary>
public sealed class ServiceRequestDetails : ITenantOwned, IAuditedEntity
{
    /// <summary>The ticket (same id).</summary>
    public Guid TicketId { get; init; }

    /// <inheritdoc />
    public Guid TenantId { get; set; }

    /// <summary>The service requested.</summary>
    public Guid ServiceId { get; init; }

    /// <summary>The published version of its card at the time.</summary>
    public Guid ServiceVersionId { get; init; }

    /// <summary>
    /// The service's ownership SLA in business minutes (task C4), copied when the request reaches
    /// the service desk; replaces the SLA policy's response target. <see langword="null"/>: the policy's.
    /// </summary>
    public int? OwnershipMinutes { get; set; }

    /// <summary>
    /// The service's completion SLA in business minutes (task C4): the card's own target, or the
    /// business time to the end of its Nth working day; replaces the policy's resolution target.
    /// </summary>
    public int? CompletionMinutes { get; set; }

    /// <summary>The service's escalation thresholds (JSON array of <see cref="ServiceEscalation"/>); none: the policy's.</summary>
    public string Escalations { get; set; } = "[]";

    /// <summary>
    /// The request is a bundle (task C5): a container whose parts are child requests, one per
    /// service of the bundle. It has no group, clocks or tasks of its own; it resolves when every
    /// part is complete and closes when every part is closed.
    /// </summary>
    public bool IsBundle { get; init; }

    /// <summary>The bundle this request is a part of (task C5); also the ticket's parent.</summary>
    public Guid? BundleId { get; init; }

    /// <summary>Stage of the part in its bundle (0-based): parts of a stage run in parallel, stages in sequence.</summary>
    public int? BundleStage { get; init; }

    /// <summary>Whether the part was optional in the bundle (the requester chose it).</summary>
    public bool BundleOptional { get; init; }

    /// <summary>Why the request is held back from the service desk (<see cref="Tickets.Ticket.Held"/>).</summary>
    public ServiceRequestHold Hold { get; set; }

    /// <summary>The proactive run that started or offered it (task C5), if any.</summary>
    public Guid? ProactiveRunId { get; init; }
}

/// <summary>Why a service request waits before it reaches the service desk.</summary>
public enum ServiceRequestHold
{
    /// <summary>Not held.</summary>
    None,

    /// <summary>A part of a bundle in sequence, waiting for the parts before it (task C5).</summary>
    Bundle,

    /// <summary>Waiting for its fee to be paid (task E8).</summary>
    Payment,
}

/// <summary>An escalation threshold of a service's SLA (task C4), like an SLA policy's.</summary>
public sealed record ServiceEscalation
{
    /// <summary>Ownership (Response), completion (Resolution) or both (null).</summary>
    public Sla.SlaMetric? Metric { get; init; }

    /// <summary>Share of the target, 1 to 300.</summary>
    public int Percent { get; init; }

    /// <summary>Tell the assignee.</summary>
    public bool NotifyAssignee { get; init; }

    /// <summary>Tell the group's team leads.</summary>
    public bool NotifyGroupLeads { get; init; }

    /// <summary>Tell the members of another group.</summary>
    public Guid? NotifyGroupId { get; init; }
}

/// <summary>
/// A fulfilment task of a service request (task C4, module 2): one step of the service's task
/// template, for a group (and an assignee), with its own target time. Tasks of the same stage run
/// in parallel; stages run in sequence. When every task is done the request is resolved (or its
/// sign-off starts).
/// </summary>
public sealed class RequestTask : ITenantOwned, IAuditedEntity
{
    /// <summary>Maximum length of <see cref="Key"/>.</summary>
    public const int KeyMaxLength = 40;

    /// <summary>Maximum length of <see cref="Notes"/>.</summary>
    public const int NotesMaxLength = 4000;

    /// <summary>Unique id (UUID version 7).</summary>
    public Guid Id { get; init; } = Guid.CreateVersion7();

    /// <inheritdoc />
    public Guid TenantId { get; set; }

    /// <summary>The request.</summary>
    public Guid TicketId { get; init; }

    /// <summary>The task's key in the template (steps of the journey may refer to it).</summary>
    public required string Key { get; init; }

    /// <summary>Name in English and Arabic.</summary>
    public required Localization.LocalizedText Name { get; init; }

    /// <summary>What to do.</summary>
    public Localization.LocalizedText Description { get; init; } = new(null, null);

    /// <summary>Order in the template.</summary>
    public int Position { get; init; }

    /// <summary>Stage (0-based): tasks of a stage start together when the stage before is done.</summary>
    public int Stage { get; init; }

    /// <summary>The group doing it.</summary>
    public Guid? GroupId { get; set; }

    /// <summary>The member doing it.</summary>
    public Guid? AssigneeId { get; set; }

    /// <summary>Where it is.</summary>
    public RequestTaskState State { get; set; } = RequestTaskState.Waiting;

    /// <summary>Target time in business minutes from when it opens; none: no target.</summary>
    public int? TargetMinutes { get; init; }

    /// <summary>When it is due (worked out on the group's calendar when it opens).</summary>
    public DateTimeOffset? DueAt { get; set; }

    /// <summary>What was done.</summary>
    public string? Notes { get; set; }

    /// <summary>When it opened.</summary>
    public DateTimeOffset? OpenedAt { get; set; }

    /// <summary>When work started.</summary>
    public DateTimeOffset? StartedAt { get; set; }

    /// <summary>When it was done or cancelled.</summary>
    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>Who completed it.</summary>
    public Guid? CompletedById { get; set; }

    /// <summary>When it was created.</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Optimistic concurrency token (PostgreSQL <c>xmin</c>).</summary>
    public uint Version { get; init; }
}

/// <summary>States of a fulfilment task.</summary>
public enum RequestTaskState
{
    /// <summary>Its stage has not started yet.</summary>
    Waiting,

    /// <summary>Ready to be worked on.</summary>
    Open,

    /// <summary>Being worked on.</summary>
    InProgress,

    /// <summary>Done.</summary>
    Done,

    /// <summary>No longer needed (the request was resolved or closed by hand).</summary>
    Cancelled,
}


/// <summary>What starts a proactive service (task C5).</summary>
public enum ProactiveTriggerKind
{
    /// <summary>A date: an answer on the person's completed request of another service, or a date sent through the API, with an offset in days.</summary>
    Date,

    /// <summary>A repeating schedule for named groups, roles, departments or people.</summary>
    Schedule,

    /// <summary>An event another system sends through the API (for example a new joiner in the HR system).</summary>
    Event,
}

/// <summary>What Zelvo does when a proactive service is due for someone.</summary>
public enum ProactiveMode
{
    /// <summary>Tell the person and ask them to confirm: the offer opens the service's form filled in with what Zelvo knows.</summary>
    Offer,

    /// <summary>Start the request at once (an offer instead when the form or documents need the person).</summary>
    AutoStart,
}

/// <summary>Where a proactive run stands.</summary>
public enum ProactiveRunState
{
    /// <summary>Due later (a date in the future).</summary>
    Scheduled,

    /// <summary>The person was told and asked to confirm.</summary>
    Offered,

    /// <summary>The request was started automatically.</summary>
    Started,

    /// <summary>The person requested the service from the offer.</summary>
    Accepted,

    /// <summary>The person declined the offer.</summary>
    Declined,

    /// <summary>The offer expired without an answer.</summary>
    Expired,

    /// <summary>It could not be offered or started (for example the service is not offered to the person); see the reason.</summary>
    Failed,
}

/// <summary>
/// One time a proactive service (task C5, module 2) is due for one person: from a date, a schedule
/// occurrence or an event. Unique per service, trigger, person and occurrence, so the same date,
/// occurrence or event never offers or starts the service twice.
/// </summary>
public sealed class ProactiveRun : ITenantOwned, IAuditedEntity
{
    /// <summary>Maximum length of <see cref="TriggerKey"/>.</summary>
    public const int KeyMaxLength = 40;

    /// <summary>Maximum length of <see cref="Occurrence"/>.</summary>
    public const int OccurrenceMaxLength = 200;

    /// <summary>Maximum length of <see cref="Reason"/>.</summary>
    public const int ReasonMaxLength = 300;

    /// <summary>Unique id (UUID version 7).</summary>
    public Guid Id { get; init; } = Guid.CreateVersion7();

    /// <inheritdoc />
    public Guid TenantId { get; set; }

    /// <summary>The proactive service.</summary>
    public Guid ServiceId { get; init; }

    /// <summary>The trigger of its card.</summary>
    public required string TriggerKey { get; init; }

    /// <summary>Kind of trigger.</summary>
    public ProactiveTriggerKind Kind { get; init; }

    /// <summary>The person it is for.</summary>
    public Guid PersonId { get; init; }

    /// <summary>
    /// What makes this run unique for the trigger and person: the source request and date, the
    /// schedule's occurrence date, or the event reference sent by the other system.
    /// </summary>
    public required string Occurrence { get; init; }

    /// <summary>When it is due (UTC date).</summary>
    public DateOnly DueOn { get; init; }

    /// <summary>Where it stands.</summary>
    public ProactiveRunState State { get; set; } = ProactiveRunState.Scheduled;

    /// <summary>The earlier request whose date answer started it (date triggers).</summary>
    public Guid? SourceTicketId { get; init; }

    /// <summary>Answers to fill in (JSON object by field key): from the source request or sent with the event.</summary>
    [AuditLengthOnly]
    public string Answers { get; init; } = "{}";

    /// <summary>The integration that sent the event or date, if any.</summary>
    public Guid? ApiClientId { get; init; }

    /// <summary>The request made: started automatically or from the offer.</summary>
    public Guid? TicketId { get; set; }

    /// <summary>Why it was offered instead of started, or why it failed (English, for administrators).</summary>
    public string? Reason { get; set; }

    /// <summary>When the person was told.</summary>
    public DateTimeOffset? OfferedAt { get; set; }

    /// <summary>When the offer expires.</summary>
    public DateTimeOffset? ExpiresAt { get; set; }

    /// <summary>When it was started, accepted, declined, expired or failed.</summary>
    public DateTimeOffset? EndedAt { get; set; }

    /// <summary>When it was created.</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Optimistic concurrency token (PostgreSQL <c>xmin</c>).</summary>
    public uint Version { get; init; }
}
