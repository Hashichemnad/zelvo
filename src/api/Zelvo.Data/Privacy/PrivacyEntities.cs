using Zelvo.Data.Auditing;
using Zelvo.Data.Localization;
using Zelvo.Data.Tenancy;

namespace Zelvo.Data.Privacy;

/// <summary>Where a privacy notice version stands.</summary>
public enum PrivacyNoticeState
{
    /// <summary>Being written; nobody sees it yet. One draft at a time.</summary>
    Draft,

    /// <summary>The current notice: shown to everyone and accepted by signed-in people.</summary>
    Published,

    /// <summary>Replaced by a later version; kept as the record of what people accepted.</summary>
    Superseded,
}

/// <summary>
/// A version of the tenant's privacy notice (GOV-5, task F5), written by the administrator in
/// English and Arabic (Markdown, never HTML). Published versions never change: what a person
/// accepted stays readable. Signed-in people accept the published version at their first sign-in
/// and again when a new version is published; visitors see it before they chat or send a message.
/// </summary>
public sealed class PrivacyNotice : ITenantOwned, IAuditedEntity
{
    /// <summary>Maximum length of <see cref="Title"/>.</summary>
    public const int TitleMaxLength = 200;

    /// <summary>Maximum length of <see cref="Body"/> per language.</summary>
    public const int BodyMaxLength = 50_000;

    /// <summary>Unique id (UUID version 7).</summary>
    public Guid Id { get; init; } = Guid.CreateVersion7();

    /// <inheritdoc />
    public Guid TenantId { get; set; }

    /// <summary>Version number, 1, 2, 3 … per tenant.</summary>
    public int Version { get; init; }

    /// <summary>Title in English and Arabic.</summary>
    public required LocalizedText Title { get; set; }

    /// <summary>The notice, Markdown, in English and Arabic.</summary>
    [AuditLengthOnly]
    public required LocalizedText Body { get; set; }

    /// <summary>Where it stands.</summary>
    public PrivacyNoticeState State { get; set; } = PrivacyNoticeState.Draft;

    /// <summary>Who wrote it.</summary>
    public Guid? CreatedById { get; init; }

    /// <summary>When it was created.</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>When it was last changed (drafts only).</summary>
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>When it was published.</summary>
    public DateTimeOffset? PublishedAt { get; set; }

    /// <summary>Who published it.</summary>
    public Guid? PublishedById { get; set; }

    /// <summary>Optimistic concurrency token (PostgreSQL <c>xmin</c>).</summary>
    public uint RowVersion { get; init; }
}

/// <summary>
/// An optional purpose people may agree to or not (task F5), for example satisfaction surveys or
/// AI assistance, defined by the administrator. Each person grants or withdraws it under Account →
/// Privacy; the history is kept in <see cref="ConsentRecord"/>.
/// </summary>
public sealed class ConsentPurpose : ITenantOwned, IAuditedEntity
{
    /// <summary>Maximum length of <see cref="Key"/>.</summary>
    public const int KeyMaxLength = 50;

    /// <summary>Maximum length of <see cref="Name"/>.</summary>
    public const int NameMaxLength = 150;

    /// <summary>Maximum length of <see cref="Description"/>.</summary>
    public const int DescriptionMaxLength = 2000;

    /// <summary>Unique id (UUID version 7).</summary>
    public Guid Id { get; init; } = Guid.CreateVersion7();

    /// <inheritdoc />
    public Guid TenantId { get; set; }

    /// <summary>Stable key for integrations, for example <c>surveys</c>; unique in the tenant.</summary>
    public required string Key { get; init; }

    /// <summary>Name in English and Arabic.</summary>
    public required LocalizedText Name { get; set; }

    /// <summary>What agreeing means, in English and Arabic.</summary>
    public required LocalizedText Description { get; set; }

    /// <summary>Whether people are asked; inactive purposes keep their history.</summary>
    public bool Active { get; set; } = true;

    /// <summary>Order on the screen.</summary>
    public int Position { get; set; }

    /// <summary>When it was created.</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>When it was last changed.</summary>
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Optimistic concurrency token (PostgreSQL <c>xmin</c>).</summary>
    public uint Version { get; init; }
}

/// <summary>What a consent record says.</summary>
public enum ConsentAction
{
    /// <summary>The person accepted a privacy notice version.</summary>
    NoticeAccepted,

    /// <summary>The person agreed to an optional purpose.</summary>
    Granted,

    /// <summary>The person withdrew their agreement to an optional purpose.</summary>
    Withdrawn,
}

/// <summary>Where consent was given.</summary>
public enum ConsentChannel
{
    /// <summary>Signed in, in the portal or the workspace.</summary>
    Web,

    /// <summary>A visitor starting a live chat or leaving a message.</summary>
    Chat,

    /// <summary>A visitor sending an anonymous complaint, inquiry or suggestion.</summary>
    Anonymous,

    /// <summary>Recorded by staff for the person (for example a signed paper form).</summary>
    Staff,

    /// <summary>Through the API by an integration.</summary>
    Api,
}

/// <summary>
/// One entry of the consent history (task F5): never changed (a trigger refuses updates), so the
/// history is proof of what was accepted, when and where. Holds no personal data besides the
/// person's id; kept when the person is erased.
/// </summary>
public sealed class ConsentRecord : ITenantOwned
{
    /// <summary>Maximum length of <see cref="VisitorReference"/>.</summary>
    public const int VisitorReferenceMaxLength = 100;

