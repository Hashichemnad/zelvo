using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;
using Zelvo.Api.Conventions;
using Zelvo.Api.Settings;
using Zelvo.Api.Tenancy;
using Zelvo.Data;
using Zelvo.Data.Tenancy;

namespace Zelvo.Api.Identity;

/// <summary>Sign-in with UAE PASS (GOV-1): Settings → UAE PASS, and what the sign-in pages need to know.</summary>
internal static partial class UaePassEndpointMappings
{
    [LoggerMessage(Level = LogLevel.Error, Message = "Could not apply the UAE PASS settings to Keycloak.")]
    private static partial void LogKeycloakRefused(ILogger logger, Exception exception);

    public static RouteGroupBuilder MapUaePassEndpoints(this RouteGroupBuilder group)
    {
        var uaePass = group.MapGroup("/identity/uae-pass").WithTags("Identity").RequireTenant();

        uaePass.MapGet("/", GetAsync)
            .WithName("GetUaePassSettings")
            .WithSummary("The tenant's UAE PASS sign-in settings, the environments this installation offers and the redirect URI to register with UAE PASS. The client secret is never returned. Needs identity.manage.")
            .RequirePermission(Permissions.IdentityManage);

        uaePass.MapPut("/", UpdateAsync)
            .WithName("UpdateUaePassSettings")
            .WithSummary("Turn UAE PASS sign-in on or off; set the environment (Staging or Production), client ID and client secret issued by the UAE PASS programme; and the GOV-1 rules: UAE PASS as the second factor for staff (SOP2+ only), the requesters' minimum account level, and automatic requester accounts. Leave clientSecret or a rule out to keep it. Applied to the tenant's Keycloak realm; audited without the secret. Needs identity.manage.")
            .RequirePermission(Permissions.IdentityManage)
            .ProducesValidationProblem();

        group.MapGet("/identity/sign-in-methods", GetSignInMethodsAsync)
            .WithTags("Identity")
            .RequireTenant()
            .AllowAnonymousAccess()
            .WithName("GetSignInMethods")
            .WithSummary("Sign-in methods the tenant offers besides password and email code: whether UAE PASS is on, and where to end a UAE PASS session when signing out. Open to visitors.");

        return group;
    }

    internal static async Task<Ok<UaePassSettingsResponse>> GetAsync(
        TenantSettingsStore store, ITenantContext tenant, IOptions<IdentityOptions> identity, IOptions<UaePassOptions> uaePass,
        CancellationToken cancellationToken) =>
        TypedResults.Ok(await UaePassSettingsResponse.LoadAsync(store, tenant, identity.Value, uaePass.Value, cancellationToken));

