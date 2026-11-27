using System.ComponentModel.DataAnnotations;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Zelvo.Api.Conventions;
using Zelvo.Api.Forms;
using Zelvo.Api.Identity;
using Zelvo.Api.ServiceDesk;
using Zelvo.Api.Settings;
using Zelvo.Api.Tenancy;
using Zelvo.Data;
using Zelvo.Data.Auditing;
using Zelvo.Data.Cmdb;
using Zelvo.Data.Localization;
using Zelvo.Tickets.Forms;

namespace Zelvo.Api.Cmdb;

/// <summary>
/// Settings → CMDB (task G1, module 18): classes with their attributes, relationship types and the
/// number prefix. Holders of <c>cmdb.read</c> read them (item pages need them); <c>cmdb.manage</c>
/// changes them. Every change is audited.
/// </summary>
internal static class CmdbSettingsEndpoints
{
    public static RouteGroupBuilder MapCmdbSettingsEndpoints(this RouteGroupBuilder group)
    {
        var cmdb = group.MapGroup("/cmdb").WithTags("CMDB").RequireTenant();

        cmdb.MapGet("/classes", ListClassesAsync)
            .WithName("ListCiClasses")
            .WithSummary("Configuration item classes in tree order, with their own and inherited attributes and how many items each has. Needs cmdb.read.")
            .RequirePermission(Permissions.CmdbRead)
            .Produces<IReadOnlyList<CiClassResponse>>();

        cmdb.MapPost("/classes", CreateClassAsync)
            .WithName("CreateCiClass")
            .WithSummary("Add a class at the top of a family or below another class (at most four levels), with its attributes (C1 fields). Needs cmdb.manage; audited.")
            .RequirePermission(Permissions.CmdbManage)
            .Produces<CiClassResponse>(StatusCodes.Status201Created)
            .ProducesValidationProblem();

        cmdb.MapPatch("/classes/{id:guid}", UpdateClassAsync)
            .WithName("UpdateCiClass")
            .WithSummary("Rename a class, change its attributes, whether requesters may pick its items, make it active or inactive, or move it among its siblings. Send the ETag as If-Match. Needs cmdb.manage; audited.")
            .RequirePermission(Permissions.CmdbManage)
            .Produces<CiClassResponse>()
            .ProducesValidationProblem();

        cmdb.MapDelete("/classes/{id:guid}", DeleteClassAsync)
            .WithName("DeleteCiClass")
            .WithSummary("Delete a class without items or classes below it; otherwise make it inactive. Needs cmdb.manage; audited.")
            .RequirePermission(Permissions.CmdbManage)
            .Produces(StatusCodes.Status204NoContent);

        cmdb.MapPost("/classes/standard", AddStandardClassesAsync)
            .WithName("AddStandardCiClasses")
            .WithSummary("Add Zelvo's standard bilingual classes (servers, computers, applications, databases, network devices, virtual machines, business services, …) that the tenant does not have yet, by key. Nothing existing is changed. Needs cmdb.manage; audited.")
            .RequirePermission(Permissions.CmdbManage)
            .Produces<IReadOnlyList<CiClassResponse>>();

        cmdb.MapGet("/relationship-types", ListTypesAsync)
            .WithName("ListCiRelationshipTypes")
            .WithSummary("Relationship types: the four built in (runs on, depends on, connects to, used by) and the tenant's own. Needs cmdb.read.")
            .RequirePermission(Permissions.CmdbRead)
            .Produces<IReadOnlyList<CiRelationshipTypeResponse>>();

        cmdb.MapPost("/relationship-types", CreateTypeAsync)
            .WithName("CreateCiRelationshipType")
            .WithSummary("Add a relationship type between items, with a bilingual name for each direction and which way faults travel. Needs cmdb.manage; audited.")
            .RequirePermission(Permissions.CmdbManage)
            .Produces<CiRelationshipTypeResponse>(StatusCodes.Status201Created)
            .ProducesValidationProblem();

        cmdb.MapPatch("/relationship-types/{id:guid}", UpdateTypeAsync)
            .WithName("UpdateCiRelationshipType")
            .WithSummary("Rename a type, change which way faults travel (not for built-in types) or make it active or inactive. Needs cmdb.manage; audited.")
            .RequirePermission(Permissions.CmdbManage)
            .Produces<CiRelationshipTypeResponse>()
            .ProducesValidationProblem();

        cmdb.MapDelete("/relationship-types/{id:guid}", DeleteTypeAsync)
            .WithName("DeleteCiRelationshipType")
            .WithSummary("Delete a type of the tenant's own that no relationship uses; built-in types and types in use are made inactive instead. Needs cmdb.manage; audited.")
            .RequirePermission(Permissions.CmdbManage)
            .Produces(StatusCodes.Status204NoContent);

        cmdb.MapGet("/settings", GetSettingsAsync)
            .WithName("GetCmdbSettings")
            .WithSummary("The CMDB settings: the prefix of item numbers. Needs cmdb.read.")
            .RequirePermission(Permissions.CmdbRead)
            .Produces<CmdbSettingsResponse>();

        cmdb.MapPut("/settings", PutSettingsAsync)
            .WithName("PutCmdbSettings")
            .WithSummary("Change the prefix of new item numbers (existing numbers keep theirs). Needs cmdb.manage; audited.")
            .RequirePermission(Permissions.CmdbManage)
            .Produces<CmdbSettingsResponse>()
            .ProducesValidationProblem();

        return group;
    }

