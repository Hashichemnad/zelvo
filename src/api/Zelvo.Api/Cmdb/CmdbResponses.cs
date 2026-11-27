using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Zelvo.Api.Conventions;
using Zelvo.Api.Forms;
using Zelvo.Api.ServiceDesk;
using Zelvo.Data;
using Zelvo.Data.Cmdb;
using Zelvo.Data.Directory;
using Zelvo.Data.Localization;
using Zelvo.Data.Tickets;
using Zelvo.Tickets.Forms;

namespace Zelvo.Api.Cmdb;

/// <summary>A class as items show it.</summary>
internal sealed record CiClassRef(Guid Id, string Key, CiFamily Family, LocalizedTextDto Name, LocalizedTextDto Path)
{
    public static CiClassRef From(CiClassIndex classes, Guid id)
    {
        var c = classes.Find(id);
        return c is null
            ? new(id, string.Empty, CiFamily.Hardware, LocalizedTextDto.From(new(null, null)), LocalizedTextDto.From(new(null, null)))
            : new(c.Id, c.Key, c.Family, LocalizedTextDto.From(c.Name), LocalizedTextDto.From(classes.Path(id)));
    }
}

/// <summary>An item in a list.</summary>
internal sealed record CiSummaryResponse(
    Guid Id, string Number, string Name, CiClassRef Class, CiStatus Status, NamedRef? OwnerGroup, NodeRef? Location, DateTimeOffset UpdatedAt)
{
    public static CiSummaryResponse From(ConfigurationItem i, CiClassIndex classes, DirectoryRefs refs) => new(
        i.Id, i.Number, i.Name, CiClassRef.From(classes, i.ClassId), i.Status, refs.Group(i.OwnerGroupId), refs.Locations.RefOf(i.LocationId), i.UpdatedAt);
}

/// <summary>An attribute of an item: its definition, the class it comes from and its value.</summary>
/// <param name="Display">For users and locations: the name or path the value refers to.</param>
internal sealed record CiAttributeResponse(JsonNode? Field, Guid ClassId, JsonNode? Value, LocalizedTextDto? Display);

/// <summary>One item with everything about it.</summary>
/// <param name="Stale">Values of attributes the class no longer has; dropped at the next change.</param>
internal sealed record CiResponse(
    Guid Id, string Number, string Name, LocalizedTextDto Description, CiClassRef Class, CiStatus Status, NamedRef? OwnerGroup, NodeRef? Location,
    IReadOnlyList<CiAttributeResponse> Attributes, JsonObject Stale, int RelationshipCount, int TicketCount, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, string Etag)
{
    public static async Task<CiResponse> LoadAsync(ZelvoDbContext db, ConfigurationItem item, CancellationToken cancellationToken)
    {
        var classes = await CiClassIndex.LoadAsync(db, cancellationToken).ConfigureAwait(false);
        var refs = await DirectoryRefs.LoadAsync(db, cancellationToken).ConfigureAwait(false);
        var values = JsonNode.Parse(item.Attributes)?.AsObject() ?? [];
        var fields = classes.Attributes(item.ClassId);
        var userIds = fields.Where(f => f.Field.Type == FieldType.User).Select(f => values[f.Field.Key]?.ToString()).Select(v => Guid.TryParse(v, out var g) ? g : Guid.Empty).Where(g => g != Guid.Empty).ToList();
        var users = await FormEndpoints.PeopleNamesAsync(db, userIds, cancellationToken).ConfigureAwait(false);
        var attributes = fields.Select(f =>
        {
            var value = values[f.Field.Key]?.DeepClone();
            LocalizedTextDto? display = null;
            if (value is JsonValue v && Guid.TryParse(v.ToString(), out var refId))
            {
                display = f.Field.Type switch
                {
                    FieldType.User when users.TryGetValue(refId, out var name) => LocalizedTextDto.From(new(name, name)),
                    FieldType.Location when refs.Locations.RefOf(refId) is { } location => location.Path,
                    _ => null,
                };
            }

            return new CiAttributeResponse(System.Text.Json.JsonSerializer.SerializeToNode(f.Field, FormJson.Options), f.ClassId, value, display);
        }).ToList();
        var keys = fields.Select(f => f.Field.Key).ToHashSet(StringComparer.Ordinal);
        var stale = new JsonObject();
        foreach (var (key, value) in values.Where(p => !keys.Contains(p.Key)))
        {
            stale[key] = value?.DeepClone();
        }

        var relationships = await db.CiRelationships.CountAsync(r => r.SourceId == item.Id || r.TargetCiId == item.Id, cancellationToken).ConfigureAwait(false);
        var tickets = await db.TicketConfigurationItems.CountAsync(l => l.ConfigurationItemId == item.Id, cancellationToken).ConfigureAwait(false);
        return new CiResponse(item.Id, item.Number, item.Name, LocalizedTextDto.From(item.Description), CiClassRef.From(classes, item.ClassId), item.Status,
            refs.Group(item.OwnerGroupId), refs.Locations.RefOf(item.LocationId), attributes, stale, relationships, tickets, item.CreatedAt, item.UpdatedAt,
            $"\"{item.Version}\"");
    }
}

