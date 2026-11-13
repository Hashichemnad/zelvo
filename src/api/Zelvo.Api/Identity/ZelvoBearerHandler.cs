using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Zelvo.Data.Tenancy;

namespace Zelvo.Api.Identity;

/// <summary>
/// Accepts Keycloak access tokens (<c>Authorization: Bearer</c>) issued by the realm of the
/// request's tenant only, for the API audience, signed with that realm's keys and carrying the
/// tenant's id. A token from another tenant's realm is invalid here, whatever header is sent.
/// </summary>
internal sealed partial class ZelvoBearerHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IOptions<IdentityOptions> identity,
    IRealmSigningKeys signingKeys,
    ITenantContext tenantContext)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "ZelvoBearer";

    private static readonly JsonWebTokenHandler Tokens = new() { MapInboundClaims = false };

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return AuthenticateResult.NoResult();
        }

        var token = header["Bearer ".Length..].Trim();
        if (tenantContext.TenantId is not { } tenantId || tenantContext.Slug is not { } slug)
        {
            return AuthenticateResult.Fail("A signed-in request needs a tenant.");
        }

        var settings = identity.Value;
        var realm = settings.RealmFor(slug);
        IReadOnlyCollection<SecurityKey> keys;
        try
        {
            keys = await signingKeys.GetAsync(realm, Context.RequestAborted).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or IOException)
        {
            LogKeysUnavailable(Logger, realm, ex);
            return AuthenticateResult.Fail("The identity provider is not reachable.");
        }

        var result = await Tokens.ValidateTokenAsync(token, new TokenValidationParameters
        {
            ValidIssuer = settings.IssuerFor(slug),
            ValidAudience = settings.Audience,
            IssuerSigningKeys = keys,
            ValidateIssuerSigningKey = true,
            RequireSignedTokens = true,
            RequireExpirationTime = true,
            ClockSkew = settings.ClockSkew,
            ValidAlgorithms = [SecurityAlgorithms.RsaSha256, SecurityAlgorithms.RsaSsaPssSha256, SecurityAlgorithms.EcdsaSha256],
            NameClaimType = "name",
            RoleClaimType = "zelvo_no_roles_in_tokens",
            AuthenticationType = SchemeName,
        }).ConfigureAwait(false);

        if (!result.IsValid)
        {
            LogRefused(Logger, realm, result.Exception);
            return AuthenticateResult.Fail("The access token is not valid for this tenant.");
        }

        var identityClaims = result.ClaimsIdentity;
        if (identityClaims.FindFirst("sub")?.Value is not { Length: > 0 }
            || identityClaims.FindFirst(ZelvoClaimTypes.TenantId)?.Value != tenantId.ToString())
        {
            return AuthenticateResult.Fail("The access token does not belong to this tenant.");
        }

        var principal = new ClaimsPrincipal(identityClaims);
        return AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName));
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not read the signing keys of realm {Realm}.")]
    private static partial void LogKeysUnavailable(ILogger logger, string realm, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Refused a token for realm {Realm}.")]
    private static partial void LogRefused(ILogger logger, string realm, Exception? exception);

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.Headers.WWWAuthenticate = "Bearer";
        return base.HandleChallengeAsync(properties);
    }
}

/// <summary>Claim types in Zelvo access tokens.</summary>
internal static class ZelvoClaimTypes
{
    /// <summary>Id of the tenant whose realm issued the token (a hard-coded claim per realm).</summary>
    public const string TenantId = "zelvo_tenant_id";

    /// <summary>Keycloak user id.</summary>
    public const string Subject = "sub";

    /// <summary>Authentication context class: <c>mfa</c> after a second factor.</summary>
    public const string Acr = "acr";
}
