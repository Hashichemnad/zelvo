using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;
using Zelvo.Licensing;

namespace Zelvo.Api.Tests;

public sealed class LicensingTests
{
    [Theory]
    [InlineData("Development")]
    [InlineData("Testing")]
    public async Task Development_stub_allows_everything_outside_production(string environmentName)
    {
        var service = new DevelopmentLicenseService(Environment(environmentName));

        var status = await service.VerifyAsync(TestContext.Current.CancellationToken);

        Assert.True(status.IsValid);
        Assert.Equal(LicenseKind.Development, status.Kind);
        Assert.All(LicensedModules.Phase1, module => Assert.True(service.IsModuleLicensed(module)));
        Assert.True(service.IsVersionLicensed(new Version(99, 0)));
    }

    [Fact]
    public async Task Development_stub_refuses_production()
    {
        var service = new DevelopmentLicenseService(Environment(Environments.Production));

        var status = await service.VerifyAsync(TestContext.Current.CancellationToken);

        Assert.False(status.IsValid);
        Assert.False(service.IsModuleLicensed(LicensedModules.Incidents));
    }

    [Fact]
    public void Api_refuses_to_start_in_production_with_the_stub()
    {
        using var factory = new ApiFactory { EnvironmentName = Environments.Production };

        var error = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.IsType<LicenseException>(error.GetBaseException());
    }

    [Fact]
    public void Phase1_lists_fifteen_unique_modules()
    {
        Assert.Equal(15, LicensedModules.Phase1.Distinct(StringComparer.Ordinal).Count());
    }

    private static HostingEnvironment Environment(string name) => new() { EnvironmentName = name };
}