    // --- Classes ---

    internal static async Task<Ok<List<CiClassResponse>>> ListClassesAsync(ZelvoDbContext db, CancellationToken cancellationToken)
    {
        var index = await CiClassIndex.LoadAsync(db, cancellationToken);
        var counts = await db.ConfigurationItems.GroupBy(i => i.ClassId).Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.Key, g => g.Count, cancellationToken);
        return TypedResults.Ok(index.Ordered().Select(c => CiClassResponse.From(c, index, counts.GetValueOrDefault(c.Id))).ToList());
    }

    internal static async Task<Results<Created<CiClassResponse>, ValidationProblem, ProblemHttpResult>> CreateClassAsync(
        CreateCiClassRequest request, ZelvoDbContext db, CancellationToken cancellationToken)
    {
        var name = DirectoryProblems.Clean(request.Name!);
        var description = Clean(request.Description);
        if (CheckNames(name, description) is { } invalid)
        {
            return invalid;
        }

        if (!FormsCommon.KeyPattern().IsMatch(request.Key!) || request.Key!.Length > CiClass.KeyMaxLength)
        {
            return UserEndpoints.Validation("key", "Keys are lower-case letters, digits and hyphens (at most 50), for example database-server.");
        }

        var index = await CiClassIndex.LoadAsync(db, cancellationToken);
        if (index.All.Any(c => c.Key == request.Key))
        {
            return FormsCommon.KeyTaken();
        }

        CiClass? parent = null;
        if (request.ParentId is { } parentId)
        {
            parent = index.Find(parentId);
            if (parent is null)
            {
                return UserEndpoints.Validation("parentId", "The parent class does not exist.");
            }

            if (parent.Depth >= CiClass.MaxDepth)
            {
                return (ProblemHttpResult)ApiProblem.Create(StatusCodes.Status409Conflict, "too-deep", $"Classes have at most {CiClass.MaxDepth} levels.");
            }
        }
        else if (request.Family is null)
        {
            return UserEndpoints.Validation("family", "A class at the top names its family.");
        }

        var family = parent?.Family ?? request.Family!.Value;
        var siblings = index.All.Where(c => c.ParentId == request.ParentId && (parent is not null || c.Family == family)).ToList();
        if (siblings.Any(s => DirectoryProblems.SameName(s.Name, name)))
        {
            return DirectoryProblems.NameTaken();
        }

        var fields = ParseAttributes(request.Attributes, out var parseProblem);
        if (parseProblem is not null)
        {
            return parseProblem;
        }

        var inherited = parent is null ? [] : index.Attributes(parent.Id).Select(a => a.Field.Key).ToHashSet(StringComparer.Ordinal);
        var problems = CmdbAttributes.Check(fields, inherited, new HashSet<string>(StringComparer.Ordinal));
        if (problems.Count > 0)
        {
            return FormsCommon.Problems("attributes-invalid", "The attributes have problems.", problems);
        }

        var ciClass = new CiClass
        {
            Key = request.Key,
            ParentId = parent?.Id,
            Depth = (parent?.Depth ?? 0) + 1,
            Family = family,
            Name = name,
            Description = description ?? new LocalizedText(null, null),
            Attributes = CmdbAttributes.Write(fields),
            RequesterVisible = request.RequesterVisible ?? false,
            Active = request.Active ?? true,
            Position = siblings.Count == 0 ? 0 : siblings.Max(s => s.Position) + 1,
        };
        db.CiClasses.Add(ciClass);
        await db.SaveChangesAsync(cancellationToken);
        index = await CiClassIndex.LoadAsync(db, cancellationToken);
        return TypedResults.Created($"/api/v1/cmdb/classes/{ciClass.Id}", CiClassResponse.From(index.Find(ciClass.Id)!, index, 0));
    }

    internal static async Task<Results<Ok<CiClassResponse>, ValidationProblem, ProblemHttpResult>> UpdateClassAsync(
        Guid id, UpdateCiClassRequest request, HttpContext http, ZelvoDbContext db, CancellationToken cancellationToken)
    {
        var ciClass = await db.CiClasses.SingleOrDefaultAsync(c => c.Id == id, cancellationToken);
        if (ciClass is null)
        {
            return FormsCommon.NotFound("class");
        }

        if (!FormsCommon.Matches(http, ciClass.Version))
        {
            return FormsCommon.VersionMismatch();
        }

        var index = await CiClassIndex.LoadAsync(db, cancellationToken);
        var siblings = index.All.Where(c => c.ParentId == ciClass.ParentId && c.Id != id && (ciClass.ParentId is not null || c.Family == ciClass.Family))
            .OrderBy(c => c.Position).ThenBy(c => c.Id).Select(c => c.Id).ToList();
        if (request.Name is not null)
        {
            var name = DirectoryProblems.Clean(request.Name);
            if (CheckNames(name, null) is { } invalid)
            {
                return invalid;
            }

            if (siblings.Select(index.Find).Any(s => s is not null && DirectoryProblems.SameName(s.Name, name)))
            {
                return DirectoryProblems.NameTaken();
            }

            ciClass.Name = name;
        }

        if (request.Description is not null)
        {
            var description = Clean(request.Description)!;
            if (CheckNames(null, description) is { } invalid)
            {
                return invalid;
            }

            ciClass.Description = description;
        }

        if (request.Attributes is not null)
        {
            var fields = ParseAttributes(request.Attributes, out var parseProblem);
            if (parseProblem is not null)
            {
                return parseProblem;
            }

            var inherited = ciClass.ParentId is { } parentId ? index.Attributes(parentId).Select(a => a.Field.Key).ToHashSet(StringComparer.Ordinal) : [];
            var below = index.WithDescendants(id).Where(c => c != id).Select(index.Find)
                .SelectMany(c => CmdbAttributes.Read(c!.Attributes)).Select(f => f.Key).ToHashSet(StringComparer.Ordinal);
            var problems = CmdbAttributes.Check(fields, inherited, below);
            if (problems.Count > 0)
            {
                return FormsCommon.Problems("attributes-invalid", "The attributes have problems.", problems);
            }

            // Values of removed attributes stay on items until they are next changed, then are dropped.
            ciClass.Attributes = CmdbAttributes.Write(fields);
        }

        if (request.RequesterVisible is { } visible)
        {
            ciClass.RequesterVisible = visible;
        }

        if (request.Active is { } active)
        {
            ciClass.Active = active;
        }

        if (request.Position is { } position)
        {
            siblings.Insert(Math.Clamp(position, 0, siblings.Count), id);
            var tracked = await db.CiClasses.Where(c => siblings.Contains(c.Id)).ToDictionaryAsync(c => c.Id, cancellationToken);
            for (var i = 0; i < siblings.Count; i++)
            {
                if (tracked[siblings[i]].Position != i)
                {
                    tracked[siblings[i]].Position = i;
                    tracked[siblings[i]].UpdatedAt = DateTimeOffset.UtcNow;
                }
            }
        }

        ciClass.UpdatedAt = DateTimeOffset.UtcNow;
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return FormsCommon.VersionMismatch();
        }

        index = await CiClassIndex.LoadAsync(db, cancellationToken);
        var count = await db.ConfigurationItems.CountAsync(i => i.ClassId == id, cancellationToken);
        var response = CiClassResponse.From(index.Find(id)!, index, count);
        http.Response.Headers.ETag = response.Etag;
        return TypedResults.Ok(response);
    }

    internal static async Task<Results<NoContent, ProblemHttpResult>> DeleteClassAsync(Guid id, ZelvoDbContext db, CancellationToken cancellationToken)
    {
        var ciClass = await db.CiClasses.SingleOrDefaultAsync(c => c.Id == id, cancellationToken);
        if (ciClass is null)
        {
            return FormsCommon.NotFound("class");
        }

        if (await db.CiClasses.AnyAsync(c => c.ParentId == id, cancellationToken))
        {
            return DirectoryProblems.InUse("Delete or move the classes below this one first, or make it inactive.");
        }

        if (await db.ConfigurationItems.AnyAsync(i => i.ClassId == id, cancellationToken))
        {
            return DirectoryProblems.InUse("Items of this class exist; make the class inactive instead.");
        }

        db.CiClasses.Remove(ciClass);
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.NoContent();
    }

    internal static async Task<Ok<List<CiClassResponse>>> AddStandardClassesAsync(ZelvoDbContext db, IAuditLog audit, CancellationToken cancellationToken)
    {
        var existing = await db.CiClasses.ToDictionaryAsync(c => c.Key, StringComparer.Ordinal, cancellationToken);
        var added = CmdbStandardClasses.AddMissing(db, existing).Select(c => c.Key).ToList();

        if (added.Count > 0)
        {
            audit.Add(AuditCategory.Admin, "cmdb.classes.standard-added", details: new Dictionary<string, object?>(StringComparer.Ordinal) { ["keys"] = added.ToArray() });
            await db.SaveChangesAsync(cancellationToken);
        }

        return await ListClassesAsync(db, cancellationToken);
    }

    // --- Relationship types ---

    internal static async Task<Ok<List<CiRelationshipTypeResponse>>> ListTypesAsync(ZelvoDbContext db, CancellationToken cancellationToken)
    {
        await CmdbRelationshipDefaults.EnsureAsync(db, cancellationToken);
        var types = await db.CiRelationshipTypes.AsNoTracking().ToListAsync(cancellationToken);
        var counts = await db.CiRelationships.GroupBy(r => r.TypeId).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(g => g.Key, g => g.Count, cancellationToken);
        return TypedResults.Ok(types.OrderByDescending(t => t.BuiltIn).ThenBy(t => t.BuiltIn ? BuiltInOrder(t.Key) : 0).ThenBy(t => t.OutgoingName.En ?? t.OutgoingName.Ar, StringComparer.CurrentCulture)
            .Select(t => CiRelationshipTypeResponse.From(t, counts.GetValueOrDefault(t.Id))).ToList());
    }

    internal static async Task<Results<Created<CiRelationshipTypeResponse>, ValidationProblem, ProblemHttpResult>> CreateTypeAsync(
        CreateCiRelationshipTypeRequest request, ZelvoDbContext db, CancellationToken cancellationToken)
    {
        if (!FormsCommon.KeyPattern().IsMatch(request.Key!) || request.Key!.Length > CiRelationshipType.KeyMaxLength)
        {
            return UserEndpoints.Validation("key", "Keys are lower-case letters, digits and hyphens (at most 50), for example backs-up.");
        }

        var outgoing = DirectoryProblems.Clean(request.OutgoingName!);
        var incoming = DirectoryProblems.Clean(request.IncomingName!);
        if (!DirectoryProblems.Fits(outgoing, CiRelationshipType.NameMaxLength) || !DirectoryProblems.Fits(incoming, CiRelationshipType.NameMaxLength))
        {
            return UserEndpoints.Validation("outgoingName", $"Use at most {CiRelationshipType.NameMaxLength} characters in each language.");
        }

        await CmdbRelationshipDefaults.EnsureAsync(db, cancellationToken);
        var types = await db.CiRelationshipTypes.ToListAsync(cancellationToken);
        if (types.Any(t => t.Key == request.Key))
        {
            return FormsCommon.KeyTaken();
        }

        if (types.Any(t => DirectoryProblems.SameName(t.OutgoingName, outgoing)))
        {
            return DirectoryProblems.NameTaken();
        }

        var type = new CiRelationshipType
        {
            Key = request.Key,
            OutgoingName = outgoing,
            IncomingName = incoming,
            Impact = request.Impact ?? ImpactDirection.ToSource,
            Active = request.Active ?? true,
        };
        db.CiRelationshipTypes.Add(type);
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Created($"/api/v1/cmdb/relationship-types/{type.Id}", CiRelationshipTypeResponse.From(type, 0));
    }

    internal static async Task<Results<Ok<CiRelationshipTypeResponse>, ValidationProblem, ProblemHttpResult>> UpdateTypeAsync(
        Guid id, UpdateCiRelationshipTypeRequest request, ZelvoDbContext db, CancellationToken cancellationToken)
    {
        var type = await db.CiRelationshipTypes.SingleOrDefaultAsync(t => t.Id == id, cancellationToken);
        if (type is null)
        {
            return FormsCommon.NotFound("relationship-type");
        }

        if (request.OutgoingName is not null)
        {
            var outgoing = DirectoryProblems.Clean(request.OutgoingName);
            if (!DirectoryProblems.Fits(outgoing, CiRelationshipType.NameMaxLength))
            {
                return UserEndpoints.Validation("outgoingName", $"Use at most {CiRelationshipType.NameMaxLength} characters in each language.");
            }

            if ((await db.CiRelationshipTypes.Where(t => t.Id != id).ToListAsync(cancellationToken)).Any(t => DirectoryProblems.SameName(t.OutgoingName, outgoing)))
            {
                return DirectoryProblems.NameTaken();
            }

            type.OutgoingName = outgoing;
        }

        if (request.IncomingName is not null)
        {
            var incoming = DirectoryProblems.Clean(request.IncomingName);
            if (!DirectoryProblems.Fits(incoming, CiRelationshipType.NameMaxLength))
            {
                return UserEndpoints.Validation("incomingName", $"Use at most {CiRelationshipType.NameMaxLength} characters in each language.");
            }

            type.IncomingName = incoming;
        }

        if (request.Impact is { } impact && impact != type.Impact)
        {
            if (type.BuiltIn)
            {
                return (ProblemHttpResult)ApiProblem.Create(StatusCodes.Status409Conflict, "built-in", "Built-in types keep how faults travel; add a type of your own instead.");
            }

            type.Impact = impact;
        }

        if (request.Active is { } active)
        {
            type.Active = active;
        }

        type.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        var count = await db.CiRelationships.CountAsync(r => r.TypeId == id, cancellationToken);
        return TypedResults.Ok(CiRelationshipTypeResponse.From(type, count));
    }

    internal static async Task<Results<NoContent, ProblemHttpResult>> DeleteTypeAsync(Guid id, ZelvoDbContext db, CancellationToken cancellationToken)
    {
        var type = await db.CiRelationshipTypes.SingleOrDefaultAsync(t => t.Id == id, cancellationToken);
        if (type is null)
        {
            return FormsCommon.NotFound("relationship-type");
        }

        if (type.BuiltIn)
        {
            return (ProblemHttpResult)ApiProblem.Create(StatusCodes.Status409Conflict, "built-in", "Built-in types cannot be deleted; make them inactive instead.");
        }

        if (await db.CiRelationships.AnyAsync(r => r.TypeId == id, cancellationToken))
        {
            return DirectoryProblems.InUse("Relationships of this type exist; make it inactive instead.");
        }

        db.CiRelationshipTypes.Remove(type);
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.NoContent();
    }

    // --- Settings ---

    internal static async Task<Ok<CmdbSettingsResponse>> GetSettingsAsync(TenantSettingsStore settings, CancellationToken cancellationToken) =>
        TypedResults.Ok(new CmdbSettingsResponse(await settings.GetAsync(CmdbSettings.NumberPrefix, cancellationToken)));

    internal static async Task<Results<Ok<CmdbSettingsResponse>, ValidationProblem>> PutSettingsAsync(
        CmdbSettingsRequest request, TenantSettingsStore settings, ZelvoDbContext db, CancellationToken cancellationToken)
    {
        if (!Knowledge.KnowledgeSettings.IsPrefix(request.NumberPrefix))
        {
            return UserEndpoints.Validation("numberPrefix", "1 to 10 capital letters or digits, starting with a letter.");
        }

        await settings.SetAsync(CmdbSettings.NumberPrefix, request.NumberPrefix!, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Ok(new CmdbSettingsResponse(request.NumberPrefix!));
    }

    // --- Helpers ---

    private static int BuiltInOrder(string key) => key switch
    {
        CmdbRelationshipDefaults.RunsOn => 0,
        CmdbRelationshipDefaults.DependsOn => 1,
        CmdbRelationshipDefaults.ConnectsTo => 2,
        _ => 3,
    };

    internal static LocalizedText? Clean(CiDescriptionInput? input) =>
        input is null ? null : new(input.En?.Trim() is { Length: > 0 } en ? en : null, input.Ar?.Trim() is { Length: > 0 } ar ? ar : null);

    private static ValidationProblem? CheckNames(LocalizedText? name, LocalizedText? description)
    {
        if (name is not null && (!name.HasAnyValue || !DirectoryProblems.Fits(name, CiClass.NameMaxLength)))
        {
            return UserEndpoints.Validation("name", $"Give a name of at most {CiClass.NameMaxLength} characters in English or Arabic.");
        }

        if (description is not null && !DirectoryProblems.Fits(description, CiClass.DescriptionMaxLength))
        {
            return UserEndpoints.Validation("description", $"Use at most {CiClass.DescriptionMaxLength} characters in each language.");
        }

        return null;
    }

    private static List<FormField> ParseAttributes(System.Text.Json.JsonElement? input, out ValidationProblem? problem)
    {
        problem = null;
        if (input is not { ValueKind: System.Text.Json.JsonValueKind.Array } array)
        {
            if (input is { ValueKind: not (System.Text.Json.JsonValueKind.Null or System.Text.Json.JsonValueKind.Undefined) })
            {
                problem = UserEndpoints.Validation("attributes", "The attributes are not a list of fields.");
            }

            return [];
        }

        var raw = array.GetRawText();
        if (raw.Length > Data.Forms.Form.SchemaMaxLength)
        {
            problem = UserEndpoints.Validation("attributes", "The attributes are too large.");
            return [];
        }

        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<List<FormField>>(raw, FormJson.Options) ?? [];
        }
        catch (System.Text.Json.JsonException)
        {
            problem = UserEndpoints.Validation("attributes", "The attributes are not a list of fields.");
            return [];
        }
    }
}

