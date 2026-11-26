using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Zelvo.Licensing;

/// <summary>A component's last reported check (<c>installation_components</c>).</summary>
/// <param name="Component">The component.</param>
/// <param name="Status">Its <see cref="ReleaseCheckStatus"/>, or <c>ImageMismatch</c>/<c>ImagesVerified</c> from the updater's image check.</param>
/// <param name="Version">The release version of its manifest, if any.</param>
/// <param name="BuildVersion">The version the component was built as.</param>
/// <param name="Detail">The reason, for administrators and the logs.</param>
/// <param name="CheckedAt">When it reported.</param>
public sealed record ReleaseComponentReport(string Component, string Status, string? Version, string BuildVersion, string Detail, DateTimeOffset CheckedAt);

/// <summary>Where components report their checks: the installation-wide table <c>installation_components</c>.</summary>
public interface IReleaseComponentStore
{
    /// <summary>Records <paramref name="report"/> (one row per component, replaced).</summary>
    Task RecordAsync(ReleaseComponentReport report, CancellationToken cancellationToken);

    /// <summary>Every component's last report.</summary>
    Task<IReadOnlyList<ReleaseComponentReport>> ListAsync(CancellationToken cancellationToken);
}

/// <inheritdoc />
internal sealed class PostgresReleaseComponentStore(IConfiguration configuration) : IReleaseComponentStore
{
    private string? ConnectionString =>
        configuration.GetConnectionString("Postgres") is { Length: > 0 } app ? app : configuration.GetConnectionString("PostgresOwner");

    public async Task RecordAsync(ReleaseComponentReport report, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (ConnectionString is not { } connectionString)
        {
            return;
        }

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand("""
            INSERT INTO installation_components (component, status, version, build_version, detail, checked_at)
            VALUES (@component, @status, @version, @build, @detail, @at)
            ON CONFLICT (component) DO UPDATE SET status = excluded.status, version = excluded.version,
                build_version = excluded.build_version, detail = excluded.detail, checked_at = excluded.checked_at
            """, connection);
        command.Parameters.AddWithValue("component", report.Component);
        command.Parameters.AddWithValue("status", report.Status);
        command.Parameters.AddWithValue("version", (object?)report.Version ?? DBNull.Value);
        command.Parameters.AddWithValue("build", report.BuildVersion);
        command.Parameters.AddWithValue("detail", report.Detail.Length <= 500 ? report.Detail : report.Detail[..500]);
        command.Parameters.AddWithValue("at", report.CheckedAt);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ReleaseComponentReport>> ListAsync(CancellationToken cancellationToken)
    {
        if (ConnectionString is not { } connectionString)
        {
            return [];
        }

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            "SELECT component, status, version, build_version, detail, checked_at FROM installation_components ORDER BY component", connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var reports = new List<ReleaseComponentReport>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            reports.Add(new ReleaseComponentReport(reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.GetString(3), reader.GetString(4), reader.GetFieldValue<DateTimeOffset>(5)));
        }

        return reports;
    }
}

/// <summary>Which component a service is, for <see cref="ReleaseIntegrityCheck"/>.</summary>
/// <param name="Component">The component's name in the release manifest.</param>
public sealed record ReleaseComponentIdentity(string Component);

