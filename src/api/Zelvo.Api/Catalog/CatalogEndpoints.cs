using System.ComponentModel.DataAnnotations;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Zelvo.Api.Conventions;
using Zelvo.Api.Forms;
using Zelvo.Api.Identity;
using Zelvo.Api.Tenancy;
using Zelvo.Data;
using Zelvo.Data.Catalog;
using Zelvo.Search;

namespace Zelvo.Api.Catalog;

/// <summary>
/// Catalogs (task C2, module 2): one per department or audience, with editors who may write that
/// catalog's services (product owner decision 2026-11-24). Holders of catalog.manage change them;
/// everyone who works on the catalog sees them. Every change is audited.
/// </summary>
internal static partial class CatalogEndpoints
{
    public static RouteGroupBuilder MapCatalogEndpoints(this RouteGroupBuilder group)
    {
        var catalogs = group.MapGroup("/catalog/catalogs").WithTags("Catalog").RequireTenant();
        catalogs.MapGet("/", ListAsync).WithName("ListCatalogs")
            .WithSummary("Every catalog with its editors and number of services, in order. For holders of catalog.manage or catalog.publish and for catalog editors.")
            .RequireAuthorization().Produces<IReadOnlyList<CatalogResponse>>();
        catalogs.MapPost("/", CreateAsync).WithName("CreateCatalog")
            .WithSummary("Add a catalog with a unique key, a bilingual name and description, an owning department and editors (users or groups). Needs catalog.manage; audited.")
            .RequirePermission(Permissions.CatalogManage).Produces<CatalogResponse>(StatusCodes.Status201Created).ProducesValidationProblem();
        catalogs.MapPut("/{id:guid}", UpdateAsync).WithName("UpdateCatalog")
            .WithSummary("Change a catalog, its editors, order and whether it is active (inactive catalogs are not shown to requesters). If-Match optional. Needs catalog.manage; audited.")
            .RequirePermission(Permissions.CatalogManage).Produces<CatalogResponse>().ProducesValidationProblem();
        catalogs.MapDelete("/{id:guid}", DeleteAsync).WithName("DeleteCatalog")
            .WithSummary("Delete a catalog no service is in; otherwise make it inactive. Needs catalog.manage; audited.")
            .RequirePermission(Permissions.CatalogManage).Produces(StatusCodes.Status204NoContent);
        return group;
    }

    internal static async Task<IResult> ListAsync(ZelvoDbContext db, CurrentUser user, CancellationToken cancellationToken)
    {
        var edited = await CatalogAccess.EditedCatalogsAsync(db, user, cancellationToken);
        if (!CatalogAccess.IsManager(user) && !user.Has(Permissions.CatalogPublish) && edited.Count == 0)
        {
            return Forbidden();
        }

        var catalogs = await db.ServiceCatalogs.AsNoTracking().Include(c => c.Editors).OrderBy(c => c.Position).ThenBy(c => c.Key).ToListAsync(cancellationToken);
        var services = await db.CatalogServices.AsNoTracking().Where(s => s.State != ServiceState.Retired).Select(s => s.EditorCatalogIds).ToListAsync(cancellationToken);
        var names = await NamesAsync(db, catalogs.SelectMany(c => c.Editors), cancellationToken);
        return TypedResults.Ok(catalogs.Select(c => CatalogResponse.From(c, services.Count(s => s.Contains(c.Id)), names, edited.Contains(c.Id))).ToList());
    }

    internal static async Task<IResult> CreateAsync(CatalogRequest request, ZelvoDbContext db, CurrentUser user, CancellationToken cancellationToken)
    {
        var problem = await CheckAsync(db, request, null, cancellationToken);
        if (problem is not null)
        {
            return problem;
        }

        var catalog = new ServiceCatalog
        {
            Key = request.Key!.Trim(),
            Name = FormsCommon.Clean(request.Name)!,
            Description = FormsCommon.Clean(request.Description) ?? new(null, null),
            DepartmentId = request.DepartmentId,
            Position = request.Position ?? (await db.ServiceCatalogs.MaxAsync(c => (int?)c.Position, cancellationToken) ?? 0) + 1,
            Active = request.Active ?? true,
        };
        catalog.Editors.AddRange(EditorsOf(request, catalog.Id));
        db.ServiceCatalogs.Add(catalog);
        await db.SaveChangesAsync(cancellationToken);
        var names = await NamesAsync(db, catalog.Editors, cancellationToken);
        return TypedResults.Created($"/api/v1/catalog/catalogs/{catalog.Id}", CatalogResponse.From(catalog, 0, names, false));
    }

