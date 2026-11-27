using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Zelvo.Api.Cmdb;
using Zelvo.Api.Conventions;
using Zelvo.Api.Forms;
using Zelvo.Api.Identity;
using Zelvo.Api.Settings;
using Zelvo.Api.Tenancy;
using Zelvo.Data;
using Zelvo.Data.Cmdb;
using Zelvo.Data.Identity;

namespace Zelvo.Api.Assets;

/// <summary>
/// Contracts and warranties on assets, software licences, the discovered software they are matched
/// with and licence compliance (task G3, module 18). <c>assets.read</c> sees them;
/// <c>assets.manage</c> changes them; every change is audited.
/// </summary>
internal static class AssetContractEndpoints
{
    /// <summary>Most assets on one contract.</summary>
    public const int MaxContractItems = 1000;

    public static RouteGroupBuilder MapAssetContractEndpoints(this RouteGroupBuilder group)
    {
        var assets = group.MapGroup("/assets").WithTags("Assets").RequireTenant();

        assets.MapGet("/contracts", ListContractsAsync)
            .WithName("ListAssetContracts")
            .WithSummary("Contracts and warranties, soonest end first, with how many assets each covers; expiring=true for those ending within the warning period. Needs assets.read.")
            .RequirePermission(Permissions.AssetsRead)
            .Produces<IReadOnlyList<ContractResponse>>();

        assets.MapPost("/contracts", CreateContractAsync)
            .WithName("CreateAssetContract")
            .WithSummary("Add a contract or warranty (kind, name, reference, supplier, dates, cost, owner, the assets it covers). Needs assets.manage; audited.")
            .RequirePermission(Permissions.AssetsManage)
            .Produces<ContractResponse>(StatusCodes.Status201Created)
            .ProducesValidationProblem();

        assets.MapGet("/contracts/{id:guid}", GetContractAsync)
            .WithName("GetAssetContract")
            .WithSummary("A contract with the assets it covers. Needs assets.read.")
            .RequirePermission(Permissions.AssetsRead)
            .Produces<ContractResponse>();

        assets.MapPatch("/contracts/{id:guid}", UpdateContractAsync)
            .WithName("UpdateAssetContract")
            .WithSummary("Change a contract; itemIds replaces the assets it covers. A new end date gets a new expiry alert. Send the ETag as If-Match. Needs assets.manage; audited.")
            .RequirePermission(Permissions.AssetsManage)
            .Produces<ContractResponse>()
            .ProducesValidationProblem();

        assets.MapDelete("/contracts/{id:guid}", DeleteContractAsync)
            .WithName("DeleteAssetContract")
            .WithSummary("Remove a contract. Needs assets.manage; audited.")
            .RequirePermission(Permissions.AssetsManage)
            .Produces(StatusCodes.Status204NoContent);

        assets.MapGet("/licenses", ListLicensesAsync)
            .WithName("ListSoftwareLicenses")
            .WithSummary("Software licences with compliance: devices and people using the products found installed, used, shortfall and spare. Needs assets.read.")
            .RequirePermission(Permissions.AssetsRead)
            .Produces<IReadOnlyList<LicenseResponse>>();

        assets.MapPost("/licenses", CreateLicenseAsync)
            .WithName("CreateSoftwareLicense")
            .WithSummary("Add a software licence: name, the discovered products it covers, per device or per person, quantity, dates, supplier, cost and owner. Needs assets.manage; audited.")
            .RequirePermission(Permissions.AssetsManage)
            .Produces<LicenseResponse>(StatusCodes.Status201Created)
            .ProducesValidationProblem();

        assets.MapPatch("/licenses/{id:guid}", UpdateLicenseAsync)
            .WithName("UpdateSoftwareLicense")
            .WithSummary("Change a licence. Send the ETag as If-Match. Needs assets.manage; audited.")
            .RequirePermission(Permissions.AssetsManage)
            .Produces<LicenseResponse>()
            .ProducesValidationProblem();

        assets.MapDelete("/licenses/{id:guid}", DeleteLicenseAsync)
            .WithName("DeleteSoftwareLicense")
            .WithSummary("Remove a licence. Needs assets.manage; audited.")
            .RequirePermission(Permissions.AssetsManage)
            .Produces(StatusCodes.Status204NoContent);

        assets.MapGet("/software", SoftwareAsync)
            .WithName("ListDiscoveredSoftware")
            .WithSummary("The products CMDB discovery found installed, with how many devices have each and the licences that cover it, for choosing a licence's products. Needs assets.read.")
            .RequirePermission(Permissions.AssetsRead)
            .Produces<PageResult<SoftwareProductResponse>>();

        return group;
    }

