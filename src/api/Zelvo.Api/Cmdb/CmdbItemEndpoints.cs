using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Zelvo.Api.Conventions;
using Zelvo.Api.Forms;
using Zelvo.Api.Identity;
using Zelvo.Api.Incidents;
using Zelvo.Api.ServiceDesk;
using Zelvo.Api.Settings;
using Zelvo.Api.Tenancy;
using Zelvo.Data;
using Zelvo.Data.Auditing;
using Zelvo.Data.Cmdb;
using Zelvo.Data.Directory;
using Zelvo.Data.Identity;
using Zelvo.Data.Localization;
using Zelvo.Data.Tenancy;
using Zelvo.Data.Tickets;
using Zelvo.Search;
using Zelvo.Tickets.Forms;

namespace Zelvo.Api.Cmdb;

/// <summary>
/// Configuration items, their relationships, the relationship map, impact traversal, history and
/// the tickets they are on (task G1, module 18). <c>cmdb.read</c> sees them; <c>cmdb.manage</c>
/// changes them. Every change is audited and the search index follows.
/// </summary>
internal static class CmdbItemEndpoints
{
    public static RouteGroupBuilder MapCmdbItemEndpoints(this RouteGroupBuilder group)
    {
        var cmdb = group.MapGroup("/cmdb").WithTags("CMDB").RequireTenant();

        cmdb.MapGet("/items", ListAsync)
            .WithName("ListConfigurationItems")
            .WithSummary("Configuration items, filtered by words in the number or name, class (with the classes below it), status, owner group and location (with the locations below it). Needs cmdb.read.")
            .RequirePermission(Permissions.CmdbRead)
            .Produces<PageResult<CiSummaryResponse>>();

        cmdb.MapPost("/items", CreateAsync)
            .WithName("CreateConfigurationItem")
            .WithSummary("Add an item of a class, with its attribute values checked against the class's attributes (C1 checks). Needs cmdb.manage; audited.")
            .RequirePermission(Permissions.CmdbManage)
            .Produces<CiResponse>(StatusCodes.Status201Created)
            .ProducesValidationProblem();

        cmdb.MapGet("/items/{id:guid}", GetAsync)
            .WithName("GetConfigurationItem")
            .WithSummary("One item with its class, attributes (with their definitions and the names of users and locations they refer to), owner group and location. Returns an ETag for If-Match. Needs cmdb.read.")
            .RequirePermission(Permissions.CmdbRead)
            .Produces<CiResponse>();

        cmdb.MapPatch("/items/{id:guid}", UpdateAsync)
            .WithName("UpdateConfigurationItem")
            .WithSummary("Change an item: name, description, class, status, owner group, location or attribute values (send all values; those left out are cleared). Send the ETag as If-Match. Needs cmdb.manage; audited.")
            .RequirePermission(Permissions.CmdbManage)
            .Produces<CiResponse>()
            .ProducesValidationProblem();

        cmdb.MapDelete("/items/{id:guid}", DeleteAsync)
            .WithName("DeleteConfigurationItem")
            .WithSummary("Delete an item that no ticket refers to (its relationships go with it); otherwise retire it. Needs cmdb.manage; audited.")
            .RequirePermission(Permissions.CmdbManage)
            .Produces(StatusCodes.Status204NoContent);

        cmdb.MapGet("/items/{id:guid}/relationships", RelationshipsAsync)
            .WithName("ListCiRelationships")
            .WithSummary("The item's relationships, from it (outgoing) and to it (incoming), with the other end. Needs cmdb.read.")
            .RequirePermission(Permissions.CmdbRead)
            .Produces<IReadOnlyList<CiRelationshipResponse>>();

        cmdb.MapPost("/items/{id:guid}/relationships", AddRelationshipAsync)
            .WithName("AddCiRelationship")
            .WithSummary("Relate the item to another item, or (for types whose target is people, such as used by) to a user, a department or a customer group. Needs cmdb.manage; audited on both items.")
            .RequirePermission(Permissions.CmdbManage)
            .Produces<CiRelationshipResponse>(StatusCodes.Status201Created)
            .ProducesValidationProblem();

        cmdb.MapDelete("/relationships/{relationshipId:guid}", RemoveRelationshipAsync)
            .WithName("RemoveCiRelationship")
            .WithSummary("Remove a relationship. Needs cmdb.manage; audited on both items.")
            .RequirePermission(Permissions.CmdbManage)
            .Produces(StatusCodes.Status204NoContent);

        cmdb.MapGet("/items/{id:guid}/impact", ImpactAsync)
            .WithName("GetCiImpact")
            .WithSummary("What a fault of the item affects, following each relationship type's impact direction, up to depth steps (1–10, default 3): the items with how they are reached, and the users, departments and customer groups that use them. Retired items are left out. Needs cmdb.read.")
            .RequirePermission(Permissions.CmdbRead)
            .Produces<CiImpactResponse>();

        cmdb.MapPost("/impact", ImpactOfManyAsync)
            .WithName("GetCiImpactOfItems")
            .WithSummary("What a fault of several items affects (for changes and problems). Needs cmdb.read.")
            .RequirePermission(Permissions.CmdbRead)
            .Produces<CiImpactResponse>()
            .ProducesValidationProblem();

        cmdb.MapGet("/items/{id:guid}/map", MapAsync)
            .WithName("GetCiMap")
            .WithSummary("Nodes and relationships around the item in both directions up to depth steps (1–4, default 2), at most 200 nodes, filtered by relationship types and classes; for the relationship map and its list view. Needs cmdb.read.")
            .RequirePermission(Permissions.CmdbRead)
            .Produces<CiMapResponse>();

        cmdb.MapGet("/items/{id:guid}/history", HistoryAsync)
            .WithName("GetCiHistory")
            .WithSummary("The item's history from the audit log: field changes, relationships and tickets added and removed. Needs cmdb.read.")
            .RequirePermission(Permissions.CmdbRead)
            .Produces<IReadOnlyList<HistoryEntryResponse>>();

        cmdb.MapGet("/items/{id:guid}/tickets", TicketsAsync)
            .WithName("ListCiTickets")
            .WithSummary("Tickets the item is on, newest first, of the record types the caller may read. Needs cmdb.read.")
            .RequirePermission(Permissions.CmdbRead)
            .Produces<IReadOnlyList<CiTicketResponse>>();

        cmdb.MapGet("/lookup", LookupAsync)
            .WithName("LookupConfigurationItems")
            .WithSummary("Items to pick in a form or on a ticket, found by number or name (at most 20). Holders of cmdb.read find every item that is not retired; others only active items of classes marked for requesters.")
            .RequireAuthorization()
            .Produces<IReadOnlyList<CiPickResponse>>();

        return group;
    }

