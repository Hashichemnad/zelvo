using Zelvo.Data.Auditing;
using Zelvo.Data.Localization;
using Zelvo.Data.Tenancy;

namespace Zelvo.Data.Cmdb;

/// <summary>
/// The six families of configuration item classes that module 18 names. Every class belongs to
/// one; sub-classes take their parent's.
/// </summary>
public enum CiFamily
{
    /// <summary>Servers, laptops, printers and other physical devices.</summary>
    Hardware,

    /// <summary>Installed software and operating systems.</summary>
    Software,

    /// <summary>What the organisation offers its customers or staff, for example "Email" or "Payroll".</summary>
    BusinessService,

    /// <summary>Applications and their components.</summary>
    Application,

    /// <summary>Routers, switches, firewalls, links.</summary>
    Network,

    /// <summary>Virtual machines, databases, storage and other cloud resources.</summary>
    CloudResource,
}

/// <summary>Where a configuration item stands.</summary>
public enum CiStatus
{
    /// <summary>Planned or being built; not yet in use.</summary>
    Planned,

    /// <summary>In use.</summary>
    Active,

    /// <summary>In use, under maintenance or out of service for a while.</summary>
    Maintenance,

    /// <summary>No longer in use. Kept for history; cannot be linked to new tickets.</summary>
    Retired,
}

/// <summary>
/// What a relationship's other end is. <see cref="ConfigurationItem"/> for most types; the built-in
/// "used by" type points at the people who use an item (product owner decision 2026-11-26: users,
/// departments and customer groups), so changes (H5) know whom they affect.
/// </summary>
public enum RelationshipTargetKind
{
    /// <summary>Another configuration item.</summary>
    ConfigurationItem,

    /// <summary>A user, a department or a customer group.</summary>
    Consumer,
}

/// <summary>
/// Which way trouble travels along a relationship of a type, for impact traversal ("what depends on
/// this item"). For "A runs on B", a fault of B affects A: <see cref="ToSource"/>.
/// </summary>
public enum ImpactDirection
{
    /// <summary>A fault of the target affects the source (runs on, depends on).</summary>
    ToSource,

    /// <summary>A fault of the source affects the target (used by).</summary>
    ToTarget,

    /// <summary>Either end affects the other (connects to).</summary>
    Both,

    /// <summary>The relationship is for information only.</summary>
    None,
}

/// <summary>
/// A class of configuration items (module 18), for example Hardware → Server → Database server.
/// Classes form a tree of up to <see cref="MaxDepth"/> levels under one of the six
/// <see cref="CiFamily"/> families. Each class defines attributes (C1 field definitions as JSON);
/// an item of a class has the attributes of the class and of every class above it.
/// </summary>
public sealed class CiClass : ITenantOwned, IAuditedEntity
{
    /// <summary>Deepest level of the class tree (1 is the top).</summary>
    public const int MaxDepth = 4;

    /// <summary>Maximum length of each language of <see cref="Name"/>.</summary>
    public const int NameMaxLength = 150;

    /// <summary>Maximum length of each language of <see cref="Description"/>.</summary>
    public const int DescriptionMaxLength = 1000;

    /// <summary>Maximum length of <see cref="Key"/>.</summary>
    public const int KeyMaxLength = 50;

    /// <summary>Unique id (UUID version 7).</summary>
    public Guid Id { get; init; } = Guid.CreateVersion7();

    /// <inheritdoc />
    public Guid TenantId { get; set; }

    /// <summary>Stable key, unique in the tenant, for example <c>database-server</c> (imports and the API use it).</summary>
    public required string Key { get; init; }

    /// <summary>The class above; <see langword="null"/> at the top.</summary>
    public Guid? ParentId { get; init; }

    /// <summary>Level in the tree: 1 at the top.</summary>
    public int Depth { get; init; } = 1;

    /// <summary>Family; sub-classes have their parent's.</summary>
    public CiFamily Family { get; init; }

    /// <summary>Name in English and Arabic.</summary>
    public required LocalizedText Name { get; set; }

    /// <summary>What the class is for, in English and Arabic (either may be empty).</summary>
    public LocalizedText Description { get; set; } = new(null, null);