    // --- Contracts ---

    internal static async Task<Ok<List<ContractResponse>>> ListContractsAsync(
        [AsParameters] ContractQuery filter, ZelvoDbContext db, TenantSettingsStore settings, TimeProvider time, CancellationToken cancellationToken)
    {
        var query = db.AssetContracts.AsNoTracking();
        if (filter.Expiring == true)
        {
            var today = Today(time);
            var until = today.AddDays(await settings.GetAsync(AssetSettings.ExpiryWarningDays, cancellationToken));
            query = query.Where(c => c.EndsOn >= today && c.EndsOn <= until);
        }

        if (filter.Kind is { } kind)
        {
            query = query.Where(c => c.Kind == kind);
        }

        var contracts = await query.OrderBy(c => c.EndsOn).ThenBy(c => c.Name).Take(500).ToListAsync(cancellationToken);
        var ids = contracts.Select(c => c.Id).ToList();
        var counts = await db.AssetContractItems.AsNoTracking().Where(i => ids.Contains(i.ContractId)).GroupBy(i => i.ContractId)
            .Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(g => g.Key, g => g.Count, cancellationToken);
        var owners = await AssetEndpoints.NamesAsync(db, contracts.Select(c => c.OwnerId), cancellationToken);
        return TypedResults.Ok(contracts.Select(c => ContractResponse.From(c, counts.GetValueOrDefault(c.Id), [], owners)).ToList());
    }

    internal static async Task<Results<Created<ContractResponse>, ValidationProblem, ProblemHttpResult>> CreateContractAsync(
        ContractRequest request, ZelvoDbContext db, CancellationToken cancellationToken)
    {
        var contract = new AssetContract { Name = request.Name?.Trim() ?? string.Empty, EndsOn = request.EndsOn ?? DateOnly.MinValue };
        if (await ApplyAsync(contract, request, creating: true, db, cancellationToken) is { } invalid)
        {
            return invalid;
        }

        db.AssetContracts.Add(contract);
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Created($"/api/v1/assets/contracts/{contract.Id}", await LoadContractAsync(db, contract.Id, cancellationToken));
    }

    internal static async Task<Results<Ok<ContractResponse>, ProblemHttpResult>> GetContractAsync(Guid id, HttpContext http, ZelvoDbContext db, CancellationToken cancellationToken)
    {
        if (!await db.AssetContracts.AnyAsync(c => c.Id == id, cancellationToken))
        {
            return FormsCommon.NotFound("contract");
        }

        var response = await LoadContractAsync(db, id, cancellationToken);
        http.Response.Headers.ETag = response.Etag;
        return TypedResults.Ok(response);
    }

    internal static async Task<Results<Ok<ContractResponse>, ValidationProblem, ProblemHttpResult>> UpdateContractAsync(
        Guid id, ContractRequest request, HttpContext http, ZelvoDbContext db, CancellationToken cancellationToken)
    {
        var contract = await db.AssetContracts.SingleOrDefaultAsync(c => c.Id == id, cancellationToken);
        if (contract is null)
        {
            return FormsCommon.NotFound("contract");
        }

        if (!FormsCommon.Matches(http, contract.Version))
        {
            return FormsCommon.VersionMismatch();
        }

        if (await ApplyAsync(contract, request, creating: false, db, cancellationToken) is { } invalid)
        {
            return invalid;
        }

        contract.UpdatedAt = DateTimeOffset.UtcNow;
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return FormsCommon.VersionMismatch();
        }

