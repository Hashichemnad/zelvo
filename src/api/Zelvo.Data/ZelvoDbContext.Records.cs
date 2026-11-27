using Microsoft.EntityFrameworkCore;
using Zelvo.Data.Localization;
using Zelvo.Data.Records;
using Zelvo.Data.Tenancy;
using Zelvo.Data.Tickets;

namespace Zelvo.Data;

/// <summary>Custom record types (module 20, task K2).</summary>
public sealed partial class ZelvoDbContext
{
    /// <summary>Record types made by administrators.</summary>
    public DbSet<RecordType> RecordTypes => Set<RecordType>();

    /// <summary>Details of custom records.</summary>
    public DbSet<CustomRecord> CustomRecords => Set<CustomRecord>();

    private static void ConfigureRecords(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<RecordType>(type =>
        {
            type.ToTable("record_types", table =>
            {
                table.HasCheckConstraint("ck_record_types_key", "key ~ '^[a-z]([a-z0-9-]{0,38}[a-z0-9])?$'");
                table.HasCheckConstraint("ck_record_types_prefix", "number_prefix ~ '^[A-Z][A-Z0-9]{0,9}$'");
                table.HasCheckConstraint("ck_record_types_states", "jsonb_typeof(states) = 'array'");
                table.HasCheckConstraint("ck_record_types_priority", "default_priority BETWEEN 1 AND 5");
                table.HasCheckConstraint("ck_record_types_last_number", "last_number >= 0");
            });
            type.HasKey(t => t.Id);
            type.Property(t => t.Id).ValueGeneratedNever();
            type.HasOne<Tenant>().WithMany().HasForeignKey(t => t.TenantId).OnDelete(DeleteBehavior.Restrict);
            type.Property(t => t.Key).HasMaxLength(RecordType.KeyMaxLength);
            type.ConfigureLocalizedText(t => t.Name, "name", RecordType.NameMaxLength);
            type.ConfigureLocalizedText(t => t.Description, "description", RecordType.DescriptionMaxLength);
            type.Property(t => t.NumberPrefix).HasMaxLength(10);
            type.Property(t => t.States).HasColumnType("jsonb");
            type.Property(t => t.Version).IsRowVersion();
            type.HasIndex(t => new { t.TenantId, t.Key }).IsUnique();
            type.HasIndex(t => new { t.TenantId, t.NumberPrefix }).IsUnique();
        });

        modelBuilder.Entity<CustomRecord>(record =>
        {
            record.ToTable("custom_records", table => table.HasCheckConstraint("ck_custom_records_priority", "priority BETWEEN 1 AND 5"));
            record.HasKey(r => r.TicketId);
            record.HasOne<Tenant>().WithMany().HasForeignKey(r => r.TenantId).OnDelete(DeleteBehavior.Restrict);
            record.HasOne<RecordType>().WithMany().HasForeignKey(r => r.RecordTypeId).OnDelete(DeleteBehavior.Restrict);
            record.Property(r => r.StateKey).HasMaxLength(CustomRecord.StateKeyMaxLength);
            record.HasIndex(r => new { r.TenantId, r.RecordTypeId, r.StateKey });
        });
    }
}
