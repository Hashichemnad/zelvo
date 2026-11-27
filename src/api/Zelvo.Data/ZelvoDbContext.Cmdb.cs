using Microsoft.EntityFrameworkCore;
using Zelvo.Data.Cmdb;
using Zelvo.Data.Directory;
using Zelvo.Data.Identity;
using Zelvo.Data.Localization;
using Zelvo.Data.Sla;
using Zelvo.Data.Tenancy;
using Zelvo.Data.Tickets;

namespace Zelvo.Data;

/// <summary>Configuration management database (module 18, task G1).</summary>
public sealed partial class ZelvoDbContext
{
    /// <summary>Configuration item classes.</summary>
    public DbSet<CiClass> CiClasses => Set<CiClass>();

    /// <summary>Configuration items.</summary>
    public DbSet<ConfigurationItem> ConfigurationItems => Set<ConfigurationItem>();

    /// <summary>Last configuration item number per tenant.</summary>
    public DbSet<CiNumberSequence> CiNumberSequences => Set<CiNumberSequence>();

    /// <summary>Relationship types.</summary>
    public DbSet<CiRelationshipType> CiRelationshipTypes => Set<CiRelationshipType>();

    /// <summary>Relationships between items, and from items to the people who use them.</summary>
    public DbSet<CiRelationship> CiRelationships => Set<CiRelationship>();

    /// <summary>Configuration items of tickets.</summary>
    public DbSet<TicketConfigurationItem> TicketConfigurationItems => Set<TicketConfigurationItem>();

