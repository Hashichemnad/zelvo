using System.Diagnostics;
using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Zelvo.Observability;

/// <summary>
/// OpenTelemetry for Zelvo's .NET services. Traces, metrics and logs are exported over OTLP to the
/// endpoint in the standard <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> setting (the OpenTelemetry
/// Collector in Compose); without it nothing is collected or sent. Telemetry never carries
/// request bodies, query strings, tokens or SQL parameter values. See docs/architecture/observability.md.
/// </summary>
public static class ZelvoTelemetry
{
    /// <summary>Activity source for Zelvo's own spans.</summary>
    public const string SourceName = "Zelvo";

    /// <summary>Meter of the API's own metrics.</summary>
    public const string ApiMeter = "Zelvo.Api";

    /// <summary>Meter of the worker's own metrics.</summary>
    public const string WorkerMeter = "Zelvo.Worker";

    /// <summary>Zelvo's own spans.</summary>
    public static readonly ActivitySource Source = new(SourceName);

    /// <summary>Tag with the tenant slug on request and job spans.</summary>
    public const string TenantTag = "zelvo.tenant";

    /// <summary>Whether an OTLP endpoint is configured.</summary>
    public static bool IsEnabled(IConfiguration configuration) =>
        !string.IsNullOrWhiteSpace(configuration?["OTEL_EXPORTER_OTLP_ENDPOINT"]);

    /// <summary>
    /// Adds OpenTelemetry for <paramref name="serviceName"/> (for example <c>zelvo-api</c>) when an
    /// OTLP endpoint is configured. <paramref name="tracing"/> and <paramref name="metrics"/> add a
    /// host's own instrumentation (ASP.NET Core in the API).
    /// </summary>
    public static IHostApplicationBuilder AddZelvoTelemetry(this IHostApplicationBuilder builder, string serviceName,
        Action<TracerProviderBuilder>? tracing = null, Action<MeterProviderBuilder>? metrics = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        if (!IsEnabled(builder.Configuration))
        {
            return builder;
        }

        var version = typeof(ZelvoTelemetry).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
        var ratio = double.TryParse(builder.Configuration["Telemetry:TraceSampleRatio"], System.Globalization.CultureInfo.InvariantCulture, out var r) && r is >= 0 and <= 1 ? r : 1.0;

        builder.Logging.AddOpenTelemetry(logging =>
        {
            logging.IncludeFormattedMessage = true;
            logging.IncludeScopes = true;
        });

        builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource => resource
                .AddService(serviceName, serviceNamespace: "zelvo", serviceVersion: version.Split('+')[0], serviceInstanceId: Environment.MachineName)
                .AddAttributes([new("deployment.environment.name", builder.Environment.EnvironmentName)]))
            .WithTracing(trace =>
            {
                trace.SetSampler(new BackgroundNoiseSampler(new ParentBasedSampler(new TraceIdRatioBasedSampler(ratio))))
                    .AddSource(SourceName, "Zelvo.*", "Npgsql", "RabbitMQ.Client.Publisher", "RabbitMQ.Client.Subscriber")
                    .AddHttpClientInstrumentation(http =>
                    {
                        // Outgoing calls (Keycloak, OpenSearch, Vault): the address without its query.
                        http.EnrichWithHttpRequestMessage = (activity, request) =>
                        {
                            if (request.RequestUri is { } uri)
                            {
                                activity.SetTag("url.full", uri.GetLeftPart(UriPartial.Path));
                                activity.SetTag("url.query", null);
                            }
                        };
                    });
                tracing?.Invoke(trace);
            })
            .WithMetrics(meter =>
            {
                meter.AddMeter("Zelvo.*", "Npgsql", "System.Runtime", "System.Net.Http");
                metrics?.Invoke(meter);
            })
            .UseOtlpExporter();
        return builder;
    }
}
