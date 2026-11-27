using Zelvo.Data.Auditing;
using Zelvo.Data.Tenancy;

namespace Zelvo.Data.Cmdb;

/// <summary>
/// Where configuration items come from besides people (task G2; product owner decision
/// 2026-11-26): the discovery API any tool pushes to, and the systems Zelvo reads on a schedule.
/// AWS is deferred until a customer asks.
/// </summary>
public enum DiscoverySourceKind
{
    /// <summary>A tool of the customer's own pushes records to <c>POST /cmdb/discovery/sources/{id}/records</c>.</summary>
    DiscoveryApi,

    /// <summary>Microsoft Intune managed devices and their detected apps (Graph, through an Entra ID connector's app).</summary>
    Intune,

    /// <summary>Entra ID device objects (Graph, through an Entra ID connector's app).</summary>
    EntraDevices,

    /// <summary>Azure resources (Resource Graph, through an Entra ID connector's app).</summary>
    Azure,

    /// <summary>Active Directory computer objects (through an LDAP connector's connection).</summary>
    ActiveDirectory,

    /// <summary>VMware vCenter clusters, hosts and virtual machines (its REST API, credential of its own).</summary>
    VCenter,
}

/// <summary>Whether a reported record is tied to an item.</summary>
public enum SourceRecordState
{
    /// <summary>Tied to <see cref="CiSourceRecord.ConfigurationItemId"/>.</summary>
    Linked,

    /// <summary>Matches several items; waits in the review queue for a person.</summary>
    Review,
}

/// <summary>What the review queue asks a person to decide.</summary>
public enum CiReviewKind
{
    /// <summary>Two items share an identifying value (serial number, host name…): merge them or keep them apart.</summary>
    PossibleDuplicate,

    /// <summary>A reported record matches several items: link it to one, or make a new item.</summary>
    Ambiguous,
}

/// <summary>Where a review stands.</summary>
public enum CiReviewState
{
    /// <summary>Waiting for a person.</summary>
    Open,

    /// <summary>The two items were merged into one.</summary>
    Merged,

    /// <summary>The two items are different; they are not suggested again.</summary>
    KeptApart,

    /// <summary>The record was linked to one of the items it matched.</summary>
    Linked,

    /// <summary>A new item was made for the record.</summary>
    Created,

    /// <summary>No longer needed (an item was retired or the record went away).</summary>
    Closed,
}

/// <summary>
/// A discovery source of a tenant (task G2): its kind, the E7 connector whose app or connection it
/// reuses (Intune, Entra ID devices and Azure an Entra ID connector, Active Directory an LDAP one),
/// its settings, its own credential (vCenter only, encrypted), its schedule and its precedence in
/// reconciliation: a lower <see cref="Priority"/> wins, per attribute where set.
/// </summary>
public sealed class CiDiscoverySource : ITenantOwned, IAuditedEntity
{
    /// <summary>Maximum length of <see cref="Name"/>.</summary>
    public const int NameMaxLength = 100;

    /// <summary>Maximum length of <see cref="LastError"/>.</summary>
    public const int ErrorMaxLength = 1000;

    /// <summary>Highest (least trusted) priority.</summary>
    public const int LowestPriority = 100;

    /// <summary>Unique id.</summary>
    public Guid Id { get; init; } = Guid.CreateVersion7();

    /// <inheritdoc />
    public Guid TenantId { get; set; }

    /// <summary>Name shown to administrators.</summary>
    public required string Name { get; set; }

    /// <summary>What it reads, or that a tool pushes to it.</summary>
    public DiscoverySourceKind Kind { get; init; }

    /// <summary>The E7 connector whose app (Entra ID) or connection (LDAP) it uses.</summary>
    public Guid? ConnectorId { get; set; }

    /// <summary>Settings as JSON (subscriptions, resource types, vCenter address, class mapping…).</summary>
    public string Settings { get; set; } = "{}";

    /// <summary>Its own credential, encrypted (the vCenter password).</summary>
    [SensitiveData]
    public string? CredentialCiphertext { get; set; }

    /// <summary>When the credential was last set.</summary>
    public DateTimeOffset? CredentialSetAt { get; set; }

    /// <summary>Whether it syncs (or accepts pushes).</summary>
    public bool Active { get; set; } = true;

    /// <summary>Precedence of its values, 1 (most trusted) to 100.</summary>
    public int Priority { get; set; } = 50;

    /// <summary>Precedence per attribute key (and <c>name</c>), as a JSON object of numbers; others use <see cref="Priority"/>.</summary>
    public string AttributePriorities { get; set; } = "{}";

    /// <summary>Minutes between syncs (pull sources).</summary>
    public int SyncMinutes { get; set; } = 1440;

    /// <summary>When it syncs next.</summary>
    public DateTimeOffset? NextSyncAt { get; set; }

    /// <summary>The instance holding it until then.</summary>
    public DateTimeOffset? LeaseUntil { get; set; }

    /// <summary>When the last sync (or push) started.</summary>
    public DateTimeOffset? LastSyncAt { get; set; }

    /// <summary>Whether it worked.</summary>
    public bool? LastSyncSucceeded { get; set; }

    /// <summary>What the last sync did, as JSON counts.</summary>
    public string? LastSyncStats { get; set; }

