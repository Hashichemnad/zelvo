using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using Zelvo.Data;
using Zelvo.Data.Search;
using Zelvo.Data.Tenancy;

namespace Zelvo.Search;

/// <summary>
/// Worker job: consumes index messages and writes them to the tenant's index through its alias.
/// Every write carries the outbox id as an external version, so a message delivered twice or late
/// never overwrites newer content. Entries OpenSearch has are deleted from the outbox; entries it
/// refuses (for example a document it cannot map) are marked failed with the reason, shown in
/// Settings → Search. When OpenSearch is unavailable the message is retried after a pause.
/// </summary>
public sealed partial class SearchIndexConsumer(
    IServiceScopeFactory scopes,
    SearchRabbitConnection rabbit,
    OpenSearchClient client,
    SearchIndexManager indices,
    IOptions<SearchPipelineOptions> options,
    ILogger<SearchIndexConsumer> logger) : BackgroundService
{
    /// <summary>Indexes one message; throws <see cref="OpenSearchException"/> when it should be retried.</summary>
    public async Task ProcessAsync(SearchIndexMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        await using var scope = scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>().SetTenant(message.TenantId, message.TenantSlug);
        var db = scope.ServiceProvider.GetRequiredService<ZelvoDbContext>();

        int schemaVersion;
        var done = new List<long>();
        var failed = new Dictionary<long, string>();
        var rebuilt = false;
        try
        {
            schemaVersion = await indices.EnsureAsync(message.TenantId, cancellationToken).ConfigureAwait(false);
            var alias = indices.AliasFor(message.TenantId);
            var pending = new List<(SearchIndexMessageEntry Entry, JsonNode Action, JsonNode? Source)>();

            foreach (var entry in message.Entries)
            {
                if (entry.Operation == SearchOperation.Purge)
                {
                    await FlushAsync(pending, done, failed, cancellationToken).ConfigureAwait(false);
                    await PurgeAsync(alias, message.TenantId, entry.PurgeBefore ?? 0, cancellationToken).ConfigureAwait(false);
                    done.Add(entry.Id);
                    rebuilt = true;
                    continue;
                }

                if (BulkLine(entry, message.TenantId, alias, out var action, out var source, out var problem))
                {
                    pending.Add((entry, action!, source));
                }
                else
                {
                    failed[entry.Id] = problem!;
                }
            }

            await FlushAsync(pending, done, failed, cancellationToken).ConfigureAwait(false);
        }
        catch (OpenSearchException exception) when (exception.IsTransient)
        {
            await RecordStateAsync(db, null, exception.Message, rebuilt: false, cancellationToken).ConfigureAwait(false);
            throw;
        }

        if (done.Count > 0)
        {
            await db.SearchOutbox.Where(e => done.Contains(e.Id)).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        }

        if (failed.Count > 0)
        {
            var ids = failed.Keys.ToList();
            var entries = await db.SearchOutbox.Where(e => ids.Contains(e.Id)).ToListAsync(cancellationToken).ConfigureAwait(false);
            var now = DateTimeOffset.UtcNow;
            foreach (var entry in entries)
            {
                entry.Attempts++;
                entry.FailedAt = now;
                entry.LastError = Truncate(failed[entry.Id]);
            }

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            LogRefused(logger, failed.Count, message.TenantId);
        }

        var lastError = failed.Count > 0 ? failed.Values.Last() : null;
        await RecordStateAsync(db, schemaVersion, lastError, rebuilt, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ConsumeUntilClosedAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                LogConsumerFailed(logger, exception);
            }

            try
            {
                await Task.Delay(options.Value.RetryDelay, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task ConsumeUntilClosedAsync(CancellationToken stoppingToken)
    {
        var connection = await rabbit.GetAsync(stoppingToken).ConfigureAwait(false);
        await using var channel = await connection.CreateChannelAsync(cancellationToken: stoppingToken).ConfigureAwait(false);
        await SearchTopology.DeclareAsync(channel, stoppingToken).ConfigureAwait(false);
        await channel.BasicQosAsync(0, options.Value.Prefetch, global: false, stoppingToken).ConfigureAwait(false);

        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        channel.ChannelShutdownAsync += (_, _) =>
        {
            closed.TrySetResult();
            return Task.CompletedTask;
        };

        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.ReceivedAsync += async (_, delivery) => await HandleAsync(channel, delivery, stoppingToken).ConfigureAwait(false);
        await channel.BasicConsumeAsync(SearchTopology.Queue, autoAck: false, consumer, stoppingToken).ConfigureAwait(false);

        await closed.Task.WaitAsync(stoppingToken).ConfigureAwait(false);
    }

    private async Task HandleAsync(IChannel channel, BasicDeliverEventArgs delivery, CancellationToken stoppingToken)
    {
        SearchIndexMessage? message;
        try
        {
            message = JsonSerializer.Deserialize<SearchIndexMessage>(delivery.Body.Span, SearchJson.Options);
        }
        catch (JsonException)
        {
            message = null;
        }

        if (message is null || message.TenantId == Guid.Empty || string.IsNullOrEmpty(message.TenantSlug) || message.Entries is null)
        {
            LogUnreadable(logger);
            await channel.BasicRejectAsync(delivery.DeliveryTag, requeue: false, stoppingToken).ConfigureAwait(false);
            return;
        }

        try
        {
            await ProcessAsync(message, stoppingToken).ConfigureAwait(false);
            await channel.BasicAckAsync(delivery.DeliveryTag, multiple: false, stoppingToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // OpenSearch or PostgreSQL unavailable: wait, then let RabbitMQ deliver it again. After
            // the delivery limit it is dead-lettered and the relay publishes its entries again.
            LogRetrying(logger, exception, message.TenantId);
            await Task.Delay(options.Value.RetryDelay, stoppingToken).ConfigureAwait(false);
            await channel.BasicNackAsync(delivery.DeliveryTag, multiple: false, requeue: true, stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task FlushAsync(
        List<(SearchIndexMessageEntry Entry, JsonNode Action, JsonNode? Source)> pending,
        List<long> done, Dictionary<long, string> failed, CancellationToken cancellationToken)
    {
        if (pending.Count == 0)
        {
            return;
        }

        var lines = new List<JsonNode>();
        foreach (var (_, action, source) in pending)
        {
            lines.Add(action);
            if (source is not null)
            {
                lines.Add(source);
            }
        }

        var items = await client.BulkAsync(lines, cancellationToken).ConfigureAwait(false);
        for (var i = 0; i < pending.Count; i++)
        {
            var result = items.Count > i ? (items[i] as JsonObject)?.FirstOrDefault().Value : null;
            var status = (HttpStatusCode)(result?["status"]?.GetValue<int>() ?? 500);
            var entry = pending[i].Entry;

            // 409: a newer version is indexed already. 404: deleting a document that is not there.
            if ((int)status is >= 200 and < 300 || status == HttpStatusCode.Conflict
                || (status == HttpStatusCode.NotFound && entry.Operation == SearchOperation.Delete))
            {
                done.Add(entry.Id);
            }
            else if (status == HttpStatusCode.TooManyRequests || (int)status >= 500)
            {
                throw new OpenSearchException($"OpenSearch could not index now: {OpenSearchClient.ErrorText(result)}", status);
            }
            else
            {
                failed[entry.Id] = $"{entry.DocumentType}/{entry.DocumentId}: {OpenSearchClient.ErrorText(result)}";
            }
        }

        pending.Clear();
    }

    private Task<long> PurgeAsync(string alias, Guid tenantId, long before, CancellationToken cancellationToken) =>
        client.DeleteByQueryAsync(alias, new JsonObject
        {
            ["bool"] = new JsonObject
            {
                ["filter"] = new JsonArray(
                    new JsonObject { ["term"] = new JsonObject { ["tenantId"] = tenantId.ToString("D") } },
                    new JsonObject { ["range"] = new JsonObject { ["outboxId"] = new JsonObject { ["lt"] = before } } }),
            },
        }, cancellationToken);

    private static bool BulkLine(
        SearchIndexMessageEntry entry, Guid tenantId, string alias, out JsonNode? action, out JsonNode? source, out string? problem)
    {
        action = null;
        source = null;
        problem = null;
        if (entry.DocumentType is null || entry.DocumentId is null)
        {
            problem = "Entry without a document type or id.";
            return false;
        }

        var meta = new JsonObject
        {
            ["_index"] = alias,
            ["_id"] = SearchDocument.IndexIdOf(entry.DocumentType, entry.DocumentId),
            ["version"] = entry.Id,
            ["version_type"] = "external",
        };

        if (entry.Operation == SearchOperation.Delete)
        {
            action = new JsonObject { ["delete"] = meta };
            return true;
        }

        SearchDocument? document;
        try
        {
            document = entry.Document is null ? null : JsonSerializer.Deserialize<SearchDocument>(entry.Document, SearchJson.Options);
        }
        catch (JsonException)
        {
            document = null;
        }

        if (document is null || document.Validate().Count > 0
            || document.Type != entry.DocumentType || document.Id != entry.DocumentId)
        {
            problem = $"{entry.DocumentType}/{entry.DocumentId}: the document is not valid.";
            return false;
        }

        action = new JsonObject { ["index"] = meta };
        source = SearchSource.Build(document, tenantId, entry.Id);
        return true;
    }

    private static async Task RecordStateAsync(ZelvoDbContext db, int? schemaVersion, string? error, bool rebuilt, CancellationToken cancellationToken)
    {
        var state = await db.SearchIndexStates.SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (state is null)
        {
            state = new SearchIndexState();
            db.SearchIndexStates.Add(state);
        }

        var now = DateTimeOffset.UtcNow;
        if (schemaVersion is { } version)
        {
            state.SchemaVersion = version;
            state.LastIndexedAt = now;
        }

        if (error is not null)
        {
            state.LastError = Truncate(error);
            state.LastErrorAt = now;
        }

        if (rebuilt)
        {
            state.RebuildCompletedAt = now;
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException)
        {
            // Another consumer created the row at the same moment; its state is as good as ours.
            db.ChangeTracker.Clear();
        }
    }

    private static string Truncate(string text) =>
        text.Length <= SearchOutboxEntry.ErrorMaxLength ? text : text[..SearchOutboxEntry.ErrorMaxLength];

    [LoggerMessage(Level = LogLevel.Warning, Message = "OpenSearch refused {Count} search document(s) of tenant {TenantId}")]
    private static partial void LogRefused(ILogger logger, int count, Guid tenantId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Indexing a message of tenant {TenantId} failed; it will be retried")]
    private static partial void LogRetrying(ILogger logger, Exception exception, Guid tenantId);

    [LoggerMessage(Level = LogLevel.Error, Message = "An unreadable search index message was dead-lettered")]
    private static partial void LogUnreadable(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The search index consumer stopped; reconnecting")]
    private static partial void LogConsumerFailed(ILogger logger, Exception exception);
}
