using Zelvo.Data.Identity;
using Zelvo.Data.Localization;
using Zelvo.Tickets;

namespace Zelvo.Api.Identity;

/// <summary>Permission keys. Each module adds its own; what a role holds is data.</summary>
internal static class Permissions
{
    /// <summary>Change the tenant's name.</summary>
    public const string TenantManage = "tenant.manage";

    /// <summary>See the missing-translation report.</summary>
    public const string TranslationsRead = "translations.read";

    /// <summary>Read and verify the audit log.</summary>
    public const string AuditRead = "audit.read";

    /// <summary>See users.</summary>
    public const string UsersRead = "users.read";

    /// <summary>Change users' names, status and roles.</summary>
    public const string UsersManage = "users.manage";

    /// <summary>See and change roles, their permissions and field rules.</summary>
    public const string RolesManage = "roles.manage";

    /// <summary>Change sign-in settings (who may sign in with an email code).</summary>
    public const string IdentityManage = "identity.manage";

    /// <summary>Change tenant settings such as the default language, calendar and digits.</summary>
    public const string SettingsManage = "settings.manage";

    /// <summary>See the search index status and rebuild the index (Settings → Search).</summary>
    public const string SearchManage = "search.manage";

    /// <summary>Demo installations only: "Reset demo" (task D). Has no effect elsewhere.</summary>
    public const string DemoReset = "demo.reset";

    /// <summary>See every incident (module 1). Requesters always see their own without it.</summary>
    public const string IncidentsRead = IncidentPermissions.Read;

    /// <summary>Log incidents on behalf of others, from any channel.</summary>
    public const string IncidentsCreate = IncidentPermissions.Create;

    /// <summary>Change incidents: details, state, parent and child links.</summary>
    public const string IncidentsEdit = IncidentPermissions.Edit;

    /// <summary>Assign incidents to agents.</summary>
    public const string IncidentsAssign = IncidentPermissions.Assign;

    /// <summary>Declare major incidents and post to their communication timeline.</summary>
    public const string IncidentsMajor = IncidentPermissions.Major;

    /// <summary>See groups and their members, skills, inactive categories and locations, and assignment rules (task B2).</summary>
    public const string DirectoryRead = "directory.read";

    /// <summary>Change categories, locations, skills, groups and their members, and assignment rules (task B2).</summary>
    public const string DirectoryManage = "directory.manage";

    /// <summary>
    /// Change business calendars, customer groups, SLA policies and the calendars of groups (task B3).
    /// Staff with <see cref="DirectoryRead"/> can read them.
    /// </summary>
    public const string SlaManage = "sla.manage";

    /// <summary>
    /// Settings → Integrations (module 14): API keys, OAuth clients, webhooks and the integration
    /// log. Never available to integrations themselves.
    /// </summary>
    public const string IntegrationsManage = "integrations.manage";

    /// <summary>
    /// Settings → AI (module 9): providers and their keys, model per feature, prices, the monthly
    /// limit, usage and the AI log. Never available to integrations themselves.
    /// </summary>
    public const string AiManage = "ai.manage";

    /// <summary>Read internal knowledge articles and the drafts of others (module 7). External articles need no permission.</summary>
    public const string KnowledgeRead = "knowledge.read";

    /// <summary>Write and change knowledge articles and submit them for review.</summary>
    public const string KnowledgeWrite = "knowledge.write";

    /// <summary>Approve or send back articles written by others, and retire articles.</summary>
    public const string KnowledgePublish = "knowledge.publish";

    /// <summary>
    /// Settings → Notifications (module 10, task B5): which events notify people, their email
    /// wording in English and Arabic, test emails and the email delivery log.
    /// </summary>
    public const string NotificationsManage = "notifications.manage";

    /// <summary>
    /// Build and publish forms and field sets, and choose which form each record type and category
    /// uses (task C1, module 2). Anyone may fill in a published form they are given.
    /// </summary>
    public const string FormsManage = "forms.manage";