    /// <summary>
    /// The class's own attributes: a JSON list of C1 form fields (<c>Zelvo.Tickets.Forms.FormField</c>).
    /// Only the field types that fit an item are allowed (no files, signatures, tables or rules).
    /// </summary>
    [AuditLengthOnly]
    public string Attributes { get; set; } = "[]";

    /// <summary>Order among the class's siblings (0 first).</summary>
    public int Position { get; set; }

    /// <summary>
    /// People without <c>cmdb.read</c> (requesters) may find and pick items of this class in forms,
    /// for example business services and applications. Off by default: servers and network devices
    /// stay hidden from them.
    /// </summary>
    public bool RequesterVisible { get; set; }

    /// <summary>Inactive classes keep their items but take no new ones.</summary>
    public bool Active { get; set; } = true;

    /// <summary>
    /// Identification rules (task G2): a JSON list of rules, each a list of attribute keys whose
    /// values together identify an item, tried in order, for example
    /// <c>[["serialNumber"],["hostName"]]</c>. <see langword="null"/>: the class above decides, and at
    /// the top Zelvo's defaults (serial number, cloud resource id, MAC address, host name).
    /// </summary>
    public string? IdentificationRules { get; set; }

    /// <summary>When the class was created.</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>When the class was last changed.</summary>
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Optimistic concurrency token (PostgreSQL <c>xmin</c>).</summary>
    public uint Version { get; init; }
}

/// <summary>
/// A configuration item (module 18): a server, an application, a business service, a laptop.
/// Numbered <c>CI-000123</c> per tenant (prefix a tenant setting), never reused: items are retired,
/// and deleted only while nothing refers to them.
/// </summary>
public sealed class ConfigurationItem : ITenantOwned, IAuditedEntity
{
    /// <summary>Maximum length of <see cref="Number"/>.</summary>
    public const int NumberMaxLength = 20;

    /// <summary>Maximum length of <see cref="Name"/>.</summary>
    public const int NameMaxLength = 200;

    /// <summary>Maximum length of each language of <see cref="Description"/>.</summary>
    public const int DescriptionMaxLength = 4000;

    /// <summary>Unique id (UUID version 7).</summary>
    public Guid Id { get; init; } = Guid.CreateVersion7();

    /// <inheritdoc />
    public Guid TenantId { get; set; }

    /// <summary>Number shown to people, for example <c>CI-000123</c>.</summary>
    public required string Number { get; init; }

    /// <summary>Sequence behind <see cref="Number"/>, per tenant.</summary>
    public long Sequence { get; init; }

    /// <summary>The item's class.</summary>
    public Guid ClassId { get; set; }

    /// <summary>
    /// Name, for example <c>srv-app-01</c> or "Payroll". One name in any language: items are
    /// usually named by their technical name, which is the same in English and Arabic.
    /// </summary>
    public required string Name { get; set; }

    /// <summary>What the item is, in English and Arabic (either may be empty).</summary>
    public LocalizedText Description { get; set; } = new(null, null);

    /// <summary>Where the item stands.</summary>
    public CiStatus Status { get; set; } = CiStatus.Active;

    /// <summary>The group that looks after the item (B2).</summary>
    public Guid? OwnerGroupId { get; set; }

    /// <summary>Where the item is (B2 location tree).</summary>
    public Guid? LocationId { get; set; }

    /// <summary>Attribute values by key, checked against the class's attributes (JSON object).</summary>
    public string Attributes { get; set; } = "{}";

    /// <summary>
    /// Who set each value last (task G2): a JSON object from attribute key (and <c>name</c>) to a
    /// discovery source id, or <c>manual</c> for people and imports. Reconciliation lets a source
    /// overwrite a value only when its precedence for the key is at least that of the last setter.
    /// </summary>
    [AuditLengthOnly]
    public string AttributeSources { get; set; } = "{}";

    /// <summary>Since when every source that reported the item stopped reporting it (task G2). Items are marked, never deleted.</summary>
    public DateTimeOffset? MissingSince { get; set; }

    /// <summary>The item this one was merged into from the review queue (it is then retired).</summary>
    public Guid? MergedIntoId { get; set; }

    /// <summary>When the item was created.</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>When the item was last changed.</summary>
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Optimistic concurrency token (PostgreSQL <c>xmin</c>).</summary>
    public uint Version { get; init; }
}

