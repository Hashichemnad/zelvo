using Zelvo.Api.Conventions;
using Zelvo.Api.Tenancy;

namespace Zelvo.Api.Identity;

/// <summary>The signed-in user.</summary>
internal static class MeEndpoints
{
    public static RouteGroupBuilder MapMeEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/me", Get)
            .WithTags("Identity")
            .WithName("GetCurrentUser")
            .WithSummary("The signed-in user: profile, roles, permissions that apply now, and whether a second factor is needed.")
            .WithDescription("The user record is created at the first sign-in, with the requester role. When `mfa.required` is true, sign in again asking for `acr_values=mfa`; `pendingPermissions` then apply too.")
            .RequireTenant()
            .RequireAuthorization();
        return group;
    }

    internal static MeResponse Get(CurrentUser user)
    {
        var account = user.Account!;
        return new MeResponse(
            account.Id,
            account.Email,
            account.DisplayName,
            [.. user.Roles.Select(r => new RoleSummary(r.Key, LocalizedTextDto.From(r.Name), r.RequiresMfa)).OrderBy(r => r.Key, StringComparer.Ordinal)],
            [.. user.Permissions.Order(StringComparer.Ordinal)],
            [.. user.PendingMfaPermissions.Order(StringComparer.Ordinal)],
            new MfaStatus(user.HasMfa, user.MfaRequired));
    }
}

/// <summary>The signed-in user.</summary>
/// <param name="Id">Zelvo user id.</param>
/// <param name="Email">Email address from the identity provider.</param>
/// <param name="DisplayName">Display name.</param>
/// <param name="Roles">All roles of the user.</param>
/// <param name="Permissions">Permissions that apply now.</param>
/// <param name="PendingPermissions">Permissions that apply after signing in with a second factor.</param>
/// <param name="Mfa">Second-factor status.</param>
internal sealed record MeResponse(
    Guid Id, string? Email, string? DisplayName, IReadOnlyList<RoleSummary> Roles,
    IReadOnlyList<string> Permissions, IReadOnlyList<string> PendingPermissions, MfaStatus Mfa);

/// <summary>A role, briefly.</summary>
internal sealed record RoleSummary(string Key, LocalizedTextDto Name, bool RequiresMfa);

/// <summary>Second-factor status of the sign-in.</summary>
/// <param name="Satisfied">The sign-in used a second factor.</param>
/// <param name="Required">Some of the user's roles need one and it was not used: sign in again with <c>acr_values=mfa</c>.</param>
internal sealed record MfaStatus(bool Satisfied, bool Required);
