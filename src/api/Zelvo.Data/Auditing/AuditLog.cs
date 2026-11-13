using Zelvo.Data.Tenancy;

namespace Zelvo.Data.Auditing;

/// <summary>
/// Writes audit entries that are not record changes: administrative actions, views of sensitive
/// data and security events. Record changes are audited automatically for
/// <see cref="IAuditedEntity"/> types.
/// </summary>
public interface IAuditLog
{
    /// <summary>Adds an entry that is saved with the next <c>SaveChanges</c>, in the same transaction.</summary>
    void Add(AuditCategory category, string action, string? entityType = null, string? entityId = null,
        IReadOnlyDictionary<string, object?>? details = null);

    /// <summary>Adds an entry and saves it now.</summary>
    Task RecordAsync(AuditCategory category, string action, string? entityType = null, string? entityId = null,
        IReadOnlyDictionary<string, object?>? details = null, CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public sealed class AuditLog(ZelvoDbContext db, ITenantContext tenantContext, IAuditContext auditContext) : IAuditLog
{
    /// <inheritdoc />
    public void Add(AuditCategory category, string action, string? entityType = null, string? entityId = null,
        IReadOnlyDictionary<string, object?>? details = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(action);
        var tenantId = tenantContext.TenantId
            ?? throw new InvalidOperationException("An audit entry needs a tenant, but none is set.");

        var metadata = new SortedDictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (key, value) in auditContext.Metadata)
        {
            metadata[key] = value;
        }

        if (details is { Count: > 0 })
        {
            metadata["details"] = new SortedDictionary<string, object?>(details.ToDictionary(), StringComparer.Ordinal);
        }

        db.AuditEvents.Add(new AuditEvent
        {
            TenantId = tenantId,
            Category = category,
            Action = action,
            ActorType = auditContext.ActorType,
            ActorId = auditContext.ActorId,
            ActorName = auditContext.ActorName,
            EntityType = entityType,
            EntityId = entityId,
            Metadata = metadata.Count == 0 ? null : AuditJson.Serialize(metadata),
        });
    }

    /// <inheritdoc />
    public async Task RecordAsync(AuditCategory category, string action, string? entityType = null, string? entityId = null,
        IReadOnlyDictionary<string, object?>? details = null, CancellationToken cancellationToken = default)
    {
        Add(category, action, entityType, entityId, details);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
