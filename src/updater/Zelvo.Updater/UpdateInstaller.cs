using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Zelvo.Data.Installation;
using Zelvo.Licensing;

namespace Zelvo.Updater;

/// <summary>What an install is to do: the package file and what the API recorded about it.</summary>
/// <param name="InstallId">The install.</param>
/// <param name="PackagePath">The package file on the updates volume.</param>
/// <param name="Sha256">The package's SHA-256 recorded at import.</param>
/// <param name="FromVersion">The version running now.</param>
/// <param name="ToVersion">The version the package installs.</param>
public sealed record InstallPlan(Guid InstallId, string PackagePath, string Sha256, string FromVersion, string ToVersion);

/// <summary>How an install ended.</summary>
public sealed record InstallOutcome(UpdateInstallState State, IReadOnlyList<UpdateInstallStep> Steps, string? Error, string? BackupPath, bool UpdaterImageChanged);

/// <summary>Where each step is told (the database and the maintenance page's status file).</summary>
public interface IInstallProgress
{
    Task ReportAsync(Guid installId, IReadOnlyList<UpdateInstallStep> steps, CancellationToken cancellationToken);
}

/// <summary>
/// Installs an update package step by step (module 13): checks it again, loads its images, stops
/// Zelvo's services (the reverse proxy shows the maintenance page), backs up the databases and the
/// configuration, switches the version, runs the migrations, starts the services and waits until
/// they are healthy. When a step after the stop fails it rolls back by itself: the databases are
/// restored from the backup when the migrations had started, the configuration is put back and the
/// previous version is started again. Each step is reported as it goes.
/// </summary>
public sealed partial class UpdateInstaller(
    IContainerHost host, IInstallProgress progress, ILicenseService license, IOptions<UpdaterOptions> options, IOptions<UpdateOptions> updates,
    IHealthProbe health, TimeProvider time, ILogger<UpdateInstaller> logger)
{
    public static IReadOnlyList<string> InstallSteps { get; } = ["verify", "load-images", "stop", "backup", "switch-version", "migrate", "start", "health"];

    private UpdaterOptions Options => options.Value;

    public async Task<InstallOutcome> InstallAsync(InstallPlan plan, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var steps = InstallSteps.Select(k => new UpdateInstallStep(k, "Pending")).ToList();
        var backup = Path.Combine(updates.Value.BackupsDirectory, $"{time.GetUtcNow():yyyyMMddHHmmss}-{plan.InstallId:N}");
        var envFile = Path.Combine(Options.ComposeDirectory, Options.EnvironmentFile);
        var stopped = false;
        var backedUp = false;
        var switched = false;
        var migrating = false;
        var updaterImageChanged = false;
        UpdatePackageManifest? manifest = null;
        try
        {
            await StepAsync("verify", async () =>
            {
                // Nothing is stopped unless the configuration can be backed up and switched.
                if (!File.Exists(envFile) || Options.ComposeFiles.Any(f => !File.Exists(Path.Combine(Options.ComposeDirectory, f))))
                {
                    throw new InstallStepException("The Compose project's files are not reachable from the updater; check Updater:ComposeDirectory.");
                }

                var check = await UpdatePackageReader.CheckAsync(plan.PackagePath, license, Version.Parse(plan.FromVersion), updates.Value.Images, cancellationToken).ConfigureAwait(false);
                if (!check.Passed)
                {
                    throw new InstallStepException(check.Reason ?? check.Problem ?? "The package did not pass its checks.");
                }

                if (!string.Equals(check.Sha256, plan.Sha256, StringComparison.OrdinalIgnoreCase) || check.Manifest!.ParsedVersion!.ToString(3) != plan.ToVersion)
                {
                    throw new InstallStepException("The package file is not the one that was imported.");
                }

                manifest = check.Manifest;
                return $"Version {plan.ToVersion} ({manifest.Type}) signed by Zelvo; every file matches.";
            }).ConfigureAwait(false);

            await StepAsync("load-images", async () =>
            {
                var folder = Directory.CreateTempSubdirectory("zelvo-images-");
                try
                {
                    foreach (var image in manifest!.Images)
                    {
                        var archive = Path.Combine(folder.FullName, Path.GetFileName(image.File));
                        await UpdatePackageReader.ExtractAsync(plan.PackagePath, image.File, archive, cancellationToken).ConfigureAwait(false);
                        var loaded = await host.LoadImageAsync(archive, cancellationToken).ConfigureAwait(false);
                        File.Delete(archive);
                        if (!loaded.Contains(image.Reference, StringComparer.Ordinal))
                        {
                            throw new InstallStepException($"{image.File} did not hold {image.Reference}.");
                        }

                        updaterImageChanged |= image.Reference.StartsWith($"zelvo/{Options.SelfService}:", StringComparison.Ordinal);
                    }
                }
                finally
                {
                    folder.Delete(recursive: true);
                }

                return $"{manifest!.Images.Count} images loaded.";
            }).ConfigureAwait(false);

            await StepAsync("stop", async () =>
            {
                stopped = true;
                await host.StopAsync([.. Enumerable.Reverse(Options.Services)], cancellationToken).ConfigureAwait(false);
                return null;
            }).ConfigureAwait(false);

            await StepAsync("backup", async () =>
            {
                Directory.CreateDirectory(backup);
                foreach (var database in Options.Databases)
                {
                    await host.BackupDatabaseAsync(database, Path.Combine(backup, $"{database}.dump"), cancellationToken).ConfigureAwait(false);
                }

                File.Copy(envFile, Path.Combine(backup, Options.EnvironmentFile.Replace('/', '_')), overwrite: true);
                foreach (var file in Options.ComposeFiles)
                {
                    File.Copy(Path.Combine(Options.ComposeDirectory, file), Path.Combine(backup, file.Replace('/', '_')), overwrite: true);
                }

                backedUp = true;
                return $"{Options.Databases.Count} databases and the configuration backed up.";
            }).ConfigureAwait(false);

            await StepAsync("switch-version", async () =>
            {
                switched = true;
                await SetVersionAsync(envFile, plan.ToVersion, cancellationToken).ConfigureAwait(false);
                return $"{Options.VersionVariable}={plan.ToVersion}";
            }).ConfigureAwait(false);

            await StepAsync("migrate", async () =>
            {
                migrating = true;
                await host.MigrateAsync(cancellationToken).ConfigureAwait(false);
                return null;
            }).ConfigureAwait(false);

            await StepAsync("start", async () =>
            {
                await host.StartAsync(Options.Services, cancellationToken).ConfigureAwait(false);
                return null;
            }).ConfigureAwait(false);

            await StepAsync("health", async () =>
            {
                await health.WaitHealthyAsync(Options.HealthUrls, Options.HealthTimeout, cancellationToken).ConfigureAwait(false);
                return $"Version {plan.ToVersion} answers.";
            }).ConfigureAwait(false);

            PruneBackups();
            return new InstallOutcome(UpdateInstallState.Succeeded, steps, null, backup, updaterImageChanged);
        }
        catch (InstallStepFailed failure)
        {
            foreach (var pending in steps.Where(s => s.State == "Pending").ToList())
            {
                steps[steps.IndexOf(pending)] = pending with { State = "Skipped" };
            }

            if (!stopped)
            {
                // Nothing was changed: the previous version is still running.
                await ReportAsync().ConfigureAwait(false);
                return new InstallOutcome(UpdateInstallState.RolledBack, steps, failure.Message, null, false);
            }

            LogRollingBack(logger, plan.InstallId, failure.Message);
            var rolledBack = await RollBackAsync(backedUp, migrating, switched).ConfigureAwait(false);
            return new InstallOutcome(rolledBack ? UpdateInstallState.RolledBack : UpdateInstallState.Failed, steps, failure.Message, backedUp ? backup : null, false);
        }

        async Task<bool> RollBackAsync(bool haveBackup, bool restoreDatabases, bool restoreConfiguration)
        {
            // Rollback works without the cancellation token: a half-installed version must not be left behind.
            var ok = true;
            ok &= await TryAsync("rollback-stop", async () => { await host.StopAsync([.. Enumerable.Reverse(Options.Services)], CancellationToken.None).ConfigureAwait(false); return null; }).ConfigureAwait(false);
            if (restoreDatabases)
            {
                ok &= haveBackup && await TryAsync("rollback-database", async () =>
                {
                    foreach (var database in Options.Databases)
                    {
                        await host.RestoreDatabaseAsync(database, Path.Combine(backup, $"{database}.dump"), CancellationToken.None).ConfigureAwait(false);
                    }

                    return "Databases restored from the backup.";
                }).ConfigureAwait(false);
            }

            if (restoreConfiguration)
            {
                ok &= haveBackup && await TryAsync("rollback-configuration", () =>
                {
                    File.Copy(Path.Combine(backup, Options.EnvironmentFile.Replace('/', '_')), envFile, overwrite: true);
                    return Task.FromResult<string?>($"{Options.VersionVariable}={plan.FromVersion}");
                }).ConfigureAwait(false);
            }

            if (!ok)
            {
                return false;
            }

            ok &= await TryAsync("rollback-start", async () => { await host.StartAsync(Options.Services, CancellationToken.None).ConfigureAwait(false); return null; }).ConfigureAwait(false);
            ok &= ok && await TryAsync("rollback-health", async () =>
            {
                await health.WaitHealthyAsync(Options.HealthUrls, Options.HealthTimeout, CancellationToken.None).ConfigureAwait(false);
                return $"Version {plan.FromVersion} answers again.";
            }).ConfigureAwait(false);
            return ok;
        }

        async Task StepAsync(string key, Func<Task<string?>> run)
        {
            if (!await TryAsync(key, run).ConfigureAwait(false))
            {
                throw new InstallStepFailed(steps.Single(s => s.Key == key).Message ?? key);
            }
        }

        async Task<bool> TryAsync(string key, Func<Task<string?>> run)
        {
            var index = steps.FindIndex(s => s.Key == key);
            if (index < 0)
            {
                steps.Add(new UpdateInstallStep(key, "Pending"));
                index = steps.Count - 1;
            }

            steps[index] = steps[index] with { State = "Running", StartedAt = time.GetUtcNow() };
            await ReportAsync().ConfigureAwait(false);
            try
            {
                var message = await run().ConfigureAwait(false);
                steps[index] = steps[index] with { State = "Done", EndedAt = time.GetUtcNow(), Message = message };
                await ReportAsync().ConfigureAwait(false);
                return true;
            }
            catch (Exception ex) when (ex is InstallStepException or HostCommandException or IOException or UnauthorizedAccessException or InvalidDataException or TimeoutException
                or HttpRequestException or System.ComponentModel.Win32Exception or OperationCanceledException)
            {
                var message = ex is OperationCanceledException ? "The updater was stopped." : ex.Message;
                steps[index] = steps[index] with { State = "Failed", EndedAt = time.GetUtcNow(), Message = message.Length > 1000 ? message[..1000] : message };
                LogStepFailed(logger, key, plan.InstallId, message);
                await ReportAsync().ConfigureAwait(false);
                return false;
            }
        }

        Task ReportAsync() => progress.ReportAsync(plan.InstallId, steps, CancellationToken.None);
    }

    /// <summary>Writes the version variable into the environment file, keeping every other line.</summary>
    internal async Task SetVersionAsync(string envFile, string version, CancellationToken cancellationToken)
    {
        var lines = File.Exists(envFile) ? (await File.ReadAllLinesAsync(envFile, cancellationToken).ConfigureAwait(false)).ToList() : [];
        var prefix = $"{Options.VersionVariable}=";
        var index = lines.FindIndex(l => l.StartsWith(prefix, StringComparison.Ordinal));
        if (index >= 0)
        {
            lines[index] = prefix + version;
        }
        else
        {
            lines.Add(prefix + version);
        }

        var temporary = envFile + ".zelvo-update";
        await File.WriteAllLinesAsync(temporary, lines, cancellationToken).ConfigureAwait(false);
        File.Move(temporary, envFile, overwrite: true);
    }

    private void PruneBackups()
    {
        if (!Directory.Exists(updates.Value.BackupsDirectory))
        {
            return;
        }

        foreach (var old in Directory.GetDirectories(updates.Value.BackupsDirectory).Order(StringComparer.Ordinal).SkipLast(Math.Max(1, Options.KeepBackups)))
        {
            Directory.Delete(old, recursive: true);
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Update step {Step} of install {InstallId} failed: {Message}")]
    private static partial void LogStepFailed(ILogger logger, string step, Guid installId, string message);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Install {InstallId} failed ({Message}); rolling back to the previous version")]
    private static partial void LogRollingBack(ILogger logger, Guid installId, string message);
}

/// <summary>A step's own refusal, with a message for the operator.</summary>
public sealed class InstallStepException(string message) : Exception(message);

/// <summary>A step failed; the installer rolls back.</summary>
internal sealed class InstallStepFailed(string message) : Exception(message);

/// <summary>Waits until every address answers 200.</summary>
public interface IHealthProbe
{
    Task WaitHealthyAsync(IReadOnlyList<string> urls, TimeSpan timeout, CancellationToken cancellationToken);
}

/// <inheritdoc />
internal sealed class HttpHealthProbe(TimeProvider time) : IHealthProbe, IDisposable
{
    private readonly HttpClient _client = new() { Timeout = TimeSpan.FromSeconds(10) };

    public void Dispose() => _client.Dispose();

    public async Task WaitHealthyAsync(IReadOnlyList<string> urls, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var until = time.GetUtcNow() + timeout;
        var client = _client;
        foreach (var url in urls)
        {
            while (true)
            {
                try
                {
                    using var response = await client.GetAsync(new Uri(url), cancellationToken).ConfigureAwait(false);
                    if (response.IsSuccessStatusCode)
                    {
                        break;
                    }
                }
                catch (HttpRequestException)
                {
                }
                catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                }

                if (time.GetUtcNow() > until)
                {
                    throw new TimeoutException($"{new Uri(url).Host} did not become healthy within {timeout.TotalMinutes:0} minutes.");
                }

                await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
