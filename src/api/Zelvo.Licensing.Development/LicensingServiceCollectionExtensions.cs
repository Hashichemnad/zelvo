using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Zelvo.Licensing;

/// <summary>
/// Registers licensing. The private release implementation exposes the same method in the same
/// namespace, so services build against either one without code changes.
/// </summary>
public static class LicensingServiceCollectionExtensions
{
    public static IServiceCollection AddZelvoLicensing(this IServiceCollection services)
    {
        services.TryAddSingleton<ILicenseService, DevelopmentLicenseService>();
        services.AddHostedService<LicenseStartupCheck>();
        return services;
    }
}
