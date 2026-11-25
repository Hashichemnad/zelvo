using Zelvo.Workflows;
using Zelvo.Ai;
using Zelvo.Channels;
using Zelvo.Data;
using Zelvo.Integrations;
using Zelvo.Licensing;
using Zelvo.Notifications;
using Zelvo.Observability;
using Zelvo.Search;
using Zelvo.Secrets;
using Zelvo.Tickets;
using Zelvo.Worker;

// Background jobs host. Search indexing (A4): relays the search outbox through RabbitMQ and
// writes tenants' OpenSearch indices. Tickets (B1): closes resolved tickets after the tenant's
// auto-close period and scans attachments for malware with ClamAV. Webhooks (E1): sends due
// deliveries with retries and removes integration records past their retention. SLA timers,
// other integrations and AI features arrive with their modules. AI (E4): clears the text of AI log
// entries past their retention. Notifications (B5): renders notifications in each recipient's
// language, sends their emails with retries and removes old ones. Email and chat (E2): reads
// mailboxes, sends replies from the ticket's mailbox, ends chats their visitors left.
var builder = Host.CreateApplicationBuilder(args);

// Traces, metrics and logs over OTLP when OTEL_EXPORTER_OTLP_ENDPOINT is set (A5).
builder.AddZelvoTelemetry("zelvo-worker");

builder.Services.AddZelvoLicensing();
builder.Services.AddHostedService<HeartbeatService>();

// The application role: row-level security applies to every job, which sets its tenant.
builder.Services.AddZelvoData(Required(builder.Configuration, "ConnectionStrings:Postgres"));
// Task C3: workflow rules also run on the worker's changes (SLA, auto-close, email intake).
builder.Services.AddZelvoWorkflowRules();
// Stored secrets (A5): decrypted per tenant when a job needs them (AI keys, gateway credentials).
builder.Services.AddZelvoSecrets(builder.Configuration);
builder.Services.AddZelvoIntegrationJobs(builder.Configuration);
// AI (E4): clears AI log text past its retention; AI jobs of later tasks call the gateway.
builder.Services.AddZelvoAiJobs(builder.Configuration);
builder.Services.AddZelvoSearchPipeline(builder.Configuration, new Uri(Required(builder.Configuration, "ConnectionStrings:RabbitMq")));
builder.Services.AddZelvoTicketJobs(builder.Configuration);
builder.Services.AddZelvoNotificationJobs(builder.Configuration);
// Email and chat (E2): reads mailboxes into tickets, sends replies from them, ends idle chats.
builder.Services.AddZelvoChannelJobs(builder.Configuration);

builder.Build().Run();

static string Required(IConfiguration configuration, string key) =>
    configuration[key] is { Length: > 0 } value
        ? value
        : throw new InvalidOperationException($"Configuration value '{key}' is required.");