    /// <summary>
    /// Agent workspace (module 5, task B4): make and change canned responses for everyone in the
    /// organisation. Team leads manage their groups' responses without it; everyone who works on
    /// tickets keeps personal ones.
    /// </summary>
    public const string ResponsesManage = "responses.manage";

    /// <summary>
    /// Agent workspace (task B4): share saved views with everyone in the organisation. Team leads
    /// share views with their groups without it.
    /// </summary>
    public const string WorkspaceViewsShare = "workspace.views.share";

    /// <summary>
    /// Settings → Mailboxes and Settings → Live chat (module 6, task E2): mailboxes with their
    /// credentials, who may raise tickets by email, the mailbox log, and chat widgets with the
    /// websites that may embed them. Never available to integrations (mailbox credentials).
    /// </summary>
    public const string ChannelsManage = "channels.manage";

    /// <summary>
    /// Settings → Connectors and Settings → Monitoring (module 14, task E7): identity directories,
    /// CRMs and REST connectors with their credentials, and monitoring sources with their tokens.
    /// Never available to integrations (connector credentials, and a connector could send ticket
    /// data to a system of its own).
    /// </summary>
    public const string ConnectorsManage = "connectors.manage";

    /// <summary>
    /// Settings → Branding (module 11, task F1): product name, logos, favicon, sign-in image,
    /// colours and font with the contrast checks, publishing, custom domains with their
    /// certificates, and the email sender domain. Not for integrations (for approval): a
    /// certificate's private key and the DKIM key are set here.
    /// </summary>
    public const string BrandingManage = "branding.manage";

    /// <summary>Answer live chats of one's groups: accept, reply, transfer, end, and make tickets from them (task E2).</summary>
    public const string ChatHandle = "chat.handle";

    /// <summary>
    /// Build the whole service catalog (task C2, module 2): catalogs and their editors, services in
    /// every catalog, templates, bulk import and export. Editors named on a catalog write that
    /// catalog's services without it.
    /// </summary>
    public const string CatalogManage = "catalog.manage";

    /// <summary>Approve or send back service versions written by others (four-eyes), suspend, resume and retire services.</summary>
    public const string CatalogPublish = "catalog.publish";

    /// <summary>See every service request. Requesters always see their own without it.</summary>
    public const string RequestsRead = "requests.read";

    /// <summary>Work on service requests: change their state.</summary>
    public const string RequestsEdit = "requests.edit";

    /// <summary>Assign service requests to groups and agents.</summary>
    public const string RequestsAssign = "requests.assign";

    /// <summary>
    /// Send dates and events that start proactive services for people (task C5), usually given to
    /// an integration such as the HR system.
    /// </summary>
    public const string ProactiveTrigger = "proactive.trigger";

    /// <summary>Settings → Payments: the switch and the gateways with their credentials (task E8).</summary>
    public const string PaymentsManage = "payments.manage";

    /// <summary>See every payment, receipts, reconciliation and the Excel export (finance; task E8).</summary>
    public const string PaymentsRead = "payments.read";

    /// <summary>Refund payments, through the gateway or recorded as manual (task E8).</summary>
    public const string PaymentsRefund = "payments.refund";

    /// <summary>See every complaint, inquiry and suggestion (task C6). Requesters always see their own.</summary>
    public const string VoiceRead = Zelvo.Tickets.VoicePermissions.Read;

    /// <summary>Work on complaints, inquiries and suggestions: states, severity, root cause, reviews, conversions, replies and notes.</summary>
    public const string VoiceEdit = Zelvo.Tickets.VoicePermissions.Edit;

    /// <summary>Assign complaints, inquiries and suggestions to groups and agents.</summary>
    public const string VoiceAssign = Zelvo.Tickets.VoicePermissions.Assign;

    /// <summary>Happiness and customer-voice reporting (GOV-3).</summary>
    public const string VoiceReport = Zelvo.Tickets.VoicePermissions.Report;

