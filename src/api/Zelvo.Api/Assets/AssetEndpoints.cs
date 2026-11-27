using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Zelvo.Api.Cmdb;
using Zelvo.Api.Conventions;
using Zelvo.Api.Forms;
using Zelvo.Api.Identity;
using Zelvo.Api.Settings;
using Zelvo.Api.Tenancy;
using Zelvo.Data;
using Zelvo.Data.Auditing;
using Zelvo.Data.Branding;
using Zelvo.Data.Cmdb;
using Zelvo.Data.Identity;
using Zelvo.Data.Localization;
using Zelvo.Data.Tenancy;
using Zelvo.Data.Tickets;
using Zelvo.Search;
using Zelvo.Tickets.Exports;

namespace Zelvo.Api.Assets;

/// <summary>
/// Assets (task G3, module 18): hardware and software configuration items with an asset tag, their
/// lifecycle, who they are assigned to, the request that asked for them, label sheets, the lookup
/// a scanned label uses, and people's own assets. <c>assets.read</c> sees them; <c>assets.manage</c>
/// changes them. Every change is audited; the item's status follows the lifecycle.
/// </summary>
internal static class AssetEndpoints
{
    /// <summary>Most labels on one sheet export.</summary>
    public const int MaxLabels = 200;

    public static RouteGroupBuilder MapAssetEndpoints(this RouteGroupBuilder group)
    {
        var assets = group.MapGroup("/assets").WithTags("Assets").RequireTenant();

        assets.MapGet("", ListAsync)
            .WithName("ListAssets")
            .WithSummary("Assets, filtered by words in the tag, number or name, state, the person they are assigned to, class (with the classes below it) and warranties ending soon. Needs assets.read.")
            .RequirePermission(Permissions.AssetsRead)
            .Produces<PageResult<AssetSummaryResponse>>();

        assets.MapPost("", CreateAsync)
            .WithName("CreateAsset")
            .WithSummary("Make a hardware or software configuration item an asset, with a tag (generated when left out), a state, the person it is assigned to, the request that asked for it and purchase details. Needs assets.manage; audited.")
            .RequirePermission(Permissions.AssetsManage)
            .Produces<AssetResponse>(StatusCodes.Status201Created)
            .ProducesValidationProblem();

        assets.MapGet("/{id:guid}", GetAsync)
            .WithName("GetAsset")
            .WithSummary("One asset (by its configuration item's id) with its contracts and warranties, licences of the software on it and the request that asked for it. Returns an ETag. Needs assets.read.")
            .RequirePermission(Permissions.AssetsRead)
            .Produces<AssetResponse>();

        assets.MapPatch("/{id:guid}", UpdateAsync)
            .WithName("UpdateAsset")
            .WithSummary("Change an asset's tag, request and purchase details (the state and person change through /state). Send the ETag as If-Match. Needs assets.manage; audited.")
            .RequirePermission(Permissions.AssetsManage)
            .Produces<AssetResponse>()
            .ProducesValidationProblem();

        assets.MapPost("/{id:guid}/state", MoveAsync)
            .WithName("MoveAsset")
            .WithSummary("Move an asset along its lifecycle (requested, ordered, received, in stock, assigned, in repair, retired, disposed); assigned needs the person. The item's status follows. Needs assets.manage; audited.")
            .RequirePermission(Permissions.AssetsManage)
            .Produces<AssetResponse>()
            .ProducesValidationProblem();

        assets.MapGet("/lookup", LookupAsync)
            .WithName("LookupAsset")
            .WithSummary("The asset with a tag, for a scanned label. Needs assets.read.")
            .RequirePermission(Permissions.AssetsRead)
            .Produces<AssetSummaryResponse>();

        assets.MapPost("/labels/exports", LabelsAsync)
            .WithName("ExportAssetLabels")
            .WithSummary("Ask for a printable PDF sheet of labels (QR code and Code 128 of each asset tag, at most 200) in en or ar; the worker makes it, poll GET /exports/{id}. Needs assets.read; audited.")
            .RequirePermission(Permissions.AssetsRead)
            .Produces(StatusCodes.Status202Accepted)
            .ProducesValidationProblem();

        group.MapGet("/users/{id:guid}/assets", ForUserAsync)
            .WithTags("Assets")
            .WithName("ListUserAssets")
            .WithSummary("The assets assigned to a person, with their warranty and how many tickets each is on (the agent's customer panel). Needs assets.read.")
            .RequireTenant()
            .RequirePermission(Permissions.AssetsRead)
            .Produces<IReadOnlyList<PersonAssetResponse>>();

        group.MapGet("/me/assets", MineAsync)
            .WithTags("Assets")
            .WithName("ListMyAssets")
            .WithSummary("The assets assigned to the caller (My assets), with their warranty.")
            .RequireTenant()
            .RequireAuthorization()
            .Produces<IReadOnlyList<PersonAssetResponse>>();

        assets.MapGet("/settings", SettingsAsync)
            .WithName("GetAssetSettings")
            .WithSummary("Asset settings: the prefix of generated tags and the days before an end date that alerts go out. Needs assets.read.")
            .RequirePermission(Permissions.AssetsRead)
            .Produces<AssetSettingsResponse>();

        assets.MapPut("/settings", PutSettingsAsync)
            .WithName("PutAssetSettings")
            .WithSummary("Change the asset settings. Needs assets.manage; audited.")
            .RequirePermission(Permissions.AssetsManage)
            .Produces<AssetSettingsResponse>()
            .ProducesValidationProblem();

        return group;
    }