    private static void ConfigureCmdb(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<CiClass>(ciClass =>
        {
            ciClass.ToTable("ci_classes", table =>
            {
                table.HasCheckConstraint("ck_ci_classes_family", EnumCheck("family", Enum.GetNames<CiFamily>()));
                table.HasCheckConstraint("ck_ci_classes_depth", $"depth BETWEEN 1 AND {CiClass.MaxDepth}");
                table.HasCheckConstraint("ck_ci_classes_parent", "(depth = 1) = (parent_id IS NULL)");
                table.HasCheckConstraint("ck_ci_classes_not_own_parent", "parent_id IS NULL OR parent_id <> id");
                table.HasCheckConstraint("ck_ci_classes_key", "key ~ '^[a-z0-9]([a-z0-9-]{0,48}[a-z0-9])?$'");
                table.HasCheckConstraint("ck_ci_classes_attributes", "jsonb_typeof(attributes) = 'array'");
            });
            ciClass.HasKey(c => c.Id);
            ciClass.Property(c => c.Id).ValueGeneratedNever();
            ciClass.HasOne<Tenant>().WithMany().HasForeignKey(c => c.TenantId).OnDelete(DeleteBehavior.Restrict);
            ciClass.HasOne<CiClass>().WithMany().HasForeignKey(c => c.ParentId).OnDelete(DeleteBehavior.Restrict);
            ciClass.Property(c => c.Key).HasMaxLength(CiClass.KeyMaxLength);
            ciClass.Property(c => c.Family).HasConversion<string>().HasMaxLength(20);
            ciClass.ConfigureLocalizedText(c => c.Name, "name", CiClass.NameMaxLength);
            ciClass.ConfigureLocalizedText(c => c.Description, "description", CiClass.DescriptionMaxLength);
            ciClass.Property(c => c.Attributes).HasColumnType("jsonb");
            ciClass.Property(c => c.Version).IsRowVersion();
            ciClass.HasIndex(c => new { c.TenantId, c.Key }).IsUnique();
            ciClass.HasIndex(c => new { c.TenantId, c.ParentId });
        });

        modelBuilder.Entity<ConfigurationItem>(item =>
        {
            item.ToTable("configuration_items", table =>
            {
                table.HasCheckConstraint("ck_configuration_items_status", EnumCheck("status", Enum.GetNames<CiStatus>()));
                table.HasCheckConstraint("ck_configuration_items_sequence", "sequence >= 1");
                table.HasCheckConstraint("ck_configuration_items_attributes", "jsonb_typeof(attributes) = 'object'");
                table.HasCheckConstraint("ck_configuration_items_name", "length(btrim(name)) > 0");
            });
            item.HasKey(i => i.Id);
            item.Property(i => i.Id).ValueGeneratedNever();
            item.HasOne<Tenant>().WithMany().HasForeignKey(i => i.TenantId).OnDelete(DeleteBehavior.Restrict);
            item.HasOne<CiClass>().WithMany().HasForeignKey(i => i.ClassId).OnDelete(DeleteBehavior.Restrict);
            item.HasOne<SupportGroup>().WithMany().HasForeignKey(i => i.OwnerGroupId).OnDelete(DeleteBehavior.Restrict);
            item.HasOne<Location>().WithMany().HasForeignKey(i => i.LocationId).OnDelete(DeleteBehavior.Restrict);
            item.Property(i => i.Number).HasMaxLength(ConfigurationItem.NumberMaxLength);
            item.Property(i => i.Name).HasMaxLength(ConfigurationItem.NameMaxLength);
            item.ConfigureLocalizedText(i => i.Description, "description", ConfigurationItem.DescriptionMaxLength);
            item.Property(i => i.Status).HasConversion<string>().HasMaxLength(20);
            item.Property(i => i.Attributes).HasColumnType("jsonb");
            item.Property(i => i.Version).IsRowVersion();
            item.HasIndex(i => new { i.TenantId, i.Number }).IsUnique();
            item.HasIndex(i => new { i.TenantId, i.Sequence }).IsUnique();
            item.HasIndex(i => new { i.TenantId, i.ClassId, i.Status });
            item.HasIndex(i => new { i.TenantId, i.Name });
            item.HasIndex(i => new { i.TenantId, i.OwnerGroupId });
        });

        modelBuilder.Entity<CiNumberSequence>(sequence =>
        {
            sequence.ToTable("ci_number_sequences");
            sequence.HasKey(s => s.TenantId);
            sequence.HasOne<Tenant>().WithMany().HasForeignKey(s => s.TenantId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<CiRelationshipType>(type =>
        {
            type.ToTable("ci_relationship_types", table =>
            {
                table.HasCheckConstraint("ck_ci_relationship_types_target_kind", EnumCheck("target_kind", Enum.GetNames<RelationshipTargetKind>()));
                table.HasCheckConstraint("ck_ci_relationship_types_impact", EnumCheck("impact", Enum.GetNames<ImpactDirection>()));
                table.HasCheckConstraint("ck_ci_relationship_types_key", "key ~ '^[a-z0-9]([a-z0-9-]{0,48}[a-z0-9])?$'");
            });
            type.HasKey(t => t.Id);
            type.Property(t => t.Id).ValueGeneratedNever();
            type.HasOne<Tenant>().WithMany().HasForeignKey(t => t.TenantId).OnDelete(DeleteBehavior.Restrict);
            type.Property(t => t.Key).HasMaxLength(CiRelationshipType.KeyMaxLength);
            type.ConfigureLocalizedText(t => t.OutgoingName, "outgoing_name", CiRelationshipType.NameMaxLength);
            type.ConfigureLocalizedText(t => t.IncomingName, "incoming_name", CiRelationshipType.NameMaxLength);
            type.Property(t => t.TargetKind).HasConversion<string>().HasMaxLength(20);
            type.Property(t => t.Impact).HasConversion<string>().HasMaxLength(20);
            type.HasIndex(t => new { t.TenantId, t.Key }).IsUnique();
        });

        modelBuilder.Entity<CiRelationship>(relationship =>
        {
            relationship.ToTable("ci_relationships", table =>
            {
                table.HasCheckConstraint("ck_ci_relationships_one_target",
                    "num_nonnulls(target_ci_id, target_user_id, target_department_id, target_customer_group_id) = 1");
                table.HasCheckConstraint("ck_ci_relationships_not_self", "target_ci_id IS NULL OR target_ci_id <> source_id");
            });
            relationship.HasKey(r => r.Id);
            relationship.Property(r => r.Id).ValueGeneratedNever();
            relationship.HasOne<Tenant>().WithMany().HasForeignKey(r => r.TenantId).OnDelete(DeleteBehavior.Restrict);
            relationship.HasOne<CiRelationshipType>().WithMany().HasForeignKey(r => r.TypeId).OnDelete(DeleteBehavior.Restrict);
            relationship.HasOne<ConfigurationItem>().WithMany().HasForeignKey(r => r.SourceId).OnDelete(DeleteBehavior.Cascade);
            relationship.HasOne<ConfigurationItem>().WithMany().HasForeignKey(r => r.TargetCiId).OnDelete(DeleteBehavior.Cascade);
            relationship.HasOne<UserAccount>().WithMany().HasForeignKey(r => r.TargetUserId).OnDelete(DeleteBehavior.Cascade);
            relationship.HasOne<Department>().WithMany().HasForeignKey(r => r.TargetDepartmentId).OnDelete(DeleteBehavior.Cascade);
            relationship.HasOne<CustomerGroup>().WithMany().HasForeignKey(r => r.TargetCustomerGroupId).OnDelete(DeleteBehavior.Cascade);
            relationship.HasOne<UserAccount>().WithMany().HasForeignKey(r => r.CreatedById).OnDelete(DeleteBehavior.SetNull);
            relationship.Property(r => r.Note).HasMaxLength(CiRelationship.NoteMaxLength);
            // One relationship of a type between the same two ends; NULLS NOT DISTINCT makes the
            // empty target columns count as equal.
            relationship.HasIndex(r => new { r.SourceId, r.TypeId, r.TargetCiId, r.TargetUserId, r.TargetDepartmentId, r.TargetCustomerGroupId })
                .IsUnique().AreNullsDistinct(false).HasDatabaseName("ux_ci_relationships_ends");
            relationship.HasIndex(r => new { r.TenantId, r.TargetCiId });
            relationship.HasIndex(r => new { r.TenantId, r.TypeId });
            relationship.HasIndex(r => r.TargetUserId);
            relationship.HasIndex(r => r.TargetDepartmentId);
            relationship.HasIndex(r => r.TargetCustomerGroupId);
        });

        modelBuilder.Entity<TicketConfigurationItem>(link =>
        {
            link.ToTable("ticket_configuration_items");
            link.HasKey(l => new { l.TicketId, l.ConfigurationItemId });
            link.HasOne<Tenant>().WithMany().HasForeignKey(l => l.TenantId).OnDelete(DeleteBehavior.Restrict);
            link.HasOne<Ticket>().WithMany().HasForeignKey(l => l.TicketId).OnDelete(DeleteBehavior.Cascade);
            link.HasOne<ConfigurationItem>().WithMany().HasForeignKey(l => l.ConfigurationItemId).OnDelete(DeleteBehavior.Restrict);
            link.HasOne<UserAccount>().WithMany().HasForeignKey(l => l.AddedById).OnDelete(DeleteBehavior.SetNull);
            link.HasIndex(l => new { l.TenantId, l.ConfigurationItemId });
        });
    }
}