        var response = await LoadContractAsync(db, id, cancellationToken);
        http.Response.Headers.ETag = response.Etag;
        return TypedResults.Ok(response);
    }

    internal static async Task<Results<NoContent, ProblemHttpResult>> DeleteContractAsync(Guid id, ZelvoDbContext db, CancellationToken cancellationToken)
    {
        var contract = await db.AssetContracts.SingleOrDefaultAsync(c => c.Id == id, cancellationToken);
        if (contract is null)
        {
            return FormsCommon.NotFound("contract");
        }

        db.AssetContractItems.RemoveRange(await db.AssetContractItems.Where(i => i.ContractId == id).ToListAsync(cancellationToken));
        db.AssetContracts.Remove(contract);
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.NoContent();
    }

    private static async Task<ProblemHttpResult?> ApplyAsync(AssetContract contract, ContractRequest request, bool creating, ZelvoDbContext db, CancellationToken cancellationToken)
    {
        if (request.Name is not null || creating)
        {
            var name = request.Name?.Trim() ?? string.Empty;
            if (name.Length is 0 or > AssetContract.NameMaxLength)
            {
                return Invalid("name", "name-required");
            }

            contract.Name = name;
        }

        if (request.Kind is { } kind)
        {
            contract.Kind = kind;
        }

        if (creating && request.EndsOn is null)
        {
            return Invalid("endsOn", "ends-on-required");
        }

        if (request.EndsOn is { } ends)
        {
            if (ends != contract.EndsOn)
            {
                // A new end date gets its own alert.
                contract.ExpiryAlertedFor = null;
            }

            contract.EndsOn = ends;
        }

        if (request.StartsOn is not null)
        {
            contract.StartsOn = request.StartsOn == DateOnly.MinValue ? null : request.StartsOn;
        }

        if (contract.StartsOn is { } starts && starts > contract.EndsOn)
        {
            return Invalid("startsOn", "dates-invalid");
        }

        if (request.Reference is not null)
        {
            contract.Reference = AssetEndpoints.Clean(request.Reference);
        }

        if (request.Supplier is not null)
        {
            contract.Supplier = AssetEndpoints.Clean(request.Supplier);
        }

        if (request.Notes is not null)
        {
            contract.Notes = AssetEndpoints.Clean(request.Notes);
        }

        if (request.CostAed is { } cost)
        {
            if (cost < 0 || decimal.Round(cost, 2) != cost)
            {
                return Invalid("costAed", "cost-invalid");
            }

            contract.CostAed = cost;
        }

        if (request.OwnerId is { } owner)
        {
            if (owner != Guid.Empty && !await db.Users.AnyAsync(u => u.Id == owner && u.Status == UserStatus.Active, cancellationToken))
            {
                return Invalid("ownerId", "owner-unknown");
            }

            contract.OwnerId = owner == Guid.Empty ? null : owner;
        }

        if (request.ItemIds is { } itemIds)
        {
            var ids = itemIds.Distinct().ToList();
            if (ids.Count > MaxContractItems)
            {
                return Invalid("itemIds", "too-many-items");
            }

            if (await db.Assets.CountAsync(a => ids.Contains(a.ConfigurationItemId), cancellationToken) != ids.Count)
            {
                return Invalid("itemIds", "asset-unknown");
            }

            var existing = await db.AssetContractItems.Where(i => i.ContractId == contract.Id).ToListAsync(cancellationToken);
            db.AssetContractItems.RemoveRange(existing.Where(e => !ids.Contains(e.ConfigurationItemId)));
            foreach (var id in ids.Where(i => existing.All(e => e.ConfigurationItemId != i)))
            {
                db.AssetContractItems.Add(new AssetContractItem { ContractId = contract.Id, ConfigurationItemId = id });
            }
        }

        return null;
    }

    private static async Task<ContractResponse> LoadContractAsync(ZelvoDbContext db, Guid id, CancellationToken cancellationToken)
    {
        var contract = await db.AssetContracts.AsNoTracking().SingleAsync(c => c.Id == id, cancellationToken);
        var items = await (from ci in db.AssetContractItems.AsNoTracking()
                           join a in db.Assets.AsNoTracking() on ci.ConfigurationItemId equals a.ConfigurationItemId
                           join i in db.ConfigurationItems.AsNoTracking() on a.ConfigurationItemId equals i.Id
                           where ci.ContractId == id
                           orderby a.Tag
                           select new ContractItemResponse(i.Id, a.Tag, i.Number, i.Name)).ToListAsync(cancellationToken);
        var owners = await AssetEndpoints.NamesAsync(db, [contract.OwnerId], cancellationToken);
        return ContractResponse.From(contract, items.Count, items, owners);
    }

    // --- Licences ---

    internal static async Task<Ok<List<LicenseResponse>>> ListLicensesAsync(ZelvoDbContext db, CancellationToken cancellationToken)
    {
        var licenses = await db.SoftwareLicenses.AsNoTracking().OrderBy(l => l.Name).Take(500).ToListAsync(cancellationToken);
        var usage = await LicenseCompliance.ForAsync(db, licenses, cancellationToken);
        var owners = await AssetEndpoints.NamesAsync(db, licenses.Select(l => l.OwnerId), cancellationToken);
        var names = await ProductNamesAsync(db, licenses.SelectMany(LicenseCompliance.Products), cancellationToken);
        return TypedResults.Ok(licenses.Select(l => LicenseResponse.From(l, usage[l.Id], owners, names)).ToList());
    }

    internal static async Task<Results<Created<LicenseResponse>, ValidationProblem, ProblemHttpResult>> CreateLicenseAsync(
        LicenseRequest request, ZelvoDbContext db, CancellationToken cancellationToken)
    {
        var license = new SoftwareLicense { Name = request.Name?.Trim() ?? string.Empty };
        if (await ApplyAsync(license, request, creating: true, db, cancellationToken) is { } invalid)
        {
            return invalid;
        }

        db.SoftwareLicenses.Add(license);
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Created($"/api/v1/assets/licenses/{license.Id}", await LoadLicenseAsync(db, license.Id, cancellationToken));
    }

    internal static async Task<Results<Ok<LicenseResponse>, ValidationProblem, ProblemHttpResult>> UpdateLicenseAsync(
        Guid id, LicenseRequest request, HttpContext http, ZelvoDbContext db, CancellationToken cancellationToken)
    {
        var license = await db.SoftwareLicenses.SingleOrDefaultAsync(l => l.Id == id, cancellationToken);
        if (license is null)
        {
            return FormsCommon.NotFound("license");
        }

        if (!FormsCommon.Matches(http, license.Version))
        {
            return FormsCommon.VersionMismatch();
        }

        if (await ApplyAsync(license, request, creating: false, db, cancellationToken) is { } invalid)
        {
            return invalid;
        }

        license.UpdatedAt = DateTimeOffset.UtcNow;
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return FormsCommon.VersionMismatch();
        }

        return TypedResults.Ok(await LoadLicenseAsync(db, id, cancellationToken));
    }

    internal static async Task<Results<NoContent, ProblemHttpResult>> DeleteLicenseAsync(Guid id, ZelvoDbContext db, CancellationToken cancellationToken)
    {
        var license = await db.SoftwareLicenses.SingleOrDefaultAsync(l => l.Id == id, cancellationToken);
        if (license is null)
        {
            return FormsCommon.NotFound("license");
        }

        db.SoftwareLicenses.Remove(license);
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.NoContent();
    }

    private static async Task<ProblemHttpResult?> ApplyAsync(SoftwareLicense license, LicenseRequest request, bool creating, ZelvoDbContext db, CancellationToken cancellationToken)
    {
        if (request.Name is not null || creating)
        {
            var name = request.Name?.Trim() ?? string.Empty;
            if (name.Length is 0 or > SoftwareLicense.NameMaxLength)
            {
                return Invalid("name", "name-required");
            }

            license.Name = name;
        }

        if (request.ProductKeys is { } keys || creating)
        {
            var products = (request.ProductKeys ?? []).Select(k => k.Trim()).Where(k => k.Length > 0).Distinct(StringComparer.Ordinal).ToList();
            if (products.Count is 0 or > SoftwareLicense.MaxProducts || products.Any(p => p.Length > CiInstalledSoftware.NameMaxLength))
            {
                return Invalid("productKeys", "products-required");
            }

            license.ProductKeys = JsonSerializer.Serialize(products);
        }

        if (request.Metric is { } metric)
        {
            license.Metric = metric;
        }

        if (request.Quantity is { } quantity || creating)
        {
            if (request.Quantity is not (>= 0 and <= 10_000_000))
            {
                return Invalid("quantity", "quantity-invalid");
            }

            license.Quantity = request.Quantity.Value;
        }

        if (request.StartsOn is not null)
        {
            license.StartsOn = request.StartsOn == DateOnly.MinValue ? null : request.StartsOn;
        }

        if (request.EndsOn is not null)
        {
            var ends = request.EndsOn == DateOnly.MinValue ? null : request.EndsOn;
            if (ends != license.EndsOn)
            {
                license.ExpiryAlertedFor = null;
            }

            license.EndsOn = ends;
        }

        if (license.StartsOn is { } s && license.EndsOn is { } e && s > e)
        {
            return Invalid("startsOn", "dates-invalid");
        }

        if (request.Publisher is not null)
        {
            license.Publisher = AssetEndpoints.Clean(request.Publisher);
        }

        if (request.Supplier is not null)
        {
            license.Supplier = AssetEndpoints.Clean(request.Supplier);
        }

        if (request.Reference is not null)
        {
            license.Reference = AssetEndpoints.Clean(request.Reference);
        }

        if (request.Notes is not null)
        {
            license.Notes = AssetEndpoints.Clean(request.Notes);
        }

        if (request.CostAed is { } cost)
        {
            if (cost < 0 || decimal.Round(cost, 2) != cost)
            {
                return Invalid("costAed", "cost-invalid");
            }

            license.CostAed = cost;
        }

        if (request.OwnerId is { } owner)
        {
            if (owner != Guid.Empty && !await db.Users.AnyAsync(u => u.Id == owner && u.Status == UserStatus.Active, cancellationToken))
            {
                return Invalid("ownerId", "owner-unknown");
            }

            license.OwnerId = owner == Guid.Empty ? null : owner;
        }

        return null;
    }

    private static async Task<LicenseResponse> LoadLicenseAsync(ZelvoDbContext db, Guid id, CancellationToken cancellationToken)
    {
        var license = await db.SoftwareLicenses.AsNoTracking().SingleAsync(l => l.Id == id, cancellationToken);
        var usage = await LicenseCompliance.ForAsync(db, [license], cancellationToken);
        var owners = await AssetEndpoints.NamesAsync(db, [license.OwnerId], cancellationToken);
        return LicenseResponse.From(license, usage[license.Id], owners, await ProductNamesAsync(db, LicenseCompliance.Products(license), cancellationToken));
    }

    internal static async Task<Ok<PageResult<SoftwareProductResponse>>> SoftwareAsync(
        [AsParameters] PageQuery page, [AsParameters] SoftwareQuery filter, ZelvoDbContext db, CancellationToken cancellationToken)
    {
        var installs = from s in db.CiInstalledSoftwareItems.AsNoTracking()
                       join i in db.ConfigurationItems.AsNoTracking() on s.ConfigurationItemId equals i.Id
                       where i.Status != CiStatus.Retired && i.MergedIntoId == null
                       select s;
        if (filter.Q?.Trim() is { Length: > 0 } q)
        {
            var pattern = CmdbItemEndpoints.LikePattern(q);
            installs = installs.Where(s => EF.Functions.ILike(s.Name, pattern) || EF.Functions.ILike(s.Publisher!, pattern));
        }

        var grouped = installs.GroupBy(s => s.ProductKey).Select(g => new
        {
            Key = g.Key, Name = g.Max(s => s.Name), Publisher = g.Max(s => s.Publisher), Devices = g.Select(s => s.ConfigurationItemId).Distinct().Count(),
        });
        var total = await grouped.LongCountAsync(cancellationToken);
        var rows = await grouped.OrderByDescending(g => g.Devices).ThenBy(g => g.Key).Skip(page.Skip).Take(page.Size).ToListAsync(cancellationToken);
        var licenses = await db.SoftwareLicenses.AsNoTracking().ToListAsync(cancellationToken);
        return TypedResults.Ok(new PageResult<SoftwareProductResponse>(
            [.. rows.Select(r => new SoftwareProductResponse(r.Key, r.Name ?? r.Key, r.Publisher, r.Devices,
                [.. licenses.Where(l => LicenseCompliance.Products(l).Contains(r.Key)).Select(l => l.Name)]))],
            page.Number, page.Size, total));
    }

    private static async Task<Dictionary<string, string>> ProductNamesAsync(ZelvoDbContext db, IEnumerable<string> keys, CancellationToken cancellationToken)
    {
        var list = keys.Distinct(StringComparer.Ordinal).ToList();
        return list.Count == 0
            ? []
            : await db.CiInstalledSoftwareItems.AsNoTracking().Where(s => list.Contains(s.ProductKey)).GroupBy(s => s.ProductKey)
                .Select(g => new { g.Key, Name = g.Max(s => s.Name) }).ToDictionaryAsync(g => g.Key, g => g.Name ?? g.Key, cancellationToken);
    }

    internal static DateOnly Today(TimeProvider time) => DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime);

    private static ProblemHttpResult Invalid(string field, string code) =>
        TypedResults.Problem(statusCode: StatusCodes.Status400BadRequest, title: "The values have a problem.", type: ApiProblem.TypeFor(code),
            extensions: new Dictionary<string, object?>(StringComparer.Ordinal) { ["code"] = code, ["field"] = field });
}

