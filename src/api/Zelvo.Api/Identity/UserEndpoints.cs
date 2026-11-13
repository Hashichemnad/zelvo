using System.ComponentModel.DataAnnotations;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Zelvo.Api.Conventions;
using Zelvo.Api.Tenancy;
using Zelvo.Data;
using Zelvo.Data.Auditing;
using Zelvo.Data.Identity;

namespace Zelvo.Api.Identity;

/// <summary>Users of the tenant. Field rules decide which fields each caller sees and may change.</summary>
internal static class UserEndpoints
{
    private static readonly string[] SensitiveFields = ["email"];

    public static RouteGroupBuilder MapUserEndpoints(this RouteGroupBuilder group)
    {
        var users = group.MapGroup("/users").WithTags("Users").RequireTenant();

        users.MapGet("/", ListAsync)
            .WithName("ListUsers")
            .WithSummary("Users of the tenant, by name. Fields hidden from the caller's roles are left out.")
            .RequirePermission(Permissions.UsersRead)
            .Produces<PageResult<UserResponse>>();

        users.MapGet("/{id:guid}", GetAsync)
            .WithName("GetUser")
            .WithSummary("One user. Fields hidden from the caller's roles are left out.")
            .RequirePermission(Permissions.UsersRead)
            .Produces<UserResponse>();

        users.MapPatch("/{id:guid}", UpdateAsync)
            .WithName("UpdateUser")
            .WithSummary("Change a user's display name or status. Needs edit access to each field; audited.")
            .RequirePermission(Permissions.UsersManage)
            .Produces<UserResponse>()
            .ProducesValidationProblem();

        users.MapPut("/{id:guid}/roles", SetRolesAsync)
            .WithName("SetUserRoles")
            .WithSummary("Replace a user's roles. The tenant always keeps an active tenant administrator; audited.")
            .RequirePermission(Permissions.UsersManage)
            .Produces<UserResponse>()
            .ProducesValidationProblem();

        return group;
    }

    internal static async Task<Ok<JsonObject>> ListAsync(
        [AsParameters] PageQuery page, ZelvoDbContext db, CurrentUser user, IAuditLog audit, CancellationToken cancellationToken)
    {
        var query = WithRoles(db).OrderBy(u => u.DisplayName ?? u.Email).ThenBy(u => u.Id);
        var total = await query.LongCountAsync(cancellationToken);
        var items = await query.Skip(page.Skip).Take(page.Size).ToListAsync(cancellationToken);

        await RecordSensitiveViewAsync(user, audit, items, cancellationToken);
        var json = new JsonObject
        {
            ["items"] = new JsonArray([.. items.Select(u => (JsonNode)FieldFilter.Apply(user, PermissionCatalog.Users, UserResponse.From(u)))]),
            ["page"] = page.Number,
            ["pageSize"] = page.Size,
            ["totalCount"] = total,
        };
        return TypedResults.Ok(json);
    }

    internal static async Task<Results<Ok<JsonObject>, ProblemHttpResult>> GetAsync(
        Guid id, ZelvoDbContext db, CurrentUser user, IAuditLog audit, CancellationToken cancellationToken)
    {
        var account = await WithRoles(db).SingleOrDefaultAsync(u => u.Id == id, cancellationToken);
        if (account is null)
        {
            return NotFound();
        }

        await RecordSensitiveViewAsync(user, audit, [account], cancellationToken);
        return TypedResults.Ok(FieldFilter.Apply(user, PermissionCatalog.Users, UserResponse.From(account)));
    }

    internal static async Task<Results<Ok<JsonObject>, ValidationProblem, ProblemHttpResult>> UpdateAsync(
        Guid id, UpdateUserRequest request, ZelvoDbContext db, CurrentUser user, CancellationToken cancellationToken)
    {
        var changed = new List<string>();
        if (request.DisplayName is not null)
        {
            changed.Add("displayName");
        }

        if (request.Status is not null)
        {
            changed.Add("status");
        }

        var refused = FieldFilter.NotEditable(user, PermissionCatalog.Users, changed);
        if (refused.Count > 0)
        {
            return FieldsReadOnly(refused);
        }

        var account = await WithRoles(db).SingleOrDefaultAsync(u => u.Id == id, cancellationToken);
        if (account is null)
        {
            return NotFound();
        }

        if (request.Status == UserStatus.Disabled && account.Id == user.Account!.Id)
        {
            return (ProblemHttpResult)ApiProblem.Create(StatusCodes.Status409Conflict, "cannot-disable-self", "You cannot disable your own account.");
        }

        if (request.Status == UserStatus.Disabled && IsTenantAdmin(account) && !await OtherActiveAdminExistsAsync(db, account.Id, cancellationToken))
        {
            return LastTenantAdmin();
        }

        if (request.DisplayName is not null)
        {
            account.DisplayName = request.DisplayName.Trim();
        }

        if (request.Status is { } status)
        {
            account.Status = status;
        }

        account.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Ok(FieldFilter.Apply(user, PermissionCatalog.Users, UserResponse.From(account)));
    }

