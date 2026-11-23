using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using Zelvo.Data;
using Zelvo.Data.Tenancy;

namespace Zelvo.Integrations;

/// <summary>
/// Worker job: sends webhook deliveries that are due, tenant by tenant. A batch is claimed with a
/// short lease (<c>FOR UPDATE SKIP LOCKED</c>), so several workers share the work and a delivery
/// whose worker stopped is picked up again when the lease ends. Each delivery is sent in its own
/// scope with the tenant set, so row-level security applies.
/// </summary>
public sealed partial class WebhookDispatcher(
    IServiceScopeFactory scopes,
    IntegrationOptions options,
    ILogger<WebhookDispatcher> logger) : BackgroundService
{
    private const int BatchSize = 50;
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(2);

    /// <summary>Sends every delivery that is due now; returns how many attempts were made.</summary>
    public async Task<int> RunOnceAsync(CancellationToken cancellationToken)
    {
        var attempts = 0;
        foreach (var (tenantId, slug) in await DueTenantsAsync(cancellationToken).ConfigureAwait(false))
        {
            List<long> claimed;
            do
            {
                claimed = await ClaimAsync(tenantId, slug, cancellationToken).ConfigureAwait(false);
                await Parallel.ForEachAsync(claimed,
                    new ParallelOptions { MaxDegreeOfParallelism = options.Webhooks.Concurrency, CancellationToken = cancellationToken },
                    async (id, token) =>
                    {
                        await AttemptAsync(tenantId, slug, id, token).ConfigureAwait(false);
                        Interlocked.Increment(ref attempts);
                    }).ConfigureAwait(false);
            }
            while (claimed.Count == BatchSize);
        }

        return attempts;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                LogRunFailed(logger, exception);
            }

            try
            {
                await Task.Delay(options.Webhooks.PollInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task AttemptAsync(Guid tenantId, string slug, long deliveryId, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>().SetTenant(tenantId, slug);
        try
        {
            await scope.ServiceProvider.GetRequiredService<WebhookDeliveryService>().AttemptAsync(deliveryId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The lease ends and the delivery is tried again; the error is not the receiver's.
            LogAttemptFailed(logger, deliveryId, exception);
        }
    }

    private async Task<List<(Guid Id, string Slug)>> DueTenantsAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ZelvoDbContext>();
        await db.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand("SELECT id, slug FROM zelvo_webhook_due_tenants()", (NpgsqlConnection)db.Database.GetDbConnection());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var tenants = new List<(Guid, string)>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            tenants.Add((reader.GetGuid(0), reader.GetString(1)));
        }

        return tenants;
    }

    private async Task<List<long>> ClaimAsync(Guid tenantId, string slug, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>().SetTenant(tenantId, slug);
        var db = scope.ServiceProvider.GetRequiredService<ZelvoDbContext>();
        var leaseUntil = DateTimeOffset.UtcNow + Lease;
        return await db.Database.SqlQuery<long>($"""
            UPDATE webhook_deliveries SET lease_until = {leaseUntil}
            WHERE id IN (
                SELECT id FROM webhook_deliveries
                WHERE status = 'Pending' AND next_attempt_at <= now() AND (lease_until IS NULL OR lease_until < now())
                ORDER BY next_attempt_at, id
                LIMIT {BatchSize}
                FOR UPDATE SKIP LOCKED)
            RETURNING id AS "Value"
            """).ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Sending webhooks failed; retrying")]
    private static partial void LogRunFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Webhook delivery {DeliveryId} could not be attempted; it will be retried")]
    private static partial void LogAttemptFailed(ILogger logger, long deliveryId, Exception exception);
}

/// <summary>
/// Worker job: removes integration log entries and finished webhook deliveries older than
/// <see cref="IntegrationOptions.LogRetention"/>, every hour.
/// </summary>
public sealed partial class IntegrationRecordsPurge(
    IServiceScopeFactory scopes,
    IntegrationOptions options,
    ILogger<IntegrationRecordsPurge> logger) : BackgroundService
{
    /// <summary>Removes old records now; returns how many.</summary>
    public async Task<long> RunOnceAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ZelvoDbContext>();
        var before = DateTimeOffset.UtcNow - options.LogRetention;
        var removed = await db.Database.SqlQuery<long>($"SELECT zelvo_purge_integration_records({before}) AS \"Value\"")
            .SingleAsync(cancellationToken).ConfigureAwait(false);
        if (removed > 0)
        {
            LogPurged(logger, removed);
        }

        return removed;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        do
        {
            try
            {
                await RunOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                LogPurgeFailed(logger, exception);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Removed {Count} integration log entries and webhook deliveries past their retention.")]
    private static partial void LogPurged(ILogger logger, long count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Removing old integration records failed; trying again in an hour")]
    private static partial void LogPurgeFailed(ILogger logger, Exception exception);
}
