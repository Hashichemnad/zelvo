using Zelvo.Api.Conventions;
using Zelvo.Api.Identity;
using Zelvo.Api.Integrations.Bulk;
using Zelvo.Integrations;
using Zelvo.Licensing;

namespace Zelvo.Api.Integrations;

/// <summary>Registers module 14 in the API: integrations, webhook events, bulk types.</summary>
internal static class IntegrationsSetup
{
    public static IServiceCollection AddZelvoApiIntegrations(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddZelvoIntegrations(configuration);
        services.AddWebhookEventSource<IdentityWebhookEvents>();
        services.AddScoped<IBulkType, UsersBulkType>();
        return services;
    }

    public static RouteGroupBuilder MapIntegrationEndpoints(this RouteGroupBuilder group) =>
        group.MapApiClientEndpoints().MapWebhookEndpoints().MapBulkEndpoints();

    /// <summary>
    /// Every API response says which kind of installation answered (<c>Zelvo-Environment</c>:
    /// production, test, demo or development), so that integrations can tell the test
    /// installation from the live one (module 14).
    /// </summary>
    public static IApplicationBuilder UseZelvoEnvironmentHeader(this IApplicationBuilder app) =>
        app.Use((context, next) =>
        {
            var licenses = context.RequestServices.GetRequiredService<ILicenseService>();
            context.Response.Headers["Zelvo-Environment"] = licenses.EnvironmentName();
            return next(context);
        });

    /// <summary>
    /// Only for people signed in (their own profile and preferences): integrations get 403
    /// <c>people-only</c>.
    /// </summary>
    public static TBuilder RequirePerson<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
    {
        builder.RequireAuthorization();
        builder.AddEndpointFilter(async (context, next) =>
            context.HttpContext.RequestServices.GetRequiredService<CurrentUser>().Account is null
                ? ApiProblem.Create(StatusCodes.Status403Forbidden, "people-only", "This endpoint is for people who sign in, not for integrations.")
                : await next(context).ConfigureAwait(false));
        return builder;
    }
}