    internal static async Task<Results<Ok<JsonObject>, ValidationProblem, ProblemHttpResult>> SetRolesAsync(
        Guid id, SetUserRolesRequest request, ZelvoDbContext db, CurrentUser user, CancellationToken cancellationToken)
    {
        var keys = request.Roles!.Distinct(StringComparer.Ordinal).ToList();
        var roles = await db.Roles.Where(r => keys.Contains(r.Key)).ToListAsync(cancellationToken);
        var unknown = keys.Except(roles.Select(r => r.Key), StringComparer.Ordinal).ToList();
        if (unknown.Count > 0)
        {
            return Validation("roles", $"Unknown roles: {string.Join(", ", unknown)}.");
        }

        var account = await WithRoles(db).SingleOrDefaultAsync(u => u.Id == id, cancellationToken);
        if (account is null)
        {
            return NotFound();
        }

        var losesAdmin = IsTenantAdmin(account) && !keys.Contains(BuiltInRoles.TenantAdmin);
        if (losesAdmin && !await OtherActiveAdminExistsAsync(db, account.Id, cancellationToken))
        {
            return LastTenantAdmin();
        }

        account.Roles.RemoveAll(r => !keys.Contains(r.Role!.Key));
        foreach (var role in roles.Where(role => account.Roles.All(r => r.RoleId != role.Id)))
        {
            account.Roles.Add(new UserRole { UserId = account.Id, RoleId = role.Id, Role = role });
        }

        account.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Ok(FieldFilter.Apply(user, PermissionCatalog.Users, UserResponse.From(account)));
    }

    private static IQueryable<UserAccount> WithRoles(ZelvoDbContext db) => db.Users.Include(u => u.Roles).ThenInclude(r => r.Role);

    private static bool IsTenantAdmin(UserAccount account) => account.Roles.Any(r => r.Role!.Key == BuiltInRoles.TenantAdmin);

    private static Task<bool> OtherActiveAdminExistsAsync(ZelvoDbContext db, Guid except, CancellationToken cancellationToken) =>
        db.Users.AnyAsync(u => u.Id != except && u.Status == UserStatus.Active
            && u.Roles.Any(r => r.Role!.Key == BuiltInRoles.TenantAdmin), cancellationToken);

    /// <summary>Viewing personal data is audited ("every view of sensitive data").</summary>
    private static Task RecordSensitiveViewAsync(CurrentUser user, IAuditLog audit, List<UserAccount> accounts, CancellationToken cancellationToken)
    {
        if (accounts.Count == 0 || !FieldFilter.IsVisible(user, PermissionCatalog.Users, "email"))
        {
            return Task.CompletedTask;
        }

        return audit.RecordAsync(AuditCategory.View, "users.personal-data.viewed", "user_account", null,
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["fields"] = SensitiveFields,
                ["userIds"] = accounts.Select(a => a.Id.ToString()).ToArray(),
            }, cancellationToken);
    }

    private static ProblemHttpResult NotFound() =>
        (ProblemHttpResult)ApiProblem.Create(StatusCodes.Status404NotFound, "user-not-found", "The user does not exist.");

    private static ProblemHttpResult LastTenantAdmin() =>
        (ProblemHttpResult)ApiProblem.Create(StatusCodes.Status409Conflict, "last-tenant-admin",
            "The tenant needs at least one active tenant administrator.");

    private static ProblemHttpResult FieldsReadOnly(IReadOnlyList<string> fields)
    {
        var problem = TypedResults.Problem(
            statusCode: StatusCodes.Status403Forbidden,
            title: "You may not change some of these fields.",
            type: ApiProblem.TypeFor("fields-read-only"),
            extensions: new Dictionary<string, object?>(StringComparer.Ordinal) { ["code"] = "fields-read-only", ["fields"] = fields });
        return problem;
    }

    internal static ValidationProblem Validation(string field, string message) =>
        TypedResults.ValidationProblem(
            new Dictionary<string, string[]>(StringComparer.Ordinal) { [field] = [message] },
            type: ApiProblem.TypeFor("validation-failed"),
            extensions: new Dictionary<string, object?>(StringComparer.Ordinal) { ["code"] = "validation-failed" });
}

/// <summary>A user. Fields hidden from the caller are left out.</summary>
/// <param name="Id">Zelvo user id.</param>
/// <param name="DisplayName">Display name.</param>
/// <param name="Email">Email address (personal data: hidden unless a role is given access).</param>
/// <param name="Status">Whether the user may use Zelvo.</param>
/// <param name="Roles">Role keys.</param>
/// <param name="LastSeenAt">When the user last used Zelvo (hourly).</param>
/// <param name="CreatedAt">First sign-in.</param>
internal sealed record UserResponse(
    Guid Id, string? DisplayName, string? Email, UserStatus Status, IReadOnlyList<string> Roles, DateTimeOffset? LastSeenAt, DateTimeOffset CreatedAt)
{
    public static UserResponse From(UserAccount account) => new(
        account.Id, account.DisplayName, account.Email, account.Status,
        [.. account.Roles.Select(r => r.Role!.Key).Order(StringComparer.Ordinal)],
        account.LastSeenAt, account.CreatedAt);
}

/// <summary>Changes to a user. Leave a field out to keep it.</summary>
public sealed class UpdateUserRequest
{
    /// <summary>Display name, 1 to 200 characters.</summary>
    [StringLength(UserAccount.DisplayNameMaxLength, MinimumLength = 1)]
    public string? DisplayName { get; init; }

    /// <summary>Active or Disabled.</summary>
    public UserStatus? Status { get; init; }
}

/// <summary>The complete list of a user's roles.</summary>
public sealed class SetUserRolesRequest
{
    /// <summary>Role keys, for example <c>["agent", "approver"]</c>.</summary>
    [Required]
    [MaxLength(50)]
    public IReadOnlyList<string>? Roles { get; init; }
}
