using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Zelvo.Data.Auditing;
using Zelvo.Data.Installation;
using Zelvo.Data.Tenancy;
using Zelvo.Licensing;

namespace Zelvo.Updater;

/// <summary>
/// The updater service (module 13): says it is there, picks up the install whose time has come
/// (one at a time; the database lets only one be scheduled or running), runs it with
/// <see cref="UpdateInstaller"/> and records the outcome and its audit entries. It listens on no
/// port: it only reads installs that administrators scheduled in Settings → Updates, and it checks
/// the package's signature again itself before changing anything.
/// </summary>
internal sealed partial class UpdaterService(
    IServiceScopeFactory scopes, NpgsqlDataSource database, UpdateInstaller installer, IContainerHost host, IOptions<UpdaterOptions> options,
    StatusFile status, ILogger<UpdaterService> logger) : BackgroundService
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (NpgsqlException ex)
            {
                LogDatabase(logger, ex.Message);
            }

            await Task.Delay(options.Value.PollInterval, stoppingToken).ConfigureAwait(false);
        }
    }

    /// <summary>One round: heartbeat, then the install that is due, if any.</summary>
    internal async Task<InstallOutcome?> RunOnceAsync(CancellationToken cancellationToken)
    {
        await using (var heartbeat = database.CreateCommand("UPDATE installation SET updater_seen_at = now()"))
        {
            await heartbeat.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var claimed = await ClaimAsync(cancellationToken).ConfigureAwait(false);
        if (claimed is not { } install)
        {
            return null;
        }

        LogStarting(logger, install.Plan.InstallId, install.Plan.FromVersion, install.Plan.ToVersion);
        await AuditAsync(install.TenantId, "update.install.started", install, null, cancellationToken).ConfigureAwait(false);
        await status.WriteAsync("installing", install.Plan.ToVersion, [], cancellationToken).ConfigureAwait(false);
        var outcome = await installer.InstallAsync(install.Plan, cancellationToken).ConfigureAwait(false);

        // After a database restore the connections of before are gone; start afresh.
        NpgsqlConnection.ClearAllPools();
        await FinishAsync(install, outcome).ConfigureAwait(false);
        await status.WriteAsync(outcome.State switch
        {
            UpdateInstallState.Succeeded => "done",
            UpdateInstallState.RolledBack => "rolled-back",
            _ => "failed",
        }, install.Plan.ToVersion, outcome.Steps, CancellationToken.None).ConfigureAwait(false);
        LogFinished(logger, install.Plan.InstallId, outcome.State);

        if (outcome is { State: UpdateInstallState.Succeeded, UpdaterImageChanged: true })
        {
            // The last thing: the updater is replaced by its new image.
            await host.RestartSelfAsync(CancellationToken.None).ConfigureAwait(false);
        }

        return outcome;
    }

    private async Task<ClaimedInstall?> ClaimAsync(CancellationToken cancellationToken)
    {
        await using var command = database.CreateCommand("""
            UPDATE update_installs i SET state = 'Running', started_at = now(), updated_at = now()
            FROM update_packages p
            WHERE i.id = (SELECT id FROM update_installs WHERE state = 'Scheduled' AND scheduled_for <= now() ORDER BY scheduled_for LIMIT 1 FOR UPDATE SKIP LOCKED)
              AND p.id = i.package_id
            RETURNING i.id, p.storage_path, p.sha256, i.from_version, i.to_version, i.requested_by_tenant_id, i.requested_by_user_id, p.id
            """);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new ClaimedInstall(
            new InstallPlan(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4)),
            reader.IsDBNull(5) ? null : reader.GetGuid(5), reader.IsDBNull(6) ? null : reader.GetGuid(6), reader.GetGuid(7));
    }

    private async Task FinishAsync(ClaimedInstall install, InstallOutcome outcome)
    {
        // Retried for a while: the database may still be starting after a restore.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await using var connection = await database.OpenConnectionAsync().ConfigureAwait(false);
                await using var transaction = await connection.BeginTransactionAsync().ConfigureAwait(false);
                await using (var update = new NpgsqlCommand("""
                    UPDATE update_installs SET state = @state, steps = @steps::jsonb, error = @error, backup_path = @backup, finished_at = now(), updated_at = now()
                    WHERE id = @id
                    """, connection, transaction))
                {
                    update.Parameters.AddWithValue("state", outcome.State.ToString());
                    update.Parameters.AddWithValue("steps", JsonSerializer.Serialize(outcome.Steps, Json));
                    update.Parameters.AddWithValue("error", (object?)Truncate(outcome.Error, UpdateInstall.MessageMaxLength) ?? DBNull.Value);
                    update.Parameters.AddWithValue("backup", (object?)outcome.BackupPath ?? DBNull.Value);
                    update.Parameters.AddWithValue("id", install.Plan.InstallId);
                    await update.ExecuteNonQueryAsync().ConfigureAwait(false);
                }

                if (outcome.State == UpdateInstallState.Succeeded)
                {
                    await using var package = new NpgsqlCommand("UPDATE update_packages SET status = 'Installed' WHERE id = @id", connection, transaction);
                    package.Parameters.AddWithValue("id", install.PackageId);
                    await package.ExecuteNonQueryAsync().ConfigureAwait(false);
                }

                await transaction.CommitAsync().ConfigureAwait(false);
                break;
            }
            catch (NpgsqlException) when (attempt < 20)
            {
                await Task.Delay(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            }
        }

        var action = outcome.State switch
        {
            UpdateInstallState.Succeeded => "update.install.succeeded",
            UpdateInstallState.RolledBack => "update.install.rolled-back",
            _ => "update.install.failed",
        };
        await AuditAsync(install.TenantId, action, install, outcome, CancellationToken.None).ConfigureAwait(false);
    }

    // Recorded in the audit log of the tenant whose administrator scheduled the install.
    private async Task AuditAsync(Guid? tenantId, string action, ClaimedInstall install, InstallOutcome? outcome, CancellationToken cancellationToken)
    {
        if (tenantId is not { } id)
        {
            return;
        }

        await using var scope = scopes.CreateAsyncScope();
        var slug = await SlugAsync(id, cancellationToken).ConfigureAwait(false);
        if (slug is null)
        {
            return;
        }

        scope.ServiceProvider.GetRequiredService<ITenantContext>().SetTenant(id, slug);
        scope.ServiceProvider.GetRequiredService<AuditContext>().SetActor(AuditActorType.System, "updater", "Zelvo updater");
        var audit = scope.ServiceProvider.GetRequiredService<IAuditLog>();
        await audit.RecordAsync(AuditCategory.Admin, action, "update-install", install.Plan.InstallId.ToString(), new Dictionary<string, object?>
        {
            ["fromVersion"] = install.Plan.FromVersion, ["toVersion"] = install.Plan.ToVersion, ["requestedBy"] = install.UserId?.ToString(),
            ["error"] = outcome?.Error, ["steps"] = outcome?.Steps.Select(s => $"{s.Key}:{s.State}").ToArray(),
        }, cancellationToken).ConfigureAwait(false);
    }

    // Row-level security shows a tenant only to a connection set to it, for this transaction only.
    private async Task<string?> SlugAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand("SELECT set_config('zelvo.tenant_id', @t, true); SELECT slug FROM tenants WHERE id = @id", connection, transaction);
        command.Parameters.AddWithValue("t", tenantId.ToString());
        command.Parameters.AddWithValue("id", tenantId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? reader.GetString(0) : null;
    }

    private static string? Truncate(string? value, int max) => value is null ? null : value.Length <= max ? value : value[..max];

    private sealed record ClaimedInstall(InstallPlan Plan, Guid? TenantId, Guid? UserId, Guid PackageId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Installing update {InstallId}: {From} to {To}")]
    private static partial void LogStarting(ILogger logger, Guid installId, string from, string to);

    [LoggerMessage(Level = LogLevel.Information, Message = "Update {InstallId} ended: {State}")]
    private static partial void LogFinished(ILogger logger, Guid installId, UpdateInstallState state);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Updater could not reach the database: {Message}")]
    private static partial void LogDatabase(ILogger logger, string message);
}