    // --- Items ---

    internal static async Task<Ok<PageResult<CiSummaryResponse>>> ListAsync(
        [AsParameters] PageQuery page, [AsParameters] CiListQuery filter, ZelvoDbContext db, CurrentUser user, CancellationToken cancellationToken)
    {
        var classes = await CiClassIndex.LoadAsync(db, cancellationToken);
        var query = db.ConfigurationItems.AsNoTracking();
        if (filter.Q?.Trim() is { Length: > 0 } q)
        {
            var pattern = LikePattern(q);
            query = query.Where(i => EF.Functions.ILike(i.Name, pattern) || EF.Functions.ILike(i.Number, pattern));
        }

        if (filter.ClassId is { } classId)
        {
            var ids = classes.WithDescendants(classId).ToList();
            query = query.Where(i => ids.Contains(i.ClassId));
        }

        if (filter.Family is { } family)
        {
            var ids = classes.All.Where(c => c.Family == family).Select(c => c.Id).ToList();
            query = query.Where(i => ids.Contains(i.ClassId));
        }

        if (filter.Status is { } status)
        {
            query = query.Where(i => i.Status == status);
        }
        else if (filter.IncludeRetired != true)
        {
            query = query.Where(i => i.Status != CiStatus.Retired);
        }

        if (filter.GroupId is { } groupId)
        {
            query = query.Where(i => i.OwnerGroupId == groupId);
        }

        // Task G2: items no source reports any more.
        if (filter.Missing == true)
        {
            query = query.Where(i => i.MissingSince != null);
        }

        if (filter.LocationId is { } locationId)
        {
            var tree = new TreeIndex<Location>(await db.Locations.AsNoTracking().ToListAsync(cancellationToken));
            var ids = tree.WithDescendants(locationId).ToList();
            query = query.Where(i => i.LocationId != null && ids.Contains(i.LocationId.Value));
        }

        var total = await query.LongCountAsync(cancellationToken);
        var items = await query.OrderBy(i => i.Name).ThenBy(i => i.Sequence).Skip(page.Skip).Take(page.Size).ToListAsync(cancellationToken);
        var refs = await DirectoryRefs.LoadAsync(db, cancellationToken);
        return TypedResults.Ok(new PageResult<CiSummaryResponse>([.. items.Select(i => CiSummaryResponse.From(i, classes, refs))], page.Number, page.Size, total));
    }

    internal static async Task<Results<Ok<CiResponse>, ProblemHttpResult>> GetAsync(
        Guid id, HttpContext http, ZelvoDbContext db, CurrentUser user, CancellationToken cancellationToken)
    {
        var item = await db.ConfigurationItems.AsNoTracking().SingleOrDefaultAsync(i => i.Id == id, cancellationToken);
        if (item is null)
        {
            return NotFound();
        }

        var response = await CiResponse.LoadAsync(db, item, cancellationToken);
        http.Response.Headers.ETag = response.Etag;
        return TypedResults.Ok(response);
    }