    internal static async Task<Results<Ok<UaePassSettingsResponse>, ValidationProblem, ProblemHttpResult>> UpdateAsync(
        UpdateUaePassSettingsRequest request, TenantSettingsStore store, ZelvoDbContext db, ITenantContext tenant,
        IKeycloakAdmin keycloak, IOptions<IdentityOptions> identity, IOptions<UaePassOptions> uaePass, ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        var options = uaePass.Value;
        var environment = request.Environment ?? UaePassEnvironment.Staging;
        if (!Enum.IsDefined(environment) || !options.Environments.Contains(environment))
        {
            return UserEndpoints.Validation("environment", $"Use one of: {string.Join(", ", options.Environments)}.");
        }

        var clientId = request.ClientId?.Trim() ?? string.Empty;
        if (!UaePassSettings.IsClientId(clientId))
        {
            return UserEndpoints.Validation("clientId",
                $"Use the client ID issued by UAE PASS: up to {UaePassSettings.MaxClientIdLength} characters, without spaces.");
        }

        var secret = string.IsNullOrEmpty(request.ClientSecret) ? null : request.ClientSecret;
        if (secret is not null && !UaePassSettings.IsClientSecret(secret))
        {
            return UserEndpoints.Validation("clientSecret",
                $"Use the client secret issued by UAE PASS: up to {UaePassSettings.MaxClientSecretLength} characters.");
        }

        var secretChangedAt = await store.GetAsync(UaePassSettings.ClientSecretChangedAt, cancellationToken);
        var enabled = request.Enabled!.Value;
        if (enabled && clientId.Length == 0)
        {
            return UserEndpoints.Validation("clientId", "Enter the client ID before turning UAE PASS on.");
        }

        if (enabled && secret is null && secretChangedAt == DateTimeOffset.MinValue)
        {
            return UserEndpoints.Validation("clientSecret", "Enter the client secret before turning UAE PASS on.");
        }

        await store.SetAsync(UaePassSettings.Enabled, enabled, cancellationToken);
        await store.SetAsync(UaePassSettings.Environment, environment, cancellationToken);
        await store.SetAsync(UaePassSettings.ClientId, clientId, cancellationToken);
        if (request.RequesterMinimumLevel is { } minimum && !Enum.IsDefined(minimum))
        {
            return UserEndpoints.Validation("requesterMinimumLevel", "Use SOP1, SOP2 or SOP3.");
        }

        if (request.AcceptAsSecondFactor is { } acceptAsSecondFactor)
        {
            await store.SetAsync(UaePassSettings.AcceptAsSecondFactor, acceptAsSecondFactor, cancellationToken);
        }

        if (request.RequesterMinimumLevel is { } requesterMinimum)
        {
            await store.SetAsync(UaePassSettings.RequesterMinimumLevel, requesterMinimum, cancellationToken);
        }

        if (request.CreateRequesters is { } createRequesters)
        {
            await store.SetAsync(UaePassSettings.CreateRequesters, createRequesters, cancellationToken);
        }
        if (secret is not null)
        {
            await store.SetAsync(UaePassSettings.ClientSecretChangedAt, DateTimeOffset.UtcNow, cancellationToken);
        }

        // Saved and applied together: if Keycloak refuses, nothing changes. Nothing is sent to
        // Keycloak while UAE PASS was never set up and stays off.
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        var configured = clientId.Length > 0 && (secret is not null || secretChangedAt != DateTimeOffset.MinValue);
        if (configured)
        {
            try
            {
                await keycloak.ApplyUaePassAsync(identity.Value.RealmFor(tenant.Slug!),
                    new UaePassProvider(enabled, options.EndpointsFor(environment), clientId, secret,
                        await store.GetAsync(UaePassSettings.CreateRequesters, cancellationToken),
                        await store.GetAsync(UaePassSettings.RequesterMinimumLevel, cancellationToken)), cancellationToken);
            }
            catch (Exception ex) when (ex is KeycloakAdminException or HttpRequestException)
            {
                LogKeycloakRefused(loggers.CreateLogger("Zelvo.Identity"), ex);
                return (ProblemHttpResult)ApiProblem.Create(StatusCodes.Status502BadGateway, "identity-provider-unavailable",
                    "The sign-in service did not accept the change. Nothing was changed.");
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return TypedResults.Ok(await UaePassSettingsResponse.LoadAsync(store, tenant, identity.Value, options, cancellationToken));
    }

    internal static async Task<Ok<SignInMethodsResponse>> GetSignInMethodsAsync(
        TenantSettingsStore store, IOptions<UaePassOptions> uaePass, CancellationToken cancellationToken)
    {
        var enabled = await store.GetAsync(UaePassSettings.Enabled, cancellationToken);
        var environment = await store.GetAsync(UaePassSettings.Environment, cancellationToken);
        var available = enabled && uaePass.Value.Environments.Contains(environment);
        return TypedResults.Ok(new SignInMethodsResponse(new UaePassSignIn(
            available, available ? uaePass.Value.EndpointsFor(environment).Logout : null)));
    }
}

/// <summary>The tenant's UAE PASS settings.</summary>
/// <param name="Enabled">Whether the sign-in page offers UAE PASS.</param>
/// <param name="Environment">Staging, Production or (development installations only) Mock.</param>
/// <param name="ClientId">Client ID issued by the UAE PASS programme; empty until set.</param>
/// <param name="ClientSecretSet">Whether a client secret was given. The secret itself is never returned.</param>
/// <param name="ClientSecretChangedAt">When the client secret was last given.</param>
/// <param name="RedirectUri">The address to register with UAE PASS as this tenant's redirect URI.</param>
/// <param name="Environments">The environments this installation offers.</param>
/// <param name="AcceptAsSecondFactor">Whether a verified (SOP2+) UAE PASS sign-in counts as the second factor for staff.</param>
/// <param name="RequesterMinimumLevel">The lowest UAE PASS account level requesters may sign in with.</param>
/// <param name="StaffMinimumLevel">The lowest level for staff: always SOP2, not configurable.</param>
/// <param name="CreateRequesters">Whether a first UAE PASS sign-in without an account creates a requester account.</param>
/// <param name="UpdatedAt">Last change; null while never changed.</param>
internal sealed record UaePassSettingsResponse(
    bool Enabled,
    UaePassEnvironment Environment,
    string ClientId,
    bool ClientSecretSet,
    DateTimeOffset? ClientSecretChangedAt,
    string RedirectUri,
    IReadOnlyList<UaePassEnvironment> Environments,
    bool AcceptAsSecondFactor,
    UaePassLevel RequesterMinimumLevel,
    UaePassLevel StaffMinimumLevel,
    bool CreateRequesters,
    DateTimeOffset? UpdatedAt)
{
    public static async Task<UaePassSettingsResponse> LoadAsync(
        TenantSettingsStore store, ITenantContext tenant, IdentityOptions identity, UaePassOptions options, CancellationToken cancellationToken)
    {
        var secretChangedAt = await store.GetAsync(UaePassSettings.ClientSecretChangedAt, cancellationToken).ConfigureAwait(false);
        return new(
            await store.GetAsync(UaePassSettings.Enabled, cancellationToken).ConfigureAwait(false),
            await store.GetAsync(UaePassSettings.Environment, cancellationToken).ConfigureAwait(false),
            await store.GetAsync(UaePassSettings.ClientId, cancellationToken).ConfigureAwait(false),
            secretChangedAt != DateTimeOffset.MinValue,
            secretChangedAt == DateTimeOffset.MinValue ? null : secretChangedAt,
            $"{identity.IssuerFor(tenant.Slug!)}/broker/{UaePassProvider.Alias}/endpoint",
            options.Environments,
            await store.GetAsync(UaePassSettings.AcceptAsSecondFactor, cancellationToken).ConfigureAwait(false),
            await store.GetAsync(UaePassSettings.RequesterMinimumLevel, cancellationToken).ConfigureAwait(false),
            UaePassSettings.StaffMinimumLevel,
            await store.GetAsync(UaePassSettings.CreateRequesters, cancellationToken).ConfigureAwait(false),
            await store.LastChangedAsync(UaePassSettings.All, cancellationToken).ConfigureAwait(false));
    }
}

/// <summary>New UAE PASS settings.</summary>
public sealed class UpdateUaePassSettingsRequest
{
    /// <summary>Whether the sign-in page offers UAE PASS. Turning it on needs a client ID and secret.</summary>
    [System.ComponentModel.DataAnnotations.Required]
    public bool? Enabled { get; init; }

    /// <summary>Staging (default) or Production; Mock on development installations.</summary>
    public UaePassEnvironment? Environment { get; init; }

    /// <summary>Client ID issued by UAE PASS.</summary>
    [System.ComponentModel.DataAnnotations.MaxLength(UaePassSettings.MaxClientIdLength)]
    public string? ClientId { get; init; }

    /// <summary>Client secret issued by UAE PASS. Leave out or empty to keep the current one.</summary>
    [System.ComponentModel.DataAnnotations.MaxLength(UaePassSettings.MaxClientSecretLength)]
    public string? ClientSecret { get; init; }

    /// <summary>Accept a verified (SOP2+) UAE PASS sign-in as the second factor for staff. Leave out to keep.</summary>
    public bool? AcceptAsSecondFactor { get; init; }

    /// <summary>SOP1, SOP2 or SOP3: the lowest level requesters may sign in with. Leave out to keep.</summary>
    public UaePassLevel? RequesterMinimumLevel { get; init; }

    /// <summary>Create a requester account at a first UAE PASS sign-in without an account. Leave out to keep.</summary>
    public bool? CreateRequesters { get; init; }
}

/// <summary>Sign-in methods the tenant offers besides password and email code.</summary>
internal sealed record SignInMethodsResponse(UaePassSignIn UaePass);

/// <summary>UAE PASS sign-in.</summary>
/// <param name="Enabled">Whether the sign-in page offers UAE PASS.</param>
/// <param name="SignOutUrl">UAE PASS sign-out address; add <c>redirect_uri</c> to come back.</param>
internal sealed record UaePassSignIn(bool Enabled, string? SignOutUrl);
