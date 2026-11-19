using System.Text.Json.Serialization;
using Zelvo.Api;
using Zelvo.Api.Auditing;
using Zelvo.Api.Commands;
using Zelvo.Api.Conventions;
using Zelvo.Api.Demo;
using Zelvo.Api.Health;
using Zelvo.Api.Identity;
using Zelvo.Api.Localization;
using Zelvo.Api.OpenApi;
using Zelvo.Api.Platform;
using Zelvo.Api.Search;
using Zelvo.Api.Security;
using Zelvo.Api.Security.RateLimiting;
using Zelvo.Api.Settings;
using Zelvo.Api.Tenancy;
using Zelvo.Data;
using Zelvo.Data.Localization;
using Zelvo.Data.Tenancy;
using Zelvo.Licensing;
using OpenTelemetry.Trace;
using Zelvo.Observability;
using Zelvo.Search;
using Zelvo.Secrets;

if (args is ["migrate", ..])
{
    return await MigrateCommand.RunAsync(args[1..]);
}

if (args is ["rewrap-keys", ..])
{
    return await RewrapKeysCommand.RunAsync(args[1..]);
}

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.ConfigureKestrel(options => options.AddServerHeader = false);

// Traces, metrics and logs over OTLP when OTEL_EXPORTER_OTLP_ENDPOINT is set (A5).
builder.AddZelvoTelemetry("zelvo-api",
    tracing => tracing.AddAspNetCoreInstrumentation(http =>
    {
        // Probes are not traced; request spans carry the route, never the query string.
        http.Filter = context => !context.Request.Path.StartsWithSegments("/health", StringComparison.OrdinalIgnoreCase);
        http.EnrichWithHttpRequest = (activity, _) => activity.SetTag("url.query", null);
    }),
    metrics => metrics.AddMeter("Microsoft.AspNetCore.Hosting", "Microsoft.AspNetCore.Server.Kestrel", "Microsoft.AspNetCore.Diagnostics"));

builder.Services.AddProblemDetails(options => options.CustomizeProblemDetails = ApiProblem.Customize);
builder.Services.AddValidation();
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddZelvoOpenApi();
builder.Services.AddZelvoLicensing();
builder.Services.AddZelvoHealthChecks(builder.Configuration);
builder.Services.AddZelvoIdentity(builder.Configuration);

builder.Services.AddZelvoData(builder.Configuration.GetConnectionString("Postgres")
    ?? throw new InvalidOperationException("Configuration value 'ConnectionStrings:Postgres' is required."));
builder.Services.AddMemoryCache();
builder.Services.AddScoped<TenantResolver>();
builder.Services.AddScoped<TenantSettingsStore>();
builder.Services.AddSingleton<IMissingTranslationSource, TenantMissingTranslations>();
builder.Services.AddZelvoSearch(builder.Configuration);
builder.Services.AddZelvoSecrets(builder.Configuration);
builder.Services.AddZelvoRateLimiting(builder.Configuration);
builder.Services.AddZelvoDemo();

var app = builder.Build();

// Client address and scheme from trusted proxies only (Network:TrustedProxies).
app.UseForwardedHeaders();
app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseMiddleware<SecurityHeadersMiddleware>();

// The tenant comes first: tokens are checked against the tenant's own Keycloak realm.
app.UseMiddleware<TenantResolutionMiddleware>();
app.UseAuthentication();
app.UseMiddleware<RateLimitingMiddleware>();
app.UseMiddleware<AuditContextMiddleware>();
app.UseMiddleware<CurrentUserMiddleware>();
app.UseAuthorization();

app.MapZelvoOpenApi();
app.MapZelvoHealthChecks();

var v1 = app.MapGroup("/api/v1");
v1.MapSystemEndpoints();
v1.MapTenantEndpoints();
v1.MapAuditEndpoints();
v1.MapTranslationEndpoints();
v1.MapMeEndpoints();
v1.MapUserEndpoints();
v1.MapRoleEndpoints();
v1.MapIdentitySettingsEndpoints();
v1.MapUaePassEndpoints();
v1.MapPreferenceEndpoints();
v1.MapSettingsEndpoints();
v1.MapSearchEndpoints();
v1.MapDemoEndpoints();

await app.RunAsync();
return 0;

namespace Zelvo.Api
{
    /// <summary>Entry point marker for integration tests.</summary>
    public partial class Program;
}