/// <summary>A bilingual description (either language may be empty).</summary>
public sealed class CiDescriptionInput
{
    /// <summary>English.</summary>
    [StringLength(ConfigurationItem.DescriptionMaxLength)]
    public string? En { get; init; }

    /// <summary>Arabic.</summary>
    [StringLength(ConfigurationItem.DescriptionMaxLength)]
    public string? Ar { get; init; }
}

/// <summary>A new class.</summary>
public sealed class CreateCiClassRequest
{
    /// <summary>Stable key, unique in the tenant: lower-case letters, digits and hyphens.</summary>
    [Required, StringLength(CiClass.KeyMaxLength)]
    public string? Key { get; init; }

    /// <summary>Name in English and Arabic.</summary>
    [Required]
    public LocalizedTextInput? Name { get; init; }

    /// <summary>What the class is for.</summary>
    public CiDescriptionInput? Description { get; init; }

    /// <summary>The class above; leave out for a class at the top of its family.</summary>
    public Guid? ParentId { get; init; }

    /// <summary>Family of a class at the top (sub-classes take their parent's).</summary>
    public CiFamily? Family { get; init; }

    /// <summary>The class's own attributes: C1 fields (text, number, date, dropdown, checkbox, radio, user, location).</summary>
    public System.Text.Json.JsonElement? Attributes { get; init; }

