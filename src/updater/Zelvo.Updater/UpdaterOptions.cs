namespace Zelvo.Updater;

/// <summary>
/// How the updater reaches the installation it updates (<c>Updater:*</c>): the Compose project
/// (directory, name, files, the <c>.env</c> file and the variable that holds Zelvo's version),
/// the services an install stops and starts, the databases it backs up, and the health checks
/// that decide whether the new version works.
/// </summary>
public sealed class UpdaterOptions
{
    /// <summary>The Compose project's directory, mounted at the same path as on the host (bind mounts resolve on the host).</summary>
    public string ComposeDirectory { get; set; } = "/zelvo/compose";

    public string ComposeProjectName { get; set; } = "zelvo";

    /// <summary>Compose files, relative to <see cref="ComposeDirectory"/>.</summary>
    public List<string> ComposeFiles { get; set; } = ["compose.yaml"];

    /// <summary>The environment file Compose reads, relative to <see cref="ComposeDirectory"/>.</summary>
    public string EnvironmentFile { get; set; } = ".env";

    /// <summary>The variable in the environment file that picks the image tags.</summary>
    public string VersionVariable { get; set; } = "ZELVO_VERSION";

    /// <summary>Services stopped before the backup and started on the new version, in start order.</summary>
    public List<string> Services { get; set; } = ["opensearch", "keycloak", "api", "worker", "web"];

    /// <summary>The one-off service that applies migrations.</summary>
    public string MigrateService { get; set; } = "migrate";

    /// <summary>The PostgreSQL service (backups and restores run inside it).</summary>
    public string PostgresService { get; set; } = "postgres";

    /// <summary>Databases backed up and restored (Zelvo's and Keycloak's).</summary>
    public List<string> Databases { get; set; } = ["zelvo", "keycloak"];

    /// <summary>Addresses that must answer 200 before the new version counts as working.</summary>
    public List<string> HealthUrls { get; set; } = ["http://api:8080/health/ready", "http://web:3000/healthz"];

    public TimeSpan HealthTimeout { get; set; } = TimeSpan.FromMinutes(10);

    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>Backups of earlier installs kept on the updates volume.</summary>
    public int KeepBackups { get; set; } = 3;

    /// <summary>The updater's own service, restarted on the new image after a successful install.</summary>
    public string SelfService { get; set; } = "updater";

    /// <summary>
    /// The folder with the installation's signed release manifest (task F6), which the services
    /// mount read-only; relative paths are under <see cref="ComposeDirectory"/>.
    /// </summary>
    public string ReleaseDirectory { get; set; } = "release";
}