/// <summary>An item to pick: number, name and class.</summary>
internal sealed record CiPickResponse(Guid Id, string Number, string Name, CiStatus Status, CiClassRef Class)
{
    public static CiPickResponse From(ConfigurationItem i, CiClassIndex classes) => new(i.Id, i.Number, i.Name, i.Status, CiClassRef.From(classes, i.ClassId));
}

/// <summary>The end of a relationship or a node of the map: an item, a user, a department or a customer group.</summary>
/// <param name="Kind">ConfigurationItem, User, Department or CustomerGroup.</param>
/// <param name="Number">Items only.</param>
/// <param name="Name">The item's or user's name (one language); departments and customer groups use <see cref="LocalizedName"/>.</param>
/// <param name="LocalizedName">Departments and customer groups.</param>
/// <param name="Class">Items only.</param>
/// <param name="Status">Items only.</param>
internal sealed record CiNodeResponse(
    CiNodeKind Kind, Guid Id, string? Number, string? Name, LocalizedTextDto? LocalizedName, CiClassRef? Class, CiStatus? Status);

/// <summary>Names of what relationships point at, loaded once.</summary>
internal sealed class CiNames
{
    public required Dictionary<Guid, ConfigurationItem> Items { get; init; }

    public required Dictionary<Guid, string?> Users { get; init; }

    public required Dictionary<Guid, LocalizedText> Departments { get; init; }

    public required Dictionary<Guid, LocalizedText> CustomerGroups { get; init; }

    public required Dictionary<Guid, CiRelationshipType> Types { get; init; }

    public required CiClassIndex Classes { get; init; }

    public static Task<CiNames> LoadAsync(ZelvoDbContext db, IReadOnlyCollection<CiRelationship> relationships, CancellationToken cancellationToken) =>
        LoadAsync(db,
            relationships.SelectMany(r => new[] { r.SourceId, r.TargetCiId ?? Guid.Empty }).Where(g => g != Guid.Empty),
            relationships.Where(r => r.TargetUserId is not null).Select(r => r.TargetUserId!.Value),
            relationships.Where(r => r.TargetDepartmentId is not null).Select(r => r.TargetDepartmentId!.Value),
            relationships.Where(r => r.TargetCustomerGroupId is not null).Select(r => r.TargetCustomerGroupId!.Value),
            cancellationToken);

    public static async Task<CiNames> LoadAsync(
        ZelvoDbContext db, IEnumerable<Guid> items, IEnumerable<Guid> users, IEnumerable<Guid> departments, IEnumerable<Guid> groups, CancellationToken cancellationToken)
    {
        var itemIds = items.Distinct().ToList();
        var userIds = users.Distinct().ToList();
        var departmentIds = departments.Distinct().ToList();
        var groupIds = groups.Distinct().ToList();
        return new CiNames
        {
            Items = itemIds.Count == 0 ? [] : await db.ConfigurationItems.AsNoTracking().Where(i => itemIds.Contains(i.Id)).ToDictionaryAsync(i => i.Id, cancellationToken).ConfigureAwait(false),
            Users = await FormEndpoints.PeopleNamesAsync(db, userIds, cancellationToken).ConfigureAwait(false),
            Departments = departmentIds.Count == 0 ? [] : await db.Departments.AsNoTracking().Where(d => departmentIds.Contains(d.Id)).ToDictionaryAsync(d => d.Id, d => d.Name, cancellationToken).ConfigureAwait(false),
            CustomerGroups = groupIds.Count == 0 ? [] : await db.CustomerGroups.AsNoTracking().Where(g => groupIds.Contains(g.Id)).ToDictionaryAsync(g => g.Id, g => g.Name, cancellationToken).ConfigureAwait(false),
            Types = await db.CiRelationshipTypes.AsNoTracking().ToDictionaryAsync(t => t.Id, cancellationToken).ConfigureAwait(false),
            Classes = await CiClassIndex.LoadAsync(db, cancellationToken).ConfigureAwait(false),
        };
    }

