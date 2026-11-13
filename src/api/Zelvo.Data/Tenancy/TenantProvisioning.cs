using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Zelvo.Data.Auditing;
using Zelvo.Data.Localization;

namespace Zelvo.Data.Tenancy;

/// <summary>
/// Creates tenants. This is an installation-level operation run with the migration owner's
/// connection (for example by <c>Zelvo.Api migrate</c>), not through the tenant-scoped API.
/// </summary>
public static partial class TenantProvisioning
{
    /// <summary>Lower-case letters, digits and inner hyphens; 1 to 63 characters.</summary>
    [GeneratedRegex("^[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?$", RegexOptions.CultureInvariant)]
    public static partial Regex SlugPattern();

    /// <summary>
    /// Creates the tenant unless one with <paramref name="slug"/> exists. Writes <c>tenant.created</c>
    /// to the new tenant's audit log, as the system actor. Returns the tenant and whether it was created.
    /// </summary>
    public static async Task<(Tenant Tenant, bool Created)> EnsureTenantAsync(
        string ownerConnectionString, string slug, LocalizedText name, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (!SlugPattern().IsMatch(slug))
        {
            throw new ArgumentException("A tenant slug is 1–63 lower-case letters, digits or hyphens.", nameof(slug));
        }

        await using (var lookup = CreateContext(ownerConnectionString, new TenantContext()))
        {
            var existing = await lookup.Tenants.AsNoTracking()
                .SingleOrDefaultAsync(t => t.Slug == slug, cancellationToken).ConfigureAwait(false);
            if (existing is not null)
            {
                return (existing, false);
            }
        }

        if (!name.HasAnyValue)
        {
            throw new ArgumentException("A tenant needs a name in English or Arabic.", nameof(name));
        }

        var tenant = new Tenant { Slug = slug, Name = name };

        // Act as the new tenant, so row-level security accepts its first audit entry even when the
        // owner role is not a superuser.
        var tenantContext = new TenantContext();
        tenantContext.SetTenant(tenant.Id, tenant.Slug);
        await using var db = CreateContext(ownerConnectionString, tenantContext);
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return (tenant, true);
    }

    private static ZelvoDbContext CreateContext(string connectionString, TenantContext tenantContext)
    {
        var auditContext = new AuditContext();
        auditContext.SetActor(AuditActorType.System, "provisioning", null);
        return new ZelvoDbContext(DataServiceCollectionExtensions.CreateOptions(
            connectionString,
            new TenantConnectionInterceptor(tenantContext),
            new AuditSaveChangesInterceptor(tenantContext, auditContext)));
    }
}