    /// <summary>Unique id (UUID version 7, so ids sort by time).</summary>
    public Guid Id { get; init; } = Guid.CreateVersion7();

    /// <inheritdoc />
    public Guid TenantId { get; set; }

    /// <summary>The person, when signed in or known.</summary>
    public Guid? UserId { get; init; }

    /// <summary>For visitors: what they did (for example <c>chat:…</c> or <c>ticket:…</c>).</summary>
    public string? VisitorReference { get; init; }

    /// <summary>What happened.</summary>
    public ConsentAction Action { get; init; }

    /// <summary>The notice accepted (also recorded with purposes: the notice current at the time).</summary>
    public Guid? NoticeId { get; init; }

    /// <summary>The notice's version number.</summary>
    public int? NoticeVersion { get; init; }

    /// <summary>The purpose granted or withdrawn.</summary>
    public Guid? PurposeId { get; init; }

    /// <summary>Where.</summary>
    public ConsentChannel Channel { get; init; }

    /// <summary>Staff member or integration user who recorded it for the person, if not the person.</summary>
    public Guid? RecordedById { get; init; }

    /// <summary>When.</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>Which right a data subject request uses (UAE PDPL, Federal Decree-Law 45 of 2021).</summary>
public enum DataSubjectRequestType
{
    /// <summary>A copy of the person's data, also in a machine-readable form to take elsewhere.</summary>
    Access,

    /// <summary>Correct wrong or incomplete data.</summary>
    Correction,

    /// <summary>Erase the person's personal data.</summary>
    Erasure,

    /// <summary>Restrict processing while something is checked.</summary>
    Restriction,

    /// <summary>Stop processing, or object to automated processing.</summary>
    Objection,
}

/// <summary>Where a data subject request stands.</summary>
public enum DataSubjectRequestState
{
    /// <summary>Received; nobody has started on it.</summary>
    Received,

    /// <summary>A privacy officer is working on it.</summary>
    InProgress,

    /// <summary>Done; the person was told.</summary>
    Completed,

    /// <summary>Refused with a reason; the person was told.</summary>
    Rejected,

    /// <summary>Withdrawn by the person before it was done.</summary>
    Withdrawn,
}

/// <summary>How the request arrived.</summary>
public enum DataSubjectRequestChannel
{
    /// <summary>The person, signed in, under Account → Privacy.</summary>
    Portal,

    /// <summary>Logged by staff (letter, phone, counter, email) after checking who the person is.</summary>
    Staff,
}

/// <summary>
/// A data subject request (GOV-5, task F5): a person asks to see, correct or erase their data, or
/// to restrict or stop its processing. Numbered <c>DSR-000123</c> per tenant, due after the
/// tenant's period (30 days by default), handled by holders of <c>privacy.requests</c>.
/// </summary>
public sealed class DataSubjectRequest : ITenantOwned, IAuditedEntity
{
    /// <summary>Maximum length of <see cref="Details"/>, <see cref="Resolution"/> and <see cref="IdentityCheck"/>.</summary>
    public const int TextMaxLength = 4000;

    /// <summary>Unique id (UUID version 7).</summary>
    public Guid Id { get; init; } = Guid.CreateVersion7();

    /// <inheritdoc />
    public Guid TenantId { get; set; }

    /// <summary>Number, for example <c>DSR-000123</c>.</summary>
    public required string Number { get; init; }

    /// <summary>Sequence behind the number, unique per tenant.</summary>
    public long Sequence { get; init; }

    /// <summary>The right used.</summary>
    public DataSubjectRequestType Type { get; init; }

    /// <summary>Where it stands.</summary>
    public DataSubjectRequestState State { get; set; } = DataSubjectRequestState.Received;

    /// <summary>The person the data is about.</summary>
    public Guid SubjectId { get; init; }

    /// <summary>How it arrived.</summary>
    public DataSubjectRequestChannel Channel { get; init; }

    /// <summary>What the person asks for, in their words. Personal data.</summary>
    [AuditLengthOnly]
    public string? Details { get; set; }

    /// <summary>For requests logged by staff: how the person's identity was checked.</summary>
    [AuditLengthOnly]
    public string? IdentityCheck { get; init; }

    /// <summary>Who logged it (the person themselves for portal requests).</summary>
    public Guid? SubmittedById { get; init; }

    /// <summary>The privacy officer working on it.</summary>
    public Guid? HandlerId { get; set; }

    /// <summary>When it was received.</summary>
    public DateTimeOffset ReceivedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>When it is due (received + the tenant's period).</summary>
    public DateTimeOffset DueAt { get; init; }

    /// <summary>When it was completed, rejected or withdrawn.</summary>
    public DateTimeOffset? ClosedAt { get; set; }

    /// <summary>What was done, or why it was refused; shown to the person.</summary>
    [AuditLengthOnly]
    public string? Resolution { get; set; }

    /// <summary>When a copy of the data was downloaded for it.</summary>
    public DateTimeOffset? ExportedAt { get; set; }

    /// <summary>When the person's data was erased for it.</summary>
    public DateTimeOffset? ErasedAt { get; set; }

    /// <summary>When it was last changed.</summary>
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Optimistic concurrency token (PostgreSQL <c>xmin</c>).</summary>
    public uint Version { get; init; }
}
