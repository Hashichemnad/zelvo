using Zelvo.Data.Identity;

namespace Zelvo.Api.Identity;

/// <summary>The signed-in user of the request, with effective permissions. Empty for anonymous requests.</summary>
internal sealed class CurrentUser
{
    private static readonly IReadOnlySet<string> NoPermissions = new HashSet<string>(StringComparer.Ordinal);

    public bool IsSignedIn => Account is not null;

    /// <summary>The user's Zelvo record.</summary>
    public UserAccount? Account { get; private set; }

    /// <summary>Whether the token shows a second factor.</summary>
    public bool HasMfa { get; private set; }

    /// <summary>All roles of the user.</summary>
    public IReadOnlyList<Role> Roles { get; private set; } = [];

    /// <summary>Roles whose permissions apply now.</summary>
    public IReadOnlyList<Role> ActiveRoles { get; private set; } = [];

    /// <summary>Permissions that apply now.</summary>
    public IReadOnlySet<string> Permissions { get; private set; } = NoPermissions;

    /// <summary>Permissions the user would get after signing in with a second factor.</summary>
    public IReadOnlySet<string> PendingMfaPermissions { get; private set; } = NoPermissions;

    /// <summary>The user has roles that need a second factor, and the token does not show one.</summary>
    public bool MfaRequired => Roles.Any(r => r.RequiresMfa) && !HasMfa;

    public bool Has(string permission) => Permissions.Contains(permission);

    public void Set(UserAccount account, IReadOnlyList<Role> roles, bool hasMfa)
    {
        Account = account;
        Roles = roles;
        HasMfa = hasMfa;
        ActiveRoles = [.. roles.Where(r => !r.RequiresMfa || hasMfa)];
        Permissions = ActiveRoles.SelectMany(r => r.Permissions).Select(p => p.Permission)
            .Where(PermissionCatalog.IsPermission).ToHashSet(StringComparer.Ordinal);
        PendingMfaPermissions = roles.Except(ActiveRoles).SelectMany(r => r.Permissions).Select(p => p.Permission)
            .Where(p => PermissionCatalog.IsPermission(p) && !Permissions.Contains(p)).ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>
    /// Access to a field: for each active role, its explicit rule, or else what its resource
    /// permissions give (read or edit; nothing for sensitive fields). The highest access wins.
    /// Fields the API never accepts changes to are at most read-only.
    /// </summary>
    public FieldAccess FieldAccessFor(ResourceDefinition resource, FieldDefinition field)
    {
        var best = FieldAccess.Hidden;
        foreach (var role in ActiveRoles)
        {
            var rule = role.FieldRules.FirstOrDefault(f => f.Resource == resource.Key && f.Field == field.Key);
            FieldAccess access;
            if (rule is not null)
            {
                access = rule.Access;
            }
            else if (field.Sensitive)
            {
                access = FieldAccess.Hidden;
            }
            else
            {
                var permissions = role.Permissions.Select(p => p.Permission).ToHashSet(StringComparer.Ordinal);
                access = permissions.Contains(resource.EditPermission) ? FieldAccess.Edit
                    : permissions.Contains(resource.ReadPermission) ? FieldAccess.Read
                    : FieldAccess.Hidden;
            }

            if (access > best)
            {
                best = access;
            }
        }

        return !field.Writable && best == FieldAccess.Edit ? FieldAccess.Read : best;
    }
}