    internal static async Task<Results<Created<CiResponse>, ValidationProblem, ProblemHttpResult>> CreateAsync(
        CreateCiRequest request, HttpContext http, ZelvoDbContext db, ITenantContext tenant, TenantSettingsStore settings, ISearchIndexer indexer,
        CancellationToken cancellationToken)
    {
        var classes = await CiClassIndex.LoadAsync(db, cancellationToken);
        if (classes.Find(request.ClassId!.Value) is null || !classes.Selectable(request.ClassId.Value))
        {
            return UserEndpoints.Validation("classId", "Choose an active class.");
        }

        var name = request.Name!.Trim();
        if (name.Length == 0)
        {
            return UserEndpoints.Validation("name", "Give the item a name.");
        }

        var description = CmdbSettingsEndpoints.Clean(request.Description) ?? new LocalizedText(null, null);
        if (await CheckPlacementAsync(db, request.OwnerGroupId, request.LocationId, cancellationToken) is { } placement)
        {
            return placement;
        }

        var (values, invalid) = await CheckAttributesAsync(db, classes, request.ClassId.Value, request.Attributes ?? [], cancellationToken);
        if (invalid is not null)
        {
            return invalid;
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var prefix = await settings.GetAsync(CmdbSettings.NumberPrefix, cancellationToken);
        var sequence = await CmdbNumbers.NextAsync(db, tenant.TenantId!.Value, cancellationToken);
        var item = new ConfigurationItem
        {
            Number = CmdbNumbers.Format(prefix, sequence),
            Sequence = sequence,
            ClassId = request.ClassId.Value,
            Name = name,
            Description = description,
            Status = request.Status ?? CiStatus.Active,
            OwnerGroupId = request.OwnerGroupId,
            LocationId = request.LocationId,
            Attributes = values.ToJsonString(),
            // Task G2: what people set wins over discovery unless an administrator ranks a source higher.
            AttributeSources = CmdbPrecedence.MarkManual("{}", [], values, nameChanged: true),
        };
        db.ConfigurationItems.Add(item);
        indexer.Upsert(CmdbSearch.Document(item, classes, await CmdbSearch.ReaderRolesAsync(db, cancellationToken)));
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        var saved = await db.ConfigurationItems.AsNoTracking().SingleAsync(i => i.Id == item.Id, cancellationToken);
        var response = await CiResponse.LoadAsync(db, saved, cancellationToken);
        http.Response.Headers.ETag = response.Etag;
        return TypedResults.Created($"/api/v1/cmdb/items/{item.Id}", response);
    }

    internal static async Task<Results<Ok<CiResponse>, ValidationProblem, ProblemHttpResult>> UpdateAsync(
        Guid id, UpdateCiRequest request, HttpContext http, ZelvoDbContext db, ISearchIndexer indexer, CancellationToken cancellationToken)
    {
        var item = await db.ConfigurationItems.SingleOrDefaultAsync(i => i.Id == id, cancellationToken);
        if (item is null)
        {
            return NotFound();
        }

        if (!FormsCommon.Matches(http, item.Version))
        {
            return FormsCommon.VersionMismatch();
        }

        var classes = await CiClassIndex.LoadAsync(db, cancellationToken);
        if (request.ClassId is { } classId && classId != item.ClassId)
        {
            if (classes.Find(classId) is null || !classes.Selectable(classId))
            {
                return UserEndpoints.Validation("classId", "Choose an active class.");
            }

            item.ClassId = classId;
        }

        var nameBefore = item.Name;
        if (request.Name is not null)
        {
            var name = request.Name.Trim();
            if (name.Length == 0)
            {
                return UserEndpoints.Validation("name", "Give the item a name.");
            }

            item.Name = name;
        }

        if (request.Description is not null)
        {
            item.Description = CmdbSettingsEndpoints.Clean(request.Description)!;
        }

        if (request.Status is { } status)
        {
            item.Status = status;
        }

        if (request.OwnerGroupId is not null || request.LocationId is not null)
        {
            if (await CheckPlacementAsync(db, request.OwnerGroupId is { } g && g != Guid.Empty && g != item.OwnerGroupId ? g : null,
                    request.LocationId is { } l && l != Guid.Empty && l != item.LocationId ? l : null, cancellationToken) is { } placement)
            {
                return placement;
            }

            // Guid.Empty clears the group or location.
            if (request.OwnerGroupId is { } group)
            {
                item.OwnerGroupId = group == Guid.Empty ? null : group;
            }

            if (request.LocationId is { } location)
            {
                item.LocationId = location == Guid.Empty ? null : location;
            }
        }

        // Attribute values are checked again when they or the class change; values of keys the
        // new class does not have are dropped.
        if (request.Attributes is not null || request.ClassId is not null)
        {
            var submitted = request.Attributes ?? JsonNode.Parse(item.Attributes)!.AsObject();
            var keys = classes.Attributes(item.ClassId).Select(a => a.Field.Key).ToHashSet(StringComparer.Ordinal);
            foreach (var key in submitted.Select(p => p.Key).Where(k => !keys.Contains(k)).ToList())
            {
                if (request.Attributes is null)
                {
                    submitted.Remove(key);
                }
            }

            var (values, invalid) = await CheckAttributesAsync(db, classes, item.ClassId, submitted.DeepClone().AsObject(), cancellationToken);
            if (invalid is not null)
            {
                return invalid;
            }

            if (values.ToJsonString() != JsonNode.Parse(item.Attributes)!.ToJsonString())
            {
                item.AttributeSources = CmdbPrecedence.MarkManual(item.AttributeSources, JsonNode.Parse(item.Attributes)!.AsObject(), values, nameChanged: false);
                item.Attributes = values.ToJsonString();
            }
        }

        if (item.Name != nameBefore)
        {
            item.AttributeSources = CmdbPrecedence.MarkManual(item.AttributeSources, [], [], nameChanged: true);
        }

        item.UpdatedAt = DateTimeOffset.UtcNow;
        indexer.Upsert(CmdbSearch.Document(item, classes, await CmdbSearch.ReaderRolesAsync(db, cancellationToken)));
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return FormsCommon.VersionMismatch();
        }

        var saved = await db.ConfigurationItems.AsNoTracking().SingleAsync(i => i.Id == id, cancellationToken);
        var response = await CiResponse.LoadAsync(db, saved, cancellationToken);
        http.Response.Headers.ETag = response.Etag;
        return TypedResults.Ok(response);
    }

