using System.ComponentModel.DataAnnotations;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Zelvo.Api.Conventions;
using Zelvo.Api.Tenancy;
using Zelvo.Data;
using Zelvo.Data.Auditing;
using Zelvo.Data.Identity;
using Zelvo.Data.Localization;
using Zelvo.Data.Tenancy;

namespace Zelvo.Api.Identity;

/// <summary>Users of the tenant. Field rules decide which fields each caller sees and may change.</summary>
internal static partial class UserEndpoints
{
    private static readonly string[] SensitiveFields = ["email", "emiratesId"];

    [LoggerMessage(Level = LogLevel.Error, Message = "Could not record the Emirates ID in Keycloak.")]
    private static partial void LogKeycloakRefused(ILogger logger, Exception exception);

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
            .WithSummary("Change a user's display name, status or Emirates ID (used to match UAE PASS sign-ins; empty string removes it). Needs edit access to each field; audited.")
            .RequirePermission(Permissions.UsersManage)
            .Produces<UserResponse>()
            .ProducesValidationProblem();

        users.MapPost("/", CreateAsync)
            .WithName("CreateUser")
            .WithSummary("Add a person: creates them in the tenant's sign-in service (Keycloak) and in Zelvo with the given roles (requester by default). Needs users.manage; audited.")
            .RequirePermission(Permissions.UsersManage)
            .Produces<UserResponse>(StatusCodes.Status201Created)
            .ProducesValidationProblem();

        users.MapPut("/{id:guid}/skills", SetSkillsAsync)
            .WithName("SetUserSkills")
            .WithSummary("Replace a user's skills (used by round-robin assignment rules). Needs users.manage and edit access to skills; audited.")
            .RequirePermission(Permissions.UsersManage)
            .Produces<UserResponse>()
            .ProducesValidationProblem();

        users.MapPut("/{id:guid}/availability", SetAvailabilityAsync)
            .WithName("SetUserAvailability")
            .WithSummary("Whether round-robin may give the user tickets. Needs users.manage, or leading a group the user belongs to; audited.")
            .RequireAuthorization()
            .Produces<UserResponse>();

        users.MapPut("/{id:guid}/roles", SetRolesAsync)
            .WithName("SetUserRoles")
            .WithSummary("Replace a user's roles. The tenant always keeps an active tenant administrator; audited.")
            .RequirePermission(Permissions.UsersManage)
            .Produces<UserResponse>()
            .ProducesValidationProblem();

