using System.Text.Json.Serialization;
using Zelvo.Api;
using Zelvo.Api.Auditing;
using Zelvo.Api.Commands;
using Zelvo.Api.Conventions;
using Zelvo.Api.Health;
using Zelvo.Api.Localization;
using Zelvo.Api.OpenApi;
using Zelvo.Api.Platform;
using Zelvo.Api.Security;
using Zelvo.Api.Tenancy;
using Zelvo.Data;
using Zelvo.Data.Localization;
using Zelvo.Data.Tenancy;
using Zelvo.Licensing;

if (args is ["migrate", ..])
{
    return await MigrateCommand.RunAsync(args[1..]);
}

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.ConfigureKestrel(options => options.AddServerHeader = false);

builder.Services.AddProblemDetails(options => options.CustomizeProblemDetails = ApiProblem.Customize);
builder.Services.AddValidation();
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddZelvoOpenApi();
builder.Services.AddZelvoLicensing();
builder.Services.AddZelvoHealthChecks(builder.Configuration);
builder.Services.AddZelvoAuthorization();

builder.Services.AddZelvoData(builder.Configuration.GetConnectionString("Postgres")
    ?? throw new InvalidOperationException("Configuration value 'ConnectionStrings:Postgres' is required."));
builder.Services.AddMemoryCache();
builder.Services.AddScoped<TenantResolver>();
builder.Services.AddSingleton<IMissingTranslationSource, TenantMissingTranslations>();

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseMiddleware<SecurityHeadersMiddleware>();
app.UseAuthentication();
app.UseMiddleware<TenantResolutionMiddleware>();
app.UseMiddleware<AuditContextMiddleware>();
app.UseAuthorization();

app.MapZelvoOpenApi();
app.MapZelvoHealthChecks();

var v1 = app.MapGroup("/api/v1");
v1.MapSystemEndpoints();
v1.MapTenantEndpoints();
v1.MapAuditEndpoints();
v1.MapTranslationEndpoints();

await app.RunAsync();
return 0;

namespace Zelvo.Api
{
    /// <summary>Entry point marker for integration tests.</summary>
    public partial class Program;
}