    public CiNodeResponse Node(CiNodeKind kind, Guid id) => kind switch
    {
        CiNodeKind.ConfigurationItem when Items.TryGetValue(id, out var i) => new(kind, id, i.Number, i.Name, null, CiClassRef.From(Classes, i.ClassId), i.Status),
        CiNodeKind.User => new(kind, id, null, Users.GetValueOrDefault(id), null, null, null),
        CiNodeKind.Department when Departments.TryGetValue(id, out var d) => new(kind, id, null, null, LocalizedTextDto.From(d), null, null),
        CiNodeKind.CustomerGroup when CustomerGroups.TryGetValue(id, out var g) => new(kind, id, null, null, LocalizedTextDto.From(g), null, null),
        _ => new(kind, id, null, null, null, null, null),
    };

    public CiNodeResponse Target(CiRelationship r) =>
        r.TargetCiId is { } item ? Node(CiNodeKind.ConfigurationItem, item)
        : r.TargetUserId is { } user ? Node(CiNodeKind.User, user)
        : r.TargetDepartmentId is { } department ? Node(CiNodeKind.Department, department)
        : Node(CiNodeKind.CustomerGroup, r.TargetCustomerGroupId!.Value);
}

/// <summary>A relationship type as relationships show it.</summary>
internal sealed record CiRelationshipTypeRef(Guid Id, string Key, LocalizedTextDto OutgoingName, LocalizedTextDto IncomingName, ImpactDirection Impact)
{
    public static CiRelationshipTypeRef From(CiRelationshipType t) =>
        new(t.Id, t.Key, LocalizedTextDto.From(t.OutgoingName), LocalizedTextDto.From(t.IncomingName), t.Impact);
}

/// <summary>Seen from an item: outgoing (the item is the source) or incoming.</summary>
internal enum CiRelationshipDirection
{
    Outgoing,
    Incoming,
}

/// <summary>A relationship seen from one of its items.</summary>
/// <param name="Name">The type's name in this direction ("runs on" outgoing, "hosts" incoming).</param>
/// <param name="Other">The other end.</param>
internal sealed record CiRelationshipResponse(
    Guid Id, CiRelationshipDirection Direction, CiRelationshipTypeRef Type, LocalizedTextDto Name, CiNodeResponse Other, string? Note, DateTimeOffset CreatedAt)
{
    public static CiRelationshipResponse From(CiRelationship r, Guid from, CiNames names)
    {
        var type = names.Types[r.TypeId];
        var outgoing = r.SourceId == from;
        return new CiRelationshipResponse(r.Id, outgoing ? CiRelationshipDirection.Outgoing : CiRelationshipDirection.Incoming, CiRelationshipTypeRef.From(type),
            LocalizedTextDto.From(outgoing ? type.OutgoingName : type.IncomingName),
            outgoing ? names.Target(r) : names.Node(CiNodeKind.ConfigurationItem, r.SourceId), r.Note, r.CreatedAt);
    }
}

/// <summary>An affected item or consumer and how it is reached.</summary>
/// <param name="Depth">Steps away (1: directly).</param>
/// <param name="Via">The item it is reached from.</param>
/// <param name="Relationship">The relationship type it is reached by.</param>
internal sealed record CiImpactEntry(CiNodeResponse Node, int Depth, CiNodeResponse Via, CiRelationshipTypeRef Relationship);