    /// <summary>Dashboards of the groups the person leads and their agents (module 10, task F2).</summary>
    public const string ReportsRead = Zelvo.Tickets.Reports.ReportPermissions.Read;

    /// <summary>Dashboards of the whole tenant: every group and agent (task F2).</summary>
    public const string ReportsAll = Zelvo.Tickets.Reports.ReportPermissions.All;

    /// <summary>Scheduled reports for other people, every schedule of the tenant, and the report targets (task F2).</summary>
    public const string ReportsManage = Zelvo.Tickets.Reports.ReportPermissions.Manage;

    /// <summary>The setup wizard and department templates (module 10, task F2).</summary>
    public const string SetupManage = "setup.manage";

    /// <summary>
    /// The installation's license (module 12, task F3) and update packages (module 13, task F4).
    /// These belong to the whole installation, so they apply only in the installation's operator
    /// tenant (<c>Installation:OperatorTenant</c>, else <c>Tenancy:DefaultTenant</c>).
    /// </summary>
    public const string InstallationManage = "installation.manage";

    /// <summary>Settings → Customer voice: anonymous submission, severities, root causes and the happiness meter (GOV-2).</summary>
    public const string VoiceManage = Zelvo.Tickets.VoicePermissions.Manage;
    /// <summary>
    /// Build workflow rules (task C3, module 10): when / if / then rules that assign, ask for
    /// approval and notify. Holders also approve when a level names nobody who can.
    /// </summary>
    public const string WorkflowsManage = "workflows.manage";

    /// <summary>See every approval process and decision (task C3). Approvers always see their own.</summary>
    public const string ApprovalsRead = "approvals.read";

    /// <summary>
    /// Settings → Privacy (GOV-5, task F5): the privacy notice and its versions, optional consent
    /// purposes, retention periods and the time to answer data subject requests.
    /// </summary>
    public const string PrivacyManage = "privacy.manage";

    /// <summary>
    /// Handle data subject requests (task F5): log them for people, download a copy of a person's
    /// data, complete, refuse, and erase a person.
    /// </summary>
    public const string PrivacyRequests = "privacy.requests";

    /// <summary>Read people's consent to the optional purposes and the notice they accepted (task F5), for example for a survey tool.</summary>
    public const string PrivacyConsentsRead = "privacy.consents.read";

    /// <summary>Task H1 (module 16): see problems, their root-cause analysis and links, and the known error database.</summary>
    public const string ProblemsRead = Zelvo.Tickets.ProblemPermissions.Read;

    /// <summary>Task H1: raise problems (from incidents or by hand), write the analysis and known errors, link incidents, items and articles.</summary>
    public const string ProblemsEdit = Zelvo.Tickets.ProblemPermissions.Edit;

    /// <summary>
    /// Task H1, the problem manager (product owner decision 11 of 2026-11-26): assign problems,
    /// record known errors, resolve and close problems with their incidents, publish and retire known errors.
    /// </summary>
    public const string ProblemsManage = Zelvo.Tickets.ProblemPermissions.Manage;

    /// <summary>Task G1 (module 18): see configuration items, their relationships, map, impact and history.</summary>
    public const string CmdbRead = "cmdb.read";

    /// <summary>Task G1 (module 18): add, change and retire configuration items and relationships; Settings → CMDB.</summary>
    public const string CmdbManage = "cmdb.manage";

    /// <summary>
    /// Task G2 (module 18): discovery sources (Intune, Entra ID devices, Azure, Active Directory,
    /// vCenter and the discovery API), which read other systems with connectors' credentials.
    /// </summary>
    public const string CmdbDiscoveryManage = "cmdb.discovery.manage";

    /// <summary>Task G3 (module 18): see assets, contracts, warranties, software licences and compliance; print labels.</summary>
    public const string AssetsRead = "assets.read";

    /// <summary>Task G3 (module 18): manage assets and their lifecycle, contracts, warranties and licences; Settings → Assets.</summary>
    public const string AssetsManage = "assets.manage";