    internal static async Task<Ok<PageResult<AssetSummaryResponse>>> ListAsync(
        [AsParameters] PageQuery page, [AsParameters] AssetListQuery filter, ZelvoDbContext db, TenantSettingsStore settings, TimeProvider time, CancellationToken cancellationToken)
    {
        var classes = await CiClassIndex.LoadAsync(db, cancellationToken);
        var query = from a in db.Assets.AsNoTracking()
                    join i in db.ConfigurationItems.AsNoTracking() on a.ConfigurationItemId equals i.Id
                    select new { Asset = a, Item = i };
        if (filter.Q?.Trim() is { Length: > 0 } q)
        {
            var pattern = CmdbItemEndpoints.LikePattern(q);
            query = query.Where(x => EF.Functions.ILike(x.Asset.Tag, pattern) || EF.Functions.ILike(x.Item.Name, pattern) || EF.Functions.ILike(x.Item.Number, pattern));
        }

        if (filter.State is { } state)
        {
            query = query.Where(x => x.Asset.State == state);
        }
        else if (filter.IncludeDisposed != true)
        {
            query = query.Where(x => x.Asset.State != AssetState.Disposed);
        }

        if (filter.AssignedToId is { } person)
        {
            query = query.Where(x => x.Asset.AssignedToId == person);
        }

        if (filter.ClassId is { } classId)
        {
            var ids = classes.WithDescendants(classId).ToList();
            query = query.Where(x => ids.Contains(x.Item.ClassId));
        }

        if (filter.WarrantyEnding == true)
        {
            var today = DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime);
            var until = today.AddDays(await settings.GetAsync(AssetSettings.ExpiryWarningDays, cancellationToken));
            var ending = from item in db.AssetContractItems
                         join contract in db.AssetContracts on item.ContractId equals contract.Id
                         where contract.Kind == AssetContractKind.Warranty && contract.EndsOn >= today && contract.EndsOn <= until
                         select item.ConfigurationItemId;
            query = query.Where(x => ending.Contains(x.Asset.ConfigurationItemId));
        }