    internal static async Task<Results<NoContent, ProblemHttpResult>> DeleteAsync(
        Guid id, ZelvoDbContext db, ISearchIndexer indexer, CancellationToken cancellationToken)
    {
        var item = await db.ConfigurationItems.SingleOrDefaultAsync(i => i.Id == id, cancellationToken);
        if (item is null)
        {
            return NotFound();
        }

        if (await db.TicketConfigurationItems.AnyAsync(l => l.ConfigurationItemId == id, cancellationToken))
        {
            return DirectoryProblems.InUse("Tickets refer to this item; retire it instead.");
        }

        db.CiRelationships.RemoveRange(await db.CiRelationships.Where(r => r.SourceId == id || r.TargetCiId == id).ToListAsync(cancellationToken));
        db.ConfigurationItems.Remove(item);
        indexer.Delete(CmdbSearch.Type, id.ToString());
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.NoContent();
    }

    // --- Relationships ---

    internal static async Task<Results<Ok<List<CiRelationshipResponse>>, ProblemHttpResult>> RelationshipsAsync(
        Guid id, ZelvoDbContext db, CancellationToken cancellationToken)
    {
        if (!await db.ConfigurationItems.AnyAsync(i => i.Id == id, cancellationToken))
        {
            return NotFound();
        }

        await CmdbRelationshipDefaults.EnsureAsync(db, cancellationToken);
        var relationships = await db.CiRelationships.AsNoTracking().Where(r => r.SourceId == id || r.TargetCiId == id).ToListAsync(cancellationToken);
        var names = await CiNames.LoadAsync(db, relationships, cancellationToken);
        return TypedResults.Ok(relationships
            .Select(r => CiRelationshipResponse.From(r, id, names))
            .OrderBy(r => r.Direction).ThenBy(r => r.Type.Key, StringComparer.Ordinal).ThenBy(r => r.Other.Name, StringComparer.CurrentCulture).ToList());
    }

    internal static async Task<Results<Created<CiRelationshipResponse>, ValidationProblem, ProblemHttpResult>> AddRelationshipAsync(
        Guid id, AddCiRelationshipRequest request, ZelvoDbContext db, CurrentUser user, IAuditLog audit, CancellationToken cancellationToken)
    {
        var source = await db.ConfigurationItems.AsNoTracking().SingleOrDefaultAsync(i => i.Id == id, cancellationToken);
        if (source is null)
        {
            return NotFound();
        }

        await CmdbRelationshipDefaults.EnsureAsync(db, cancellationToken);
        var type = await db.CiRelationshipTypes.AsNoTracking().SingleOrDefaultAsync(t => t.Id == request.TypeId, cancellationToken);
        if (type is null || !type.Active)
        {
            return UserEndpoints.Validation("typeId", "Choose an active relationship type.");
        }

        var targets = new[] { request.TargetId, request.TargetUserId, request.TargetDepartmentId, request.TargetCustomerGroupId }.Count(t => t is not null);
        if (targets != 1)
        {
            return UserEndpoints.Validation("targetId", "Name exactly one target: an item, a user, a department or a customer group.");
        }

        string targetLabel;
        if (type.TargetKind == RelationshipTargetKind.ConfigurationItem)
        {
            if (request.TargetId is not { } targetId)
            {
                return UserEndpoints.Validation("targetId", "This relationship type relates two items.");
            }

            if (targetId == id)
            {
                return UserEndpoints.Validation("targetId", "An item cannot be related to itself.");
            }

            var target = await db.ConfigurationItems.AsNoTracking().SingleOrDefaultAsync(i => i.Id == targetId, cancellationToken);
            if (target is null)
            {
                return UserEndpoints.Validation("targetId", "The item does not exist.");
            }

            targetLabel = $"{target.Number} {target.Name}";
        }
        else if (request.TargetId is not null)
        {
            return UserEndpoints.Validation("targetId", "This relationship type points at users, departments or customer groups.");
        }
        else if (request.TargetUserId is { } userId)
        {
            var target = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == userId && u.Status == UserStatus.Active, cancellationToken);
            if (target is null)
            {
                return UserEndpoints.Validation("targetUserId", "Choose an active user.");
            }

            targetLabel = target.DisplayName ?? target.Id.ToString();
        }
        else if (request.TargetDepartmentId is { } departmentId)
        {
            var target = await db.Departments.AsNoTracking().SingleOrDefaultAsync(d => d.Id == departmentId && d.Active, cancellationToken);
            if (target is null)
            {
                return UserEndpoints.Validation("targetDepartmentId", "Choose an active department.");
            }

            targetLabel = target.Name.En ?? target.Name.Ar ?? string.Empty;
        }
        else
        {
            var target = await db.CustomerGroups.AsNoTracking().SingleOrDefaultAsync(g => g.Id == request.TargetCustomerGroupId && g.Active, cancellationToken);
            if (target is null)
            {
                return UserEndpoints.Validation("targetCustomerGroupId", "Choose an active customer group.");
            }

            targetLabel = target.Name.En ?? target.Name.Ar ?? string.Empty;
        }

