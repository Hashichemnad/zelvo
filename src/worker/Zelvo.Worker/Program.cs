using Zelvo.Data;
using Zelvo.Licensing;
using Zelvo.Search;
using Zelvo.Worker;

// Background jobs host. Search indexing (A4): relays the search outbox through RabbitMQ and
// writes tenants' OpenSearch indices. SLA timers, notifications, integrations and AI jobs arrive
// with their modules.
var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddZelvoLicensing();
builder.Services.AddHostedService<HeartbeatService>();

// The application role: row-level security applies to every job, which sets its tenant.
builder.Services.AddZelvoData(Required(builder.Configuration, "ConnectionStrings:Postgres"));
builder.Services.AddZelvoSearchPipeline(builder.Configuration, new Uri(Required(builder.Configuration, "ConnectionStrings:RabbitMq")));

builder.Build().Run();

static string Required(IConfiguration configuration, string key) =>
    configuration[key] is { Length: > 0 } value
        ? value
        : throw new InvalidOperationException($"Configuration value '{key}' is required.");
