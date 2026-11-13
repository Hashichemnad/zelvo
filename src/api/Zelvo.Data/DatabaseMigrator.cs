using Microsoft.EntityFrameworkCore;
using Zelvo.Data.Auditing;
using Zelvo.Data.Tenancy;

namespace Zelvo.Data;

/// <summary>Applies migrations with the database owner's connection.</summary>
public static class DatabaseMigrator
{
    /// <summary>Applies every pending migration. Safe to run repeatedly and from several instances.</summary>
    public static async Task MigrateAsync(string ownerConnectionString, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerConnectionString);
        var tenantContext = new TenantContext();
        await using var db = new ZelvoDbContext(DataServiceCollectionExtensions.CreateOptions(
            ownerConnectionString,
            new TenantConnectionInterceptor(tenantContext),
            new AuditSaveChangesInterceptor(tenantContext, new AuditContext())));
        await db.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
    }
}
