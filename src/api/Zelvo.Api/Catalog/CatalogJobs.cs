using Microsoft.EntityFrameworkCore;
using Npgsql;
using Zelvo.Data;
using Zelvo.Data.Auditing;
using Zelvo.Data.Tenancy;
using Zelvo.Data.Tickets;

namespace Zelvo.Api.Catalog;

/// <summary>
/// The catalog's background work (task C5), run by the API because requests are made there
/// (approvals run on the embedded engine): proactive services (dates, schedules, due runs, expired
/// offers) and bundles whose parts changed elsewhere (for example closed by the worker). Every
/// minute by default (<c>Catalog:Jobs:Interval</c>); off with <c>Catalog:Jobs:Enabled=false</c>.
/// One API instance works on a tenant at a time (a PostgreSQL advisory lock).
/// </summary>
internal sealed partial class CatalogJobs(IServiceScopeFactory scopes, IConfiguration configuration, ILogger<CatalogJobs> logger, TimeProvider time) : BackgroundService
{
    private TimeSpan Interval => configuration.GetValue("Catalog:Jobs:Interval", TimeSpan.FromMinutes(1));

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!configuration.GetValue("Catalog:Jobs:Enabled", true))
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                foreach (var (id, slug) in await TenantsAsync(stoppingToken))
                {
                    await RunTenantAsync(id, slug, time.GetUtcNow(), stoppingToken);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                LogFailed(logger, exception);
            }

            try
            {
                await Task.Delay(Interval, time, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>One tenant's catalog work; returns how many runs and bundles changed (tests call it directly).</summary>
    public async Task<int> RunTenantAsync(Guid tenantId, string slug, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>().SetTenant(tenantId, slug);
        scope.ServiceProvider.GetRequiredService<AuditContext>().SetActor(AuditActorType.System, "catalog.jobs", "Catalog jobs");
        var db = scope.ServiceProvider.GetRequiredService<ZelvoDbContext>();
        await db.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            if (!await LockAsync(db, tenantId, "SELECT pg_try_advisory_lock(hashtext('zelvo.catalog-jobs'), hashtext(@tenant))", cancellationToken))
            {
                return 0;
            }

            var changed = await scope.ServiceProvider.GetRequiredService<ProactiveEngine>().RunAsync(now, cancellationToken);

            // Bundles moved on by changes made elsewhere (the worker's auto-close, rejections).
            var bundles = await db.ServiceRequests.AsNoTracking().Where(r => r.IsBundle)
                .Join(db.Tickets.AsNoTracking().Where(t => t.State != TicketState.Closed), r => r.TicketId, t => t.Id, (r, t) => t.Id)
                .Take(500).ToListAsync(cancellationToken);
            var progress = scope.ServiceProvider.GetRequiredService<BundleProgress>();
            foreach (var bundle in bundles)
            {
                if (await progress.AdvanceAsync(bundle, cancellationToken))
                {
                    changed++;
                }
            }

            return changed;
        }
        finally
        {
            await LockAsync(db, tenantId, "SELECT pg_advisory_unlock(hashtext('zelvo.catalog-jobs'), hashtext(@tenant))", CancellationToken.None);
            await db.Database.CloseConnectionAsync();
        }
    }

    private static async Task<bool> LockAsync(ZelvoDbContext db, Guid tenantId, string sql, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, (NpgsqlConnection)db.Database.GetDbConnection());
        command.Parameters.AddWithValue("tenant", tenantId.ToString());
        return await command.ExecuteScalarAsync(cancellationToken) is true;
    }

    private async Task<List<(Guid Id, string Slug)>> TenantsAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ZelvoDbContext>();
        await db.Database.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("SELECT id, slug FROM zelvo_catalog_job_tenants()", (NpgsqlConnection)db.Database.GetDbConnection());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var tenants = new List<(Guid, string)>();
        while (await reader.ReadAsync(cancellationToken))
        {
            tenants.Add((reader.GetGuid(0), reader.GetString(1)));
        }

        return tenants;
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "The catalog jobs failed.")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}
