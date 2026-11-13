namespace Zelvo.Data.Tenancy;

/// <summary>The tenant the current request or job works for.</summary>
public interface ITenantContext
{
    /// <summary>Current tenant id, or <see langword="null"/> when no tenant is set.</summary>
    Guid? TenantId { get; }

    /// <summary>Current tenant slug, or <see langword="null"/> when no tenant is set.</summary>
    string? Slug { get; }

    /// <summary>Sets the tenant. It can be set once per scope; changing it throws.</summary>
    void SetTenant(Guid tenantId, string slug);
}

/// <summary>Scoped <see cref="ITenantContext"/>: one per request or job.</summary>
public sealed class TenantContext : ITenantContext
{
    /// <inheritdoc />
    public Guid? TenantId { get; private set; }

    /// <inheritdoc />
    public string? Slug { get; private set; }

    /// <inheritdoc />
    public void SetTenant(Guid tenantId, string slug)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(slug);
        if (tenantId == Guid.Empty)
        {
            throw new ArgumentException("Tenant id must not be empty.", nameof(tenantId));
        }

        if (TenantId is { } current && current != tenantId)
        {
            throw new InvalidOperationException("The tenant of this scope is already set and cannot change.");
        }

        TenantId = tenantId;
        Slug = slug;
    }
}
