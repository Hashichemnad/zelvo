using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Zelvo.Api.Identity;

namespace Zelvo.Api.Tests;

/// <summary>
/// Hosts the API in memory. Dependency addresses point at closed local ports unless a test
/// overrides them, so readiness reports those dependencies as unhealthy.
/// </summary>
public class ApiFactory : WebApplicationFactory<Program>
{
    private readonly Dictionary<string, string?> _settings = new(StringComparer.Ordinal)
    {
        ["ConnectionStrings:Postgres"] = "Host=127.0.0.1;Port=1;Username=none;Password=none;Database=none;Timeout=1",
        ["ConnectionStrings:Redis"] = "127.0.0.1:1,abortConnect=false,connectTimeout=500",
        ["ConnectionStrings:RabbitMq"] = "amqp://guest:guest@127.0.0.1:1/",
        ["Services:OpenSearch"] = "http://127.0.0.1:1/",
        ["Services:KeycloakHealth"] = "http://127.0.0.1:1/health/ready",
        ["Identity:PublicUrl"] = TestTokens.PublicUrl,
    };

    public string EnvironmentName { get; init; } = "Testing";

    public ApiFactory WithSetting(string key, string value)
    {
        _settings[key] = value;
        return this;
    }

    /// <summary>What the API asked Keycloak to change.</summary>
    internal FakeKeycloakAdmin Keycloak { get; } = new();

    /// <summary>An API connected to the test database as the application role, trusting <see cref="TestTokens"/>.</summary>
    public static ApiFactory ForDatabase(DatabaseFixture database) =>
        new ApiFactory().WithSetting("ConnectionStrings:Postgres", database.AppConnectionString);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(EnvironmentName);
        foreach (var (key, value) in _settings)
        {
            builder.UseSetting(key, value);
        }

        // Real token checks, with the test signing key instead of Keycloak's.
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IRealmSigningKeys>();
            services.AddSingleton<IRealmSigningKeys, TestRealmSigningKeys>();
            services.RemoveAll<IKeycloakAdmin>();
            services.AddSingleton<IKeycloakAdmin>(Keycloak);
        });
    }
}
