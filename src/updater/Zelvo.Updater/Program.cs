using Npgsql;
using Zelvo.Data;
using Zelvo.Licensing;
using Zelvo.Observability;
using Zelvo.Updater;

// Zelvo's updater service (module 13, task F4): installs the update packages administrators
// imported and scheduled in Settings → Updates. See docs/architecture/updates.md.
var builder = Host.CreateApplicationBuilder(args);
builder.AddZelvoTelemetry("zelvo-updater");

// The package's signature, version and upgrade license are checked again here, with the same
// license service as the API (a tampered license stops the updater too).
builder.Services.AddZelvoLicensing(ActivationWait.StartLimited);
var connection = builder.Configuration.GetConnectionString("Postgres")
    ?? throw new InvalidOperationException("Configuration value 'ConnectionStrings:Postgres' is required.");
builder.Services.AddZelvoData(connection);
builder.Services.AddSingleton(NpgsqlDataSource.Create(connection));
builder.Services.Configure<UpdaterOptions>(builder.Configuration.GetSection("Updater"));
builder.Services.PostConfigure<UpdaterOptions>(options =>
{
    // Compose's own COMPOSE_FILE (colon separated) and a comma-separated database list replace the defaults.
    if (builder.Configuration["Updater:ComposeFileList"] is { Length: > 0 } files)
    {
        options.ComposeFiles = [.. files.Split([':', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct()];
    }

    if (builder.Configuration["Updater:DatabaseList"] is { Length: > 0 } databases)
    {
        options.Databases = [.. databases.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct()];
    }
});
builder.Services.AddZelvoUpdateOptions(builder.Configuration);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IHealthProbe, HttpHealthProbe>();
builder.Services.AddSingleton<IContainerHost, DockerComposeHost>();
builder.Services.AddSingleton<StatusFile>();
builder.Services.AddSingleton<IInstallProgress, InstallProgress>();
builder.Services.AddSingleton<UpdateInstaller>();
builder.Services.AddHostedService<UpdaterService>();

builder.Build().Run();
