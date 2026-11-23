using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Zelvo.Data;
using Zelvo.Data.Integrations;
using Zelvo.Data.Tenancy;

namespace Zelvo.Api.Integrations;

/// <summary>Claims Zelvo adds for integrations.</summary>
internal static class ApiClientClaims
{
    /// <summary>
    /// The Zelvo id of the integration. In OAuth client tokens it is a hard-coded claim of that
    /// Keycloak client; for API keys the API key handler sets it.
    /// </summary>
    public const string ClientId = "zelvo_api_client";

    /// <summary>The OAuth client's id in Keycloak (authorised party).</summary>
    public const string AuthorizedParty = "azp";
}

/// <summary>
/// API keys: <c>zvk_&lt;12-character prefix&gt;_&lt;secret&gt;</c>. The prefix finds the key;
/// only the SHA-256 of the whole key is stored and compared in constant time. The key has 256
/// random bits, so a fast hash is enough (no password hashing needed).
/// </summary>
internal static class ApiKeys
{
    public const string Prefix = "zvk_";
    public const string HeaderName = "X-Api-Key";

    private const string PrefixAlphabet = "abcdefghijklmnopqrstuvwxyz0123456789";

    /// <summary>A new key: the prefix to store, the hash to store and the key to show once.</summary>
    public static (string KeyPrefix, byte[] Hash, string Key) Create()
    {
        var prefix = RandomNumberGenerator.GetString(PrefixAlphabet, ApiClient.KeyPrefixLength);
        var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var key = $"{Prefix}{prefix}_{secret}";
        return (prefix, Hash(key), key);
    }

    public static byte[] Hash(string key) => SHA256.HashData(Encoding.UTF8.GetBytes(key));

    /// <summary>The prefix of a well-formed key, or null.</summary>
    public static string? PrefixOf(string? key)
    {
        if (key is null || key.Length < Prefix.Length + ApiClient.KeyPrefixLength + 2 || key.Length > 200
            || !key.StartsWith(Prefix, StringComparison.Ordinal) || key[Prefix.Length + ApiClient.KeyPrefixLength] != '_')
        {
            return null;
        }

        var prefix = key.Substring(Prefix.Length, ApiClient.KeyPrefixLength);
        return prefix.All(PrefixAlphabet.Contains) ? prefix : null;
    }
}

/// <summary>
/// Signs in integrations that send an API key in <c>X-Api-Key</c>. The key must belong to the
/// request's tenant, be active and not expired. What the integration may do is worked out by
/// <c>CurrentUserMiddleware</c> from its role and scopes.
/// </summary>
internal sealed partial class ApiKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    ITenantContext tenantContext,
    ApiClientDirectory directory)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "ZelvoApiKey";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(ApiKeys.HeaderName, out var values))
        {
            return AuthenticateResult.NoResult();
        }

        if (tenantContext.TenantId is null)
        {
            return AuthenticateResult.Fail("An API key needs a tenant.");
        }

        var key = values.Count == 1 ? values[0]?.Trim() : null;
        var prefix = ApiKeys.PrefixOf(key);
        var client = prefix is null ? null : await directory.FindKeyAsync(prefix, Context.RequestAborted).ConfigureAwait(false);
        if (client?.KeyHash is not { } hash || !CryptographicOperations.FixedTimeEquals(hash, ApiKeys.Hash(key!)))
        {
            LogRefused(Logger, tenantContext.Slug);
            return AuthenticateResult.Fail("The API key is not valid for this tenant.");
        }

        if (!client.IsUsable(DateTimeOffset.UtcNow))
        {
            return AuthenticateResult.Fail("The API key is revoked or expired.");
        }

        var identity = new ClaimsIdentity(
            [
                new Claim("sub", $"apikey:{client.Id}"),
                new Claim(ApiClientClaims.ClientId, client.Id.ToString()),
                new Claim("name", client.Name),
            ],
            SchemeName, "name", "zelvo_no_roles_in_tokens");
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.Headers.WWWAuthenticate = "Bearer";
        return base.HandleChallengeAsync(properties);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Refused an API key for tenant {Tenant}.")]
    private static partial void LogRefused(ILogger logger, string? tenant);
}

/// <summary>
/// Loads the calling integration once per request, with its role, the role's permissions and
/// field rules. Never cached across requests, so a revocation applies to the next call.
/// </summary>
internal sealed class ApiClientDirectory(ZelvoDbContext db)
{
    private readonly Dictionary<Guid, ApiClient?> _clients = [];
    private readonly Dictionary<string, ApiClient?> _keys = new(StringComparer.Ordinal);

    public async Task<ApiClient?> FindKeyAsync(string prefix, CancellationToken cancellationToken)
    {
        if (!_keys.TryGetValue(prefix, out var client))
        {
            client = await db.ApiClients.AsNoTracking()
                .SingleOrDefaultAsync(c => c.Kind == ApiClientKind.ApiKey && c.KeyPrefix == prefix, cancellationToken).ConfigureAwait(false);
            _keys[prefix] = client;
            if (client is not null)
            {
                _clients[client.Id] = client;
            }
        }

        return client;
    }

    public async Task<ApiClient?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        if (!_clients.TryGetValue(id, out var client))
        {
            client = await db.ApiClients.AsNoTracking().SingleOrDefaultAsync(c => c.Id == id, cancellationToken).ConfigureAwait(false);
            _clients[id] = client;
        }

        return client;
    }

    /// <summary>The integration of a signed-in principal (API key or OAuth client token), or null for people.</summary>
    public async Task<ApiClient?> ForPrincipalAsync(ClaimsPrincipal principal, CancellationToken cancellationToken)
    {
        if (principal.FindFirstValue(ApiClientClaims.ClientId) is not { } value || !Guid.TryParse(value, out var id))
        {
            return null;
        }

        var client = await GetAsync(id, cancellationToken).ConfigureAwait(false);
        if (client is null)
        {
            return null;
        }

        // An OAuth token must come from that client's own Keycloak client and service account.
        if (principal.Identity?.AuthenticationType != ApiKeyAuthenticationHandler.SchemeName
            && (client.Kind != ApiClientKind.OAuthClient
                || principal.FindFirstValue(ApiClientClaims.AuthorizedParty) != client.KeycloakClientId
                || principal.FindFirstValue("sub") != client.ServiceAccountSubject))
        {
            return null;
        }

        return client;
    }
}

/// <summary>Chooses the API key handler when <c>X-Api-Key</c> is sent, and Keycloak tokens otherwise.</summary>
internal static class ZelvoAuthenticationSchemes
{
    public const string Default = "Zelvo";

    public static string Select(HttpContext context) =>
        context.Request.Headers.ContainsKey(ApiKeys.HeaderName) ? ApiKeyAuthenticationHandler.SchemeName : Identity.ZelvoBearerHandler.SchemeName;
}
