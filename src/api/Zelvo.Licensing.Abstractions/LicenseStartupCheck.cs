using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Zelvo.Licensing;

/// <summary>What a service does at startup while the installation waits for activation.</summary>
public enum ActivationWait
{
    /// <summary>Start, and offer only the activation screens (the API).</summary>
    StartLimited,

    /// <summary>Wait until a valid license is imported before the service's jobs start (the worker).</summary>
    WaitForLicense,
}

/// <summary>How <see cref="LicenseStartupCheck"/> behaves.</summary>
/// <param name="Wait">While activation is needed.</param>
/// <param name="PollInterval">How often a waiting service checks again.</param>
public sealed record LicenseStartupOptions(ActivationWait Wait, TimeSpan PollInterval)
{
    /// <summary>The API's behaviour.</summary>
    public static LicenseStartupOptions Default { get; } = new(ActivationWait.StartLimited, TimeSpan.FromSeconds(30));
}

/// <summary>
/// Runs license verification when a service starts (module 12). A tampered license (changed file,
/// signature that is not ours, another customer's license for this build) stops the service and is
/// logged as critical. Without a license for these servers the API starts with its activation
/// screens only, and the worker waits until a license is imported.
/// </summary>
public sealed partial class LicenseStartupCheck(
    ILicenseService licenseService,
    ILogger<LicenseStartupCheck> logger,
    LicenseStartupOptions? options = null) : IHostedService
{
    private readonly LicenseStartupOptions _options = options ?? LicenseStartupOptions.Default;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var status = await licenseService.VerifyAsync(cancellationToken).ConfigureAwait(false);
        while (true)
        {
            if (status.IsValid)
            {
                LogVerified(logger, status.Kind, status.LicensedTo);
                return;
            }

            if (!status.ActivationRequired)
            {
                LogRefused(logger, status.ProblemCode ?? "unknown", status.Reason ?? "unknown");
                throw new LicenseException(status.Reason ?? "License verification failed.");
            }

            if (_options.Wait == ActivationWait.StartLimited)
            {
                LogActivationNeeded(logger, status.ProblemCode ?? "unknown", status.Reason ?? "unknown");
                return;
            }

            LogWaiting(logger, status.ProblemCode ?? "unknown");
            await Task.Delay(_options.PollInterval, cancellationToken).ConfigureAwait(false);
            status = await licenseService.VerifyAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(Level = LogLevel.Critical, Message = "License verification failed ({Problem}); service refuses to start: {Reason}")]
    private static partial void LogRefused(ILogger logger, string problem, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Zelvo is not activated on these servers ({Problem}): only the activation screens are available. {Reason}")]
    private static partial void LogActivationNeeded(ILogger logger, string problem, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Waiting for Zelvo to be activated ({Problem}); background jobs start once a license is imported.")]
    private static partial void LogWaiting(ILogger logger, string problem);

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