        var exists = await db.CiRelationships.AnyAsync(r => r.SourceId == id && r.TypeId == type.Id && r.TargetCiId == request.TargetId
            && r.TargetUserId == request.TargetUserId && r.TargetDepartmentId == request.TargetDepartmentId && r.TargetCustomerGroupId == request.TargetCustomerGroupId,
            cancellationToken);
        if (exists)
        {
            return (ProblemHttpResult)ApiProblem.Create(StatusCodes.Status409Conflict, "relationship-exists", "These two are already related this way.");
        }

        var note = request.Note?.Trim() is { Length: > 0 } n ? n : null;
        var relationship = new CiRelationship
        {
            TypeId = type.Id,
            SourceId = id,
            TargetCiId = request.TargetId,
            TargetUserId = request.TargetUserId,
            TargetDepartmentId = request.TargetDepartmentId,
            TargetCustomerGroupId = request.TargetCustomerGroupId,
            Note = note,
            CreatedById = user.Account?.Id,
        };
        db.CiRelationships.Add(relationship);
        RecordRelationship(audit, "cmdb.relationship.added", relationship, type, $"{source.Number} {source.Name}", targetLabel);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            return (ProblemHttpResult)ApiProblem.Create(StatusCodes.Status409Conflict, "relationship-exists", "These two are already related this way.");
        }

        var names = await CiNames.LoadAsync(db, [relationship], cancellationToken);
        return TypedResults.Created($"/api/v1/cmdb/relationships/{relationship.Id}", CiRelationshipResponse.From(relationship, id, names));
    }

    internal static async Task<Results<NoContent, ProblemHttpResult>> RemoveRelationshipAsync(
        Guid relationshipId, ZelvoDbContext db, IAuditLog audit, CancellationToken cancellationToken)
    {
        var relationship = await db.CiRelationships.SingleOrDefaultAsync(r => r.Id == relationshipId, cancellationToken);
        if (relationship is null)
        {
            return FormsCommon.NotFound("relationship");
        }

        var type = await db.CiRelationshipTypes.AsNoTracking().SingleAsync(t => t.Id == relationship.TypeId, cancellationToken);
        var names = await CiNames.LoadAsync(db, [relationship], cancellationToken);
        var response = CiRelationshipResponse.From(relationship, relationship.SourceId, names);
        var source = names.Items.GetValueOrDefault(relationship.SourceId);
        db.CiRelationships.Remove(relationship);
        RecordRelationship(audit, "cmdb.relationship.removed", relationship, type, source is null ? string.Empty : $"{source.Number} {source.Name}",
            response.Other.Number is { } number ? $"{number} {response.Other.Name}" : response.Other.Name ?? string.Empty);
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.NoContent();
    }

    /// <summary>Named entries on both items, so each item's history shows the relationship.</summary>
    private static void RecordRelationship(IAuditLog audit, string action, CiRelationship r, CiRelationshipType type, string sourceLabel, string targetLabel)
    {
        var details = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["relationshipId"] = r.Id,
            ["type"] = type.Key,
            ["typeName"] = type.OutgoingName.En ?? type.OutgoingName.Ar,
            ["typeNameAr"] = type.OutgoingName.Ar ?? type.OutgoingName.En,
            ["source"] = sourceLabel,
            ["target"] = targetLabel,
        };
        audit.Add(AuditCategory.Change, action, CmdbAudit.ItemEntity, r.SourceId.ToString(), details);
        if (r.TargetCiId is { } target)
        {
            audit.Add(AuditCategory.Change, action, CmdbAudit.ItemEntity, target.ToString(), details);
        }
    }

    // --- Impact and map ---

    internal static async Task<Results<Ok<CiImpactResponse>, ProblemHttpResult>> ImpactAsync(
        Guid id, [Range(1, CmdbGraph.MaxImpactDepth)] int? depth, ZelvoDbContext db, CancellationToken cancellationToken)
    {
        if (!await db.ConfigurationItems.AnyAsync(i => i.Id == id, cancellationToken))
        {
            return NotFound();
        }

        await CmdbRelationshipDefaults.EnsureAsync(db, cancellationToken);
        var result = await CmdbGraph.ImpactAsync(db, [id], depth ?? 3, cancellationToken);
        return TypedResults.Ok(await CiImpactResponse.LoadAsync(db, result, cancellationToken));
    }

    internal static async Task<Results<Ok<CiImpactResponse>, ValidationProblem>> ImpactOfManyAsync(
        CiImpactRequest request, ZelvoDbContext db, CancellationToken cancellationToken)
    {
        var ids = request.ItemIds!.Distinct().ToList();
        var found = await db.ConfigurationItems.CountAsync(i => ids.Contains(i.Id), cancellationToken);
        if (found != ids.Count)
        {
            return UserEndpoints.Validation("itemIds", "An item does not exist.");
        }

        await CmdbRelationshipDefaults.EnsureAsync(db, cancellationToken);
        var result = await CmdbGraph.ImpactAsync(db, ids, request.Depth ?? 3, cancellationToken);
        return TypedResults.Ok(await CiImpactResponse.LoadAsync(db, result, cancellationToken));
    }

    internal static async Task<Results<Ok<CiMapResponse>, ProblemHttpResult>> MapAsync(
        Guid id, [AsParameters] CiMapQuery query, ZelvoDbContext db, CancellationToken cancellationToken)
    {
        if (!await db.ConfigurationItems.AnyAsync(i => i.Id == id, cancellationToken))
        {
            return NotFound();
        }

        await CmdbRelationshipDefaults.EnsureAsync(db, cancellationToken);
        var classes = await CiClassIndex.LoadAsync(db, cancellationToken);
        var classIds = (query.ClassIds ?? []).SelectMany(classes.WithDescendants).ToHashSet();
        var graph = await CmdbGraph.MapAsync(db, id, query.Depth ?? 2, (query.TypeIds ?? []).ToHashSet(), classIds,
            query.Consumers ?? true, query.Retired ?? false, cancellationToken);
        return TypedResults.Ok(await CiMapResponse.LoadAsync(db, graph, classes, cancellationToken));
    }

    // --- History and tickets ---

    internal static async Task<Results<Ok<List<HistoryEntryResponse>>, ProblemHttpResult>> HistoryAsync(
        Guid id, ZelvoDbContext db, CurrentUser user, CancellationToken cancellationToken)
    {
        if (!await db.ConfigurationItems.AnyAsync(i => i.Id == id, cancellationToken))
        {
            return NotFound();
        }

        var entityId = id.ToString();
        var events = await db.AuditEvents.AsNoTracking()
            .Where(e => e.EntityType == CmdbAudit.ItemEntity && e.EntityId == entityId)
            .OrderByDescending(e => e.Sequence).Take(1000)
            .ToListAsync(cancellationToken);
        events.Reverse();
        var erased = await Privacy.ErasedPeople.AmongAsync(db, events.Select(e => e.ActorId), cancellationToken);
        var entries = events.Select(e => HistoryEntryResponse.From(e, user) is var entry && Privacy.ErasedPeople.IsErased(e, erased) ? entry with { ActorName = null } : entry).ToList();

        // Classes, groups and locations are shown by name rather than by id.
        var classes = await CiClassIndex.LoadAsync(db, cancellationToken);
        var refs = await DirectoryRefs.LoadAsync(db, cancellationToken);
        var language = user.Account?.PreferredLanguage ?? LocalizedText.English;
        var names = refs.Names(language).ToDictionary(p => p.Key, p => p.Value);
        foreach (var c in classes.All)
        {
            names[c.Id] = classes.Path(c.Id).Get(language) ?? c.Key;
        }

        foreach (var change in entries.SelectMany(e => e.Changes ?? []).Where(c => c.Key is "classId" or "ownerGroupId" or "locationId").ToList())
        {
            foreach (var side in new[] { "old", "new" })
            {
                if (change.Value?[side] is JsonValue value && value.GetValueKind() == JsonValueKind.String && Guid.TryParse(value.GetValue<string>(), out var referenced))
                {
                    change.Value[side] = names.GetValueOrDefault(referenced, referenced.ToString());
                }
            }
        }

        return TypedResults.Ok(entries);
    }

    internal static async Task<Results<Ok<List<CiTicketResponse>>, ProblemHttpResult>> TicketsAsync(
        Guid id, ZelvoDbContext db, CurrentUser user, CancellationToken cancellationToken)
    {
        if (!await db.ConfigurationItems.AnyAsync(i => i.Id == id, cancellationToken))
        {
            return NotFound();
        }

        var types = CmdbTicketEndpoints.ReadableTypes(user);
        var tickets = await db.TicketConfigurationItems.AsNoTracking().Where(l => l.ConfigurationItemId == id)
            .Join(db.Tickets.AsNoTracking(), l => l.TicketId, t => t.Id, (l, t) => new { Link = l, Ticket = t })
            .Where(x => types.Contains(x.Ticket.Type))
            .OrderByDescending(x => x.Ticket.CreatedAt).Take(200)
            .Select(x => new CiTicketResponse(x.Ticket.Id, x.Ticket.Number, x.Ticket.Type, x.Ticket.Title, x.Ticket.State, x.Ticket.CreatedAt, x.Link.AddedAt))
            .ToListAsync(cancellationToken);
        return TypedResults.Ok(tickets);
    }

    internal static async Task<Ok<List<CiPickResponse>>> LookupAsync(
        [AsParameters] CiLookupQuery query, ZelvoDbContext db, CurrentUser user, CancellationToken cancellationToken)
    {
        var classes = await CiClassIndex.LoadAsync(db, cancellationToken);
        var allowed = CmdbPicker.PickableClasses(classes, user, query.ClassIds);
        var items = db.ConfigurationItems.AsNoTracking().Where(i => i.Status != CiStatus.Retired);
        if (allowed is not null)
        {
            var ids = allowed.ToList();
            items = items.Where(i => ids.Contains(i.ClassId));
        }

        if (!CmdbAccess.IsReader(user))
        {
            items = items.Where(i => i.Status == CiStatus.Active || i.Status == CiStatus.Maintenance);
        }

        if (query.Q?.Trim() is { Length: > 0 } q)
        {
            var pattern = LikePattern(q);
            items = items.Where(i => EF.Functions.ILike(i.Name, pattern) || EF.Functions.ILike(i.Number, pattern));
        }

        if (query.Ids is { Length: > 0 } wanted)
        {
            var list = wanted.Take(CiLookupQuery.MaxIds).ToList();
            items = items.Where(i => list.Contains(i.Id));
        }

        var found = await items.OrderBy(i => i.Name).ThenBy(i => i.Sequence).Take(Math.Clamp(query.Limit ?? 20, 1, 50)).ToListAsync(cancellationToken);
        return TypedResults.Ok(found.Select(i => CiPickResponse.From(i, classes)).ToList());
    }

    // --- Helpers ---

    internal static ProblemHttpResult NotFound() => FormsCommon.NotFound("configuration-item");

    internal static string LikePattern(string q) =>
        "%" + q.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal) + "%";

    private static async Task<ValidationProblem?> CheckPlacementAsync(ZelvoDbContext db, Guid? groupId, Guid? locationId, CancellationToken cancellationToken)
    {
        if (groupId is { } g && !await db.SupportGroups.AnyAsync(s => s.Id == g && s.Active, cancellationToken))
        {
            return UserEndpoints.Validation("ownerGroupId", "Choose an active group.");
        }

        if (locationId is { } l && !await TreeEndpoints.IsSelectableAsync(db.Locations, l, cancellationToken))
        {
            return UserEndpoints.Validation("locationId", "Choose an active location.");
        }

        return null;
    }

    private static async Task<(JsonObject Values, ValidationProblem? Problem)> CheckAttributesAsync(
        ZelvoDbContext db, CiClassIndex classes, Guid classId, JsonObject submitted, CancellationToken cancellationToken)
    {
        var fields = classes.Attributes(classId).Select(a => a.Field).ToList();
        var (values, errors, users, locations) = CmdbAttributes.CheckValues(fields, submitted);
        var userIds = users.Select(u => u.Id).Distinct().ToList();
        var active = userIds.Count == 0 ? [] : await db.Users.Where(u => userIds.Contains(u.Id) && u.Status == UserStatus.Active).Select(u => u.Id).ToListAsync(cancellationToken);
        errors.AddRange(users.Where(u => !active.Contains(u.Id)).Select(u => new FormAnswerError(u.Field, "user-unknown", "Choose an active user of this organisation.")));
        foreach (var (field, id) in locations)
        {
            if (!await TreeEndpoints.IsSelectableAsync(db.Locations, id, cancellationToken))
            {
                errors.Add(new(field, "location-unknown", "Choose an active location."));
            }
        }

        if (errors.Count == 0)
        {
            return (values, null);
        }

        return (values, TypedResults.ValidationProblem(
            errors.GroupBy(e => "attributes." + e.Field).ToDictionary(g => g.Key, g => g.Select(e => e.Message).ToArray(), StringComparer.Ordinal),
            type: ApiProblem.TypeFor("validation-failed"),
            extensions: new Dictionary<string, object?>(StringComparer.Ordinal) { ["code"] = "validation-failed", ["attributeErrors"] = errors }));
    }
}

