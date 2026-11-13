using System.Reflection;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Zelvo.Data.Tenancy;

namespace Zelvo.Data.Auditing;

/// <summary>Turns pending changes of <see cref="IAuditedEntity"/> records into audit entries.</summary>
internal static class AuditChangeCollector
{
    public static List<AuditEvent> Collect(DbContext context, ITenantContext tenantContext, IAuditContext auditContext)
    {
        var events = new List<AuditEvent>();
        foreach (var entry in context.ChangeTracker.Entries().ToList())
        {
            if (entry.Entity is not IAuditedEntity
                || entry.State is not (EntityState.Added or EntityState.Modified or EntityState.Deleted))
            {
                continue;
            }

            var changes = CollectChanges(entry);
            if (entry.State == EntityState.Modified && changes.Count == 0)
            {
                continue;
            }

            var entityType = EntityTypeName(entry);
            var (category, verb) = entry.State switch
            {
                EntityState.Added => (AuditCategory.Create, "created"),
                EntityState.Modified => (AuditCategory.Change, "updated"),
                _ => (AuditCategory.Delete, "deleted"),
            };

            events.Add(new AuditEvent
            {
                TenantId = TenantOf(entry.Entity, tenantContext),
                Category = category,
                Action = $"{entityType}.{verb}",
                ActorType = auditContext.ActorType,
                ActorId = auditContext.ActorId,
                ActorName = auditContext.ActorName,
                EntityType = entityType,
                EntityId = KeyOf(entry),
                Changes = AuditJson.Serialize(changes),
                Metadata = auditContext.Metadata.Count == 0 ? null : AuditJson.Serialize(auditContext.Metadata),
            });
        }

        return events;
    }

    /// <summary>Stable entity type name in snake case, for example <c>service_request</c>.</summary>
    internal static string EntityTypeName(EntityEntry entry) => ToSnakeCase(entry.Metadata.ClrType.Name);

    private static Guid TenantOf(object entity, ITenantContext tenantContext) => entity switch
    {
        ITenantOwned owned => owned.TenantId,
        Tenant tenant => tenant.Id,
        _ => tenantContext.TenantId
            ?? throw new InvalidOperationException("An audited change needs a tenant, but none is set."),
    };

    private static string KeyOf(EntityEntry entry)
    {
        var key = entry.Metadata.FindPrimaryKey()
            ?? throw new InvalidOperationException($"{entry.Metadata.DisplayName()} has no primary key.");
        return string.Join(',', key.Properties.Select(p => Convert.ToString(entry.Property(p.Name).CurrentValue, System.Globalization.CultureInfo.InvariantCulture)));
    }

    private static SortedDictionary<string, FieldChange> CollectChanges(EntityEntry entry)
    {
        var changes = new SortedDictionary<string, FieldChange>(StringComparer.Ordinal);
        foreach (var property in entry.Properties)
        {
            Add(changes, entry.State, FieldName(property.Metadata.Name), property, property.Metadata.PropertyInfo);
        }

        foreach (var complex in entry.ComplexProperties)
        {
            if (complex is not ComplexPropertyEntry single)
            {
                continue;
            }

            var prefix = FieldName(complex.Metadata.Name);
            foreach (var property in single.Properties)
            {
                Add(changes, entry.State, $"{prefix}.{FieldName(property.Metadata.Name)}", property, property.Metadata.PropertyInfo);
            }
        }

        return changes;
    }

    private static void Add(
        SortedDictionary<string, FieldChange> changes, EntityState state, string field, PropertyEntry property, PropertyInfo? info)
    {
        if (property.Metadata.IsConcurrencyToken || property.Metadata.IsShadowProperty())
        {
            return;
        }

        var sensitive = info?.GetCustomAttribute<SensitiveDataAttribute>() is not null;
        object? Value(object? value) => sensitive && value is not null ? AuditJson.Masked : value;

        switch (state)
        {
            case EntityState.Added:
                changes[field] = new FieldChange(null, Value(property.CurrentValue));
                break;
            case EntityState.Deleted:
                changes[field] = new FieldChange(Value(property.OriginalValue), null);
                break;
            case EntityState.Modified when property.IsModified && !Equals(property.OriginalValue, property.CurrentValue):
                changes[field] = new FieldChange(Value(property.OriginalValue), Value(property.CurrentValue));
                break;
            default:
                break;
        }
    }

    private static string FieldName(string clrName) => char.ToLowerInvariant(clrName[0]) + clrName[1..];

    private static string ToSnakeCase(string name)
    {
        var builder = new StringBuilder(name.Length + 8);
        for (var i = 0; i < name.Length; i++)
        {
            if (char.IsUpper(name[i]) && i > 0)
            {
                builder.Append('_');
            }

            builder.Append(char.ToLowerInvariant(name[i]));
        }

        return builder.ToString();
    }

    /// <summary>Old and new value of one field.</summary>
    internal sealed record FieldChange(object? Old, object? New);
}
