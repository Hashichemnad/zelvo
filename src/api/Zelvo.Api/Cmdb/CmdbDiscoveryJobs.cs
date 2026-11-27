using Microsoft.EntityFrameworkCore;
using Npgsql;
using Zelvo.Connectors;
using Zelvo.Data;
using Zelvo.Data.Auditing;
using Zelvo.Data.Cmdb;
using Zelvo.Data.Integrations;
using Zelvo.Data.Tenancy;
using Zelvo.Integrations;
using Zelvo.Secrets;

namespace Zelvo.Api.Cmdb;

/// <summary>
/// Reads a pull source with the connector's app or connection (or vCenter's own password),
/// reconciles what it reports as a complete list, and records the sync: the source's last sync
/// and counts, the audit log (<c>cmdb.discovery.synced</c>) and every exchange in the integration log
/// with personal data masked.
/// </summary>
internal sealed class CmdbDiscoveryRunner(
    ZelvoDbContext db, ISecretProtector secrets, IDiscoveryClients clients, CmdbReconciler reconciler, IAuditLog audit, TimeProvider time)
{
    /// <summary>Most exchanges of one sync kept in the integration log (an Intune sync asks once per app).</summary>
    private const int MaxLoggedExchanges = 200;

    /// <summary>Purpose of a source's own credential.</summary>
    public static string Purpose(Guid sourceId) => $"cmdb-discovery:{sourceId:N}.credential";

    /// <summary>What the source reports now, or why it could not be read.</summary>
    public async Task<(IReadOnlyList<DiscoveredRecord>? Records, string? Error, ExchangeLog Log)> ReadAsync(CiDiscoverySource source, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        var log = new ExchangeLog();
        try
        {
            var connector = source.ConnectorId is { } id ? await db.Connectors.AsNoTracking().SingleOrDefaultAsync(c => c.Id == id, cancellationToken) : null;
            var connectorCredential = connector is null ? null : await ConnectorSecrets.ReadAsync(secrets, connector, cancellationToken);
            var credential = source.CredentialCiphertext is { } ciphertext ? await secrets.UnprotectAsync(Purpose(source.Id), ciphertext, cancellationToken) : null;
            var records = await clients.DiscoverAsync(source.Kind, DiscoverySettings.Read(source.Settings), connector,
                connector is null ? null : ConnectorSettings.Read(connector.Settings), connectorCredential, credential, log, cancellationToken);
            return (records, null, log);
        }
        catch (ConnectorException ex)
        {
            return (null, ex.Summary, log);
        }
        catch (SecretDecryptionException)
        {
            return (null, "key-unreadable: A stored credential cannot be decrypted. Enter it again.", log);
        }
    }

    /// <summary>Syncs source <paramref name="id"/>; returns the counts, or null when it could not be read (the error is kept on the source).</summary>
    public async Task<ReconcileStats?> RunAsync(Guid id, CancellationToken cancellationToken)
    {
        var source = await db.CiDiscoverySources.AsNoTracking().SingleOrDefaultAsync(s => s.Id == id, cancellationToken);
        if (source is null || source.Kind == DiscoverySourceKind.DiscoveryApi)
        {
            return null;
        }

        var started = time.GetUtcNow();
        var (records, error, log) = await ReadAsync(source, cancellationToken);
        ReconcileStats? stats = null;
        if (records is not null)
        {
            stats = await reconciler.ApplyAsync(source, records, true, cancellationToken);
        }

        await LogAsync(source, log, "sync", cancellationToken);
        var tracked = await db.CiDiscoverySources.SingleAsync(s => s.Id == id, cancellationToken);
        tracked.LastSyncAt = started;
        tracked.LastSyncSucceeded = error is null;
        tracked.LastError = error is null ? null : Cut(PersonalDataMasker.MaskText(error), CiDiscoverySource.ErrorMaxLength);
        tracked.LastSyncStats = stats?.Write() ?? tracked.LastSyncStats;
        tracked.NextSyncAt = tracked.Active ? started.AddMinutes(tracked.SyncMinutes) : null;
        tracked.LeaseUntil = null;
        var details = stats?.ToAudit() ?? new Dictionary<string, object?>(StringComparer.Ordinal);
        details["succeeded"] = error is null;
        audit.Add(AuditCategory.Change, "cmdb.discovery.synced", "ci_discovery_source", id.ToString(), details);
        await db.SaveChangesAsync(cancellationToken);
        return stats;
    }

    /// <summary>The exchanges of a sync or test, masked, in the integration log.</summary>
    public async Task LogAsync(CiDiscoverySource source, ExchangeLog log, string operation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(log);
        var now = time.GetUtcNow();
        foreach (var exchange in log.Exchanges.Take(MaxLoggedExchanges))
        {
            var url = new Uri(exchange.Url);
            db.IntegrationLog.Add(new IntegrationLogEntry
            {
                TenantId = source.TenantId,
                OccurredAt = now,
                Direction = IntegrationDirection.Outgoing,
                EventType = $"cmdb.discovery.{source.Kind.ToString().ToLowerInvariant()}.{operation}",
                Method = exchange.Method,
                Target = Cut(url.GetLeftPart(UriPartial.Path) + PersonalDataMasker.MaskQueryString(url.Query), IntegrationLogEntry.TargetMaxLength),
                StatusCode = exchange.Status,
                DurationMs = exchange.DurationMs,
                RequestBody = PersonalDataMasker.MaskBody(exchange.RequestBody, exchange.RequestType ?? "application/json", IntegrationLogEntry.BodyMaxLength),
                ResponseBody = PersonalDataMasker.MaskBody(exchange.ResponseBody, exchange.ResponseType, IntegrationLogEntry.BodyMaxLength),
                Error = exchange.Error is null ? null : Cut(PersonalDataMasker.MaskText(exchange.Error), IntegrationLogEntry.ErrorMaxLength),
                CorrelationId = System.Diagnostics.Activity.Current?.TraceId.ToString(),
            });
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    private static string Cut(string value, int max) => value.Length <= max ? value : value[..max];
}

/// <summary>
/// Runs the due pull sources (task G2) in the API every minute, tenant by tenant, with a lease
/// per source (<c>FOR UPDATE SKIP LOCKED</c>) so several API instances share the work; the CMDB's
/// numbering, checks and search documents live in the API.
/// </summary>
internal sealed partial class CmdbDiscoveryJobs(IServiceScopeFactory scopes, IConfiguration configuration, ILogger<CmdbDiscoveryJobs> logger, TimeProvider time) : BackgroundService
{
    /// <summary>Runs the due sources of every tenant once; returns how many ran.</summary>
    public async Task<int> RunOnceAsync(CancellationToken cancellationToken)
    {
        var ran = 0;
        foreach (var (id, slug) in await TenantsAsync(cancellationToken))
        {
            ran += await RunTenantAsync(id, slug, cancellationToken);
        }

        return ran;
    }

    /// <summary>The due sources of one tenant.</summary>
    public async Task<int> RunTenantAsync(Guid tenantId, string slug, CancellationToken cancellationToken)
    {
        List<Guid> due;
        await using (var scope = scopes.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<ITenantContext>().SetTenant(tenantId, slug);
            var db = scope.ServiceProvider.GetRequiredService<ZelvoDbContext>();
            var lease = time.GetUtcNow() + TimeSpan.FromHours(1);
            due = await db.Database.SqlQuery<Guid>($"""
                UPDATE ci_discovery_sources SET lease_until = {lease}
                WHERE id IN (
                    SELECT id FROM ci_discovery_sources
                    WHERE active AND kind <> 'DiscoveryApi' AND next_sync_at <= now() AND (lease_until IS NULL OR lease_until < now())
                    ORDER BY next_sync_at LIMIT 5
                    FOR UPDATE SKIP LOCKED)
                RETURNING id AS "Value"
                """).ToListAsync(cancellationToken);
        }

        foreach (var id in due)
        {
            await using var scope = scopes.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<ITenantContext>().SetTenant(tenantId, slug);
            scope.ServiceProvider.GetRequiredService<AuditContext>().SetActor(AuditActorType.System, "cmdb.discovery", "CMDB discovery");
            try
            {
                await scope.ServiceProvider.GetRequiredService<CmdbDiscoveryRunner>().RunAsync(id, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                LogSourceFailed(logger, slug, exception);
            }
        }

        return due.Count;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!configuration.GetValue("Cmdb:Discovery:Jobs:Enabled", true))
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                LogFailed(logger, exception);
            }

            try
            {
                await Task.Delay(configuration.GetValue("Cmdb:Discovery:Jobs:Interval", TimeSpan.FromMinutes(1)), time, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task<List<(Guid Id, string Slug)>> TenantsAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ZelvoDbContext>();
        await db.Database.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("SELECT id, slug FROM zelvo_cmdb_discovery_tenants()", (NpgsqlConnection)db.Database.GetDbConnection());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var tenants = new List<(Guid, string)>();
        while (await reader.ReadAsync(cancellationToken))
        {
            tenants.Add((reader.GetGuid(0), reader.GetString(1)));
        }

        return tenants;
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "The CMDB discovery jobs failed.")]
    private static partial void LogFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "A discovery source of tenant {Tenant} failed; it is tried again at its next sync.")]
    private static partial void LogSourceFailed(ILogger logger, string tenant, Exception exception);
}
