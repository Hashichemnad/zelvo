using Microsoft.EntityFrameworkCore;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;
using Zelvo.Data.Ai;
using Zelvo.Data.Auditing;
using Zelvo.Data.Directory;
using Zelvo.Data.Identity;
using Zelvo.Data.Integrations;
using Zelvo.Data.Knowledge;
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

    /// <summary>Integrations that call the API: API keys and OAuth clients (module 14).</summary>
    public DbSet<ApiClient> ApiClients => Set<ApiClient>();

    /// <summary>Outgoing webhook subscriptions (module 14).</summary>
    public DbSet<WebhookSubscription> WebhookSubscriptions => Set<WebhookSubscription>();

    /// <summary>Webhook deliveries: queue and delivery log.</summary>
    public DbSet<WebhookDelivery> WebhookDeliveries => Set<WebhookDelivery>();

    /// <summary>Integration log: calls in and out, personal data masked.</summary>
    public DbSet<IntegrationLogEntry> IntegrationLog => Set<IntegrationLogEntry>();

    /// <summary>Category tree (task B2).</summary>
    public DbSet<Category> Categories => Set<Category>();

    /// <summary>Location tree (task B2).</summary>
    public DbSet<Location> Locations => Set<Location>();

    /// <summary>Skills agents can have (task B2).</summary>
    public DbSet<Skill> Skills => Set<Skill>();

    /// <summary>Skills of users.</summary>
    public DbSet<UserSkill> UserSkills => Set<UserSkill>();

    /// <summary>Groups tickets are assigned to (task B2).</summary>
    public DbSet<SupportGroup> SupportGroups => Set<SupportGroup>();

    /// <summary>Members of groups.</summary>
    public DbSet<SupportGroupMember> SupportGroupMembers => Set<SupportGroupMember>();

    /// <summary>Round-robin state of groups.</summary>
    public DbSet<SupportGroupRotation> SupportGroupRotations => Set<SupportGroupRotation>();

    /// <summary>Assignment rules, in order (task B2).</summary>
    public DbSet<AssignmentRule> AssignmentRules => Set<AssignmentRule>();

    /// <summary>Knowledge articles (module 7).</summary>
    public DbSet<KbArticle> KbArticles => Set<KbArticle>();

    /// <summary>Versions of knowledge articles.</summary>
    public DbSet<KbArticleVersion> KbArticleVersions => Set<KbArticleVersion>();

    /// <summary>"Was this helpful?" answers.</summary>
    public DbSet<KbFeedback> KbFeedbackEntries => Set<KbFeedback>();

    /// <summary>Article views per viewer and day.</summary>
    public DbSet<KbArticleView> KbArticleViews => Set<KbArticleView>();

    /// <summary>AI provider connections (module 9).</summary>
    public DbSet<AiProvider> AiProviders => Set<AiProvider>();

    /// <summary>Provider and model of each AI feature.</summary>
    public DbSet<AiFeatureSetting> AiFeatureSettings => Set<AiFeatureSetting>();

    /// <summary>Prices of AI models, entered by administrators.</summary>
    public DbSet<AiModelPrice> AiModelPrices => Set<AiModelPrice>();

    /// <summary>AI calls: usage, cost and the AI log.</summary>
    public DbSet<AiCall> AiCalls => Set<AiCall>();

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
        ConfigureDirectory(modelBuilder);
        ConfigureTickets(modelBuilder);
        ConfigureIntegrations(modelBuilder);
        ConfigureAi(modelBuilder);
        ConfigureKnowledge(modelBuilder);

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

    private static void ConfigureDirectory(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Category>(category => ConfigureTree(category, "categories"));
        modelBuilder.Entity<Location>(location => ConfigureTree(location, "locations"));

        modelBuilder.Entity<Skill>(skill =>
        {
            skill.ToTable("skills");
            skill.HasKey(s => s.Id);
            skill.Property(s => s.Id).ValueGeneratedNever();
            skill.HasOne<Tenant>().WithMany().HasForeignKey(s => s.TenantId).OnDelete(DeleteBehavior.Restrict);
            skill.ConfigureLocalizedText(s => s.Name, "name", Skill.NameMaxLength);
            skill.HasIndex(s => s.TenantId);
        });

        modelBuilder.Entity<UserSkill>(userSkill =>
        {
            userSkill.ToTable("user_skills");
            userSkill.HasKey(u => new { u.UserId, u.SkillId });
            userSkill.HasOne(u => u.Skill).WithMany().HasForeignKey(u => u.SkillId).OnDelete(DeleteBehavior.Cascade);
            userSkill.HasIndex(u => new { u.TenantId, u.SkillId });
        });

        modelBuilder.Entity<SupportGroup>(group =>
        {
            group.ToTable("support_groups");
            group.HasKey(g => g.Id);
            group.Property(g => g.Id).ValueGeneratedNever();
            group.HasOne<Tenant>().WithMany().HasForeignKey(g => g.TenantId).OnDelete(DeleteBehavior.Restrict);
            group.ConfigureLocalizedText(g => g.Name, "name", SupportGroup.NameMaxLength);
            group.ConfigureLocalizedText(g => g.Description, "description", SupportGroup.DescriptionMaxLength);
            group.Property(g => g.Version).IsRowVersion();
            group.HasMany(g => g.Members).WithOne().HasForeignKey(m => m.GroupId).OnDelete(DeleteBehavior.Cascade);
            group.HasIndex(g => g.TenantId);
        });

        modelBuilder.Entity<SupportGroupMember>(member =>
        {
            member.ToTable("support_group_members");
            member.HasKey(m => new { m.GroupId, m.UserId });
            member.HasOne(m => m.User).WithMany().HasForeignKey(m => m.UserId).OnDelete(DeleteBehavior.Cascade);
            member.HasIndex(m => new { m.TenantId, m.UserId });
        });

        modelBuilder.Entity<SupportGroupRotation>(rotation =>
        {
            rotation.ToTable("support_group_rotation");
            rotation.HasKey(r => new { r.GroupId, r.UserId });
            rotation.HasOne<SupportGroupMember>().WithOne()
                .HasForeignKey<SupportGroupRotation>(r => new { r.GroupId, r.UserId })
                .HasPrincipalKey<SupportGroupMember>(m => new { m.GroupId, m.UserId })
                .OnDelete(DeleteBehavior.Cascade);
            rotation.HasIndex(r => r.TenantId);
        });

        modelBuilder.Entity<AssignmentRule>(rule =>
        {
            rule.ToTable("assignment_rules", table =>
            {
                table.HasCheckConstraint("ck_assignment_rules_method", EnumCheck("method", Enum.GetNames<AssignmentMethod>()));
                table.HasCheckConstraint("ck_assignment_rules_language", EnumCheck("language", LocalizedText.Languages));
                table.HasCheckConstraint("ck_assignment_rules_skill", "skill_id IS NULL OR method = 'RoundRobin'");
            });
            rule.HasKey(r => r.Id);
            rule.Property(r => r.Id).ValueGeneratedNever();
            rule.HasOne<Tenant>().WithMany().HasForeignKey(r => r.TenantId).OnDelete(DeleteBehavior.Restrict);
            rule.ConfigureLocalizedText(r => r.Name, "name", AssignmentRule.NameMaxLength);
            rule.Property(r => r.Language).HasMaxLength(2);
            rule.Property(r => r.Method).HasConversion<string>().HasMaxLength(20);
            rule.Property(r => r.Version).IsRowVersion();
            rule.HasOne<Category>().WithMany().HasForeignKey(r => r.CategoryId).OnDelete(DeleteBehavior.Restrict);
            rule.HasOne<Location>().WithMany().HasForeignKey(r => r.LocationId).OnDelete(DeleteBehavior.Restrict);
            rule.HasOne<SupportGroup>().WithMany().HasForeignKey(r => r.GroupId).OnDelete(DeleteBehavior.Restrict);
            rule.HasOne<Skill>().WithMany().HasForeignKey(r => r.SkillId).OnDelete(DeleteBehavior.Restrict);
            rule.HasIndex(r => new { r.TenantId, r.Position });
        });
    }

    private static void ConfigureTree<TNode>(Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<TNode> node, string table)
        where TNode : TreeNode
    {
        node.ToTable(table, t =>
        {
            t.HasCheckConstraint($"ck_{table}_depth", $"depth BETWEEN 1 AND {TreeNode.MaxDepth}");
            t.HasCheckConstraint($"ck_{table}_parent", "(depth = 1) = (parent_id IS NULL)");
            t.HasCheckConstraint($"ck_{table}_not_own_parent", "parent_id IS NULL OR parent_id <> id");
        });
        node.HasKey(n => n.Id);
        node.Property(n => n.Id).ValueGeneratedNever();
        node.HasOne<Tenant>().WithMany().HasForeignKey(n => n.TenantId).OnDelete(DeleteBehavior.Restrict);
        // NO ACTION (checked at the end of each statement), so a tenant's tree can be deleted together.
        node.HasOne<TNode>().WithMany().HasForeignKey(n => n.ParentId).OnDelete(DeleteBehavior.NoAction);
        node.ConfigureLocalizedText(n => n.Name, "name", TreeNode.NameMaxLength);
        node.Property(n => n.Version).IsRowVersion();
        node.HasIndex(n => new { n.TenantId, n.ParentId, n.Position });
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
            ticket.HasOne(t => t.Category).WithMany().HasForeignKey(t => t.CategoryId).OnDelete(DeleteBehavior.Restrict);
            ticket.HasOne(t => t.Location).WithMany().HasForeignKey(t => t.LocationId).OnDelete(DeleteBehavior.Restrict);
            ticket.HasOne(t => t.Group).WithMany().HasForeignKey(t => t.GroupId).OnDelete(DeleteBehavior.Restrict);
            // The rule may be deleted later; the ticket then simply no longer names it.
            ticket.HasOne<AssignmentRule>().WithMany().HasForeignKey(t => t.RoutingRuleId).OnDelete(DeleteBehavior.SetNull);
            ticket.HasIndex(t => new { t.TenantId, t.GroupId });
            ticket.HasIndex(t => new { t.TenantId, t.CategoryId });
            ticket.HasIndex(t => new { t.TenantId, t.LocationId });
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
                table.HasCheckConstraint("ck_ticket_attachments_owner", "(ticket_id IS NULL) <> (article_id IS NULL)");
            });
            attachment.HasKey(a => a.Id);
            attachment.Property(a => a.Id).ValueGeneratedNever();
            attachment.HasOne<Ticket>().WithMany().HasForeignKey(a => a.TicketId).OnDelete(DeleteBehavior.Cascade);
            attachment.HasOne<KbArticle>().WithMany().HasForeignKey(a => a.ArticleId).OnDelete(DeleteBehavior.Cascade);
            attachment.HasIndex(a => new { a.TenantId, a.ArticleId }).HasFilter("article_id IS NOT NULL");
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

    private static void ConfigureIntegrations(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ApiClient>(client =>
        {
            client.ToTable("api_clients", table =>
            {
                table.HasCheckConstraint("ck_api_clients_kind", EnumCheck("kind", Enum.GetNames<ApiClientKind>()));
                table.HasCheckConstraint("ck_api_clients_status", EnumCheck("status", Enum.GetNames<ApiClientStatus>()));
                table.HasCheckConstraint("ck_api_clients_rate_limit",
                    $"rate_limit_per_minute BETWEEN {ApiClient.MinRateLimit} AND {ApiClient.MaxRateLimit}");
                table.HasCheckConstraint("ck_api_clients_credentials",
                    "(kind = 'ApiKey' AND key_prefix IS NOT NULL AND key_hash IS NOT NULL AND keycloak_client_id IS NULL)"
                    + " OR (kind = 'OAuthClient' AND key_prefix IS NULL AND key_hash IS NULL AND keycloak_client_id IS NOT NULL)");
                table.HasCheckConstraint("ck_api_clients_key_prefix", "key_prefix ~ '^[a-z0-9]{12}$'");
            });
            client.HasKey(c => c.Id);
            client.Property(c => c.Id).ValueGeneratedNever();
            client.HasOne<Tenant>().WithMany().HasForeignKey(c => c.TenantId).OnDelete(DeleteBehavior.Restrict);
            client.HasOne<Role>().WithMany().HasForeignKey(c => c.RoleId).OnDelete(DeleteBehavior.Restrict);
            client.Property(c => c.Kind).HasConversion<string>().HasMaxLength(20);
            client.Property(c => c.Status).HasConversion<string>().HasMaxLength(20);
            client.Property(c => c.Name).HasMaxLength(ApiClient.NameMaxLength);
            client.Property(c => c.Description).HasMaxLength(ApiClient.DescriptionMaxLength);
            client.Property(c => c.KeyPrefix).HasMaxLength(ApiClient.KeyPrefixLength);
            client.Property(c => c.KeycloakClientId).HasMaxLength(ApiClient.KeycloakIdMaxLength);
            client.Property(c => c.KeycloakInternalId).HasMaxLength(ApiClient.KeycloakIdMaxLength);
            client.Property(c => c.ServiceAccountSubject).HasMaxLength(ApiClient.KeycloakIdMaxLength);
            client.Property(c => c.Version).IsRowVersion();
            client.HasIndex(c => new { c.TenantId, c.KeyPrefix }).IsUnique().HasFilter("key_prefix IS NOT NULL");
            client.HasIndex(c => new { c.TenantId, c.KeycloakClientId }).IsUnique().HasFilter("keycloak_client_id IS NOT NULL");
            client.HasIndex(c => new { c.TenantId, c.ServiceAccountSubject }).IsUnique().HasFilter("service_account_subject IS NOT NULL");
            client.HasIndex(c => c.RoleId);
        });

        modelBuilder.Entity<WebhookSubscription>(subscription =>
        {
            subscription.ToTable("webhook_subscriptions", table =>
            {
                table.HasCheckConstraint("ck_webhook_subscriptions_status", EnumCheck("status", Enum.GetNames<WebhookStatus>()));
                table.HasCheckConstraint("ck_webhook_subscriptions_url", "url ~ '^https?://'");
            });
            subscription.HasKey(s => s.Id);
            subscription.Property(s => s.Id).ValueGeneratedNever();
            subscription.HasOne<Tenant>().WithMany().HasForeignKey(s => s.TenantId).OnDelete(DeleteBehavior.Restrict);
            subscription.Property(s => s.Name).HasMaxLength(WebhookSubscription.NameMaxLength);
            subscription.Property(s => s.Url).HasMaxLength(WebhookSubscription.UrlMaxLength);
            subscription.Property(s => s.Status).HasConversion<string>().HasMaxLength(20);
            subscription.Property(s => s.DisabledReason).HasMaxLength(WebhookSubscription.ReasonMaxLength);
            subscription.Property(s => s.Version).IsRowVersion();
            subscription.HasIndex(s => new { s.TenantId, s.Status });
        });

        modelBuilder.Entity<WebhookDelivery>(delivery =>
        {
            delivery.ToTable("webhook_deliveries", table =>
                table.HasCheckConstraint("ck_webhook_deliveries_status", EnumCheck("status", Enum.GetNames<WebhookDeliveryStatus>())));
            delivery.HasKey(d => d.Id);
            delivery.Property(d => d.Id).UseIdentityAlwaysColumn();
            delivery.HasOne<Tenant>().WithMany().HasForeignKey(d => d.TenantId).OnDelete(DeleteBehavior.Restrict);
            delivery.HasOne<WebhookSubscription>().WithMany().HasForeignKey(d => d.SubscriptionId).OnDelete(DeleteBehavior.Cascade);
            delivery.Property(d => d.EventType).HasMaxLength(WebhookDelivery.EventTypeMaxLength);
            delivery.Property(d => d.Payload).HasColumnType("jsonb");
            delivery.Property(d => d.Status).HasConversion<string>().HasMaxLength(20);
            delivery.Property(d => d.LastError).HasMaxLength(WebhookDelivery.ErrorMaxLength);

            // The worker looks for due deliveries per tenant; the delivery log lists a subscription's newest first.
            delivery.HasIndex(d => new { d.TenantId, d.NextAttemptAt }).HasFilter("status = 'Pending'");
            delivery.HasIndex(d => new { d.SubscriptionId, d.Id });
            delivery.HasIndex(d => new { d.TenantId, d.CreatedAt });
        });

        modelBuilder.Entity<IntegrationLogEntry>(entry =>
        {
            entry.ToTable("integration_log", table =>
                table.HasCheckConstraint("ck_integration_log_direction", EnumCheck("direction", Enum.GetNames<IntegrationDirection>())));
            entry.HasKey(e => e.Id);
            entry.Property(e => e.Id).UseIdentityAlwaysColumn();
            entry.HasOne<Tenant>().WithMany().HasForeignKey(e => e.TenantId).OnDelete(DeleteBehavior.Restrict);
            entry.Property(e => e.Direction).HasConversion<string>().HasMaxLength(20);
            entry.Property(e => e.EventType).HasMaxLength(WebhookDelivery.EventTypeMaxLength);
            entry.Property(e => e.Method).HasMaxLength(IntegrationLogEntry.MethodMaxLength);
            entry.Property(e => e.Target).HasMaxLength(IntegrationLogEntry.TargetMaxLength);
            entry.Property(e => e.RequestBody).HasMaxLength(IntegrationLogEntry.BodyMaxLength);
            entry.Property(e => e.ResponseBody).HasMaxLength(IntegrationLogEntry.BodyMaxLength);
            entry.Property(e => e.Error).HasMaxLength(IntegrationLogEntry.ErrorMaxLength);
            entry.Property(e => e.CorrelationId).HasMaxLength(IntegrationLogEntry.CorrelationIdMaxLength);
            entry.HasIndex(e => new { e.TenantId, e.Id });
            entry.HasIndex(e => new { e.TenantId, e.OccurredAt });
            entry.HasIndex(e => new { e.TenantId, e.ApiClientId, e.Id }).HasFilter("api_client_id IS NOT NULL");
            entry.HasIndex(e => new { e.TenantId, e.SubscriptionId, e.Id }).HasFilter("subscription_id IS NOT NULL");
        });
    }

    private static void ConfigureKnowledge(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<KbArticle>(article =>
        {
            article.ToTable("kb_articles", table =>
            {
                table.HasCheckConstraint("ck_kb_articles_state", EnumCheck("state", Enum.GetNames<ArticleState>()));
                table.HasCheckConstraint("ck_kb_articles_visibility", EnumCheck("visibility", Enum.GetNames<ArticleVisibility>()));
                table.HasCheckConstraint("ck_kb_articles_counts", "view_count >= 0 AND helpful_count >= 0 AND not_helpful_count >= 0");
                table.HasCheckConstraint("ck_kb_articles_published", "state <> 'Published' OR published_version_id IS NOT NULL");
            });
            article.HasKey(a => a.Id);
            article.Property(a => a.Id).ValueGeneratedNever();
            article.HasOne<Tenant>().WithMany().HasForeignKey(a => a.TenantId).OnDelete(DeleteBehavior.Restrict);
            article.HasOne<Directory.Category>().WithMany().HasForeignKey(a => a.CategoryId).OnDelete(DeleteBehavior.Restrict);
            article.HasOne<UserAccount>().WithMany().HasForeignKey(a => a.CreatedById).OnDelete(DeleteBehavior.Restrict);
            article.HasOne<Ticket>().WithMany().HasForeignKey(a => a.SourceTicketId).OnDelete(DeleteBehavior.SetNull);
            article.Property(a => a.Number).HasMaxLength(KbArticle.NumberMaxLength);
            article.Property(a => a.State).HasConversion<string>().HasMaxLength(20);
            article.Property(a => a.Visibility).HasConversion<string>().HasMaxLength(20);
            article.HasIndex(a => new { a.TenantId, a.Sequence }).IsUnique();
            article.HasIndex(a => new { a.TenantId, a.Number }).IsUnique();
            article.HasIndex(a => new { a.TenantId, a.State, a.Visibility });
            article.HasIndex(a => a.CategoryId);
            article.HasIndex(a => a.SourceTicketId).HasFilter("source_ticket_id IS NOT NULL");
            article.HasIndex(a => a.CreatedById);
        });

        modelBuilder.Entity<KbArticleVersion>(version =>
        {
            version.ToTable("kb_article_versions", table =>
            {
                table.HasCheckConstraint("ck_kb_article_versions_state", EnumCheck("state", Enum.GetNames<ArticleVersionState>()));
                table.HasCheckConstraint("ck_kb_article_versions_visibility", EnumCheck("visibility", Enum.GetNames<ArticleVisibility>()));
                table.HasCheckConstraint("ck_kb_article_versions_number", "number >= 1");
                table.HasCheckConstraint("ck_kb_article_versions_title", "title_en IS NOT NULL OR title_ar IS NOT NULL");
            });
            version.HasKey(v => v.Id);
            version.Property(v => v.Id).ValueGeneratedNever();
            version.HasOne<Tenant>().WithMany().HasForeignKey(v => v.TenantId).OnDelete(DeleteBehavior.Restrict);
            version.HasOne<KbArticle>().WithMany().HasForeignKey(v => v.ArticleId).OnDelete(DeleteBehavior.Cascade);
            version.HasOne<Directory.Category>().WithMany().HasForeignKey(v => v.CategoryId).OnDelete(DeleteBehavior.Restrict);
            version.ConfigureLocalizedText(v => v.Title, "title", KbArticleVersion.TitleMaxLength);
            version.ConfigureLocalizedText(v => v.Summary, "summary", KbArticleVersion.SummaryMaxLength);
            version.ConfigureLocalizedText(v => v.Body, "body", KbArticleVersion.BodyMaxLength);
            version.Property(v => v.State).HasConversion<string>().HasMaxLength(20);
            version.Property(v => v.Visibility).HasConversion<string>().HasMaxLength(20);
            version.Property(v => v.ChangeNote).HasMaxLength(KbArticleVersion.NoteMaxLength);
            version.Property(v => v.ReviewComment).HasMaxLength(KbArticleVersion.NoteMaxLength);
            version.Property(v => v.Version).IsRowVersion();
            version.HasIndex(v => new { v.ArticleId, v.Number }).IsUnique();
            version.HasIndex(v => new { v.TenantId, v.State });
            version.HasIndex(v => v.CategoryId);
        });

        modelBuilder.Entity<KbFeedback>(feedback =>
        {
            feedback.ToTable("kb_feedback", table =>
            {
                table.HasCheckConstraint("ck_kb_feedback_who", "(user_id IS NULL) <> (visitor_hash IS NULL)");
                table.HasCheckConstraint("ck_kb_feedback_visitor_hash", "visitor_hash ~ '^[0-9a-f]{64}$'");
            });
            feedback.HasKey(f => f.Id);
            feedback.Property(f => f.Id).ValueGeneratedNever();
            feedback.HasOne<Tenant>().WithMany().HasForeignKey(f => f.TenantId).OnDelete(DeleteBehavior.Restrict);
            feedback.HasOne<KbArticle>().WithMany().HasForeignKey(f => f.ArticleId).OnDelete(DeleteBehavior.Cascade);
            feedback.HasOne<KbArticleVersion>().WithMany().HasForeignKey(f => f.VersionId).OnDelete(DeleteBehavior.SetNull);
            feedback.HasOne<UserAccount>().WithMany().HasForeignKey(f => f.UserId).OnDelete(DeleteBehavior.Cascade);
            feedback.Property(f => f.VisitorHash).HasMaxLength(64).IsFixedLength();
            feedback.Property(f => f.Comment).HasMaxLength(Knowledge.KbFeedback.CommentMaxLength);
            feedback.HasIndex(f => new { f.ArticleId, f.UserId }).IsUnique().HasFilter("user_id IS NOT NULL");
            feedback.HasIndex(f => new { f.ArticleId, f.VisitorHash }).IsUnique().HasFilter("visitor_hash IS NOT NULL");
            feedback.HasIndex(f => new { f.TenantId, f.ArticleId, f.CreatedAt });
            feedback.HasIndex(f => f.VersionId);
            feedback.HasIndex(f => f.UserId);
        });

        modelBuilder.Entity<KbArticleView>(view =>
        {
            view.ToTable("kb_article_views", table => table.HasCheckConstraint("ck_kb_article_views_viewer_hash", "viewer_hash ~ '^[0-9a-f]{64}$'"));
            view.HasKey(v => new { v.ArticleId, v.ViewerHash, v.Day });
            view.HasOne<Tenant>().WithMany().HasForeignKey(v => v.TenantId).OnDelete(DeleteBehavior.Restrict);
            view.HasOne<KbArticle>().WithMany().HasForeignKey(v => v.ArticleId).OnDelete(DeleteBehavior.Cascade);
            view.Property(v => v.ViewerHash).HasMaxLength(64).IsFixedLength();
            view.HasIndex(v => new { v.TenantId, v.Day });
        });
    }

    private static void ConfigureAi(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AiProvider>(provider =>
        {
            provider.ToTable("ai_providers", table =>
            {
                table.HasCheckConstraint("ck_ai_providers_kind", EnumCheck("kind", Enum.GetNames<AiProviderKind>()));
                table.HasCheckConstraint("ck_ai_providers_status", EnumCheck("status", Enum.GetNames<AiProviderStatus>()));
                table.HasCheckConstraint("ck_ai_providers_endpoint", "endpoint IS NULL OR endpoint ~ '^https?://'");
                table.HasCheckConstraint("ck_ai_providers_endpoint_required",
                    "kind NOT IN ('AzureOpenAI', 'OpenAICompatible') OR endpoint IS NOT NULL");
            });
            provider.HasKey(p => p.Id);
            provider.Property(p => p.Id).ValueGeneratedNever();
            provider.HasOne<Tenant>().WithMany().HasForeignKey(p => p.TenantId).OnDelete(DeleteBehavior.Restrict);
            provider.Property(p => p.Name).HasMaxLength(AiProvider.NameMaxLength);
            provider.Property(p => p.Kind).HasConversion<string>().HasMaxLength(20);
            provider.Property(p => p.Status).HasConversion<string>().HasMaxLength(20);
            provider.Property(p => p.Endpoint).HasMaxLength(AiProvider.EndpointMaxLength);
            provider.Property(p => p.ApiVersion).HasMaxLength(AiProvider.ApiVersionMaxLength);
            provider.Property(p => p.LastTestError).HasMaxLength(AiProvider.ErrorMaxLength);
            provider.Property(p => p.Version).IsRowVersion();
            provider.HasIndex(p => new { p.TenantId, p.Status });
        });

        modelBuilder.Entity<AiFeatureSetting>(feature =>
        {
            feature.ToTable("ai_feature_settings", table =>
            {
                table.HasCheckConstraint("ck_ai_feature_settings_feature", "feature ~ '^[a-z][a-zA-Z]*$'");
                table.HasCheckConstraint("ck_ai_feature_settings_enabled", "NOT enabled OR (provider_id IS NOT NULL AND model IS NOT NULL)");
            });
            feature.HasKey(f => new { f.TenantId, f.Feature });
            feature.HasOne<Tenant>().WithMany().HasForeignKey(f => f.TenantId).OnDelete(DeleteBehavior.Restrict);
            feature.HasOne<AiProvider>().WithMany().HasForeignKey(f => f.ProviderId).OnDelete(DeleteBehavior.Restrict);
            feature.Property(f => f.Feature).HasMaxLength(AiFeatureSetting.FeatureMaxLength);
            feature.Property(f => f.Model).HasMaxLength(AiFeatureSetting.ModelMaxLength);
            feature.Property(f => f.Version).IsRowVersion();
            feature.HasIndex(f => f.ProviderId);
        });

        modelBuilder.Entity<AiModelPrice>(price =>
        {
            price.ToTable("ai_model_prices", table =>
                table.HasCheckConstraint("ck_ai_model_prices_range",
                    $"input_per_million >= 0 AND input_per_million <= {AiModelPrice.MaxPrice} AND output_per_million >= 0 AND output_per_million <= {AiModelPrice.MaxPrice}"));
            price.HasKey(p => new { p.TenantId, p.ProviderId, p.Model });
            price.HasOne<Tenant>().WithMany().HasForeignKey(p => p.TenantId).OnDelete(DeleteBehavior.Restrict);
            price.HasOne<AiProvider>().WithMany().HasForeignKey(p => p.ProviderId).OnDelete(DeleteBehavior.Restrict);
            price.Property(p => p.Model).HasMaxLength(AiFeatureSetting.ModelMaxLength);
            price.Property(p => p.InputPerMillion).HasPrecision(12, 6);
            price.Property(p => p.OutputPerMillion).HasPrecision(12, 6);
            price.Property(p => p.Version).IsRowVersion();
            price.HasIndex(p => p.ProviderId);
        });

        modelBuilder.Entity<AiCall>(call =>
        {
            call.ToTable("ai_calls", table =>
            {
                table.HasCheckConstraint("ck_ai_calls_outcome", EnumCheck("outcome", Enum.GetNames<AiCallOutcome>()));
                table.HasCheckConstraint("ck_ai_calls_provider_kind", "provider_kind IS NULL OR " + EnumCheck("provider_kind", Enum.GetNames<AiProviderKind>()));
                table.HasCheckConstraint("ck_ai_calls_tokens", "input_tokens >= 0 AND output_tokens >= 0");
            });
            call.HasKey(c => c.Id);
            call.Property(c => c.Id).UseIdentityAlwaysColumn();
            call.HasOne<Tenant>().WithMany().HasForeignKey(c => c.TenantId).OnDelete(DeleteBehavior.Restrict);
            call.HasOne<AiProvider>().WithMany().HasForeignKey(c => c.ProviderId).OnDelete(DeleteBehavior.Restrict);
            call.Property(c => c.Feature).HasMaxLength(AiFeatureSetting.FeatureMaxLength);
            call.Property(c => c.ProviderKind).HasConversion<string>().HasMaxLength(20);
            call.Property(c => c.Model).HasMaxLength(AiFeatureSetting.ModelMaxLength);
            call.Property(c => c.EntityType).HasMaxLength(AiCall.EntityMaxLength);
            call.Property(c => c.EntityId).HasMaxLength(AiCall.EntityMaxLength);
            call.Property(c => c.Outcome).HasConversion<string>().HasMaxLength(20);
            call.Property(c => c.ErrorCode).HasMaxLength(AiCall.ErrorCodeMaxLength);
            call.Property(c => c.Error).HasMaxLength(AiCall.ErrorMaxLength);
            call.Property(c => c.CostUsd).HasPrecision(18, 8);
            call.Property(c => c.Prompt).HasMaxLength(AiCall.TextMaxLength);
            call.Property(c => c.Response).HasMaxLength(AiCall.TextMaxLength);
            call.Property(c => c.CorrelationId).HasMaxLength(AiCall.CorrelationIdMaxLength);

            // The dashboard and the monthly limit sum a tenant's calls by time; the AI log lists newest first.
            call.HasIndex(c => new { c.TenantId, c.OccurredAt });
            call.HasIndex(c => new { c.TenantId, c.Id });
            call.HasIndex(c => c.ProviderId);
            call.HasIndex(c => c.OccurredAt).HasFilter("prompt IS NOT NULL OR response IS NOT NULL").HasDatabaseName("ix_ai_calls_text_retention");
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
                table.HasCheckConstraint("ck_users_languages", "languages <@ ARRAY['en', 'ar']::text[]");
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
            user.HasMany(u => u.Skills).WithOne().HasForeignKey(s => s.UserId).OnDelete(DeleteBehavior.Cascade);
            user.Property(u => u.Languages).HasColumnType("text[]").HasDefaultValueSql("'{}'");
            user.Property(u => u.AvailableForAssignment).HasDefaultValue(true);
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
