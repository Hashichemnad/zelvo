using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;

namespace Zelvo.Api.Identity;

/// <summary>Registers sign-in, the current user and permission checks.</summary>
internal static class IdentityServiceCollectionExtensions
{
    public static IServiceCollection AddZelvoIdentity(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<IdentityOptions>().Bind(configuration.GetSection(IdentityOptions.Section));
        services.AddOptions<UaePassOptions>().Bind(configuration.GetSection(UaePassOptions.Section));
        services.AddHttpClient(OidcRealmSigningKeys.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(10));
        services.AddHttpClient(KeycloakAdminClient.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(30));
        services.AddSingleton<IRealmSigningKeys, OidcRealmSigningKeys>();
        services.AddScoped<IKeycloakAdmin, KeycloakAdminClient>();

        // People and OAuth clients send Keycloak tokens; integrations may send an API key instead (E1).
        services.AddScoped<Integrations.ApiClientDirectory>();
        services.AddAuthentication(Integrations.ZelvoAuthenticationSchemes.Default)
            .AddPolicyScheme(Integrations.ZelvoAuthenticationSchemes.Default, "Zelvo", o => o.ForwardDefaultSelector = Integrations.ZelvoAuthenticationSchemes.Select)
            .AddScheme<AuthenticationSchemeOptions, ZelvoBearerHandler>(ZelvoBearerHandler.SchemeName, null)
            .AddScheme<AuthenticationSchemeOptions, Integrations.ApiKeyAuthenticationHandler>(Integrations.ApiKeyAuthenticationHandler.SchemeName, null);

        services.AddScoped<CurrentUser>();
        services.AddScoped<IAuthorizationHandler, PermissionHandler>();
        services.AddSingleton<IAuthorizationPolicyProvider, PermissionPolicyProvider>();
        services.AddSingleton<IAuthorizationMiddlewareResultHandler, ZelvoAuthorizationResultHandler>();
        services.AddAuthorization();
        return services;
    }
}
