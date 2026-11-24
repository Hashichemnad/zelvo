using System.ComponentModel.DataAnnotations;
using System.Linq.Expressions;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Zelvo.Api.Conventions;
using Zelvo.Api.Identity;
using Zelvo.Api.Tenancy;
using Zelvo.Data;
using Zelvo.Data.Directory;
using Zelvo.Data.Tickets;

namespace Zelvo.Api.ServiceDesk;

/// <summary>
/// Categories and locations (task B2; product owner decision 2026-11-23): trees of up to three
/// levels with bilingual names, shared by all record types. Every signed-in user sees the active
/// nodes (requesters choose them when reporting); administrators change them.
/// </summary>
internal static class TreeEndpoints
{
    /// <summary>What differs between the two trees.</summary>
    internal sealed record TreeKind<TNode>(
        string Path, string Singular, Func<ZelvoDbContext, DbSet<TNode>> Set,
        Func<Guid, Expression<Func<Ticket, bool>>> TicketUses, Func<Guid, Expression<Func<AssignmentRule, bool>>> RuleUses)
        where TNode : TreeNode;

    internal static readonly TreeKind<Category> Categories = new(
        "categories", "category", db => db.Categories, id => t => t.CategoryId == id, id => r => r.CategoryId == id);

    internal static readonly TreeKind<Location> Locations = new(
        "locations", "location", db => db.Locations, id => t => t.LocationId == id, id => r => r.LocationId == id);

    public static RouteGroupBuilder MapTreeEndpoints(this RouteGroupBuilder group)
    {
        Map(group, Categories, (name, parent) => new Category { Name = name, ParentId = parent?.Id, Depth = (parent?.Depth ?? 0) + 1 });
        Map(group, Locations, (name, parent) => new Location { Name = name, ParentId = parent?.Id, Depth = (parent?.Depth ?? 0) + 1 });
        return group;
    }

    private static void Map<TNode>(RouteGroupBuilder group, TreeKind<TNode> kind, Func<Data.Localization.LocalizedText, TNode?, TNode> create)
        where TNode : TreeNode
    {
        var nodes = group.MapGroup("/" + kind.Path).WithTags("Directory").RequireTenant();
        var title = kind.Singular == "category" ? "Categories" : "Locations";

        nodes.MapGet("/", (bool? includeInactive, ZelvoDbContext db, CurrentUser user, CancellationToken ct) => ListAsync(kind, includeInactive, db, user, ct))
            .WithName("List" + title)
            .WithSummary($"{title} as a flat list ordered by path (parents before children). Every signed-in user sees the ones that can be chosen; includeInactive=true needs directory.read.")
            .RequireAuthorization()
            .Produces<IReadOnlyList<TreeNodeResponse>>();

        nodes.MapPost("/", (CreateTreeNodeRequest request, ZelvoDbContext db, CancellationToken ct) => CreateAsync(kind, create, request, db, ct))
            .WithName("Create" + kind.Singular[..1].ToUpperInvariant() + kind.Singular[1..])
            .WithSummary($"Add a {kind.Singular} at the top or below another one (at most three levels). Needs directory.manage; audited.")
            .RequirePermission(Permissions.DirectoryManage)
            .Produces<TreeNodeResponse>(StatusCodes.Status201Created)
            .ProducesValidationProblem();

        nodes.MapPatch("/{id:guid}", (Guid id, UpdateTreeNodeRequest request, ZelvoDbContext db, CancellationToken ct) => UpdateAsync(kind, id, request, db, ct))
            .WithName("Update" + kind.Singular[..1].ToUpperInvariant() + kind.Singular[1..])
            .WithSummary($"Rename a {kind.Singular}, make it active or inactive (it stays on existing tickets), or move it among its siblings. Needs directory.manage; audited.")
            .RequirePermission(Permissions.DirectoryManage)
            .Produces<TreeNodeResponse>()
            .ProducesValidationProblem();

        nodes.MapDelete("/{id:guid}", (Guid id, ZelvoDbContext db, CancellationToken ct) => DeleteAsync(kind, id, db, ct))
            .WithName("Delete" + kind.Singular[..1].ToUpperInvariant() + kind.Singular[1..])
            .WithSummary($"Delete a {kind.Singular} that nothing uses (no nodes below it, no tickets, no assignment rules); otherwise make it inactive. Needs directory.manage; audited.")
            .RequirePermission(Permissions.DirectoryManage)
            .Produces(StatusCodes.Status204NoContent);
    }

