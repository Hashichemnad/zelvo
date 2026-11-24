using Microsoft.EntityFrameworkCore;
using Zelvo.Api.Conventions;
using Zelvo.Api.Integrations;
using Zelvo.Api.Settings;
using Zelvo.Api.Tenancy;
using Zelvo.Data;

namespace Zelvo.Api.Identity;

/// <summary>The signed-in user.</summary>
internal static class MeEndpoints
{
    public static RouteGroupBuilder MapMeEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/me", Get)
            .WithTags("Identity")
            .WithName("GetCurrentUser")
            .WithSummary("The signed-in user: profile, display preferences, roles, permissions that apply now, and whether a second factor is needed.")
            .WithDescription("The user record is created at the first sign-in, with the requester role. When `mfa.required` is true, sign in again asking for `acr_values=mfa`; `pendingPermissions` then apply too.")
            .RequireTenant()
            .RequirePerson();

        group.MapPut("/me/availability", SetAvailabilityAsync)
            .WithTags("Identity")
            .WithName("SetMyAvailability")
            .WithSummary("Switch your own availability for round-robin assignment on or off (task B2). Needs incidents.edit; audited.")
            .RequireTenant()
            .RequirePerson()
            .RequirePermission(Zelvo.Tickets.IncidentPermissions.Edit);
        return group;
    }

    internal static async Task<MeResponse> SetAvailabilityAsync(
        SetAvailabilityRequest request, CurrentUser user, ZelvoDbContext db, TenantSettingsStore store, CancellationToken cancellationToken)
    {
        var account = await db.Users.SingleAsync(u => u.Id == user.Account!.Id, cancellationToken).ConfigureAwait(false);
        account.AvailableForAssignment = request.Available!.Value;
        account.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return await Get(user, db, store, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<MeResponse> Get(CurrentUser user, ZelvoDbContext db, TenantSettingsStore store, CancellationToken cancellationToken)
    {
        var account = user.Account!;
        var fresh = await db.Users.AsNoTracking().Where(u => u.Id == account.Id).Select(u => u.AvailableForAssignment).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        var groups = await UserEndpoints.GroupsOfAsync(db, [account.Id], cancellationToken).ConfigureAwait(false);
        return new MeResponse(
            account.Id,
            account.Email,
            account.DisplayName,
            [.. user.Roles.Select(r => new RoleSummary(r.Key, LocalizedTextDto.From(r.Name), r.RequiresMfa)).OrderBy(r => r.Key, StringComparer.Ordinal)],
            [.. user.Permissions.Order(StringComparer.Ordinal)],
            [.. user.PendingMfaPermissions.Order(StringComparer.Ordinal)],
            new MfaStatus(user.HasMfa, user.MfaRequired),
            await PreferencesResponse.LoadAsync(account, store, cancellationToken).ConfigureAwait(false),
            fresh, [.. groups[account.Id]]);
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
/// <param name="Preferences">Own language, calendar and digits, and what applies.</param>
/// <param name="AvailableForAssignment">Whether round-robin may give you tickets (task B2).</param>
/// <param name="Groups">Groups you belong to (task B2).</param>
internal sealed record MeResponse(
    Guid Id, string? Email, string? DisplayName, IReadOnlyList<RoleSummary> Roles,
    IReadOnlyList<string> Permissions, IReadOnlyList<string> PendingPermissions, MfaStatus Mfa, PreferencesResponse Preferences,
    bool AvailableForAssignment, IReadOnlyList<UserGroupResponse> Groups);

/// <summary>A role, briefly.</summary>
internal sealed record RoleSummary(string Key, LocalizedTextDto Name, bool RequiresMfa);

/// <summary>Second-factor status of the sign-in.</summary>
/// <param name="Satisfied">The sign-in used a second factor.</param>
/// <param name="Required">Some of the user's roles need one and it was not used: sign in again with <c>acr_values=mfa</c>.</param>
internal sealed record MfaStatus(bool Satisfied, bool Required);