/// <summary>
/// Checks the service against the signed release manifest when it starts (module 12 tamper
/// detection, task F6). In a release build a missing or foreign manifest, or files that differ from
/// the release, stop the service with a critical log entry; development builds only log. The
/// result is reported to <c>installation_components</c> so the other services and Settings →
/// License see it.
/// </summary>
public sealed partial class ReleaseIntegrityCheck(
    ReleaseComponentIdentity identity,
    ReleaseIntegrityOptions options,
    ImageSigningKeys keys,
    IReleaseComponentStore store,
    TimeProvider time,
    ILogger<ReleaseIntegrityCheck> logger) : IHostedService
{
    /// <summary>Whether a failed check stops the service (release builds); tests set it.</summary>
    internal bool Enforce { get; init; } = VendorKeys.IsReleaseBuild;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var result = ReleaseIntegrity.Check(identity.Component, options, keys);
        try
        {
            await store.RecordAsync(new ReleaseComponentReport(identity.Component, result.Status.ToString(), result.Version,
                ReleaseIntegrity.BuildVersion, result.Reason, time.GetUtcNow()), cancellationToken).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // The check's outcome matters more than reporting it; the log has it either way.
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            LogReportFailed(logger, identity.Component, ex);
        }

        if (result.IsVerified)
        {
            LogVerified(logger, identity.Component, result.Version ?? "?");
            return;
        }

        if (!Enforce)
        {
            if (result.Status == ReleaseCheckStatus.NoManifest)
            {
                LogDevelopment(logger, identity.Component);
            }
            else
            {
                LogFailedDevelopment(logger, identity.Component, result.Status, result.Reason);
            }

            return;
        }

        LogRefused(logger, identity.Component, result.Status, result.Reason);
        throw new LicenseException(result.Reason);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(Level = LogLevel.Critical, Message = "Release integrity check of {Component} failed ({Status}); the service refuses to start: {Reason}")]
    private static partial void LogRefused(ILogger logger, string component, ReleaseCheckStatus status, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Release integrity check of {Component} failed ({Status}): {Reason} Development builds start anyway.")]
    private static partial void LogFailedDevelopment(ILogger logger, string component, ReleaseCheckStatus status, string reason);

    [LoggerMessage(Level = LogLevel.Debug, Message = "No release manifest: {Component} runs as a development build")]
    private static partial void LogDevelopment(ILogger logger, string component);

    [LoggerMessage(Level = LogLevel.Information, Message = "Release integrity verified: {Component} is release {Version}")]
    private static partial void LogVerified(ILogger logger, string component, string version);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not report the release check of {Component}")]
    private static partial void LogReportFailed(ILogger logger, string component, Exception exception);
}

/// <summary>
/// The API's view of the other components (module 12: "services verify each other"): every minute
/// it reads their reports and logs, once per change, a component that failed its check, reports a
/// different release than the API's own or stopped reporting a verified state.
/// </summary>
internal sealed partial class ReleaseComponentMonitor(
    IReleaseComponentStore store, ReleaseIntegrityOptions options, ImageSigningKeys keys, ILogger<ReleaseComponentMonitor> logger) : BackgroundService
{
    private readonly Dictionary<string, string> _seen = new(StringComparer.Ordinal);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        do
        {
            try
            {
                Review(await store.ListAsync(stoppingToken).ConfigureAwait(false));
            }
#pragma warning disable CA1031 // A failed round is tried again next minute.
            catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
            {
                LogReviewFailed(logger, ex);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    internal IReadOnlyList<string> Review(IReadOnlyList<ReleaseComponentReport> reports)
    {
        var release = ReleaseIntegrity.ReadManifest(options.ManifestPath, keys, out _, out _)?.Version;
        var problems = new List<string>();
        foreach (var report in reports)
        {
            var problem = report.Status is nameof(ReleaseCheckStatus.Verified) or "ImagesVerified"
                ? release is not null && report.Version is not null && report.Version != release ? $"reports release {report.Version}, the installation is {release}" : null
                : report.Status == nameof(ReleaseCheckStatus.NoManifest) && release is null ? null
                : $"{report.Status}: {report.Detail}";
            var key = problem ?? "ok";
            if (_seen.TryGetValue(report.Component, out var previous) && previous == key)
            {
                continue;
            }

            _seen[report.Component] = key;
            if (problem is not null)
            {
                problems.Add(report.Component);
                LogComponentProblem(logger, report.Component, problem);
            }
        }

        return problems;
    }

    [LoggerMessage(Level = LogLevel.Critical, Message = "Release component {Component} is not the verified release: {Problem}")]
    private static partial void LogComponentProblem(ILogger logger, string component, string problem);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Could not review the release components")]
    private static partial void LogReviewFailed(ILogger logger, Exception exception);
}

/// <summary>Registers the release integrity check (task F6).</summary>
public static class ReleaseIntegrityServiceCollectionExtensions
{
    /// <summary>
    /// Checks the service as <paramref name="component"/> at startup; with
    /// <paramref name="monitorOthers"/> (the API) it also watches the other components' reports.
    /// </summary>
    public static IServiceCollection AddZelvoReleaseIntegrity(this IServiceCollection services, string component, bool monitorOthers = false)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton(new ReleaseComponentIdentity(component));
        services.AddSingleton(sp => ReleaseIntegrityOptions.From(sp.GetService<IConfiguration>()));
        services.AddSingleton(sp => ImageSigningKeys.Load(sp.GetService<IConfiguration>()));
        services.AddSingleton<IReleaseComponentStore, PostgresReleaseComponentStore>();
        services.AddHostedService<ReleaseIntegrityCheck>();
        if (monitorOthers)
        {
            services.AddHostedService<ReleaseComponentMonitor>();
        }

        return services;
    }
}
