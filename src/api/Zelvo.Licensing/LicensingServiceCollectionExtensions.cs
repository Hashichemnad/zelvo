using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Zelvo.Licensing;

/// <summary>Registers licensing (module 12) for a service.</summary>
public static class LicensingServiceCollectionExtensions
{
    /// <summary>
    /// The license service, its startup check (a tampered license stops the service) and a refresh
    /// every minute, so a license imported in one service applies in the others.
    /// </summary>
    /// <param name="services">The services.</param>
    /// <param name="wait">What the service does while the installation waits for activation.</param>
    public static IServiceCollection AddZelvoLicensing(this IServiceCollection services, ActivationWait wait = ActivationWait.StartLimited)
    {
        services.TryAddSingleton<IInstallationStore, PostgresInstallationStore>();
        services.TryAddSingleton<ServerFingerprintSource>();
        services.TryAddSingleton(sp => VendorKeys.Load(sp.GetService<IConfiguration>()));
        services.TryAddSingleton<LicenseService>();
        services.TryAddSingleton<ILicenseService>(sp => sp.GetRequiredService<LicenseService>());
        services.TryAddSingleton<IInstallationWatermark>(sp => sp.GetRequiredService<LicenseService>());
        services.AddSingleton(new LicenseStartupOptions(wait, TimeSpan.FromSeconds(30)));
        services.AddHostedService<LicenseStartupCheck>();
        services.AddHostedService<LicenseRefreshService>();
        return services;
    }
}

/// <summary>Verifies the license again every minute (imports in another service, server changes).</summary>
internal sealed partial class LicenseRefreshService(ILicenseService license, ILogger<LicenseRefreshService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                await license.VerifyAsync(stoppingToken).ConfigureAwait(false);
            }
#pragma warning disable CA1031 // A failed refresh keeps the last status; the next one tries again.
            catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
            {
                LogRefreshFailed(logger, ex);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "License refresh failed")]
    private static partial void LogRefreshFailed(ILogger logger, Exception exception);
}