// --- Requests ---

/// <summary>Filters of the contract list.</summary>
public sealed class ContractQuery
{
    /// <summary>Only contracts ending within the warning period.</summary>
    public bool? Expiring { get; init; }

    /// <summary>Only one kind.</summary>
    public AssetContractKind? Kind { get; init; }
}

/// <summary>Filters of the discovered software list.</summary>
public sealed class SoftwareQuery
{
    /// <summary>Words in the name or publisher.</summary>
    [MaxLength(200)]
    public string? Q { get; init; }
}

/// <summary>A contract or warranty, new or changed (what is left out stays; empty texts, Guid.Empty and 0001-01-01 clear).</summary>
public sealed class ContractRequest
{
    /// <summary>Warranty, Support, Maintenance or Lease.</summary>
    public AssetContractKind? Kind { get; init; }

    /// <summary>Name.</summary>
    [StringLength(AssetContract.NameMaxLength)]
    public string? Name { get; init; }

    /// <summary>The supplier's reference.</summary>
    [StringLength(AssetContract.ReferenceMaxLength)]
    public string? Reference { get; init; }

    /// <summary>The supplier.</summary>
    [StringLength(AssetContract.NameMaxLength)]
    public string? Supplier { get; init; }

    /// <summary>When it starts.</summary>
    public DateOnly? StartsOn { get; init; }