/// <summary>Names used in audit entries of the CMDB.</summary>
internal static class CmdbAudit
{
    /// <summary>Entity type of configuration items in the audit log (the automatic name of <see cref="ConfigurationItem"/>).</summary>
    public const string ItemEntity = "configuration_item";
}

/// <summary>Which classes a caller may pick items of (forms and tickets).</summary>
internal static class CmdbPicker
{
    /// <summary>
    /// The classes items may be picked from: <paramref name="limit"/> (each with the classes below
    /// it) when given, intersected for people without <c>cmdb.read</c> with the classes marked for
    /// requesters; <see langword="null"/> when there is no limit.
    /// </summary>
    public static HashSet<Guid>? PickableClasses(CiClassIndex classes, CurrentUser user, IEnumerable<Guid>? limit)
    {
        HashSet<Guid>? allowed = limit?.Any() == true ? [.. limit.SelectMany(classes.WithDescendants)] : null;
        if (!CmdbAccess.IsReader(user))
        {
            var visible = classes.All.Where(c => c.RequesterVisible).Select(c => c.Id).ToHashSet();
            allowed = allowed is null ? visible : [.. allowed.Intersect(visible)];
        }

        return allowed;
    }

    /// <summary>Checks the configuration items of form answers (task G1's picker field).</summary>
    public static async Task CheckAsync(ZelvoDbContext db, CurrentUser user, FormAnswerResult result, CancellationToken cancellationToken)
    {
        if (result.ConfigurationItems.Count == 0)
        {
            return;
        }

        var classes = await CiClassIndex.LoadAsync(db, cancellationToken);
        var ids = result.ConfigurationItems.Select(c => c.Id).Distinct().ToList();
        var items = await db.ConfigurationItems.AsNoTracking().Where(i => ids.Contains(i.Id)).ToDictionaryAsync(i => i.Id, cancellationToken);
        var reader = CmdbAccess.IsReader(user);
        foreach (var (field, id, limit) in result.ConfigurationItems)
        {
            var allowed = PickableClasses(classes, user, limit);
            if (!items.TryGetValue(id, out var item) || item.Status == CiStatus.Retired || (allowed is not null && !allowed.Contains(item.ClassId))
                || (!reader && item.Status is not (CiStatus.Active or CiStatus.Maintenance)))
            {
                result.Errors.Add(new(field, "configuration-item-unknown", "Choose an item from the list."));
            }
        }
    }
}

