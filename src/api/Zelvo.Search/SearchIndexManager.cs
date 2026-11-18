using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Zelvo.Search;

/// <summary>
/// Makes sure each tenant has an index of the current <see cref="SearchIndexDefinition.SchemaVersion"/>
/// behind its alias. A tenant whose alias still points at an older version gets a new index,
/// filled from the old one (stored documents keep their versions), then the alias moves to it in
/// one step, documents written meanwhile are copied once more, and the old index is deleted.
/// Reads and writes always go through the alias, so searches keep working during the upgrade.
/// </summary>
public sealed partial class SearchIndexManager(OpenSearchClient client, IOptions<SearchOptions> options, ILogger<SearchIndexManager> logger)
{
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _locks = new();
    private readonly ConcurrentDictionary<Guid, bool> _ready = new();

    /// <summary>Alias of a tenant's index.</summary>
    public string AliasFor(Guid tenantId) => SearchIndexDefinition.AliasFor(options.Value.IndexPrefix, tenantId);

    /// <summary>Ensures the tenant's index exists at the current schema version; returns that version.</summary>
    public async Task<int> EnsureAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        if (_ready.ContainsKey(tenantId))
        {
            return SearchIndexDefinition.SchemaVersion;
        }

        var gate = _locks.GetOrAdd(tenantId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_ready.ContainsKey(tenantId))
            {
                await EnsureCoreAsync(tenantId, cancellationToken).ConfigureAwait(false);
                _ready[tenantId] = true;
            }
        }
        finally
        {
            gate.Release();
        }

        return SearchIndexDefinition.SchemaVersion;
    }

    /// <summary>Forgets what is known about a tenant's index (after it was deleted, in tests).</summary>
    public void Forget(Guid tenantId) => _ready.TryRemove(tenantId, out _);

    private async Task EnsureCoreAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var prefix = options.Value.IndexPrefix;
        var alias = SearchIndexDefinition.AliasFor(prefix, tenantId);
        var current = SearchIndexDefinition.IndexFor(prefix, tenantId, SearchIndexDefinition.SchemaVersion);
        var targets = await client.GetAliasTargetsAsync(alias, cancellationToken).ConfigureAwait(false);

        if (targets.Count == 1 && targets[0] == current)
        {
            return;
        }

        await client.CreateIndexAsync(current, SearchIndexDefinition.CreateIndexBody(options.Value.Replicas), cancellationToken).ConfigureAwait(false);
        var older = targets.Where(t => t != current).ToList();
        foreach (var old in older)
        {
            var from = SearchIndexDefinition.VersionOf(old);
            LogUpgrading(logger, tenantId, from, SearchIndexDefinition.SchemaVersion);
            await CopyAsync(old, current, cancellationToken).ConfigureAwait(false);
        }

        var actions = new JsonArray();
        foreach (var old in older)
        {
            actions.Add(new JsonObject { ["remove"] = new JsonObject { ["index"] = old, ["alias"] = alias } });
        }

        actions.Add(new JsonObject { ["add"] = new JsonObject { ["index"] = current, ["alias"] = alias, ["is_write_index"] = true } });
        await client.UpdateAliasesAsync(actions, cancellationToken).ConfigureAwait(false);

        foreach (var old in older)
        {
            // Writes that reached the old index between the copy and the alias switch.
            await CopyAsync(old, current, cancellationToken).ConfigureAwait(false);
            await client.DeleteIndexAsync(old, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task CopyAsync(string from, string to, CancellationToken cancellationToken)
    {
        // A copy reads only refreshed documents; refresh first so recent writes are included.
        await client.RefreshAsync(from, cancellationToken).ConfigureAwait(false);
        await client.ReindexAsync(from, to, cancellationToken).ConfigureAwait(false);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Upgrading search index of tenant {TenantId} from schema version {From} to {To}")]
    private static partial void LogUpgrading(ILogger logger, Guid tenantId, int? from, int to);
}
