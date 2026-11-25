using Zelvo.Workflows;
using Zelvo.Workflows.Engine;
using Zelvo.Api.ServiceDesk;
using System.Text.Json.Serialization;
using Zelvo.Api;
using Zelvo.Api.Ai;
using Zelvo.Api.Auditing;
using Zelvo.Api.Channels;
using Zelvo.Api.Commands;
using Zelvo.Api.Conventions;
using Zelvo.Api.Demo;
using Zelvo.Api.Forms;
using Zelvo.Api.Health;
using Zelvo.Api.Identity;
using Zelvo.Api.Incidents;
using Zelvo.Api.Installation;
using Zelvo.Api.Integrations;
using Zelvo.Api.Catalog;
using Zelvo.Api.Payments;
using Zelvo.Api.Privacy;
using Zelvo.Api.Portal;
using Zelvo.Api.Voice;
using Zelvo.Api.Knowledge;
using Zelvo.Api.Notifications;
using Zelvo.Api.Localization;
using Zelvo.Api.Mcp;
using Zelvo.Api.OpenApi;
using Zelvo.Api.Platform;
using Zelvo.Api.Search;
using Zelvo.Api.Branding;
using Zelvo.Api.Security;
using Zelvo.Api.Security.RateLimiting;
using Zelvo.Api.Settings;
using Zelvo.Api.Sla;
using Zelvo.Api.Tenancy;
using Zelvo.Api.Reports;
using Zelvo.Api.Setup;
using Zelvo.Api.Workspace;
using Zelvo.Api.Workflows;
using Zelvo.Data;
using Zelvo.Data.Localization;
using Zelvo.Data.Tenancy;
using Zelvo.Connectors;
using Zelvo.Api.Connectors;
using Zelvo.Licensing;
using OpenTelemetry.Trace;
using Zelvo.Observability;
using Zelvo.Search;
using Zelvo.Secrets;
using Zelvo.Tickets;

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

var postgres = builder.Configuration.GetConnectionString("Postgres")
    ?? throw new InvalidOperationException("Configuration value 'ConnectionStrings:Postgres' is required.");
builder.Services.AddZelvoData(postgres);
// Task C3: workflow rules run with every save (before notifications and webhooks see the
// changes), and approval processes run on the embedded Elsa engine.
builder.Services.AddZelvoWorkflowRules();
builder.Services.AddZelvoWorkflowEngine(postgres);
builder.Services.AddMemoryCache();
builder.Services.AddScoped<TenantResolver>();
builder.Services.AddScoped<TenantSettingsStore>();
builder.Services.AddSingleton<IMissingTranslationSource, TenantMissingTranslations>();
builder.Services.AddZelvoSearch(builder.Configuration);
builder.Services.AddZelvoTickets(builder.Configuration);
builder.Services.AddSingleton<IMissingTranslationSource, IncidentMissingTranslations>();
builder.Services.AddSingleton<IMissingTranslationSource, DirectoryMissingTranslations>();
builder.Services.AddSingleton<IMissingTranslationSource, SlaMissingTranslations>();
builder.Services.AddSingleton<IMissingTranslationSource, FormMissingTranslations>();
builder.Services.AddZelvoSecrets(builder.Configuration);
builder.Services.AddZelvoRateLimiting(builder.Configuration);
builder.Services.AddZelvoDemo();
builder.Services.AddZelvoApiIntegrations(builder.Configuration);
builder.Services.AddZelvoApiAi(builder.Configuration);
builder.Services.AddZelvoKnowledge();
builder.Services.AddZelvoCatalog();
// Online payments (module 15, task E8).
builder.Services.AddZelvoPayments(builder.Configuration);
builder.Services.AddZelvoPrivacy();
builder.Services.AddZelvoCustomerVoice();
builder.Services.AddZelvoApiWorkflows();
builder.Services.AddZelvoMcpServer();
builder.Services.AddZelvoApiNotifications(builder.Configuration);
// Agent workspace (task B4): live updates over SignalR.
builder.Services.AddZelvoLiveUpdates();
builder.Services.AddZelvoApiChannels(builder.Configuration);
builder.Services.AddZelvoConnectors(builder.Configuration);
builder.Services.AddZelvoBranding(builder.Configuration);
builder.Services.AddZelvoReports();
// Task F4: where update packages are kept, and the running version.
builder.Services.AddZelvoUpdateOptions(builder.Configuration);
builder.Services.AddSingleton<InstalledVersionProvider>();

