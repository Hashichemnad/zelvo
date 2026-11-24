using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Zelvo.Api.Conventions;
using Zelvo.Api.Tenancy;
using Zelvo.Data;
using Zelvo.Data.Identity;

namespace Zelvo.Api.Identity;

/// <summary>Roles, their permissions and field rules: data that tenant administrators change.</summary>
internal static partial class RoleEndpoints
{
    public static RouteGroupBuilder MapRoleEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/permissions", GetCatalog)
            .WithTags("Roles")
            .WithName("GetPermissionCatalog")
            .WithSummary("Everything that can be granted: permission keys and resources with their fields.")
            .RequireTenant()
            .RequirePermission(Permissions.RolesManage);

        var roles = group.MapGroup("/roles").WithTags("Roles").RequireTenant().RequirePermission(Permissions.RolesManage);

        roles.MapGet("/", ListAsync).WithName("ListRoles").WithSummary("Roles with their permissions and field rules.");
        roles.MapGet("/{key}", GetAsync).WithName("GetRole").WithSummary("One role.");
        roles.MapPost("/", CreateAsync)
            .WithName("CreateRole")
            .WithSummary("Add a custom role (task B2). It starts without permissions or field rules. Audited.")
            .Produces<RoleResponse>(StatusCodes.Status201Created)
            .ProducesValidationProblem();
        roles.MapDelete("/{key}", DeleteAsync)
            .WithName("DeleteRole")
            .WithSummary("Delete a custom role that no user or integration has. Built-in roles cannot be deleted. Audited.")
            .Produces(StatusCodes.Status204NoContent);
        roles.MapPatch("/{key}", UpdateAsync)
            .WithName("UpdateRole")
            .WithSummary("Rename a role or change whether it needs a second factor (always on for agents, team leads and tenant administrators). Audited.")
            .ProducesValidationProblem();
        roles.MapPut("/{key}/permissions", SetPermissionsAsync)
            .WithName("SetRolePermissions")
            .WithSummary("Replace a role's permissions. Tenant administrators always keep users.manage and roles.manage. Audited.")
            .ProducesValidationProblem();
        roles.MapPut("/{key}/field-rules", SetFieldRulesAsync)
            .WithName("SetRoleFieldRules")
            .WithSummary("Replace a role's field rules (Hidden, Read or Edit per field). Fields without a rule follow the role's permissions; sensitive fields stay hidden. Audited.")
            .ProducesValidationProblem();

