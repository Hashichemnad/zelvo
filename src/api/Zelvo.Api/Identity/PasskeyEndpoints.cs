using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Zelvo.Api.Conventions;
using Zelvo.Api.Integrations;
using Zelvo.Api.Tenancy;
using Zelvo.Data;
using Zelvo.Data.Auditing;
using Zelvo.Data.Identity;
using Zelvo.Data.Tenancy;

namespace Zelvo.Api.Identity;

/// <summary>
/// Passkeys (module 19 "biometric sign-in where the device supports it", task J3; product owner
/// decision 15 of 2026-11-26): through Keycloak's own WebAuthn support. People add passkeys under
/// Account → Security (Keycloak's "add a passkey" step, started by the web app), sign in with them,
/// and remove them here; a passkey sign-in counts as staff's second factor because the device
/// verified the person. Administrators turn passkeys off, allow them (default) or require them as
/// staff's second factor.
/// </summary>
internal static partial class PasskeyEndpoints
{
    [LoggerMessage(Level = LogLevel.Error, Message = "Keycloak did not answer a passkey request.")]
    private static partial void LogKeycloakRefused(ILogger logger, Exception exception);

    public static RouteGroupBuilder MapPasskeyEndpoints(this RouteGroupBuilder group)
    {
        var mine = group.MapGroup("/me/passkeys").WithTags("Identity").RequireTenant().RequirePerson();

        mine.MapGet("/", ListMineAsync)
            .WithName("ListMyPasskeys")
            .WithSummary("Your passkeys and whether your organisation offers or requires them. Add one with Keycloak's required action webauthn-register-passwordless (the web app's Account → Security starts it).")
            .Produces<MyPasskeysResponse>()
            .ProducesProblem(StatusCodes.Status502BadGateway);

        mine.MapDelete("/{id}", RemoveMineAsync)
            .WithName("RemoveMyPasskey")
            .WithSummary("Removes one of your passkeys; it no longer signs you in. Audited.")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status502BadGateway);

