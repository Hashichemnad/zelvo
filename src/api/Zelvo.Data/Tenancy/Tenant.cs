using Zelvo.Data.Auditing;
using Zelvo.Data.Localization;

namespace Zelvo.Data.Tenancy;

/// <summary>
/// An organisation using this installation. Every tenant-owned row carries the tenant's id, and
/// PostgreSQL row-level security only shows rows of the tenant set on the connection.
/// </summary>
public sealed class Tenant : IAuditedEntity
{
    /// <summary>Maximum length of <see cref="Slug"/>.</summary>
    public const int SlugMaxLength = 63;

    /// <summary>Maximum length of each language of <see cref="Name"/>.</summary>
    public const int NameMaxLength = 200;

    /// <summary>Unique id (UUID version 7).</summary>
    public Guid Id { get; init; } = Guid.CreateVersion7();

    /// <summary>Stable, URL-safe identifier, for example <c>ajman-municipality</c>.</summary>
    public required string Slug { get; init; }

    /// <summary>Display name in English and Arabic.</summary>
    public required LocalizedText Name { get; set; }

    /// <summary>Whether the tenant can be used.</summary>
    public TenantStatus Status { get; set; } = TenantStatus.Active;

    /// <summary>When the tenant was created.</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>When the tenant was last changed.</summary>
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Optimistic concurrency token (PostgreSQL <c>xmin</c>).</summary>
    public uint Version { get; init; }
}

/// <summary>Lifecycle state of a tenant.</summary>
public enum TenantStatus
{
    /// <summary>In use.</summary>
    Active,

    /// <summary>Blocked: the API refuses its requests; data is kept.</summary>
    Suspended,
}
