using Microsoft.EntityFrameworkCore;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;
using Zelvo.Data.Auditing;
using Zelvo.Data.Localization;
using Zelvo.Data.Tenancy;

namespace Zelvo.Data;

/// <summary>
/// The Zelvo database. The application connects with a role that has no <c>BYPASSRLS</c>, so
/// PostgreSQL row-level security limits every query to the tenant set by
/// <see cref="TenantConnectionInterceptor"/>. Schema changes are made only through migrations.
/// </summary>
public sealed class ZelvoDbContext(DbContextOptions<ZelvoDbContext> options) : DbContext(options)
{
    /// <summary>Tenants. The application role sees only the current tenant.</summary>
    public DbSet<Tenant> Tenants => Set<Tenant>();

    /// <summary>Audit log entries. Append-only.</summary>
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Tenant>(tenant =>
        {
            tenant.ToTable("tenants", table =>
            {
                table.HasCheckConstraint("ck_tenants_status", EnumCheck("status", Enum.GetNames<TenantStatus>()));
                table.HasCheckConstraint("ck_tenants_slug", "slug ~ '^[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?$'");
            });
            tenant.HasKey(t => t.Id);
            tenant.Property(t => t.Id).ValueGeneratedNever();
            tenant.Property(t => t.Slug).HasMaxLength(Tenant.SlugMaxLength);
            tenant.HasIndex(t => t.Slug).IsUnique();
            tenant.ConfigureLocalizedText(t => t.Name, "name", Tenant.NameMaxLength);
            tenant.Property(t => t.Status).HasConversion<string>().HasMaxLength(20);
            tenant.Property(t => t.Version).IsRowVersion();
        });

        modelBuilder.Entity<AuditEvent>(audit =>
        {
            audit.ToTable("audit_events", table =>
            {
                table.HasCheckConstraint("ck_audit_events_category", EnumCheck("category", Enum.GetNames<AuditCategory>()));
                table.HasCheckConstraint("ck_audit_events_actor_type", EnumCheck("actor_type", Enum.GetNames<AuditActorType>()));
            });
            audit.HasKey(a => a.Id);
            audit.Property(a => a.Id).UseIdentityAlwaysColumn();
            audit.HasOne<Tenant>().WithMany().HasForeignKey(a => a.TenantId).OnDelete(DeleteBehavior.Restrict);

            // Filled in by the zelvo_audit_events_chain trigger and read back after insert.
            audit.Property(a => a.Sequence).ValueGeneratedOnAdd()
                .Metadata.SetValueGenerationStrategy(NpgsqlValueGenerationStrategy.None);
            audit.Property(a => a.OccurredAt).ValueGeneratedOnAdd();
            audit.Property(a => a.PreviousHash).ValueGeneratedOnAdd();
            audit.Property(a => a.Hash).ValueGeneratedOnAdd().IsRequired();

            audit.Property(a => a.Category).HasConversion<string>().HasMaxLength(20);
            audit.Property(a => a.ActorType).HasConversion<string>().HasMaxLength(20);
            audit.Property(a => a.Action).HasMaxLength(AuditEvent.TextMaxLength);
            audit.Property(a => a.ActorId).HasMaxLength(AuditEvent.TextMaxLength);
            audit.Property(a => a.ActorName).HasMaxLength(AuditEvent.TextMaxLength);
            audit.Property(a => a.EntityType).HasMaxLength(AuditEvent.TextMaxLength);
            audit.Property(a => a.EntityId).HasMaxLength(AuditEvent.TextMaxLength);
            audit.Property(a => a.Changes).HasColumnType("jsonb");
            audit.Property(a => a.Metadata).HasColumnType("jsonb");

            audit.HasIndex(a => new { a.TenantId, a.Sequence }).IsUnique();
            audit.HasIndex(a => new { a.TenantId, a.OccurredAt });
            audit.HasIndex(a => new { a.TenantId, a.EntityType, a.EntityId });
        });
    }

    private static string EnumCheck(string column, IEnumerable<string> values) =>
        $"{column} IN ({string.Join(", ", values.Select(v => $"'{v}'"))})";
}