var app = builder.Build();

// Client address and scheme from trusted proxies only (Network:TrustedProxies).
app.UseForwardedHeaders();
app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseMiddleware<SecurityHeadersMiddleware>();
app.UseZelvoEnvironmentHeader();

// The tenant comes first: tokens are checked against the tenant's own Keycloak realm.
app.UseMiddleware<TenantResolutionMiddleware>();
app.UseAuthentication();
app.UseMiddleware<RateLimitingMiddleware>();
app.UseMiddleware<AuditContextMiddleware>();
app.UseMiddleware<CurrentUserMiddleware>();
// Task F3: activation mode and module gating.
app.UseMiddleware<LicenseEnforcementMiddleware>();
app.UseMiddleware<IntegrationLogMiddleware>();
app.UseZelvoMcpAccess();
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
v1.MapTreeEndpoints();
v1.MapGroupEndpoints();
v1.MapAssignmentRuleEndpoints();
v1.MapDepartmentEndpoints();
v1.MapFormEndpoints();
v1.MapFieldSetEndpoints();
v1.MapFormBindingEndpoints();
v1.MapFormUploadEndpoints();
// Task F5: UAE privacy law (GOV-5) applies whatever the license includes.
v1.MapPrivacyEndpoints();
v1.MapDataSubjectRequestEndpoints();
v1.MapZelvoWorkflowEndpoints();
v1.MapNotificationEndpoints();
v1.MapSetupEndpoints();
// Task F3: the license screens; the rest of each module answers only when the license includes it.
v1.MapLicenseEndpoints();
// Task F4: offline update packages.
v1.MapUpdateEndpoints();
v1.ForModule(LicensedModules.Incidents).MapIncidentEndpoints();
v1.ForModule(LicensedModules.Incidents).MapIncidentSettingsEndpoints();
v1.ForModule(LicensedModules.Sla).MapSlaEndpoints();
v1.ForModule(LicensedModules.Integrations).MapIntegrationEndpoints();
v1.ForModule(LicensedModules.Ai).MapAiEndpoints();
v1.ForModule(LicensedModules.Ai).MapAiFeatureEndpoints();
v1.ForModule(LicensedModules.Ai).MapZelvoMcp();
v1.ForModule(LicensedModules.Ai).MapMcpEndpoints();
v1.ForModule(LicensedModules.Ai).MapMcpServerEndpoints();
v1.ForModule(LicensedModules.KnowledgeBase).MapZelvoKnowledgeEndpoints();
v1.ForModule(LicensedModules.ServiceCatalog).MapZelvoCatalogEndpoints();
v1.ForModule(LicensedModules.Payments).MapPaymentEndpoints();
v1.ForModule(LicensedModules.CustomerVoice).MapZelvoCustomerVoiceEndpoints();
v1.ForModule(LicensedModules.Portal).MapPortalEndpoints();
v1.ForModule(LicensedModules.AgentWorkspace).MapWorkspaceEndpoints();
v1.ForModule(LicensedModules.NotificationsReporting).MapZelvoReportEndpoints();
app.MapZelvoLiveUpdates();
v1.ForModule(LicensedModules.Omnichannel).MapZelvoChannels();
// Task E7: identity, CRM and REST connectors and monitoring alerts (module 14).
v1.ForModule(LicensedModules.Integrations).MapConnectorEndpoints();
app.MapBrandingEndpoints(v1);

await app.RunAsync();
return 0;

namespace Zelvo.Api
{
    /// <summary>Entry point marker for integration tests.</summary>
    public partial class Program;
}