    internal static async Task<Results<Ok<List<TreeNodeResponse>>, ProblemHttpResult>> ListAsync<TNode>(
        TreeKind<TNode> kind, bool? includeInactive, ZelvoDbContext db, CurrentUser user, CancellationToken cancellationToken)
        where TNode : TreeNode
    {
        if (includeInactive == true && !user.Has(Permissions.DirectoryRead))
        {
            return DirectoryProblems.Forbidden("Inactive entries need directory.read.");
        }

        var index = new TreeIndex<TNode>(await kind.Set(db).AsNoTracking().ToListAsync(cancellationToken));
        return TypedResults.Ok(index.Ordered().Where(n => includeInactive == true || index.Selectable(n)).Select(index.Response).ToList());
    }

    internal static async Task<Results<Created<TreeNodeResponse>, ValidationProblem, ProblemHttpResult>> CreateAsync<TNode>(
        TreeKind<TNode> kind, Func<Data.Localization.LocalizedText, TNode?, TNode> create, CreateTreeNodeRequest request,
        ZelvoDbContext db, CancellationToken cancellationToken)
        where TNode : TreeNode
    {
        var name = DirectoryProblems.Clean(request.Name!);
        if (!DirectoryProblems.Fits(name, TreeNode.NameMaxLength))
        {
            return UserEndpoints.Validation("name", $"Use at most {TreeNode.NameMaxLength} characters in each language.");
        }

        var set = kind.Set(db);
        TNode? parent = null;
        if (request.ParentId is { } parentId)
        {
            parent = await set.SingleOrDefaultAsync(n => n.Id == parentId, cancellationToken);
            if (parent is null)
            {
                return UserEndpoints.Validation("parentId", $"The parent {kind.Singular} does not exist.");
            }

            if (parent.Depth >= TreeNode.MaxDepth)
            {
                return (ProblemHttpResult)ApiProblem.Create(StatusCodes.Status409Conflict, "too-deep",
                    $"A {kind.Singular} tree has at most {TreeNode.MaxDepth} levels.");
            }
        }

        var siblings = await set.Where(n => n.ParentId == request.ParentId).ToListAsync(cancellationToken);
        if (siblings.Any(s => DirectoryProblems.SameName(s.Name, name)))
        {
            return DirectoryProblems.NameTaken();
        }

        var node = create(name, parent);
        node.Position = siblings.Count == 0 ? 0 : siblings.Max(s => s.Position) + 1;
        node.Active = request.Active ?? true;
        set.Add(node);
        await db.SaveChangesAsync(cancellationToken);
        var index = new TreeIndex<TNode>(await set.AsNoTracking().ToListAsync(cancellationToken));
        return TypedResults.Created($"/api/v1/{kind.Path}/{node.Id}", index.Response(index.Find(node.Id)!));
    }

