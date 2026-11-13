using Zelvo.Data.Identity;
using Zelvo.Data.Localization;

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
    ];

    public static ResourceDefinition Users { get; } = new(
        "users",
        Permissions.UsersRead,
        Permissions.UsersManage,
        [
            new FieldDefinition("displayName", Writable: true),
            new FieldDefinition("email", Sensitive: true),
            new FieldDefinition("status", Writable: true),
            new FieldDefinition("roles"),
            new FieldDefinition("lastSeenAt"),
        ]);

    public static IReadOnlyList<ResourceDefinition> Resources { get; } = [Users];

    public static IReadOnlyList<RoleDefaults> Roles { get; } =
    [
        new(BuiltInRoles.Requester, new LocalizedText("Requester", "مقدّم الطلب"), [], new Dictionary<string, FieldAccess>()),
        new(BuiltInRoles.Agent, new LocalizedText("Agent", "موظف الدعم"),
            [Permissions.UsersRead],
            new Dictionary<string, FieldAccess> { ["users.email"] = FieldAccess.Read }),
        new(BuiltInRoles.TeamLead, new LocalizedText("Team lead", "قائد الفريق"),
            [Permissions.UsersRead],
            new Dictionary<string, FieldAccess> { ["users.email"] = FieldAccess.Read }),
        new(BuiltInRoles.Approver, new LocalizedText("Approver", "المعتمِد"), [], new Dictionary<string, FieldAccess>()),
        new(BuiltInRoles.TenantAdmin, new LocalizedText("Tenant administrator", "مسؤول الجهة"),
            All,
            new Dictionary<string, FieldAccess> { ["users.email"] = FieldAccess.Read }),
        new(BuiltInRoles.ExecutiveAuditor, new LocalizedText("Executive / auditor", "تنفيذي / مدقق"),
            [Permissions.AuditRead, Permissions.UsersRead],
            new Dictionary<string, FieldAccess> { ["users.email"] = FieldAccess.Read }),
    ];

    /// <summary>Permissions the tenant administrator role always keeps, so a tenant cannot lock itself out.</summary>
    public static IReadOnlySet<string> TenantAdminLocked { get; } =
        new HashSet<string>(StringComparer.Ordinal) { Permissions.UsersManage, Permissions.RolesManage };

    public static bool IsPermission(string key) => All.Contains(key, StringComparer.Ordinal);

    public static ResourceDefinition? Resource(string key) => Resources.FirstOrDefault(r => r.Key == key);
}
