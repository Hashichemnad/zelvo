using Microsoft.EntityFrameworkCore;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;
using Zelvo.Data.Auditing;
using Zelvo.Data.Identity;
using Zelvo.Data.Localization;
using Zelvo.Data.Settings;
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

    /// <summary>People who sign in to the tenant.</summary>
    public DbSet<UserAccount> Users => Set<UserAccount>();

    /// <summary>Roles given to users.</summary>
    public DbSet<UserRole> UserRoles => Set<UserRole>();

    /// <summary>Roles: the six built-in personas and any added later.</summary>
    public DbSet<Role> Roles => Set<Role>();

    /// <summary>Permissions of roles.</summary>
    public DbSet<RoleGrant> RoleGrants => Set<RoleGrant>();

    /// <summary>Field rules of roles.</summary>
    public DbSet<RoleFieldRule> RoleFieldRules => Set<RoleFieldRule>();

    /// <summary>Default grants of built-in roles already given to the tenant.</summary>
    public DbSet<RoleDefaultGrant> RoleDefaultGrants => Set<RoleDefaultGrant>();

    /// <summary>Sign-in settings (one row per tenant).</summary>
    public DbSet<IdentitySettings> IdentitySettings => Set<IdentitySettings>();

    /// <summary>Tenant settings (one row per changed key).</summary>
    public DbSet<TenantSetting> TenantSettings => Set<TenantSetting>();

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

        ConfigureIdentity(modelBuilder);

        modelBuilder.Entity<TenantSetting>(setting =>
        {
            setting.ToTable("tenant_settings", table =>
                table.HasCheckConstraint("ck_tenant_settings_key", "key ~ '^[a-z][a-zA-Z0-9]*(\\.[a-z][a-zA-Z0-9]*)+$'"));
            setting.HasKey(s => new { s.TenantId, s.Key });
            setting.HasOne<Tenant>().WithMany().HasForeignKey(s => s.TenantId).OnDelete(DeleteBehavior.Restrict);
            setting.Property(s => s.Key).HasMaxLength(TenantSetting.KeyMaxLength);
            setting.Property(s => s.Value).HasColumnType("jsonb");
            setting.Property(s => s.Version).IsRowVersion();
        });
    }

    private static void ConfigureIdentity(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<UserAccount>(user =>
        {
            user.ToTable("users", table =>
            {
                table.HasCheckConstraint("ck_users_status", EnumCheck("status", Enum.GetNames<UserStatus>()));
                table.HasCheckConstraint("ck_users_preferred_language", EnumCheck("preferred_language", LocalizedText.Languages));
                table.HasCheckConstraint("ck_users_calendar", EnumCheck("calendar", Enum.GetNames<CalendarSystem>()));
                table.HasCheckConstraint("ck_users_digits", EnumCheck("digits", Enum.GetNames<DigitSystem>()));
            });
            user.HasKey(u => u.Id);
            user.Property(u => u.Id).ValueGeneratedNever();
            user.HasOne<Tenant>().WithMany().HasForeignKey(u => u.TenantId).OnDelete(DeleteBehavior.Restrict);
            user.Property(u => u.Subject).HasMaxLength(UserAccount.SubjectMaxLength);
            user.Property(u => u.Email).HasMaxLength(UserAccount.EmailMaxLength);
            user.Property(u => u.DisplayName).HasMaxLength(UserAccount.DisplayNameMaxLength);
            user.Property(u => u.Status).HasConversion<string>().HasMaxLength(20);
            user.Property(u => u.PreferredLanguage).HasMaxLength(2);
            user.Property(u => u.Calendar).HasConversion<string>().HasMaxLength(20);
            user.Property(u => u.Digits).HasConversion<string>().HasMaxLength(20);
            user.Property(u => u.Version).IsRowVersion();
            user.HasIndex(u => new { u.TenantId, u.Subject }).IsUnique();
            user.HasIndex(u => new { u.TenantId, u.Email });
            user.HasMany(u => u.Roles).WithOne().HasForeignKey(r => r.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Role>(role =>
        {
            role.ToTable("roles", table =>
                table.HasCheckConstraint("ck_roles_key", "key ~ '^[a-z0-9]([a-z0-9-]{0,48}[a-z0-9])?$'"));
            role.HasKey(r => r.Id);
            role.Property(r => r.Id).ValueGeneratedNever();
            role.HasOne<Tenant>().WithMany().HasForeignKey(r => r.TenantId).OnDelete(DeleteBehavior.Restrict);
            role.Property(r => r.Key).HasMaxLength(Role.KeyMaxLength);
            role.ConfigureLocalizedText(r => r.Name, "name", Role.NameMaxLength);
            role.Property(r => r.Version).IsRowVersion();
            role.HasIndex(r => new { r.TenantId, r.Key }).IsUnique();
            role.HasMany(r => r.Permissions).WithOne().HasForeignKey(p => p.RoleId).OnDelete(DeleteBehavior.Cascade);
            role.HasMany(r => r.FieldRules).WithOne().HasForeignKey(f => f.RoleId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<UserRole>(userRole =>
        {
            userRole.ToTable("user_roles");
            userRole.HasKey(u => new { u.UserId, u.RoleId });
            userRole.HasOne(u => u.Role).WithMany().HasForeignKey(u => u.RoleId).OnDelete(DeleteBehavior.Cascade);
            userRole.HasIndex(u => new { u.TenantId, u.RoleId });
        });

        modelBuilder.Entity<RoleGrant>(permission =>
        {
            permission.ToTable("role_permissions");
            permission.HasKey(p => new { p.RoleId, p.Permission });
            permission.Property(p => p.Permission).HasMaxLength(RoleGrant.PermissionMaxLength);
            permission.HasIndex(p => p.TenantId);
        });

        modelBuilder.Entity<RoleFieldRule>(rule =>
        {
            rule.ToTable("role_field_rules", table =>
                table.HasCheckConstraint("ck_role_field_rules_access", EnumCheck("access", Enum.GetNames<FieldAccess>())));
            rule.HasKey(f => new { f.RoleId, f.Resource, f.Field });
            rule.Property(f => f.Resource).HasMaxLength(RoleFieldRule.NameMaxLength);
            rule.Property(f => f.Field).HasMaxLength(RoleFieldRule.NameMaxLength);
            rule.Property(f => f.Access).HasConversion<string>().HasMaxLength(20);
            rule.HasIndex(f => f.TenantId);
        });

        modelBuilder.Entity<RoleDefaultGrant>(grant =>
        {
            grant.ToTable("role_default_grants");
            grant.HasKey(g => new { g.TenantId, g.RoleKey, g.Grant });
            grant.HasOne<Tenant>().WithMany().HasForeignKey(g => g.TenantId).OnDelete(DeleteBehavior.Restrict);
            grant.Property(g => g.RoleKey).HasMaxLength(Role.KeyMaxLength);
            grant.Property(g => g.Grant).HasMaxLength(RoleDefaultGrant.GrantMaxLength);
        });

        modelBuilder.Entity<IdentitySettings>(settings =>
        {
            settings.ToTable("identity_settings", table =>
                table.HasCheckConstraint("ck_identity_settings_email_code_sign_in",
                    EnumCheck("email_code_sign_in", Enum.GetNames<EmailCodeSignIn>())));
            settings.HasKey(s => s.TenantId);
            settings.HasOne<Tenant>().WithMany().HasForeignKey(s => s.TenantId).OnDelete(DeleteBehavior.Restrict);
            settings.Property(s => s.EmailCodeSignIn).HasConversion<string>().HasMaxLength(20);
            settings.Property(s => s.Version).IsRowVersion();
        });
    }

    private static string EnumCheck(string column, IEnumerable<string> values) =>
        $"{column} IN ({string.Join(", ", values.Select(v => $"'{v}'"))})";
}