        group.MapDelete("/users/{id:guid}/passkeys", RemoveAllOfUserAsync)
            .WithTags("Identity")
            .WithName("RemoveUserPasskeys")
            .WithSummary("Removes every passkey of a person, for example after a lost phone; they sign in another way and may add a new one. Needs users.manage; audited.")
            .RequireTenant()
            .RequirePermission(Permissions.UsersManage)
            .Produces<PasskeysRemovedResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status502BadGateway);

        group.MapPut("/identity/settings/passkeys", UpdateSettingAsync)
            .WithTags("Identity")
            .WithName("UpdatePasskeySettings")
            .WithSummary("Off, Allowed (default: anyone may use passkeys; a passkey sign-in is staff's second factor) or RequiredForStaff (staff's second factor must be a passkey). Applied to the tenant's Keycloak realm with the change; audited.")
            .RequireTenant()
            .RequirePermission(Permissions.IdentityManage)
            .Produces<IdentitySettingsResponse>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status502BadGateway);

        return group;
    }

    internal static async Task<IResult> ListMineAsync(
        CurrentUser user, ZelvoDbContext db, ITenantContext tenant, IKeycloakAdmin keycloak, IOptions<IdentityOptions> options, ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        var mode = (await db.IdentitySettings.AsNoTracking().SingleOrDefaultAsync(cancellationToken))?.Passkeys ?? PasskeySignIn.Allowed;
        try
        {
            var passkeys = await keycloak.ListPasskeysAsync(options.Value.RealmFor(tenant.Slug!), user.Account!.Subject, cancellationToken);
            return TypedResults.Ok(new MyPasskeysResponse(mode, IsStaff(user), [.. passkeys.Select(p => new PasskeyResponse(p.Id, p.Label, p.CreatedAt))]));
        }
        catch (Exception ex) when (ex is KeycloakAdminException or HttpRequestException)
        {
            LogKeycloakRefused(loggers.CreateLogger("Zelvo.Identity"), ex);
            return Unavailable();
        }
    }

    internal static async Task<IResult> RemoveMineAsync(
        string id, CurrentUser user, ITenantContext tenant, IKeycloakAdmin keycloak, IOptions<IdentityOptions> options, IAuditLog audit,
        ILoggerFactory loggers, CancellationToken cancellationToken)
    {
        try
        {
            if (!await keycloak.DeletePasskeyAsync(options.Value.RealmFor(tenant.Slug!), user.Account!.Subject, id, cancellationToken))
            {
                return ApiProblem.Create(StatusCodes.Status404NotFound, "passkey-not-found", "You have no passkey with this id.");
            }
        }
        catch (Exception ex) when (ex is KeycloakAdminException or HttpRequestException)
        {
            LogKeycloakRefused(loggers.CreateLogger("Zelvo.Identity"), ex);
            return Unavailable();
        }

        await audit.RecordAsync(AuditCategory.Security, "identity.passkey.removed", "user", user.Account.Id.ToString(),
            new Dictionary<string, object?> { ["credential"] = id }, cancellationToken);
        return TypedResults.NoContent();
    }

    internal static async Task<IResult> RemoveAllOfUserAsync(
        Guid id, ZelvoDbContext db, ITenantContext tenant, IKeycloakAdmin keycloak, IOptions<IdentityOptions> options, IAuditLog audit,
        ILoggerFactory loggers, CancellationToken cancellationToken)
    {
        var person = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == id, cancellationToken);
        if (person is null)
        {
            return ApiProblem.Create(StatusCodes.Status404NotFound, "user-not-found", "There is no user with this id.");
        }

        var realm = options.Value.RealmFor(tenant.Slug!);
        var removed = 0;
        try
        {
            foreach (var passkey in await keycloak.ListPasskeysAsync(realm, person.Subject, cancellationToken))
            {
                removed += await keycloak.DeletePasskeyAsync(realm, person.Subject, passkey.Id, cancellationToken) ? 1 : 0;
            }
        }
        catch (Exception ex) when (ex is KeycloakAdminException or HttpRequestException)
        {
            LogKeycloakRefused(loggers.CreateLogger("Zelvo.Identity"), ex);
            return Unavailable();
        }

        await audit.RecordAsync(AuditCategory.Security, "identity.passkeys.removed-by-admin", "user", id.ToString(),
            new Dictionary<string, object?> { ["count"] = removed }, cancellationToken);
        return TypedResults.Ok(new PasskeysRemovedResponse(removed));
    }

    internal static async Task<Results<Ok<IdentitySettingsResponse>, ValidationProblem, ProblemHttpResult>> UpdateSettingAsync(
        UpdatePasskeySettingsRequest request, ZelvoDbContext db, ITenantContext tenant, IKeycloakAdmin keycloak, IOptions<IdentityOptions> options,
        ILoggerFactory loggers, CancellationToken cancellationToken)
    {
        if (request.Mode is not { } mode || !Enum.IsDefined(mode))
        {
            return UserEndpoints.Validation("mode", "Choose Off, Allowed or RequiredForStaff.");
        }

        var settings = await db.IdentitySettings.SingleOrDefaultAsync(cancellationToken);
        if (settings is null)
        {
            settings = new IdentitySettings();
            db.IdentitySettings.Add(settings);
        }

        settings.Passkeys = mode;
        settings.UpdatedAt = DateTimeOffset.UtcNow;

        // Saved and applied together: if Keycloak refuses, nothing changes.
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        try
        {
            await keycloak.SetPasskeysAsync(options.Value.RealmFor(tenant.Slug!), mode, cancellationToken);
        }
        catch (Exception ex) when (ex is KeycloakAdminException or HttpRequestException)
        {
            LogKeycloakRefused(loggers.CreateLogger("Zelvo.Identity"), ex);
            return (ProblemHttpResult)ApiProblem.Create(StatusCodes.Status502BadGateway, "identity-provider-unavailable",
                "The sign-in service did not accept the change. Nothing was changed.");
        }

        await transaction.CommitAsync(cancellationToken);
        return TypedResults.Ok(IdentitySettingsResponse.From(settings));
    }

    /// <summary>Whether the person's roles need a second factor (staff), so a passkey can be their second factor.</summary>
    private static bool IsStaff(CurrentUser user) => user.Roles.Any(r => r.RequiresMfa);

    private static IResult Unavailable() =>
        ApiProblem.Create(StatusCodes.Status502BadGateway, "identity-provider-unavailable", "The sign-in service did not answer. Please try again.");
}

/// <summary>Your passkeys.</summary>
/// <param name="Mode">Off, Allowed or RequiredForStaff.</param>
/// <param name="SecondFactor">Whether your roles need a second factor, which a passkey sign-in gives.</param>
/// <param name="Passkeys">Your passkeys.</param>
internal sealed record MyPasskeysResponse(PasskeySignIn Mode, bool SecondFactor, IReadOnlyList<PasskeyResponse> Passkeys);

/// <summary>One passkey.</summary>
/// <param name="Id">Id.</param>
/// <param name="Label">Name given when it was added.</param>
/// <param name="CreatedAt">When it was added.</param>
internal sealed record PasskeyResponse(string Id, string? Label, DateTimeOffset? CreatedAt);

/// <summary>How many passkeys were removed.</summary>
/// <param name="Removed">Count.</param>
internal sealed record PasskeysRemovedResponse(int Removed);

/// <summary>The tenant's passkey setting.</summary>
public sealed class UpdatePasskeySettingsRequest
{
    /// <summary>Off, Allowed or RequiredForStaff.</summary>
    [Required]
    public PasskeySignIn? Mode { get; init; }
}