/// <summary>
/// Progress of each step: into the install's record (while the database is up) and into the status
/// file the maintenance page reads while Zelvo's own pages are stopped.
/// </summary>
internal sealed class InstallProgress(NpgsqlDataSource database, StatusFile status) : IInstallProgress
{
    public async Task ReportAsync(Guid installId, IReadOnlyList<UpdateInstallStep> steps, CancellationToken cancellationToken)
    {
        var rollingBack = steps.Any(s => s.Key.StartsWith("rollback-", StringComparison.Ordinal) && s.State != "Pending");
        await status.WriteAsync(rollingBack ? "rolling-back" : "installing", null, steps, cancellationToken).ConfigureAwait(false);
        try
        {
            await using var command = database.CreateCommand("UPDATE update_installs SET steps = @steps::jsonb, updated_at = now() WHERE id = @id");
            command.Parameters.AddWithValue("steps", JsonSerializer.Serialize(steps, UpdaterService.Json));
            command.Parameters.AddWithValue("id", installId);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException)
        {
            // The database is being restored; the outcome is written once it is back.
        }
    }
}

/// <summary>
/// <c>status/status.json</c> on the updates volume, served by the reverse proxy to the maintenance
/// page: only the state, the version and each step's key and state (no names, no messages).
/// </summary>
internal sealed class StatusFile(IOptions<UpdateOptions> updates, TimeProvider time)
{
    private string? _version;

    public async Task WriteAsync(string state, string? version, IReadOnlyList<UpdateInstallStep> steps, CancellationToken cancellationToken)
    {
        _version = version ?? _version;
        Directory.CreateDirectory(updates.Value.StatusDirectory);
        var path = Path.Combine(updates.Value.StatusDirectory, "status.json");
        var temporary = path + ".tmp";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(new
        {
            state, version = _version, updatedAt = time.GetUtcNow(), steps = steps.Select(s => new { key = s.Key, state = s.State }),
        }, UpdaterService.Json), cancellationToken).ConfigureAwait(false);
        File.Move(temporary, path, overwrite: true);
    }
}
