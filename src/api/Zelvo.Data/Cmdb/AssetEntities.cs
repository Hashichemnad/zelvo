using Zelvo.Data.Auditing;
using Zelvo.Data.Tenancy;

namespace Zelvo.Data.Cmdb;

/// <summary>
/// Where an asset is in its life (module 18): requested, ordered, received, in stock, assigned,
/// in repair, retired, disposed.
/// </summary>
public enum AssetState
{
    /// <summary>Asked for (often by a service request); not bought yet.</summary>
    Requested,

    /// <summary>Ordered from the supplier.</summary>
    Ordered,

    /// <summary>Delivered; not in stock or handed out yet.</summary>
    Received,

    /// <summary>In stock, ready to hand out.</summary>
    InStock,

    /// <summary>Handed to a person.</summary>
    Assigned,

    /// <summary>Away for repair.</summary>
    InRepair,

    /// <summary>No longer in use; may still be disposed of.</summary>
    Retired,

    /// <summary>Disposed of (sold, recycled, destroyed). Final.</summary>
    Disposed,
}

/// <summary>What a contract covers.</summary>
public enum AssetContractKind
{
    /// <summary>The manufacturer's or seller's warranty.</summary>
    Warranty,

    /// <summary>A support contract.</summary>
    Support,

    /// <summary>A maintenance contract.</summary>
    Maintenance,

    /// <summary>A lease or rental.</summary>
    Lease,
}

/// <summary>What a software licence counts.</summary>
public enum LicenseMetric
{
    /// <summary>One entitlement per device it is installed on.</summary>
    Device,

    /// <summary>One entitlement per person using it (devices without a person count once each).</summary>
    User,
}

/// <summary>
/// An asset (task G3, module 18): a hardware or software configuration item with an asset tag
/// (<c>AST-000123</c> or one of the organisation's own), its lifecycle, the person it is assigned to
/// and the service request that asked for it. One per item; the item's status follows the state.
/// </summary>
public sealed class Asset : ITenantOwned, IAuditedEntity
{
    /// <summary>Maximum length of <see cref="Tag"/> (labels carry it as QR and Code 128).</summary>
    public const int TagMaxLength = 32;

    /// <summary>Maximum length of <see cref="Supplier"/> and <see cref="OrderNumber"/>.</summary>
    public const int SupplierMaxLength = 200;

    /// <summary>Maximum length of <see cref="Notes"/>.</summary>
    public const int NotesMaxLength = 2000;

    /// <summary>The configuration item (also the asset's key).</summary>
    public Guid ConfigurationItemId { get; init; }

    /// <inheritdoc />
    public Guid TenantId { get; set; }

    /// <summary>The asset tag, unique in the tenant: capital letters, digits and hyphens.</summary>
    public required string Tag { get; set; }

    /// <summary>Where it is in its life.</summary>
    public AssetState State { get; set; } = AssetState.InStock;

    /// <summary>The person it is assigned to (only while <see cref="AssetState.Assigned"/>).</summary>
    public Guid? AssignedToId { get; set; }

    /// <summary>When it was handed to them.</summary>
    public DateTimeOffset? AssignedAt { get; set; }

    /// <summary>The service request that asked for it.</summary>
    public Guid? RequestTicketId { get; set; }

    /// <summary>When it was bought.</summary>
    public DateOnly? PurchaseDate { get; set; }

    /// <summary>What it cost, in AED.</summary>
    public decimal? PurchaseCost { get; set; }

    /// <summary>Who it was bought from.</summary>
    public string? Supplier { get; set; }

    /// <summary>The purchase order or invoice number.</summary>
    public string? OrderNumber { get; set; }

    /// <summary>Notes.</summary>
    public string? Notes { get; set; }

    /// <summary>When it became an asset.</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>When it was last changed.</summary>
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Optimistic concurrency token (PostgreSQL <c>xmin</c>).</summary>
    public uint Version { get; init; }
}

/// <summary>Last generated asset tag number, per tenant.</summary>
public sealed class AssetTagSequence : ITenantOwned
{
    /// <inheritdoc />
    public Guid TenantId { get; set; }

    /// <summary>Last number given.</summary>
    public long LastValue { get; set; }
}