/// <summary>Filters of the item list.</summary>
public sealed class CiListQuery
{
    /// <summary>Words in the number or name.</summary>
    [MaxLength(200)]
    public string? Q { get; init; }

    /// <summary>Only items of this class or the classes below it.</summary>
    public Guid? ClassId { get; init; }

    /// <summary>Only items of classes of this family.</summary>
    public CiFamily? Family { get; init; }

    /// <summary>Task G2: only items no discovery source reports any more.</summary>
    public bool? Missing { get; init; }

    /// <summary>Only this status.</summary>
    public CiStatus? Status { get; init; }

    /// <summary>Also retired items (without a status filter).</summary>
    public bool? IncludeRetired { get; init; }

    /// <summary>Only items this group looks after.</summary>
    public Guid? GroupId { get; init; }

    /// <summary>Only items at this location or below it.</summary>
    public Guid? LocationId { get; init; }
}

/// <summary>Map filters.</summary>
public sealed class CiMapQuery
{
    /// <summary>Steps from the item (1–4, default 2).</summary>
    [Range(1, CmdbGraph.MaxMapDepth)]
    public int? Depth { get; init; }

    /// <summary>Only relationships of these types (all when left out).</summary>
    public Guid[]? TypeIds { get; init; }

    /// <summary>Only items of these classes and the classes below them (all when left out; the item itself always).</summary>
    public Guid[]? ClassIds { get; init; }

