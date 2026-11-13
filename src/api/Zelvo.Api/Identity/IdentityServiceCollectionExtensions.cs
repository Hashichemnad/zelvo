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
        services.AddHttpClient(OidcRealmSigningKeys.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(10));
        services.AddHttpClient(KeycloakAdminClient.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(30));
        services.AddSingleton<IRealmSigningKeys, OidcRealmSigningKeys>();
        services.AddScoped<IKeycloakAdmin, KeycloakAdminClient>();

        services.AddAuthentication(ZelvoBearerHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, ZelvoBearerHandler>(ZelvoBearerHandler.SchemeName, null);

        services.AddScoped<CurrentUser>();
        services.AddScoped<IAuthorizationHandler, PermissionHandler>();
        services.AddSingleton<IAuthorizationPolicyProvider, PermissionPolicyProvider>();
        services.AddSingleton<IAuthorizationMiddlewareResultHandler, ZelvoAuthorizationResultHandler>();
        services.AddAuthorization();
        return services;
    }
}