    /// <summary>The last day it covers (required for a new contract).</summary>
    public DateOnly? EndsOn { get; init; }

    /// <summary>What it costs in AED.</summary>
    public decimal? CostAed { get; init; }

    /// <summary>Notes.</summary>
    [StringLength(Asset.NotesMaxLength)]
    public string? Notes { get; init; }

    /// <summary>Who is told before it ends (besides asset managers).</summary>
    public Guid? OwnerId { get; init; }

    /// <summary>The assets it covers (all of them).</summary>
    public List<Guid>? ItemIds { get; init; }
}

/// <summary>A software licence, new or changed.</summary>
public sealed class LicenseRequest
{
    /// <summary>Name.</summary>
    [StringLength(SoftwareLicense.NameMaxLength)]
    public string? Name { get; init; }

    /// <summary>The publisher.</summary>
    [StringLength(SoftwareLicense.NameMaxLength)]
    public string? Publisher { get; init; }

    /// <summary>The discovered products it covers (product keys from GET /assets/software), 1–20.</summary>
    public List<string>? ProductKeys { get; init; }

    /// <summary>Device (default) or User.</summary>
    public LicenseMetric? Metric { get; init; }

    /// <summary>How many it entitles (required for a new licence).</summary>
    public int? Quantity { get; init; }

