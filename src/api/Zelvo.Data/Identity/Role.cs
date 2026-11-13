using Zelvo.Data.Auditing;
using Zelvo.Data.Localization;
using Zelvo.Data.Tenancy;

namespace Zelvo.Data.Identity;

/// <summary>
/// A set of permissions and field rules given to users. Every tenant has the six built-in
/// persona roles (<see cref="BuiltInRoles"/>); what each role may do is data, changed by tenant
/// administrators.
/// </summary>
public sealed class Role : ITenantOwned, IAuditedEntity
{
    /// <summary>Maximum length of <see cref="Key"/>.</summary>
    public const int KeyMaxLength = 50;

    /// <summary>Maximum length of each language of <see cref="Name"/>.</summary>
    public const int NameMaxLength = 100;

    /// <summary>Unique id (UUID version 7).</summary>
    public Guid Id { get; init; } = Guid.CreateVersion7();

    /// <inheritdoc />
    public Guid TenantId { get; set; }

    /// <summary>Stable key, for example <c>agent</c>; unique in the tenant.</summary>
    public required string Key { get; init; }

    /// <summary>Display name in English and Arabic.</summary>
    public required LocalizedText Name { get; set; }

    /// <summary>A built-in persona role: cannot be deleted or renamed by key.</summary>
    public bool IsSystem { get; init; }

    /// <summary>
    /// Users must have signed in with a second factor before this role's permissions apply.
    /// Always on for agents, team leads and tenant administrators.
    /// </summary>
    public bool RequiresMfa { get; set; }

    /// <summary>When the role was created.</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>When the role was last changed.</summary>
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Permissions of the role.</summary>
    public List<RoleGrant> Permissions { get; init; } = [];

    /// <summary>Field rules of the role.</summary>
    public List<RoleFieldRule> FieldRules { get; init; } = [];

    /// <summary>Optimistic concurrency token (PostgreSQL <c>xmin</c>).</summary>
    public uint Version { get; init; }
}

/// <summary>A permission given to a role (table <c>role_permissions</c>). Keys come from the permission catalog in code.</summary>
public sealed class RoleGrant : ITenantOwned, IAuditedEntity
{
    /// <summary>Maximum length of <see cref="Permission"/>.</summary>
    public const int PermissionMaxLength = 100;

    /// <inheritdoc />
    public Guid TenantId { get; set; }

    /// <summary>The role.</summary>
    public Guid RoleId { get; init; }

    /// <summary>Permission key, for example <c>users.read</c>.</summary>
    public required string Permission { get; init; }
}

/// <summary>What a role may do with one field of a resource.</summary>
public sealed class RoleFieldRule : ITenantOwned, IAuditedEntity
{
    /// <summary>Maximum length of <see cref="Resource"/> and <see cref="Field"/>.</summary>
    public const int NameMaxLength = 100;

    /// <inheritdoc />
    public Guid TenantId { get; set; }

    /// <summary>The role.</summary>
    public Guid RoleId { get; init; }

    /// <summary>Resource key, for example <c>users</c>.</summary>
    public required string Resource { get; init; }

    /// <summary>Field key, for example <c>email</c>.</summary>
    public required string Field { get; init; }

    /// <summary>Access the role has to the field.</summary>
    public FieldAccess Access { get; set; }
}

/// <summary>Access to one field. Higher values include the lower ones.</summary>
public enum FieldAccess
{
    /// <summary>Not returned by the API.</summary>
    Hidden = 0,

    /// <summary>Returned, but changes are refused.</summary>
    Read = 1,

    /// <summary>Returned and may be changed.</summary>
    Edit = 2,
}

/// <summary>
/// Remembers which default grants of a built-in role were already given to a tenant, so later
/// releases add only new defaults and never undo an administrator's change.
/// </summary>
public sealed class RoleDefaultGrant : ITenantOwned
{
    /// <summary>Maximum length of <see cref="Grant"/>.</summary>
    public const int GrantMaxLength = 210;

    /// <inheritdoc />
    public Guid TenantId { get; set; }

    /// <summary>Built-in role key.</summary>
    public required string RoleKey { get; init; }

    /// <summary><c>permission:&lt;key&gt;</c> or <c>field:&lt;resource&gt;.&lt;field&gt;</c>.</summary>
    public required string Grant { get; init; }
}

/// <summary>Keys of the six built-in persona roles (requirements, "Users and personas").</summary>
public static class BuiltInRoles
{
    /// <summary>End user / customer.</summary>
    public const string Requester = "requester";

    /// <summary>Service desk or support staff.</summary>
    public const string Agent = "agent";

    /// <summary>Team lead / supervisor.</summary>
    public const string TeamLead = "team-lead";

    /// <summary>Manager or budget owner who approves requests.</summary>
    public const string Approver = "approver";

    /// <summary>Tenant administrator.</summary>
    public const string TenantAdmin = "tenant-admin";

    /// <summary>Executive / auditor.</summary>
    public const string ExecutiveAuditor = "executive-auditor";

    /// <summary>All built-in role keys, in persona order.</summary>
    public static IReadOnlyList<string> All { get; } = [Requester, Agent, TeamLead, Approver, TenantAdmin, ExecutiveAuditor];

    /// <summary>Roles that always need a second factor ("mandatory MFA for agents and admins").</summary>
    public static IReadOnlySet<string> AlwaysRequireMfa { get; } = new HashSet<string>(StringComparer.Ordinal) { Agent, TeamLead, TenantAdmin };
}
