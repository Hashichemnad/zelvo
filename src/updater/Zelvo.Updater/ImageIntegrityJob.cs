using Zelvo.Licensing;

namespace Zelvo.Updater;

/// <summary>
/// The image side of tamper detection (module 12, task F6): the updater, which alone can see the
/// Docker host, compares the image of every running Zelvo container with the image id in the signed
/// release manifest, at start and every 10 minutes, and reports the result as the component
/// <c>images</c> (<c>ImagesVerified</c> or <c>ImageMismatch</c>). The services check their own files
/// when they start; this catches a container started from another image. It only reports: the API
/// logs a mismatch as critical and Settings → License shows it.
/// </summary>
internal sealed partial class ImageIntegrityJob(
    IContainerHost host,
    ReleaseIntegrityOptions options,
    ImageSigningKeys keys,
    IReleaseComponentStore store,
    TimeProvider time,
    ILogger<ImageIntegrityJob> logger) : BackgroundService
{
    internal const string Component = "images";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(10), time);
        do
        {
            try
            {
                await CheckAsync(stoppingToken).ConfigureAwait(false);
            }
#pragma warning disable CA1031 // A failed round (Docker busy, database restarting) is tried again later.
            catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
            {
                LogCheckFailed(logger, ex);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    internal async Task<ReleaseComponentReport?> CheckAsync(CancellationToken cancellationToken)
    {
        var manifest = ReleaseIntegrity.ReadManifest(options.ManifestPath, keys, out var status, out var reason);
        if (manifest is null)
        {
            if (status == ReleaseCheckStatus.NoManifest && !VendorKeys.IsReleaseBuild)
            {
                return null; // a development installation
            }

            var invalid = Report(status.ToString(), null, reason);
            await store.RecordAsync(invalid, cancellationToken).ConfigureAwait(false);
            return invalid;
        }

        var expected = manifest.Components.Where(c => c.ImageId is not null).ToDictionary(c => c.Name, c => c.ImageId!, StringComparer.Ordinal);
        var mismatches = (await host.RunningContainersAsync(cancellationToken).ConfigureAwait(false))
            .Where(c => expected.TryGetValue(c.Service, out var id) && !string.Equals(id, c.ImageId, StringComparison.OrdinalIgnoreCase))
            .Select(c => c.Service)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();
        var services = string.Join(", ", mismatches);
        var report = mismatches.Count == 0
            ? Report("ImagesVerified", manifest.Version, $"Every running Zelvo container runs an image of release {manifest.Version}.")
            : Report("ImageMismatch", manifest.Version, $"Not the images of release {manifest.Version}: {services}.");
        if (mismatches.Count > 0)
        {
            LogMismatch(logger, manifest.Version, services);
        }

        await store.RecordAsync(report, cancellationToken).ConfigureAwait(false);
        return report;
    }

    private ReleaseComponentReport Report(string status, string? version, string detail) =>
        new(Component, status, version, ReleaseIntegrity.BuildVersion, detail, time.GetUtcNow());

    [LoggerMessage(Level = LogLevel.Critical, Message = "Containers run images that are not release {Version}: {Services}")]
    private static partial void LogMismatch(ILogger logger, string version, string services);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The image integrity check failed; it runs again in 10 minutes")]
    private static partial void LogCheckFailed(ILogger logger, Exception exception);
}
