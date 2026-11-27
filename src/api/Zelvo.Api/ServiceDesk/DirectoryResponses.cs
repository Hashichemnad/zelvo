using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Zelvo.Api.Conventions;
using Zelvo.Data.Directory;
using Zelvo.Data.Localization;

namespace Zelvo.Api.ServiceDesk;

/// <summary>Something named: a skill, a group or a rule.</summary>
internal sealed record NamedRef(Guid Id, LocalizedTextDto Name)
{
    public static NamedRef From(Guid id, LocalizedText name) => new(id, LocalizedTextDto.From(name));
}

/// <summary>A category or location with the names above it (<c>Hardware › Laptop</c>).</summary>
internal sealed record NodeRef(Guid Id, LocalizedTextDto Name, LocalizedTextDto Path);

/// <summary>A category or location.</summary>
/// <param name="Id">Id.</param>
/// <param name="ParentId">The node above; null at the top.</param>
/// <param name="Depth">Level, 1 at the top, at most 3.</param>
/// <param name="Position">Order among siblings (0 first).</param>
/// <param name="Name">Name.</param>
/// <param name="Path">Names from the top down to this node, joined with " › ".</param>
/// <param name="Active">Whether it can be chosen for new tickets (it and every node above it must be active).</param>
/// <param name="Selectable">Active and every node above is active.</param>
/// <param name="ChildCount">Nodes directly below it.</param>
internal sealed record TreeNodeResponse(
    Guid Id, Guid? ParentId, int Depth, int Position, LocalizedTextDto Name, LocalizedTextDto Path, bool Active, bool Selectable, int ChildCount);

/// <summary>Names and paths of a whole tree, for responses.</summary>
internal sealed class TreeIndex<TNode>
    where TNode : TreeNode
{
    private const string Separator = " › ";

    private readonly Dictionary<Guid, TNode> _nodes;

    public TreeIndex(IEnumerable<TNode> nodes) => _nodes = nodes.ToDictionary(n => n.Id);

    public IEnumerable<TNode> Nodes => _nodes.Values;

    public TNode? Find(Guid id) => _nodes.GetValueOrDefault(id);

    /// <summary>The node and the nodes above it, top first.</summary>
    public IReadOnlyList<TNode> Chain(TNode node)
    {
        var chain = new List<TNode> { node };
        var current = node;
        while (current.ParentId is { } parent && _nodes.TryGetValue(parent, out var above) && chain.Count < TreeNode.MaxDepth)
        {
            chain.Insert(0, above);
            current = above;
        }

        return chain;
    }

    public LocalizedText PathOf(TNode node)
    {
        var chain = Chain(node);
        string? Join(Func<LocalizedText, string?> pick) =>
            chain.All(n => pick(n.Name) is null)
                ? null
                : string.Join(Separator, chain.Select(n => pick(n.Name) ?? n.Name.En ?? n.Name.Ar));
        return new LocalizedText(Join(t => t.En), Join(t => t.Ar));
    }

    public bool Selectable(TNode node) => Chain(node).All(n => n.Active);

    /// <summary>The node and every node below it (task G1: items at a location or below).</summary>
    public HashSet<Guid> WithDescendants(Guid id)
    {
        var result = new HashSet<Guid>();
        var queue = new Queue<Guid>([id]);
        while (queue.Count > 0)
        {
            var next = queue.Dequeue();
            if (result.Add(next))
            {
                foreach (var child in _nodes.Values.Where(n => n.ParentId == next))
                {
                    queue.Enqueue(child.Id);
                }
            }
        }

        return result;
    }

    public NodeRef? RefOf(Guid? id) =>
        id is { } nodeId && Find(nodeId) is { } node ? new NodeRef(node.Id, LocalizedTextDto.From(node.Name), LocalizedTextDto.From(PathOf(node))) : null;

    public TreeNodeResponse Response(TNode node) => new(
        node.Id, node.ParentId, node.Depth, node.Position, LocalizedTextDto.From(node.Name), LocalizedTextDto.From(PathOf(node)),
        node.Active, Selectable(node), _nodes.Values.Count(n => n.ParentId == node.Id));

    /// <summary>Every node, ordered by path: parents before children, siblings by position.</summary>
    public IEnumerable<TNode> Ordered()
    {
        IEnumerable<TNode> Below(Guid? parent) => _nodes.Values.Where(n => n.ParentId == parent)
            .OrderBy(n => n.Position).ThenBy(n => n.Id)
            .SelectMany(n => new[] { n }.Concat(Below(n.Id)));
        return Below(null);
    }
}

