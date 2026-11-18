using Zelvo.Data.Tenancy;

namespace Zelvo.Data.Search;

/// <summary>Indexing state of one tenant's search index, kept by the worker and shown in Settings → Search.</summary>
public sealed class SearchIndexState : ITenantOwned
{
    /// <summary>Maximum length of <see cref="LastError"/>.</summary>
    public const int ErrorMaxLength = SearchOutboxEntry.ErrorMaxLength;

    /// <inheritdoc />
    public Guid TenantId { get; set; }

    /// <summary>Mapping version of the tenant's live index; null until the index exists.</summary>
    public int? SchemaVersion { get; set; }

    /// <summary>When changes were last written to the index.</summary>
    public DateTimeOffset? LastIndexedAt { get; set; }

    /// <summary>When indexing last failed.</summary>
    public DateTimeOffset? LastErrorAt { get; set; }

    /// <summary>The last indexing error. Never holds document content.</summary>
    public string? LastError { get; set; }

    /// <summary>When an administrator last asked for a rebuild.</summary>
    public DateTimeOffset? RebuildRequestedAt { get; set; }

    /// <summary>When the last rebuild finished (its purge step was indexed).</summary>
    public DateTimeOffset? RebuildCompletedAt { get; set; }
}
