using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

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
    };

    public string EnvironmentName { get; init; } = "Testing";

    public ApiFactory WithSetting(string key, string value)
    {
        _settings[key] = value;
        return this;
    }

    /// <summary>Replaces the "no sign-in yet" scheme with <see cref="TestAuthHandler"/>.</summary>
    public bool UseTestAuthentication { get; init; }

    /// <summary>An API connected to the test database as the application role, with test sign-in.</summary>
    public static ApiFactory ForDatabase(DatabaseFixture database) =>
        new ApiFactory { UseTestAuthentication = true }
            .WithSetting("ConnectionStrings:Postgres", database.AppConnectionString);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(EnvironmentName);
        foreach (var (key, value) in _settings)
        {
            builder.UseSetting(key, value);
        }

        if (UseTestAuthentication)
        {
            builder.ConfigureTestServices(services => services
                .AddAuthentication(TestAuthHandler.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, null));
        }
    }
}