        var total = await query.LongCountAsync(cancellationToken);
        var rows = await query.OrderBy(x => x.Asset.Tag).Skip(page.Skip).Take(page.Size).ToListAsync(cancellationToken);
        var people = await NamesAsync(db, rows.Select(r => r.Asset.AssignedToId), cancellationToken);
        var warranties = await AssetWarranties.EndsAsync(db, [.. rows.Select(r => r.Asset.ConfigurationItemId)], cancellationToken);
        return TypedResults.Ok(new PageResult<AssetSummaryResponse>(
            [.. rows.Select(r => AssetSummaryResponse.From(r.Asset, r.Item, classes, people, warranties))], page.Number, page.Size, total));
    }

    internal static async Task<Results<Created<AssetResponse>, ValidationProblem, ProblemHttpResult>> CreateAsync(
        CreateAssetRequest request, HttpContext http, ZelvoDbContext db, ITenantContext tenant, TenantSettingsStore settings, ISearchIndexer indexer, CancellationToken cancellationToken)
    {
        var item = await db.ConfigurationItems.SingleOrDefaultAsync(i => i.Id == request.ConfigurationItemId, cancellationToken);
        if (item is null || item.MergedIntoId is not null)
        {
            return UserEndpoints.Validation("configurationItemId", "Choose a configuration item.");
        }

        var classes = await CiClassIndex.LoadAsync(db, cancellationToken);
        if (classes.Find(item.ClassId) is not { } ciClass || !AssetLifecycle.IsAssetFamily(ciClass.Family))
        {
            return Problem("not-an-asset-class", "Only hardware and software items are assets.");
        }

        if (await db.Assets.AnyAsync(a => a.ConfigurationItemId == item.Id, cancellationToken))
        {
            return Problem("already-an-asset", "The item is an asset already.");
        }

        var state = request.State ?? AssetState.InStock;
        if (state == AssetState.Assigned != (request.AssignedToId is not null))
        {
            return UserEndpoints.Validation("assignedToId", "Assigned assets name the person, and only they do.");
        }

        if (await CheckAsync(db, request.AssignedToId, request.RequestTicketId, request.PurchaseCost, cancellationToken) is { } invalid)
        {
            return invalid;
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        string tag;
        if (request.Tag is { Length: > 0 } given)
        {
            tag = given.Trim().ToUpperInvariant();
            if (!AssetTags.IsTag(tag))
            {
                return UserEndpoints.Validation("tag", "Tags are 1 to 32 capital letters, digits and hyphens.");
            }

            if (await db.Assets.AnyAsync(a => a.Tag == tag, cancellationToken))
            {
                return Problem("tag-taken", "Another asset has this tag.");
            }
        }
        else
        {
            tag = await AssetTags.NextAsync(db, tenant.TenantId!.Value, settings, cancellationToken);
        }

        var asset = new Asset
        {
            ConfigurationItemId = item.Id, Tag = tag, State = state, AssignedToId = request.AssignedToId, AssignedAt = request.AssignedToId is null ? null : DateTimeOffset.UtcNow,
            RequestTicketId = request.RequestTicketId, PurchaseDate = request.PurchaseDate, PurchaseCost = request.PurchaseCost, Supplier = Clean(request.Supplier),
            OrderNumber = Clean(request.OrderNumber), Notes = Clean(request.Notes),
        };
        db.Assets.Add(asset);
        Follow(item, state, classes, indexer, await CmdbSearch.ReaderRolesAsync(db, cancellationToken));
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        var response = await AssetResponse.LoadAsync(db, item.Id, cancellationToken);
        http.Response.Headers.ETag = response!.Etag;
        return TypedResults.Created($"/api/v1/assets/{item.Id}", response);
    }

    internal static async Task<Results<Ok<AssetResponse>, ProblemHttpResult>> GetAsync(Guid id, HttpContext http, ZelvoDbContext db, CancellationToken cancellationToken)
    {
        var response = await AssetResponse.LoadAsync(db, id, cancellationToken);
        if (response is null)
        {
            return NotFound();
        }

        http.Response.Headers.ETag = response.Etag;
        return TypedResults.Ok(response);
    }

    internal static async Task<Results<Ok<AssetResponse>, ValidationProblem, ProblemHttpResult>> UpdateAsync(
        Guid id, UpdateAssetRequest request, HttpContext http, ZelvoDbContext db, CancellationToken cancellationToken)
    {
        var asset = await db.Assets.SingleOrDefaultAsync(a => a.ConfigurationItemId == id, cancellationToken);
        if (asset is null)
        {
            return NotFound();
        }

        if (!FormsCommon.Matches(http, asset.Version))
        {
            return FormsCommon.VersionMismatch();
        }

        if (request.Tag is { } tagText)
        {
            var tag = tagText.Trim().ToUpperInvariant();
            if (!AssetTags.IsTag(tag))
            {
                return UserEndpoints.Validation("tag", "Tags are 1 to 32 capital letters, digits and hyphens.");
            }

            if (tag != asset.Tag && await db.Assets.AnyAsync(a => a.Tag == tag, cancellationToken))
            {
                return Problem("tag-taken", "Another asset has this tag.");
            }

            asset.Tag = tag;
        }

        var ticket = request.RequestTicketId is { } t && t != Guid.Empty ? t : (Guid?)null;
        if (await CheckAsync(db, null, ticket, request.PurchaseCost, cancellationToken) is { } invalid)
        {
            return invalid;
        }

        if (request.RequestTicketId is not null)
        {
            asset.RequestTicketId = ticket;
        }

        if (request.PurchaseDate is not null)
        {
            asset.PurchaseDate = request.PurchaseDate == DateOnly.MinValue ? null : request.PurchaseDate;
        }

        if (request.PurchaseCost is not null)
        {
            asset.PurchaseCost = request.PurchaseCost;
        }

        if (request.Supplier is not null)
        {
            asset.Supplier = Clean(request.Supplier);
        }

        if (request.OrderNumber is not null)
        {
            asset.OrderNumber = Clean(request.OrderNumber);
        }

        if (request.Notes is not null)
        {
            asset.Notes = Clean(request.Notes);
        }

        asset.UpdatedAt = DateTimeOffset.UtcNow;
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return FormsCommon.VersionMismatch();
        }

        var response = await AssetResponse.LoadAsync(db, id, cancellationToken);
        http.Response.Headers.ETag = response!.Etag;
        return TypedResults.Ok(response);
    }

    internal static async Task<Results<Ok<AssetResponse>, ValidationProblem, ProblemHttpResult>> MoveAsync(
        Guid id, MoveAssetRequest request, HttpContext http, ZelvoDbContext db, IAuditLog audit, ISearchIndexer indexer, CancellationToken cancellationToken)
    {
        var asset = await db.Assets.SingleOrDefaultAsync(a => a.ConfigurationItemId == id, cancellationToken);
        if (asset is null)
        {
            return NotFound();
        }

        if (!FormsCommon.Matches(http, asset.Version))
        {
            return FormsCommon.VersionMismatch();
        }

        var to = request.State!.Value;
        if (!AssetLifecycle.CanMove(asset.State, to))
        {
            return Problem("state-not-allowed", $"An asset that is {asset.State} cannot become {to}.");
        }

        if (to == AssetState.Assigned != (request.AssignedToId is not null))
        {
            return UserEndpoints.Validation("assignedToId", "Assigned assets name the person, and only they do.");
        }

        if (to == AssetState.Assigned && request.AssignedToId == asset.AssignedToId)
        {
            return UserEndpoints.Validation("assignedToId", "The asset is assigned to this person already.");
        }

        if (await CheckAsync(db, request.AssignedToId, null, null, cancellationToken) is { } invalid)
        {
            return invalid;
        }

        var from = asset.State;
        var previousPerson = asset.AssignedToId;
        asset.State = to;
        asset.AssignedToId = request.AssignedToId;
        asset.AssignedAt = request.AssignedToId is null ? null : DateTimeOffset.UtcNow;
        asset.UpdatedAt = DateTimeOffset.UtcNow;
        var item = await db.ConfigurationItems.SingleAsync(i => i.Id == id, cancellationToken);
        Follow(item, to, await CiClassIndex.LoadAsync(db, cancellationToken), indexer, await CmdbSearch.ReaderRolesAsync(db, cancellationToken));
        audit.Add(AuditCategory.Change, "assets.state-changed", CmdbAudit.ItemEntity, id.ToString(), new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["from"] = from.ToString(), ["to"] = to.ToString(), ["assignedTo"] = request.AssignedToId?.ToString(), ["previouslyAssignedTo"] = previousPerson?.ToString(),
            ["note"] = request.Note is { Length: > 0 } note ? (note.Length <= 500 ? note : note[..500]) : null,
        });
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return FormsCommon.VersionMismatch();
        }

        var response = await AssetResponse.LoadAsync(db, id, cancellationToken);
        http.Response.Headers.ETag = response!.Etag;
        return TypedResults.Ok(response);
    }

    internal static async Task<Results<Ok<AssetSummaryResponse>, ProblemHttpResult>> LookupAsync(
        [Required, StringLength(Asset.TagMaxLength)] string tag, ZelvoDbContext db, CancellationToken cancellationToken)
    {
        var wanted = tag.Trim().ToUpperInvariant();
        var row = await (from a in db.Assets.AsNoTracking()
                         join i in db.ConfigurationItems.AsNoTracking() on a.ConfigurationItemId equals i.Id
                         where a.Tag == wanted
                         select new { Asset = a, Item = i }).SingleOrDefaultAsync(cancellationToken);
        if (row is null)
        {
            return NotFound();
        }

        var classes = await CiClassIndex.LoadAsync(db, cancellationToken);
        var people = await NamesAsync(db, [row.Asset.AssignedToId], cancellationToken);
        var warranties = await AssetWarranties.EndsAsync(db, [row.Asset.ConfigurationItemId], cancellationToken);
        return TypedResults.Ok(AssetSummaryResponse.From(row.Asset, row.Item, classes, people, warranties));
    }

    internal static async Task<Results<Accepted<ExportStarted>, ValidationProblem, ProblemHttpResult>> LabelsAsync(
        [AsParameters] LabelQuery query, LabelLanguage? body, ZelvoDbContext db, CurrentUser user, IAuditLog audit, CancellationToken cancellationToken)
    {
        var ids = (query.Ids ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(v => Guid.TryParse(v, out var g) ? g : Guid.Empty).Distinct().ToList();
        if (ids.Count == 0 || ids.Count > MaxLabels || ids.Contains(Guid.Empty))
        {
            return UserEndpoints.Validation("ids", $"Choose 1 to {MaxLabels} assets.");
        }

        var labels = await (from a in db.Assets.AsNoTracking()
                            join i in db.ConfigurationItems.AsNoTracking() on a.ConfigurationItemId equals i.Id
                            where ids.Contains(a.ConfigurationItemId)
                            orderby a.Tag
                            select new AssetLabel(a.Tag, i.Name)).ToListAsync(cancellationToken);
        if (labels.Count != ids.Count)
        {
            return UserEndpoints.Validation("ids", "Choose assets that exist.");
        }

        var me = user.Account!;
        if (await db.DocumentExports.CountAsync(e => e.RequestedById == me.Id && e.Status == ExportStatus.Pending, cancellationToken) >= 5)
        {
            return (ProblemHttpResult)ApiProblem.Create(StatusCodes.Status429TooManyRequests, "too-many-exports", "Wait for your other exports to finish.");
        }

        var language = (body?.Language ?? query.Language) is LocalizedText.Arabic ? LocalizedText.Arabic
            : (body?.Language ?? query.Language) is LocalizedText.English ? LocalizedText.English
            : me.PreferredLanguage is LocalizedText.Arabic ? LocalizedText.Arabic : LocalizedText.English;
        var export = new DocumentExport
        {
            Kind = ExportKind.AssetLabels, Parameters = new AssetLabelSheet(query.Layout ?? AssetLabelLayout.Small, labels).Write(),
            Language = language, StaffView = true, RequestedById = me.Id,
        };
        db.DocumentExports.Add(export);
        audit.Add(AuditCategory.View, "assets.labels.requested", "asset", null,
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["exportId"] = export.Id.ToString(), ["count"] = labels.Count, ["layout"] = (query.Layout ?? AssetLabelLayout.Small).ToString() });
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Accepted($"/api/v1/exports/{export.Id}", new ExportStarted(export.Id, export.Status));
    }

    internal static async Task<Ok<List<PersonAssetResponse>>> ForUserAsync(Guid id, ZelvoDbContext db, CancellationToken cancellationToken) =>
        TypedResults.Ok(await PersonAssetResponse.LoadAsync(db, id, withTickets: true, cancellationToken));

    internal static async Task<Results<Ok<List<PersonAssetResponse>>, ProblemHttpResult>> MineAsync(ZelvoDbContext db, CurrentUser user, CancellationToken cancellationToken)
    {
        if (user.Account is not { } me)
        {
            return (ProblemHttpResult)ApiProblem.Create(StatusCodes.Status403Forbidden, "people-only", "Only people have assets.");
        }

        return TypedResults.Ok(await PersonAssetResponse.LoadAsync(db, me.Id, withTickets: false, cancellationToken));
    }

    internal static async Task<Ok<AssetSettingsResponse>> SettingsAsync(TenantSettingsStore settings, CancellationToken cancellationToken) =>
        TypedResults.Ok(new AssetSettingsResponse(await settings.GetAsync(AssetSettings.TagPrefix, cancellationToken), await settings.GetAsync(AssetSettings.ExpiryWarningDays, cancellationToken)));

    internal static async Task<Results<Ok<AssetSettingsResponse>, ValidationProblem>> PutSettingsAsync(
        AssetSettingsRequest request, TenantSettingsStore settings, ZelvoDbContext db, CancellationToken cancellationToken)
    {
        if (request.TagPrefix is { } prefix && !AssetSettings.TagPrefix.IsValid(prefix))
        {
            return UserEndpoints.Validation("tagPrefix", "1 to 10 capital letters or digits, starting with a letter.");
        }

        if (request.ExpiryWarningDays is { } days && !AssetSettings.ExpiryWarningDays.IsValid(days))
        {
            return UserEndpoints.Validation("expiryWarningDays", "From 1 to 365 days.");
        }

        if (request.TagPrefix is { } p)
        {
            await settings.SetAsync(AssetSettings.TagPrefix, p, cancellationToken);
        }

        if (request.ExpiryWarningDays is { } d)
        {
            await settings.SetAsync(AssetSettings.ExpiryWarningDays, d, cancellationToken);
        }

        await db.SaveChangesAsync(cancellationToken);
        return await SettingsAsync(settings, cancellationToken);
    }

    // --- Helpers ---

    /// <summary>The item's status follows the asset's state, and its search document follows the item.</summary>
    private static void Follow(ConfigurationItem item, AssetState state, CiClassIndex classes, ISearchIndexer indexer, IReadOnlyList<string> roles)
    {
        var status = AssetLifecycle.StatusOf(state);
        if (item.Status != status)
        {
            item.Status = status;
            item.UpdatedAt = DateTimeOffset.UtcNow;
            indexer.Upsert(CmdbSearch.Document(item, classes, roles));
        }
    }

    private static async Task<ValidationProblem?> CheckAsync(ZelvoDbContext db, Guid? person, Guid? ticket, decimal? cost, CancellationToken cancellationToken)
    {
        if (person is { } p && !await db.Users.AnyAsync(u => u.Id == p && u.Status == UserStatus.Active && u.ErasedAt == null, cancellationToken))
        {
            return UserEndpoints.Validation("assignedToId", "Choose an active person of this organisation.");
        }

        if (ticket is { } t && !await db.Tickets.AnyAsync(x => x.Id == t && x.Type == TicketType.ServiceRequest, cancellationToken))
        {
            return UserEndpoints.Validation("requestTicketId", "Choose a service request.");
        }

        if (cost is < 0 or > 9_999_999_999m || cost is { } c && decimal.Round(c, 2) != c)
        {
            return UserEndpoints.Validation("purchaseCost", "A cost in AED with at most two decimals.");
        }

        return null;
    }

    internal static async Task<Dictionary<Guid, string?>> NamesAsync(ZelvoDbContext db, IEnumerable<Guid?> ids, CancellationToken cancellationToken) =>
        await FormEndpoints.PeopleNamesAsync(db, ids.Where(i => i is not null).Select(i => i!.Value).Distinct(), cancellationToken);

    internal static string? Clean(string? value) => value?.Trim() is { Length: > 0 } v ? v : null;

    internal static ProblemHttpResult NotFound() => FormsCommon.NotFound("asset");

    internal static ProblemHttpResult Problem(string code, string title) => (ProblemHttpResult)ApiProblem.Create(StatusCodes.Status409Conflict, code, title);
}

