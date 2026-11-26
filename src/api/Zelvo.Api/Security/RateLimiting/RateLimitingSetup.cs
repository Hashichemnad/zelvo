using System.Globalization;
using System.Net;
using Microsoft.AspNetCore.HttpOverrides;
using StackExchange.Redis;

namespace Zelvo.Api.Security.RateLimiting;

/// <summary>Registers rate limiting and the trusted proxies whose forwarded client address is used.</summary>
internal static class RateLimitingSetup
{
    public static IServiceCollection AddZelvoRateLimiting(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<RateLimitOptions>().Configure(o =>
        {
            var section = configuration.GetSection(RateLimitOptions.Section);
            o.Enabled = bool.TryParse(section["Enabled"], out var enabled) ? enabled : o.Enabled;
            o.Window = TimeSpan.TryParse(section["Window"], CultureInfo.InvariantCulture, out var window) && window > TimeSpan.Zero ? window : o.Window;
            o.SignedInPermits = int.TryParse(section["SignedInPermits"], CultureInfo.InvariantCulture, out var signedIn) && signedIn > 0 ? signedIn : o.SignedInPermits;
            o.VisitorPermits = int.TryParse(section["VisitorPermits"], CultureInfo.InvariantCulture, out var visitor) && visitor > 0 ? visitor : o.VisitorPermits;
        });

        services.AddSingleton(_ => new Lazy<Task<IConnectionMultiplexer>>(async () =>
            await ConnectionMultiplexer.ConnectAsync(ValkeyOptions(configuration)).ConfigureAwait(false)));
        services.AddSingleton<IRateLimitStore, ValkeyRateLimitStore>();

        // Client addresses: X-Forwarded-For and -Proto are used only when the request comes from a
        // trusted proxy (the web app's server, a load balancer), listed in Network:TrustedProxies
        // as addresses or networks (for example 10.0.4.0/24).
        services.Configure<ForwardedHeadersOptions>(o =>
        {
            o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            o.ForwardLimit = 1;
            foreach (var entry in configuration.GetSection("Network:TrustedProxies").GetChildren().Select(c => c.Value?.Trim()))
            {
                if (string.IsNullOrEmpty(entry))
                {
                    continue;
                }

                if (System.Net.IPNetwork.TryParse(entry, out var network))
                {
                    o.KnownIPNetworks.Add(network);
                }
                else if (IPAddress.TryParse(entry, out var address))
                {
                    o.KnownProxies.Add(address);
                }
                else
                {
                    throw new InvalidOperationException($"Network:TrustedProxies has an entry that is not an address or network: '{entry}'.");
                }
            }
        });
        return services;
    }

    /// <summary>
    /// The Valkey connection's options from <c>ConnectionStrings:Redis</c>; with TLS
    /// (<c>ssl=true</c>, task F6) the server's certificate is checked against
    /// <c>Redis:CaFile</c> when one is given (the installation's own CA), else the system's CAs.
    /// </summary>
    internal static ConfigurationOptions ValkeyOptions(IConfiguration configuration)
    {
        var options = ConfigurationOptions.Parse(configuration.GetConnectionString("Redis") ?? string.Empty, ignoreUnknown: true);
        if (options.Ssl && configuration["Redis:CaFile"] is { Length: > 0 } caFile)
        {
            options.TrustIssuer(caFile);
        }

        return options;
    }
}