        return group;
    }

    internal static async Task<Ok<JsonObject>> ListAsync(
        [AsParameters] PageQuery page, [AsParameters] UserListQuery filter, ZelvoDbContext db, CurrentUser user, IAuditLog audit, CancellationToken cancellationToken)
    {
        var filtered = WithRoles(db);
        if (filter.Q?.Trim() is { Length: > 0 } q)
        {
            // Searching by email only for callers who may see emails, so the search reveals nothing hidden.
            var pattern = "%" + q.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal) + "%";
            filtered = FieldFilter.IsVisible(user, PermissionCatalog.Users, "email")
                ? filtered.Where(u => EF.Functions.ILike(u.DisplayName ?? "", pattern) || EF.Functions.ILike(u.Email ?? "", pattern))
                : filtered.Where(u => EF.Functions.ILike(u.DisplayName ?? "", pattern));
        }

        if (filter.Role is { Length: > 0 } role)
        {
            filtered = filtered.Where(u => u.Roles.Any(r => r.Role!.Key == role));
        }

        if (filter.Status is { } status)
        {
            filtered = filtered.Where(u => u.Status == status);
        }

        var query = filtered.OrderBy(u => u.DisplayName ?? u.Email).ThenBy(u => u.Id);
        var total = await query.LongCountAsync(cancellationToken);
        var items = await query.Skip(page.Skip).Take(page.Size).ToListAsync(cancellationToken);
        var groups = await GroupsOfAsync(db, [.. items.Select(u => u.Id)], cancellationToken);

        await RecordSensitiveViewAsync(user, audit, items, cancellationToken);
        var json = new JsonObject
        {
            ["items"] = new JsonArray([.. items.Select(u => (JsonNode)FieldFilter.Apply(user, PermissionCatalog.Users, UserResponse.From(u, groups)))]),
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
        return TypedResults.Ok(await ResponseAsync(db, user, account, cancellationToken));
    }

    internal static async Task<Results<Created<JsonObject>, ValidationProblem, ProblemHttpResult>> CreateAsync(
        CreateUserRequest request, ZelvoDbContext db, CurrentUser user, ITenantContext tenant, IKeycloakAdmin keycloak,
        IOptions<IdentityOptions> options, ILoggerFactory loggers, CancellationToken cancellationToken)
    {
        var email = request.Email!.Trim().ToLowerInvariant();
        if (!new EmailAddressAttribute().IsValid(email) || email.Length > UserAccount.EmailMaxLength)
        {
            return Validation("email", "Use a valid email address.");
        }

        var keys = (request.Roles is { Count: > 0 } r ? r : [BuiltInRoles.Requester]).Distinct(StringComparer.Ordinal).ToList();
        var roles = await db.Roles.Where(x => keys.Contains(x.Key)).ToListAsync(cancellationToken);
        if (roles.Count != keys.Count)
        {
            return Validation("roles", $"Unknown roles: {string.Join(", ", keys.Except(roles.Select(x => x.Key), StringComparer.Ordinal))}.");
        }

        if (await db.Users.AnyAsync(u => u.Email == email, cancellationToken))
        {
            return (ProblemHttpResult)ApiProblem.Create(StatusCodes.Status409Conflict, "user-exists", "A user with this email address already exists.");
        }

        var displayName = request.DisplayName?.Trim() is { Length: > 0 } n ? n : null;
        var name = displayName ?? email.Split('@')[0];
        var space = name.IndexOf(' ', StringComparison.Ordinal);
        string subject;
        try
        {
            subject = await keycloak.FindOrCreateUserAsync(options.Value.RealmFor(tenant.Slug!), email,
                space > 0 ? name[..space] : name, space > 0 ? name[(space + 1)..] : string.Empty, cancellationToken);
        }
        catch (Exception ex) when (ex is KeycloakAdminException or HttpRequestException)
        {
            LogKeycloakRefused(loggers.CreateLogger("Zelvo.Identity"), ex);
            return (ProblemHttpResult)ApiProblem.Create(StatusCodes.Status502BadGateway, "identity-provider-unavailable",
                "The sign-in service did not accept the new user. Nothing was changed.");
        }

        if (await db.Users.AnyAsync(u => u.Subject == subject, cancellationToken))
        {
            return (ProblemHttpResult)ApiProblem.Create(StatusCodes.Status409Conflict, "user-exists", "This person already has a Zelvo account.");
        }

        var account = new UserAccount { Subject = subject, Email = email, DisplayName = displayName, Status = UserStatus.Active };
        foreach (var role in roles)
        {
            account.Roles.Add(new UserRole { UserId = account.Id, RoleId = role.Id, Role = role });
        }

        db.Users.Add(account);
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Created($"/api/v1/users/{account.Id}", await ResponseAsync(db, user, account, cancellationToken));
    }

    internal static async Task<Results<Ok<JsonObject>, ValidationProblem, ProblemHttpResult>> SetSkillsAsync(
        Guid id, SetUserSkillsRequest request, ZelvoDbContext db, CurrentUser user, CancellationToken cancellationToken)
    {
        if (FieldFilter.NotEditable(user, PermissionCatalog.Users, ["skills"]) is { Count: > 0 } refused)
        {
            return FieldsReadOnly(refused);
        }

        var account = await WithRoles(db).SingleOrDefaultAsync(u => u.Id == id, cancellationToken);
        if (account is null)
        {
            return NotFound();
        }

        var ids = request.SkillIds!.Distinct().ToList();
        var known = await db.Skills.Where(s => ids.Contains(s.Id)).ToListAsync(cancellationToken);
        var kept = account.Skills.Select(s => s.SkillId).ToHashSet();
        if (known.Count != ids.Count || known.Any(s => !s.Active && !kept.Contains(s.Id)))
        {
            return Validation("skillIds", "Choose active skills of this organisation.");
        }

        account.Skills.RemoveAll(s => !ids.Contains(s.SkillId));
        foreach (var skill in known.Where(k => !kept.Contains(k.Id)))
        {
            account.Skills.Add(new Data.Directory.UserSkill { UserId = account.Id, SkillId = skill.Id, Skill = skill });
        }

        account.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Ok(await ResponseAsync(db, user, account, cancellationToken));
    }

    internal static async Task<Results<Ok<JsonObject>, ProblemHttpResult>> SetAvailabilityAsync(
        Guid id, SetAvailabilityRequest request, ZelvoDbContext db, CurrentUser user, CancellationToken cancellationToken)
    {
        var mayManage = user.Has(Permissions.UsersManage);
        var leads = user.Account is { } me && user.Has(Zelvo.Tickets.IncidentPermissions.Edit)
            && await db.SupportGroupMembers.AnyAsync(m => m.UserId == id
                && db.SupportGroupMembers.Any(l => l.GroupId == m.GroupId && l.UserId == me.Id && l.IsLead), cancellationToken);
        if (!mayManage && !leads)
        {
            return user.Has(Permissions.UsersRead)
                ? (ProblemHttpResult)ApiProblem.Create(StatusCodes.Status403Forbidden, "forbidden", "Only administrators and the leads of the user's groups may change it.")
                : NotFound();
        }

        var account = await WithRoles(db).SingleOrDefaultAsync(u => u.Id == id, cancellationToken);
        if (account is null)
        {
            return NotFound();
        }

        account.AvailableForAssignment = request.Available!.Value;
        account.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Ok(await ResponseAsync(db, user, account, cancellationToken));
    }

    internal static async Task<JsonObject> ResponseAsync(ZelvoDbContext db, CurrentUser user, UserAccount account, CancellationToken cancellationToken) =>
        FieldFilter.Apply(user, PermissionCatalog.Users, UserResponse.From(account, await GroupsOfAsync(db, [account.Id], cancellationToken)));

    /// <summary>The groups of each user, by name.</summary>
    internal static async Task<ILookup<Guid, UserGroupResponse>> GroupsOfAsync(ZelvoDbContext db, IReadOnlyList<Guid> userIds, CancellationToken cancellationToken)
    {
        var rows = await db.SupportGroupMembers.AsNoTracking().Where(m => userIds.Contains(m.UserId))
            .Join(db.SupportGroups, m => m.GroupId, g => g.Id, (m, g) => new { m.UserId, g.Id, g.Name, m.IsLead })
            .ToListAsync(cancellationToken);
        return rows.ToLookup(r => r.UserId, r => new UserGroupResponse(r.Id, LocalizedTextDto.From(r.Name), r.IsLead));
    }

    internal static async Task<Results<Ok<JsonObject>, ValidationProblem, ProblemHttpResult>> UpdateAsync(
        Guid id, UpdateUserRequest request, ZelvoDbContext db, CurrentUser user, ITenantContext tenant, IKeycloakAdmin keycloak,
        IOptions<IdentityOptions> options, ILoggerFactory loggers, CancellationToken cancellationToken)
    {
        var changed = new List<string>();
        string? emiratesId = null;
        if (request.EmiratesId is not null)
        {
            changed.Add("emiratesId");
            emiratesId = request.EmiratesId.Replace("-", "", StringComparison.Ordinal).Replace(" ", "", StringComparison.Ordinal);
            if (emiratesId.Length > 0 && !IsEmiratesId(emiratesId))
            {
                return Validation("emiratesId", "Use the 15-digit Emirates ID number, starting with 784.");
            }
        }

        if (request.DisplayName is not null)
        {
            changed.Add("displayName");
        }

        if (request.Status is not null)
        {
            changed.Add("status");
        }

        if (request.Languages is not null)
        {
            changed.Add("languages");
            if (request.Languages.Any(l => !LocalizedText.Languages.Contains(l)))
            {
                return Validation("languages", "Use en and ar.");
            }
        }

        if (request.AvailableForAssignment is not null)
        {
            changed.Add("availableForAssignment");
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

        if (request.Status == UserStatus.Disabled && account.Id == user.Account?.Id)
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

        if (request.Languages is not null)
        {
            account.Languages = [.. LocalizedText.Languages.Where(request.Languages.Contains)];
        }

        account.AvailableForAssignment = request.AvailableForAssignment ?? account.AvailableForAssignment;

        var newEmiratesId = string.IsNullOrEmpty(emiratesId) ? null : emiratesId;
        var emiratesIdChanged = request.EmiratesId is not null && newEmiratesId != account.EmiratesId;
        if (emiratesIdChanged && newEmiratesId is not null
            && await db.Users.AnyAsync(u => u.Id != account.Id && u.EmiratesId == newEmiratesId, cancellationToken))
        {
            return (ProblemHttpResult)ApiProblem.Create(StatusCodes.Status409Conflict, "emirates-id-in-use",
                "Another user of this tenant has this Emirates ID.");
        }

        if (emiratesIdChanged)
        {
            account.EmiratesId = newEmiratesId;
        }

        account.UpdatedAt = DateTimeOffset.UtcNow;

        // Saved and copied to Keycloak together: if Keycloak refuses, nothing changes.
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        if (emiratesIdChanged)
        {
            try
            {
                await keycloak.SetEmiratesIdAsync(options.Value.RealmFor(tenant.Slug!), account.Subject, newEmiratesId, cancellationToken);
            }
            catch (Exception ex) when (ex is KeycloakAdminException or HttpRequestException)
            {
                LogKeycloakRefused(loggers.CreateLogger("Zelvo.Identity"), ex);
                return (ProblemHttpResult)ApiProblem.Create(StatusCodes.Status502BadGateway, "identity-provider-unavailable",
                    "The sign-in service did not accept the change. Nothing was changed.");
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return TypedResults.Ok(await ResponseAsync(db, user, account, cancellationToken));
    }

    private static bool IsEmiratesId(string value) =>
        value.Length == UserAccount.EmiratesIdLength && value.StartsWith("784", StringComparison.Ordinal) && value.All(char.IsAsciiDigit);

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
        return TypedResults.Ok(await ResponseAsync(db, user, account, cancellationToken));
    }

    private static IQueryable<UserAccount> WithRoles(ZelvoDbContext db) =>
        db.Users.Include(u => u.Roles).ThenInclude(r => r.Role).Include(u => u.Skills).ThenInclude(s => s.Skill).AsSplitQuery();

    private static bool IsTenantAdmin(UserAccount account) => account.Roles.Any(r => r.Role!.Key == BuiltInRoles.TenantAdmin);

    private static Task<bool> OtherActiveAdminExistsAsync(ZelvoDbContext db, Guid except, CancellationToken cancellationToken) =>
        db.Users.AnyAsync(u => u.Id != except && u.Status == UserStatus.Active
            && u.Roles.Any(r => r.Role!.Key == BuiltInRoles.TenantAdmin), cancellationToken);

    /// <summary>Viewing personal data is audited ("every view of sensitive data").</summary>
    private static Task RecordSensitiveViewAsync(CurrentUser user, IAuditLog audit, List<UserAccount> accounts, CancellationToken cancellationToken)
    {
        var visible = SensitiveFields.Where(f => FieldFilter.IsVisible(user, PermissionCatalog.Users, f)).ToArray();
        if (accounts.Count == 0 || visible.Length == 0)
        {
            return Task.CompletedTask;
        }

        return audit.RecordAsync(AuditCategory.View, "users.personal-data.viewed", "user_account", null,
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["fields"] = visible,
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
/// <param name="EmiratesId">Emirates ID number, for matching UAE PASS sign-ins (personal data: hidden unless a role is given access).</param>
/// <param name="Status">Whether the user may use Zelvo.</param>
/// <param name="Roles">Role keys.</param>
/// <param name="LastSeenAt">When the user last used Zelvo (hourly).</param>
/// <param name="CreatedAt">First sign-in.</param>
/// <param name="Skills">Skills (task B2).</param>
/// <param name="Languages">Languages the user handles tickets in (task B2).</param>
/// <param name="AvailableForAssignment">Whether round-robin may give them tickets (task B2).</param>
/// <param name="Groups">Groups the user belongs to (task B2).</param>
internal sealed record UserResponse(
    Guid Id, string? DisplayName, string? Email, string? EmiratesId, UserStatus Status, IReadOnlyList<string> Roles,
    DateTimeOffset? LastSeenAt, DateTimeOffset CreatedAt,
    IReadOnlyList<ServiceDesk.NamedRef> Skills, IReadOnlyList<string> Languages, bool AvailableForAssignment, IReadOnlyList<UserGroupResponse> Groups)
{
    public static UserResponse From(UserAccount account, ILookup<Guid, UserGroupResponse>? groups = null) => new(
        account.Id, account.DisplayName, account.Email, account.EmiratesId, account.Status,
        [.. account.Roles.Select(r => r.Role!.Key).Order(StringComparer.Ordinal)],
        account.LastSeenAt, account.CreatedAt,
        [.. account.Skills.Where(s => s.Skill is not null).Select(s => ServiceDesk.NamedRef.From(s.SkillId, s.Skill!.Name)).OrderBy(s => s.Name.En, StringComparer.OrdinalIgnoreCase)],
        account.Languages, account.AvailableForAssignment,
        [.. (groups?[account.Id] ?? []).OrderBy(g => g.Name.En, StringComparer.OrdinalIgnoreCase)]);
}

/// <summary>A group a user belongs to.</summary>
internal sealed record UserGroupResponse(Guid Id, LocalizedTextDto Name, bool IsLead);

/// <summary>Filters of the user list.</summary>
public sealed class UserListQuery
{
    /// <summary>Part of the display name, or of the email when the caller may see emails.</summary>
    [MaxLength(100)]
    public string? Q { get; init; }

    /// <summary>Only users with this role key.</summary>
    [MaxLength(Data.Identity.Role.KeyMaxLength)]
    public string? Role { get; init; }

    /// <summary>Only Active or Disabled users.</summary>
    public UserStatus? Status { get; init; }
}

/// <summary>A person to add.</summary>
public sealed class CreateUserRequest
{
    /// <summary>Email address; the person signs in with it.</summary>
    [Required]
    [MaxLength(UserAccount.EmailMaxLength)]
    public string? Email { get; init; }

    /// <summary>Display name.</summary>
    [StringLength(UserAccount.DisplayNameMaxLength)]
    public string? DisplayName { get; init; }

    /// <summary>Role keys; requester when left out.</summary>
    [MaxLength(50)]
    public IReadOnlyList<string>? Roles { get; init; }
}

/// <summary>The complete list of a user's skills.</summary>
public sealed class SetUserSkillsRequest
{
    /// <summary>Skill ids.</summary>
    [Required]
    [MaxLength(100)]
    public IReadOnlyList<Guid>? SkillIds { get; init; }
}

/// <summary>Whether round-robin may give the user tickets.</summary>
public sealed class SetAvailabilityRequest
{
    /// <summary>Available for assignment.</summary>
    [Required]
    public bool? Available { get; init; }
}

/// <summary>Changes to a user. Leave a field out to keep it.</summary>
public sealed class UpdateUserRequest
{
    /// <summary>Display name, 1 to 200 characters.</summary>
    [StringLength(UserAccount.DisplayNameMaxLength, MinimumLength = 1)]
    public string? DisplayName { get; init; }

    /// <summary>Active or Disabled.</summary>
    public UserStatus? Status { get; init; }

    /// <summary>Emirates ID number (15 digits starting with 784; dashes allowed). Empty string removes it.</summary>
    [MaxLength(20)]
    public string? EmiratesId { get; init; }
    /// <summary>Languages the user handles tickets in: en, ar (task B2).</summary>
    [MaxLength(2)]
    public IReadOnlyList<string>? Languages { get; init; }

    /// <summary>Whether round-robin may give the user tickets (task B2).</summary>
    public bool? AvailableForAssignment { get; init; }
}

/// <summary>The complete list of a user's roles.</summary>
public sealed class SetUserRolesRequest
{
    /// <summary>Role keys, for example <c>["agent", "approver"]</c>.</summary>
    [Required]
    [MaxLength(50)]
    public IReadOnlyList<string>? Roles { get; init; }
}
