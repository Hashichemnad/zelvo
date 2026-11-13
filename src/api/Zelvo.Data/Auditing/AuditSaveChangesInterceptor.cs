using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Zelvo.Data.Tenancy;

namespace Zelvo.Data.Auditing;

/// <summary>
/// Before every save: fills in the tenant of new tenant-owned rows, refuses rows of another
/// tenant, and adds an audit entry for each created, changed or deleted
/// <see cref="IAuditedEntity"/>. The entries are saved in the same transaction as the change.
/// </summary>
public sealed class AuditSaveChangesInterceptor(ITenantContext tenantContext, IAuditContext auditContext)
    : SaveChangesInterceptor
{
    /// <inheritdoc />
    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Prepare(eventData.Context);
        return result;
    }

    /// <inheritdoc />
    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Prepare(eventData.Context);
        return ValueTask.FromResult(result);
    }

    private void Prepare(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        context.ChangeTracker.DetectChanges();
        StampTenant(context);
        var events = AuditChangeCollector.Collect(context, tenantContext, auditContext);
        context.Set<AuditEvent>().AddRange(events);
    }

    private void StampTenant(DbContext context)
    {
        foreach (var entry in context.ChangeTracker.Entries<ITenantOwned>())
        {
            if (entry.State == EntityState.Added && entry.Entity.TenantId == Guid.Empty)
            {
                entry.Entity.TenantId = tenantContext.TenantId
                    ?? throw new InvalidOperationException("A tenant-owned record needs a tenant, but none is set.");
            }
            else if (entry.State is EntityState.Added or EntityState.Modified
                     && entry.Entity.TenantId != tenantContext.TenantId)
            {
                // Row-level security refuses this too; failing here gives a clearer error.
                throw new InvalidOperationException("A record of another tenant cannot be written.");
            }
        }
    }
}
