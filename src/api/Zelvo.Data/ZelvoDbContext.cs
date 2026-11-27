using Zelvo.Data.Payments;
using Zelvo.Data.Privacy;
using Microsoft.EntityFrameworkCore;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;
using Zelvo.Data.Ai;
using Zelvo.Data.Auditing;
using Zelvo.Data.Branding;
using Zelvo.Data.Channels;
using Zelvo.Data.Directory;
using Zelvo.Data.Catalog;
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
public sealed partial class ZelvoDbContext(DbContextOptions<ZelvoDbContext> options) : DbContext(options)
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

    /// <summary>Service catalogs (task C2, module 2).</summary>
    public DbSet<ServiceCatalog> ServiceCatalogs => Set<ServiceCatalog>();

    /// <summary>Editors of service catalogs.</summary>
    public DbSet<CatalogEditor> CatalogEditors => Set<CatalogEditor>();

    /// <summary>Services of the catalog.</summary>
    public DbSet<CatalogService> CatalogServices => Set<CatalogService>();

    /// <summary>Versions of service cards.</summary>
    public DbSet<CatalogServiceVersion> CatalogServiceVersions => Set<CatalogServiceVersion>();

    /// <summary>Service templates saved by the tenant.</summary>
    public DbSet<CatalogServiceTemplate> CatalogServiceTemplates => Set<CatalogServiceTemplate>();

    /// <summary>Service details of service request tickets.</summary>
    public DbSet<ServiceRequestDetails> ServiceRequests => Set<ServiceRequestDetails>();

    /// <summary>Customer voice details of complaints, inquiries and suggestions (task C6).</summary>
    public DbSet<Voice.CustomerVoiceRecord> CustomerVoiceRecords => Set<Voice.CustomerVoiceRecord>();

    /// <summary>Happiness meter answers (GOV-2, task C6).</summary>
    public DbSet<Voice.HappinessRating> HappinessRatings => Set<Voice.HappinessRating>();
    /// <summary>Fulfilment tasks of service requests (task C4).</summary>
    public DbSet<RequestTask> RequestTasks => Set<RequestTask>();

    /// <summary>Times a proactive service is due for a person (task C5).</summary>
    public DbSet<ProactiveRun> ProactiveRuns => Set<ProactiveRun>();

    /// <summary>Payment gateways (module 15, task E8).</summary>
    public DbSet<PaymentGateway> PaymentGateways => Set<PaymentGateway>();

    /// <summary>Fees of service requests and sandbox test payments (task E8).</summary>
    public DbSet<Payment> Payments => Set<Payment>();

    /// <summary>Tries at paying (task E8).</summary>
    public DbSet<PaymentAttempt> PaymentAttempts => Set<PaymentAttempt>();

    /// <summary>Refunds (task E8).</summary>
    public DbSet<PaymentRefund> PaymentRefunds => Set<PaymentRefund>();

    /// <summary>Daily reconciliations (task E8).</summary>
    public DbSet<PaymentReconciliation> PaymentReconciliations => Set<PaymentReconciliation>();

    /// <summary>Workflow rules of the no-code builder (task C3).</summary>
    public DbSet<Workflows.WorkflowRule> WorkflowRules => Set<Workflows.WorkflowRule>();

    /// <summary>Approval processes of tickets (task C3).</summary>
    public DbSet<Workflows.ApprovalProcess> ApprovalProcesses => Set<Workflows.ApprovalProcess>();

    /// <summary>Approvers' decisions (task C3).</summary>
    public DbSet<Workflows.ApprovalDecision> ApprovalDecisions => Set<Workflows.ApprovalDecision>();

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

    /// <summary>Background AI work for tickets (task E5).</summary>
    public DbSet<AiJob> AiJobs => Set<AiJob>();

    /// <summary>AI triage suggestions for new incidents.</summary>
    public DbSet<AiTriageSuggestion> AiTriageSuggestions => Set<AiTriageSuggestion>();

    /// <summary>Sentiment and summaries per ticket.</summary>
    public DbSet<AiTicketInsight> AiTicketInsights => Set<AiTicketInsight>();

    /// <summary>Possible duplicate incidents found by the AI.</summary>
    public DbSet<AiDuplicateCandidate> AiDuplicateCandidates => Set<AiDuplicateCandidate>();

    /// <summary>Cached one-click translations.</summary>
    public DbSet<AiTranslation> AiTranslations => Set<AiTranslation>();

    /// <summary>White-label branding per tenant (module 11, task F1).</summary>
    public DbSet<TenantBranding> TenantBrandings => Set<TenantBranding>();

    /// <summary>Custom domains of tenants.</summary>
    public DbSet<TenantDomain> TenantDomains => Set<TenantDomain>();

    /// <summary>Tenants' own email sender domains.</summary>
    public DbSet<SenderDomain> SenderDomains => Set<SenderDomain>();

    /// <summary>Branded PDF exports.</summary>
    public DbSet<DocumentExport> DocumentExports => Set<DocumentExport>();

    /// <summary>Scheduled reports (module 10, task F2).</summary>
    public DbSet<Reports.ReportSchedule> ReportSchedules => Set<Reports.ReportSchedule>();

    /// <summary>Recipients of scheduled reports.</summary>
    public DbSet<Reports.ReportScheduleRecipient> ReportScheduleRecipients => Set<Reports.ReportScheduleRecipient>();

    /// <summary>Emails of scheduled reports (delivery log).</summary>
    public DbSet<Reports.ReportDelivery> ReportDeliveries => Set<Reports.ReportDelivery>();

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

    /// <summary>Versions of the privacy notice (GOV-5, task F5).</summary>
    public DbSet<PrivacyNotice> PrivacyNotices => Set<PrivacyNotice>();

    /// <summary>Agents' daily wellbeing figures (module 21, task K4).</summary>
    public DbSet<Wellbeing.WellbeingDay> WellbeingDays => Set<Wellbeing.WellbeingDay>();

    /// <summary>Groups' wellbeing thresholds (task K4).</summary>
    public DbSet<Wellbeing.WellbeingThreshold> WellbeingThresholds => Set<Wellbeing.WellbeingThreshold>();

    /// <summary>Agents past a threshold (task K4).</summary>
    public DbSet<Wellbeing.WellbeingAlert> WellbeingAlerts => Set<Wellbeing.WellbeingAlert>();

    /// <summary>Optional purposes people agree to or not (task F5).</summary>
    public DbSet<ConsentPurpose> ConsentPurposes => Set<ConsentPurpose>();

    /// <summary>The consent history, never changed (task F5).</summary>
    public DbSet<ConsentRecord> ConsentRecords => Set<ConsentRecord>();

    /// <summary>One-time links to choose a password (task M2: the first tenant administrator of a hardened installation).</summary>
    public DbSet<AccountSetupLink> AccountSetupLinks => Set<AccountSetupLink>();

    /// <summary>Data subject requests (task F5).</summary>
    public DbSet<DataSubjectRequest> DataSubjectRequests => Set<DataSubjectRequest>();

    /// <summary>Imported update packages (module 13, task F4); installation-wide.</summary>
    public DbSet<Installation.UpdatePackage> UpdatePackages => Set<Installation.UpdatePackage>();

    /// <summary>Installs of update packages; installation-wide.</summary>
    public DbSet<Installation.UpdateInstall> UpdateInstalls => Set<Installation.UpdateInstall>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ConfigureUpdates(modelBuilder);
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
        ConfigureCatalog(modelBuilder);
        ConfigureVoice(modelBuilder);
        ConfigureMessaging(modelBuilder);
        ConfigureConnectors(modelBuilder);
        ConfigureCmdb(modelBuilder);
        ConfigureProblems(modelBuilder);

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

    private static void ConfigureCatalog(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ServiceCatalog>(catalog =>
        {
            catalog.ToTable("service_catalogs", table =>
                table.HasCheckConstraint("ck_service_catalogs_key", "key ~ '^[a-z0-9]([a-z0-9-]{0,58}[a-z0-9])?$'"));
            catalog.HasKey(c => c.Id);
            catalog.Property(c => c.Id).ValueGeneratedNever();
            catalog.HasOne<Tenant>().WithMany().HasForeignKey(c => c.TenantId).OnDelete(DeleteBehavior.Restrict);
            catalog.HasOne<Department>().WithMany().HasForeignKey(c => c.DepartmentId).OnDelete(DeleteBehavior.Restrict);
            catalog.Property(c => c.Key).HasMaxLength(ServiceCatalog.KeyMaxLength);
            catalog.ConfigureLocalizedText(c => c.Name, "name", ServiceCatalog.NameMaxLength);
            catalog.ConfigureLocalizedText(c => c.Description, "description", ServiceCatalog.DescriptionMaxLength);
            catalog.Property(c => c.Version).IsRowVersion();
            catalog.HasIndex(c => new { c.TenantId, c.Key }).IsUnique();
            catalog.HasIndex(c => c.DepartmentId);
            catalog.HasMany(c => c.Editors).WithOne().HasForeignKey(e => e.CatalogId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<CatalogEditor>(editor =>
        {
            editor.ToTable("catalog_editors", table =>
                table.HasCheckConstraint("ck_catalog_editors_one", "num_nonnulls(user_id, group_id) = 1"));
            editor.HasKey(e => e.Id);
            editor.Property(e => e.Id).ValueGeneratedNever();
            editor.HasOne<Tenant>().WithMany().HasForeignKey(e => e.TenantId).OnDelete(DeleteBehavior.Restrict);
            editor.HasOne<UserAccount>().WithMany().HasForeignKey(e => e.UserId).OnDelete(DeleteBehavior.Cascade);
            editor.HasOne<SupportGroup>().WithMany().HasForeignKey(e => e.GroupId).OnDelete(DeleteBehavior.Cascade);
            editor.HasIndex(e => new { e.CatalogId, e.UserId, e.GroupId }).IsUnique().AreNullsDistinct(false);
            editor.HasIndex(e => e.TenantId);
            editor.HasIndex(e => e.UserId);
            editor.HasIndex(e => e.GroupId);
        });

        modelBuilder.Entity<CatalogService>(service =>
        {
            service.ToTable("catalog_services", table =>
            {
                table.HasCheckConstraint("ck_catalog_services_state", EnumCheck("state", Enum.GetNames<ServiceState>()));
                table.HasCheckConstraint("ck_catalog_services_code", "code ~ '^[A-Z0-9][A-Z0-9-]{0,28}[A-Z0-9]$'");
                table.HasCheckConstraint("ck_catalog_services_published", "state IN ('Draft', 'UnderReview') OR published_version_id IS NOT NULL");
                table.HasCheckConstraint("ck_catalog_services_not_own_parent", "parent_id IS NULL OR parent_id <> id");
            });
            service.HasKey(s => s.Id);
            service.Property(s => s.Id).ValueGeneratedNever();
            service.HasOne<Tenant>().WithMany().HasForeignKey(s => s.TenantId).OnDelete(DeleteBehavior.Restrict);
            service.HasOne<UserAccount>().WithMany().HasForeignKey(s => s.CreatedById).OnDelete(DeleteBehavior.SetNull);
            // NO ACTION (checked at the end of each statement), so a tenant's catalog can be deleted together.
            service.HasOne<CatalogService>().WithMany().HasForeignKey(s => s.ParentId).OnDelete(DeleteBehavior.NoAction);
            // No foreign keys to the versions (as for knowledge articles): a service and its first
            // version are inserted together, and versions belong to their service.
            service.Property(s => s.Code).HasMaxLength(CatalogService.CodeMaxLength);
            service.Property(s => s.State).HasConversion<string>().HasMaxLength(20);
            service.ConfigureLocalizedText(s => s.Name, "name", CatalogService.NameMaxLength);
            service.ConfigureLocalizedText(s => s.SuspendedMessage, "suspended_message", CatalogService.SuspendedMessageMaxLength);
            service.Property(s => s.CatalogIds).HasColumnType("uuid[]").HasDefaultValueSql("'{}'");
            service.Property(s => s.CategoryIds).HasColumnType("uuid[]").HasDefaultValueSql("'{}'");
            service.Property(s => s.EditorCatalogIds).HasColumnType("uuid[]").HasDefaultValueSql("'{}'");
            service.Property(s => s.Version).IsRowVersion();
            service.HasIndex(s => new { s.TenantId, s.Code }).IsUnique();
            service.HasIndex(s => new { s.TenantId, s.State });
            service.HasIndex(s => s.ParentId);
            service.HasIndex(s => s.PublishedVersionId);
            service.HasIndex(s => s.WorkingVersionId);
            service.HasIndex(s => s.CreatedById);
        });

        modelBuilder.Entity<CatalogServiceVersion>(version =>
        {
            version.ToTable("catalog_service_versions", table =>
            {
                table.HasCheckConstraint("ck_catalog_service_versions_state", EnumCheck("state", Enum.GetNames<ServiceVersionState>()));
                table.HasCheckConstraint("ck_catalog_service_versions_number", "number >= 1");
            });
            version.HasKey(v => v.Id);
            version.Property(v => v.Id).ValueGeneratedNever();
            version.HasOne<Tenant>().WithMany().HasForeignKey(v => v.TenantId).OnDelete(DeleteBehavior.Restrict);
            version.HasOne<CatalogService>().WithMany().HasForeignKey(v => v.ServiceId).OnDelete(DeleteBehavior.Cascade);
            version.HasOne<UserAccount>().WithMany().HasForeignKey(v => v.CreatedById).OnDelete(DeleteBehavior.SetNull);
            version.HasOne<UserAccount>().WithMany().HasForeignKey(v => v.LastEditedById).OnDelete(DeleteBehavior.SetNull);
            version.HasOne<UserAccount>().WithMany().HasForeignKey(v => v.SubmittedById).OnDelete(DeleteBehavior.SetNull);
            version.HasOne<UserAccount>().WithMany().HasForeignKey(v => v.ReviewedById).OnDelete(DeleteBehavior.SetNull);
            version.Property(v => v.State).HasConversion<string>().HasMaxLength(20);
            version.Property(v => v.Content).HasColumnType("jsonb");
            version.Property(v => v.ChangeNote).HasMaxLength(CatalogServiceVersion.NoteMaxLength);
            version.Property(v => v.ReviewComment).HasMaxLength(CatalogServiceVersion.NoteMaxLength);
            version.Property(v => v.Version).IsRowVersion();
            version.HasIndex(v => new { v.ServiceId, v.Number }).IsUnique();
            version.HasIndex(v => new { v.TenantId, v.State });
            version.HasIndex(v => v.CreatedById);
            version.HasIndex(v => v.LastEditedById);
            version.HasIndex(v => v.SubmittedById);
            version.HasIndex(v => v.ReviewedById);
        });

        modelBuilder.Entity<CatalogServiceTemplate>(template =>
        {
            template.ToTable("catalog_service_templates");
            template.HasKey(t => t.Id);
            template.Property(t => t.Id).ValueGeneratedNever();
            template.HasOne<Tenant>().WithMany().HasForeignKey(t => t.TenantId).OnDelete(DeleteBehavior.Restrict);
            template.HasOne<UserAccount>().WithMany().HasForeignKey(t => t.CreatedById).OnDelete(DeleteBehavior.SetNull);
            template.ConfigureLocalizedText(t => t.Name, "name", CatalogService.NameMaxLength);
            template.ConfigureLocalizedText(t => t.Description, "description", ServiceCatalog.DescriptionMaxLength);
            template.Property(t => t.Content).HasColumnType("jsonb");
            template.HasIndex(t => t.TenantId);
            template.HasIndex(t => t.CreatedById);
        });

        modelBuilder.Entity<ServiceRequestDetails>(request =>
        {
            request.ToTable("service_requests");
            request.HasKey(r => r.TicketId);
            request.HasOne<Tenant>().WithMany().HasForeignKey(r => r.TenantId).OnDelete(DeleteBehavior.Restrict);
            request.HasOne<Ticket>().WithOne().HasForeignKey<ServiceRequestDetails>(r => r.TicketId).OnDelete(DeleteBehavior.Cascade);
            request.HasOne<CatalogService>().WithMany().HasForeignKey(r => r.ServiceId).OnDelete(DeleteBehavior.Restrict);
            request.HasOne<CatalogServiceVersion>().WithMany().HasForeignKey(r => r.ServiceVersionId).OnDelete(DeleteBehavior.Restrict);
            request.HasIndex(r => new { r.TenantId, r.ServiceId });
            request.HasIndex(r => r.ServiceVersionId);
            request.Property(r => r.Escalations).HasColumnType("jsonb").HasDefaultValueSql("'[]'::jsonb");
            // Task C5: parts of bundles, and requests a proactive service started.
            request.Property(r => r.Hold).HasConversion<string>().HasMaxLength(20).HasDefaultValue(ServiceRequestHold.None).HasSentinel(ServiceRequestHold.None);
            request.HasOne<Ticket>().WithMany().HasForeignKey(r => r.BundleId).OnDelete(DeleteBehavior.NoAction);
            request.HasIndex(r => r.BundleId);
            request.HasOne<ProactiveRun>().WithMany().HasForeignKey(r => r.ProactiveRunId).OnDelete(DeleteBehavior.SetNull);
            request.HasIndex(r => r.ProactiveRunId);
            request.ToTable(t =>
            {
                t.HasCheckConstraint("ck_service_requests_hold", EnumCheck("hold", Enum.GetNames<ServiceRequestHold>()));
                t.HasCheckConstraint("ck_service_requests_bundle_stage", "(bundle_id IS NULL) = (bundle_stage IS NULL) AND (bundle_stage IS NULL OR bundle_stage >= 0)");
                t.HasCheckConstraint("ck_service_requests_bundle_part", "NOT (is_bundle AND bundle_id IS NOT NULL)");
                t.HasCheckConstraint("ck_service_requests_ownership", "ownership_minutes IS NULL OR ownership_minutes > 0");
                t.HasCheckConstraint("ck_service_requests_completion", "completion_minutes IS NULL OR completion_minutes > 0");
            });
        });
    }

    private static void ConfigureVoice(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Voice.CustomerVoiceRecord>(voice =>
        {
            voice.ToTable("customer_voice_records", table =>
            {
                table.HasCheckConstraint("ck_customer_voice_records_priority", "priority BETWEEN 1 AND 5");
                table.HasCheckConstraint("ck_customer_voice_records_severity", "severity IS NULL OR severity BETWEEN 1 AND 3");
                table.HasCheckConstraint("ck_customer_voice_records_confirmation", EnumCheck("confirmation", Enum.GetNames<Voice.ComplaintConfirmation>()));
                table.HasCheckConstraint("ck_customer_voice_records_review", EnumCheck("review", Enum.GetNames<Voice.SuggestionReview>()));
                table.HasCheckConstraint("ck_customer_voice_records_tracking", "anonymous = (tracking_code_hash IS NOT NULL)");
                table.HasCheckConstraint("ck_customer_voice_records_tracking_hash", "tracking_code_hash IS NULL OR tracking_code_hash ~ '^[0-9a-f]{64}$'");
            });
            voice.HasKey(v => v.TicketId);
            voice.HasOne<Tenant>().WithMany().HasForeignKey(v => v.TenantId).OnDelete(DeleteBehavior.Restrict);
            voice.Property(v => v.RootCauseKey).HasMaxLength(Voice.CustomerVoiceRecord.KeyMaxLength);
            voice.Property(v => v.Confirmation).HasConversion<string>().HasMaxLength(20);
            voice.Property(v => v.Review).HasConversion<string>().HasMaxLength(20);
            voice.Property(v => v.ConfirmationComment).HasMaxLength(Voice.CustomerVoiceRecord.TextMaxLength);
            voice.Property(v => v.ReviewFeedback).HasMaxLength(Voice.CustomerVoiceRecord.TextMaxLength);
            voice.Property(v => v.TrackingCodeHash).HasMaxLength(64);
            // NO ACTION, so a tenant's tickets can be deleted together.
            voice.HasOne<Ticket>().WithMany().HasForeignKey(v => v.ConvertedToId).OnDelete(DeleteBehavior.NoAction);
            voice.HasOne<CatalogService>().WithMany().HasForeignKey(v => v.ServiceId).OnDelete(DeleteBehavior.SetNull);
            voice.HasIndex(v => v.ConvertedToId);
            voice.HasIndex(v => new { v.TenantId, v.ServiceId });
        });

        modelBuilder.Entity<Voice.HappinessRating>(rating =>
        {
            rating.ToTable("happiness_ratings", table =>
            {
                table.HasCheckConstraint("ck_happiness_ratings_points", "points IN (3, 5)");
                table.HasCheckConstraint("ck_happiness_ratings_score", "score BETWEEN 1 AND points");
                table.HasCheckConstraint("ck_happiness_ratings_record_type", EnumCheck("record_type", Enum.GetNames<TicketType>()));
                table.HasCheckConstraint("ck_happiness_ratings_channel", EnumCheck("channel", Enum.GetNames<TicketChannel>()));
                table.HasCheckConstraint("ck_happiness_ratings_device_hash", "device_hash IS NULL OR device_hash ~ '^[0-9a-f]{64}$'");
            });
            rating.HasKey(r => r.Id);
            rating.Property(r => r.Id).ValueGeneratedNever();
            rating.HasOne<Tenant>().WithMany().HasForeignKey(r => r.TenantId).OnDelete(DeleteBehavior.Restrict);
            rating.HasOne<Ticket>().WithMany().HasForeignKey(r => r.TicketId).OnDelete(DeleteBehavior.Cascade);
            rating.HasOne<UserAccount>().WithMany().HasForeignKey(r => r.UserId).OnDelete(DeleteBehavior.SetNull);
            rating.HasOne<CatalogService>().WithMany().HasForeignKey(r => r.ServiceId).OnDelete(DeleteBehavior.SetNull);
            rating.Property(r => r.RecordType).HasConversion<string>().HasMaxLength(30);
            rating.Property(r => r.Channel).HasConversion<string>().HasMaxLength(20);
            rating.Property(r => r.DeviceHash).HasMaxLength(64);
            rating.Property(r => r.Comment).HasMaxLength(Voice.HappinessRating.CommentMaxLength);
            rating.HasIndex(r => r.TicketId).IsUnique().HasFilter("ticket_id IS NOT NULL");
            rating.HasIndex(r => new { r.TenantId, r.CreatedAt });
            rating.HasIndex(r => r.UserId);
            rating.HasIndex(r => r.ServiceId);
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
            ticket.HasOne(t => t.Voice).WithOne().HasForeignKey<Voice.CustomerVoiceRecord>(v => v.TicketId).OnDelete(DeleteBehavior.Cascade);
            ticket.HasOne(t => t.Problem).WithOne().HasForeignKey<Problems.ProblemRecord>(p => p.TicketId).OnDelete(DeleteBehavior.Cascade);
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

        modelBuilder.Entity<RequestTask>(task =>
        {
            task.ToTable("request_tasks", table =>
            {
                table.HasCheckConstraint("ck_request_tasks_state", EnumCheck("state", Enum.GetNames<RequestTaskState>()));
                table.HasCheckConstraint("ck_request_tasks_stage", "stage >= 0");
                table.HasCheckConstraint("ck_request_tasks_target", "target_minutes IS NULL OR target_minutes > 0");
            });
            task.HasKey(t => t.Id);
            task.Property(t => t.Id).ValueGeneratedNever();
            task.HasOne<Tenant>().WithMany().HasForeignKey(t => t.TenantId).OnDelete(DeleteBehavior.Restrict);
            task.HasOne<Ticket>().WithMany().HasForeignKey(t => t.TicketId).OnDelete(DeleteBehavior.Cascade);
            task.HasOne<SupportGroup>().WithMany().HasForeignKey(t => t.GroupId).OnDelete(DeleteBehavior.Restrict);
            task.HasOne<UserAccount>().WithMany().HasForeignKey(t => t.AssigneeId).OnDelete(DeleteBehavior.Restrict);
            task.HasOne<UserAccount>().WithMany().HasForeignKey(t => t.CompletedById).OnDelete(DeleteBehavior.SetNull);
            task.Property(t => t.Key).HasMaxLength(RequestTask.KeyMaxLength);
            task.ConfigureLocalizedText(t => t.Name, "name", 150);
            task.ConfigureLocalizedText(t => t.Description, "description", 1000);
            task.Property(t => t.State).HasConversion<string>().HasMaxLength(20);
            task.Property(t => t.Notes).HasMaxLength(RequestTask.NotesMaxLength);
            task.Property(t => t.Version).IsRowVersion();
            task.HasIndex(t => new { t.TicketId, t.Key }).IsUnique();
            task.HasIndex(t => new { t.TenantId, t.GroupId, t.State });
            task.HasIndex(t => new { t.TenantId, t.AssigneeId, t.State });
            task.HasIndex(t => t.GroupId);
            task.HasIndex(t => t.AssigneeId);
            task.HasIndex(t => t.CompletedById);
        });

        modelBuilder.Entity<PaymentGateway>(gateway =>
        {
            gateway.ToTable("payment_gateways", t => t.HasCheckConstraint("ck_payment_gateways_mode", EnumCheck("mode", Enum.GetNames<GatewayMode>())));
            gateway.HasKey(g => g.Id);
            gateway.Property(g => g.Id).ValueGeneratedNever();
            gateway.HasOne<Tenant>().WithMany().HasForeignKey(g => g.TenantId).OnDelete(DeleteBehavior.Restrict);
            gateway.Property(g => g.Name).HasMaxLength(PaymentGateway.NameMaxLength);
            gateway.Property(g => g.MerchantId).HasMaxLength(PaymentGateway.MerchantIdMaxLength);
            gateway.Property(g => g.Mode).HasConversion<string>().HasMaxLength(10);
            gateway.Property(g => g.Connector).HasColumnType("jsonb").HasDefaultValueSql("'{}'::jsonb");
            gateway.Property(g => g.CredentialNames).HasColumnType("text[]").HasDefaultValueSql("'{}'");
            gateway.Property(g => g.Version).IsRowVersion();
            gateway.HasIndex(g => new { g.TenantId, g.Name }).IsUnique();
            // One default gateway per tenant.
            gateway.HasIndex(g => g.TenantId).IsUnique().HasFilter("is_default").HasDatabaseName("ix_payment_gateways_default");
        });

        modelBuilder.Entity<Payment>(payment =>
        {
            payment.ToTable("payments", t =>
            {
                t.HasCheckConstraint("ck_payments_state", EnumCheck("state", Enum.GetNames<PaymentState>()));
                t.HasCheckConstraint("ck_payments_timing", EnumCheck("timing", Enum.GetNames<PaymentTiming>()));
                t.HasCheckConstraint("ck_payments_amount", "amount_aed > 0 AND refunded_aed >= 0 AND refunded_aed <= amount_aed");
                t.HasCheckConstraint("ck_payments_ticket", "is_test OR ticket_id IS NOT NULL");
            });
            payment.HasKey(p => p.Id);
            payment.Property(p => p.Id).ValueGeneratedNever();
            payment.HasOne<Tenant>().WithMany().HasForeignKey(p => p.TenantId).OnDelete(DeleteBehavior.Restrict);
            payment.HasOne<Ticket>().WithMany().HasForeignKey(p => p.TicketId).OnDelete(DeleteBehavior.Cascade);
            payment.HasOne<PaymentGateway>().WithMany().HasForeignKey(p => p.GatewayId).OnDelete(DeleteBehavior.Restrict);
            payment.Property(p => p.AmountAed).HasPrecision(12, 2);
            payment.Property(p => p.RefundedAed).HasPrecision(12, 2);
            payment.Property(p => p.Breakdown).HasColumnType("jsonb").HasDefaultValueSql("'[]'::jsonb");
            payment.Property(p => p.Timing).HasConversion<string>().HasMaxLength(20);
            payment.Property(p => p.State).HasConversion<string>().HasMaxLength(20);
            payment.Property(p => p.TransactionId).HasMaxLength(PaymentAttempt.GatewayValueMaxLength);
            payment.Property(p => p.ReceiptNumber).HasMaxLength(Payment.ReceiptMaxLength);
            payment.Property(p => p.Version).IsRowVersion();
            payment.HasIndex(p => p.TicketId);
            payment.HasIndex(p => p.GatewayId);
            payment.HasIndex(p => new { p.TenantId, p.State, p.ExpiresAt });
            payment.HasIndex(p => new { p.TenantId, p.PaidAt });
            payment.HasIndex(p => new { p.TenantId, p.ReceiptSequence }).IsUnique();
        });

        modelBuilder.Entity<PaymentAttempt>(attempt =>
        {
            attempt.ToTable("payment_attempts", t => t.HasCheckConstraint("ck_payment_attempts_state", EnumCheck("state", Enum.GetNames<PaymentAttemptState>())));
            attempt.HasKey(a => a.Id);
            attempt.Property(a => a.Id).ValueGeneratedNever();
            attempt.HasOne<Tenant>().WithMany().HasForeignKey(a => a.TenantId).OnDelete(DeleteBehavior.Restrict);
            attempt.HasOne<Payment>().WithMany().HasForeignKey(a => a.PaymentId).OnDelete(DeleteBehavior.Cascade);
            attempt.HasOne<PaymentGateway>().WithMany().HasForeignKey(a => a.GatewayId).OnDelete(DeleteBehavior.Restrict);
            attempt.HasOne<UserAccount>().WithMany().HasForeignKey(a => a.StartedById).OnDelete(DeleteBehavior.SetNull);
            attempt.Property(a => a.Reference).HasMaxLength(PaymentAttempt.ReferenceMaxLength);
            attempt.Property(a => a.State).HasConversion<string>().HasMaxLength(20);
            attempt.Property(a => a.TransactionId).HasMaxLength(PaymentAttempt.GatewayValueMaxLength);
            attempt.Property(a => a.GatewayStatus).HasMaxLength(PaymentAttempt.GatewayValueMaxLength);
            attempt.Property(a => a.Version).IsRowVersion();
            // References are random and unique in the installation: the anonymous callback finds the tenant by them.
            attempt.HasIndex(a => a.Reference).IsUnique();
            attempt.HasIndex(a => a.PaymentId);
            attempt.HasIndex(a => a.GatewayId);
            attempt.HasIndex(a => a.StartedById);
            attempt.HasIndex(a => new { a.GatewayId, a.TransactionId }).IsUnique().HasFilter("transaction_id IS NOT NULL");
        });

        modelBuilder.Entity<PaymentRefund>(refund =>
        {
            refund.ToTable("payment_refunds", t =>
            {
                t.HasCheckConstraint("ck_payment_refunds_state", EnumCheck("state", Enum.GetNames<RefundState>()));
                t.HasCheckConstraint("ck_payment_refunds_amount", "amount_aed > 0");
            });
            refund.HasKey(r => r.Id);
            refund.Property(r => r.Id).ValueGeneratedNever();
            refund.HasOne<Tenant>().WithMany().HasForeignKey(r => r.TenantId).OnDelete(DeleteBehavior.Restrict);
            refund.HasOne<Payment>().WithMany().HasForeignKey(r => r.PaymentId).OnDelete(DeleteBehavior.Cascade);
            refund.HasOne<UserAccount>().WithMany().HasForeignKey(r => r.CreatedById).OnDelete(DeleteBehavior.SetNull);
            refund.Property(r => r.AmountAed).HasPrecision(12, 2);
            refund.Property(r => r.Reason).HasMaxLength(PaymentRefund.ReasonMaxLength);
            refund.Property(r => r.State).HasConversion<string>().HasMaxLength(20);
            refund.Property(r => r.GatewayRefundId).HasMaxLength(PaymentAttempt.GatewayValueMaxLength);
            refund.HasIndex(r => r.PaymentId);
            refund.HasIndex(r => r.CreatedById);
        });

        modelBuilder.Entity<PaymentReconciliation>(day =>
        {
            day.ToTable("payment_reconciliations");
            day.HasKey(r => r.Id);
            day.Property(r => r.Id).ValueGeneratedNever();
            day.HasOne<Tenant>().WithMany().HasForeignKey(r => r.TenantId).OnDelete(DeleteBehavior.Restrict);
            day.HasOne<PaymentGateway>().WithMany().HasForeignKey(r => r.GatewayId).OnDelete(DeleteBehavior.Cascade);
            day.Property(r => r.TotalAed).HasPrecision(14, 2);
            day.Property(r => r.Lines).HasColumnType("jsonb").HasDefaultValueSql("'[]'::jsonb");
            day.HasIndex(r => new { r.TenantId, r.GatewayId, r.Day }).IsUnique();
            day.HasIndex(r => r.GatewayId);
        });

        modelBuilder.Entity<ProactiveRun>(run =>
        {
            run.ToTable("proactive_runs", table =>
            {
                table.HasCheckConstraint("ck_proactive_runs_kind", EnumCheck("kind", Enum.GetNames<ProactiveTriggerKind>()));
                table.HasCheckConstraint("ck_proactive_runs_state", EnumCheck("state", Enum.GetNames<ProactiveRunState>()));
            });
            run.HasKey(r => r.Id);
            run.Property(r => r.Id).ValueGeneratedNever();
            run.HasOne<Tenant>().WithMany().HasForeignKey(r => r.TenantId).OnDelete(DeleteBehavior.Restrict);
            run.HasOne<CatalogService>().WithMany().HasForeignKey(r => r.ServiceId).OnDelete(DeleteBehavior.Cascade);
            run.HasOne<UserAccount>().WithMany().HasForeignKey(r => r.PersonId).OnDelete(DeleteBehavior.Cascade);
            // NO ACTION, so a tenant's tickets can be deleted together (demo reset).
            run.HasOne<Ticket>().WithMany().HasForeignKey(r => r.SourceTicketId).OnDelete(DeleteBehavior.NoAction);
            run.HasOne<Ticket>().WithMany().HasForeignKey(r => r.TicketId).OnDelete(DeleteBehavior.NoAction);
            run.Property(r => r.TriggerKey).HasMaxLength(ProactiveRun.KeyMaxLength);
            run.Property(r => r.Occurrence).HasMaxLength(ProactiveRun.OccurrenceMaxLength);
            run.Property(r => r.Kind).HasConversion<string>().HasMaxLength(20);
            run.Property(r => r.State).HasConversion<string>().HasMaxLength(20);
            run.Property(r => r.Answers).HasColumnType("jsonb").HasDefaultValueSql("'{}'::jsonb");
            run.Property(r => r.Reason).HasMaxLength(ProactiveRun.ReasonMaxLength);
            run.Property(r => r.Version).IsRowVersion();
            run.HasIndex(r => new { r.TenantId, r.ServiceId, r.TriggerKey, r.PersonId, r.Occurrence }).IsUnique();
            run.HasIndex(r => new { r.TenantId, r.State, r.DueOn });
            run.HasIndex(r => new { r.TenantId, r.PersonId, r.State });
            run.HasIndex(r => r.ServiceId);
            run.HasIndex(r => r.PersonId);
            run.HasIndex(r => r.SourceTicketId);
            run.HasIndex(r => r.TicketId);
        });

        modelBuilder.Entity<Workflows.WorkflowRule>(rule =>
        {
            rule.ToTable("workflow_rules", table =>
            {
                table.HasCheckConstraint("ck_workflow_rules_trigger", EnumCheck("trigger", Enum.GetNames<Workflows.WorkflowTrigger>()));
                table.HasCheckConstraint("ck_workflow_rules_record_type", "record_type IS NULL OR " + EnumCheck("record_type", Enum.GetNames<TicketType>()));
                table.HasCheckConstraint("ck_workflow_rules_match", EnumCheck("match", Enum.GetNames<Workflows.ConditionMatch>()));
            });
            rule.HasKey(r => r.Id);
            rule.Property(r => r.Id).ValueGeneratedNever();
            rule.HasOne<Tenant>().WithMany().HasForeignKey(r => r.TenantId).OnDelete(DeleteBehavior.Restrict);
            rule.HasOne<UserAccount>().WithMany().HasForeignKey(r => r.CreatedById).OnDelete(DeleteBehavior.SetNull);
            rule.ConfigureLocalizedText(r => r.Name, "name", Workflows.WorkflowRule.NameMaxLength);
            rule.ConfigureLocalizedText(r => r.Description, "description", Workflows.WorkflowRule.DescriptionMaxLength);
            rule.Property(r => r.Trigger).HasConversion<string>().HasMaxLength(30);
            rule.Property(r => r.RecordType).HasConversion<string>().HasMaxLength(30);
            rule.Property(r => r.Match).HasConversion<string>().HasMaxLength(10);
            rule.Property(r => r.Conditions).HasColumnType("jsonb");
            rule.Property(r => r.Actions).HasColumnType("jsonb");
            rule.Property(r => r.Version).IsRowVersion();
            rule.HasIndex(r => new { r.TenantId, r.Trigger, r.Position });
            rule.HasIndex(r => r.CreatedById);
            // Task K1: plain-language drafts.
            rule.Property(r => r.SourceText).HasMaxLength(Workflows.WorkflowRule.SourceTextMaxLength);
            rule.Property(r => r.SourceLanguage).HasMaxLength(2);
            rule.Property(r => r.Unmapped).HasColumnType("jsonb").HasDefaultValueSql("'[]'::jsonb");
            rule.HasOne<UserAccount>().WithMany().HasForeignKey(r => r.ApprovedById).OnDelete(DeleteBehavior.SetNull);
            rule.HasIndex(r => r.ApprovedById);
        });

        modelBuilder.Entity<Workflows.ApprovalProcess>(process =>
        {
            process.ToTable("approval_processes", table =>
            {
                table.HasCheckConstraint("ck_approval_processes_stage", EnumCheck("stage", Enum.GetNames<Workflows.ApprovalStage>()));
                table.HasCheckConstraint("ck_approval_processes_state", EnumCheck("state", Enum.GetNames<Workflows.ApprovalProcessState>()));
            });
            process.HasKey(p => p.Id);
            process.Property(p => p.Id).ValueGeneratedNever();
            process.HasOne<Tenant>().WithMany().HasForeignKey(p => p.TenantId).OnDelete(DeleteBehavior.Restrict);
            process.HasOne<Ticket>().WithMany().HasForeignKey(p => p.TicketId).OnDelete(DeleteBehavior.Cascade);
            process.HasOne<UserAccount>().WithMany().HasForeignKey(p => p.StartedById).OnDelete(DeleteBehavior.SetNull);
            process.Property(p => p.Stage).HasConversion<string>().HasMaxLength(20);
            process.Property(p => p.State).HasConversion<string>().HasMaxLength(20);
            process.Property(p => p.Levels).HasColumnType("jsonb");
            process.Property(p => p.EngineInstanceId).HasMaxLength(100);
            process.Property(p => p.EngineBookmarkId).HasMaxLength(100);
            process.Property(p => p.Reason).HasMaxLength(Workflows.ApprovalProcess.TextMaxLength);
            process.Property(p => p.ResolutionNotes).HasMaxLength(Workflows.ApprovalProcess.TextMaxLength);
            process.Property(p => p.Version).IsRowVersion();
            process.HasIndex(p => new { p.TenantId, p.TicketId });
            process.HasIndex(p => p.TicketId);
            process.HasIndex(p => p.StartedById);
        });

        modelBuilder.Entity<Workflows.ApprovalDecision>(decision =>
        {
            decision.ToTable("approval_decisions", table =>
            {
                table.HasCheckConstraint("ck_approval_decisions_state", EnumCheck("state", Enum.GetNames<Workflows.ApprovalDecisionState>()));
                table.HasCheckConstraint("ck_approval_decisions_reason", "state <> 'Rejected' OR comment IS NOT NULL");
            });
            decision.HasKey(d => d.Id);
            decision.Property(d => d.Id).ValueGeneratedNever();
            decision.HasOne<Tenant>().WithMany().HasForeignKey(d => d.TenantId).OnDelete(DeleteBehavior.Restrict);
            decision.HasOne<Workflows.ApprovalProcess>().WithMany().HasForeignKey(d => d.ProcessId).OnDelete(DeleteBehavior.Cascade);
            decision.HasOne<Ticket>().WithMany().HasForeignKey(d => d.TicketId).OnDelete(DeleteBehavior.Cascade);
            decision.HasOne<UserAccount>().WithMany().HasForeignKey(d => d.ApproverId).OnDelete(DeleteBehavior.Restrict);
            decision.Property(d => d.State).HasConversion<string>().HasMaxLength(20);
            decision.Property(d => d.Comment).HasMaxLength(Workflows.ApprovalDecision.CommentMaxLength);
            decision.Property(d => d.DecidedVia).HasMaxLength(20);
            decision.Property(d => d.Version).IsRowVersion();
            decision.HasIndex(d => new { d.ProcessId, d.Level, d.ApproverId }).IsUnique();
            decision.HasIndex(d => new { d.TenantId, d.ApproverId, d.State });
            decision.HasIndex(d => d.TicketId);
            decision.HasIndex(d => d.ApproverId);
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
                table.HasCheckConstraint("ck_ticket_attachments_owner", "num_nonnulls(ticket_id, article_id, service_id, branding_slot) = 1 OR (ticket_id IS NULL AND article_id IS NULL AND service_id IS NULL AND branding_slot IS NULL AND form_field_key IS NOT NULL)");
                table.HasCheckConstraint("ck_ticket_attachments_branding_slot", $"branding_slot IS NULL OR {EnumCheck("branding_slot", Enum.GetNames<BrandingSlot>())}");
                table.HasCheckConstraint("ck_ticket_attachments_form_field_key", "form_field_key ~ '^[a-z][a-zA-Z0-9]{0,59}$'");
                table.HasCheckConstraint("ck_ticket_attachments_comment", "comment_id IS NULL OR ticket_id IS NOT NULL");
            });
            attachment.HasKey(a => a.Id);
            attachment.Property(a => a.Id).ValueGeneratedNever();
            attachment.HasOne<Ticket>().WithMany().HasForeignKey(a => a.TicketId).OnDelete(DeleteBehavior.Cascade);
            attachment.HasOne<KbArticle>().WithMany().HasForeignKey(a => a.ArticleId).OnDelete(DeleteBehavior.Cascade);
            attachment.HasOne<CatalogService>().WithMany().HasForeignKey(a => a.ServiceId).OnDelete(DeleteBehavior.Cascade);
            attachment.HasIndex(a => new { a.TenantId, a.ServiceId }).HasFilter("service_id IS NOT NULL");
            attachment.Property(a => a.BrandingSlot).HasConversion<string>().HasMaxLength(20);
            attachment.Property(a => a.FormFieldKey).HasMaxLength(TicketAttachment.FormFieldKeyMaxLength);
            attachment.HasIndex(a => new { a.TenantId, a.UploadedAt }).HasFilter("ticket_id IS NULL AND article_id IS NULL AND service_id IS NULL").HasDatabaseName("ix_ticket_attachments_form_uploads");
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

        ConfigureAiFeatures(modelBuilder);
        ConfigureBranding(modelBuilder);
        ConfigureReports(modelBuilder);
    }

    // Task F1: white-label branding, custom domains, sender domains and PDF exports.
    private static void ConfigureBranding(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<TenantBranding>(branding =>
        {
            branding.ToTable("tenant_brandings");
            branding.HasKey(b => b.TenantId);
            branding.HasOne<Tenant>().WithOne().HasForeignKey<TenantBranding>(b => b.TenantId).OnDelete(DeleteBehavior.Restrict);
            branding.HasOne<UserAccount>().WithMany().HasForeignKey(b => b.PublishedById).OnDelete(DeleteBehavior.Restrict);
            branding.Property(b => b.Draft).HasColumnType("jsonb");
            branding.Property(b => b.Published).HasColumnType("jsonb");
            branding.Property(b => b.Version).IsRowVersion();
            branding.HasIndex(b => b.PublishedById);
        });

        modelBuilder.Entity<TenantDomain>(domain =>
        {
            domain.ToTable("tenant_domains", table =>
            {
                table.HasCheckConstraint("ck_tenant_domains_status", EnumCheck("status", Enum.GetNames<DomainStatus>()));
                table.HasCheckConstraint("ck_tenant_domains_certificate_mode", EnumCheck("certificate_mode", Enum.GetNames<CertificateMode>()));
                table.HasCheckConstraint("ck_tenant_domains_host", "host ~ '^[a-z0-9]([a-z0-9-]*[a-z0-9])?(\\.[a-z0-9]([a-z0-9-]*[a-z0-9])?)+$'");
                table.HasCheckConstraint("ck_tenant_domains_uploaded", "certificate_mode <> 'Uploaded' OR (certificate_pem IS NOT NULL AND private_key_ciphertext IS NOT NULL)");
            });
            domain.HasKey(d => d.Id);
            domain.Property(d => d.Id).ValueGeneratedNever();
            domain.HasOne<Tenant>().WithMany().HasForeignKey(d => d.TenantId).OnDelete(DeleteBehavior.Restrict);
            domain.Property(d => d.Host).HasMaxLength(TenantDomain.HostMaxLength);
            domain.Property(d => d.Status).HasConversion<string>().HasMaxLength(20);
            domain.Property(d => d.CertificateMode).HasConversion<string>().HasMaxLength(20);
            domain.Property(d => d.VerificationToken).HasMaxLength(64);
            domain.Property(d => d.LastError).HasMaxLength(TenantDomain.ErrorMaxLength);
            domain.Property(d => d.CertificatePem).HasMaxLength(TenantDomain.CertificateMaxLength);
            domain.Property(d => d.Version).IsRowVersion();
            // A host belongs to one tenant in the whole installation.
            domain.HasIndex(d => d.Host).IsUnique();
            domain.HasIndex(d => d.TenantId);
        });

        modelBuilder.Entity<SenderDomain>(sender =>
        {
            sender.ToTable("sender_domains", table =>
            {
                table.HasCheckConstraint("ck_sender_domains_spf", EnumCheck("spf", Enum.GetNames<DnsCheck>()));
                table.HasCheckConstraint("ck_sender_domains_dkim", EnumCheck("dkim", Enum.GetNames<DnsCheck>()));
                table.HasCheckConstraint("ck_sender_domains_dmarc", EnumCheck("dmarc", Enum.GetNames<DnsCheck>()));

                table.HasCheckConstraint("ck_sender_domains_domain", "domain ~ '^[a-z0-9]([a-z0-9-]*[a-z0-9])?(\\.[a-z0-9]([a-z0-9-]*[a-z0-9])?)+$'");
                table.HasCheckConstraint("ck_sender_domains_local_part", "local_part ~ '^[a-z0-9]([a-z0-9._-]*[a-z0-9])?$'");
            });
            sender.HasKey(d => d.TenantId);
            sender.HasOne<Tenant>().WithOne().HasForeignKey<SenderDomain>(d => d.TenantId).OnDelete(DeleteBehavior.Restrict);
            sender.Property(d => d.Domain).HasMaxLength(TenantDomain.HostMaxLength);
            sender.Property(d => d.LocalPart).HasMaxLength(SenderDomain.LocalPartMaxLength);
            sender.Property(d => d.DkimSelector).HasMaxLength(SenderDomain.SelectorMaxLength);
            sender.Property(d => d.Spf).HasConversion<string>().HasMaxLength(20);
            sender.Property(d => d.Dkim).HasConversion<string>().HasMaxLength(20);
            sender.Property(d => d.Dmarc).HasConversion<string>().HasMaxLength(20);
            sender.Property(d => d.Version).IsRowVersion();
        });

        modelBuilder.Entity<DocumentExport>(export =>
        {
            export.ToTable("document_exports", table =>
            {
                table.HasCheckConstraint("ck_document_exports_kind", EnumCheck("kind", Enum.GetNames<ExportKind>()));
                table.HasCheckConstraint("ck_document_exports_status", EnumCheck("status", Enum.GetNames<ExportStatus>()));
                table.HasCheckConstraint("ck_document_exports_language", "language IN ('en', 'ar')");
                // Task F2: report exports have their dashboard and filter instead of a ticket.
                // Reports and asset label sheets (task G3) are described by their parameters; tickets by their id.
                table.HasCheckConstraint("ck_document_exports_subject", "(kind IN ('Report', 'AssetLabels')) = (entity_id IS NULL AND parameters IS NOT NULL)");
            });
            export.HasKey(e => e.Id);
            export.Property(e => e.Id).ValueGeneratedNever();
            export.HasOne<Tenant>().WithMany().HasForeignKey(e => e.TenantId).OnDelete(DeleteBehavior.Restrict);
            export.HasOne<Ticket>().WithMany().HasForeignKey(e => e.EntityId).OnDelete(DeleteBehavior.Cascade);
            export.HasOne<UserAccount>().WithMany().HasForeignKey(e => e.RequestedById).OnDelete(DeleteBehavior.Restrict);
            export.Property(e => e.Kind).HasConversion<string>().HasMaxLength(20);
            export.Property(e => e.Status).HasConversion<string>().HasMaxLength(20);
            export.Property(e => e.Language).HasMaxLength(5);
            export.Property(e => e.FileName).HasMaxLength(DocumentExport.FileNameMaxLength);
            export.Property(e => e.Error).HasMaxLength(DocumentExport.ErrorMaxLength);
            export.Property(e => e.Parameters).HasColumnType("jsonb");
            export.HasIndex(e => new { e.TenantId, e.Status, e.CreatedAt });
            export.HasIndex(e => e.EntityId);
            export.HasIndex(e => e.RequestedById);
            export.HasIndex(e => e.CreatedAt).HasFilter("status = 'Pending'").HasDatabaseName("ix_document_exports_due");
        });
    }

    // Task F4: update packages and their installs belong to the whole installation (no tenant).
    private static void ConfigureUpdates(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Installation.UpdatePackage>(package =>
        {
            package.ToTable("update_packages", table =>
            {
                table.HasCheckConstraint("ck_update_packages_status", EnumCheck("status", Enum.GetNames<Installation.UpdatePackageStatus>()));
                table.HasCheckConstraint("ck_update_packages_type", "package_type IN ('Security', 'Feature')");
            });
            package.HasKey(p => p.Id);
            package.Property(p => p.Id).ValueGeneratedNever();
            package.Property(p => p.Version).HasMaxLength(Installation.UpdatePackage.VersionMaxLength);
            package.Property(p => p.PackageType).HasMaxLength(10);
            package.Property(p => p.FileName).HasMaxLength(Installation.UpdatePackage.FileNameMaxLength);
            package.Property(p => p.StoragePath).HasMaxLength(500);
            package.Property(p => p.Sha256).HasMaxLength(64);
            package.Property(p => p.Manifest).HasColumnType("jsonb");
            package.Property(p => p.Status).HasConversion<string>().HasMaxLength(20);
            package.HasIndex(p => p.ImportedAt);
        });

        modelBuilder.Entity<Installation.UpdateInstall>(install =>
        {
            install.ToTable("update_installs", table =>
            {
                table.HasCheckConstraint("ck_update_installs_state", EnumCheck("state", Enum.GetNames<Installation.UpdateInstallState>()));
                table.HasCheckConstraint("ck_update_installs_notice", "notice_from <= scheduled_for");
                table.HasCheckConstraint("ck_update_installs_minutes", "expected_minutes BETWEEN 1 AND 1440");
            });
            install.HasKey(i => i.Id);
            install.Property(i => i.Id).ValueGeneratedNever();
            install.HasOne<Installation.UpdatePackage>().WithMany().HasForeignKey(i => i.PackageId).OnDelete(DeleteBehavior.Restrict);
            install.Property(i => i.FromVersion).HasMaxLength(Installation.UpdatePackage.VersionMaxLength);
            install.Property(i => i.ToVersion).HasMaxLength(Installation.UpdatePackage.VersionMaxLength);
            install.Property(i => i.State).HasConversion<string>().HasMaxLength(20);
            install.Property(i => i.Steps).HasColumnType("jsonb");
            install.Property(i => i.Error).HasMaxLength(Installation.UpdateInstall.MessageMaxLength);
            install.Property(i => i.BackupPath).HasMaxLength(500);
            // One install at a time: ux_update_installs_active (SQL in the migration) allows at most one scheduled or running.
            install.HasIndex(i => i.CreatedAt);
        });
    }

    // Task E5: the AI features' own records.
    // Task F2: scheduled reports, their recipients and the delivery log.
    private static void ConfigureReports(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Reports.ReportSchedule>(schedule =>
        {
            schedule.ToTable("report_schedules", table =>
            {
                table.HasCheckConstraint("ck_report_schedules_report", "report IN ('Overview', 'Volumes', 'Backlog', 'Sla', 'Agents', 'Happiness')");
                table.HasCheckConstraint("ck_report_schedules_frequency", EnumCheck("frequency", Enum.GetNames<Reports.ReportFrequency>()));
                table.HasCheckConstraint("ck_report_schedules_day_of_week", "(frequency = 'Weekly') = (day_of_week IS NOT NULL) AND (day_of_week IS NULL OR day_of_week BETWEEN 1 AND 7)");
                table.HasCheckConstraint("ck_report_schedules_day_of_month", "(frequency = 'Monthly') = (day_of_month IS NOT NULL) AND (day_of_month IS NULL OR day_of_month BETWEEN 1 AND 28)");
                table.HasCheckConstraint("ck_report_schedules_time_of_day", "time_of_day BETWEEN 0 AND 1439");
            });
            schedule.HasKey(s => s.Id);
            schedule.Property(s => s.Id).ValueGeneratedNever();
            schedule.HasOne<Tenant>().WithMany().HasForeignKey(s => s.TenantId).OnDelete(DeleteBehavior.Restrict);
            schedule.HasOne<UserAccount>().WithMany().HasForeignKey(s => s.OwnerId).OnDelete(DeleteBehavior.Restrict);
            schedule.Property(s => s.Name).HasMaxLength(Reports.ReportSchedule.NameMaxLength);
            schedule.Property(s => s.Report).HasMaxLength(20);
            schedule.Property(s => s.Filters).HasColumnType("jsonb");
            schedule.Property(s => s.Frequency).HasConversion<string>().HasMaxLength(20);
            schedule.Property(s => s.Version).IsRowVersion();
            schedule.HasMany(s => s.Recipients).WithOne().HasForeignKey(r => r.ScheduleId).OnDelete(DeleteBehavior.Cascade);
            schedule.HasIndex(s => s.TenantId);
            schedule.HasIndex(s => s.OwnerId);
            schedule.HasIndex(s => s.NextRunAt).HasFilter("active AND next_run_at IS NOT NULL").HasDatabaseName("ix_report_schedules_due");
        });

        modelBuilder.Entity<Reports.ReportScheduleRecipient>(recipient =>
        {
            recipient.ToTable("report_schedule_recipients");
            recipient.HasKey(r => new { r.ScheduleId, r.UserId });
            recipient.HasOne<Tenant>().WithMany().HasForeignKey(r => r.TenantId).OnDelete(DeleteBehavior.Restrict);
            recipient.HasOne<UserAccount>().WithMany().HasForeignKey(r => r.UserId).OnDelete(DeleteBehavior.Cascade);
            recipient.HasIndex(r => r.TenantId);
            recipient.HasIndex(r => r.UserId);
        });

        modelBuilder.Entity<Reports.ReportDelivery>(delivery =>
        {
            delivery.ToTable("report_deliveries", table =>
                table.HasCheckConstraint("ck_report_deliveries_status", EnumCheck("status", Enum.GetNames<Reports.ReportDeliveryStatus>())));
            delivery.HasKey(d => d.Id);
            delivery.Property(d => d.Id).ValueGeneratedNever();
            delivery.HasOne<Tenant>().WithMany().HasForeignKey(d => d.TenantId).OnDelete(DeleteBehavior.Restrict);
            delivery.HasOne<Reports.ReportSchedule>().WithMany().HasForeignKey(d => d.ScheduleId).OnDelete(DeleteBehavior.Cascade);
            delivery.HasOne<UserAccount>().WithMany().HasForeignKey(d => d.RecipientId).OnDelete(DeleteBehavior.Cascade);
            delivery.Property(d => d.Status).HasConversion<string>().HasMaxLength(20);
            delivery.Property(d => d.Error).HasMaxLength(Reports.ReportDelivery.ErrorMaxLength);
            // One email per recipient and run, so a run queued twice sends once.
            delivery.HasIndex(d => new { d.ScheduleId, d.RecipientId, d.RunAt }).IsUnique();
            delivery.HasIndex(d => new { d.TenantId, d.CreatedAt });
            delivery.HasIndex(d => d.RecipientId);
            delivery.HasIndex(d => d.NextAttemptAt).HasFilter("status = 'Pending'").HasDatabaseName("ix_report_deliveries_due");
        });
    }

    private static void ConfigureAiFeatures(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AiJob>(job =>
        {
            job.ToTable("ai_jobs", table =>
            {
                table.HasCheckConstraint("ck_ai_jobs_kind", EnumCheck("kind", Enum.GetNames<AiJobKind>()));
                table.HasCheckConstraint("ck_ai_jobs_status", EnumCheck("status", Enum.GetNames<AiJobStatus>()));
            });
            job.HasKey(j => j.Id);
            job.Property(j => j.Id).UseIdentityAlwaysColumn();
            job.HasOne<Tenant>().WithMany().HasForeignKey(j => j.TenantId).OnDelete(DeleteBehavior.Restrict);
            job.HasOne<Ticket>().WithMany().HasForeignKey(j => j.TicketId).OnDelete(DeleteBehavior.Cascade);
            job.HasOne<TicketComment>().WithMany().HasForeignKey(j => j.CommentId).OnDelete(DeleteBehavior.Cascade);
            job.Property(j => j.Kind).HasConversion<string>().HasMaxLength(20);
            job.Property(j => j.Status).HasConversion<string>().HasMaxLength(20);
            job.Property(j => j.LastError).HasMaxLength(AiJob.ErrorMaxLength);
            job.HasIndex(j => new { j.TenantId, j.Status, j.NextAttemptAt });
            job.HasIndex(j => j.TicketId);
            job.HasIndex(j => j.CommentId);
            job.HasIndex(j => j.NextAttemptAt).HasFilter("status = 'Pending'").HasDatabaseName("ix_ai_jobs_due");
        });

        modelBuilder.Entity<AiTriageSuggestion>(suggestion =>
        {
            suggestion.ToTable("ai_triage_suggestions", table =>
            {
                table.HasCheckConstraint("ck_ai_triage_suggestions_status", EnumCheck("status", Enum.GetNames<TriageStatus>()));
                table.HasCheckConstraint("ck_ai_triage_suggestions_levels",
                    "(impact IS NULL OR impact BETWEEN 1 AND 3) AND (urgency IS NULL OR urgency BETWEEN 1 AND 3) "
                    + "AND (final_impact IS NULL OR final_impact BETWEEN 1 AND 3) AND (final_urgency IS NULL OR final_urgency BETWEEN 1 AND 3)");
                table.HasCheckConstraint("ck_ai_triage_suggestions_confidence", "confidence BETWEEN 0 AND 100");
            });
            suggestion.HasKey(s => s.Id);
            suggestion.Property(s => s.Id).ValueGeneratedNever();
            suggestion.HasOne<Tenant>().WithMany().HasForeignKey(s => s.TenantId).OnDelete(DeleteBehavior.Restrict);
            suggestion.HasOne<Ticket>().WithMany().HasForeignKey(s => s.TicketId).OnDelete(DeleteBehavior.Cascade);
            suggestion.HasOne<Category>().WithMany().HasForeignKey(s => s.CategoryId).OnDelete(DeleteBehavior.SetNull);
            suggestion.HasOne<SupportGroup>().WithMany().HasForeignKey(s => s.GroupId).OnDelete(DeleteBehavior.SetNull);
            suggestion.HasOne<Category>().WithMany().HasForeignKey(s => s.FinalCategoryId).OnDelete(DeleteBehavior.SetNull);
            suggestion.HasOne<SupportGroup>().WithMany().HasForeignKey(s => s.FinalGroupId).OnDelete(DeleteBehavior.SetNull);
            suggestion.HasOne<UserAccount>().WithMany().HasForeignKey(s => s.DecidedById).OnDelete(DeleteBehavior.Restrict);
            suggestion.Property(s => s.Reason).HasMaxLength(AiTriageSuggestion.ReasonMaxLength);
            suggestion.Property(s => s.Status).HasConversion<string>().HasMaxLength(20);
            suggestion.Property(s => s.Version).IsRowVersion();
            suggestion.HasIndex(s => new { s.TenantId, s.TicketId });
            // Examples for later suggestions: the latest decided ones.
            suggestion.HasIndex(s => new { s.TenantId, s.DecidedAt }).HasFilter("decided_at IS NOT NULL");
            suggestion.HasIndex(s => s.TicketId);
            suggestion.HasIndex(s => s.CategoryId);
            suggestion.HasIndex(s => s.GroupId);
            suggestion.HasIndex(s => s.FinalCategoryId);
            suggestion.HasIndex(s => s.FinalGroupId);
            suggestion.HasIndex(s => s.DecidedById);
        });

        modelBuilder.Entity<AiTicketInsight>(insight =>
        {
            insight.ToTable("ai_ticket_insights", table =>
            {
                table.HasCheckConstraint("ck_ai_ticket_insights_sentiment", $"sentiment IS NULL OR {EnumCheck("sentiment", Enum.GetNames<AiSentiment>())}");
                table.HasCheckConstraint("ck_ai_ticket_insights_language", "summary_language IS NULL OR summary_language IN ('en', 'ar')");
            });
            insight.HasKey(i => i.TicketId);
            insight.HasOne<Tenant>().WithMany().HasForeignKey(i => i.TenantId).OnDelete(DeleteBehavior.Restrict);
            insight.HasOne<Ticket>().WithOne().HasForeignKey<AiTicketInsight>(i => i.TicketId).OnDelete(DeleteBehavior.Cascade);
            insight.HasOne<TicketComment>().WithMany().HasForeignKey(i => i.SentimentCommentId).OnDelete(DeleteBehavior.SetNull);
            insight.Property(i => i.Sentiment).HasConversion<string>().HasMaxLength(20);
            insight.Property(i => i.SentimentReason).HasMaxLength(AiTicketInsight.ReasonMaxLength);
            insight.Property(i => i.Summary).HasMaxLength(AiTicketInsight.SummaryMaxLength);
            insight.Property(i => i.SummaryLanguage).HasMaxLength(5);
            insight.Property(i => i.SummaryBasis).HasMaxLength(AiTicketInsight.BasisMaxLength);
            insight.Property(i => i.Version).IsRowVersion();
            insight.HasIndex(i => new { i.TenantId, i.FrustratedSince }).HasFilter("frustrated_since IS NOT NULL");
            insight.HasIndex(i => i.SentimentCommentId);
        });

        modelBuilder.Entity<AiDuplicateCandidate>(candidate =>
        {
            candidate.ToTable("ai_duplicate_candidates", table =>
            {
                table.HasCheckConstraint("ck_ai_duplicate_candidates_status", EnumCheck("status", Enum.GetNames<DuplicateStatus>()));
                table.HasCheckConstraint("ck_ai_duplicate_candidates_confidence", "confidence BETWEEN 0 AND 100");
                table.HasCheckConstraint("ck_ai_duplicate_candidates_other", "ticket_id <> candidate_id");
            });
            candidate.HasKey(c => c.Id);
            candidate.Property(c => c.Id).ValueGeneratedNever();
            candidate.HasOne<Tenant>().WithMany().HasForeignKey(c => c.TenantId).OnDelete(DeleteBehavior.Restrict);
            candidate.HasOne<Ticket>().WithMany().HasForeignKey(c => c.TicketId).OnDelete(DeleteBehavior.Cascade);
            candidate.HasOne<Ticket>().WithMany().HasForeignKey(c => c.CandidateId).OnDelete(DeleteBehavior.Cascade);
            candidate.HasOne<UserAccount>().WithMany().HasForeignKey(c => c.DecidedById).OnDelete(DeleteBehavior.Restrict);
            candidate.Property(c => c.Reason).HasMaxLength(AiDuplicateCandidate.ReasonMaxLength);
            candidate.Property(c => c.Status).HasConversion<string>().HasMaxLength(20);
            candidate.Property(c => c.Version).IsRowVersion();
            candidate.HasIndex(c => new { c.TenantId, c.TicketId, c.CandidateId }).IsUnique();
            candidate.HasIndex(c => c.TicketId);
            candidate.HasIndex(c => c.CandidateId);
            candidate.HasIndex(c => c.DecidedById);
        });

        modelBuilder.Entity<AiTranslation>(translation =>
        {
            translation.ToTable("ai_translations", table =>
            {
                table.HasCheckConstraint("ck_ai_translations_source", EnumCheck("source", Enum.GetNames<TranslationSource>()));
                table.HasCheckConstraint("ck_ai_translations_language", "language IN ('en', 'ar')");
            });
            translation.HasKey(t => new { t.TenantId, t.Source, t.SourceId, t.Language });
            translation.HasOne<Tenant>().WithMany().HasForeignKey(t => t.TenantId).OnDelete(DeleteBehavior.Restrict);
            translation.Property(t => t.Source).HasConversion<string>().HasMaxLength(20);
            translation.Property(t => t.Language).HasMaxLength(5);
            translation.Property(t => t.SourceHash).HasMaxLength(64).IsFixedLength();
            translation.Property(t => t.Text).HasMaxLength(AiTranslation.TextMaxLength);
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
            message.Property(m => m.Citations).HasColumnType("uuid[]");
            message.HasIndex(m => new { m.TenantId, m.SessionId, m.Sequence });
        });

        // Task K4 (module 21): agent wellbeing.
        modelBuilder.Entity<Wellbeing.WellbeingDay>(day =>
        {
            day.ToTable("wellbeing_days");
            day.HasKey(d => d.Id);
            day.Property(d => d.Id).ValueGeneratedNever();
            day.HasOne<Tenant>().WithMany().HasForeignKey(d => d.TenantId).OnDelete(DeleteBehavior.Restrict);
            day.HasOne<UserAccount>().WithMany().HasForeignKey(d => d.UserId).OnDelete(DeleteBehavior.Cascade);
            day.HasIndex(d => new { d.TenantId, d.UserId, d.Day }).IsUnique();
            day.HasIndex(d => new { d.TenantId, d.Day });
            day.HasIndex(d => d.UserId);
        });

        modelBuilder.Entity<Wellbeing.WellbeingThreshold>(threshold =>
        {
            threshold.ToTable("wellbeing_thresholds", t =>
            {
                t.HasCheckConstraint("ck_wellbeing_thresholds_load", "max_weighted_load IS NULL OR max_weighted_load BETWEEN 1 AND 1000");
                t.HasCheckConstraint("ck_wellbeing_thresholds_new", "max_new_per_day IS NULL OR max_new_per_day BETWEEN 1 AND 500");
                t.HasCheckConstraint("ck_wellbeing_thresholds_hours", "max_after_hours_hours IS NULL OR max_after_hours_hours BETWEEN 0 AND 168");
                t.HasCheckConstraint("ck_wellbeing_thresholds_difficult", "max_difficult_percent IS NULL OR max_difficult_percent BETWEEN 1 AND 100");
            });
            threshold.HasKey(t => t.GroupId);
            threshold.HasOne<Tenant>().WithMany().HasForeignKey(t => t.TenantId).OnDelete(DeleteBehavior.Restrict);
            threshold.HasOne<Directory.SupportGroup>().WithMany().HasForeignKey(t => t.GroupId).OnDelete(DeleteBehavior.Cascade);
            threshold.HasOne<UserAccount>().WithMany().HasForeignKey(t => t.UpdatedById).OnDelete(DeleteBehavior.SetNull);
            threshold.HasIndex(t => t.TenantId);
            threshold.HasIndex(t => t.UpdatedById);
        });

        modelBuilder.Entity<Wellbeing.WellbeingAlert>(alert =>
        {
            alert.ToTable("wellbeing_alerts", t => t.HasCheckConstraint("ck_wellbeing_alerts_kind", EnumCheck("kind", Enum.GetNames<Wellbeing.WellbeingAlertKind>())));
            alert.HasKey(a => a.Id);
            alert.Property(a => a.Id).ValueGeneratedNever();
            alert.HasOne<Tenant>().WithMany().HasForeignKey(a => a.TenantId).OnDelete(DeleteBehavior.Restrict);
            alert.HasOne<UserAccount>().WithMany().HasForeignKey(a => a.UserId).OnDelete(DeleteBehavior.Cascade);
            alert.HasOne<Directory.SupportGroup>().WithMany().HasForeignKey(a => a.GroupId).OnDelete(DeleteBehavior.Cascade);
            alert.Property(a => a.Kind).HasConversion<string>().HasMaxLength(20);
            // One open alert per agent, group and kind.
            alert.HasIndex(a => new { a.TenantId, a.UserId, a.GroupId, a.Kind }, "ix_wellbeing_alerts_open").IsUnique().HasFilter("ended_at IS NULL");
            alert.HasIndex(a => new { a.TenantId, a.GroupId, a.RaisedAt });
            alert.HasIndex(a => a.UserId);
            alert.HasIndex(a => a.GroupId);
        });

        modelBuilder.Entity<PrivacyNotice>(notice =>
        {
            notice.ToTable("privacy_notices", t => t.HasCheckConstraint("ck_privacy_notices_state", EnumCheck("state", Enum.GetNames<PrivacyNoticeState>())));
            notice.HasKey(n => n.Id);
            notice.Property(n => n.Id).ValueGeneratedNever();
            notice.HasOne<Tenant>().WithMany().HasForeignKey(n => n.TenantId).OnDelete(DeleteBehavior.Restrict);
            notice.HasOne<UserAccount>().WithMany().HasForeignKey(n => n.CreatedById).OnDelete(DeleteBehavior.SetNull);
            notice.HasOne<UserAccount>().WithMany().HasForeignKey(n => n.PublishedById).OnDelete(DeleteBehavior.SetNull);
            notice.ConfigureLocalizedText(n => n.Title, "title", PrivacyNotice.TitleMaxLength);
            notice.ConfigureLocalizedText(n => n.Body, "body", PrivacyNotice.BodyMaxLength);
            notice.Property(n => n.State).HasConversion<string>().HasMaxLength(20);
            notice.Property(n => n.RowVersion).IsRowVersion();
            notice.HasIndex(n => new { n.TenantId, n.Version }).IsUnique();
            // One draft and one published version per tenant.
            notice.HasIndex(n => n.TenantId, "ix_privacy_notices_draft").IsUnique().HasFilter("state = 'Draft'");
            notice.HasIndex(n => n.TenantId, "ix_privacy_notices_published").IsUnique().HasFilter("state = 'Published'");
            notice.HasIndex(n => n.CreatedById);
            notice.HasIndex(n => n.PublishedById);
        });

        modelBuilder.Entity<ConsentPurpose>(purpose =>
        {
            purpose.ToTable("consent_purposes");
            purpose.HasKey(p => p.Id);
            purpose.Property(p => p.Id).ValueGeneratedNever();
            purpose.HasOne<Tenant>().WithMany().HasForeignKey(p => p.TenantId).OnDelete(DeleteBehavior.Restrict);
            purpose.Property(p => p.Key).HasMaxLength(ConsentPurpose.KeyMaxLength);
            purpose.ConfigureLocalizedText(p => p.Name, "name", ConsentPurpose.NameMaxLength);
            purpose.ConfigureLocalizedText(p => p.Description, "description", ConsentPurpose.DescriptionMaxLength);
            purpose.Property(p => p.Version).IsRowVersion();
            purpose.HasIndex(p => new { p.TenantId, p.Key }).IsUnique();
        });

        modelBuilder.Entity<AccountSetupLink>(link =>
        {
            link.ToTable("account_setup_links");
            link.HasKey(l => l.Id);
            link.Property(l => l.Id).ValueGeneratedNever();
            link.HasOne<Tenant>().WithMany().HasForeignKey(l => l.TenantId).OnDelete(DeleteBehavior.Restrict);
            link.HasOne<UserAccount>().WithMany().HasForeignKey(l => l.UserId).OnDelete(DeleteBehavior.Cascade);
            link.Property(l => l.TokenHash).HasMaxLength(64).IsFixedLength();
            link.HasIndex(l => new { l.TenantId, l.TokenHash }).IsUnique();
            link.HasIndex(l => l.UserId);
        });

        modelBuilder.Entity<ConsentRecord>(record =>
        {
            record.ToTable("consent_records", t =>
            {
                t.HasCheckConstraint("ck_consent_records_action", EnumCheck("action", Enum.GetNames<ConsentAction>()));
                t.HasCheckConstraint("ck_consent_records_channel", EnumCheck("channel", Enum.GetNames<ConsentChannel>()));
                t.HasCheckConstraint("ck_consent_records_who", "user_id IS NOT NULL OR visitor_reference IS NOT NULL");
                t.HasCheckConstraint("ck_consent_records_what", "(action = 'NoticeAccepted' AND notice_id IS NOT NULL) OR (action <> 'NoticeAccepted' AND purpose_id IS NOT NULL)");
            });
            record.HasKey(r => r.Id);
            record.Property(r => r.Id).ValueGeneratedNever();
            record.HasOne<Tenant>().WithMany().HasForeignKey(r => r.TenantId).OnDelete(DeleteBehavior.Restrict);
            record.HasOne<UserAccount>().WithMany().HasForeignKey(r => r.UserId).OnDelete(DeleteBehavior.Restrict);
            record.HasOne<UserAccount>().WithMany().HasForeignKey(r => r.RecordedById).OnDelete(DeleteBehavior.Restrict);
            record.HasOne<PrivacyNotice>().WithMany().HasForeignKey(r => r.NoticeId).OnDelete(DeleteBehavior.Restrict);
            record.HasOne<ConsentPurpose>().WithMany().HasForeignKey(r => r.PurposeId).OnDelete(DeleteBehavior.Restrict);
            record.Property(r => r.Action).HasConversion<string>().HasMaxLength(20);
            record.Property(r => r.Channel).HasConversion<string>().HasMaxLength(20);
            record.Property(r => r.VisitorReference).HasMaxLength(ConsentRecord.VisitorReferenceMaxLength);
            record.HasIndex(r => new { r.TenantId, r.UserId, r.CreatedAt });
            record.HasIndex(r => r.UserId);
            record.HasIndex(r => r.RecordedById);
            record.HasIndex(r => r.NoticeId);
            record.HasIndex(r => r.PurposeId);
        });

        modelBuilder.Entity<DataSubjectRequest>(request =>
        {
            request.ToTable("data_subject_requests", t =>
            {
                t.HasCheckConstraint("ck_data_subject_requests_type", EnumCheck("type", Enum.GetNames<DataSubjectRequestType>()));
                t.HasCheckConstraint("ck_data_subject_requests_state", EnumCheck("state", Enum.GetNames<DataSubjectRequestState>()));
                t.HasCheckConstraint("ck_data_subject_requests_channel", EnumCheck("channel", Enum.GetNames<DataSubjectRequestChannel>()));
            });
            request.HasKey(r => r.Id);
            request.Property(r => r.Id).ValueGeneratedNever();
            request.HasOne<Tenant>().WithMany().HasForeignKey(r => r.TenantId).OnDelete(DeleteBehavior.Restrict);
            request.HasOne<UserAccount>().WithMany().HasForeignKey(r => r.SubjectId).OnDelete(DeleteBehavior.Restrict);
            request.HasOne<UserAccount>().WithMany().HasForeignKey(r => r.SubmittedById).OnDelete(DeleteBehavior.SetNull);
            request.HasOne<UserAccount>().WithMany().HasForeignKey(r => r.HandlerId).OnDelete(DeleteBehavior.SetNull);
            request.Property(r => r.Number).HasMaxLength(30);
            request.Property(r => r.Type).HasConversion<string>().HasMaxLength(20);
            request.Property(r => r.State).HasConversion<string>().HasMaxLength(20);
            request.Property(r => r.Channel).HasConversion<string>().HasMaxLength(20);
            request.Property(r => r.Details).HasMaxLength(DataSubjectRequest.TextMaxLength);
            request.Property(r => r.IdentityCheck).HasMaxLength(DataSubjectRequest.TextMaxLength);
            request.Property(r => r.Resolution).HasMaxLength(DataSubjectRequest.TextMaxLength);
            request.Property(r => r.Version).IsRowVersion();
            request.HasIndex(r => new { r.TenantId, r.Sequence }).IsUnique();
            request.HasIndex(r => new { r.TenantId, r.State, r.DueAt });
            request.HasIndex(r => r.SubjectId);
            request.HasIndex(r => r.SubmittedById);
            request.HasIndex(r => r.HandlerId);
        });
    }

    private static string EnumCheck(string column, IEnumerable<string> values) =>
        $"{column} IN ({string.Join(", ", values.Select(v => $"'{v}'"))})";
}