    /// <summary>
    /// The wellbeing dashboard (module 21, task K4; product owner decision 2026-11-26): team leads
    /// see each agent of the groups they lead, others with this permission only group totals.
    /// Every agent sees their own figures without it.
    /// </summary>
    public const string WellbeingRead = "wellbeing.read";

    /// <summary>Settings → Wellbeing (task K4): turn wellbeing on or off and set the default thresholds.</summary>
    public const string WellbeingManage = "wellbeing.manage";
}

/// <summary>A field of a resource that field rules can hide or make read-only.</summary>
/// <param name="Key">Field key as it appears in API responses (camelCase).</param>
/// <param name="Sensitive">Personal or secret data: hidden unless a role is given access.</param>
/// <param name="Writable">Whether the API ever accepts changes to it.</param>
internal sealed record FieldDefinition(string Key, bool Sensitive = false, bool Writable = false);

/// <summary>A resource with field-level access control.</summary>
/// <param name="Key">Resource key, for example <c>users</c>.</param>
/// <param name="ReadPermission">Permission that lets a role read non-sensitive fields.</param>
/// <param name="EditPermission">Permission that lets a role change writable non-sensitive fields.</param>
/// <param name="Fields">Fields under field-level control. Ids and timestamps of the record itself are always shown.</param>
internal sealed record ResourceDefinition(string Key, string ReadPermission, string EditPermission, IReadOnlyList<FieldDefinition> Fields)
{
    public FieldDefinition? Field(string key) => Fields.FirstOrDefault(f => f.Key == key);
}

/// <summary>Default grants of a built-in role, applied once per tenant.</summary>
internal sealed record RoleDefaults(
    string Key,
    LocalizedText Name,
    IReadOnlyList<string> Permissions,
    IReadOnlyDictionary<string, FieldAccess> FieldRules);

/// <summary>
/// Everything that can be granted: permission keys, resources with fields, and the defaults of the
/// six persona roles. Least privilege: anything not granted is denied, and sensitive fields are
/// hidden unless a role is given access. Later modules add their permissions and defaults here;
/// <see cref="IdentityProvisioning"/> gives new defaults to existing tenants once.
/// </summary>
internal static class PermissionCatalog
{
    public static IReadOnlyList<string> All { get; } =
    [
        Permissions.TenantManage,
        Permissions.TranslationsRead,
        Permissions.AuditRead,
        Permissions.UsersRead,
        Permissions.UsersManage,
        Permissions.RolesManage,
        Permissions.IdentityManage,
        Permissions.SettingsManage,
        Permissions.SearchManage,
        Permissions.DemoReset,
        Permissions.IncidentsRead,
        Permissions.IncidentsCreate,
        Permissions.IncidentsEdit,
        Permissions.IncidentsAssign,
        Permissions.IncidentsMajor,
        Permissions.IntegrationsManage,
        Permissions.DirectoryRead,
        Permissions.DirectoryManage,
        Permissions.AiManage,
        Permissions.KnowledgeRead,
        Permissions.KnowledgeWrite,
        Permissions.KnowledgePublish,
        Permissions.SlaManage,
        Permissions.NotificationsManage,
        Permissions.FormsManage,
        Permissions.ResponsesManage,
        Permissions.WorkspaceViewsShare,
        Permissions.ChannelsManage,
        Permissions.ConnectorsManage,
        Permissions.ChatHandle,
        Permissions.CatalogManage,
        Permissions.CatalogPublish,
        Permissions.RequestsRead,
        Permissions.RequestsEdit,
        Permissions.RequestsAssign,
        Permissions.ProactiveTrigger,
        Permissions.PaymentsManage,
        Permissions.PaymentsRead,
        Permissions.PaymentsRefund,
        Permissions.VoiceRead,
        Permissions.VoiceEdit,
        Permissions.VoiceAssign,
        Permissions.VoiceReport,
        Permissions.VoiceManage,
        Permissions.BrandingManage,
        Permissions.WorkflowsManage,
        Permissions.ApprovalsRead,
        Permissions.ReportsRead,
        Permissions.ReportsAll,
        Permissions.ReportsManage,
        Permissions.SetupManage,
        Permissions.PrivacyManage,
        Permissions.PrivacyRequests,
        Permissions.PrivacyConsentsRead,
        Permissions.InstallationManage,
        Permissions.CmdbRead,
        Permissions.CmdbManage,
        Permissions.CmdbDiscoveryManage,
        Permissions.AssetsRead,
        Permissions.AssetsManage,
        Permissions.WellbeingRead,
        Permissions.WellbeingManage,
        Permissions.ProblemsRead,
        Permissions.ProblemsEdit,
        Permissions.ProblemsManage,
    ];