    /// <summary>When it starts.</summary>
    public DateOnly? StartsOn { get; init; }

    /// <summary>When it ends (subscriptions).</summary>
    public DateOnly? EndsOn { get; init; }

    /// <summary>The supplier.</summary>
    [StringLength(SoftwareLicense.NameMaxLength)]
    public string? Supplier { get; init; }

    /// <summary>The agreement's reference.</summary>
    [StringLength(AssetContract.ReferenceMaxLength)]
    public string? Reference { get; init; }

    /// <summary>What it costs in AED.</summary>
    public decimal? CostAed { get; init; }

    /// <summary>Notes.</summary>
    [StringLength(Asset.NotesMaxLength)]
    public string? Notes { get; init; }

    /// <summary>Who is told before it ends.</summary>
    public Guid? OwnerId { get; init; }
}

// --- Responses ---

/// <summary>An asset a contract covers.</summary>
internal sealed record ContractItemResponse(Guid Id, string Tag, string Number, string Name);

/// <summary>A contract or warranty.</summary>
internal sealed record ContractResponse(
    Guid Id, AssetContractKind Kind, string Name, string? Reference, string? Supplier, DateOnly? StartsOn, DateOnly EndsOn, decimal? CostAed, string? Notes,
    AssetPerson? Owner, int ItemCount, IReadOnlyList<ContractItemResponse> Items, string Etag)
{
    public static ContractResponse From(AssetContract c, int count, IReadOnlyList<ContractItemResponse> items, IReadOnlyDictionary<Guid, string?> owners) =>
        new(c.Id, c.Kind, c.Name, c.Reference, c.Supplier, c.StartsOn, c.EndsOn, c.CostAed, c.Notes,
            c.OwnerId is { } o ? new AssetPerson(o, owners.GetValueOrDefault(o)) : null, count, items, $"\"{c.Version}\"");
}