// --- Labels ---

/// <summary>Label sheet choices.</summary>
public sealed class LabelQuery
{
    /// <summary>Asset ids (their configuration items), separated by commas.</summary>
    [MaxLength(8000)]
    public string? Ids { get; init; }

    /// <summary>Small (3 × 8 on A4, default) or Large (2 × 5).</summary>
    public AssetLabelLayout? Layout { get; init; }

    /// <summary>en or ar.</summary>
    [MaxLength(2)]
    public string? Language { get; init; }
}

/// <summary>The language of a label sheet.</summary>
public sealed class LabelLanguage
{
    /// <summary>en or ar.</summary>
    [MaxLength(2)]
    public string? Language { get; init; }
}

/// <summary>An export that was asked for.</summary>
internal sealed record ExportStarted(Guid Id, ExportStatus Status);

// --- Requests ---

/// <summary>Filters of the asset list.</summary>
public sealed class AssetListQuery
{
    /// <summary>Words in the tag, number or name.</summary>
    [MaxLength(200)]
    public string? Q { get; init; }

    /// <summary>Only assets in this state.</summary>
    public AssetState? State { get; init; }

    /// <summary>Also disposed assets (left out otherwise).</summary>
    public bool? IncludeDisposed { get; init; }

    /// <summary>Only assets assigned to this person.</summary>
    public Guid? AssignedToId { get; init; }

