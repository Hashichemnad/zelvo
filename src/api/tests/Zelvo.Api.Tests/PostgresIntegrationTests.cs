using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Zelvo.Api.Tests;

/// <summary>Runs against a real PostgreSQL (with pgvector) in Docker.</summary>
public sealed class PostgresIntegrationTests(DatabaseFixture database)
{
    [Fact]
    public async Task Postgres_health_check_passes_against_a_real_database()
    {
        await using var factory = new ApiFactory().WithSetting("ConnectionStrings:Postgres", database.AppConnectionString);
        var healthChecks = factory.Services.GetRequiredService<HealthCheckService>();

        var report = await healthChecks.CheckHealthAsync(
            registration => registration.Name == "postgres",
            TestContext.Current.CancellationToken);

        Assert.Equal(HealthStatus.Healthy, report.Entries["postgres"].Status);
    }
}
