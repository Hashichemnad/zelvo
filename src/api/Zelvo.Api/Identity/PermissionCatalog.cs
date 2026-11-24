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
                Permissions.KnowledgeRead, Permissions.KnowledgeWrite],
            new Dictionary<string, FieldAccess> { ["users.email"] = FieldAccess.Read }),
        new(BuiltInRoles.TeamLead, new LocalizedText("Team lead", "قائد الفريق"),
            [Permissions.UsersRead, Permissions.IncidentsRead, Permissions.IncidentsCreate, Permissions.IncidentsEdit, Permissions.IncidentsAssign, Permissions.IncidentsMajor, Permissions.DirectoryRead,
                Permissions.KnowledgeRead, Permissions.KnowledgeWrite, Permissions.KnowledgePublish],
            new Dictionary<string, FieldAccess> { ["users.email"] = FieldAccess.Read }),
        new(BuiltInRoles.Approver, new LocalizedText("Approver", "المعتمِد"), [], new Dictionary<string, FieldAccess>()),
        new(BuiltInRoles.TenantAdmin, new LocalizedText("Tenant administrator", "مسؤول الجهة"),
            All,
            new Dictionary<string, FieldAccess> { ["users.email"] = FieldAccess.Read, ["users.emiratesId"] = FieldAccess.Edit }),
        new(BuiltInRoles.ExecutiveAuditor, new LocalizedText("Executive / auditor", "تنفيذي / مدقق"),
            [Permissions.AuditRead, Permissions.UsersRead, Permissions.IncidentsRead, Permissions.DirectoryRead, Permissions.KnowledgeRead],
            new Dictionary<string, FieldAccess> { ["users.email"] = FieldAccess.Read }),
    ];

    /// <summary>Permissions the tenant administrator role always keeps, so a tenant cannot lock itself out.</summary>
    public static IReadOnlySet<string> TenantAdminLocked { get; } =
        new HashSet<string>(StringComparer.Ordinal) { Permissions.UsersManage, Permissions.RolesManage };

    public static bool IsPermission(string key) => All.Contains(key, StringComparer.Ordinal);

    public static ResourceDefinition? Resource(string key) => Resources.FirstOrDefault(r => r.Key == key);
}
