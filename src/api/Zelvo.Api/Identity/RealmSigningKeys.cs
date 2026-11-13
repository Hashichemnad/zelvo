using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Zelvo.Api.Identity;

/// <summary>Signing keys of a tenant realm.</summary>
internal interface IRealmSigningKeys
{
    Task<IReadOnlyCollection<SecurityKey>> GetAsync(string realm, CancellationToken cancellationToken);
}

/// <summary>
/// Reads each realm's keys from its OpenID metadata over the internal Keycloak address, cached and
/// refreshed by <see cref="ConfigurationManager{T}"/> (also when a token names an unknown key).
/// </summary>
internal sealed class OidcRealmSigningKeys(IOptions<IdentityOptions> options, IHttpClientFactory httpClients) : IRealmSigningKeys
{
    public const string HttpClientName = "keycloak-metadata";

    private readonly ConcurrentDictionary<string, ConfigurationManager<OpenIdConnectConfiguration>> _managers = new(StringComparer.Ordinal);

    public async Task<IReadOnlyCollection<SecurityKey>> GetAsync(string realm, CancellationToken cancellationToken)
    {
        var manager = _managers.GetOrAdd(realm, Create);
        var configuration = await manager.GetConfigurationAsync(cancellationToken).ConfigureAwait(false);
        return [.. configuration.SigningKeys];
    }

    private ConfigurationManager<OpenIdConnectConfiguration> Create(string realm)
    {
        var baseUrl = options.Value.BackchannelUrl;
        var retriever = new HttpDocumentRetriever(httpClients.CreateClient(HttpClientName))
        {
            RequireHttps = baseUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase),
        };
        return new ConfigurationManager<OpenIdConnectConfiguration>(
            $"{baseUrl}/realms/{Uri.EscapeDataString(realm)}/.well-known/openid-configuration",
            new OpenIdConnectConfigurationRetriever(),
            retriever)
        {
            AutomaticRefreshInterval = TimeSpan.FromHours(1),
            RefreshInterval = TimeSpan.FromSeconds(30),
        };
    }
}
