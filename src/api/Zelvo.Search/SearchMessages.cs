using System.Text.Json.Nodes;
using RabbitMQ.Client;
using Zelvo.Data.Search;

namespace Zelvo.Search;

/// <summary>A batch of one tenant's outbox entries, as published to RabbitMQ.</summary>
/// <param name="TenantId">The tenant.</param>
/// <param name="TenantSlug">Its slug (the tenant context needs both).</param>
/// <param name="Entries">Entries in outbox order.</param>
public sealed record SearchIndexMessage(Guid TenantId, string TenantSlug, IReadOnlyList<SearchIndexMessageEntry> Entries);

/// <summary>One outbox entry in a <see cref="SearchIndexMessage"/>.</summary>
public sealed record SearchIndexMessageEntry(
    long Id, SearchOperation Operation, string? DocumentType, string? DocumentId, string? Document, long? PurgeBefore)
{
    /// <summary>Copies an outbox entry.</summary>
    public static SearchIndexMessageEntry From(SearchOutboxEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return new(entry.Id, entry.Operation, entry.DocumentType, entry.DocumentId, entry.Document, entry.PurgeBefore);
    }
}

/// <summary>
/// RabbitMQ names for the indexing pipeline. Messages go to a durable quorum queue; one that cannot
/// be read, or keeps failing past the delivery limit, goes to the dead-letter queue. Its outbox
/// entries stay in PostgreSQL and are published again later, so nothing is lost.
/// </summary>
public static class SearchTopology
{
    /// <summary>Exchange the relay publishes to.</summary>
    public const string Exchange = "zelvo.search";

    /// <summary>Queue the indexer consumes.</summary>
    public const string Queue = "zelvo.search.index";

    /// <summary>Routing key of index messages.</summary>
    public const string RoutingKey = "index";

    /// <summary>Dead-letter exchange.</summary>
    public const string DeadLetterExchange = "zelvo.search.dead";

    /// <summary>Dead-letter queue, for operators.</summary>
    public const string DeadLetterQueue = "zelvo.search.index.dead";

    /// <summary>Deliveries before a message is dead-lettered.</summary>
    public const int DeliveryLimit = 10;

    /// <summary>Declares the exchanges and queues; safe to call again.</summary>
    public static async Task DeclareAsync(IChannel channel, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(channel);
        await channel.ExchangeDeclareAsync(DeadLetterExchange, ExchangeType.Fanout, durable: true, autoDelete: false, cancellationToken: cancellationToken).ConfigureAwait(false);
        await channel.QueueDeclareAsync(DeadLetterQueue, durable: true, exclusive: false, autoDelete: false,
            arguments: new Dictionary<string, object?> { ["x-queue-type"] = "quorum" }, cancellationToken: cancellationToken).ConfigureAwait(false);
        await channel.QueueBindAsync(DeadLetterQueue, DeadLetterExchange, string.Empty, cancellationToken: cancellationToken).ConfigureAwait(false);

        await channel.ExchangeDeclareAsync(Exchange, ExchangeType.Direct, durable: true, autoDelete: false, cancellationToken: cancellationToken).ConfigureAwait(false);
        await channel.QueueDeclareAsync(Queue, durable: true, exclusive: false, autoDelete: false, arguments: new Dictionary<string, object?>
        {
            ["x-queue-type"] = "quorum",
            ["x-dead-letter-exchange"] = DeadLetterExchange,
            ["x-delivery-limit"] = DeliveryLimit,
        }, cancellationToken: cancellationToken).ConfigureAwait(false);
        await channel.QueueBindAsync(Queue, Exchange, RoutingKey, cancellationToken: cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>Builds what OpenSearch stores for a document.</summary>
internal static class SearchSource
{
    public static JsonObject Build(SearchDocument document, Guid tenantId, long outboxId)
    {
        var attributes = new JsonObject();
        foreach (var (key, value) in document.Attributes)
        {
            attributes[key] = value;
        }

        return new JsonObject
        {
            ["tenantId"] = tenantId.ToString("D"),
            ["type"] = document.Type,
            ["id"] = document.Id,
            ["title"] = Bilingual(document.Title),
            ["body"] = Bilingual(document.Body),
            ["text"] = Strings(document.Text),
            ["keywords"] = Strings(document.Keywords),
            ["readers"] = Strings(document.Readers),
            ["attributes"] = attributes,
            ["url"] = document.Url,
            ["updatedAt"] = document.UpdatedAt?.ToUniversalTime().ToString("O"),
            ["outboxId"] = outboxId,
        };
    }

    private static JsonObject Bilingual(Data.Localization.LocalizedText? text) =>
        new() { ["en"] = text?.En, ["ar"] = text?.Ar };

    private static JsonArray Strings(IEnumerable<string> values) =>
        [.. values.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => (JsonNode)v)];
}
