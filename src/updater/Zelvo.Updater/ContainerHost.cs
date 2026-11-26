using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Options;

namespace Zelvo.Updater;

/// <summary>What the installer does to the installation's containers (Docker Compose here; Helm with task F6).</summary>
public interface IContainerHost
{
    /// <summary>Loads a <c>docker save</c> archive and returns the image references it held.</summary>
    Task<IReadOnlyList<string>> LoadImageAsync(string archive, CancellationToken cancellationToken);

    Task StopAsync(IReadOnlyList<string> services, CancellationToken cancellationToken);

    /// <summary>Starts the services on the images the environment file names now.</summary>
    Task StartAsync(IReadOnlyList<string> services, CancellationToken cancellationToken);

    /// <summary>Runs the migrate step of the version the environment file names now.</summary>
    Task MigrateAsync(CancellationToken cancellationToken);

    Task BackupDatabaseAsync(string database, string file, CancellationToken cancellationToken);

    Task RestoreDatabaseAsync(string database, string file, CancellationToken cancellationToken);

    /// <summary>Restarts the updater itself on its new image (the last thing it does).</summary>
    Task RestartSelfAsync(CancellationToken cancellationToken);

    /// <summary>The installation's running containers and their image ids (release integrity, task F6).</summary>
    Task<IReadOnlyList<RunningContainer>> RunningContainersAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<RunningContainer>>([]);
}

/// <summary>A command that failed, with the end of its error output (no secrets: no command line holds one).</summary>
public sealed class HostCommandException(string message) : Exception(message);

/// <summary>A running container of the installation: its Compose service and its image id (<c>sha256:…</c>).</summary>
public sealed record RunningContainer(string Service, string ImageId);

/// <summary>Runs <c>docker</c> and <c>docker compose</c> against the installation's Compose project.</summary>
internal sealed class DockerComposeHost(IOptions<UpdaterOptions> options) : IContainerHost
{
    private UpdaterOptions Options => options.Value;

    public async Task<IReadOnlyList<string>> LoadImageAsync(string archive, CancellationToken cancellationToken)
    {
        var output = await RunAsync("docker", ["load", "--input", archive], null, null, cancellationToken).ConfigureAwait(false);
        return [.. output.Split('\n').Where(l => l.StartsWith("Loaded image: ", StringComparison.Ordinal)).Select(l => l["Loaded image: ".Length..].Trim())];
    }

    public Task StopAsync(IReadOnlyList<string> services, CancellationToken cancellationToken) =>
        RunAsync("docker", StopArguments(services), null, null, cancellationToken);

    public Task StartAsync(IReadOnlyList<string> services, CancellationToken cancellationToken) =>
        RunAsync("docker", StartArguments(services), null, null, cancellationToken);

    public Task MigrateAsync(CancellationToken cancellationToken) =>
        RunAsync("docker", MigrateArguments(), null, null, cancellationToken);

    public async Task BackupDatabaseAsync(string database, string file, CancellationToken cancellationToken)
    {
        await using var target = File.Create(file);
        await RunAsync("docker", BackupArguments(database), target, null, cancellationToken).ConfigureAwait(false);
    }

    public async Task RestoreDatabaseAsync(string database, string file, CancellationToken cancellationToken)
    {
        await using var source = File.OpenRead(file);
        await RunAsync("docker", RestoreArguments(database), null, source, cancellationToken).ConfigureAwait(false);
    }

    public Task RestartSelfAsync(CancellationToken cancellationToken) =>
        RunAsync("docker", StartArguments([Options.SelfService]), null, null, cancellationToken);

    public async Task<IReadOnlyList<RunningContainer>> RunningContainersAsync(CancellationToken cancellationToken)
    {
        var ids = (await RunAsync("docker", [.. Compose(), "ps", "--quiet"], null, null, cancellationToken).ConfigureAwait(false))
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (ids.Length == 0)
        {
            return [];
        }

        var output = await RunAsync("docker", ["inspect", "--format", "{{index .Config.Labels \"com.docker.compose.service\"}} {{.Image}}", .. ids], null, null, cancellationToken)
            .ConfigureAwait(false);
        return [.. output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.Split(' ', 2))
            .Where(parts => parts.Length == 2 && parts[0].Length > 0)
            .Select(parts => new RunningContainer(parts[0], parts[1]))];
    }

    // The arguments of each command, kept apart so tests can check them against Compose itself.
    // Images never come from a registry: only from packages we signed (`--pull never`).
    internal List<string> StopArguments(IReadOnlyList<string> services) => [.. Compose(), "stop", .. services];

    internal List<string> StartArguments(IReadOnlyList<string> services) =>
        [.. Compose(), "up", "--detach", "--no-build", "--no-deps", "--pull", "never", .. services];

    internal List<string> MigrateArguments() => [.. Compose(), "run", "--rm", "--pull", "never", Options.MigrateService];

    internal List<string> BackupArguments(string database) => [.. Compose(), "exec", "-T", Options.PostgresService, "sh", "-c",
        "pg_dump --username=\"$POSTGRES_USER\" --format=custom --create \"$1\"", "zelvo-backup", database];

    internal List<string> RestoreArguments(string database) => [.. Compose(), "exec", "-T", Options.PostgresService, "sh", "-c",
        "dropdb --username=\"$POSTGRES_USER\" --if-exists --force \"$1\" && pg_restore --username=\"$POSTGRES_USER\" --create --exit-on-error --dbname=postgres",
        "zelvo-restore", database];

    private List<string> Compose()
    {
        var arguments = new List<string> { "compose", "--project-name", Options.ComposeProjectName, "--project-directory", Options.ComposeDirectory,
            "--env-file", Path.Combine(Options.ComposeDirectory, Options.EnvironmentFile) };
        foreach (var file in Options.ComposeFiles)
        {
            arguments.Add("--file");
            arguments.Add(Path.Combine(Options.ComposeDirectory, file));
        }

        return arguments;
    }

    private static async Task<string> RunAsync(string program, List<string> arguments, Stream? stdout, Stream? stdin, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(program) { RedirectStandardError = true, RedirectStandardOutput = true, RedirectStandardInput = stdin is not null, UseShellExecute = false };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start) ?? throw new HostCommandException($"{program} could not be started.");
        var error = process.StandardError.ReadToEndAsync(cancellationToken);
        var output = new StringBuilder();
        Task copy;
        if (stdout is not null)
        {
            copy = process.StandardOutput.BaseStream.CopyToAsync(stdout, cancellationToken);
        }
        else
        {
            copy = Task.Run(async () => output.Append(await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false)), cancellationToken);
        }

        if (stdin is not null)
        {
            await stdin.CopyToAsync(process.StandardInput.BaseStream, cancellationToken).ConfigureAwait(false);
            process.StandardInput.Close();
        }

        await copy.ConfigureAwait(false);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        var errors = await error.ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            var tail = errors.Length > 600 ? errors[^600..] : errors;
            throw new HostCommandException($"{string.Join(' ', arguments.Take(arguments.Count > 12 ? 12 : arguments.Count).Where(a => !a.Contains('/', StringComparison.Ordinal)))} ended with {process.ExitCode}: {tail.Trim()}");
        }

        return output.ToString();
    }
}