/// <summary>What a fault affects.</summary>
/// <param name="Items">Affected items, nearest first.</param>
/// <param name="BusinessServices">The affected items that are business services.</param>
/// <param name="Consumers">Users, departments and customer groups that use affected items (or the items the traversal started from).</param>
/// <param name="UserCount">People reached: users named directly plus active members of the departments.</param>
/// <param name="Truncated">More than 5,000 items were reached; the rest are left out.</param>
internal sealed record CiImpactResponse(
    IReadOnlyList<CiImpactEntry> Items, IReadOnlyList<CiImpactEntry> BusinessServices, IReadOnlyList<CiImpactEntry> Consumers, int UserCount, bool Truncated)
{
    public static async Task<CiImpactResponse> LoadAsync(ZelvoDbContext db, ImpactResult result, CancellationToken cancellationToken)
    {
        var relationshipIds = result.Items.Select(i => i.RelationshipId).Concat(result.Consumers.Select(c => c.RelationshipId)).Distinct().ToList();
        var typeOf = relationshipIds.Count == 0 ? [] : await db.CiRelationships.AsNoTracking().Where(r => relationshipIds.Contains(r.Id))
            .ToDictionaryAsync(r => r.Id, r => r.TypeId, cancellationToken).ConfigureAwait(false);
        var names = await CiNames.LoadAsync(db,
            result.Items.SelectMany(i => new[] { i.Id, i.ViaId }).Concat(result.Consumers.Select(c => c.ViaId)),
            result.Consumers.Where(c => c.Kind == CiNodeKind.User).Select(c => c.Id),
            result.Consumers.Where(c => c.Kind == CiNodeKind.Department).Select(c => c.Id),
            result.Consumers.Where(c => c.Kind == CiNodeKind.CustomerGroup).Select(c => c.Id),
            cancellationToken).ConfigureAwait(false);

        CiRelationshipTypeRef TypeOf(Guid relationshipId) => CiRelationshipTypeRef.From(names.Types[typeOf[relationshipId]]);

        var items = result.Items.Select(i => new CiImpactEntry(names.Node(CiNodeKind.ConfigurationItem, i.Id), i.Depth,
            names.Node(CiNodeKind.ConfigurationItem, i.ViaId), TypeOf(i.RelationshipId))).ToList();
        var consumers = result.Consumers.Select(c => new CiImpactEntry(names.Node(c.Kind, c.Id), c.Depth,
            names.Node(CiNodeKind.ConfigurationItem, c.ViaId), TypeOf(c.RelationshipId))).ToList();

        var departmentIds = result.Consumers.Where(c => c.Kind == CiNodeKind.Department).Select(c => c.Id).ToList();
        var directUsers = result.Consumers.Where(c => c.Kind == CiNodeKind.User).Select(c => c.Id).ToHashSet();
        var departmentUsers = departmentIds.Count == 0 ? [] : await db.Users.AsNoTracking()
            .Where(u => u.DepartmentId != null && departmentIds.Contains(u.DepartmentId.Value) && u.Status == Data.Identity.UserStatus.Active)
            .Select(u => u.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
        directUsers.UnionWith(departmentUsers);

        return new CiImpactResponse(items, [.. items.Where(i => i.Node.Class?.Family == CiFamily.BusinessService)], consumers, directUsers.Count, result.Truncated);
    }
}

/// <summary>A node of the map with its distance from the item.</summary>
internal sealed record CiMapNode(CiNodeResponse Node, int Depth);

/// <summary>An edge of the map: source, target and type.</summary>
internal sealed record CiMapEdge(Guid Id, Guid Source, Guid Target, CiRelationshipTypeRef Type, string? Note);

/// <summary>The relationship map: nodes (the item first) and edges.</summary>
/// <param name="Truncated">More than 200 nodes were reached; narrow the depth or filters.</param>
internal sealed record CiMapResponse(IReadOnlyList<CiMapNode> Nodes, IReadOnlyList<CiMapEdge> Edges, bool Truncated)
{
    public static async Task<CiMapResponse> LoadAsync(ZelvoDbContext db, CiGraph graph, CiClassIndex classes, CancellationToken cancellationToken)
    {
        var names = await CiNames.LoadAsync(db,
            graph.Nodes.Where(n => n.Kind == CiNodeKind.ConfigurationItem).Select(n => n.Id),
            graph.Nodes.Where(n => n.Kind == CiNodeKind.User).Select(n => n.Id),
            graph.Nodes.Where(n => n.Kind == CiNodeKind.Department).Select(n => n.Id),
            graph.Nodes.Where(n => n.Kind == CiNodeKind.CustomerGroup).Select(n => n.Id),
            cancellationToken).ConfigureAwait(false);
        return new CiMapResponse(
            [.. graph.Nodes.Select(n => new CiMapNode(names.Node(n.Kind, n.Id), n.Depth))],
            [.. graph.Edges.Select(e => new CiMapEdge(e.Id, e.SourceId,
                e.TargetCiId ?? e.TargetUserId ?? e.TargetDepartmentId ?? e.TargetCustomerGroupId!.Value, CiRelationshipTypeRef.From(names.Types[e.TypeId]), e.Note))],
            graph.Truncated);
    }
}

/// <summary>A ticket an item is on.</summary>
internal sealed record CiTicketResponse(Guid Id, string Number, TicketType Type, string Title, TicketState State, DateTimeOffset CreatedAt, DateTimeOffset LinkedAt);

/// <summary>An item linked to a ticket.</summary>
internal sealed record TicketCiResponse(CiPickResponse Item, Guid? AddedById, string? AddedByName, DateTimeOffset AddedAt);