/// <summary>A product a licence covers.</summary>
internal sealed record LicenseProduct(string Key, string? Name);

/// <summary>A software licence with its compliance.</summary>
/// <param name="Devices">Devices the products are installed on.</param>
/// <param name="People">People using them (devices without a person once each).</param>
/// <param name="Used">What counts against the quantity (devices or people, by the metric).</param>
/// <param name="Shortfall">How many more entitlements are needed.</param>
/// <param name="Spare">Entitlements not used.</param>
internal sealed record LicenseResponse(
    Guid Id, string Name, string? Publisher, IReadOnlyList<LicenseProduct> Products, LicenseMetric Metric, int Quantity, DateOnly? StartsOn, DateOnly? EndsOn,
    string? Supplier, string? Reference, decimal? CostAed, string? Notes, AssetPerson? Owner, int Devices, int People, int Used, int Shortfall, int Spare, string Etag)
{
    public static LicenseResponse From(SoftwareLicense l, LicenseCompliance.Usage usage, IReadOnlyDictionary<Guid, string?> owners, IReadOnlyDictionary<string, string> names) =>
        new(l.Id, l.Name, l.Publisher, [.. LicenseCompliance.Products(l).Select(k => new LicenseProduct(k, names.GetValueOrDefault(k)))], l.Metric, l.Quantity, l.StartsOn, l.EndsOn,
            l.Supplier, l.Reference, l.CostAed, l.Notes, l.OwnerId is { } o ? new AssetPerson(o, owners.GetValueOrDefault(o)) : null,
            usage.Devices, usage.People, usage.Used, usage.Shortfall, usage.Spare, $"\"{l.Version}\"");
}

/// <summary>A product discovery found.</summary>
internal sealed record SoftwareProductResponse(string ProductKey, string Name, string? Publisher, int Devices, IReadOnlyList<string> Licenses);
