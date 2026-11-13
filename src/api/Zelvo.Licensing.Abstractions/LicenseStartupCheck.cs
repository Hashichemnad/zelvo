using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Zelvo.Licensing;

/// <summary>
/// Runs license verification when a service starts; an unlicensed service refuses to start.
/// </summary>
public sealed partial class LicenseStartupCheck(
    ILicenseService licenseService,
    ILogger<LicenseStartupCheck> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var status = await licenseService.VerifyAsync(cancellationToken).ConfigureAwait(false);
        if (!status.IsValid)
        {
            LogRefused(logger, status.Reason ?? "unknown");
            throw new LicenseException(status.Reason ?? "License verification failed.");
        }

        LogVerified(logger, status.Kind, status.LicensedTo);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(Level = LogLevel.Critical, Message = "License verification failed; service refuses to start: {Reason}")]
    private static partial void LogRefused(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "License verified: {Kind} license for {LicensedTo}")]
    private static partial void LogVerified(ILogger logger, LicenseKind kind, string licensedTo);
}

/// <summary>Thrown when a service may not run under the installed license.</summary>
public sealed class LicenseException : Exception
{
    public LicenseException() { }
    public LicenseException(string message) : base(message) { }
    public LicenseException(string message, Exception innerException) : base(message, innerException) { }
}