    /// <summary>The last error.</summary>
    public string? LastError { get; set; }

    /// <summary>When it was added.</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>When it was last changed.</summary>
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Optimistic concurrency token (PostgreSQL <c>xmin</c>).</summary>
    public uint Version { get; init; }
}

/// <summary>
/// A record a source reports, by the source's own id, and the item it is tied to. Not audited
/// itself (a sync touches thousands): the items' changes are.
/// </summary>
public sealed class CiSourceRecord : ITenantOwned
{
    /// <summary>Maximum length of <see cref="ExternalId"/>.</summary>
    public const int ExternalIdMaxLength = 500;

    /// <summary>Maximum length of <see cref="PrimaryUser"/>.</summary>
    public const int PrimaryUserMaxLength = 320;

    /// <summary>Unique id.</summary>
    public Guid Id { get; init; } = Guid.CreateVersion7();

    /// <inheritdoc />
    public Guid TenantId { get; set; }

    /// <summary>The source.</summary>
    public Guid SourceId { get; init; }

    /// <summary>The source's id of the record (Intune device id, Azure resource id, objectGUID…).</summary>
    public required string ExternalId { get; init; }

    /// <summary>The item it is tied to (<see langword="null"/> while it waits for review).</summary>
    public Guid? ConfigurationItemId { get; set; }

    /// <summary>The class it was reported as.</summary>
    public Guid ClassId { get; set; }

    /// <summary>Its name as last reported.</summary>
    public required string Name { get; set; }

    /// <summary>Its attribute values as last reported (JSON object).</summary>
    public string Attributes { get; set; } = "{}";

    /// <summary>The person the source says uses it (an email or user principal name), for G3's assets.</summary>
    public string? PrimaryUser { get; set; }

    /// <summary>Linked or waiting for review.</summary>
    public SourceRecordState State { get; set; } = SourceRecordState.Linked;

    /// <summary>When the source first reported it.</summary>
    public DateTimeOffset FirstSeenAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>When the source last reported it.</summary>
    public DateTimeOffset LastSeenAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Since when a complete sync of the source no longer reports it.</summary>
    public DateTimeOffset? MissingSince { get; set; }
}

/// <summary>Software a source found installed on an item (Intune's detected apps, the discovery API), for licence compliance (G3).</summary>
public sealed class CiInstalledSoftware : ITenantOwned
{
    /// <summary>Maximum length of <see cref="Name"/>, <see cref="Publisher"/> and <see cref="ProductKey"/>.</summary>
    public const int NameMaxLength = 300;

    /// <summary>Maximum length of <see cref="Version"/>.</summary>
    public const int VersionMaxLength = 100;

    /// <summary>Unique id.</summary>
    public long Id { get; init; }

    /// <inheritdoc />
    public Guid TenantId { get; set; }

    /// <summary>The item it is installed on.</summary>
    public Guid ConfigurationItemId { get; set; }

    /// <summary>The source that reported it.</summary>
    public Guid SourceId { get; init; }

    /// <summary>The product, normalised (lower case, single spaces), to count installations per product.</summary>
    public required string ProductKey { get; init; }

    /// <summary>The name as reported.</summary>
    public required string Name { get; set; }

    /// <summary>The publisher as reported.</summary>
    public string? Publisher { get; set; }

    /// <summary>The version as reported (empty when none).</summary>
    public string Version { get; init; } = string.Empty;

    /// <summary>When the source last reported it.</summary>
    public DateTimeOffset LastSeenAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>A decision the review queue asks for: a possible duplicate pair, or a record that matches several items.</summary>
public sealed class CiReviewItem : ITenantOwned, IAuditedEntity
{
    /// <summary>Maximum length of <see cref="MatchedOn"/>.</summary>
    public const int MatchedOnMaxLength = 100;

    /// <summary>Maximum length of <see cref="MatchedValue"/>.</summary>
    public const int MatchedValueMaxLength = 500;

    /// <summary>Unique id.</summary>
    public Guid Id { get; init; } = Guid.CreateVersion7();

    /// <inheritdoc />
    public Guid TenantId { get; set; }

    /// <summary>What is to be decided.</summary>
    public CiReviewKind Kind { get; init; }

    /// <summary>Where it stands.</summary>
    public CiReviewState State { get; set; } = CiReviewState.Open;

    /// <summary>The first item of a duplicate pair.</summary>
    public Guid? ConfigurationItemId { get; set; }

    /// <summary>The second item of a duplicate pair.</summary>
    public Guid? OtherItemId { get; set; }

    /// <summary>The record that matches several items.</summary>
    public Guid? SourceRecordId { get; init; }

    /// <summary>The items an ambiguous record matches (JSON array of ids).</summary>
    public string Candidates { get; set; } = "[]";

    /// <summary>The attribute keys they share, for example <c>serialNumber</c>.</summary>
    public required string MatchedOn { get; init; }

    /// <summary>The value they share.</summary>
    public string? MatchedValue { get; init; }

    /// <summary>When it was raised.</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Who decided.</summary>
    public Guid? DecidedById { get; set; }

    /// <summary>When.</summary>
    public DateTimeOffset? DecidedAt { get; set; }
}