    internal static async Task<IResult> UpdateAsync(
        Guid id, CatalogRequest request, HttpContext http, ZelvoDbContext db, ISearchIndexer indexer, CancellationToken cancellationToken)
    {
        var catalog = await db.ServiceCatalogs.Include(c => c.Editors).SingleOrDefaultAsync(c => c.Id == id, cancellationToken);
        if (catalog is null)
        {
            return FormsCommon.NotFound("catalog");
        }

        if (!FormsCommon.Matches(http, catalog.Version))
        {
            return FormsCommon.VersionMismatch();
        }

        db.Entry(catalog).Property(c => c.Version).OriginalValue = catalog.Version;
        var problem = await CheckAsync(db, request, id, cancellationToken);
        if (problem is not null)
        {
            return problem;
        }

        var activeChanged = catalog.Active != (request.Active ?? catalog.Active);
        catalog.Key = request.Key!.Trim();
        catalog.Name = FormsCommon.Clean(request.Name)!;
        catalog.Description = FormsCommon.Clean(request.Description) ?? new(null, null);
        catalog.DepartmentId = request.DepartmentId;
        catalog.Position = request.Position ?? catalog.Position;
        catalog.Active = request.Active ?? catalog.Active;
        catalog.UpdatedAt = DateTimeOffset.UtcNow;
        var wanted = EditorsOf(request, catalog.Id).ToList();
        db.CatalogEditors.RemoveRange(catalog.Editors.Where(e => !wanted.Any(w => w.UserId == e.UserId && w.GroupId == e.GroupId)));
        foreach (var editor in wanted.Where(w => !catalog.Editors.Any(e => e.UserId == w.UserId && e.GroupId == w.GroupId)))
        {
            db.CatalogEditors.Add(editor);
        }

        if (activeChanged)
        {
            // Services listed only through this catalog appear in or leave the index.
            var inCatalog = await db.CatalogServices.Where(s => s.CatalogIds.Contains(id)).ToListAsync(cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            foreach (var service in inCatalog)
            {
                await CatalogSearch.IndexAsync(db, indexer, service, cancellationToken);
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        var editors = await db.CatalogEditors.AsNoTracking().Where(e => e.CatalogId == id).ToListAsync(cancellationToken);
        var names = await NamesAsync(db, editors, cancellationToken);
        var count = await db.CatalogServices.CountAsync(s => s.State != ServiceState.Retired && s.EditorCatalogIds.Contains(id), cancellationToken);
        var fresh = await db.ServiceCatalogs.AsNoTracking().Include(c => c.Editors).SingleAsync(c => c.Id == id, cancellationToken);
        return TypedResults.Ok(CatalogResponse.From(fresh, count, names, false));
    }

    internal static async Task<IResult> DeleteAsync(Guid id, ZelvoDbContext db, CancellationToken cancellationToken)
    {
        var catalog = await db.ServiceCatalogs.SingleOrDefaultAsync(c => c.Id == id, cancellationToken);
        if (catalog is null)
        {
            return FormsCommon.NotFound("catalog");
        }

        if (await db.CatalogServices.AnyAsync(s => s.EditorCatalogIds.Contains(id) || s.CatalogIds.Contains(id), cancellationToken))
        {
            return ApiProblem.Create(StatusCodes.Status409Conflict, "catalog-in-use", "Services are in this catalog. Make it inactive instead.");
        }

        db.ServiceCatalogs.Remove(catalog);
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.NoContent();
    }

    internal static IResult Forbidden() =>
        ApiProblem.Create(StatusCodes.Status403Forbidden, "forbidden", "Only catalog managers, publishers and catalog editors work on the catalog.");

    private static IEnumerable<CatalogEditor> EditorsOf(CatalogRequest request, Guid catalogId) =>
        (request.Editors ?? []).Select(e => new CatalogEditor { CatalogId = catalogId, UserId = e.UserId, GroupId = e.UserId is null ? e.GroupId : null })
            .DistinctBy(e => (e.UserId, e.GroupId));

    private static async Task<IResult?> CheckAsync(ZelvoDbContext db, CatalogRequest request, Guid? id, CancellationToken cancellationToken)
    {
        var key = request.Key?.Trim();
        if (key is null || !KeyPattern().IsMatch(key))
        {
            return UserEndpoints.Validation("key", "Use 1–60 lower-case letters, digits and hyphens, for example it-services.");
        }

        if (await db.ServiceCatalogs.AnyAsync(c => c.Key == key && c.Id != id, cancellationToken))
        {
            return FormsCommon.KeyTaken();
        }

        var name = FormsCommon.Clean(request.Name);
        if (name is null || !name.HasAnyValue || !ServiceDesk.DirectoryProblems.Fits(name, ServiceCatalog.NameMaxLength))
        {
            return UserEndpoints.Validation("name", $"Name the catalog in English or Arabic, at most {ServiceCatalog.NameMaxLength} characters.");
        }

        if (FormsCommon.Clean(request.Description) is { } description && !ServiceDesk.DirectoryProblems.Fits(description, ServiceCatalog.DescriptionMaxLength))
        {
            return UserEndpoints.Validation("description", $"Use at most {ServiceCatalog.DescriptionMaxLength} characters in each language.");
        }

        if (request.DepartmentId is { } department && !await db.Departments.AnyAsync(d => d.Id == department, cancellationToken))
        {
            return UserEndpoints.Validation("departmentId", "Choose a department.");
        }

        var editors = request.Editors ?? [];
        if (editors.Count > 100 || editors.Any(e => (e.UserId is null) == (e.GroupId is null)))
        {
            return UserEndpoints.Validation("editors", "Up to 100 editors, each a user or a group.");
        }

        var users = editors.Where(e => e.UserId is not null).Select(e => e.UserId!.Value).Distinct().ToList();
        var groups = editors.Where(e => e.GroupId is not null).Select(e => e.GroupId!.Value).Distinct().ToList();
        if (await db.Users.CountAsync(u => users.Contains(u.Id), cancellationToken) != users.Count
            || await db.SupportGroups.CountAsync(g => groups.Contains(g.Id), cancellationToken) != groups.Count)
        {
            return UserEndpoints.Validation("editors", "An editor is not a user or group of this organisation.");
        }

        return null;
    }

    internal static async Task<Dictionary<Guid, string?>> NamesAsync(ZelvoDbContext db, IEnumerable<CatalogEditor> editors, CancellationToken cancellationToken)
    {
        var list = editors.ToList();
        var userIds = list.Where(e => e.UserId is not null).Select(e => e.UserId!.Value).Distinct().ToList();
        var groupIds = list.Where(e => e.GroupId is not null).Select(e => e.GroupId!.Value).Distinct().ToList();
        var names = await db.Users.AsNoTracking().Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, cancellationToken);
        foreach (var g in await db.SupportGroups.AsNoTracking().Where(g => groupIds.Contains(g.Id)).ToListAsync(cancellationToken))
        {
            names[g.Id] = g.Name.En ?? g.Name.Ar;
        }

        return names;
    }

    [GeneratedRegex("^[a-z0-9]([a-z0-9-]{0,58}[a-z0-9])?$", RegexOptions.CultureInvariant)]
    private static partial Regex KeyPattern();
}

/// <summary>An editor of a catalog: a user or a group.</summary>
internal sealed record CatalogEditorInput(Guid? UserId, Guid? GroupId);

/// <summary>A catalog to create or change.</summary>
internal sealed class CatalogRequest
{
    /// <summary>Unique key: lower-case letters, digits and hyphens.</summary>
    [Required, StringLength(ServiceCatalog.KeyMaxLength)]
    public string? Key { get; init; }

    /// <summary>Name in English and Arabic.</summary>
    [Required]
    public LocalizedTextInput? Name { get; init; }

    /// <summary>What it offers and to whom.</summary>
    public LocalizedTextInput? Description { get; init; }

    /// <summary>Owning department.</summary>
    public Guid? DepartmentId { get; init; }

    /// <summary>Order in lists.</summary>
    [Range(0, 10_000)]
    public int? Position { get; init; }

    /// <summary>Shown to requesters.</summary>
    public bool? Active { get; init; }

    /// <summary>Users and groups who write this catalog's services.</summary>
    public IReadOnlyList<CatalogEditorInput>? Editors { get; init; }
}

/// <summary>An editor as shown.</summary>
internal sealed record CatalogEditorResponse(Guid? UserId, Guid? GroupId, string? Name);

/// <summary>A catalog.</summary>
internal sealed record CatalogResponse(
    Guid Id, string Key, LocalizedTextDto Name, LocalizedTextDto Description, Guid? DepartmentId, int Position, bool Active,
    IReadOnlyList<CatalogEditorResponse> Editors, int ServiceCount, bool YouEdit, string Etag, DateTimeOffset UpdatedAt)
{
    public static CatalogResponse From(ServiceCatalog c, int services, IReadOnlyDictionary<Guid, string?> names, bool youEdit) =>
        new(c.Id, c.Key, LocalizedTextDto.From(c.Name), LocalizedTextDto.From(c.Description), c.DepartmentId, c.Position, c.Active,
            [.. c.Editors.Select(e => new CatalogEditorResponse(e.UserId, e.GroupId, names.GetValueOrDefault(e.UserId ?? e.GroupId ?? Guid.Empty)))],
            services, youEdit, $"\"{c.Version}\"", c.UpdatedAt);
}