        return group;
    }

    internal static PermissionCatalogResponse GetCatalog() => new(
        PermissionCatalog.All,
        [.. PermissionCatalog.Resources.Select(r => new ResourceResponse(r.Key, r.ReadPermission, r.EditPermission,
            [.. r.Fields.Select(f => new FieldResponse(f.Key, f.Sensitive, f.Writable))]))]);

    internal static async Task<Ok<List<RoleResponse>>> ListAsync(ZelvoDbContext db, CancellationToken cancellationToken)
    {
        var roles = await Query(db).OrderBy(r => r.Key).ToListAsync(cancellationToken);
        return TypedResults.Ok(roles.Select(RoleResponse.From).ToList());
    }

    internal static async Task<Results<Ok<RoleResponse>, ProblemHttpResult>> GetAsync(string key, ZelvoDbContext db, CancellationToken cancellationToken)
    {
        var role = await Query(db).SingleOrDefaultAsync(r => r.Key == key, cancellationToken);
        return role is null ? NotFound() : TypedResults.Ok(RoleResponse.From(role));
    }

    internal static async Task<Results<Created<RoleResponse>, ValidationProblem, ProblemHttpResult>> CreateAsync(
        CreateRoleRequest request, ZelvoDbContext db, CancellationToken cancellationToken)
    {
        var key = request.Key!.Trim();
        if (!RoleKey().IsMatch(key) || BuiltInRoles.All.Contains(key, StringComparer.Ordinal))
        {
            return UserEndpoints.Validation("key", "Use 2 to 50 lower-case letters, digits and hyphens, starting with a letter, and not a built-in role.");
        }

        var name = request.Name!.ToValue();
        if (name.En?.Length > Role.NameMaxLength || name.Ar?.Length > Role.NameMaxLength)
        {
            return UserEndpoints.Validation("name", $"Each language of the name is at most {Role.NameMaxLength} characters.");
        }

        if (await db.Roles.AnyAsync(r => r.Key == key, cancellationToken))
        {
            return (ProblemHttpResult)ApiProblem.Create(StatusCodes.Status409Conflict, "role-exists", "A role with this key already exists.");
        }

        var role = new Role { Key = key, Name = name, IsSystem = false, RequiresMfa = request.RequiresMfa ?? false };
        db.Roles.Add(role);
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Created($"/api/v1/roles/{key}", RoleResponse.From(role));
    }

    internal static async Task<Results<NoContent, ProblemHttpResult>> DeleteAsync(string key, ZelvoDbContext db, CancellationToken cancellationToken)
    {
        var role = await Query(db).SingleOrDefaultAsync(r => r.Key == key, cancellationToken);
        if (role is null)
        {
            return NotFound();
        }

        if (role.IsSystem)
        {
            return (ProblemHttpResult)ApiProblem.Create(StatusCodes.Status409Conflict, "role-system", "Built-in roles cannot be deleted.");
        }

        if (await db.UserRoles.AnyAsync(u => u.RoleId == role.Id, cancellationToken) || await db.ApiClients.AnyAsync(c => c.RoleId == role.Id, cancellationToken))
        {
            return (ProblemHttpResult)ApiProblem.Create(StatusCodes.Status409Conflict, "in-use", "Users or integrations have this role; give them another role first.");
        }

        db.Roles.Remove(role);
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.NoContent();
    }

    [System.Text.RegularExpressions.GeneratedRegex("^[a-z][a-z0-9-]{0,48}[a-z0-9]$", System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex RoleKey();

    internal static async Task<Results<Ok<RoleResponse>, ValidationProblem, ProblemHttpResult>> UpdateAsync(
        string key, UpdateRoleRequest request, ZelvoDbContext db, CancellationToken cancellationToken)
    {
        var role = await Query(db).SingleOrDefaultAsync(r => r.Key == key, cancellationToken);
        if (role is null)
        {
            return NotFound();
        }

        if (request.RequiresMfa == false && BuiltInRoles.AlwaysRequireMfa.Contains(role.Key))
        {
            return (ProblemHttpResult)ApiProblem.Create(StatusCodes.Status409Conflict, "role-mfa-locked",
                "Agents, team leads and tenant administrators always sign in with a second factor.");
        }

        if (request.Name is not null)
        {
            var name = request.Name.ToValue();
            if (name.En?.Length > Role.NameMaxLength || name.Ar?.Length > Role.NameMaxLength)
            {
                return UserEndpoints.Validation("name", $"Each language of the name is at most {Role.NameMaxLength} characters.");
            }

            role.Name = name;
        }

        if (request.RequiresMfa is { } requiresMfa)
        {
            role.RequiresMfa = requiresMfa;
        }

        role.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Ok(RoleResponse.From(role));
    }

    internal static async Task<Results<Ok<RoleResponse>, ValidationProblem, ProblemHttpResult>> SetPermissionsAsync(
        string key, SetRolePermissionsRequest request, ZelvoDbContext db, CancellationToken cancellationToken)
    {
        var wanted = request.Permissions!.Distinct(StringComparer.Ordinal).ToList();
        var unknown = wanted.Where(p => !PermissionCatalog.IsPermission(p)).ToList();
        if (unknown.Count > 0)
        {
            return UserEndpoints.Validation("permissions", $"Unknown permissions: {string.Join(", ", unknown)}.");
        }

        var role = await Query(db).SingleOrDefaultAsync(r => r.Key == key, cancellationToken);
        if (role is null)
        {
            return NotFound();
        }

        if (role.Key == BuiltInRoles.TenantAdmin && !PermissionCatalog.TenantAdminLocked.IsSubsetOf(wanted))
        {
            return (ProblemHttpResult)ApiProblem.Create(StatusCodes.Status409Conflict, "role-locked",
                "Tenant administrators always keep users.manage and roles.manage, so the tenant cannot lock itself out.");
        }

        role.Permissions.RemoveAll(p => !wanted.Contains(p.Permission));
        foreach (var permission in wanted.Where(p => role.Permissions.All(existing => existing.Permission != p)))
        {
            role.Permissions.Add(new RoleGrant { RoleId = role.Id, Permission = permission });
        }

        role.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Ok(RoleResponse.From(role));
    }

    internal static async Task<Results<Ok<RoleResponse>, ValidationProblem, ProblemHttpResult>> SetFieldRulesAsync(
        string key, SetRoleFieldRulesRequest request, ZelvoDbContext db, CancellationToken cancellationToken)
    {
        var rules = request.Rules!;
        var errors = new List<string>();
        foreach (var rule in rules)
        {
            if (PermissionCatalog.Resource(rule.Resource ?? string.Empty) is not { } resource)
            {
                errors.Add($"Unknown resource '{rule.Resource}'.");
            }
            else if (resource.Field(rule.Field ?? string.Empty) is not { } field)
            {
                errors.Add($"Unknown field '{rule.Resource}.{rule.Field}'.");
            }
            else if (rule.Access == FieldAccess.Edit && !field.Writable)
            {
                errors.Add($"'{rule.Resource}.{rule.Field}' cannot be edited through the API; use Read.");
            }
        }

        if (rules.GroupBy(r => (r.Resource, r.Field)).Any(g => g.Count() > 1))
        {
            errors.Add("Each field appears once.");
        }

        if (errors.Count > 0)
        {
            return UserEndpoints.Validation("rules", string.Join(" ", errors));
        }

        var role = await Query(db).SingleOrDefaultAsync(r => r.Key == key, cancellationToken);
        if (role is null)
        {
            return NotFound();
        }

        role.FieldRules.RemoveAll(existing => !rules.Any(r => r.Resource == existing.Resource && r.Field == existing.Field));
        foreach (var rule in rules)
        {
            var existing = role.FieldRules.FirstOrDefault(f => f.Resource == rule.Resource && f.Field == rule.Field);
            if (existing is null)
            {
                role.FieldRules.Add(new RoleFieldRule { RoleId = role.Id, Resource = rule.Resource!, Field = rule.Field!, Access = rule.Access!.Value });
            }
            else
            {
                existing.Access = rule.Access!.Value;
            }
        }

        role.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Ok(RoleResponse.From(role));
    }

    private static IQueryable<Role> Query(ZelvoDbContext db) =>
        db.Roles.Include(r => r.Permissions).Include(r => r.FieldRules).AsSplitQuery();

    private static ProblemHttpResult NotFound() =>
        (ProblemHttpResult)ApiProblem.Create(StatusCodes.Status404NotFound, "role-not-found", "The role does not exist.");
}

/// <summary>Everything that can be granted.</summary>
internal sealed record PermissionCatalogResponse(IReadOnlyList<string> Permissions, IReadOnlyList<ResourceResponse> Resources);

/// <summary>A resource with field-level access control.</summary>
internal sealed record ResourceResponse(string Key, string ReadPermission, string EditPermission, IReadOnlyList<FieldResponse> Fields);

/// <summary>A field under field-level control.</summary>
/// <param name="Key">Field key.</param>
/// <param name="Sensitive">Personal or secret: hidden unless a role is given access.</param>
/// <param name="Writable">Whether it can ever be changed through the API.</param>
internal sealed record FieldResponse(string Key, bool Sensitive, bool Writable);

/// <summary>A role.</summary>
internal sealed record RoleResponse(
    Guid Id, string Key, LocalizedTextDto Name, bool IsSystem, bool RequiresMfa,
    IReadOnlyList<string> Permissions, IReadOnlyList<FieldRuleResponse> FieldRules)
{
    public static RoleResponse From(Role role) => new(
        role.Id, role.Key, LocalizedTextDto.From(role.Name), role.IsSystem, role.RequiresMfa,
        [.. role.Permissions.Select(p => p.Permission).Order(StringComparer.Ordinal)],
        [.. role.FieldRules.OrderBy(f => f.Resource, StringComparer.Ordinal).ThenBy(f => f.Field, StringComparer.Ordinal)
            .Select(f => new FieldRuleResponse(f.Resource, f.Field, f.Access))]);
}

/// <summary>A field rule.</summary>
internal sealed record FieldRuleResponse(string Resource, string Field, FieldAccess Access);

/// <summary>A new custom role.</summary>
public sealed class CreateRoleRequest
{
    /// <summary>Stable key, for example <c>finance-desk</c>.</summary>
    [Required]
    [MaxLength(Role.KeyMaxLength)]
    public string? Key { get; init; }

    /// <summary>Name in English and Arabic.</summary>
    [Required]
    public LocalizedTextInput? Name { get; init; }

    /// <summary>Whether the role's permissions need a second factor (default false).</summary>
    public bool? RequiresMfa { get; init; }
}

/// <summary>Changes to a role. Leave a field out to keep it.</summary>
public sealed class UpdateRoleRequest
{
    /// <summary>Display name; at least one language, each at most 100 characters.</summary>
    public LocalizedTextInput? Name { get; init; }

    /// <summary>Whether users need a second factor before this role's permissions apply.</summary>
    public bool? RequiresMfa { get; init; }
}

/// <summary>The complete list of a role's permissions.</summary>
public sealed class SetRolePermissionsRequest
{
    /// <summary>Permission keys from <c>GET /permissions</c>.</summary>
    [Required]
    [MaxLength(200)]
    public IReadOnlyList<string>? Permissions { get; init; }
}

/// <summary>The complete list of a role's field rules.</summary>
public sealed class SetRoleFieldRulesRequest
{
    /// <summary>One rule per field.</summary>
    [Required]
    [MaxLength(500)]
    public IReadOnlyList<FieldRuleInput>? Rules { get; init; }
}

/// <summary>A field rule.</summary>
public sealed class FieldRuleInput
{
    /// <summary>Resource key, for example <c>users</c>.</summary>
    [Required]
    [StringLength(RoleFieldRule.NameMaxLength)]
    public string? Resource { get; init; }

    /// <summary>Field key, for example <c>email</c>.</summary>
    [Required]
    [StringLength(RoleFieldRule.NameMaxLength)]
    public string? Field { get; init; }

    /// <summary>Hidden, Read or Edit.</summary>
    [Required]
    public FieldAccess? Access { get; init; }
}
