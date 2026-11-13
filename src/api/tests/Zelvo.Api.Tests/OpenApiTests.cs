using System.Net;
using System.Text.Json;

namespace Zelvo.Api.Tests;

public sealed class OpenApiTests : IAsyncDisposable
{
    private readonly ApiFactory _factory = new ApiFactory().WithSetting("OpenApi:Enabled", "true");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    [Fact]
    public async Task The_document_describes_the_platform_endpoints()
    {
        using var client = _factory.CreateClient();

        using var document = JsonDocument.Parse(await client.GetStringAsync(new Uri("/openapi/v1.json", UriKind.Relative), Ct));
        var paths = document.RootElement.GetProperty("paths").EnumerateObject().Select(p => p.Name).ToList();

        Assert.Equal("Zelvo API", document.RootElement.GetProperty("info").GetProperty("title").GetString());
        Assert.Contains("/api/v1/tenant", paths);
        Assert.Contains("/api/v1/audit/events", paths);
        Assert.Contains("/api/v1/audit/verification", paths);
        Assert.Contains("/api/v1/translations/missing", paths);
    }

    [Fact]
    public async Task The_explorer_is_served_with_a_same_origin_content_policy()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync(new Uri("/api-explorer/", UriKind.Relative), Ct);
        var html = await response.Content.ReadAsStringAsync(Ct);
        var policy = response.Headers.GetValues("Content-Security-Policy").Single();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("<html", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("default-src 'self'", policy, StringComparison.Ordinal);
        Assert.Contains("frame-ancestors 'none'", policy, StringComparison.Ordinal);
        Assert.DoesNotContain("cdn.jsdelivr.net", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Both_are_off_outside_development_unless_enabled()
    {
        await using var factory = new ApiFactory();
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(new Uri("/openapi/v1.json", UriKind.Relative), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(new Uri("/api-explorer/", UriKind.Relative), Ct)).StatusCode);
    }
}
