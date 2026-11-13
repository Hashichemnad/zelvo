using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Testcontainers.PostgreSql;

namespace Zelvo.Api.Tests;

/// <summary>Runs against a real PostgreSQL (with pgvector) in Docker.</summary>
public sealed class PostgresIntegrationTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("pgvector/pgvector:pg17").Build();

    public async ValueTask InitializeAsync() => await _postgres.StartAsync();

    public async ValueTask DisposeAsync() => await _postgres.DisposeAsync();

    [Fact]
    public async Task Postgres_health_check_passes_against_a_real_database()
    {
        await using var factory = new ApiFactory().WithSetting("ConnectionStrings:Postgres", _postgres.GetConnectionString());
        var healthChecks = factory.Services.GetRequiredService<HealthCheckService>();

        var report = await healthChecks.CheckHealthAsync(
            registration => registration.Name == "postgres",
            TestContext.Current.CancellationToken);

        Assert.Equal(HealthStatus.Healthy, report.Entries["postgres"].Status);
    }
}
