using Microsoft.EntityFrameworkCore;
using Zelvo.Data.Connectors;
using Zelvo.Data.Directory;
using Zelvo.Data.Identity;
using Zelvo.Data.Tenancy;
using Zelvo.Data.Tickets;

namespace Zelvo.Data;

/// <summary>Connectors and monitoring alerts (module 14, task E7).</summary>
public sealed partial class ZelvoDbContext
{
    /// <summary>Identity, CRM and REST connectors.</summary>
    public DbSet<Connector> Connectors => Set<Connector>();

    /// <summary>Zelvo records and their counterparts in connected systems.</summary>
    public DbSet<ConnectorLink> ConnectorLinks => Set<ConnectorLink>();

    /// <summary>Outgoing connector calls (outbox).</summary>
    public DbSet<ConnectorCall> ConnectorCalls => Set<ConnectorCall>();

    /// <summary>Monitoring tools that send alerts.</summary>
    public DbSet<MonitoringSource> MonitoringSources => Set<MonitoringSource>();

    /// <summary>Alerts and their incidents.</summary>
    public DbSet<MonitoringAlert> MonitoringAlerts => Set<MonitoringAlert>();

    private static void ConfigureConnectors(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Connector>(connector =>
        {
            connector.ToTable("connectors", table =>
            {
                table.HasCheckConstraint("ck_connectors_kind", EnumCheck("kind", Enum.GetNames<ConnectorKind>()));
                table.HasCheckConstraint("ck_connectors_sync_minutes", "sync_minutes BETWEEN 5 AND 10080");
            });
            connector.HasKey(c => c.Id);
            connector.Property(c => c.Id).ValueGeneratedNever();
            connector.HasOne<Tenant>().WithMany().HasForeignKey(c => c.TenantId).OnDelete(DeleteBehavior.Restrict);
            connector.HasOne<UserAccount>().WithMany().HasForeignKey(c => c.SystemUserId).OnDelete(DeleteBehavior.SetNull);
            connector.Property(c => c.Name).HasMaxLength(Connector.NameMaxLength);
            connector.Property(c => c.Kind).HasConversion<string>().HasMaxLength(20);
            connector.Property(c => c.Settings).HasColumnType("jsonb");
            connector.Property(c => c.LastSyncStats).HasColumnType("jsonb");
            connector.Property(c => c.SyncCursor).HasMaxLength(4000);
            connector.Property(c => c.LastError).HasMaxLength(Connector.ErrorMaxLength);
            connector.Property(c => c.Version).IsRowVersion();
            connector.HasIndex(c => new { c.TenantId, c.Kind });
            connector.HasIndex(c => new { c.Active, c.NextSyncAt });
        });

        modelBuilder.Entity<ConnectorLink>(link =>
        {
            link.ToTable("connector_links", table =>
                table.HasCheckConstraint("ck_connector_links_entity", EnumCheck("entity", Enum.GetNames<ConnectorEntity>())));
            link.HasKey(l => l.Id);
            link.Property(l => l.Id).UseIdentityAlwaysColumn();
            link.HasOne<Tenant>().WithMany().HasForeignKey(l => l.TenantId).OnDelete(DeleteBehavior.Restrict);
            link.HasOne<Connector>().WithMany().HasForeignKey(l => l.ConnectorId).OnDelete(DeleteBehavior.Cascade);
            link.Property(l => l.Entity).HasConversion<string>().HasMaxLength(20);
            link.Property(l => l.ExternalId).HasMaxLength(ConnectorLink.ExternalIdMaxLength);
            // One counterpart per record and connector, both ways.
            link.HasIndex(l => new { l.ConnectorId, l.Entity, l.ExternalId }).IsUnique();
            link.HasIndex(l => new { l.ConnectorId, l.Entity, l.ZelvoId }).IsUnique();
            link.HasIndex(l => new { l.TenantId, l.Entity, l.ZelvoId });
        });

        modelBuilder.Entity<ConnectorCall>(call =>
        {
            call.ToTable("connector_calls", table =>
            {
                table.HasCheckConstraint("ck_connector_calls_kind", EnumCheck("kind", Enum.GetNames<ConnectorCallKind>()));
                table.HasCheckConstraint("ck_connector_calls_status", EnumCheck("status", Enum.GetNames<ConnectorCallStatus>()));
            });
            call.HasKey(c => c.Id);
            call.Property(c => c.Id).UseIdentityAlwaysColumn();
            call.HasOne<Tenant>().WithMany().HasForeignKey(c => c.TenantId).OnDelete(DeleteBehavior.Restrict);
            call.HasOne<Connector>().WithMany().HasForeignKey(c => c.ConnectorId).OnDelete(DeleteBehavior.Cascade);
            call.HasOne<Ticket>().WithMany().HasForeignKey(c => c.TicketId).OnDelete(DeleteBehavior.Cascade);
            call.Property(c => c.Kind).HasConversion<string>().HasMaxLength(20);
            call.Property(c => c.Status).HasConversion<string>().HasMaxLength(20);
            call.Property(c => c.Operation).HasMaxLength(ConnectorCall.OperationMaxLength);
            call.Property(c => c.Error).HasMaxLength(ConnectorCall.ErrorMaxLength);
            call.HasIndex(c => new { c.TenantId, c.Status, c.NextAttemptAt });
            call.HasIndex(c => new { c.TenantId, c.ConnectorId, c.CreatedAt });
        });

        modelBuilder.Entity<MonitoringSource>(source =>
        {
            source.ToTable("monitoring_sources", table =>
                table.HasCheckConstraint("ck_monitoring_sources_format", EnumCheck("format", Enum.GetNames<AlertFormat>())));
            source.HasKey(s => s.Id);
            source.Property(s => s.Id).ValueGeneratedNever();
            source.HasOne<Tenant>().WithMany().HasForeignKey(s => s.TenantId).OnDelete(DeleteBehavior.Restrict);
            source.HasOne<Category>().WithMany().HasForeignKey(s => s.CategoryId).OnDelete(DeleteBehavior.Restrict);
            source.HasOne<Location>().WithMany().HasForeignKey(s => s.LocationId).OnDelete(DeleteBehavior.Restrict);
            source.HasOne<SupportGroup>().WithMany().HasForeignKey(s => s.GroupId).OnDelete(DeleteBehavior.Restrict);
            source.HasOne<UserAccount>().WithMany().HasForeignKey(s => s.SystemUserId).OnDelete(DeleteBehavior.Restrict);
            source.Property(s => s.Name).HasMaxLength(MonitoringSource.NameMaxLength);
            source.Property(s => s.Format).HasConversion<string>().HasMaxLength(20);
            source.Property(s => s.Mapping).HasColumnType("jsonb");
            source.Property(s => s.TokenHash).HasMaxLength(64).IsFixedLength();
            source.Property(s => s.Version).IsRowVersion();
            source.HasIndex(s => s.TenantId);
        });

        modelBuilder.Entity<MonitoringAlert>(alert =>
        {
            alert.ToTable("monitoring_alerts", table =>
                table.HasCheckConstraint("ck_monitoring_alerts_state", EnumCheck("state", Enum.GetNames<AlertState>())));
            alert.HasKey(a => a.Id);
            alert.Property(a => a.Id).UseIdentityAlwaysColumn();
            alert.HasOne<Tenant>().WithMany().HasForeignKey(a => a.TenantId).OnDelete(DeleteBehavior.Restrict);
            alert.HasOne<MonitoringSource>().WithMany().HasForeignKey(a => a.SourceId).OnDelete(DeleteBehavior.Cascade);
            alert.HasOne<Ticket>().WithMany().HasForeignKey(a => a.TicketId).OnDelete(DeleteBehavior.Cascade);
            alert.Property(a => a.Key).HasMaxLength(MonitoringAlert.KeyMaxLength);
            alert.Property(a => a.State).HasConversion<string>().HasMaxLength(20);
            // One firing alert per key: firing again adds to its incident.
            alert.HasIndex(a => new { a.SourceId, a.Key }).IsUnique().HasFilter("state = 'Firing'");
            alert.HasIndex(a => new { a.TenantId, a.TicketId });
        });
    }
}
