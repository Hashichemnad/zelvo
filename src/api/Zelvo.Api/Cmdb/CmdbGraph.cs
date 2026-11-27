using Microsoft.EntityFrameworkCore;
using Zelvo.Data;
using Zelvo.Data.Cmdb;

namespace Zelvo.Api.Cmdb;

/// <summary>Kinds of node on the relationship map and in impact results.</summary>
internal enum CiNodeKind
{
    ConfigurationItem,
    User,
    Department,
    CustomerGroup,
}

/// <summary>An item reached by impact traversal.</summary>
/// <param name="Id">The affected item.</param>
/// <param name="Depth">Steps from the items the traversal started from (1: directly related).</param>
/// <param name="ViaId">The item it was reached from.</param>
/// <param name="RelationshipId">The relationship it was reached by.</param>
internal sealed record ImpactedItem(Guid Id, int Depth, Guid ViaId, Guid RelationshipId);

/// <summary>A user, department or customer group reached through a "used by" relationship (decision 6, 2026-11-26).</summary>
internal sealed record ImpactedConsumer(CiNodeKind Kind, Guid Id, int Depth, Guid ViaId, Guid RelationshipId);

/// <summary>What a fault of the start items reaches.</summary>
internal sealed record ImpactResult(IReadOnlyList<ImpactedItem> Items, IReadOnlyList<ImpactedConsumer> Consumers, bool Truncated);

/// <summary>Nodes and edges around an item, for the relationship map and its list view.</summary>
internal sealed record CiGraph(IReadOnlyList<(CiNodeKind Kind, Guid Id, int Depth)> Nodes, IReadOnlyList<CiRelationship> Edges, bool Truncated);

/// <summary>
/// Traversal of the CMDB's relationships (task G1). Impact follows each type's
/// <see cref="ImpactDirection"/> ("what depends on this item"); the map follows every relationship
/// both ways. Both go level by level, one query per level, so they stay fast with hundreds of
/// thousands of items (decision 12, 2026-11-26). Retired items are left out.
/// Changes (H5) and problems (H1) use <see cref="ImpactAsync"/>.
/// </summary>
internal static class CmdbGraph
{
    /// <summary>Deepest impact traversal.</summary>
    public const int MaxImpactDepth = 10;

    /// <summary>Most affected items one traversal returns.</summary>
    public const int MaxImpactItems = 5000;

    /// <summary>Deepest map.</summary>
    public const int MaxMapDepth = 4;

    /// <summary>Most nodes on one map.</summary>
    public const int MaxMapNodes = 200;

    /// <summary>What a fault of <paramref name="start"/> affects, up to <paramref name="depth"/> steps away.</summary>
    public static async Task<ImpactResult> ImpactAsync(ZelvoDbContext db, IReadOnlyCollection<Guid> start, int depth, CancellationToken cancellationToken)
    {
        depth = Math.Clamp(depth, 1, MaxImpactDepth);
        var types = await db.CiRelationshipTypes.AsNoTracking().ToDictionaryAsync(t => t.Id, cancellationToken).ConfigureAwait(false);
        var visited = new HashSet<Guid>(start);
        var items = new List<ImpactedItem>();
        var consumers = new Dictionary<(CiNodeKind, Guid), ImpactedConsumer>();
        var frontier = start.ToList();
        var truncated = false;
        for (var level = 1; level <= depth && frontier.Count > 0 && !truncated; level++)
        {
            var relationships = await db.CiRelationships.AsNoTracking()
                .Where(r => frontier.Contains(r.SourceId) || (r.TargetCiId != null && frontier.Contains(r.TargetCiId.Value)))
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            var current = frontier.ToHashSet();
            var candidates = new List<ImpactedItem>();
            foreach (var r in relationships)
            {
                if (!types.TryGetValue(r.TypeId, out var type) || type.Impact == ImpactDirection.None)
                {
                    continue;
                }

                if (current.Contains(r.SourceId) && type.Impact is ImpactDirection.ToTarget or ImpactDirection.Both)
                {
                    if (r.TargetCiId is { } target)
                    {
                        candidates.Add(new ImpactedItem(target, level, r.SourceId, r.Id));
                    }
                    else if (Consumer(r) is { } consumer)
                    {
                        consumers.TryAdd(consumer, new ImpactedConsumer(consumer.Kind, consumer.Id, level, r.SourceId, r.Id));
                    }
                }

                if (r.TargetCiId is { } t && current.Contains(t) && type.Impact is ImpactDirection.ToSource or ImpactDirection.Both)
                {
                    candidates.Add(new ImpactedItem(r.SourceId, level, t, r.Id));
                }
            }

            var newIds = candidates.Select(c => c.Id).Where(id => !visited.Contains(id)).Distinct().ToList();
            var live = newIds.Count == 0 ? [] : (await db.ConfigurationItems.AsNoTracking()
                .Where(i => newIds.Contains(i.Id) && i.Status != CiStatus.Retired).Select(i => i.Id)
                .ToListAsync(cancellationToken).ConfigureAwait(false)).ToHashSet();
            frontier = [];
            foreach (var candidate in candidates.Where(c => live.Contains(c.Id)).OrderBy(c => c.Id))
            {
                if (!visited.Add(candidate.Id))
                {
                    continue;
                }

                if (items.Count >= MaxImpactItems)
                {
                    truncated = true;
                    break;
                }

                items.Add(candidate);
                frontier.Add(candidate.Id);
            }
        }

        return new ImpactResult(items, [.. consumers.Values.OrderBy(c => c.Depth)], truncated);
    }

