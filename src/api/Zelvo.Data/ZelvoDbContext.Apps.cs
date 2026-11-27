using Microsoft.EntityFrameworkCore;
using Zelvo.Data.Apps;
using Zelvo.Data.Identity;
using Zelvo.Data.Localization;
using Zelvo.Data.Records;
using Zelvo.Data.Tenancy;

namespace Zelvo.Data;

/// <summary>The low-code app builder: apps, data lists and imported app packages (module 20, task K3).</summary>
public sealed partial class ZelvoDbContext
{
    /// <summary>Apps.</summary>
    public DbSet<App> Apps => Set<App>();

    /// <summary>Data lists.</summary>
    public DbSet<DataList> DataLists => Set<DataList>();

    /// <summary>Items of data lists.</summary>
    public DbSet<DataListItem> DataListItems => Set<DataListItem>();

    /// <summary>Imported app packages.</summary>
    public DbSet<AppImport> AppImports => Set<AppImport>();

    private static void ConfigureApps(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<App>(app =>
        {
            app.ToTable("apps", table =>
            {
                table.HasCheckConstraint("ck_apps_key", "key ~ '^[a-z]([a-z0-9-]{0,38}[a-z0-9])?$'");
                table.HasCheckConstraint("ck_apps_definition", "jsonb_typeof(definition) = 'object'");
                table.HasCheckConstraint("ck_apps_suspended", "jsonb_typeof(suspended) = 'object'");
            });
            app.HasKey(a => a.Id);
            app.Property(a => a.Id).ValueGeneratedNever();
            app.HasOne<Tenant>().WithMany().HasForeignKey(a => a.TenantId).OnDelete(DeleteBehavior.Restrict);
            app.Property(a => a.Key).HasMaxLength(App.KeyMaxLength);
            app.ConfigureLocalizedText(a => a.Name, "name", App.NameMaxLength);
            app.ConfigureLocalizedText(a => a.Description, "description", App.DescriptionMaxLength);
            app.Property(a => a.Icon).HasMaxLength(40);
            app.Property(a => a.Definition).HasColumnType("jsonb");
            app.Property(a => a.Suspended).HasColumnType("jsonb");
            app.Property(a => a.Version).IsRowVersion();
            app.HasIndex(a => new { a.TenantId, a.Key }).IsUnique();
        });

        modelBuilder.Entity<DataList>(list =>
        {
            list.ToTable("data_lists", table =>
            {
                table.HasCheckConstraint("ck_data_lists_key", "key ~ '^[a-z][a-zA-Z0-9-]{0,39}$'");
                table.HasCheckConstraint("ck_data_lists_columns", "jsonb_typeof(columns) = 'array'");
            });
            list.HasKey(l => l.Id);
            list.Property(l => l.Id).ValueGeneratedNever();
            list.HasOne<Tenant>().WithMany().HasForeignKey(l => l.TenantId).OnDelete(DeleteBehavior.Restrict);
            list.HasOne<App>().WithMany().HasForeignKey(l => l.AppId).OnDelete(DeleteBehavior.SetNull);
            list.Property(l => l.Key).HasMaxLength(DataList.KeyMaxLength);
            list.ConfigureLocalizedText(l => l.Name, "name", DataList.NameMaxLength);
            list.ConfigureLocalizedText(l => l.Description, "description", DataList.DescriptionMaxLength);
            list.Property(l => l.Columns).HasColumnType("jsonb");
            list.Property(l => l.Version).IsRowVersion();
            list.HasIndex(l => new { l.TenantId, l.Key }).IsUnique();
        });

        modelBuilder.Entity<DataListItem>(item =>
        {
            item.ToTable("data_list_items", table => table.HasCheckConstraint("ck_data_list_items_values", "jsonb_typeof(values) = 'object'"));
            item.HasKey(i => i.Id);
            item.Property(i => i.Id).ValueGeneratedNever();
            item.HasOne<Tenant>().WithMany().HasForeignKey(i => i.TenantId).OnDelete(DeleteBehavior.Restrict);
            item.HasOne<DataList>().WithMany().HasForeignKey(i => i.DataListId).OnDelete(DeleteBehavior.Cascade);
            item.Property(i => i.Key).HasMaxLength(DataListItem.KeyMaxLength);
            item.ConfigureLocalizedText(i => i.Label, "label", DataListItem.LabelMaxLength);
            item.Property(i => i.Values).HasColumnType("jsonb");
            item.HasIndex(i => new { i.DataListId, i.Key }).IsUnique();
            item.HasIndex(i => new { i.TenantId, i.DataListId, i.Position });
        });

        modelBuilder.Entity<AppImport>(import =>
        {
            import.ToTable("app_imports", table =>
            {
                table.HasCheckConstraint("ck_app_imports_package", "jsonb_typeof(package) = 'object'");
                table.HasCheckConstraint("ck_app_imports_sha256", "sha256 ~ '^[0-9a-f]{64}$'");
            });
            import.HasKey(i => i.Id);
            import.Property(i => i.Id).ValueGeneratedNever();
            import.HasOne<Tenant>().WithMany().HasForeignKey(i => i.TenantId).OnDelete(DeleteBehavior.Restrict);
            import.HasOne<App>().WithMany().HasForeignKey(i => i.AppId).OnDelete(DeleteBehavior.SetNull);
            import.Property(i => i.FileName).HasMaxLength(AppImport.FileNameMaxLength);
            import.Property(i => i.Sha256).HasMaxLength(64);
            import.Property(i => i.Package).HasColumnType("jsonb");
            import.Property(i => i.AppKey).HasMaxLength(App.KeyMaxLength);
            import.ConfigureLocalizedText(i => i.AppName, "app_name", App.NameMaxLength);
            import.Property(i => i.Status).HasConversion<string>().HasMaxLength(20);
            import.Property(i => i.Choices).HasColumnType("jsonb");
            import.Property(i => i.Result).HasColumnType("jsonb");
            import.Property(i => i.Version).IsRowVersion();
            import.HasIndex(i => new { i.TenantId, i.Status, i.ImportedAt });
        });

        // Task K3: record types and roles an app groups.
        modelBuilder.Entity<RecordType>().HasOne<App>().WithMany().HasForeignKey(t => t.AppId).OnDelete(DeleteBehavior.SetNull);
        modelBuilder.Entity<Role>().HasOne<App>().WithMany().HasForeignKey(r => r.AppId).OnDelete(DeleteBehavior.SetNull);
    }
}