    /// <summary>Only assets of this class or below.</summary>
    public Guid? ClassId { get; init; }

    /// <summary>Only assets whose warranty ends within the warning period.</summary>
    public bool? WarrantyEnding { get; init; }
}

/// <summary>A new asset.</summary>
public sealed class CreateAssetRequest
{
    /// <summary>The hardware or software configuration item.</summary>
    [Required]
    public Guid? ConfigurationItemId { get; init; }

    /// <summary>The asset tag; generated (AST-000123) when left out.</summary>
    [StringLength(Asset.TagMaxLength)]
    public string? Tag { get; init; }

    /// <summary>Its state (default in stock).</summary>
    public AssetState? State { get; init; }

    /// <summary>The person, for an assigned asset.</summary>
    public Guid? AssignedToId { get; init; }

    /// <summary>The service request that asked for it.</summary>
    public Guid? RequestTicketId { get; init; }

    /// <summary>When it was bought.</summary>
    public DateOnly? PurchaseDate { get; init; }

    /// <summary>What it cost in AED.</summary>
    public decimal? PurchaseCost { get; init; }

    /// <summary>Who it was bought from.</summary>
    [StringLength(Asset.SupplierMaxLength)]
    public string? Supplier { get; init; }

    /// <summary>The order or invoice number.</summary>
    [StringLength(Asset.SupplierMaxLength)]
    public string? OrderNumber { get; init; }