/// <summary>Problems shared by the directory endpoints.</summary>
internal static class DirectoryProblems
{
    public static ProblemHttpResult NotFound(string what) =>
        (ProblemHttpResult)ApiProblem.Create(StatusCodes.Status404NotFound, $"{what}-not-found", $"The {what} does not exist.");

    public static ProblemHttpResult InUse(string detail) =>
        (ProblemHttpResult)ApiProblem.Create(StatusCodes.Status409Conflict, "in-use", "It is still in use.", detail);

    public static ProblemHttpResult NameTaken() =>
        (ProblemHttpResult)ApiProblem.Create(StatusCodes.Status409Conflict, "name-taken", "Another one here already has this name in English or Arabic.");

    public static ProblemHttpResult Forbidden(string title) =>
        (ProblemHttpResult)ApiProblem.Create(StatusCodes.Status403Forbidden, "forbidden", title);

    /// <summary>Whether two bilingual names share a value in either language (ignoring case).</summary>
    public static bool SameName(LocalizedText a, LocalizedText b) =>
        (a.En is not null && string.Equals(a.En, b.En, StringComparison.OrdinalIgnoreCase))
        || (a.Ar is not null && string.Equals(a.Ar, b.Ar, StringComparison.Ordinal));

    /// <summary>Trims both languages; blank ones become missing.</summary>
    public static LocalizedText Clean(LocalizedTextInput input) =>
        new(input.En?.Trim() is { Length: > 0 } en ? en : null, input.Ar?.Trim() is { Length: > 0 } ar ? ar : null);

    /// <summary>Whether each language fits <paramref name="maxLength"/>.</summary>
    public static bool Fits(LocalizedText text, int maxLength) => (text.En?.Length ?? 0) <= maxLength && (text.Ar?.Length ?? 0) <= maxLength;
}

/// <summary>Names of a tenant's categories, locations, groups and rules, loaded once per request for ticket responses.</summary>
internal sealed class DirectoryRefs
{
    private DirectoryRefs(TreeIndex<Category> categories, TreeIndex<Location> locations, Dictionary<Guid, LocalizedText> groups, Dictionary<Guid, LocalizedText> rules)
    {
        Categories = categories;
        Locations = locations;
        Groups = groups;
        Rules = rules;
    }

    public TreeIndex<Category> Categories { get; }

    public TreeIndex<Location> Locations { get; }

    public Dictionary<Guid, LocalizedText> Groups { get; }

    public Dictionary<Guid, LocalizedText> Rules { get; }

    public static async Task<DirectoryRefs> LoadAsync(Data.ZelvoDbContext db, CancellationToken cancellationToken) => new(
        new TreeIndex<Category>(await db.Categories.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false)),
        new TreeIndex<Location>(await db.Locations.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false)),
        await db.SupportGroups.AsNoTracking().ToDictionaryAsync(g => g.Id, g => g.Name, cancellationToken).ConfigureAwait(false),
        await db.AssignmentRules.AsNoTracking().ToDictionaryAsync(r => r.Id, r => r.Name, cancellationToken).ConfigureAwait(false));

    public NamedRef? Group(Guid? id) => id is { } g && Groups.TryGetValue(g, out var name) ? NamedRef.From(g, name) : null;

    public NamedRef? Rule(Guid? id) => id is { } r && Rules.TryGetValue(r, out var name) ? NamedRef.From(r, name) : null;

    /// <summary>Every id with its name (paths for categories and locations) in <paramref name="language"/>, for the history.</summary>
    public IEnumerable<KeyValuePair<Guid, string>> Names(string language)
    {
        foreach (var c in Categories.Nodes) yield return new(c.Id, Categories.PathOf(c).GetOrFallback(language) ?? c.Id.ToString());
        foreach (var l in Locations.Nodes) yield return new(l.Id, Locations.PathOf(l).GetOrFallback(language) ?? l.Id.ToString());
        foreach (var (id, name) in Groups) yield return new(id, name.GetOrFallback(language) ?? id.ToString());
        foreach (var (id, name) in Rules) yield return new(id, name.GetOrFallback(language) ?? id.ToString());
    }
}
