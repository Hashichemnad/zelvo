namespace Zelvo.Search;

/// <summary>Search settings (configuration section <c>Search</c>; OpenSearch address from <c>Services:OpenSearch</c>).</summary>
public sealed class SearchOptions
{
    /// <summary>Configuration section.</summary>
    public const string Section = "Search";

    /// <summary>OpenSearch base address.</summary>
    public Uri OpenSearch { get; set; } = new("http://localhost:9200/");

    /// <summary>Start of every index and alias name, so several installations can share a cluster.</summary>
    public string IndexPrefix { get; set; } = "zelvo";

    /// <summary>Replicas per index: 0 on a single node, 1 or more on a cluster.</summary>
    public int Replicas { get; set; }

    /// <summary>How long a request to OpenSearch may take.</summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(30);
}

/// <summary>Settings of the indexing pipeline in the worker (configuration section <c>Search:Pipeline</c>).</summary>
public sealed class SearchPipelineOptions
{
    /// <summary>Configuration section.</summary>
    public const string Section = "Search:Pipeline";

    /// <summary>How often the relay looks for new outbox entries.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Entries published but not indexed after this long are published again.</summary>
    public TimeSpan RepublishAfter { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Most entries in one message.</summary>
    public int BatchSize { get; set; } = 100;

    /// <summary>Wait before a message is retried after OpenSearch was unavailable.</summary>
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Messages a consumer works on at once.</summary>
    public ushort Prefetch { get; set; } = 4;
}