    /// <summary>Notes.</summary>
    [StringLength(Asset.NotesMaxLength)]
    public string? Notes { get; init; }
}

/// <summary>Changes to an asset; what is left out stays. Empty texts and Guid.Empty clear.</summary>
public sealed class UpdateAssetRequest
{
    /// <summary>New tag.</summary>
    [StringLength(Asset.TagMaxLength)]
    public string? Tag { get; init; }

    /// <summary>The service request that asked for it (Guid.Empty clears).</summary>
    public Guid? RequestTicketId { get; init; }

    /// <summary>When it was bought (0001-01-01 clears).</summary>
    public DateOnly? PurchaseDate { get; init; }

    /// <summary>What it cost in AED.</summary>
    public decimal? PurchaseCost { get; init; }

    /// <summary>Who it was bought from.</summary>
    [StringLength(Asset.SupplierMaxLength)]
    public string? Supplier { get; init; }

    /// <summary>The order or invoice number.</summary>
    [StringLength(Asset.SupplierMaxLength)]
    public string? OrderNumber { get; init; }

    /// <summary>Notes.</summary>
    [StringLength(Asset.NotesMaxLength)]
    public string? Notes { get; init; }
}

/// <summary>A move along the lifecycle.</summary>
public sealed class MoveAssetRequest
{
    /// <summary>The new state.</summary>
    [Required]
    public AssetState? State { get; init; }