    /// <summary>
    /// Permissions an API key or OAuth client can never use, whatever its role (product owner
    /// decision 2026-11-22): a leaked key must not be able to take over the tenant.
    /// </summary>
    public static IReadOnlySet<string> NotForIntegrations { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        Permissions.UsersManage, Permissions.RolesManage, Permissions.IdentityManage, Permissions.IntegrationsManage,
        // Not in the 2026-11-22 decision; added with E4 for the product owner's approval: an
        // integration could point a provider at its own endpoint and receive the stored key's use.
        Permissions.AiManage,
        // Added with B5 for the product owner's approval: templates reach every person's mailbox,
        // so a leaked key could turn them into convincing phishing.
        Permissions.NotificationsManage,
        // Task E2, for the product owner's approval: mailbox settings hold credentials, and an
        // integration could point a mailbox at a server of its own.
        Permissions.ChannelsManage,
        // Task E7, for the product owner's approval: connector credentials, and connectors send ticket data out.
        Permissions.ConnectorsManage,
        // Task F1, for the product owner's approval: certificates' private keys, DKIM keys and the
        // domains the installation answers for are set here.
        Permissions.BrandingManage,
        // Task C3, for the product owner's approval: rules send wording of their own to anyone,
        // like notification templates, and could route requests past their approvers.
        Permissions.WorkflowsManage,
        // Task F2, for the product owner's approval: department templates create groups, SLA
        // policies and assignment rules in bulk.
        Permissions.SetupManage,
        // Task E8, for the product owner's approval: gateways hold credentials, and an integration
        // could point a gateway at an address of its own.
        Permissions.PaymentsManage,
        // Task F5, for the product owner's approval: the privacy notice reaches everyone, and a
        // leaked key must never be able to download or erase people's data.
        Permissions.PrivacyManage,
        Permissions.PrivacyRequests,
        // Task F3, for the product owner's approval: licenses and update packages change the
        // whole installation, and an update stops it for a while.
        Permissions.InstallationManage,
        // Task K4, for the product owner's approval: people's workload figures are personal data
        // (UAE PDPL) and are for the people who balance work, never for other systems.
        Permissions.WellbeingRead,
        Permissions.WellbeingManage,
        // Task G2, for the product owner's approval: discovery sources use connectors' stored
        // credentials and the vCenter password (pushing records through the discovery API needs
        // only cmdb.manage, which integrations may hold).
        Permissions.CmdbDiscoveryManage,
    };

    /// <summary>Permissions an integration may be given as scopes.</summary>
    public static IReadOnlyList<string> IntegrationScopes { get; } = [.. All.Where(p => !NotForIntegrations.Contains(p))];

