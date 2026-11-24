using Microsoft.EntityFrameworkCore;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;
using Zelvo.Data.Ai;
using Zelvo.Data.Auditing;
using Zelvo.Data.Channels;
using Zelvo.Data.Directory;
using Zelvo.Data.Forms;
using Zelvo.Data.Identity;
using Zelvo.Data.Integrations;
using Zelvo.Data.Knowledge;
using Zelvo.Data.Localization;
using Zelvo.Data.Mcp;
using Zelvo.Data.Notifications;
using Zelvo.Data.Search;
using Zelvo.Data.Secrets;
using Zelvo.Data.Settings;
using Zelvo.Data.Sla;
using Zelvo.Data.Tenancy;
using Zelvo.Data.Tickets;
using Zelvo.Data.Workspace;

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

    /// <summary>Business calendars (task B3, GOV-7).</summary>
    public DbSet<BusinessCalendar> BusinessCalendars => Set<BusinessCalendar>();

    /// <summary>Working periods of calendars.</summary>
    public DbSet<BusinessCalendarPeriod> BusinessCalendarPeriods => Set<BusinessCalendarPeriod>();

    /// <summary>Holidays of calendars.</summary>
    public DbSet<BusinessCalendarHoliday> BusinessCalendarHolidays => Set<BusinessCalendarHoliday>();

    /// <summary>Customer groups for SLA policies (task B3).</summary>
    public DbSet<CustomerGroup> CustomerGroups => Set<CustomerGroup>();

    /// <summary>Requesters added to customer groups by hand.</summary>
    public DbSet<CustomerGroupMember> CustomerGroupMembers => Set<CustomerGroupMember>();

    /// <summary>SLA policies, in order (task B3).</summary>
    public DbSet<SlaPolicy> SlaPolicies => Set<SlaPolicy>();

    /// <summary>Targets of SLA policies per priority.</summary>
    public DbSet<SlaPolicyTarget> SlaPolicyTargets => Set<SlaPolicyTarget>();

    /// <summary>Escalation thresholds of SLA policies.</summary>
    public DbSet<SlaEscalation> SlaEscalations => Set<SlaEscalation>();

    /// <summary>SLA clocks of tickets.</summary>
    public DbSet<TicketSla> TicketSlas => Set<TicketSla>();

    /// <summary>Thresholds tickets reached, for notifications (B5).</summary>
    public DbSet<SlaAlert> SlaAlerts => Set<SlaAlert>();

    /// <summary>AI provider connections (module 9).</summary>
    public DbSet<AiProvider> AiProviders => Set<AiProvider>();

    /// <summary>Provider and model of each AI feature.</summary>
    public DbSet<AiFeatureSetting> AiFeatureSettings => Set<AiFeatureSetting>();

    /// <summary>Prices of AI models, entered by administrators.</summary>
    public DbSet<AiModelPrice> AiModelPrices => Set<AiModelPrice>();

    /// <summary>AI calls: usage, cost and the AI log.</summary>
    public DbSet<AiCall> AiCalls => Set<AiCall>();

    /// <summary>AI agents of the built-in MCP server (module 9, task E6).</summary>
    public DbSet<McpAgent> McpAgents => Set<McpAgent>();

    /// <summary>Changes AI agents asked for through MCP tools, with their approvals.</summary>
    public DbSet<McpAction> McpActions => Set<McpAction>();

    /// <summary>Customers' own MCP servers that Zelvo's AI may use as tools.</summary>
    public DbSet<McpServerConnection> McpServers => Set<McpServerConnection>();

    /// <summary>Notifications of people: queue and in-app list (module 10, task B5).</summary>
    public DbSet<Notification> Notifications => Set<Notification>();

    /// <summary>Emails of notifications: queue and delivery log.</summary>
    public DbSet<NotificationDelivery> NotificationDeliveries => Set<NotificationDelivery>();

    /// <summary>Tenants' own wording of notifications.</summary>
    public DbSet<NotificationTemplate> NotificationTemplates => Set<NotificationTemplate>();

    /// <summary>People's email choices per event.</summary>
    public DbSet<NotificationPreference> NotificationPreferences => Set<NotificationPreference>();

    /// <summary>Public replies and internal notes on tickets (module 5, task B4).</summary>
    public DbSet<TicketComment> TicketComments => Set<TicketComment>();

    /// <summary>Staff mentioned in comments.</summary>
    public DbSet<TicketCommentMention> TicketCommentMentions => Set<TicketCommentMention>();

    /// <summary>Canned responses in English and Arabic.</summary>
    public DbSet<CannedResponse> CannedResponses => Set<CannedResponse>();

    /// <summary>Saved views of the agent workspace.</summary>
    public DbSet<SavedView> SavedViews => Set<SavedView>();
    /// <summary>Mailboxes that turn emails into tickets (module 6).</summary>
    public DbSet<Mailbox> Mailboxes => Set<Mailbox>();

    /// <summary>Mailbox log: every email read.</summary>
    public DbSet<InboundEmail> InboundEmails => Set<InboundEmail>();

    /// <summary>Staff replies going out by email.</summary>
    public DbSet<OutboundEmail> OutboundEmails => Set<OutboundEmail>();

    /// <summary>Live chat widgets (module 6).</summary>
    public DbSet<ChatWidget> ChatWidgets => Set<ChatWidget>();

    /// <summary>Live chats.</summary>
    public DbSet<ChatSession> ChatSessions => Set<ChatSession>();

    /// <summary>Messages of live chats.</summary>
    public DbSet<ChatMessage> ChatMessages => Set<ChatMessage>();

    /// <inheritdoc />
    /// <summary>Departments (task C1).</summary>
    public DbSet<Department> Departments => Set<Department>();

    /// <summary>Forms (task C1).</summary>
    public DbSet<Form> Forms => Set<Form>();

    /// <summary>Published versions of forms.</summary>
    public DbSet<FormVersion> FormVersions => Set<FormVersion>();

    /// <summary>Reusable field sets.</summary>
    public DbSet<FieldSet> FieldSets => Set<FieldSet>();

    /// <summary>Published versions of field sets.</summary>
    public DbSet<FieldSetVersion> FieldSetVersions => Set<FieldSetVersion>();

    /// <summary>Which form each record type and category uses.</summary>
    public DbSet<FormBinding> FormBindings => Set<FormBinding>();

    /// <summary>Form answers of tickets.</summary>
    public DbSet<TicketFormResponse> TicketFormResponses => Set<TicketFormResponse>();

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
        ConfigureSla(modelBuilder);
        ConfigureIntegrations(modelBuilder);
        ConfigureAi(modelBuilder);
        ConfigureMcp(modelBuilder);
        ConfigureKnowledge(modelBuilder);
        ConfigureNotifications(modelBuilder);
        ConfigureForms(modelBuilder);
        ConfigureWorkspace(modelBuilder);
        ConfigureChannels(modelBuilder);

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
            group.ToTable("support_groups", table => table.HasCheckConstraint("ck_support_groups_chat_limit", "chat_limit BETWEEN 1 AND 20"));
            group.HasKey(g => g.Id);
            group.Property(g => g.Id).ValueGeneratedNever();
            group.HasOne<Tenant>().WithMany().HasForeignKey(g => g.TenantId).OnDelete(DeleteBehavior.Restrict);
            group.ConfigureLocalizedText(g => g.Name, "name", SupportGroup.NameMaxLength);
            group.ConfigureLocalizedText(g => g.Description, "description", SupportGroup.DescriptionMaxLength);
            group.Property(g => g.Version).IsRowVersion();
            group.HasMany(g => g.Members).WithOne().HasForeignKey(m => m.GroupId).OnDelete(DeleteBehavior.Cascade);
            group.HasIndex(g => g.TenantId);
            group.HasOne<BusinessCalendar>().WithMany().HasForeignKey(g => g.CalendarId).OnDelete(DeleteBehavior.Restrict);
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

    private static void ConfigureForms(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Form>(form =>
        {
            form.ToTable("forms", table =>
            {
                table.HasCheckConstraint("ck_forms_state", EnumCheck("state", Enum.GetNames<FormState>()));
                table.HasCheckConstraint("ck_forms_key", "key ~ '^[a-z0-9]([a-z0-9-]{0,58}[a-z0-9])?$'");
                table.HasCheckConstraint("ck_forms_published", "state = 'Draft' OR published_version_id IS NOT NULL");
            });
            form.HasKey(f => f.Id);
            form.Property(f => f.Id).ValueGeneratedNever();
            form.HasOne<Tenant>().WithMany().HasForeignKey(f => f.TenantId).OnDelete(DeleteBehavior.Restrict);
            form.HasOne<UserAccount>().WithMany().HasForeignKey(f => f.CreatedById).OnDelete(DeleteBehavior.SetNull);
            form.Property(f => f.Key).HasMaxLength(Form.KeyMaxLength);
            form.ConfigureLocalizedText(f => f.Name, "name", Form.NameMaxLength);
            form.ConfigureLocalizedText(f => f.Description, "description", Form.DescriptionMaxLength);
            form.Property(f => f.State).HasConversion<string>().HasMaxLength(20);
            form.Property(f => f.DraftSchema).HasColumnType("jsonb");
            form.Property(f => f.Version).IsRowVersion();
            form.HasIndex(f => new { f.TenantId, f.Key }).IsUnique();
            form.HasIndex(f => f.CreatedById);
            // NO ACTION (checked at the end of each statement): deleting a form deletes its versions with it.
            form.HasOne<FormVersion>().WithMany().HasForeignKey(f => f.PublishedVersionId).OnDelete(DeleteBehavior.NoAction);
            form.HasIndex(f => f.PublishedVersionId);
        });

        modelBuilder.Entity<FormVersion>(version =>
        {
            version.ToTable("form_versions", table => table.HasCheckConstraint("ck_form_versions_number", "number >= 1"));
            version.HasKey(v => v.Id);
            version.Property(v => v.Id).ValueGeneratedNever();
            version.HasOne<Tenant>().WithMany().HasForeignKey(v => v.TenantId).OnDelete(DeleteBehavior.Restrict);
            version.HasOne<Form>().WithMany().HasForeignKey(v => v.FormId).OnDelete(DeleteBehavior.Cascade);
            version.HasOne<UserAccount>().WithMany().HasForeignKey(v => v.PublishedById).OnDelete(DeleteBehavior.SetNull);
            version.Property(v => v.Schema).HasColumnType("jsonb");
            version.HasIndex(v => new { v.FormId, v.Number }).IsUnique();
            version.HasIndex(v => v.TenantId);
            version.HasIndex(v => v.PublishedById);
        });

        modelBuilder.Entity<FieldSet>(set =>
        {
            set.ToTable("field_sets", table =>
            {
                table.HasCheckConstraint("ck_field_sets_state", EnumCheck("state", Enum.GetNames<FormState>()));
                table.HasCheckConstraint("ck_field_sets_key", "key ~ '^[a-z0-9]([a-z0-9-]{0,58}[a-z0-9])?$'");
                table.HasCheckConstraint("ck_field_sets_published", "state = 'Draft' OR published_version_id IS NOT NULL");
            });
            set.HasKey(f => f.Id);
            set.Property(f => f.Id).ValueGeneratedNever();
            set.HasOne<Tenant>().WithMany().HasForeignKey(f => f.TenantId).OnDelete(DeleteBehavior.Restrict);
            set.HasOne<UserAccount>().WithMany().HasForeignKey(f => f.CreatedById).OnDelete(DeleteBehavior.SetNull);
            set.Property(f => f.Key).HasMaxLength(Form.KeyMaxLength);
            set.ConfigureLocalizedText(f => f.Name, "name", Form.NameMaxLength);
            set.ConfigureLocalizedText(f => f.Description, "description", Form.DescriptionMaxLength);
            set.Property(f => f.State).HasConversion<string>().HasMaxLength(20);
            set.Property(f => f.DraftSchema).HasColumnType("jsonb");
            set.Property(f => f.Version).IsRowVersion();
            set.HasIndex(f => new { f.TenantId, f.Key }).IsUnique();
            set.HasIndex(f => f.CreatedById);
            // NO ACTION (checked at the end of each statement): deleting a form deletes its versions with it.
            set.HasOne<FieldSetVersion>().WithMany().HasForeignKey(f => f.PublishedVersionId).OnDelete(DeleteBehavior.NoAction);
            set.HasIndex(f => f.PublishedVersionId);
        });

        modelBuilder.Entity<FieldSetVersion>(version =>
        {
            version.ToTable("field_set_versions", table => table.HasCheckConstraint("ck_field_set_versions_number", "number >= 1"));
            version.HasKey(v => v.Id);
            version.Property(v => v.Id).ValueGeneratedNever();
            version.HasOne<Tenant>().WithMany().HasForeignKey(v => v.TenantId).OnDelete(DeleteBehavior.Restrict);
            version.HasOne<FieldSet>().WithMany().HasForeignKey(v => v.FieldSetId).OnDelete(DeleteBehavior.Cascade);
            version.HasOne<UserAccount>().WithMany().HasForeignKey(v => v.PublishedById).OnDelete(DeleteBehavior.SetNull);
            version.Property(v => v.Schema).HasColumnType("jsonb");
            version.HasIndex(v => new { v.FieldSetId, v.Number }).IsUnique();
            version.HasIndex(v => v.TenantId);
            version.HasIndex(v => v.PublishedById);
        });

        modelBuilder.Entity<FormBinding>(binding =>
        {
            binding.ToTable("form_bindings", table =>
                table.HasCheckConstraint("ck_form_bindings_record_type", EnumCheck("record_type", Enum.GetNames<TicketType>())));
            binding.HasKey(b => b.Id);
            binding.Property(b => b.Id).ValueGeneratedNever();
            binding.HasOne<Tenant>().WithMany().HasForeignKey(b => b.TenantId).OnDelete(DeleteBehavior.Restrict);
            binding.HasOne<Form>().WithMany().HasForeignKey(b => b.FormId).OnDelete(DeleteBehavior.Cascade);
            binding.HasOne<Category>().WithMany().HasForeignKey(b => b.CategoryId).OnDelete(DeleteBehavior.Cascade);
            binding.Property(b => b.RecordType).HasConversion<string>().HasMaxLength(20);
            binding.HasIndex(b => new { b.TenantId, b.RecordType, b.CategoryId }).IsUnique().AreNullsDistinct(false);
            binding.HasIndex(b => b.FormId);
            binding.HasIndex(b => b.CategoryId);
        });

        modelBuilder.Entity<TicketFormResponse>(response =>
        {
            response.ToTable("ticket_form_responses");
            response.HasKey(r => r.TicketId);
            response.HasOne<Tenant>().WithMany().HasForeignKey(r => r.TenantId).OnDelete(DeleteBehavior.Restrict);
            response.HasOne<Ticket>().WithOne().HasForeignKey<TicketFormResponse>(r => r.TicketId).OnDelete(DeleteBehavior.Cascade);
            response.HasOne<Form>().WithMany().HasForeignKey(r => r.FormId).OnDelete(DeleteBehavior.Restrict);
            response.HasOne<FormVersion>().WithMany().HasForeignKey(r => r.FormVersionId).OnDelete(DeleteBehavior.Restrict);
            response.HasOne<UserAccount>().WithMany().HasForeignKey(r => r.SubmittedById).OnDelete(DeleteBehavior.SetNull);
            response.Property(r => r.Fields).HasColumnType("jsonb");
            response.Property(r => r.Answers).HasColumnType("jsonb");
            response.Property(r => r.PersonalAnswers).HasColumnType("jsonb");
            response.HasIndex(r => r.TenantId);
            response.HasIndex(r => r.FormId);
            response.HasIndex(r => r.FormVersionId);
            response.HasIndex(r => r.SubmittedById);
        });

        modelBuilder.Entity<Department>(department =>
        {
            department.ToTable("departments");
            department.HasKey(d => d.Id);
            department.Property(d => d.Id).ValueGeneratedNever();
            department.HasOne<Tenant>().WithMany().HasForeignKey(d => d.TenantId).OnDelete(DeleteBehavior.Restrict);
            department.ConfigureLocalizedText(d => d.Name, "name", Department.NameMaxLength);
            department.HasIndex(d => d.TenantId);
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
            ticket.HasOne<ApiClient>().WithMany().HasForeignKey(t => t.CreatedByApiClientId).OnDelete(DeleteBehavior.Restrict);
            ticket.HasIndex(t => t.CreatedByApiClientId);
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
            ticket.HasOne<Mailbox>().WithMany().HasForeignKey(t => t.MailboxId).OnDelete(DeleteBehavior.SetNull);
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
                table.HasCheckConstraint("ck_ticket_attachments_owner", "num_nonnulls(ticket_id, article_id) = 1 OR (ticket_id IS NULL AND article_id IS NULL AND form_field_key IS NOT NULL)");
                table.HasCheckConstraint("ck_ticket_attachments_form_field_key", "form_field_key ~ '^[a-z][a-zA-Z0-9]{0,59}$'");
                table.HasCheckConstraint("ck_ticket_attachments_comment", "comment_id IS NULL OR ticket_id IS NOT NULL");
            });
            attachment.HasKey(a => a.Id);
            attachment.Property(a => a.Id).ValueGeneratedNever();
            attachment.HasOne<Ticket>().WithMany().HasForeignKey(a => a.TicketId).OnDelete(DeleteBehavior.Cascade);
            attachment.HasOne<KbArticle>().WithMany().HasForeignKey(a => a.ArticleId).OnDelete(DeleteBehavior.Cascade);
            attachment.Property(a => a.FormFieldKey).HasMaxLength(TicketAttachment.FormFieldKeyMaxLength);
            attachment.HasIndex(a => new { a.TenantId, a.UploadedAt }).HasFilter("ticket_id IS NULL AND article_id IS NULL").HasDatabaseName("ix_ticket_attachments_form_uploads");
            // Comments are never removed, so the link never needs to change.
            attachment.HasOne<TicketComment>().WithMany().HasForeignKey(a => a.CommentId).OnDelete(DeleteBehavior.NoAction);
            attachment.HasIndex(a => a.CommentId).HasFilter("comment_id IS NOT NULL");
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

    private static void ConfigureWorkspace(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<TicketComment>(comment =>
        {
            comment.ToTable("ticket_comments", table =>
            {
                table.HasCheckConstraint("ck_ticket_comments_visibility", EnumCheck("visibility", Enum.GetNames<CommentVisibility>()));
                table.HasCheckConstraint("ck_ticket_comments_language", "language IN ('en', 'ar')");
                // Requesters write public replies only.
                table.HasCheckConstraint("ck_ticket_comments_internal_by_staff", "visibility = 'Public' OR by_staff");
                // Task E2: email and chat messages are public; only Zelvo writes them.
                table.HasCheckConstraint("ck_ticket_comments_source", EnumCheck("source", Enum.GetNames<CommentSource>()));
                table.HasCheckConstraint("ck_ticket_comments_channel_public", "source = 'Web' OR visibility = 'Public'");
            });
            comment.HasKey(c => c.Id);
            comment.Property(c => c.Id).ValueGeneratedNever();
            comment.HasOne<Ticket>().WithMany().HasForeignKey(c => c.TicketId).OnDelete(DeleteBehavior.Cascade);
            comment.HasOne<UserAccount>().WithMany().HasForeignKey(c => c.AuthorId).OnDelete(DeleteBehavior.Restrict);
            comment.HasOne<CannedResponse>().WithMany().HasForeignKey(c => c.CannedResponseId).OnDelete(DeleteBehavior.SetNull);
            comment.Property(c => c.Visibility).HasConversion<string>().HasMaxLength(20);
            comment.Property(c => c.Body).HasMaxLength(TicketComment.BodyMaxLength);
            comment.Property(c => c.Language).HasMaxLength(2);
            comment.HasIndex(c => new { c.TenantId, c.TicketId, c.CreatedAt });
            comment.Property(c => c.Source).HasConversion<string>().HasMaxLength(20).HasDefaultValue(CommentSource.Web).HasSentinel(CommentSource.Web);
            comment.Property(c => c.EmailMessageId).HasMaxLength(TicketComment.MessageIdMaxLength);
            comment.Property(c => c.EmailInReplyTo).HasMaxLength(TicketComment.MessageIdMaxLength);
            // Threading (task E2): replies name earlier Message-IDs in In-Reply-To and References.
            comment.HasIndex(c => new { c.TenantId, c.EmailMessageId }).HasFilter("email_message_id IS NOT NULL");
        });

        modelBuilder.Entity<TicketCommentMention>(mention =>
        {
            mention.ToTable("ticket_comment_mentions");
            mention.HasKey(m => new { m.CommentId, m.UserId });
            mention.HasOne<TicketComment>().WithMany().HasForeignKey(m => m.CommentId).OnDelete(DeleteBehavior.Cascade);
            mention.HasOne<Ticket>().WithMany().HasForeignKey(m => m.TicketId).OnDelete(DeleteBehavior.Cascade);
            mention.HasOne<UserAccount>().WithMany().HasForeignKey(m => m.UserId).OnDelete(DeleteBehavior.Cascade);
            mention.HasIndex(m => new { m.TenantId, m.UserId });
            mention.HasIndex(m => m.TicketId);
        });

        modelBuilder.Entity<CannedResponse>(response =>
        {
            response.ToTable("canned_responses", table =>
            {
                table.HasCheckConstraint("ck_canned_responses_scope", EnumCheck("scope", Enum.GetNames<WorkspaceScope>()));
                table.HasCheckConstraint("ck_canned_responses_owner",
                    "(scope = 'Tenant' AND group_id IS NULL AND owner_id IS NULL) OR (scope = 'Group' AND group_id IS NOT NULL AND owner_id IS NULL) OR (scope = 'Personal' AND group_id IS NULL AND owner_id IS NOT NULL)");
            });
            response.HasKey(r => r.Id);
            response.Property(r => r.Id).ValueGeneratedNever();
            response.HasOne<Tenant>().WithMany().HasForeignKey(r => r.TenantId).OnDelete(DeleteBehavior.Restrict);
            response.HasOne<SupportGroup>().WithMany().HasForeignKey(r => r.GroupId).OnDelete(DeleteBehavior.Cascade);
            response.HasOne<UserAccount>().WithMany().HasForeignKey(r => r.OwnerId).OnDelete(DeleteBehavior.Cascade);
            response.HasOne<UserAccount>().WithMany().HasForeignKey(r => r.CreatedById).OnDelete(DeleteBehavior.Restrict);
            response.Property(r => r.Scope).HasConversion<string>().HasMaxLength(20);
            response.ConfigureLocalizedText(r => r.Title, "title", CannedResponse.TitleMaxLength);
            response.ConfigureLocalizedText(r => r.Body, "body", CannedResponse.BodyMaxLength);
            response.Property(r => r.Version).IsRowVersion();
            response.HasIndex(r => new { r.TenantId, r.Scope });
            response.HasIndex(r => r.GroupId);
            response.HasIndex(r => r.OwnerId);
        });

        modelBuilder.Entity<SavedView>(view =>
        {
            view.ToTable("saved_views", table =>
            {
                table.HasCheckConstraint("ck_saved_views_scope", EnumCheck("scope", Enum.GetNames<WorkspaceScope>()));
                table.HasCheckConstraint("ck_saved_views_group", "(scope = 'Group') = (group_id IS NOT NULL)");
            });
            view.HasKey(v => v.Id);
            view.Property(v => v.Id).ValueGeneratedNever();
            view.HasOne<Tenant>().WithMany().HasForeignKey(v => v.TenantId).OnDelete(DeleteBehavior.Restrict);
            view.HasOne<SupportGroup>().WithMany().HasForeignKey(v => v.GroupId).OnDelete(DeleteBehavior.Cascade);
            view.HasOne<UserAccount>().WithMany().HasForeignKey(v => v.OwnerId).OnDelete(DeleteBehavior.Cascade);
            view.Property(v => v.Scope).HasConversion<string>().HasMaxLength(20);
            view.ConfigureLocalizedText(v => v.Name, "name", SavedView.NameMaxLength);
            view.Property(v => v.Filters).HasColumnType("jsonb");
            view.HasIndex(v => new { v.TenantId, v.Scope });
            view.HasIndex(v => v.OwnerId);
            view.HasIndex(v => v.GroupId);
        });
    }

    private static void ConfigureSla(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<BusinessCalendar>(calendar =>
        {
            calendar.ToTable("business_calendars");
            calendar.HasKey(c => c.Id);
            calendar.Property(c => c.Id).ValueGeneratedNever();
            calendar.HasOne<Tenant>().WithMany().HasForeignKey(c => c.TenantId).OnDelete(DeleteBehavior.Restrict);
            calendar.ConfigureLocalizedText(c => c.Name, "name", BusinessCalendar.NameMaxLength);
            calendar.Property(c => c.TimeZone).HasMaxLength(BusinessCalendar.TimeZoneMaxLength);
            calendar.Property(c => c.Version).IsRowVersion();
            calendar.HasMany(c => c.Periods).WithOne().HasForeignKey(p => p.CalendarId).OnDelete(DeleteBehavior.Cascade);
            calendar.HasMany(c => c.Holidays).WithOne().HasForeignKey(h => h.CalendarId).OnDelete(DeleteBehavior.Cascade);
            calendar.HasIndex(c => c.TenantId);
            // At most one default per tenant.
            calendar.HasIndex(c => c.TenantId, "ux_business_calendars_default").IsUnique().HasFilter("is_default");
        });

        modelBuilder.Entity<BusinessCalendarPeriod>(period =>
        {
            period.ToTable("business_calendar_periods", table =>
            {
                table.HasCheckConstraint("ck_business_calendar_periods_day", "day BETWEEN 0 AND 6");
                table.HasCheckConstraint("ck_business_calendar_periods_minutes",
                    $"start_minute >= 0 AND end_minute <= {BusinessCalendarPeriod.MinutesPerDay} AND start_minute < end_minute");
            });
            period.HasKey(p => p.Id);
            period.Property(p => p.Id).ValueGeneratedNever();
            period.HasIndex(p => new { p.TenantId, p.CalendarId });
        });

        modelBuilder.Entity<BusinessCalendarHoliday>(holiday =>
        {
            holiday.ToTable("business_calendar_holidays", table =>
                table.HasCheckConstraint("ck_business_calendar_holidays_dates", "start_date <= end_date"));
            holiday.HasKey(h => h.Id);
            holiday.Property(h => h.Id).ValueGeneratedNever();
            holiday.ConfigureLocalizedText(h => h.Name, "name", BusinessCalendarHoliday.NameMaxLength);
            holiday.HasIndex(h => new { h.TenantId, h.CalendarId, h.StartDate });
        });

        modelBuilder.Entity<CustomerGroup>(group =>
        {
            group.ToTable("customer_groups");
            group.HasKey(g => g.Id);
            group.Property(g => g.Id).ValueGeneratedNever();
            group.HasOne<Tenant>().WithMany().HasForeignKey(g => g.TenantId).OnDelete(DeleteBehavior.Restrict);
            group.ConfigureLocalizedText(g => g.Name, "name", CustomerGroup.NameMaxLength);
            group.Property(g => g.EmailDomains).HasColumnType("text[]").HasDefaultValueSql("'{}'");
            group.Property(g => g.Version).IsRowVersion();
            group.HasMany(g => g.Members).WithOne().HasForeignKey(m => m.GroupId).OnDelete(DeleteBehavior.Cascade);
            group.HasIndex(g => g.TenantId);
        });

        modelBuilder.Entity<CustomerGroupMember>(member =>
        {
            member.ToTable("customer_group_members");
            member.HasKey(m => new { m.GroupId, m.UserId });
            member.HasOne(m => m.User).WithMany().HasForeignKey(m => m.UserId).OnDelete(DeleteBehavior.Cascade);
            member.HasIndex(m => new { m.TenantId, m.UserId });
        });

        modelBuilder.Entity<SlaPolicy>(policy =>
        {
            policy.ToTable("sla_policies", table =>
                table.HasCheckConstraint("ck_sla_policies_record_type", EnumCheck("record_type", Enum.GetNames<TicketType>())));
            policy.HasKey(p => p.Id);
            policy.Property(p => p.Id).ValueGeneratedNever();
            policy.HasOne<Tenant>().WithMany().HasForeignKey(p => p.TenantId).OnDelete(DeleteBehavior.Restrict);
            policy.ConfigureLocalizedText(p => p.Name, "name", SlaPolicy.NameMaxLength);
            policy.Property(p => p.RecordType).HasConversion<string>().HasMaxLength(30);
            policy.Property(p => p.Version).IsRowVersion();
            policy.HasOne<Category>().WithMany().HasForeignKey(p => p.CategoryId).OnDelete(DeleteBehavior.Restrict);
            policy.HasOne<CustomerGroup>().WithMany().HasForeignKey(p => p.CustomerGroupId).OnDelete(DeleteBehavior.Restrict);
            policy.HasOne<BusinessCalendar>().WithMany().HasForeignKey(p => p.CalendarId).OnDelete(DeleteBehavior.Restrict);
            policy.HasMany(p => p.Targets).WithOne().HasForeignKey(t => t.PolicyId).OnDelete(DeleteBehavior.Cascade);
            policy.HasMany(p => p.Escalations).WithOne().HasForeignKey(e => e.PolicyId).OnDelete(DeleteBehavior.Cascade);
            policy.HasIndex(p => new { p.TenantId, p.Position });
        });

        modelBuilder.Entity<SlaPolicyTarget>(target =>
        {
            target.ToTable("sla_policy_targets", table =>
            {
                table.HasCheckConstraint("ck_sla_policy_targets_priority", "priority BETWEEN 1 AND 9");
                table.HasCheckConstraint("ck_sla_policy_targets_minutes",
                    $"(response_minutes IS NULL OR response_minutes BETWEEN 1 AND {SlaPolicyTarget.MaxMinutes})"
                    + $" AND (resolution_minutes IS NULL OR resolution_minutes BETWEEN 1 AND {SlaPolicyTarget.MaxMinutes})");
            });
            target.HasKey(t => new { t.PolicyId, t.Priority });
            target.HasIndex(t => t.TenantId);
        });

        modelBuilder.Entity<SlaEscalation>(escalation =>
        {
            escalation.ToTable("sla_escalations", table =>
            {
                table.HasCheckConstraint("ck_sla_escalations_metric", EnumCheck("metric", Enum.GetNames<SlaMetric>()));
                table.HasCheckConstraint("ck_sla_escalations_percent", $"percent BETWEEN 1 AND {SlaEscalation.MaxPercent}");
            });
            escalation.HasKey(e => e.Id);
            escalation.Property(e => e.Id).ValueGeneratedNever();
            escalation.Property(e => e.Metric).HasConversion<string>().HasMaxLength(20);
            escalation.HasOne<SupportGroup>().WithMany().HasForeignKey(e => e.NotifyGroupId).OnDelete(DeleteBehavior.Restrict);
            escalation.HasIndex(e => new { e.TenantId, e.PolicyId });
        });

        modelBuilder.Entity<TicketSla>(sla =>
        {
            sla.ToTable("ticket_slas", table =>
            {
                table.HasCheckConstraint("ck_ticket_slas_metric", EnumCheck("metric", Enum.GetNames<SlaMetric>()));
                table.HasCheckConstraint("ck_ticket_slas_state", EnumCheck("state", Enum.GetNames<SlaClockState>()));
                table.HasCheckConstraint("ck_ticket_slas_elapsed", "elapsed_seconds >= 0");
            });
            sla.HasKey(s => s.Id);
            sla.Property(s => s.Id).ValueGeneratedNever();
            sla.Property(s => s.Metric).HasConversion<string>().HasMaxLength(20);
            sla.Property(s => s.State).HasConversion<string>().HasMaxLength(20);
            sla.HasOne<Ticket>().WithMany().HasForeignKey(s => s.TicketId).OnDelete(DeleteBehavior.Cascade);
            // A deleted policy or calendar leaves the clock without one; the next change of the ticket finds another.
            sla.HasOne<SlaPolicy>().WithMany().HasForeignKey(s => s.PolicyId).OnDelete(DeleteBehavior.SetNull);
            sla.HasOne<BusinessCalendar>().WithMany().HasForeignKey(s => s.CalendarId).OnDelete(DeleteBehavior.SetNull);
            sla.HasIndex(s => new { s.TicketId, s.Metric }).IsUnique();
            sla.HasIndex(s => new { s.TenantId, s.TicketId });
            // The timer job looks for running clocks that are due a look.
            sla.HasIndex(s => new { s.TenantId, s.NextCheckAt }, "ix_ticket_slas_due").HasFilter("next_check_at IS NOT NULL");
        });

        modelBuilder.Entity<SlaAlert>(alert =>
        {
            alert.ToTable("sla_alerts", table =>
            {
                table.HasCheckConstraint("ck_sla_alerts_metric", EnumCheck("metric", Enum.GetNames<SlaMetric>()));
                table.HasCheckConstraint("ck_sla_alerts_percent", $"percent BETWEEN 1 AND {SlaEscalation.MaxPercent}");
            });
            alert.HasKey(a => a.Id);
            alert.Property(a => a.Id).ValueGeneratedNever();
            alert.Property(a => a.Metric).HasConversion<string>().HasMaxLength(20);
            alert.Property(a => a.RecipientIds).HasColumnType("uuid[]").HasDefaultValueSql("'{}'");
            alert.HasOne<Ticket>().WithMany().HasForeignKey(a => a.TicketId).OnDelete(DeleteBehavior.Cascade);
            alert.HasOne<TicketSla>().WithMany().HasForeignKey(a => a.TicketSlaId).OnDelete(DeleteBehavior.Cascade);
            // Each threshold once per clock.
            alert.HasIndex(a => new { a.TicketSlaId, a.Percent }).IsUnique();
            alert.HasIndex(a => new { a.TenantId, a.TicketId });
            // Notifications (B5) pick up alerts not delivered yet.
            alert.HasIndex(a => new { a.TenantId, a.ReachedAt }, "ix_sla_alerts_undelivered").HasFilter("delivered_at IS NULL");
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

    private static void ConfigureNotifications(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Notification>(notification =>
        {
            notification.ToTable("notifications", table =>
            {
                table.HasCheckConstraint("ck_notifications_status", EnumCheck("status", Enum.GetNames<NotificationStatus>()));
                table.HasCheckConstraint("ck_notifications_language", "language IS NULL OR language IN ('en', 'ar')");
                table.HasCheckConstraint("ck_notifications_link_path", "link_path IS NULL OR link_path ~ '^/[A-Za-z0-9/_?=&.-]*$'");
            });
            notification.HasKey(n => n.Id);
            notification.Property(n => n.Id).ValueGeneratedNever();
            notification.HasOne<Tenant>().WithMany().HasForeignKey(n => n.TenantId).OnDelete(DeleteBehavior.Restrict);
            notification.HasOne<UserAccount>().WithMany().HasForeignKey(n => n.RecipientId).OnDelete(DeleteBehavior.Cascade);
            notification.HasOne<Ticket>().WithMany().HasForeignKey(n => n.TicketId).OnDelete(DeleteBehavior.Cascade);
            notification.HasOne<KbArticle>().WithMany().HasForeignKey(n => n.ArticleId).OnDelete(DeleteBehavior.Cascade);
            notification.HasOne<SlaAlert>().WithMany().HasForeignKey(n => n.SlaAlertId).OnDelete(DeleteBehavior.Cascade);
            notification.Property(n => n.EventType).HasMaxLength(Notification.EventTypeMaxLength);
            notification.Property(n => n.Data).HasColumnType("jsonb");
            notification.Property(n => n.LinkPath).HasMaxLength(Notification.LinkPathMaxLength);
            notification.Property(n => n.Status).HasConversion<string>().HasMaxLength(20);
            notification.Property(n => n.Language).HasMaxLength(2);
            notification.Property(n => n.Title).HasMaxLength(Notification.TitleMaxLength);
            notification.Property(n => n.Text).HasMaxLength(Notification.TextMaxLength);

            // The worker looks for pending ones; people list theirs newest first and count the unread.
            notification.HasIndex(n => new { n.TenantId, n.CreatedAt }).HasFilter("status = 'Pending'").HasDatabaseName("ix_notifications_pending");
            notification.HasIndex(n => new { n.RecipientId, n.Id }).HasFilter("status = 'Delivered'");
            notification.HasIndex(n => n.RecipientId).HasFilter("status = 'Delivered' AND read_at IS NULL").HasDatabaseName("ix_notifications_unread");
            notification.HasIndex(n => n.TicketId);
            notification.HasIndex(n => n.ArticleId);
            notification.HasIndex(n => n.SlaAlertId);
            // Retention removes old ones in every tenant.
            notification.HasIndex(n => n.CreatedAt);
        });

        modelBuilder.Entity<NotificationDelivery>(delivery =>
        {
            delivery.ToTable("notification_deliveries", table =>
            {
                table.HasCheckConstraint("ck_notification_deliveries_status", EnumCheck("status", Enum.GetNames<NotificationDeliveryStatus>()));
                table.HasCheckConstraint("ck_notification_deliveries_language", "language IN ('en', 'ar')");
            });
            delivery.HasKey(d => d.Id);
            delivery.Property(d => d.Id).UseIdentityAlwaysColumn();
            delivery.HasOne<Tenant>().WithMany().HasForeignKey(d => d.TenantId).OnDelete(DeleteBehavior.Restrict);
            delivery.HasOne<Notification>().WithMany().HasForeignKey(d => d.NotificationId).OnDelete(DeleteBehavior.SetNull);
            delivery.HasOne<UserAccount>().WithMany().HasForeignKey(d => d.RecipientId).OnDelete(DeleteBehavior.SetNull);
            delivery.Property(d => d.EventType).HasMaxLength(Notification.EventTypeMaxLength);
            delivery.Property(d => d.Address).HasMaxLength(UserAccount.EmailMaxLength);
            delivery.Property(d => d.Language).HasMaxLength(2);
            delivery.Property(d => d.Subject).HasMaxLength(NotificationDelivery.SubjectMaxLength);
            delivery.Property(d => d.HtmlBody).HasMaxLength(NotificationDelivery.BodyMaxLength);
            delivery.Property(d => d.TextBody).HasMaxLength(NotificationDelivery.BodyMaxLength);
            delivery.Property(d => d.Status).HasConversion<string>().HasMaxLength(20);
            delivery.Property(d => d.LastError).HasMaxLength(NotificationDelivery.ErrorMaxLength);

            delivery.HasIndex(d => new { d.TenantId, d.NextAttemptAt }).HasFilter("status = 'Pending'");
            delivery.HasIndex(d => new { d.TenantId, d.Id });
            delivery.HasIndex(d => new { d.TenantId, d.CreatedAt });
            delivery.HasIndex(d => d.NotificationId);
            delivery.HasIndex(d => d.RecipientId);
        });

        modelBuilder.Entity<NotificationTemplate>(template =>
        {
            template.ToTable("notification_templates");
            template.HasKey(t => new { t.TenantId, t.EventType });
            template.HasOne<Tenant>().WithMany().HasForeignKey(t => t.TenantId).OnDelete(DeleteBehavior.Restrict);
            template.Property(t => t.EventType).HasMaxLength(Notification.EventTypeMaxLength);
            template.ConfigureLocalizedText(t => t.Subject, "subject", NotificationTemplate.SubjectMaxLength);
            template.ConfigureLocalizedText(t => t.Body, "body", NotificationTemplate.BodyMaxLength);
            template.ConfigureLocalizedText(t => t.InApp, "in_app", NotificationTemplate.InAppMaxLength);
            template.ConfigureLocalizedText(t => t.Action, "action", NotificationTemplate.ActionMaxLength);
            template.Property(t => t.Version).IsRowVersion();
        });

        modelBuilder.Entity<NotificationPreference>(preference =>
        {
            preference.ToTable("notification_preferences");
            preference.HasKey(p => new { p.UserId, p.EventType });
            preference.HasOne<Tenant>().WithMany().HasForeignKey(p => p.TenantId).OnDelete(DeleteBehavior.Restrict);
            preference.HasOne<UserAccount>().WithMany().HasForeignKey(p => p.UserId).OnDelete(DeleteBehavior.Cascade);
            preference.Property(p => p.EventType).HasMaxLength(Notification.EventTypeMaxLength);
            preference.HasIndex(p => p.TenantId);
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

    private static void ConfigureMcp(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<McpAgent>(agent =>
        {
            agent.ToTable("mcp_agents");
            agent.HasKey(a => a.ApiClientId);
            agent.Property(a => a.ApiClientId).ValueGeneratedNever();
            agent.HasOne<Tenant>().WithMany().HasForeignKey(a => a.TenantId).OnDelete(DeleteBehavior.Restrict);
            agent.HasOne<ApiClient>().WithOne().HasForeignKey<McpAgent>(a => a.ApiClientId).OnDelete(DeleteBehavior.Restrict);
            agent.Property(a => a.Version).IsRowVersion();
            agent.HasIndex(a => a.TenantId);
        });

        modelBuilder.Entity<McpAction>(action =>
        {
            action.ToTable("mcp_actions", table =>
            {
                table.HasCheckConstraint("ck_mcp_actions_status", EnumCheck("status", Enum.GetNames<McpActionStatus>()));
                table.HasCheckConstraint("ck_mcp_actions_tool", "tool ~ '^[a-z][a-z0-9_]*$'");
                table.HasCheckConstraint("ck_mcp_actions_expiry", "NOT approval_required OR expires_at IS NOT NULL");
            });
            action.HasKey(a => a.Id);
            action.Property(a => a.Id).ValueGeneratedNever();
            action.HasOne<Tenant>().WithMany().HasForeignKey(a => a.TenantId).OnDelete(DeleteBehavior.Restrict);
            action.HasOne<ApiClient>().WithMany().HasForeignKey(a => a.ApiClientId).OnDelete(DeleteBehavior.Restrict);
            action.HasOne<UserAccount>().WithMany().HasForeignKey(a => a.DecidedById).OnDelete(DeleteBehavior.Restrict);
            action.HasOne<Ticket>().WithMany().HasForeignKey(a => a.TicketId).OnDelete(DeleteBehavior.Restrict);
            action.Property(a => a.Tool).HasMaxLength(McpAction.ToolMaxLength);
            action.Property(a => a.Permission).HasMaxLength(100);
            action.Property(a => a.Arguments).HasColumnType("jsonb");
            action.Property(a => a.Result).HasColumnType("jsonb");
            action.Property(a => a.Summary).HasMaxLength(McpAction.SummaryMaxLength);
            action.Property(a => a.Status).HasConversion<string>().HasMaxLength(20);
            action.Property(a => a.DecisionNote).HasMaxLength(McpAction.NoteMaxLength);
            action.Property(a => a.ErrorCode).HasMaxLength(McpAction.ErrorCodeMaxLength);
            action.Property(a => a.Error).HasMaxLength(McpAction.ErrorMaxLength);
            action.Property(a => a.Version).IsRowVersion();
            action.HasIndex(a => new { a.TenantId, a.Status, a.RequestedAt });
            action.HasIndex(a => new { a.ApiClientId, a.RequestedAt });
            action.HasIndex(a => a.DecidedById);
            action.HasIndex(a => a.TicketId);
        });

        modelBuilder.Entity<McpServerConnection>(server =>
        {
            server.ToTable("mcp_servers", table =>
            {
                table.HasCheckConstraint("ck_mcp_servers_status", EnumCheck("status", Enum.GetNames<McpServerStatus>()));
                table.HasCheckConstraint("ck_mcp_servers_url", "url ~ '^https?://'");
                table.HasCheckConstraint("ck_mcp_servers_auth_header", "auth_header IS NULL OR auth_header ~ '^[A-Za-z][A-Za-z0-9-]*$'");
            });
            server.HasKey(s => s.Id);
            server.Property(s => s.Id).ValueGeneratedNever();
            server.HasOne<Tenant>().WithMany().HasForeignKey(s => s.TenantId).OnDelete(DeleteBehavior.Restrict);
            server.Property(s => s.Name).HasMaxLength(McpServerConnection.NameMaxLength);
            server.Property(s => s.Url).HasMaxLength(McpServerConnection.UrlMaxLength);
            server.Property(s => s.AuthHeader).HasMaxLength(McpServerConnection.HeaderMaxLength);
            server.Property(s => s.Status).HasConversion<string>().HasMaxLength(20);
            server.Property(s => s.Tools).HasColumnType("jsonb");
            server.Property(s => s.LastTestError).HasMaxLength(McpServerConnection.ErrorMaxLength);
            server.Property(s => s.Version).IsRowVersion();
            server.HasIndex(s => new { s.TenantId, s.Status });
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
                table.HasCheckConstraint("ck_users_phone", "phone ~ '^\\+[1-9][0-9]{6,14}$'");
                table.HasCheckConstraint("ck_users_not_own_manager", "manager_id IS NULL OR manager_id <> id");
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
            // Task C1: profile fields that forms fill in.
            user.Property(u => u.EmployeeId).HasMaxLength(UserAccount.EmployeeIdMaxLength);
            user.Property(u => u.Phone).HasMaxLength(UserAccount.PhoneMaxLength);
            user.HasOne<Department>().WithMany().HasForeignKey(u => u.DepartmentId).OnDelete(DeleteBehavior.Restrict);
            user.HasOne<UserAccount>().WithMany().HasForeignKey(u => u.ManagerId).OnDelete(DeleteBehavior.SetNull);
            user.HasOne<Location>().WithMany().HasForeignKey(u => u.LocationId).OnDelete(DeleteBehavior.Restrict);
            user.HasIndex(u => u.DepartmentId);
            user.HasIndex(u => u.ManagerId);
            user.HasIndex(u => u.LocationId);
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

    private static void ConfigureChannels(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Mailbox>(mailbox =>
        {
            mailbox.ToTable("mailboxes", table =>
            {
                table.HasCheckConstraint("ck_mailboxes_provider", EnumCheck("provider", Enum.GetNames<MailboxProvider>()));
                table.HasCheckConstraint("ck_mailboxes_auth", EnumCheck("auth", Enum.GetNames<MailboxAuth>()));
                table.HasCheckConstraint("ck_mailboxes_imap_security", EnumCheck("imap_security", Enum.GetNames<MailSecurity>()));
                table.HasCheckConstraint("ck_mailboxes_smtp_security", EnumCheck("smtp_security", Enum.GetNames<MailSecurity>()));
                table.HasCheckConstraint("ck_mailboxes_new_senders", EnumCheck("new_senders", Enum.GetNames<NewSenderPolicy>()));
                table.HasCheckConstraint("ck_mailboxes_status", EnumCheck("status", Enum.GetNames<MailboxStatus>()));
                table.HasCheckConstraint("ck_mailboxes_ports", "(imap_port IS NULL OR imap_port BETWEEN 1 AND 65535) AND (smtp_port IS NULL OR smtp_port BETWEEN 1 AND 65535)");
            });
            mailbox.HasKey(m => m.Id);
            mailbox.Property(m => m.Id).ValueGeneratedNever();
            mailbox.HasOne<Tenant>().WithMany().HasForeignKey(m => m.TenantId).OnDelete(DeleteBehavior.Restrict);
            mailbox.HasOne<Category>().WithMany().HasForeignKey(m => m.CategoryId).OnDelete(DeleteBehavior.Restrict);
            mailbox.HasOne<SupportGroup>().WithMany().HasForeignKey(m => m.GroupId).OnDelete(DeleteBehavior.Restrict);
            mailbox.Property(m => m.Name).HasMaxLength(Mailbox.NameMaxLength);
            mailbox.Property(m => m.Address).HasMaxLength(Mailbox.AddressMaxLength);
            mailbox.Property(m => m.Provider).HasConversion<string>().HasMaxLength(20);
            mailbox.Property(m => m.Auth).HasConversion<string>().HasMaxLength(20);
            mailbox.Property(m => m.ImapSecurity).HasConversion<string>().HasMaxLength(20);
            mailbox.Property(m => m.SmtpSecurity).HasConversion<string>().HasMaxLength(20);
            mailbox.Property(m => m.NewSenders).HasConversion<string>().HasMaxLength(20);
            mailbox.Property(m => m.Status).HasConversion<string>().HasMaxLength(20);
            mailbox.Property(m => m.ImapHost).HasMaxLength(Mailbox.HostMaxLength);
            mailbox.Property(m => m.SmtpHost).HasMaxLength(Mailbox.HostMaxLength);
            mailbox.Property(m => m.Username).HasMaxLength(Mailbox.IdentifierMaxLength);
            mailbox.Property(m => m.ClientId).HasMaxLength(Mailbox.IdentifierMaxLength);
            mailbox.Property(m => m.DirectoryId).HasMaxLength(Mailbox.IdentifierMaxLength);
            mailbox.Property(m => m.SignedInAs).HasMaxLength(Mailbox.AddressMaxLength);
            mailbox.Property(m => m.SyncState).HasColumnType("jsonb");
            mailbox.Property(m => m.LastError).HasMaxLength(Mailbox.ErrorMaxLength);
            mailbox.Property(m => m.Version).IsRowVersion();
            // One mailbox per address in a tenant: threading and loop checks rely on it.
            mailbox.HasIndex(m => new { m.TenantId, m.Address }).IsUnique();
            mailbox.HasIndex(m => new { m.Status, m.NextCheckAt });
        });

        modelBuilder.Entity<InboundEmail>(email =>
        {
            email.ToTable("inbound_emails", table =>
                table.HasCheckConstraint("ck_inbound_emails_outcome", EnumCheck("outcome", Enum.GetNames<InboundEmailOutcome>())));
            email.HasKey(e => e.Id);
            email.Property(e => e.Id).UseIdentityAlwaysColumn();
            email.HasOne<Tenant>().WithMany().HasForeignKey(e => e.TenantId).OnDelete(DeleteBehavior.Restrict);
            email.HasOne<Mailbox>().WithMany().HasForeignKey(e => e.MailboxId).OnDelete(DeleteBehavior.Cascade);
            email.HasOne<Ticket>().WithMany().HasForeignKey(e => e.TicketId).OnDelete(DeleteBehavior.SetNull);
            email.HasOne<TicketComment>().WithMany().HasForeignKey(e => e.CommentId).OnDelete(DeleteBehavior.NoAction);
            email.Property(e => e.ProviderMessageId).HasMaxLength(InboundEmail.ProviderIdMaxLength);
            email.Property(e => e.MessageId).HasMaxLength(TicketComment.MessageIdMaxLength);
            email.Property(e => e.FromAddress).HasMaxLength(Mailbox.AddressMaxLength);
            email.Property(e => e.Subject).HasMaxLength(InboundEmail.SubjectMaxLength);
            email.Property(e => e.Outcome).HasConversion<string>().HasMaxLength(20);
            email.Property(e => e.Reason).HasMaxLength(InboundEmail.ReasonMaxLength);
            // Each email is read once.
            email.HasIndex(e => new { e.MailboxId, e.ProviderMessageId }).IsUnique();
            email.HasIndex(e => new { e.TenantId, e.MailboxId, e.ReceivedAt });
            email.HasIndex(e => new { e.TenantId, e.MessageId }).HasFilter("message_id IS NOT NULL");
        });

        modelBuilder.Entity<OutboundEmail>(email =>
        {
            email.ToTable("outbound_emails", table =>
                table.HasCheckConstraint("ck_outbound_emails_status", EnumCheck("status", Enum.GetNames<OutboundEmailStatus>())));
            email.HasKey(e => e.Id);
            email.Property(e => e.Id).UseIdentityAlwaysColumn();
            email.HasOne<Tenant>().WithMany().HasForeignKey(e => e.TenantId).OnDelete(DeleteBehavior.Restrict);
            email.HasOne<Mailbox>().WithMany().HasForeignKey(e => e.MailboxId).OnDelete(DeleteBehavior.Cascade);
            email.HasOne<Ticket>().WithMany().HasForeignKey(e => e.TicketId).OnDelete(DeleteBehavior.Cascade);
            email.HasOne<TicketComment>().WithMany().HasForeignKey(e => e.CommentId).OnDelete(DeleteBehavior.NoAction);
            email.HasOne<Notification>().WithMany().HasForeignKey(e => e.NotificationId).OnDelete(DeleteBehavior.SetNull);
            email.HasIndex(e => e.NotificationId).IsUnique().HasFilter("notification_id IS NOT NULL");
            email.Property(e => e.ToAddress).HasMaxLength(Mailbox.AddressMaxLength);
            email.Property(e => e.Subject).HasMaxLength(OutboundEmail.SubjectMaxLength);
            email.Property(e => e.MessageId).HasMaxLength(TicketComment.MessageIdMaxLength);
            email.Property(e => e.InReplyTo).HasMaxLength(TicketComment.MessageIdMaxLength);
            email.Property(e => e.References).HasMaxLength(OutboundEmail.ReferencesMaxLength);
            email.Property(e => e.Language).HasMaxLength(5);
            email.Property(e => e.Status).HasConversion<string>().HasMaxLength(20);
            email.Property(e => e.LastError).HasMaxLength(Mailbox.ErrorMaxLength);
            email.HasIndex(e => new { e.TenantId, e.Status, e.NextAttemptAt });
            email.HasIndex(e => new { e.TenantId, e.MailboxId, e.CreatedAt });
        });

        modelBuilder.Entity<ChatWidget>(widget =>
        {
            widget.ToTable("chat_widgets");
            widget.HasKey(w => w.Id);
            widget.Property(w => w.Id).ValueGeneratedNever();
            widget.HasOne<Tenant>().WithMany().HasForeignKey(w => w.TenantId).OnDelete(DeleteBehavior.Restrict);
            widget.HasOne<SupportGroup>().WithMany().HasForeignKey(w => w.GroupId).OnDelete(DeleteBehavior.Restrict);
            widget.HasOne<Category>().WithMany().HasForeignKey(w => w.CategoryId).OnDelete(DeleteBehavior.Restrict);
            widget.ConfigureLocalizedText(w => w.Name, "name", ChatWidget.NameMaxLength);
            widget.ConfigureLocalizedText(w => w.Greeting, "greeting", ChatWidget.GreetingMaxLength);
            widget.Property(w => w.Version).IsRowVersion();
            widget.HasIndex(w => w.TenantId);
        });

        modelBuilder.Entity<ChatSession>(session =>
        {
            session.ToTable("chat_sessions", table =>
            {
                table.HasCheckConstraint("ck_chat_sessions_state", EnumCheck("state", Enum.GetNames<ChatState>()));
                table.HasCheckConstraint("ck_chat_sessions_ended_by", $"ended_by IS NULL OR {EnumCheck("ended_by", Enum.GetNames<ChatEndedBy>())}");
                table.HasCheckConstraint("ck_chat_sessions_language", "language IN ('en', 'ar')");
                table.HasCheckConstraint("ck_chat_sessions_agent", "state <> 'Active' OR agent_id IS NOT NULL");
            });
            session.HasKey(c => c.Id);
            session.Property(c => c.Id).ValueGeneratedNever();
            session.HasOne<Tenant>().WithMany().HasForeignKey(c => c.TenantId).OnDelete(DeleteBehavior.Restrict);
            session.HasOne<ChatWidget>().WithMany().HasForeignKey(c => c.WidgetId).OnDelete(DeleteBehavior.Restrict);
            session.HasOne<SupportGroup>().WithMany().HasForeignKey(c => c.GroupId).OnDelete(DeleteBehavior.Restrict);
            session.HasOne<UserAccount>().WithMany().HasForeignKey(c => c.VisitorUserId).OnDelete(DeleteBehavior.Restrict);
            session.HasOne(c => c.Agent).WithMany().HasForeignKey(c => c.AgentId).OnDelete(DeleteBehavior.Restrict);
            session.HasOne<Ticket>().WithMany().HasForeignKey(c => c.TicketId).OnDelete(DeleteBehavior.SetNull);
            session.Property(c => c.VisitorName).HasMaxLength(ChatSession.NameMaxLength);
            session.Property(c => c.VisitorEmail).HasMaxLength(Mailbox.AddressMaxLength);
            session.Property(c => c.VisitorTokenHash).HasMaxLength(64).IsFixedLength();
            session.Property(c => c.Language).HasMaxLength(5);
            session.Property(c => c.Origin).HasMaxLength(ChatSession.OriginMaxLength);
            session.Property(c => c.State).HasConversion<string>().HasMaxLength(20);
            session.Property(c => c.EndedBy).HasConversion<string>().HasMaxLength(20);
            session.Property(c => c.Version).IsRowVersion();
            session.HasIndex(c => new { c.TenantId, c.GroupId, c.State });
            session.HasIndex(c => new { c.TenantId, c.AgentId, c.State });
            session.HasIndex(c => new { c.TenantId, c.VisitorUserId });
            session.HasIndex(c => new { c.State, c.LastVisitorActivityAt }).HasFilter("state <> 'Ended'");
        });

        modelBuilder.Entity<ChatMessage>(message =>
        {
            message.ToTable("chat_messages", table =>
                table.HasCheckConstraint("ck_chat_messages_sender", EnumCheck("sender", Enum.GetNames<ChatSender>())));
            message.HasKey(m => m.Id);
            message.Property(m => m.Id).ValueGeneratedNever();
            message.Property(m => m.Sequence).UseIdentityAlwaysColumn();
            message.HasOne<Tenant>().WithMany().HasForeignKey(m => m.TenantId).OnDelete(DeleteBehavior.Restrict);
            message.HasOne<ChatSession>().WithMany().HasForeignKey(m => m.SessionId).OnDelete(DeleteBehavior.Cascade);
            message.HasOne<UserAccount>().WithMany().HasForeignKey(m => m.AuthorId).OnDelete(DeleteBehavior.Restrict);
            message.Property(m => m.Sender).HasConversion<string>().HasMaxLength(20);
            message.Property(m => m.Body).HasMaxLength(ChatMessage.BodyMaxLength);
            message.Property(m => m.Event).HasMaxLength(ChatMessage.EventMaxLength);
            message.HasIndex(m => new { m.TenantId, m.SessionId, m.Sequence });
        });
    }

    private static string EnumCheck(string column, IEnumerable<string> values) =>
        $"{column} IN ({string.Join(", ", values.Select(v => $"'{v}'"))})";
}