    /// <summary>Requesters may pick items of this class in forms (default no).</summary>
    public bool? RequesterVisible { get; init; }

    /// <summary>Active (default yes).</summary>
    public bool? Active { get; init; }
}

/// <summary>Changes to a class; what is left out stays.</summary>
public sealed class UpdateCiClassRequest
{
    /// <summary>New name.</summary>
    public LocalizedTextInput? Name { get; init; }

    /// <summary>New description (empty languages clear it).</summary>
    public CiDescriptionInput? Description { get; init; }

    /// <summary>The class's own attributes, all of them.</summary>
    public System.Text.Json.JsonElement? Attributes { get; init; }

    /// <summary>Requesters may pick items of this class.</summary>
    public bool? RequesterVisible { get; init; }

    /// <summary>Active or inactive.</summary>
    public bool? Active { get; init; }

    /// <summary>New place among its siblings (0 first).</summary>
    [Range(0, 1000)]
    public int? Position { get; init; }
}

/// <summary>A class with what an item of it has.</summary>
/// <param name="Attributes">The class's own attributes.</param>
/// <param name="InheritedAttributes">Attributes of the classes above, top first.</param>
/// <param name="Selectable">New items can be added (the class and those above are active).</param>
/// <param name="ItemCount">Items of exactly this class.</param>
/// <param name="Etag">Send as If-Match with changes.</param>
internal sealed record CiClassResponse(
    Guid Id, string Key, Guid? ParentId, int Depth, CiFamily Family, LocalizedTextDto Name, LocalizedTextDto Description, LocalizedTextDto Path,
    JsonNode? Attributes, IReadOnlyList<InheritedAttribute> InheritedAttributes, bool RequesterVisible, bool Active, bool Selectable, int ItemCount, string Etag)
{
    public static CiClassResponse From(CiClass c, CiClassIndex index, int itemCount) => new(
        c.Id, c.Key, c.ParentId, c.Depth, c.Family, LocalizedTextDto.From(c.Name), LocalizedTextDto.From(c.Description), LocalizedTextDto.From(index.Path(c.Id)),
        JsonNode.Parse(c.Attributes),
        c.ParentId is { } p
            ? [.. index.Attributes(p).Select(a => new InheritedAttribute(a.ClassId, LocalizedTextDto.From(index.Find(a.ClassId)!.Name), System.Text.Json.JsonSerializer.SerializeToNode(a.Field, FormJson.Options)))]
            : [],
        c.RequesterVisible, c.Active, index.Selectable(c.Id), itemCount, $"\"{c.Version}\"");
}