    public static ResourceDefinition Users { get; } = new(
        "users",
        Permissions.UsersRead,
        Permissions.UsersManage,
        [
            new FieldDefinition("displayName", Writable: true),
            new FieldDefinition("email", Sensitive: true),
            // Recorded by administrators to match UAE PASS sign-ins (GOV-1).
            new FieldDefinition("emiratesId", Sensitive: true, Writable: true),
            new FieldDefinition("status", Writable: true),
            new FieldDefinition("roles"),
            new FieldDefinition("lastSeenAt"),
            // Task B2: what round-robin looks at.
            new FieldDefinition("skills", Writable: true),
            new FieldDefinition("languages", Writable: true),
            new FieldDefinition("availableForAssignment", Writable: true),
            new FieldDefinition("groups"),
            // Task C1: profile fields that forms fill in.
            new FieldDefinition("department", Writable: true),
            new FieldDefinition("manager", Writable: true),
            new FieldDefinition("location", Writable: true),
            new FieldDefinition("employeeId", Writable: true),
            new FieldDefinition("phone", Sensitive: true, Writable: true),
        ]);

    /// <summary>Incidents (module 1), as staff see them. Requesters see their own through a fixed, smaller view.</summary>
    public static ResourceDefinition Incidents { get; } = new(
        "incidents",
        Permissions.IncidentsRead,
        Permissions.IncidentsEdit,
        [
            new FieldDefinition("title", Writable: true),
            new FieldDefinition("description", Writable: true),
            new FieldDefinition("impact", Writable: true),
            new FieldDefinition("urgency", Writable: true),
            new FieldDefinition("priority"),
            new FieldDefinition("requester"),
            new FieldDefinition("assignee"),
            new FieldDefinition("channel"),
            new FieldDefinition("resolutionNotes"),
            // Task B2.
            new FieldDefinition("category", Writable: true),
            new FieldDefinition("location", Writable: true),
            new FieldDefinition("group"),
            // Task B3: response and resolution clocks.
            new FieldDefinition("sla"),
        ]);

    public static IReadOnlyList<ResourceDefinition> Resources { get; } = [Users, Incidents];

    public static IReadOnlyList<RoleDefaults> Roles { get; } =
    [
        new(BuiltInRoles.Requester, new LocalizedText("Requester", "مقدّم الطلب"), [], new Dictionary<string, FieldAccess>()),
        new(BuiltInRoles.Agent, new LocalizedText("Agent", "موظف الدعم"),
            [Permissions.UsersRead, Permissions.IncidentsRead, Permissions.IncidentsCreate, Permissions.IncidentsEdit, Permissions.IncidentsAssign, Permissions.DirectoryRead,
                Permissions.KnowledgeRead, Permissions.KnowledgeWrite, Permissions.RequestsRead, Permissions.RequestsEdit, Permissions.RequestsAssign, Permissions.ChatHandle,
                Permissions.VoiceRead, Permissions.VoiceEdit, Permissions.VoiceAssign, Permissions.CmdbRead, Permissions.AssetsRead, Permissions.ProblemsRead, Permissions.ProblemsEdit],
            new Dictionary<string, FieldAccess> { ["users.email"] = FieldAccess.Read }),
        new(BuiltInRoles.TeamLead, new LocalizedText("Team lead", "قائد الفريق"),
            [Permissions.UsersRead, Permissions.IncidentsRead, Permissions.IncidentsCreate, Permissions.IncidentsEdit, Permissions.IncidentsAssign, Permissions.IncidentsMajor, Permissions.DirectoryRead,
                Permissions.KnowledgeRead, Permissions.KnowledgeWrite, Permissions.KnowledgePublish, Permissions.RequestsRead, Permissions.RequestsEdit, Permissions.RequestsAssign, Permissions.ChatHandle,
                Permissions.VoiceRead, Permissions.VoiceEdit, Permissions.VoiceAssign, Permissions.VoiceReport, Permissions.ReportsRead, Permissions.CmdbRead, Permissions.AssetsRead, Permissions.WellbeingRead,
                Permissions.ProblemsRead, Permissions.ProblemsEdit, Permissions.ProblemsManage],
            new Dictionary<string, FieldAccess> { ["users.email"] = FieldAccess.Read }),
        new(BuiltInRoles.Approver, new LocalizedText("Approver", "المعتمِد"), [], new Dictionary<string, FieldAccess>()),
        new(BuiltInRoles.TenantAdmin, new LocalizedText("Tenant administrator", "مسؤول الجهة"),
            All,
            new Dictionary<string, FieldAccess> { ["users.email"] = FieldAccess.Read, ["users.emiratesId"] = FieldAccess.Edit, ["users.phone"] = FieldAccess.Edit }),
        new(BuiltInRoles.ExecutiveAuditor, new LocalizedText("Executive / auditor", "تنفيذي / مدقق"),
            [Permissions.AuditRead, Permissions.UsersRead, Permissions.IncidentsRead, Permissions.DirectoryRead, Permissions.KnowledgeRead, Permissions.RequestsRead,
                Permissions.VoiceRead, Permissions.VoiceReport, Permissions.ApprovalsRead, Permissions.ReportsRead, Permissions.ReportsAll, Permissions.PaymentsRead,
                Permissions.CmdbRead, Permissions.AssetsRead, Permissions.WellbeingRead, Permissions.ProblemsRead],
            new Dictionary<string, FieldAccess> { ["users.email"] = FieldAccess.Read }),
    ];

