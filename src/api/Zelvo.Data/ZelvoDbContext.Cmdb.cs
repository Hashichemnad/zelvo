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

    /// <summary>Discovery sources (task G2).</summary>
    public DbSet<CiDiscoverySource> CiDiscoverySources => Set<CiDiscoverySource>();

    /// <summary>Records the sources report, tied to items.</summary>
    public DbSet<CiSourceRecord> CiSourceRecords => Set<CiSourceRecord>();

    /// <summary>Software found installed on items.</summary>
    public DbSet<CiInstalledSoftware> CiInstalledSoftwareItems => Set<CiInstalledSoftware>();

    /// <summary>The reconciliation review queue.</summary>
    public DbSet<CiReviewItem> CiReviewItems => Set<CiReviewItem>();

    /// <summary>Assets (task G3).</summary>
    public DbSet<Asset> Assets => Set<Asset>();

    /// <summary>Last generated asset tag number per tenant.</summary>
    public DbSet<AssetTagSequence> AssetTagSequences => Set<AssetTagSequence>();

    /// <summary>Contracts and warranties.</summary>
    public DbSet<AssetContract> AssetContracts => Set<AssetContract>();

    /// <summary>Assets of contracts.</summary>
    public DbSet<AssetContractItem> AssetContractItems => Set<AssetContractItem>();

    /// <summary>Software licences.</summary>
    public DbSet<SoftwareLicense> SoftwareLicenses => Set<SoftwareLicense>();

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
            ciClass.Property(c => c.IdentificationRules).HasColumnType("jsonb");
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
            item.Property(i => i.AttributeSources).HasColumnType("jsonb");
            item.HasOne<ConfigurationItem>().WithMany().HasForeignKey(i => i.MergedIntoId).OnDelete(DeleteBehavior.Restrict);
            item.Property(i => i.Version).IsRowVersion();
            item.HasIndex(i => new { i.TenantId, i.Number }).IsUnique();
            item.HasIndex(i => new { i.TenantId, i.Sequence }).IsUnique();
            item.HasIndex(i => new { i.TenantId, i.ClassId, i.Status });
            item.HasIndex(i => new { i.TenantId, i.Name });
            item.HasIndex(i => new { i.TenantId, i.OwnerGroupId });
            item.HasIndex(i => new { i.TenantId, i.MissingSince });
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

        ConfigureCmdbDiscovery(modelBuilder);
        ConfigureAssets(modelBuilder);
    }

    private static void ConfigureCmdbDiscovery(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<CiDiscoverySource>(source =>
        {
            source.ToTable("ci_discovery_sources", table =>
            {
                table.HasCheckConstraint("ck_ci_discovery_sources_kind", EnumCheck("kind", Enum.GetNames<DiscoverySourceKind>()));
                table.HasCheckConstraint("ck_ci_discovery_sources_priority", $"priority BETWEEN 1 AND {CiDiscoverySource.LowestPriority}");
                table.HasCheckConstraint("ck_ci_discovery_sources_sync_minutes", "sync_minutes BETWEEN 15 AND 10080");
                table.HasCheckConstraint("ck_ci_discovery_sources_settings", "jsonb_typeof(settings) = 'object'");
                table.HasCheckConstraint("ck_ci_discovery_sources_priorities", "jsonb_typeof(attribute_priorities) = 'object'");
            });
            source.HasKey(s => s.Id);
            source.Property(s => s.Id).ValueGeneratedNever();
            source.HasOne<Tenant>().WithMany().HasForeignKey(s => s.TenantId).OnDelete(DeleteBehavior.Restrict);
            source.HasOne<Zelvo.Data.Connectors.Connector>().WithMany().HasForeignKey(s => s.ConnectorId).OnDelete(DeleteBehavior.Restrict);
            source.Property(s => s.Name).HasMaxLength(CiDiscoverySource.NameMaxLength);
            source.Property(s => s.Kind).HasConversion<string>().HasMaxLength(20);
            source.Property(s => s.Settings).HasColumnType("jsonb");
            source.Property(s => s.AttributePriorities).HasColumnType("jsonb");
            source.Property(s => s.LastSyncStats).HasColumnType("jsonb");
            source.Property(s => s.LastError).HasMaxLength(CiDiscoverySource.ErrorMaxLength);
            source.Property(s => s.Version).IsRowVersion();
            source.HasIndex(s => new { s.TenantId, s.Name }).IsUnique();
            source.HasIndex(s => new { s.TenantId, s.NextSyncAt });
        });

        modelBuilder.Entity<CiSourceRecord>(record =>
        {
            record.ToTable("ci_source_records", table =>
            {
                table.HasCheckConstraint("ck_ci_source_records_state", EnumCheck("state", Enum.GetNames<SourceRecordState>()));
                table.HasCheckConstraint("ck_ci_source_records_linked", "(state = 'Linked') = (configuration_item_id IS NOT NULL)");
                table.HasCheckConstraint("ck_ci_source_records_attributes", "jsonb_typeof(attributes) = 'object'");
            });
            record.HasKey(r => r.Id);
            record.Property(r => r.Id).ValueGeneratedNever();
            record.HasOne<Tenant>().WithMany().HasForeignKey(r => r.TenantId).OnDelete(DeleteBehavior.Restrict);
            record.HasOne<CiDiscoverySource>().WithMany().HasForeignKey(r => r.SourceId).OnDelete(DeleteBehavior.Cascade);
            record.HasOne<ConfigurationItem>().WithMany().HasForeignKey(r => r.ConfigurationItemId).OnDelete(DeleteBehavior.Cascade);
            record.HasOne<CiClass>().WithMany().HasForeignKey(r => r.ClassId).OnDelete(DeleteBehavior.Restrict);
            record.Property(r => r.ExternalId).HasMaxLength(CiSourceRecord.ExternalIdMaxLength);
            record.Property(r => r.Name).HasMaxLength(ConfigurationItem.NameMaxLength);
            record.Property(r => r.Attributes).HasColumnType("jsonb");
            record.Property(r => r.PrimaryUser).HasMaxLength(CiSourceRecord.PrimaryUserMaxLength);
            record.Property(r => r.State).HasConversion<string>().HasMaxLength(20);
            record.HasIndex(r => new { r.SourceId, r.ExternalId }).IsUnique();
            record.HasIndex(r => new { r.TenantId, r.ConfigurationItemId });
        });

        modelBuilder.Entity<CiInstalledSoftware>(software =>
        {
            software.ToTable("ci_installed_software");
            software.HasKey(s => s.Id);
            software.Property(s => s.Id).UseIdentityAlwaysColumn();
            software.HasOne<Tenant>().WithMany().HasForeignKey(s => s.TenantId).OnDelete(DeleteBehavior.Restrict);
            software.HasOne<ConfigurationItem>().WithMany().HasForeignKey(s => s.ConfigurationItemId).OnDelete(DeleteBehavior.Cascade);
            software.HasOne<CiDiscoverySource>().WithMany().HasForeignKey(s => s.SourceId).OnDelete(DeleteBehavior.Cascade);
            software.Property(s => s.ProductKey).HasMaxLength(CiInstalledSoftware.NameMaxLength);
            software.Property(s => s.Name).HasMaxLength(CiInstalledSoftware.NameMaxLength);
            software.Property(s => s.Publisher).HasMaxLength(CiInstalledSoftware.NameMaxLength);
            software.Property(s => s.Version).HasMaxLength(CiInstalledSoftware.VersionMaxLength);
            software.HasIndex(s => new { s.ConfigurationItemId, s.SourceId, s.ProductKey, s.Version }).IsUnique();
            software.HasIndex(s => new { s.TenantId, s.ProductKey });
        });

        modelBuilder.Entity<CiReviewItem>(review =>
        {
            review.ToTable("ci_review_items", table =>
            {
                table.HasCheckConstraint("ck_ci_review_items_kind", EnumCheck("kind", Enum.GetNames<CiReviewKind>()));
                table.HasCheckConstraint("ck_ci_review_items_state", EnumCheck("state", Enum.GetNames<CiReviewState>()));
                table.HasCheckConstraint("ck_ci_review_items_ends",
                    "(kind = 'PossibleDuplicate' AND configuration_item_id IS NOT NULL AND other_item_id IS NOT NULL) OR (kind = 'Ambiguous' AND source_record_id IS NOT NULL)");
                table.HasCheckConstraint("ck_ci_review_items_candidates", "jsonb_typeof(candidates) = 'array'");
            });
            review.HasKey(r => r.Id);
            review.Property(r => r.Id).ValueGeneratedNever();
            review.HasOne<Tenant>().WithMany().HasForeignKey(r => r.TenantId).OnDelete(DeleteBehavior.Restrict);
            review.HasOne<ConfigurationItem>().WithMany().HasForeignKey(r => r.ConfigurationItemId).OnDelete(DeleteBehavior.Cascade);
            review.HasOne<ConfigurationItem>().WithMany().HasForeignKey(r => r.OtherItemId).OnDelete(DeleteBehavior.Cascade);
            review.HasOne<CiSourceRecord>().WithMany().HasForeignKey(r => r.SourceRecordId).OnDelete(DeleteBehavior.Cascade);
            review.HasOne<UserAccount>().WithMany().HasForeignKey(r => r.DecidedById).OnDelete(DeleteBehavior.SetNull);
            review.Property(r => r.Kind).HasConversion<string>().HasMaxLength(20);
            review.Property(r => r.State).HasConversion<string>().HasMaxLength(20);
            review.Property(r => r.Candidates).HasColumnType("jsonb");
            review.Property(r => r.MatchedOn).HasMaxLength(CiReviewItem.MatchedOnMaxLength);
            review.Property(r => r.MatchedValue).HasMaxLength(CiReviewItem.MatchedValueMaxLength);
            review.HasIndex(r => new { r.TenantId, r.State, r.CreatedAt });
            // A pair is raised once, whatever was decided: the smaller id first.
            review.HasIndex(r => new { r.ConfigurationItemId, r.OtherItemId }).IsUnique().HasFilter("kind = 'PossibleDuplicate'");
            review.HasIndex(r => r.SourceRecordId).IsUnique().HasFilter("kind = 'Ambiguous' AND state = 'Open'");
        });
    }

    private static void ConfigureAssets(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Asset>(asset =>
        {
            asset.ToTable("assets", table =>
            {
                table.HasCheckConstraint("ck_assets_state", EnumCheck("state", Enum.GetNames<AssetState>()));
                table.HasCheckConstraint("ck_assets_tag", "tag ~ '^[A-Z0-9]([A-Z0-9-]{0,30}[A-Z0-9])?$'");
                table.HasCheckConstraint("ck_assets_assigned", "(state = 'Assigned') = (assigned_to_id IS NOT NULL)");
                table.HasCheckConstraint("ck_assets_cost", "purchase_cost IS NULL OR purchase_cost >= 0");
            });
            asset.HasKey(a => a.ConfigurationItemId);
            asset.HasOne<Tenant>().WithMany().HasForeignKey(a => a.TenantId).OnDelete(DeleteBehavior.Restrict);
            asset.HasOne<ConfigurationItem>().WithOne().HasForeignKey<Asset>(a => a.ConfigurationItemId).OnDelete(DeleteBehavior.Cascade);
            asset.HasOne<UserAccount>().WithMany().HasForeignKey(a => a.AssignedToId).OnDelete(DeleteBehavior.Restrict);
            asset.HasOne<Ticket>().WithMany().HasForeignKey(a => a.RequestTicketId).OnDelete(DeleteBehavior.SetNull);
            asset.Property(a => a.Tag).HasMaxLength(Asset.TagMaxLength);
            asset.Property(a => a.State).HasConversion<string>().HasMaxLength(20);
            asset.Property(a => a.PurchaseCost).HasPrecision(12, 2);
            asset.Property(a => a.Supplier).HasMaxLength(Asset.SupplierMaxLength);
            asset.Property(a => a.OrderNumber).HasMaxLength(Asset.SupplierMaxLength);
            asset.Property(a => a.Notes).HasMaxLength(Asset.NotesMaxLength);
            asset.Property(a => a.Version).IsRowVersion();
            asset.HasIndex(a => new { a.TenantId, a.Tag }).IsUnique();
            asset.HasIndex(a => new { a.TenantId, a.State });
            asset.HasIndex(a => new { a.TenantId, a.AssignedToId });
            asset.HasIndex(a => a.RequestTicketId);
        });

        modelBuilder.Entity<AssetTagSequence>(sequence =>
        {
            sequence.ToTable("asset_tag_sequences");
            sequence.HasKey(s => s.TenantId);
            sequence.HasOne<Tenant>().WithMany().HasForeignKey(s => s.TenantId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<AssetContract>(contract =>
        {
            contract.ToTable("asset_contracts", table =>
            {
                table.HasCheckConstraint("ck_asset_contracts_kind", EnumCheck("kind", Enum.GetNames<AssetContractKind>()));
                table.HasCheckConstraint("ck_asset_contracts_dates", "starts_on IS NULL OR starts_on <= ends_on");
                table.HasCheckConstraint("ck_asset_contracts_cost", "cost_aed IS NULL OR cost_aed >= 0");
            });
            contract.HasKey(c => c.Id);
            contract.Property(c => c.Id).ValueGeneratedNever();
            contract.HasOne<Tenant>().WithMany().HasForeignKey(c => c.TenantId).OnDelete(DeleteBehavior.Restrict);
            contract.HasOne<UserAccount>().WithMany().HasForeignKey(c => c.OwnerId).OnDelete(DeleteBehavior.SetNull);
            contract.Property(c => c.Kind).HasConversion<string>().HasMaxLength(20);
            contract.Property(c => c.Name).HasMaxLength(AssetContract.NameMaxLength);
            contract.Property(c => c.Reference).HasMaxLength(AssetContract.ReferenceMaxLength);
            contract.Property(c => c.Supplier).HasMaxLength(AssetContract.NameMaxLength);
            contract.Property(c => c.Notes).HasMaxLength(Asset.NotesMaxLength);
            contract.Property(c => c.CostAed).HasPrecision(12, 2);
            contract.Property(c => c.Version).IsRowVersion();
            contract.HasIndex(c => new { c.TenantId, c.EndsOn });
        });

        modelBuilder.Entity<AssetContractItem>(item =>
        {
            item.ToTable("asset_contract_items");
            item.HasKey(i => new { i.ContractId, i.ConfigurationItemId });
            item.HasOne<Tenant>().WithMany().HasForeignKey(i => i.TenantId).OnDelete(DeleteBehavior.Restrict);
            item.HasOne<AssetContract>().WithMany().HasForeignKey(i => i.ContractId).OnDelete(DeleteBehavior.Cascade);
            item.HasOne<Asset>().WithMany().HasForeignKey(i => i.ConfigurationItemId).OnDelete(DeleteBehavior.Cascade);
            item.HasIndex(i => new { i.TenantId, i.ConfigurationItemId });
        });

        modelBuilder.Entity<SoftwareLicense>(license =>
        {
            license.ToTable("software_licenses", table =>
            {
                table.HasCheckConstraint("ck_software_licenses_metric", EnumCheck("metric", Enum.GetNames<LicenseMetric>()));
                table.HasCheckConstraint("ck_software_licenses_quantity", "quantity BETWEEN 0 AND 10000000");
                table.HasCheckConstraint("ck_software_licenses_products", "jsonb_typeof(product_keys) = 'array'");
                table.HasCheckConstraint("ck_software_licenses_dates", "starts_on IS NULL OR ends_on IS NULL OR starts_on <= ends_on");
                table.HasCheckConstraint("ck_software_licenses_cost", "cost_aed IS NULL OR cost_aed >= 0");
            });
            license.HasKey(l => l.Id);
            license.Property(l => l.Id).ValueGeneratedNever();
            license.HasOne<Tenant>().WithMany().HasForeignKey(l => l.TenantId).OnDelete(DeleteBehavior.Restrict);
            license.HasOne<UserAccount>().WithMany().HasForeignKey(l => l.OwnerId).OnDelete(DeleteBehavior.SetNull);
            license.Property(l => l.Name).HasMaxLength(SoftwareLicense.NameMaxLength);
            license.Property(l => l.Publisher).HasMaxLength(SoftwareLicense.NameMaxLength);
            license.Property(l => l.Supplier).HasMaxLength(SoftwareLicense.NameMaxLength);
            license.Property(l => l.Reference).HasMaxLength(AssetContract.ReferenceMaxLength);
            license.Property(l => l.Notes).HasMaxLength(Asset.NotesMaxLength);
            license.Property(l => l.ProductKeys).HasColumnType("jsonb");
            license.Property(l => l.Metric).HasConversion<string>().HasMaxLength(20);
            license.Property(l => l.CostAed).HasPrecision(12, 2);
            license.Property(l => l.Version).IsRowVersion();
            license.HasIndex(l => new { l.TenantId, l.Name });
            license.HasIndex(l => new { l.TenantId, l.EndsOn });
        });
    }
}
