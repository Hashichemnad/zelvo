using Zelvo.Api;
using Zelvo.Api.Health;
using Zelvo.Api.Security;
using Zelvo.Api.Platform;
using Zelvo.Licensing;

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.ConfigureKestrel(options => options.AddServerHeader = false);

builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();
builder.Services.AddZelvoLicensing();
builder.Services.AddZelvoHealthChecks(builder.Configuration);

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseMiddleware<SecurityHeadersMiddleware>();

if (app.Environment.IsDevelopment())
{
    // OpenAPI document; a browsable API explorer arrives with module 14.
    app.MapOpenApi();
}

app.MapZelvoHealthChecks();
app.MapGroup("/api/v1").MapSystemEndpoints();

app.Run();

namespace Zelvo.Api
{
    /// <summary>Entry point marker for integration tests.</summary>
    public partial class Program;
}
