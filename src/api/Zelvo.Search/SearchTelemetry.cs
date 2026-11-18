using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Zelvo.Search;

/// <summary>Spans and metrics of the search pipeline (source and meter <c>Zelvo.Search</c>).</summary>
internal static class SearchTelemetry
{
    public static readonly ActivitySource Source = new("Zelvo.Search");

    private static readonly Meter Meter = new("Zelvo.Search");

    /// <summary>Outbox entries published to RabbitMQ.</summary>
    public static readonly Counter<long> Published = Meter.CreateCounter<long>("zelvo.search.outbox.published", description: "Search outbox entries published to the queue.");

    /// <summary>Entries written to OpenSearch.</summary>
    public static readonly Counter<long> Indexed = Meter.CreateCounter<long>("zelvo.search.indexed", description: "Search outbox entries written to the tenant's index.");

    /// <summary>Entries OpenSearch refused for good (shown in Settings → Search).</summary>
    public static readonly Counter<long> Refused = Meter.CreateCounter<long>("zelvo.search.refused", description: "Search outbox entries the index refused.");

    /// <summary>Messages put back for a retry (OpenSearch or PostgreSQL unavailable).</summary>
    public static readonly Counter<long> Retried = Meter.CreateCounter<long>("zelvo.search.retried", description: "Search index messages retried later.");
}
