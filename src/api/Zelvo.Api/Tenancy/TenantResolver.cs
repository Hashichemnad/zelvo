using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Zelvo.Data;

namespace Zelvo.Api.Tenancy;

/// <summary>A tenant found by its slug.</summary>
internal sealed class ResolvedTenant
{
    public Guid Id { get; init; }

    public required string Slug { get; init; }

    public required string Status { get; init; }
}

/// <summary>
/// Looks tenants up by slug through <c>zelvo_resolve_tenant</c>, which works before a tenant is
/// set on the connection. Results are cached briefly.
/// </summary>
internal sealed class TenantResolver(ZelvoDbContext db, IMemoryCache cache)
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(30);

    public async Task<ResolvedTenant?> ResolveAsync(string slug, CancellationToken cancellationToken)
    {
        var key = $"zelvo:tenant:{slug}";
        if (cache.TryGetValue(key, out ResolvedTenant? cached))
        {
            return cached;
        }

        var results = await db.Database.SqlQuery<ResolvedTenant>(
                $"SELECT id, slug, status FROM zelvo_resolve_tenant({slug})")
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var tenant = results.SingleOrDefault();
        cache.Set(key, tenant, CacheDuration);
        return tenant;
    }
}
