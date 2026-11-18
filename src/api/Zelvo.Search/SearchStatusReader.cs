using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Zelvo.Data;
using Zelvo.Data.Tenancy;

namespace Zelvo.Search;

/// <summary>The current tenant's search index, as Settings → Search shows it.</summary>
/// <param name="Available">OpenSearch answered.</param>
/// <param name="Documents">Documents in the index; null when OpenSearch did not answer.</param>
/// <param name="PendingUpdates">Changes waiting to be indexed.</param>
/// <param name="FailedUpdates">Changes OpenSearch refused; a rebuild sends their records again.</param>
/// <param name="LastError">The last indexing error, if any.</param>
/// <param name="LastErrorAt">When it happened.</param>
/// <param name="LastIndexedAt">When changes were last indexed.</param>
/// <param name="SchemaVersion">Version of the live index; null until it exists.</param>
/// <param name="CurrentSchemaVersion">Version this release uses.</param>
/// <param name="RebuildRequestedAt">When a rebuild was last asked for.</param>
/// <param name="RebuildCompletedAt">When the last rebuild finished.</param>
public sealed record SearchStatus(
    bool Available,
    long? Documents,
    int PendingUpdates,
    int FailedUpdates,
    string? LastError,
    DateTimeOffset? LastErrorAt,
    DateTimeOffset? LastIndexedAt,
    int? SchemaVersion,
    int CurrentSchemaVersion,
    DateTimeOffset? RebuildRequestedAt,
    DateTimeOffset? RebuildCompletedAt)
{
    /// <summary>A rebuild was asked for and has not finished yet.</summary>
    public bool RebuildInProgress => RebuildRequestedAt is { } requested && (RebuildCompletedAt is not { } completed || completed < requested);
}

/// <summary>Reads <see cref="SearchStatus"/> for the current tenant.</summary>
public sealed class SearchStatusReader(ZelvoDbContext db, OpenSearchClient client, IOptions<SearchOptions> options, ITenantContext tenantContext)
{
    /// <summary>The status; OpenSearch being down is reported, not thrown.</summary>
    public async Task<SearchStatus> ReadAsync(CancellationToken cancellationToken)
    {
        var tenantId = tenantContext.TenantId ?? throw new InvalidOperationException("Search status needs a tenant.");
        var pending = await db.SearchOutbox.CountAsync(e => e.FailedAt == null, cancellationToken).ConfigureAwait(false);
        var failed = await db.SearchOutbox.CountAsync(e => e.FailedAt != null, cancellationToken).ConfigureAwait(false);
        var state = await db.SearchIndexStates.AsNoTracking().SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);

        long? documents;
        bool available;
        try
        {
            var alias = SearchIndexDefinition.AliasFor(options.Value.IndexPrefix, tenantId);
            documents = await client.CountAsync(alias,
                new JsonObject { ["term"] = new JsonObject { ["tenantId"] = tenantId.ToString("D") } }, cancellationToken).ConfigureAwait(false) ?? 0;
            available = true;
        }
        catch (OpenSearchException)
        {
            documents = null;
            available = false;
        }

        return new SearchStatus(
            available, documents, pending, failed,
            state?.LastError, state?.LastErrorAt, state?.LastIndexedAt,
            state?.SchemaVersion, SearchIndexDefinition.SchemaVersion,
            state?.RebuildRequestedAt, state?.RebuildCompletedAt);
    }
}