    /// <summary>The person, when it becomes assigned.</summary>
    public Guid? AssignedToId { get; init; }

    /// <summary>Why, for the history.</summary>
    [StringLength(500)]
    public string? Note { get; init; }
}

/// <summary>New asset settings.</summary>
public sealed class AssetSettingsRequest
{
    /// <summary>Prefix of generated tags.</summary>
    [StringLength(10)]
    public string? TagPrefix { get; init; }

    /// <summary>Days before an end date that alerts go out (1–365).</summary>
    public int? ExpiryWarningDays { get; init; }
}

// --- Responses ---

/// <summary>Asset settings.</summary>
internal sealed record AssetSettingsResponse(string TagPrefix, int ExpiryWarningDays);

/// <summary>A person, by id and name.</summary>
internal sealed record AssetPerson(Guid Id, string? DisplayName);

/// <summary>An asset in a list.</summary>
internal sealed record AssetSummaryResponse(
    Guid Id, string Tag, string Number, string Name, CiClassRefResponse? Class, AssetState State, CiStatus ItemStatus, AssetPerson? AssignedTo, DateOnly? WarrantyEndsOn)
{
    public static AssetSummaryResponse From(Asset a, ConfigurationItem i, CiClassIndex classes, IReadOnlyDictionary<Guid, string?> people, IReadOnlyDictionary<Guid, DateOnly> warranties) =>
        new(i.Id, a.Tag, i.Number, i.Name, CiClassRefResponse.From(classes, i.ClassId), a.State, i.Status,
            a.AssignedToId is { } p ? new AssetPerson(p, people.GetValueOrDefault(p)) : null, warranties.TryGetValue(i.Id, out var end) ? end : null);
}

/// <summary>A contract of an asset.</summary>
internal sealed record AssetContractRef(Guid Id, AssetContractKind Kind, string Name, string? Supplier, DateOnly? StartsOn, DateOnly EndsOn);

/// <summary>A licence that covers software found on the asset.</summary>
internal sealed record AssetLicenseRef(Guid Id, string Name, string Product);

/// <summary>The service request that asked for an asset.</summary>
internal sealed record AssetRequestRef(Guid Id, string Number, string Title);

