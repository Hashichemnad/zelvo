using Microsoft.EntityFrameworkCore;
using Zelvo.Data;
using Zelvo.Data.Search;

namespace Zelvo.Search;

/// <summary>
/// Rebuilds the current tenant's index from the database: every <see cref="ISearchDocumentSource"/>
/// sends its documents again through the outbox, then a purge entry removes documents last written
/// before the rebuild started (records deleted while the index missed it). Searches keep working
/// meanwhile. Entries that had failed are dropped, because their records are sent again.
/// </summary>
public sealed class SearchRebuild(ZelvoDbContext db, ISearchIndexer indexer, IEnumerable<ISearchDocumentSource> sources)
{
    private const int SaveEvery = 500;

    /// <summary>Queues the rebuild; returns how many documents were queued.</summary>
    public async Task<int> RequestAsync(CancellationToken cancellationToken)
    {
        // Outbox ids are the document versions; every entry from now on gets a larger one.
        var startsAt = await db.Database
            .SqlQuery<long>($"SELECT nextval(pg_get_serial_sequence('search_outbox', 'id')) AS \"Value\"")
            .SingleAsync(cancellationToken).ConfigureAwait(false);

        await db.SearchOutbox.Where(e => e.FailedAt != null).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);

        var queued = 0;
        foreach (var source in sources)
        {
            await foreach (var document in source.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                indexer.Upsert(document);
                if (++queued % SaveEvery == 0)
                {
                    await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                    db.ChangeTracker.Clear();
                }
            }
        }

        db.SearchOutbox.Add(new SearchOutboxEntry { Operation = SearchOperation.Purge, PurgeBefore = startsAt });
        var state = await db.SearchIndexStates.SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (state is null)
        {
            state = new SearchIndexState();
            db.SearchIndexStates.Add(state);
        }

        state.RebuildRequestedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return queued;
    }
}