    /// <summary>Show the users, departments and customer groups that use items (default yes).</summary>
    public bool? Consumers { get; init; }

    /// <summary>Show retired items (default no).</summary>
    public bool? Retired { get; init; }
}

/// <summary>Picker search.</summary>
public sealed class CiLookupQuery
{
    /// <summary>Most ids one lookup takes.</summary>
    public const int MaxIds = 50;

    /// <summary>Words in the number or name.</summary>
    [MaxLength(200)]
    public string? Q { get; init; }

    /// <summary>Only items of these classes and the classes below them.</summary>
    public Guid[]? ClassIds { get; init; }

    /// <summary>These items only (to show the names of items already picked).</summary>
    public Guid[]? Ids { get; init; }

    /// <summary>Most items (1–50, default 20).</summary>
    [Range(1, 50)]
    public int? Limit { get; init; }
}

/// <summary>A new item.</summary>
public sealed class CreateCiRequest
{
    /// <summary>The class.</summary>
    [Required]
    public Guid? ClassId { get; init; }

    /// <summary>Name, for example <c>srv-app-01</c> or "Payroll".</summary>
    [Required, StringLength(ConfigurationItem.NameMaxLength)]
    public string? Name { get; init; }

    /// <summary>What the item is, in English and Arabic.</summary>
    public CiDescriptionInput? Description { get; init; }

    /// <summary>Status (default Active).</summary>
    public CiStatus? Status { get; init; }

    /// <summary>The group that looks after it.</summary>
    public Guid? OwnerGroupId { get; init; }

    /// <summary>Where it is.</summary>
    public Guid? LocationId { get; init; }

    /// <summary>Attribute values by key.</summary>
    public JsonObject? Attributes { get; init; }
}

/// <summary>Changes to an item; what is left out stays. Guid.Empty clears the group or location.</summary>
public sealed class UpdateCiRequest
{
    /// <summary>New class (values are checked again; those the new class does not have are dropped).</summary>
    public Guid? ClassId { get; init; }

    /// <summary>New name.</summary>
    [StringLength(ConfigurationItem.NameMaxLength)]
    public string? Name { get; init; }

    /// <summary>New description.</summary>
    public CiDescriptionInput? Description { get; init; }

    /// <summary>New status.</summary>
    public CiStatus? Status { get; init; }

    /// <summary>New owner group (Guid.Empty: none).</summary>
    public Guid? OwnerGroupId { get; init; }

    /// <summary>New location (Guid.Empty: none).</summary>
    public Guid? LocationId { get; init; }

    /// <summary>All attribute values; values left out are cleared.</summary>
    public JsonObject? Attributes { get; init; }
}

/// <summary>A new relationship from an item.</summary>
public sealed class AddCiRelationshipRequest
{
    /// <summary>The relationship type.</summary>
    [Required]
    public Guid? TypeId { get; init; }

    /// <summary>The other item.</summary>
    public Guid? TargetId { get; init; }

    /// <summary>A user ("used by").</summary>
    public Guid? TargetUserId { get; init; }

    /// <summary>A department ("used by").</summary>
    public Guid? TargetDepartmentId { get; init; }

    /// <summary>A customer group ("used by").</summary>
    public Guid? TargetCustomerGroupId { get; init; }

    /// <summary>Optional note.</summary>
    [StringLength(CiRelationship.NoteMaxLength)]
    public string? Note { get; init; }
}

/// <summary>Several items whose impact is wanted.</summary>
public sealed class CiImpactRequest
{
    /// <summary>The items (1–100).</summary>
    [Required, MinLength(1), MaxLength(100)]
    public IReadOnlyList<Guid>? ItemIds { get; init; }

    /// <summary>Steps (1–10, default 3).</summary>
    [Range(1, CmdbGraph.MaxImpactDepth)]
    public int? Depth { get; init; }
}
