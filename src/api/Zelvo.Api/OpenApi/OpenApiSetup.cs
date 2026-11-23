using Microsoft.OpenApi;
using Scalar.AspNetCore;

namespace Zelvo.Api.OpenApi;

/// <summary>
/// OpenAPI document at <c>/openapi/v1.json</c> and the built-in API explorer at
/// <c>/api-explorer</c> (module 14). Both are on in Development and otherwise only when
/// <c>OpenApi:Enabled</c> is true. The explorer's assets are bundled, so it works offline. The
/// document declares the API key and bearer schemes, so calls can be tried with an
/// administrator's own test key (E1).
/// </summary>
internal static class OpenApiSetup
{
    public const string ExplorerPath = "/api-explorer";

    public static IServiceCollection AddZelvoOpenApi(this IServiceCollection services) =>
        services.AddOpenApi(options => options.AddDocumentTransformer((document, _, _) =>
        {
            document.Info.Title = "Zelvo API";
            document.Info.Version = "v1";
            document.Info.Description =
                "REST API of Zelvo. Send the tenant slug in the X-Zelvo-Tenant header (single-tenant installations may omit it). " +
                "Errors are RFC 9457 problem details with a stable 'code'. Lists take page and pageSize (at most 100). " +
                "Integrations sign in with an API key (X-Api-Key) or an OAuth client's access token (client credentials grant); " +
                "administrators create both in Settings → Integrations and can try calls here with their own test key.";

            // Both ways of signing in, so the explorer can send an administrator's test key (E1).
            document.Components ??= new OpenApiComponents();
            document.Components.SecuritySchemes = new Dictionary<string, IOpenApiSecurityScheme>(StringComparer.Ordinal)
            {
                ["apiKey"] = new OpenApiSecurityScheme
                {
                    Type = SecuritySchemeType.ApiKey,
                    In = ParameterLocation.Header,
                    Name = "X-Api-Key",
                    Description = "An API key from Settings → Integrations (zvk_…).",
                },
                ["bearer"] = new OpenApiSecurityScheme
                {
                    Type = SecuritySchemeType.Http,
                    Scheme = "bearer",
                    BearerFormat = "JWT",
                    Description = "An access token from the tenant's Keycloak realm (people, or OAuth clients with the client credentials grant).",
                },
            };
            document.Security =
            [
                new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference("apiKey", document)] = [] },
                new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference("bearer", document)] = [] },
            ];
            return Task.CompletedTask;
        }));

    public static bool IsEnabled(WebApplication app) =>
        app.Environment.IsDevelopment() || app.Configuration.GetValue<bool>("OpenApi:Enabled");

    public static WebApplication MapZelvoOpenApi(this WebApplication app)
    {
        if (!IsEnabled(app))
        {
            return app;
        }

        app.MapOpenApi();
        app.MapScalarApiReference(ExplorerPath, options => options
            .WithTitle("Zelvo API")
            .DisableDefaultFonts()
            .DisableTelemetry()
            .DisableAgent()
            .DisableMcp()
            .HideClientButton()
            .HideDeveloperTools());
        return app;
    }
}
