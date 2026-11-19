using System.Data;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using RabbitMQ.Client;
using Zelvo.Data;
using Zelvo.Data.Search;
using Zelvo.Data.Tenancy;

namespace Zelvo.Search;

/// <summary>
/// Worker job: publishes outbox entries to RabbitMQ, tenant by tenant, oldest first. Each batch is
/// locked (<c>FOR UPDATE SKIP LOCKED</c>, so several workers share the work), published with a
/// broker confirmation, then marked as published. Entries published long ago without being
/// indexed are published again (indexing is idempotent).
/// </summary>
public sealed partial class SearchOutboxRelay(
    IServiceScopeFactory scopes,
    SearchRabbitConnection rabbit,
    IOptions<SearchPipelineOptions> options,
    ILogger<SearchOutboxRelay> logger) : BackgroundService
{
    private IChannel? _channel;

    /// <summary>Publishes everything waiting now; returns how many entries were published.</summary>
    public async Task<int> RunOnceAsync(CancellationToken cancellationToken)
    {
        var channel = await GetChannelAsync(cancellationToken).ConfigureAwait(false);
        var published = 0;
        foreach (var (tenantId, slug) in await PendingTenantsAsync(cancellationToken).ConfigureAwait(false))
        {
            int count;
            do
            {
                count = await PublishBatchAsync(channel, tenantId, slug, cancellationToken).ConfigureAwait(false);
                published += count;
                SearchTelemetry.Published.Add(count);
            }
            while (count == options.Value.BatchSize);
        }

        return published;
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
                LogRelayFailed(logger, exception);
                await DropChannelAsync().ConfigureAwait(false);
            }

            try
            {
                await Task.Delay(options.Value.PollInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <inheritdoc />
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
        await DropChannelAsync().ConfigureAwait(false);
    }

    private async Task<List<(Guid Id, string Slug)>> PendingTenantsAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ZelvoDbContext>();
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        await db.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand("SELECT id, slug FROM zelvo_search_outbox_tenants(@stale)", connection);
        command.Parameters.AddWithValue("stale", options.Value.RepublishAfter);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var tenants = new List<(Guid, string)>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            tenants.Add((reader.GetGuid(0), reader.GetString(1)));
        }

        return tenants;
    }

    private async Task<int> PublishBatchAsync(IChannel channel, Guid tenantId, string slug, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>().SetTenant(tenantId, slug);
        var db = scope.ServiceProvider.GetRequiredService<ZelvoDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken).ConfigureAwait(false);

        var staleBefore = DateTimeOffset.UtcNow - options.Value.RepublishAfter;
        var entries = await db.SearchOutbox
            .FromSql($"""
                SELECT * FROM search_outbox
                WHERE failed_at IS NULL AND (published_at IS NULL OR published_at < {staleBefore})
                ORDER BY id
                LIMIT {options.Value.BatchSize}
                FOR UPDATE SKIP LOCKED
                """)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        if (entries.Count == 0)
        {
            return 0;
        }

        var message = new SearchIndexMessage(tenantId, slug, [.. entries.Select(SearchIndexMessageEntry.From)]);
        var body = JsonSerializer.SerializeToUtf8Bytes(message, SearchJson.Options);
        var properties = new BasicProperties { Persistent = true, ContentType = "application/json", Type = "search.index" };

        // Throws when the broker does not confirm; the entries stay unpublished and are retried.
        await channel.BasicPublishAsync(SearchTopology.Exchange, SearchTopology.RoutingKey, mandatory: true, properties, body, cancellationToken).ConfigureAwait(false);

        var now = DateTimeOffset.UtcNow;
        foreach (var entry in entries)
        {
            entry.PublishedAt = now;
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return entries.Count;
    }

    private async Task<IChannel> GetChannelAsync(CancellationToken cancellationToken)
    {
        if (_channel is { IsOpen: true } open)
        {
            return open;
        }

        await DropChannelAsync().ConfigureAwait(false);
        var connection = await rabbit.GetAsync(cancellationToken).ConfigureAwait(false);
        var channel = await connection.CreateChannelAsync(
            new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true),
            cancellationToken).ConfigureAwait(false);
        await SearchTopology.DeclareAsync(channel, cancellationToken).ConfigureAwait(false);
        _channel = channel;
        return channel;
    }

    private async Task DropChannelAsync()
    {
        if (_channel is { } channel)
        {
            _channel = null;
            try
            {
                await channel.CloseAsync().ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                LogChannelCloseFailed(logger, exception);
            }

            channel.Dispose();
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Publishing search outbox entries failed; retrying")]
    private static partial void LogRelayFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Closing the RabbitMQ channel failed")]
    private static partial void LogChannelCloseFailed(ILogger logger, Exception exception);
}
