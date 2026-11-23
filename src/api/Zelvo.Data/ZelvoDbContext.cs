using Microsoft.EntityFrameworkCore;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;
using Zelvo.Data.Auditing;
using Zelvo.Data.Identity;
using Zelvo.Data.Localization;
using Zelvo.Data.Search;
using Zelvo.Data.Secrets;
using Zelvo.Data.Settings;
using Zelvo.Data.Tenancy;
using Zelvo.Data.Tickets;

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

    /// <summary>Pending changes to tenants' search indices (transactional outbox).</summary>
    public DbSet<SearchOutboxEntry> SearchOutbox => Set<SearchOutboxEntry>();

    /// <summary>Indexing state of each tenant's search index.</summary>
    public DbSet<SearchIndexState> SearchIndexStates => Set<SearchIndexState>();

    /// <summary>Tenants' data keys for secrets stored in the database (wrapped, never in clear).</summary>
    public DbSet<TenantDataKey> TenantDataKeys => Set<TenantDataKey>();

    /// <summary>Tickets of every record type (shared core, module 1).</summary>
    public DbSet<Ticket> Tickets => Set<Ticket>();

    /// <summary>Incident details of tickets.</summary>
    public DbSet<Incident> Incidents => Set<Incident>();

    /// <summary>Last number given per record type.</summary>
    public DbSet<TicketNumberSequence> TicketNumberSequences => Set<TicketNumberSequence>();

    /// <summary>Files attached to tickets.</summary>
    public DbSet<TicketAttachment> TicketAttachments => Set<TicketAttachment>();

    /// <summary>Content of attachments (PostgreSQL attachment store).</summary>
    public DbSet<TicketAttachmentContent> TicketAttachmentContents => Set<TicketAttachmentContent>();

    /// <summary>Major-incident communication timeline.</summary>
    public DbSet<MajorIncidentUpdate> MajorIncidentUpdates => Set<MajorIncidentUpdate>();

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

        ConfigureSearch(modelBuilder);
        ConfigureTickets(modelBuilder);

        modelBuilder.Entity<TenantDataKey>(key =>
        {
            key.ToTable("tenant_data_keys", table =>
                table.HasCheckConstraint("ck_tenant_data_keys_kek_id", "kek_id ~ '^[a-z]+:[A-Za-z0-9._/-]+$'"));
            key.HasKey(k => k.Id);
            key.Property(k => k.Id).ValueGeneratedNever();
            key.HasOne<Tenant>().WithMany().HasForeignKey(k => k.TenantId).OnDelete(DeleteBehavior.Restrict);
            key.Property(k => k.KekId).HasMaxLength(TenantDataKey.KekIdMaxLength);

            // One active key per tenant.
            key.HasIndex(k => k.TenantId).IsUnique().HasFilter("retired_at IS NULL").HasDatabaseName("ux_tenant_data_keys_active");
        });
    }

    private static void ConfigureTickets(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Ticket>(ticket =>
        {
            ticket.ToTable("tickets", table =>
            {
                table.HasCheckConstraint("ck_tickets_type", EnumCheck("type", Enum.GetNames<TicketType>()));
                table.HasCheckConstraint("ck_tickets_state", EnumCheck("state", Enum.GetNames<TicketState>()));
                table.HasCheckConstraint("ck_tickets_channel", EnumCheck("channel", Enum.GetNames<TicketChannel>()));
                table.HasCheckConstraint("ck_tickets_on_hold_reason", "(state = 'OnHold') = (on_hold_reason IS NOT NULL)");
                table.HasCheckConstraint("ck_tickets_not_own_parent", "parent_id IS NULL OR parent_id <> id");
            });
            ticket.HasKey(t => t.Id);
            ticket.Property(t => t.Id).ValueGeneratedNever();
            ticket.HasOne<Tenant>().WithMany().HasForeignKey(t => t.TenantId).OnDelete(DeleteBehavior.Restrict);
            ticket.Property(t => t.Type).HasConversion<string>().HasMaxLength(30);
            ticket.Property(t => t.State).HasConversion<string>().HasMaxLength(20);
            ticket.Property(t => t.Channel).HasConversion<string>().HasMaxLength(20);
            ticket.Property(t => t.Number).HasMaxLength(Ticket.NumberMaxLength);
            ticket.Property(t => t.Title).HasMaxLength(Ticket.TitleMaxLength);
            ticket.Property(t => t.Description).HasMaxLength(Ticket.DescriptionMaxLength);
            ticket.Property(t => t.ResolutionNotes).HasMaxLength(Ticket.ResolutionNotesMaxLength);
            ticket.Property(t => t.OnHoldReason).HasMaxLength(Ticket.ReasonKeyMaxLength);
            ticket.Property(t => t.Version).IsRowVersion();
            ticket.HasOne(t => t.Requester).WithMany().HasForeignKey(t => t.RequesterId).OnDelete(DeleteBehavior.Restrict);
            ticket.HasOne(t => t.Assignee).WithMany().HasForeignKey(t => t.AssigneeId).OnDelete(DeleteBehavior.Restrict);
            ticket.HasOne<UserAccount>().WithMany().HasForeignKey(t => t.CreatedById).OnDelete(DeleteBehavior.Restrict);
            // NO ACTION (checked at the end of each statement), so a tenant's tickets can be deleted together.
            ticket.HasOne<Ticket>().WithMany().HasForeignKey(t => t.ParentId).OnDelete(DeleteBehavior.NoAction);
            ticket.HasOne<Ticket>().WithMany().HasForeignKey(t => t.FollowUpOfId).OnDelete(DeleteBehavior.NoAction);
            ticket.HasOne(t => t.Incident).WithOne().HasForeignKey<Incident>(i => i.TicketId).OnDelete(DeleteBehavior.Cascade);
            ticket.HasIndex(t => new { t.TenantId, t.Number }).IsUnique();
            ticket.HasIndex(t => new { t.TenantId, t.Type, t.Sequence }).IsUnique();
            ticket.HasIndex(t => new { t.TenantId, t.RequesterId, t.CreatedAt });
            ticket.HasIndex(t => new { t.TenantId, t.AssigneeId });
            ticket.HasIndex(t => new { t.TenantId, t.ParentId });
            ticket.HasIndex(t => new { t.TenantId, t.State, t.ResolvedAt });
            ticket.HasIndex(t => new { t.TenantId, t.CreatedAt });
        });

        modelBuilder.Entity<Incident>(incident =>
        {
            incident.ToTable("incidents", table =>
            {
                table.HasCheckConstraint("ck_incidents_levels", "impact BETWEEN 1 AND 9 AND urgency BETWEEN 1 AND 9 AND priority BETWEEN 1 AND 9");
                table.HasCheckConstraint("ck_incidents_major", "NOT is_major OR major_declared_at IS NOT NULL");
            });
            incident.HasKey(i => i.TicketId);
            incident.HasOne<UserAccount>().WithMany().HasForeignKey(i => i.MajorDeclaredById).OnDelete(DeleteBehavior.Restrict);
            incident.HasIndex(i => new { i.TenantId, i.Priority });
            incident.HasIndex(i => new { i.TenantId, i.IsMajor });
        });

        modelBuilder.Entity<TicketNumberSequence>(sequence =>
        {
            sequence.ToTable("ticket_number_sequences", table =>
                table.HasCheckConstraint("ck_ticket_number_sequences_type", EnumCheck("type", Enum.GetNames<TicketType>())));
            sequence.HasKey(s => new { s.TenantId, s.Type });
            sequence.HasOne<Tenant>().WithMany().HasForeignKey(s => s.TenantId).OnDelete(DeleteBehavior.Restrict);
            sequence.Property(s => s.Type).HasConversion<string>().HasMaxLength(30);
        });

        modelBuilder.Entity<TicketAttachment>(attachment =>
        {
            attachment.ToTable("ticket_attachments", table =>
            {
                table.HasCheckConstraint("ck_ticket_attachments_scan_status", EnumCheck("scan_status", Enum.GetNames<AttachmentScanStatus>()));
                table.HasCheckConstraint("ck_ticket_attachments_size", "size >= 0");
                table.HasCheckConstraint("ck_ticket_attachments_sha256", "sha256 ~ '^[0-9a-f]{64}$'");
            });
            attachment.HasKey(a => a.Id);
            attachment.Property(a => a.Id).ValueGeneratedNever();
            attachment.HasOne<Ticket>().WithMany().HasForeignKey(a => a.TicketId).OnDelete(DeleteBehavior.Cascade);
            attachment.HasOne<UserAccount>().WithMany().HasForeignKey(a => a.UploadedById).OnDelete(DeleteBehavior.Restrict);
            attachment.Property(a => a.FileName).HasMaxLength(TicketAttachment.FileNameMaxLength);
            attachment.Property(a => a.ContentType).HasMaxLength(100);
            attachment.Property(a => a.Sha256).HasMaxLength(64).IsFixedLength();
            attachment.Property(a => a.ScanStatus).HasConversion<string>().HasMaxLength(20);
            attachment.Property(a => a.ScanResult).HasMaxLength(TicketAttachment.ScanResultMaxLength);
            attachment.HasIndex(a => new { a.TenantId, a.TicketId });
            // The scanner looks for waiting files, oldest first.
            attachment.HasIndex(a => new { a.TenantId, a.UploadedAt }).HasFilter("scan_status = 'Pending'").HasDatabaseName("ix_ticket_attachments_pending");
        });

        modelBuilder.Entity<TicketAttachmentContent>(content =>
        {
            content.ToTable("ticket_attachment_contents");
            content.HasKey(c => c.AttachmentId);
            content.HasOne<TicketAttachment>().WithOne().HasForeignKey<TicketAttachmentContent>(c => c.AttachmentId).OnDelete(DeleteBehavior.Cascade);
            content.HasIndex(c => c.TenantId);
        });

        modelBuilder.Entity<MajorIncidentUpdate>(update =>
        {
            update.ToTable("major_incident_updates", table =>
                table.HasCheckConstraint("ck_major_incident_updates_audience", EnumCheck("audience", Enum.GetNames<UpdateAudience>())));
            update.HasKey(u => u.Id);
            update.Property(u => u.Id).ValueGeneratedNever();
            update.HasOne<Ticket>().WithMany().HasForeignKey(u => u.TicketId).OnDelete(DeleteBehavior.Cascade);
            update.HasOne<UserAccount>().WithMany().HasForeignKey(u => u.PostedById).OnDelete(DeleteBehavior.Restrict);
            update.ConfigureLocalizedText(u => u.Body, "body", MajorIncidentUpdate.BodyMaxLength);
            update.Property(u => u.Audience).HasConversion<string>().HasMaxLength(20);
            update.HasIndex(u => new { u.TenantId, u.TicketId, u.PostedAt });
        });
    }

    private static void ConfigureSearch(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<SearchOutboxEntry>(entry =>
        {
            entry.ToTable("search_outbox", table =>
            {
                table.HasCheckConstraint("ck_search_outbox_operation", EnumCheck("operation", Enum.GetNames<SearchOperation>()));
                table.HasCheckConstraint("ck_search_outbox_shape",
                    "(operation = 'Purge' AND purge_before IS NOT NULL AND document_type IS NULL AND document_id IS NULL AND document IS NULL)"
                    + " OR (operation = 'Upsert' AND document_type IS NOT NULL AND document_id IS NOT NULL AND document IS NOT NULL)"
                    + " OR (operation = 'Delete' AND document_type IS NOT NULL AND document_id IS NOT NULL AND document IS NULL)");
                table.HasCheckConstraint("ck_search_outbox_document_type", "document_type ~ '^[a-z][a-z0-9-]{0,49}$'");
            });
            entry.HasKey(e => e.Id);
            entry.Property(e => e.Id).UseIdentityAlwaysColumn();
            entry.HasOne<Tenant>().WithMany().HasForeignKey(e => e.TenantId).OnDelete(DeleteBehavior.Restrict);
            entry.Property(e => e.Operation).HasConversion<string>().HasMaxLength(20);
            entry.Property(e => e.DocumentType).HasMaxLength(SearchOutboxEntry.DocumentTypeMaxLength);
            entry.Property(e => e.DocumentId).HasMaxLength(SearchOutboxEntry.DocumentIdMaxLength);
            entry.Property(e => e.Document).HasColumnType("jsonb");
            entry.Property(e => e.LastError).HasMaxLength(SearchOutboxEntry.ErrorMaxLength);

            // The relay looks for entries to publish per tenant, oldest first.
            entry.HasIndex(e => new { e.TenantId, e.Id }).HasFilter("failed_at IS NULL");
        });

        modelBuilder.Entity<SearchIndexState>(state =>
        {
            state.ToTable("search_index_states");
            state.HasKey(s => s.TenantId);
            state.HasOne<Tenant>().WithMany().HasForeignKey(s => s.TenantId).OnDelete(DeleteBehavior.Restrict);
            state.Property(s => s.LastError).HasMaxLength(SearchIndexState.ErrorMaxLength);
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
                table.HasCheckConstraint("ck_users_emirates_id", "emirates_id ~ '^784[0-9]{12}$'");
            });
            user.HasKey(u => u.Id);
            user.Property(u => u.Id).ValueGeneratedNever();
            user.HasOne<Tenant>().WithMany().HasForeignKey(u => u.TenantId).OnDelete(DeleteBehavior.Restrict);
            user.Property(u => u.Subject).HasMaxLength(UserAccount.SubjectMaxLength);
            user.Property(u => u.Email).HasMaxLength(UserAccount.EmailMaxLength);
            user.Property(u => u.DisplayName).HasMaxLength(UserAccount.DisplayNameMaxLength);
            user.Property(u => u.EmiratesId).HasMaxLength(UserAccount.EmiratesIdLength);
            user.Property(u => u.Status).HasConversion<string>().HasMaxLength(20);
            user.Property(u => u.PreferredLanguage).HasMaxLength(2);
            user.Property(u => u.Calendar).HasConversion<string>().HasMaxLength(20);
            user.Property(u => u.Digits).HasConversion<string>().HasMaxLength(20);
            user.Property(u => u.Version).IsRowVersion();
            user.HasIndex(u => new { u.TenantId, u.Subject }).IsUnique();
            user.HasIndex(u => new { u.TenantId, u.Email });
            user.HasIndex(u => new { u.TenantId, u.EmiratesId }).IsUnique().HasFilter("emirates_id IS NOT NULL");
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