/// <summary>One asset with everything about it.</summary>
internal sealed record AssetResponse(
    Guid Id, string Tag, string Number, string Name, CiClassRefResponse? Class, AssetState State, IReadOnlyList<AssetState> NextStates, CiStatus ItemStatus,
    AssetPerson? AssignedTo, DateTimeOffset? AssignedAt, AssetRequestRef? Request, DateOnly? PurchaseDate, decimal? PurchaseCost, string? Supplier, string? OrderNumber,
    string? Notes, IReadOnlyList<AssetContractRef> Contracts, IReadOnlyList<AssetLicenseRef> Licenses, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, string Etag)
{
    public static async Task<AssetResponse?> LoadAsync(ZelvoDbContext db, Guid id, CancellationToken cancellationToken)
    {
        var asset = await db.Assets.AsNoTracking().SingleOrDefaultAsync(a => a.ConfigurationItemId == id, cancellationToken).ConfigureAwait(false);
        if (asset is null)
        {
            return null;
        }

        var item = await db.ConfigurationItems.AsNoTracking().SingleAsync(i => i.Id == id, cancellationToken).ConfigureAwait(false);
        var classes = await CiClassIndex.LoadAsync(db, cancellationToken).ConfigureAwait(false);
        var people = await AssetEndpoints.NamesAsync(db, [asset.AssignedToId], cancellationToken).ConfigureAwait(false);
        var request = asset.RequestTicketId is { } t
            ? await db.Tickets.AsNoTracking().Where(x => x.Id == t).Select(x => new AssetRequestRef(x.Id, x.Number, x.Title)).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false)
            : null;
        var contracts = await (from ci in db.AssetContractItems.AsNoTracking()
                               join c in db.AssetContracts.AsNoTracking() on ci.ContractId equals c.Id
                               where ci.ConfigurationItemId == id
                               orderby c.EndsOn
                               select new AssetContractRef(c.Id, c.Kind, c.Name, c.Supplier, c.StartsOn, c.EndsOn)).ToListAsync(cancellationToken).ConfigureAwait(false);
        var products = await db.CiInstalledSoftwareItems.AsNoTracking().Where(s => s.ConfigurationItemId == id).Select(s => s.ProductKey).Distinct()
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var licenses = new List<AssetLicenseRef>();
        if (products.Count > 0)
        {
            foreach (var license in await db.SoftwareLicenses.AsNoTracking().OrderBy(l => l.Name).ToListAsync(cancellationToken).ConfigureAwait(false))
            {
                if (LicenseCompliance.Products(license).FirstOrDefault(products.Contains) is { } product)
                {
                    licenses.Add(new AssetLicenseRef(license.Id, license.Name, product));
                }
            }
        }

        return new AssetResponse(item.Id, asset.Tag, item.Number, item.Name, CiClassRefResponse.From(classes, item.ClassId), asset.State, AssetLifecycle.Next(asset.State), item.Status,
            asset.AssignedToId is { } p ? new AssetPerson(p, people.GetValueOrDefault(p)) : null, asset.AssignedAt, request, asset.PurchaseDate, asset.PurchaseCost, asset.Supplier,
            asset.OrderNumber, asset.Notes, contracts, licenses, asset.CreatedAt, asset.UpdatedAt, $"\"{asset.Version}\"");
    }
}

/// <summary>An asset assigned to a person (customer panel, My assets).</summary>
/// <param name="TicketCount">Tickets the item is on (staff only; null in My assets).</param>
internal sealed record PersonAssetResponse(
    Guid Id, string Tag, string Number, string Name, LocalizedTextDto? ClassPath, AssetState State, DateTimeOffset? AssignedAt, DateOnly? WarrantyEndsOn, int? TicketCount)
{
    public static async Task<List<PersonAssetResponse>> LoadAsync(ZelvoDbContext db, Guid person, bool withTickets, CancellationToken cancellationToken)
    {
        var rows = await (from a in db.Assets.AsNoTracking()
                          join i in db.ConfigurationItems.AsNoTracking() on a.ConfigurationItemId equals i.Id
                          where a.AssignedToId == person && a.State == AssetState.Assigned
                          orderby a.Tag
                          select new { Asset = a, Item = i }).Take(50).ToListAsync(cancellationToken).ConfigureAwait(false);
        var ids = rows.Select(r => r.Item.Id).ToList();
        var warranties = await AssetWarranties.EndsAsync(db, ids, cancellationToken).ConfigureAwait(false);
        var tickets = withTickets
            ? await db.TicketConfigurationItems.AsNoTracking().Where(l => ids.Contains(l.ConfigurationItemId)).GroupBy(l => l.ConfigurationItemId)
                .Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(g => g.Key, g => g.Count, cancellationToken).ConfigureAwait(false)
            : [];
        var classes = await CiClassIndex.LoadAsync(db, cancellationToken).ConfigureAwait(false);
        return [.. rows.Select(r => new PersonAssetResponse(r.Item.Id, r.Asset.Tag, r.Item.Number, r.Item.Name,
            classes.Find(r.Item.ClassId) is null ? null : LocalizedTextDto.From(classes.Path(r.Item.ClassId)), r.Asset.State, r.Asset.AssignedAt,
            warranties.TryGetValue(r.Item.Id, out var end) ? end : null, withTickets ? tickets.GetValueOrDefault(r.Item.Id) : null))];
    }
}
