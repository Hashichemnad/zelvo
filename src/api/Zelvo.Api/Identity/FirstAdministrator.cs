using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Zelvo.Data;
using Zelvo.Data.Auditing;
using Zelvo.Data.Identity;
using Zelvo.Data.Tenancy;
using Zelvo.Tickets.Branding;

namespace Zelvo.Api.Identity;

/// <summary>What <see cref="FirstAdministrator.EnsureAsync"/> did.</summary>
/// <param name="Token">The one-time setup token, shown once; null when the tenant already has an administrator.</param>
/// <param name="ExpiresAt">When the link stops working.</param>
internal sealed record FirstAdministratorResult(string? Token, DateTimeOffset? ExpiresAt)
{
    public static FirstAdministratorResult AlreadyHasAdministrator { get; } = new(null, null);
}

/// <summary>
/// Task M2: the first tenant administrator of a hardened installation (pentest pack, open point 1).
/// <c>migrate</c> creates the person given by <c>Tenancy:Bootstrap:FirstAdminEmail</c> in the
/// tenant's realm (email verified, the required action "configure authenticator app", no password),
/// gives them the tenant administrator role in Zelvo and makes a one-time setup link, which it
/// prints once. Until they use it, every run of <c>migrate</c> makes a fresh link (earlier ones stop
/// working); once the tenant has an administrator, it does nothing.
/// </summary>
internal static class FirstAdministrator
{
    /// <summary>A new random token (256 bits, URL-safe base64).</summary>
    public static string NewToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>What is stored for a token: lower-case hex SHA-256.</summary>
    public static string Hash(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    /// <summary>The email in lower case when it looks like an address, else null.</summary>
    public static string? NormalizeEmail(string? email)
    {
        var value = email?.Trim().ToLowerInvariant();
        return value is { Length: > 3 and <= UserAccount.EmailMaxLength } && value.IndexOf('@', StringComparison.Ordinal) is > 0 and var at
            && at < value.Length - 3 && value.IndexOf('.', at) > at + 1 && !value.Contains(' ', StringComparison.Ordinal)
            ? value
            : null;
    }

    /// <summary>
    /// Makes sure the tenant has its first administrator. <paramref name="createKeycloakUser"/> finds
    /// or creates the person in the tenant's realm and returns their Keycloak id.
    /// </summary>
    public static async Task<FirstAdministratorResult> EnsureAsync(
        string ownerConnectionString, Tenant tenant, string email, Func<CancellationToken, Task<string>> createKeycloakUser,
        TimeProvider time, CancellationToken cancellationToken)
    {
        await using (var db = CreateContext(ownerConnectionString, tenant))
        {
            var admins = await db.Users
                .Where(u => u.TenantId == tenant.Id && u.ErasedAt == null && u.Status == UserStatus.Active
                    && u.Roles.Any(r => r.Role!.Key == BuiltInRoles.TenantAdmin))
                .Include(u => u.Roles).ThenInclude(r => r.Role)
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            var ids = admins.Select(a => a.Id).ToList();
            var links = await db.AccountSetupLinks.Where(l => ids.Contains(l.UserId)).ToListAsync(cancellationToken).ConfigureAwait(false);

            // A real administrator: made otherwise (no setup link) or one who already chose a password.
            bool SetUp(UserAccount admin) => links.Where(l => l.UserId == admin.Id) is var own && (!own.Any() || own.Any(l => l.UsedAt != null));
            if (admins.Any(SetUp))
            {
                return FirstAdministratorResult.AlreadyHasAdministrator;
            }

            // First administrators who never used their link under another address (a mistyped
            // email corrected by the operator) lose the role and are disabled.
            var now = time.GetUtcNow();
            foreach (var pending in admins.Where(a => !string.Equals(a.Email, email, StringComparison.OrdinalIgnoreCase)))
            {
                pending.Roles.RemoveAll(r => r.Role!.Key == BuiltInRoles.TenantAdmin);
                pending.Status = UserStatus.Disabled;
                foreach (var link in links.Where(l => l.UserId == pending.Id && l.ExpiresAt > now))
                {
                    link.ExpiresAt = now;
                }
            }

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        var subject = await createKeycloakUser(cancellationToken).ConfigureAwait(false);
        var user = await IdentityProvisioning.EnsureUserAsync(ownerConnectionString, tenant, subject, email, email, [BuiltInRoles.TenantAdmin], cancellationToken)
            .ConfigureAwait(false);

        await using (var db = CreateContext(ownerConnectionString, tenant))
        {
            var now = time.GetUtcNow();
            foreach (var pending in await db.AccountSetupLinks.Where(l => l.UserId == user.Id && l.UsedAt == null && l.ExpiresAt > now).ToListAsync(cancellationToken).ConfigureAwait(false))
            {
                pending.ExpiresAt = now;
            }

            var token = NewToken();
            var link = new AccountSetupLink { UserId = user.Id, TokenHash = Hash(token), CreatedAt = now, ExpiresAt = now + AccountSetupLink.Lifetime };
            db.AccountSetupLinks.Add(link);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return new FirstAdministratorResult(token, link.ExpiresAt);
        }
    }

    /// <summary>
    /// Task M2: serves a tenant made by <c>migrate</c> on its own host (<c>Tenancy:Bootstrap:Host</c>)
    /// as a verified custom domain: the installation operator vouches for it, so no DNS proof is
    /// asked. Returns whether it was added now; false when the tenant has it already.
    /// </summary>
    public static async Task<bool> EnsureHostAsync(string ownerConnectionString, Tenant tenant, string host, TimeProvider time, CancellationToken cancellationToken)
    {
        var normalized = HostNames.Normalize(host) ?? throw new ArgumentException($"'{host}' is not a host name.", nameof(host));
        await using var db = CreateContext(ownerConnectionString, tenant);
        var existing = await db.TenantDomains.SingleOrDefaultAsync(d => d.TenantId == tenant.Id && d.Host == normalized, cancellationToken).ConfigureAwait(false);
        var now = time.GetUtcNow();
        if (existing is not null)
        {
            if (existing.Status != Data.Branding.DomainStatus.Verified)
            {
                existing.Status = Data.Branding.DomainStatus.Verified;
                existing.VerifiedAt = now;
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }

            return false;
        }

        db.TenantDomains.Add(new Data.Branding.TenantDomain
        {
            Host = normalized,
            VerificationToken = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16)),
            Status = Data.Branding.DomainStatus.Verified,
            VerifiedAt = now,
            LastCheckedAt = now,
        });
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>A context for <paramref name="tenant"/> on the owner's connection, audited as provisioning.</summary>
    internal static ZelvoDbContext CreateContext(string connectionString, Tenant tenant)
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
