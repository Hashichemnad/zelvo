using Zelvo.Data;
using Zelvo.Licensing;
using Zelvo.Observability;
using Zelvo.Search;
using Zelvo.Secrets;
using Zelvo.Tickets;
using Zelvo.Worker;

// Background jobs host. Search indexing (A4): relays the search outbox through RabbitMQ and
// writes tenants' OpenSearch indices. Tickets (B1): closes resolved tickets after the tenant's
// auto-close period and scans attachments for malware with ClamAV. SLA timers, notifications,
// integrations and AI jobs arrive with their modules.
var builder = Host.CreateApplicationBuilder(args);

// Traces, metrics and logs over OTLP when OTEL_EXPORTER_OTLP_ENDPOINT is set (A5).
builder.AddZelvoTelemetry("zelvo-worker");

builder.Services.AddZelvoLicensing();
builder.Services.AddHostedService<HeartbeatService>();

// The application role: row-level security applies to every job, which sets its tenant.
builder.Services.AddZelvoData(Required(builder.Configuration, "ConnectionStrings:Postgres"));
// Stored secrets (A5): decrypted per tenant when a job needs them (AI keys, gateway credentials).
builder.Services.AddZelvoSecrets(builder.Configuration);
builder.Services.AddZelvoSearchPipeline(builder.Configuration, new Uri(Required(builder.Configuration, "ConnectionStrings:RabbitMq")));
builder.Services.AddZelvoTicketJobs(builder.Configuration);

builder.Build().Run();

static string Required(IConfiguration configuration, string key) =>
    configuration[key] is { Length: > 0 } value
        ? value
        : throw new InvalidOperationException($"Configuration value '{key}' is required.");
