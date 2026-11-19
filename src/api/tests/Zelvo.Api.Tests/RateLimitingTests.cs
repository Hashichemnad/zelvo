using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;
using Zelvo.Api.Security.RateLimiting;

namespace Zelvo.Api.Tests;

/// <summary>API rate limits (A5): per signed-in user, per client address for visitors.</summary>
public sealed class RateLimitingTests(DatabaseFixture database)
{
    private static readonly Uri Info = new("/api/v1/system/info", UriKind.Relative);

    [Fact]
    public async Task Visitors_get_429_with_retry_after_once_their_address_used_its_requests()
    {
        await using var factory = Limited(visitorPermits: 3);
        using var client = factory.CreateClient();

        for (var i = 0; i < 3; i++)
        {
            using var allowed = await client.GetAsync(Info, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
            Assert.Equal("3", allowed.Headers.GetValues("RateLimit-Limit").Single());
            Assert.Equal((2 - i).ToString(System.Globalization.CultureInfo.InvariantCulture), allowed.Headers.GetValues("RateLimit-Remaining").Single());
        }

        using var refused = await client.GetAsync(Info, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
        Assert.True(refused.Headers.RetryAfter?.Delta > TimeSpan.Zero);
        var problem = await refused.Content.ReadFromJsonAsync<Problem>(TestContext.Current.CancellationToken);
        Assert.Equal("rate-limited", problem!.Code);

        // Probes and the API description are never limited.
        using var health = await client.GetAsync(new Uri("/health/live", UriKind.Relative), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
    }

    [Fact]
    public async Task Behind_a_trusted_proxy_each_forwarded_client_address_has_its_own_limit()
    {
        await using var factory = Limited(visitorPermits: 1).WithSetting("Network:TrustedProxies:0", "10.0.0.0/8");
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await GetFrom(client, "203.0.113.7")).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await GetFrom(client, "203.0.113.7")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await GetFrom(client, "203.0.113.8")).StatusCode);
    }

    [Fact]
    public async Task Forwarded_addresses_from_untrusted_callers_are_ignored()
    {
        await using var factory = Limited(visitorPermits: 1);
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await GetFrom(client, "203.0.113.7")).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await GetFrom(client, "203.0.113.9")).StatusCode);
    }

    [Fact]
    public async Task Signed_in_users_are_counted_per_user_not_per_address()
    {
        var tenant = await database.CreateTenantAsync("rl");
        await using var factory = Limited(visitorPermits: 1, signedInPermits: 2).WithSetting("ConnectionStrings:Postgres", database.AppConnectionString);
        using var first = await factory.ForTenant(tenant).SignedInAsync(database, tenant, "requester");
        using var second = await factory.ForTenant(tenant).SignedInAsync(database, tenant, "requester");
        var me = new Uri("/api/v1/me", UriKind.Relative);

        Assert.Equal(HttpStatusCode.OK, (await first.GetAsync(me, TestContext.Current.CancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await first.GetAsync(me, TestContext.Current.CancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await first.GetAsync(me, TestContext.Current.CancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await second.GetAsync(me, TestContext.Current.CancellationToken)).StatusCode);
    }

    [Fact]
    public async Task Counters_are_kept_in_valkey_with_the_window_as_expiry_and_hold_no_user_data()
    {
        await using var valkey = new ContainerBuilder(Environment.GetEnvironmentVariable("ZELVO_TEST_VALKEY_IMAGE") ?? "valkey/valkey:9.1-alpine")
            .WithPortBinding(6379, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilCommandIsCompleted("valkey-cli", "ping"))
            .Build();
        await valkey.StartAsync(TestContext.Current.CancellationToken);
        var multiplexer = await ConnectionMultiplexer.ConnectAsync($"{valkey.Hostname}:{valkey.GetMappedPublicPort(6379)}");
        var store = new ValkeyRateLimitStore(new Lazy<Task<IConnectionMultiplexer>>(Task.FromResult<IConnectionMultiplexer>(multiplexer)), NullLogger<ValkeyRateLimitStore>.Instance);

        var first = await store.HitAsync("zelvo:rl:test:a", TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        var second = await store.HitAsync("zelvo:rl:test:a", TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        var other = await store.HitAsync("zelvo:rl:test:b", TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        Assert.Equal(1, first!.Value.Count);
        Assert.Equal(2, second!.Value.Count);
        Assert.Equal(1, other!.Value.Count);
        Assert.InRange(second.Value.ResetsIn, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30));
        Assert.InRange((await multiplexer.GetDatabase().KeyTimeToLiveAsync("zelvo:rl:test:a"))!.Value, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30));
        await multiplexer.DisposeAsync();
    }

    [Fact]
    public async Task Requests_are_allowed_when_valkey_is_unavailable()
    {
        // The default test settings point the cache at a closed port.
        await using var factory = new ApiFactory().WithSetting("RateLimiting:VisitorPermits", "1");
        using var client = factory.CreateClient();

        for (var i = 0; i < 3; i++)
        {
            using var response = await client.GetAsync(Info, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.False(response.Headers.Contains("RateLimit-Limit"));
        }
    }

    private static ApiFactory Limited(int visitorPermits, int signedInPermits = 600) =>
        new ApiFactory()
            .WithSetting("RateLimiting:VisitorPermits", visitorPermits.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .WithSetting("RateLimiting:SignedInPermits", signedInPermits.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .WithServices(services =>
            {
                services.RemoveAll<IRateLimitStore>();
                services.AddSingleton<IRateLimitStore, MemoryRateLimitStore>();

                // The in-memory test server has no client address; requests come from 10.0.0.5.
                services.AddSingleton<IStartupFilter, TestClientAddress>();
            });

    private static async Task<HttpResponseMessage> GetFrom(HttpClient client, string forwardedFor)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Info);
        request.Headers.Add("X-Forwarded-For", forwardedFor);
        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private sealed record Problem(string Code);

    private sealed class MemoryRateLimitStore : IRateLimitStore
    {
        private readonly ConcurrentDictionary<string, long> _counts = new(StringComparer.Ordinal);

        public Task<RateLimitCount?> HitAsync(string key, TimeSpan window, CancellationToken cancellationToken) =>
            Task.FromResult<RateLimitCount?>(new RateLimitCount(_counts.AddOrUpdate(key, 1, (_, c) => c + 1), window));
    }

    private sealed class TestClientAddress : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((HttpContext context, RequestDelegate nextMiddleware) =>
            {
                context.Connection.RemoteIpAddress = IPAddress.Parse("10.0.0.5");
                return nextMiddleware(context);
            });
            next(app);
        };
    }
}