/// <summary>Last configuration item number given, per tenant.</summary>
public sealed class CiNumberSequence : ITenantOwned
{
    /// <inheritdoc />
    public Guid TenantId { get; set; }

    /// <summary>Last number given.</summary>
    public long LastValue { get; set; }
}

/// <summary>
/// A kind of relationship between configuration items, with a bilingual name for each direction
/// ("runs on" / "hosts"). Four are built in (runs on, depends on, connects to, used by) and can be
/// renamed but not deleted; administrators add their own.
/// </summary>
public sealed class CiRelationshipType : ITenantOwned, IAuditedEntity
{
    /// <summary>Maximum length of <see cref="Key"/>.</summary>
    public const int KeyMaxLength = 50;

    /// <summary>Maximum length of each language of the names.</summary>
    public const int NameMaxLength = 100;

    /// <summary>Unique id (UUID version 7).</summary>
    public Guid Id { get; init; } = Guid.CreateVersion7();

    /// <inheritdoc />
    public Guid TenantId { get; set; }

    /// <summary>Stable key, unique in the tenant, for example <c>runs-on</c>.</summary>
    public required string Key { get; init; }

    /// <summary>Seen from the source: "runs on".</summary>
    public required LocalizedText OutgoingName { get; set; }

    /// <summary>Seen from the target: "hosts".</summary>
    public required LocalizedText IncomingName { get; set; }

    /// <summary>Whether the target is another item or a consumer (users, departments, customer groups).</summary>
    public RelationshipTargetKind TargetKind { get; init; } = RelationshipTargetKind.ConfigurationItem;

    /// <summary>Which way faults travel, for impact traversal.</summary>
    public ImpactDirection Impact { get; set; } = ImpactDirection.ToSource;

    /// <summary>One of Zelvo's built-in types: renamed only.</summary>
    public bool BuiltIn { get; init; }

    /// <summary>Inactive types keep their relationships but take no new ones.</summary>
    public bool Active { get; set; } = true;

    /// <summary>When the type was created.</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>When the type was last changed.</summary>
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// A relationship: <see cref="SourceId"/> (an item) is related by <see cref="TypeId"/> to exactly
/// one target: another item, or (for "used by") a user, a department or a customer group.
/// </summary>
public sealed class CiRelationship : ITenantOwned, IAuditedEntity
{
    /// <summary>Maximum length of <see cref="Note"/>.</summary>
    public const int NoteMaxLength = 500;

    /// <summary>Unique id (UUID version 7).</summary>
    public Guid Id { get; init; } = Guid.CreateVersion7();

    /// <inheritdoc />
    public Guid TenantId { get; set; }

    /// <summary>The relationship type.</summary>
    public Guid TypeId { get; init; }

    /// <summary>The item the relationship starts from.</summary>
    public Guid SourceId { get; init; }

    /// <summary>The item it points at.</summary>
    public Guid? TargetCiId { get; init; }

    /// <summary>The user it points at ("used by").</summary>
    public Guid? TargetUserId { get; init; }

    /// <summary>The department it points at ("used by").</summary>
    public Guid? TargetDepartmentId { get; init; }

    /// <summary>The customer group it points at ("used by").</summary>
    public Guid? TargetCustomerGroupId { get; init; }

    /// <summary>Optional note, for example "primary" or "via VPN".</summary>
    public string? Note { get; set; }

    /// <summary>Who added it.</summary>
    public Guid? CreatedById { get; init; }

    /// <summary>When it was added.</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>A configuration item a ticket is about (incidents and service requests now; problems and changes later).</summary>
public sealed class TicketConfigurationItem : ITenantOwned, IAuditedEntity
{
    /// <inheritdoc />
    public Guid TenantId { get; set; }

    /// <summary>The ticket.</summary>
    public Guid TicketId { get; init; }

    /// <summary>The configuration item.</summary>
    public Guid ConfigurationItemId { get; init; }

    /// <summary>Who linked it (<see langword="null"/>: an integration or a form answer without a person).</summary>
    public Guid? AddedById { get; init; }

    /// <summary>When it was linked.</summary>
    public DateTimeOffset AddedAt { get; init; } = DateTimeOffset.UtcNow;
}