    /// <summary>
    /// Items and consumers up to <paramref name="depth"/> steps from <paramref name="root"/> in either
    /// direction, with the relationships between them, for the map. Only relationships of
    /// <paramref name="typeIds"/> (all when empty) and items of <paramref name="classIds"/> (all
    /// when empty; the root always) are followed.
    /// </summary>
    public static async Task<CiGraph> MapAsync(
        ZelvoDbContext db, Guid root, int depth, IReadOnlySet<Guid> typeIds, IReadOnlySet<Guid> classIds, bool includeConsumers, bool includeRetired,
        CancellationToken cancellationToken)
    {
        depth = Math.Clamp(depth, 1, MaxMapDepth);
        var nodes = new List<(CiNodeKind Kind, Guid Id, int Depth)> { (CiNodeKind.ConfigurationItem, root, 0) };
        var seen = new HashSet<(CiNodeKind, Guid)> { (CiNodeKind.ConfigurationItem, root) };
        var edges = new Dictionary<Guid, CiRelationship>();
        var frontier = new List<Guid> { root };
        var truncated = false;
        for (var level = 1; level <= depth && frontier.Count > 0 && !truncated; level++)
        {
            var relationships = await db.CiRelationships.AsNoTracking()
                .Where(r => frontier.Contains(r.SourceId) || (r.TargetCiId != null && frontier.Contains(r.TargetCiId.Value)))
                .Where(r => typeIds.Count == 0 || typeIds.Contains(r.TypeId))
                .OrderBy(r => r.Id)
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            var otherIds = relationships.SelectMany(r => new[] { r.SourceId, r.TargetCiId ?? Guid.Empty })
                .Where(id => id != Guid.Empty && !seen.Contains((CiNodeKind.ConfigurationItem, id))).Distinct().ToList();
            var allowed = otherIds.Count == 0 ? [] : (await db.ConfigurationItems.AsNoTracking()
                .Where(i => otherIds.Contains(i.Id))
                .Where(i => includeRetired || i.Status != CiStatus.Retired)
                .Where(i => classIds.Count == 0 || classIds.Contains(i.ClassId))
                .Select(i => i.Id).ToListAsync(cancellationToken).ConfigureAwait(false)).ToHashSet();

            var next = new List<Guid>();
            foreach (var r in relationships)
            {
                foreach (var (kind, id) in Ends(r))
                {
                    if (seen.Contains((kind, id)))
                    {
                        continue;
                    }

                    var ok = kind == CiNodeKind.ConfigurationItem ? allowed.Contains(id) : includeConsumers;
                    if (!ok)
                    {
                        continue;
                    }

                    if (nodes.Count >= MaxMapNodes)
                    {
                        truncated = true;
                        break;
                    }

                    seen.Add((kind, id));
                    nodes.Add((kind, id, level));
                    if (kind == CiNodeKind.ConfigurationItem)
                    {
                        next.Add(id);
                    }
                }
            }

            frontier = next;
        }

        // Every relationship between two nodes on the map, also those found at the last level.
        var itemIds = nodes.Where(n => n.Kind == CiNodeKind.ConfigurationItem).Select(n => n.Id).ToList();
        var between = await db.CiRelationships.AsNoTracking()
            .Where(r => itemIds.Contains(r.SourceId))
            .Where(r => typeIds.Count == 0 || typeIds.Contains(r.TypeId))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var r in between)
        {
            if (Ends(r).All(end => seen.Contains(end)))
            {
                edges.TryAdd(r.Id, r);
            }
        }

        return new CiGraph(nodes, [.. edges.Values.OrderBy(e => e.Id)], truncated);
    }

    private static (CiNodeKind Kind, Guid Id)? Consumer(CiRelationship r) =>
        r.TargetUserId is { } u ? (CiNodeKind.User, u)
        : r.TargetDepartmentId is { } d ? (CiNodeKind.Department, d)
        : r.TargetCustomerGroupId is { } g ? (CiNodeKind.CustomerGroup, g)
        : null;

    private static IEnumerable<(CiNodeKind Kind, Guid Id)> Ends(CiRelationship r)
    {
        yield return (CiNodeKind.ConfigurationItem, r.SourceId);
        if (r.TargetCiId is { } target)
        {
            yield return (CiNodeKind.ConfigurationItem, target);
        }
        else if (Consumer(r) is { } consumer)
        {
            yield return consumer;
        }
    }
}