    internal static async Task<Results<Ok<TreeNodeResponse>, ValidationProblem, ProblemHttpResult>> UpdateAsync<TNode>(
        TreeKind<TNode> kind, Guid id, UpdateTreeNodeRequest request, ZelvoDbContext db, CancellationToken cancellationToken)
        where TNode : TreeNode
    {
        var set = kind.Set(db);
        var node = await set.SingleOrDefaultAsync(n => n.Id == id, cancellationToken);
        if (node is null)
        {
            return DirectoryProblems.NotFound(kind.Singular);
        }

        var siblings = await set.Where(n => n.ParentId == node.ParentId && n.Id != id).OrderBy(n => n.Position).ThenBy(n => n.Id).ToListAsync(cancellationToken);
        if (request.Name is not null)
        {
            var name = DirectoryProblems.Clean(request.Name);
            if (!DirectoryProblems.Fits(name, TreeNode.NameMaxLength))
            {
                return UserEndpoints.Validation("name", $"Use at most {TreeNode.NameMaxLength} characters in each language.");
            }

            if (siblings.Any(s => DirectoryProblems.SameName(s.Name, name)))
            {
                return DirectoryProblems.NameTaken();
            }

            node.Name = name;
        }

        if (request.Active is { } active)
        {
            node.Active = active;
        }

        if (request.Position is { } position)
        {
            // Renumber the siblings 0..n with the node at the requested place.
            siblings.Insert(Math.Clamp(position, 0, siblings.Count), node);
            for (var i = 0; i < siblings.Count; i++)
            {
                if (siblings[i].Position != i)
                {
                    siblings[i].Position = i;
                    siblings[i].UpdatedAt = DateTimeOffset.UtcNow;
                }
            }
        }

        node.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        var index = new TreeIndex<TNode>(await set.AsNoTracking().ToListAsync(cancellationToken));
        return TypedResults.Ok(index.Response(index.Find(node.Id)!));
    }

    internal static async Task<Results<NoContent, ProblemHttpResult>> DeleteAsync<TNode>(
        TreeKind<TNode> kind, Guid id, ZelvoDbContext db, CancellationToken cancellationToken)
        where TNode : TreeNode
    {
        var set = kind.Set(db);
        var node = await set.SingleOrDefaultAsync(n => n.Id == id, cancellationToken);
        if (node is null)
        {
            return DirectoryProblems.NotFound(kind.Singular);
        }

        if (await set.AnyAsync(n => n.ParentId == id, cancellationToken))
        {
            return DirectoryProblems.InUse($"Delete or move the entries below this {kind.Singular} first, or make it inactive.");
        }

        if (await db.Tickets.AnyAsync(kind.TicketUses(id), cancellationToken) || await db.AssignmentRules.AnyAsync(kind.RuleUses(id), cancellationToken))
        {
            return DirectoryProblems.InUse($"Tickets or assignment rules use this {kind.Singular}; make it inactive instead.");
        }

        set.Remove(node);
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.NoContent();
    }

    /// <summary>Whether <paramref name="id"/> is a node that new tickets can use (it and the nodes above it active).</summary>
    internal static async Task<bool> IsSelectableAsync<TNode>(DbSet<TNode> set, Guid id, CancellationToken cancellationToken)
        where TNode : TreeNode
    {
        Guid? current = id;
        var levels = 0;
        while (current is { } nodeId && levels++ < TreeNode.MaxDepth)
        {
            var node = await set.AsNoTracking().Where(n => n.Id == nodeId).Select(n => new { n.Active, n.ParentId }).SingleOrDefaultAsync(cancellationToken);
            if (node is null || !node.Active)
            {
                return false;
            }

            current = node.ParentId;
        }

        return true;
    }
}

/// <summary>A new category or location.</summary>
public sealed class CreateTreeNodeRequest
{
    /// <summary>Name in English and Arabic (at least one).</summary>
    [Required]
    public LocalizedTextInput? Name { get; init; }

    /// <summary>The node it goes below; leave out for the top level.</summary>
    public Guid? ParentId { get; init; }

    /// <summary>Whether it can be chosen at once (default true).</summary>
    public bool? Active { get; init; }
}

/// <summary>Changes to a category or location. Leave a field out to keep it.</summary>
public sealed class UpdateTreeNodeRequest
{
    /// <summary>New name.</summary>
    public LocalizedTextInput? Name { get; init; }

    /// <summary>Active or inactive.</summary>
    public bool? Active { get; init; }

    /// <summary>New place among its siblings (0 first).</summary>
    [Range(0, 10_000)]
    public int? Position { get; init; }
}