/// <summary>
/// A contract or warranty (task G3) on one or more assets, with the date it ends; an alert goes
/// out a set number of days before.
/// </summary>
public sealed class AssetContract : ITenantOwned, IAuditedEntity
{
    /// <summary>Maximum length of <see cref="Name"/>, <see cref="Supplier"/>.</summary>
    public const int NameMaxLength = 200;

    /// <summary>Maximum length of <see cref="Reference"/>.</summary>
    public const int ReferenceMaxLength = 100;

    /// <summary>Unique id.</summary>
    public Guid Id { get; init; } = Guid.CreateVersion7();

    /// <inheritdoc />
    public Guid TenantId { get; set; }

    /// <summary>What it is.</summary>
    public AssetContractKind Kind { get; set; } = AssetContractKind.Warranty;

    /// <summary>Name, for example "HP Care Pack 3 years".</summary>
    public required string Name { get; set; }

    /// <summary>The supplier's reference.</summary>
    public string? Reference { get; set; }

    /// <summary>The supplier.</summary>
    public string? Supplier { get; set; }

    /// <summary>When it starts.</summary>
    public DateOnly? StartsOn { get; set; }

    /// <summary>The last day it covers.</summary>
    public DateOnly EndsOn { get; set; }

    /// <summary>What it costs, in AED.</summary>
    public decimal? CostAed { get; set; }

    /// <summary>Notes.</summary>
    public string? Notes { get; set; }

    /// <summary>Who is told before it ends, besides the asset managers.</summary>
    public Guid? OwnerId { get; set; }

    /// <summary>The end date the expiry alert went out for (a new end date gets a new alert).</summary>
    public DateOnly? ExpiryAlertedFor { get; set; }

    /// <summary>When it was added.</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>When it was last changed.</summary>
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Optimistic concurrency token (PostgreSQL <c>xmin</c>).</summary>
    public uint Version { get; init; }
}

/// <summary>An asset a contract covers.</summary>
public sealed class AssetContractItem : ITenantOwned, IAuditedEntity
{
    /// <inheritdoc />
    public Guid TenantId { get; set; }

    /// <summary>The contract.</summary>
    public Guid ContractId { get; init; }

    /// <summary>The asset (its configuration item).</summary>
    public Guid ConfigurationItemId { get; init; }
}

/// <summary>
/// A software licence (task G3): an entitlement to a product, counted per device or per person,
/// compared with the installations G2's discovery found (the products are discovery's product keys).
/// </summary>
public sealed class SoftwareLicense : ITenantOwned, IAuditedEntity
{
    /// <summary>Maximum length of <see cref="Name"/>, <see cref="Publisher"/>, <see cref="Supplier"/>.</summary>
    public const int NameMaxLength = 200;

    /// <summary>Most products one licence covers.</summary>
    public const int MaxProducts = 20;

    /// <summary>Unique id.</summary>
    public Guid Id { get; init; } = Guid.CreateVersion7();

    /// <inheritdoc />
    public Guid TenantId { get; set; }

    /// <summary>Name, for example "Microsoft 365 E3".</summary>
    public required string Name { get; set; }

    /// <summary>The publisher.</summary>
    public string? Publisher { get; set; }

    /// <summary>The discovered products it covers (JSON array of product keys).</summary>
    public string ProductKeys { get; set; } = "[]";

    /// <summary>What it counts.</summary>
    public LicenseMetric Metric { get; set; } = LicenseMetric.Device;

    /// <summary>How many it entitles.</summary>
    public int Quantity { get; set; }

    /// <summary>When it starts.</summary>
    public DateOnly? StartsOn { get; set; }

    /// <summary>When it ends (subscriptions); none for perpetual licences.</summary>
    public DateOnly? EndsOn { get; set; }

    /// <summary>The supplier.</summary>
    public string? Supplier { get; set; }

    /// <summary>The supplier's or agreement's reference.</summary>
    public string? Reference { get; set; }

    /// <summary>What it costs, in AED.</summary>
    public decimal? CostAed { get; set; }

    /// <summary>Notes.</summary>
    public string? Notes { get; set; }

    /// <summary>Who is told before it ends, besides the asset managers.</summary>
    public Guid? OwnerId { get; set; }

    /// <summary>The end date the expiry alert went out for.</summary>
    public DateOnly? ExpiryAlertedFor { get; set; }

    /// <summary>When it was added.</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>When it was last changed.</summary>
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Optimistic concurrency token (PostgreSQL <c>xmin</c>).</summary>
    public uint Version { get; init; }
}