    /// <summary>Permissions the tenant administrator role always keeps, so a tenant cannot lock itself out.</summary>
    public static IReadOnlySet<string> TenantAdminLocked { get; } =
        new HashSet<string>(StringComparer.Ordinal) { Permissions.UsersManage, Permissions.RolesManage };

    public static bool IsPermission(string key) => All.Contains(key, StringComparer.Ordinal);

    /// <summary>
    /// The licensed module a permission belongs to (task F3): without the module in the license the
    /// permission does not apply. Permissions of the shared platform (users, roles, settings,
    /// directory, forms, notifications, workflow rules, setup, privacy (GOV-5), license and updates) belong to none.
    /// </summary>
    public static string? ModuleOf(string permission) => permission switch
    {
        Permissions.IncidentsRead or Permissions.IncidentsCreate or Permissions.IncidentsEdit or Permissions.IncidentsAssign or Permissions.IncidentsMajor
            => Zelvo.Licensing.LicensedModules.Incidents,
        Permissions.CatalogManage or Permissions.CatalogPublish or Permissions.RequestsRead or Permissions.RequestsEdit or Permissions.RequestsAssign
            or Permissions.ProactiveTrigger => Zelvo.Licensing.LicensedModules.ServiceCatalog,
        Permissions.VoiceRead or Permissions.VoiceEdit or Permissions.VoiceAssign or Permissions.VoiceReport or Permissions.VoiceManage
            => Zelvo.Licensing.LicensedModules.CustomerVoice,
        Permissions.ResponsesManage or Permissions.WorkspaceViewsShare => Zelvo.Licensing.LicensedModules.AgentWorkspace,
        Permissions.ChannelsManage or Permissions.ChatHandle => Zelvo.Licensing.LicensedModules.Omnichannel,
        Permissions.KnowledgeRead or Permissions.KnowledgeWrite or Permissions.KnowledgePublish => Zelvo.Licensing.LicensedModules.KnowledgeBase,
        Permissions.SlaManage => Zelvo.Licensing.LicensedModules.Sla,
        Permissions.AiManage => Zelvo.Licensing.LicensedModules.Ai,
        Permissions.ReportsRead or Permissions.ReportsAll or Permissions.ReportsManage => Zelvo.Licensing.LicensedModules.NotificationsReporting,
        Permissions.BrandingManage => Zelvo.Licensing.LicensedModules.Branding,
        // Task E7: connectors are part of module 14.
        Permissions.IntegrationsManage or Permissions.ConnectorsManage => Zelvo.Licensing.LicensedModules.Integrations,
        Permissions.PaymentsManage or Permissions.PaymentsRead or Permissions.PaymentsRefund => Zelvo.Licensing.LicensedModules.Payments,
        _ => null,
    };

    public static ResourceDefinition? Resource(string key) => Resources.FirstOrDefault(r => r.Key == key);
}
