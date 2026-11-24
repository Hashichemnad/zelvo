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
}
