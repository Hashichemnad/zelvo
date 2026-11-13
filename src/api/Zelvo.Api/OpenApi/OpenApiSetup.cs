using Scalar.AspNetCore;

namespace Zelvo.Api.OpenApi;

/// <summary>
/// OpenAPI document at <c>/openapi/v1.json</c> and the built-in API explorer at
/// <c>/api-explorer</c> (module 14). Both are on in Development and otherwise only when
/// <c>OpenApi:Enabled</c> is true. The explorer's assets are bundled, so it works offline.
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
                "Errors are RFC 9457 problem details with a stable 'code'. Lists take page and pageSize (at most 100).";
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
