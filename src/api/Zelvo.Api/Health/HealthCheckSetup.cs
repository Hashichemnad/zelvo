using System.Text.Json;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using RabbitMQ.Client;

namespace Zelvo.Api.Health;

/// <summary>Liveness and readiness checks for the API and the services it depends on.</summary>
internal static class HealthCheckSetup
{
    internal const string ReadyTag = "ready";

    // Check names are stable identifiers; the web app translates them for display.
    internal const string Postgres = "postgres";
    internal const string Redis = "redis";
    internal const string RabbitMq = "rabbitmq";
    internal const string OpenSearch = "opensearch";
    internal const string Keycloak = "keycloak";
    internal const string KeyVault = "keyVault";

    private static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(3);

    public static IServiceCollection AddZelvoHealthChecks(this IServiceCollection services, IConfiguration configuration)
    {
        var postgres = Required(configuration, "ConnectionStrings:Postgres");
        var redis = Required(configuration, "ConnectionStrings:Redis");
        var rabbitMq = new Uri(Required(configuration, "ConnectionStrings:RabbitMq"));
        _ = new Uri(Required(configuration, "Services:OpenSearch"));
        var keycloak = new Uri(Required(configuration, "Services:KeycloakHealth"));

        services.AddSingleton<IConnection>(_ =>
            Zelvo.Search.RabbitMqConnections.CreateFactory(rabbitMq, "zelvo-api", configuration["RabbitMq:CaFile"])
                .CreateConnectionAsync().GetAwaiter().GetResult());

        services.AddHealthChecks()
            .AddNpgSql(postgres, name: Postgres, tags: [ReadyTag], timeout: CheckTimeout)
            .AddRedis(redis, name: Redis, tags: [ReadyTag], timeout: CheckTimeout)
            .AddRabbitMQ(name: RabbitMq, tags: [ReadyTag], timeout: CheckTimeout)
            .AddCheck<OpenSearchHealthCheck>(OpenSearch, tags: [ReadyTag], timeout: CheckTimeout)
            .AddUrlGroup(keycloak, name: Keycloak, tags: [ReadyTag], timeout: CheckTimeout)
            .AddCheck<Secrets.KeyEncryptionKeyHealthCheck>(KeyVault, tags: [ReadyTag], timeout: CheckTimeout);

        return services;
    }

    public static IEndpointRouteBuilder MapZelvoHealthChecks(this IEndpointRouteBuilder endpoints)
    {
        // Liveness: the process is up. No dependency checks, so it never flaps with them.
        endpoints.MapHealthChecks("/health/live", new HealthCheckOptions
        {
            Predicate = _ => false,
            ResponseWriter = WriteResponse,
        });

        // Readiness: every dependency answers.
        endpoints.MapHealthChecks("/health/ready", new HealthCheckOptions
        {
            Predicate = check => check.Tags.Contains(ReadyTag),
            ResponseWriter = WriteResponse,
        });

        return endpoints;
    }

    /// <summary>
    /// Writes status per check without exception details, so no connection strings or internal
    /// errors are disclosed.
    /// </summary>
    internal static Task WriteResponse(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json; charset=utf-8";
        var body = new HealthResponse(
            report.Status.ToString(),
            report.Entries
                .OrderBy(e => e.Key, StringComparer.Ordinal)
                .Select(e => new HealthEntry(e.Key, e.Value.Status.ToString()))
                .ToList());
        return context.Response.WriteAsync(JsonSerializer.Serialize(body, HealthJson.Options));
    }

    private static string Required(IConfiguration configuration, string key) =>
        configuration[key] is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException($"Configuration value '{key}' is required.");
}

/// <summary>OpenSearch answers with the API's own credentials and the cluster is at least yellow.</summary>
internal sealed class OpenSearchHealthCheck(Zelvo.Search.OpenSearchClient client) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var status = await client.ClusterStatusAsync(cancellationToken);
            return status is "green" or "yellow" ? HealthCheckResult.Healthy() : HealthCheckResult.Unhealthy($"Cluster status {status}.");
        }
        catch (Zelvo.Search.OpenSearchException ex)
        {
            // Logged by the health check service; the readiness response shows only the status.
            return HealthCheckResult.Unhealthy(ex.InnerException?.Message ?? ex.Message);
        }
    }
}

internal sealed record HealthResponse(string Status, IReadOnlyList<HealthEntry> Checks);

internal sealed record HealthEntry(string Name, string Status);

internal static class HealthJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
}
