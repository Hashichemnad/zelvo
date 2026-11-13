using Microsoft.EntityFrameworkCore;
using Zelvo.Data;
using Zelvo.Data.Auditing;
using Zelvo.Data.Identity;
using Zelvo.Data.Tenancy;

namespace Zelvo.Api.Identity;

/// <summary>
/// Gives a tenant its built-in roles, their default grants and its sign-in settings. Safe to run
/// on every start: a default grant is given once per tenant and never again, so an administrator's
/// later change is not undone. Runs with the migration owner's connection, which is not limited by
/// row-level security, so every query here filters by tenant itself.
/// </summary>
internal static class IdentityProvisioning
{
    public static async Task EnsureTenantIdentityAsync(string ownerConnectionString, Tenant tenant, CancellationToken cancellationToken = default)
    {
        await using var db = CreateContext(ownerConnectionString, tenant);

        var roles = await db.Roles.Where(r => r.TenantId == tenant.Id).Include(r => r.Permissions).Include(r => r.FieldRules)
            .ToDictionaryAsync(r => r.Key, StringComparer.Ordinal, cancellationToken).ConfigureAwait(false);
        var given = (await db.RoleDefaultGrants.Where(g => g.TenantId == tenant.Id).Select(g => new { g.RoleKey, g.Grant })
                .ToListAsync(cancellationToken).ConfigureAwait(false))
            .Select(g => (g.RoleKey, g.Grant))
            .ToHashSet();

        foreach (var defaults in PermissionCatalog.Roles)
        {
            if (!roles.TryGetValue(defaults.Key, out var role))
            {
                role = new Role
                {
                    Key = defaults.Key,
                    Name = defaults.Name,
                    IsSystem = true,
                    RequiresMfa = BuiltInRoles.AlwaysRequireMfa.Contains(defaults.Key),
                };
                db.Roles.Add(role);
            }

            foreach (var permission in defaults.Permissions)
            {
                var grant = $"permission:{permission}";
                if (given.Add((defaults.Key, grant)))
                {
                    db.RoleDefaultGrants.Add(new RoleDefaultGrant { RoleKey = defaults.Key, Grant = grant });
                    if (role.Permissions.All(p => p.Permission != permission))
                    {
                        role.Permissions.Add(new RoleGrant { Permission = permission });
                    }
                }
            }

            foreach (var (field, access) in defaults.FieldRules)
            {
                var grant = $"field:{field}";
                if (given.Add((defaults.Key, grant)))
                {
                    db.RoleDefaultGrants.Add(new RoleDefaultGrant { RoleKey = defaults.Key, Grant = grant });
                    var parts = field.Split('.', 2);
                    if (role.FieldRules.All(f => f.Resource != parts[0] || f.Field != parts[1]))
                    {
                        role.FieldRules.Add(new RoleFieldRule { Resource = parts[0], Field = parts[1], Access = access });
                    }
                }
            }
        }

        if (!await db.IdentitySettings.AnyAsync(s => s.TenantId == tenant.Id, cancellationToken).ConfigureAwait(false))
        {
            db.IdentitySettings.Add(new IdentitySettings());
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Creates or updates the Zelvo record of a Keycloak user and gives it <paramref name="roleKeys"/>.
    /// Used for development users; real users are created at their first sign-in.
    /// </summary>
    public static async Task<UserAccount> EnsureUserAsync(
        string ownerConnectionString, Tenant tenant, string subject, string? email, string? displayName,
        IReadOnlyCollection<string> roleKeys, CancellationToken cancellationToken = default)
    {
        await using var db = CreateContext(ownerConnectionString, tenant);
        var user = await db.Users.Include(u => u.Roles)
            .SingleOrDefaultAsync(u => u.TenantId == tenant.Id && u.Subject == subject, cancellationToken).ConfigureAwait(false);
        if (user is null)
        {
            user = new UserAccount { Subject = subject, Email = email, DisplayName = displayName };
            db.Users.Add(user);
        }

        var roles = await db.Roles.Where(r => r.TenantId == tenant.Id && roleKeys.Contains(r.Key)).ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var role in roles.Where(role => user.Roles.All(r => r.RoleId != role.Id)))
        {
            user.Roles.Add(new UserRole { RoleId = role.Id, UserId = user.Id });
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return user;
    }

    private static ZelvoDbContext CreateContext(string connectionString, Tenant tenant)
    {
        var tenantContext = new TenantContext();
        tenantContext.SetTenant(tenant.Id, tenant.Slug);
        var auditContext = new AuditContext();
        auditContext.SetActor(AuditActorType.System, "provisioning", null);
        return new ZelvoDbContext(DataServiceCollectionExtensions.CreateOptions(
            connectionString,
            new TenantConnectionInterceptor(tenantContext),
            new AuditSaveChangesInterceptor(tenantContext, auditContext)));
    }
}
