using System.Net.Http.Json;

namespace Zelvo.Api.Tests;

public sealed class SystemInfoTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;

    public SystemInfoTests(ApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Info_reports_product_languages_and_development_license()
    {
        using var client = _factory.CreateClient();

        var info = await client.GetFromJsonAsync<Info>(new Uri("/api/v1/system/info", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal("Zelvo", info!.Product);
        Assert.Equal(["en", "ar"], info.Languages);
        Assert.Equal("Development", info.LicenseMode);
        Assert.DoesNotContain("+", info.Version, StringComparison.Ordinal);
    }

    private sealed record Info(string Product, string Version, List<string> Languages, string LicenseMode);
}