/// <summary>An attribute an item gets from a class above its own.</summary>
internal sealed record InheritedAttribute(Guid ClassId, LocalizedTextDto ClassName, JsonNode? Field);

/// <summary>A new relationship type between items.</summary>
public sealed class CreateCiRelationshipTypeRequest
{
    /// <summary>Stable key, unique in the tenant.</summary>
    [Required, StringLength(CiRelationshipType.KeyMaxLength)]
    public string? Key { get; init; }

    /// <summary>Seen from the source, for example "backs up".</summary>
    [Required]
    public LocalizedTextInput? OutgoingName { get; init; }

    /// <summary>Seen from the target, for example "backed up by".</summary>
    [Required]
    public LocalizedTextInput? IncomingName { get; init; }

    /// <summary>Which way faults travel (default: a fault of the target affects the source).</summary>
    public ImpactDirection? Impact { get; init; }

    /// <summary>Active (default yes).</summary>
    public bool? Active { get; init; }
}

/// <summary>Changes to a relationship type.</summary>
public sealed class UpdateCiRelationshipTypeRequest
{
    /// <summary>New name seen from the source.</summary>
    public LocalizedTextInput? OutgoingName { get; init; }

    /// <summary>New name seen from the target.</summary>
    public LocalizedTextInput? IncomingName { get; init; }

    /// <summary>Which way faults travel (not for built-in types).</summary>
    public ImpactDirection? Impact { get; init; }

    /// <summary>Active or inactive.</summary>
    public bool? Active { get; init; }
}

/// <summary>A relationship type.</summary>
internal sealed record CiRelationshipTypeResponse(
    Guid Id, string Key, LocalizedTextDto OutgoingName, LocalizedTextDto IncomingName, RelationshipTargetKind TargetKind,
    ImpactDirection Impact, bool BuiltIn, bool Active, int RelationshipCount)
{
    public static CiRelationshipTypeResponse From(CiRelationshipType t, int count) => new(
        t.Id, t.Key, LocalizedTextDto.From(t.OutgoingName), LocalizedTextDto.From(t.IncomingName), t.TargetKind, t.Impact, t.BuiltIn, t.Active, count);
}

/// <summary>CMDB settings.</summary>
internal sealed record CmdbSettingsResponse(string NumberPrefix);

/// <summary>New CMDB settings.</summary>
public sealed class CmdbSettingsRequest
{
    /// <summary>Prefix of new item numbers: 1–10 capital letters or digits, starting with a letter.</summary>
    [Required, StringLength(10)]
    public string? NumberPrefix { get; init; }
}
