using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Zelvo.Api.Conventions;
using Zelvo.Api.Forms;
using Zelvo.Api.Identity;
using Zelvo.Api.Tenancy;
using Zelvo.Connectors;
using Zelvo.Data;
using Zelvo.Data.Auditing;
using Zelvo.Data.Cmdb;
using Zelvo.Data.Connectors;
using Zelvo.Data.Localization;
using Zelvo.Data.Tenancy;
using Zelvo.Secrets;

namespace Zelvo.Api.Cmdb;

/// <summary>
/// Discovery sources, the discovery API, what the sources report and the reconciliation review
/// queue (task G2, module 18). Sources need <c>cmdb.discovery.manage</c> (people only); tools push
/// with <c>cmdb.manage</c>; the review queue needs <c>cmdb.manage</c>.
/// </summary>
internal static class CmdbDiscoveryEndpoints
{
    /// <summary>Most records in one push.</summary>
    public const int MaxPush = 1000;

    public static RouteGroupBuilder MapCmdbDiscoveryEndpoints(this RouteGroupBuilder group)
    {
        var cmdb = group.MapGroup("/cmdb").WithTags("CMDB discovery").RequireTenant();

        cmdb.MapGet("/discovery/sources", ListSourcesAsync)
            .WithName("ListDiscoverySources")
            .WithSummary("Discovery sources with their schedule, precedence and last sync. Needs cmdb.discovery.manage.")
            .RequirePermission(Permissions.CmdbDiscoveryManage)
            .Produces<IReadOnlyList<DiscoverySourceResponse>>();

        cmdb.MapPost("/discovery/sources", CreateSourceAsync)
            .WithName("CreateDiscoverySource")
            .WithSummary("Add a discovery source: the discovery API, Intune, Entra ID devices or Azure (an Entra ID connector's app), Active Directory (an LDAP connector) or vCenter (address, account and password, encrypted). Needs cmdb.discovery.manage; audited.")
            .RequirePermission(Permissions.CmdbDiscoveryManage)
            .Produces<DiscoverySourceResponse>(StatusCodes.Status201Created)
            .ProducesValidationProblem();

        cmdb.MapPatch("/discovery/sources/{id:guid}", UpdateSourceAsync)
            .WithName("UpdateDiscoverySource")
            .WithSummary("Change a source; a new vCenter address or account needs the password again. Send the ETag as If-Match. Needs cmdb.discovery.manage; audited.")
            .RequirePermission(Permissions.CmdbDiscoveryManage)
            .Produces<DiscoverySourceResponse>()
            .ProducesValidationProblem();

        cmdb.MapDelete("/discovery/sources/{id:guid}", DeleteSourceAsync)
            .WithName("DeleteDiscoverySource")
            .WithSummary("Remove a source and what it reported; its items stay. Needs cmdb.discovery.manage; audited.")
            .RequirePermission(Permissions.CmdbDiscoveryManage)
            .Produces(StatusCodes.Status204NoContent);

        cmdb.MapPost("/discovery/sources/{id:guid}/test", TestSourceAsync)
            .WithName("TestDiscoverySource")
            .WithSummary("Read the source without changing anything: how many records it reports, per class, and the first ones. Needs cmdb.discovery.manage.")
            .RequirePermission(Permissions.CmdbDiscoveryManage)
            .Produces<DiscoveryTestResponse>();

        cmdb.MapPost("/discovery/sources/{id:guid}/sync", SyncNowAsync)
            .WithName("SyncDiscoverySource")
            .WithSummary("Sync the source now (within a minute). Needs cmdb.discovery.manage.")
            .RequirePermission(Permissions.CmdbDiscoveryManage)
            .Produces(StatusCodes.Status202Accepted);

        cmdb.MapGet("/discovery/sources/{id:guid}/records", SourceRecordsAsync)
            .WithName("ListDiscoveryRecords")
            .WithSummary("What the source reports, with the item each record is tied to. Needs cmdb.discovery.manage.")
            .RequirePermission(Permissions.CmdbDiscoveryManage)
            .Produces<PageResult<SourceRecordResponse>>();

        cmdb.MapPost("/discovery/sources/{id:guid}/records", PushAsync)
            .WithName("PushDiscoveryRecords")
            .WithSummary("The discovery API: a tool sends up to 1,000 records (its id, class key, name, attribute values, installed software, relationships); they are reconciled at once. Needs cmdb.manage and a discovery API source.")
            .RequirePermission(Permissions.CmdbManage)
            .Produces<ReconcileStats>()
            .ProducesValidationProblem();

        cmdb.MapPost("/discovery/sources/{id:guid}/snapshot-complete", SnapshotCompleteAsync)
            .WithName("CompleteDiscoverySnapshot")
            .WithSummary("Ends a complete snapshot pushed in several calls: records not sent since startedAt are marked missing, and items no source reports any more. Needs cmdb.manage and a discovery API source.")
            .RequirePermission(Permissions.CmdbManage)
            .Produces<ReconcileStats>()
            .ProducesValidationProblem();

        cmdb.MapGet("/items/{id:guid}/discovery", ItemDiscoveryAsync)
            .WithName("GetCiDiscovery")
            .WithSummary("Which sources report the item, who set each value, and the software found on it. Needs cmdb.read.")
            .RequirePermission(Permissions.CmdbRead)
            .Produces<CiDiscoveryResponse>();

        cmdb.MapGet("/review", ReviewListAsync)
            .WithName("ListCiReviews")
            .WithSummary("The reconciliation review queue: possible duplicates and records that match several items. Needs cmdb.manage.")
            .RequirePermission(Permissions.CmdbManage)
            .Produces<PageResult<CiReviewResponse>>();

        cmdb.MapPost("/review/{id:guid}/merge", MergeAsync)
            .WithName("MergeCiDuplicates")
            .WithSummary("Merge a possible duplicate into the item kept (keepId): tickets, relationships, source records, software and missing values move; the other is retired. Needs cmdb.manage; audited.")
            .RequirePermission(Permissions.CmdbManage)
            .Produces<CiReviewResponse>();

        cmdb.MapPost("/review/{id:guid}/keep-apart", KeepApartAsync)
            .WithName("KeepCiItemsApart")
            .WithSummary("The two items are different; they are not suggested again. Needs cmdb.manage; audited.")
            .RequirePermission(Permissions.CmdbManage)
            .Produces<CiReviewResponse>();

        cmdb.MapPost("/review/{id:guid}/link", LinkAsync)
            .WithName("LinkCiRecord")
            .WithSummary("Tie a record that matched several items to one of them (itemId). Needs cmdb.manage; audited.")
            .RequirePermission(Permissions.CmdbManage)
            .Produces<CiReviewResponse>();

        cmdb.MapPost("/review/{id:guid}/create", CreateFromRecordAsync)
            .WithName("CreateCiFromRecord")
            .WithSummary("Make a new item for a record that matched several items. Needs cmdb.manage; audited.")
            .RequirePermission(Permissions.CmdbManage)
            .Produces<CiReviewResponse>();

        return group;
    }

    // --- Sources ---

    internal static async Task<Ok<List<DiscoverySourceResponse>>> ListSourcesAsync(ZelvoDbContext db, CancellationToken cancellationToken)
    {
        var sources = await db.CiDiscoverySources.AsNoTracking().OrderBy(s => s.Name).ToListAsync(cancellationToken);
        var counts = await CountsAsync(db, sources.Select(s => s.Id).ToList(), cancellationToken);
        var connectors = await ConnectorNamesAsync(db, cancellationToken);
        return TypedResults.Ok(sources.Select(s => DiscoverySourceResponse.From(s, connectors, counts)).ToList());
    }

    internal static async Task<Results<Created<DiscoverySourceResponse>, ValidationProblem, ProblemHttpResult>> CreateSourceAsync(
        CreateDiscoverySourceRequest request, HttpContext http, ZelvoDbContext db, ISecretProtector secrets, ITenantContext tenant, CancellationToken cancellationToken)
    {
        var kind = request.Kind!.Value;
        var source = new CiDiscoverySource { Name = request.Name!.Trim(), Kind = kind, TenantId = tenant.TenantId!.Value };
        if (await ApplyAsync(source, request.Name, request.ConnectorId, true, request.Settings, request.Credential, request.Active, request.Priority, request.AttributePriorities,
                request.SyncMinutes, db, secrets, cancellationToken) is { } invalid)
        {
            return invalid;
        }

        source.NextSyncAt = kind == DiscoverySourceKind.DiscoveryApi || !source.Active ? null : DateTimeOffset.UtcNow;
        db.CiDiscoverySources.Add(source);
        await db.SaveChangesAsync(cancellationToken);
        var saved = await db.CiDiscoverySources.AsNoTracking().SingleAsync(s => s.Id == source.Id, cancellationToken);
        var response = DiscoverySourceResponse.From(saved, await ConnectorNamesAsync(db, cancellationToken), new Dictionary<Guid, (int, int, int)>());
        http.Response.Headers.ETag = response.Etag;
        return TypedResults.Created($"/api/v1/cmdb/discovery/sources/{source.Id}", response);
    }

    internal static async Task<Results<Ok<DiscoverySourceResponse>, ValidationProblem, ProblemHttpResult>> UpdateSourceAsync(
        Guid id, UpdateDiscoverySourceRequest request, HttpContext http, ZelvoDbContext db, ISecretProtector secrets, CancellationToken cancellationToken)
    {
        var source = await db.CiDiscoverySources.SingleOrDefaultAsync(s => s.Id == id, cancellationToken);
        if (source is null)
        {
            return SourceNotFound();
        }

        if (!FormsCommon.Matches(http, source.Version))
        {
            return FormsCommon.VersionMismatch();
        }

        var wasActive = source.Active;
        if (await ApplyAsync(source, request.Name, request.ConnectorId, false, request.Settings, request.Credential, request.Active, request.Priority, request.AttributePriorities,
                request.SyncMinutes, db, secrets, cancellationToken) is { } invalid)
        {
            return invalid;
        }

        if (source.Kind != DiscoverySourceKind.DiscoveryApi && source.Active && !wasActive)
        {
            source.NextSyncAt = DateTimeOffset.UtcNow;
        }

        source.UpdatedAt = DateTimeOffset.UtcNow;
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return FormsCommon.VersionMismatch();
        }

        var saved = await db.CiDiscoverySources.AsNoTracking().SingleAsync(s => s.Id == id, cancellationToken);
        var response = DiscoverySourceResponse.From(saved, await ConnectorNamesAsync(db, cancellationToken), await CountsAsync(db, [id], cancellationToken));
        http.Response.Headers.ETag = response.Etag;
        return TypedResults.Ok(response);
    }

    internal static async Task<Results<NoContent, ProblemHttpResult>> DeleteSourceAsync(Guid id, ZelvoDbContext db, CancellationToken cancellationToken)
    {
        var source = await db.CiDiscoverySources.SingleOrDefaultAsync(s => s.Id == id, cancellationToken);
        if (source is null)
        {
            return SourceNotFound();
        }

        db.CiDiscoverySources.Remove(source);
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.NoContent();
    }

    internal static async Task<Results<Ok<DiscoveryTestResponse>, ProblemHttpResult>> TestSourceAsync(
        Guid id, ZelvoDbContext db, CmdbDiscoveryRunner runner, CancellationToken cancellationToken)
    {
        var source = await db.CiDiscoverySources.AsNoTracking().SingleOrDefaultAsync(s => s.Id == id, cancellationToken);
        if (source is null)
        {
            return SourceNotFound();
        }

        if (source.Kind == DiscoverySourceKind.DiscoveryApi)
        {
            return NotAPullSource();
        }

        var (records, error, log) = await runner.ReadAsync(source, cancellationToken);
        await runner.LogAsync(source, log, "test", cancellationToken);
        return TypedResults.Ok(new DiscoveryTestResponse(
            error is null, error, records?.Count ?? 0,
            (records ?? []).GroupBy(r => r.ClassKey, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal),
            [.. (records ?? []).Take(10).Select(r => new DiscoveryTestRecord(r.ExternalId, r.ClassKey, r.Name, r.Attributes.DeepClone()))]));
    }

    internal static async Task<Results<Accepted, ProblemHttpResult>> SyncNowAsync(Guid id, ZelvoDbContext db, IAuditLog audit, CancellationToken cancellationToken)
    {
        var source = await db.CiDiscoverySources.SingleOrDefaultAsync(s => s.Id == id, cancellationToken);
        if (source is null)
        {
            return SourceNotFound();
        }

        if (source.Kind == DiscoverySourceKind.DiscoveryApi)
        {
            return NotAPullSource();
        }

        if (!source.Active)
        {
            return (ProblemHttpResult)ApiProblem.Create(StatusCodes.Status409Conflict, "source-inactive", "Turn the source on first.");
        }

        source.NextSyncAt = DateTimeOffset.UtcNow;
        audit.Add(AuditCategory.Admin, "cmdb.discovery.sync-requested", "ci_discovery_source", id.ToString());
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Accepted($"/api/v1/cmdb/discovery/sources/{id}");
    }

    internal static async Task<Results<Ok<PageResult<SourceRecordResponse>>, ProblemHttpResult>> SourceRecordsAsync(
        Guid id, [AsParameters] PageQuery page, [AsParameters] SourceRecordQuery filter, ZelvoDbContext db, CancellationToken cancellationToken)
    {
        if (!await db.CiDiscoverySources.AnyAsync(s => s.Id == id, cancellationToken))
        {
            return SourceNotFound();
        }

        var query = db.CiSourceRecords.AsNoTracking().Where(r => r.SourceId == id);
        if (filter.Q?.Trim() is { Length: > 0 } q)
        {
            var pattern = CmdbItemEndpoints.LikePattern(q);
            query = query.Where(r => EF.Functions.ILike(r.Name, pattern) || EF.Functions.ILike(r.ExternalId, pattern));
        }

        if (filter.Missing is { } missing)
        {
            query = missing ? query.Where(r => r.MissingSince != null) : query.Where(r => r.MissingSince == null);
        }

        if (filter.State is { } state)
        {
            query = query.Where(r => r.State == state);
        }

        var total = await query.LongCountAsync(cancellationToken);
        var records = await query.OrderBy(r => r.Name).ThenBy(r => r.Id).Skip(page.Skip).Take(page.Size).ToListAsync(cancellationToken);
        var classes = await CiClassIndex.LoadAsync(db, cancellationToken);
        var itemIds = records.Where(r => r.ConfigurationItemId is not null).Select(r => r.ConfigurationItemId!.Value).ToList();
        var items = await db.ConfigurationItems.AsNoTracking().Where(i => itemIds.Contains(i.Id)).ToDictionaryAsync(i => i.Id, cancellationToken);
        return TypedResults.Ok(new PageResult<SourceRecordResponse>(
            [.. records.Select(r => new SourceRecordResponse(
                r.Id, r.ExternalId, r.Name, CiClassRefResponse.From(classes, r.ClassId), r.State,
                r.ConfigurationItemId is { } c && items.TryGetValue(c, out var item) ? CiItemRef.From(item) : null,
                r.PrimaryUser, JsonNode.Parse(r.Attributes), r.FirstSeenAt, r.LastSeenAt, r.MissingSince))],
            page.Number, page.Size, total));
    }

    internal static async Task<Results<Ok<ReconcileStats>, ValidationProblem, ProblemHttpResult>> PushAsync(
        Guid id, PushRecordsRequest request, ZelvoDbContext db, CmdbReconciler reconciler, IAuditLog audit, TimeProvider time, CancellationToken cancellationToken)
    {
        var source = await db.CiDiscoverySources.AsNoTracking().SingleOrDefaultAsync(s => s.Id == id, cancellationToken);
        if (source is null)
        {
            return SourceNotFound();
        }

        if (source.Kind != DiscoverySourceKind.DiscoveryApi)
        {
            return (ProblemHttpResult)ApiProblem.Create(StatusCodes.Status409Conflict, "not-a-push-source", "Only discovery API sources take records; Zelvo reads the others itself.");
        }

        if (!source.Active)
        {
            return (ProblemHttpResult)ApiProblem.Create(StatusCodes.Status409Conflict, "source-inactive", "The source is turned off.");
        }

        var records = request.Records ?? [];
        if (records.Count > MaxPush)
        {
            return UserEndpoints.Validation("records", $"Send at most {MaxPush} records at a time.");
        }

        var discovered = new List<DiscoveredRecord>(records.Count);
        for (var i = 0; i < records.Count; i++)
        {
            var r = records[i];
            if (string.IsNullOrWhiteSpace(r.Id) || string.IsNullOrWhiteSpace(r.Class) || string.IsNullOrWhiteSpace(r.Name))
            {
                return UserEndpoints.Validation($"records[{i}]", "Each record needs its id, class and name.");
            }

            if ((r.Software?.Count ?? 0) > 2000 || (r.Relationships?.Count ?? 0) > 100)
            {
                return UserEndpoints.Validation($"records[{i}]", "A record has at most 2,000 software entries and 100 relationships.");
            }

            discovered.Add(new DiscoveredRecord(r.Id.Trim(), r.Class.Trim(), r.Name, r.Attributes ?? [], r.User,
                [.. (r.Software ?? []).Where(s => !string.IsNullOrWhiteSpace(s.Name)).Select(s => new DiscoveredSoftware(s.Name!, s.Publisher, s.Version))],
                [.. (r.Relationships ?? []).Where(l => !string.IsNullOrWhiteSpace(l.Type) && !string.IsNullOrWhiteSpace(l.Target)).Select(l => new DiscoveredLink(l.Type!, l.Target!))]));
        }

        var started = time.GetUtcNow();
        var stats = await reconciler.ApplyAsync(source, discovered, false, cancellationToken);
        var tracked = await db.CiDiscoverySources.SingleAsync(s => s.Id == id, cancellationToken);
        tracked.LastSyncAt = started;
        tracked.LastSyncSucceeded = true;
        tracked.LastSyncStats = stats.Write();
        tracked.LastError = null;
        audit.Add(AuditCategory.Change, "cmdb.discovery.pushed", "ci_discovery_source", id.ToString(), stats.ToAudit());
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Ok(stats);
    }

    internal static async Task<Results<Ok<ReconcileStats>, ValidationProblem, ProblemHttpResult>> SnapshotCompleteAsync(
        Guid id, SnapshotCompleteRequest request, ZelvoDbContext db, CmdbReconciler reconciler, IAuditLog audit, TimeProvider time, CancellationToken cancellationToken)
    {
        var source = await db.CiDiscoverySources.AsNoTracking().SingleOrDefaultAsync(s => s.Id == id, cancellationToken);
        if (source is null)
        {
            return SourceNotFound();
        }

        if (source.Kind != DiscoverySourceKind.DiscoveryApi)
        {
            return (ProblemHttpResult)ApiProblem.Create(StatusCodes.Status409Conflict, "not-a-push-source", "Only discovery API sources take records; Zelvo reads the others itself.");
        }

        var startedAt = request.StartedAt!.Value;
        if (startedAt > time.GetUtcNow() || startedAt < time.GetUtcNow().AddDays(-7))
        {
            return UserEndpoints.Validation("startedAt", "The snapshot started in the last 7 days.");
        }

        var stats = new ReconcileStats { Missing = await reconciler.MarkMissingAsync(id, startedAt, cancellationToken) };
        audit.Add(AuditCategory.Change, "cmdb.discovery.snapshot-completed", "ci_discovery_source", id.ToString(), stats.ToAudit());
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Ok(stats);
    }

    internal static async Task<Results<Ok<CiDiscoveryResponse>, ProblemHttpResult>> ItemDiscoveryAsync(Guid id, ZelvoDbContext db, CancellationToken cancellationToken)
    {
        var item = await db.ConfigurationItems.AsNoTracking().SingleOrDefaultAsync(i => i.Id == id, cancellationToken);
        if (item is null)
        {
            return CmdbItemEndpoints.NotFound();
        }

        var sources = await db.CiDiscoverySources.AsNoTracking().ToDictionaryAsync(s => s.Id, cancellationToken);
        var records = await db.CiSourceRecords.AsNoTracking().Where(r => r.ConfigurationItemId == id).OrderBy(r => r.FirstSeenAt).ToListAsync(cancellationToken);
        var software = await db.CiInstalledSoftwareItems.AsNoTracking().Where(s => s.ConfigurationItemId == id).OrderBy(s => s.Name).ThenBy(s => s.Version).Take(1000)
            .ToListAsync(cancellationToken);
        DiscoverySourceRef? Ref(Guid sourceId) => sources.TryGetValue(sourceId, out var s) ? new DiscoverySourceRef(s.Id, s.Name, s.Kind) : null;
        var setters = new Dictionary<string, ValueSetterResponse>(StringComparer.Ordinal);
        foreach (var (key, value) in CmdbPrecedence.ReadSources(item.AttributeSources))
        {
            var setter = (string?)value;
            setters[key] = setter == CmdbPrecedence.Manual
                ? new ValueSetterResponse(true, null)
                : new ValueSetterResponse(false, Guid.TryParse(setter, out var sid) ? Ref(sid) : null);
        }

        ConfigurationItem? merged = item.MergedIntoId is { } m ? await db.ConfigurationItems.AsNoTracking().SingleOrDefaultAsync(i => i.Id == m, cancellationToken) : null;
        return TypedResults.Ok(new CiDiscoveryResponse(
            item.MissingSince, merged is null ? null : CiItemRef.From(merged),
            [.. records.Select(r => new CiSourceRecordRef(Ref(r.SourceId), r.ExternalId, r.Name, r.FirstSeenAt, r.LastSeenAt, r.MissingSince))],
            setters,
            [.. software.Select(s => new InstalledSoftwareResponse(s.Name, s.Publisher, s.Version.Length == 0 ? null : s.Version, Ref(s.SourceId), s.LastSeenAt))]));
    }

    // --- Review queue ---

    internal static async Task<Ok<PageResult<CiReviewResponse>>> ReviewListAsync(
        [AsParameters] PageQuery page, [AsParameters] CiReviewQuery filter, ZelvoDbContext db, CancellationToken cancellationToken)
    {
        var query = db.CiReviewItems.AsNoTracking().Where(r => r.State == (filter.State ?? CiReviewState.Open));
        if (filter.Kind is { } kind)
        {
            query = query.Where(r => r.Kind == kind);
        }

        var total = await query.LongCountAsync(cancellationToken);
        var reviews = await query.OrderBy(r => r.CreatedAt).ThenBy(r => r.Id).Skip(page.Skip).Take(page.Size).ToListAsync(cancellationToken);
        var responses = await CiReviewResponse.LoadAsync(db, reviews, cancellationToken);
        return TypedResults.Ok(new PageResult<CiReviewResponse>(responses, page.Number, page.Size, total));
    }

    internal static async Task<Results<Ok<CiReviewResponse>, ValidationProblem, ProblemHttpResult>> MergeAsync(
        Guid id, MergeCiRequest request, ZelvoDbContext db, CurrentUser user, CmdbMerger merger, IAuditLog audit, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var review = await OpenReviewAsync(db, id, cancellationToken);
        if (review is null)
        {
            return ReviewNotFound();
        }

        if (review.Kind != CiReviewKind.PossibleDuplicate)
        {
            return FormsCommon.State("Only possible duplicates are merged.");
        }

        if (request.KeepId != review.ConfigurationItemId && request.KeepId != review.OtherItemId)
        {
            return UserEndpoints.Validation("keepId", "Keep one of the two items.");
        }

        var dropId = request.KeepId == review.ConfigurationItemId ? review.OtherItemId!.Value : review.ConfigurationItemId!.Value;
        var keep = await db.ConfigurationItems.SingleAsync(i => i.Id == request.KeepId, cancellationToken);
        var drop = await db.ConfigurationItems.SingleAsync(i => i.Id == dropId, cancellationToken);
        if (keep.Status == CiStatus.Retired || drop.Status == CiStatus.Retired)
        {
            return FormsCommon.State("A retired item is not merged.");
        }

        await merger.MergeAsync(keep, drop, cancellationToken);
        Decide(review, CiReviewState.Merged, user, audit);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return TypedResults.Ok((await CiReviewResponse.LoadAsync(db, [review], cancellationToken))[0]);
    }

    internal static async Task<Results<Ok<CiReviewResponse>, ProblemHttpResult>> KeepApartAsync(
        Guid id, ZelvoDbContext db, CurrentUser user, IAuditLog audit, CancellationToken cancellationToken)
    {
        var review = await OpenReviewAsync(db, id, cancellationToken);
        if (review is null)
        {
            return ReviewNotFound();
        }

        if (review.Kind != CiReviewKind.PossibleDuplicate)
        {
            return FormsCommon.State("Only possible duplicates are kept apart.");
        }

        Decide(review, CiReviewState.KeptApart, user, audit);
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Ok((await CiReviewResponse.LoadAsync(db, [review], cancellationToken))[0]);
    }

    internal static async Task<Results<Ok<CiReviewResponse>, ValidationProblem, ProblemHttpResult>> LinkAsync(
        Guid id, LinkCiRecordRequest request, ZelvoDbContext db, CurrentUser user, CmdbReconciler reconciler, IAuditLog audit, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var review = await OpenReviewAsync(db, id, cancellationToken);
        if (review is null)
        {
            return ReviewNotFound();
        }

        if (review.Kind != CiReviewKind.Ambiguous)
        {
            return FormsCommon.State("Only records that matched several items are linked.");
        }

        var candidates = JsonSerializer.Deserialize<List<Guid>>(review.Candidates) ?? [];
        var item = candidates.Contains(request.ItemId!.Value)
            ? await db.ConfigurationItems.SingleOrDefaultAsync(i => i.Id == request.ItemId && i.Status != CiStatus.Retired, cancellationToken)
            : null;
        if (item is null)
        {
            return UserEndpoints.Validation("itemId", "Choose one of the items the record matched.");
        }

        var record = await db.CiSourceRecords.SingleAsync(r => r.Id == review.SourceRecordId, cancellationToken);
        await reconciler.ResolveAsync(record, item, cancellationToken);
        Decide(review, CiReviewState.Linked, user, audit);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return TypedResults.Ok((await CiReviewResponse.LoadAsync(db, [review], cancellationToken))[0]);
    }

    internal static async Task<Results<Ok<CiReviewResponse>, ProblemHttpResult>> CreateFromRecordAsync(
        Guid id, ZelvoDbContext db, CurrentUser user, CmdbReconciler reconciler, IAuditLog audit, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var review = await OpenReviewAsync(db, id, cancellationToken);
        if (review is null)
        {
            return ReviewNotFound();
        }

        if (review.Kind != CiReviewKind.Ambiguous)
        {
            return FormsCommon.State("Only records that matched several items get a new item.");
        }

        var record = await db.CiSourceRecords.SingleAsync(r => r.Id == review.SourceRecordId, cancellationToken);
        var classes = await CiClassIndex.LoadAsync(db, cancellationToken);
        if (!classes.Selectable(record.ClassId))
        {
            return FormsCommon.State("The record's class takes no new items.");
        }

        await reconciler.ResolveAsync(record, null, cancellationToken);
        Decide(review, CiReviewState.Created, user, audit);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return TypedResults.Ok((await CiReviewResponse.LoadAsync(db, [review], cancellationToken))[0]);
    }

    // --- Helpers ---

    private static async Task<CiReviewItem?> OpenReviewAsync(ZelvoDbContext db, Guid id, CancellationToken cancellationToken)
    {
        // One decision at a time: the row is locked until the transaction ends.
        var locked = db.Database.CurrentTransaction is null
            ? null
            : await db.CiReviewItems.FromSql($"SELECT * FROM ci_review_items WHERE id = {id} FOR UPDATE").SingleOrDefaultAsync(cancellationToken);
        var review = locked ?? await db.CiReviewItems.SingleOrDefaultAsync(r => r.Id == id, cancellationToken);
        return review is { State: CiReviewState.Open } ? review : null;
    }

    private static void Decide(CiReviewItem review, CiReviewState state, CurrentUser user, IAuditLog audit)
    {
        review.State = state;
        review.DecidedById = user.Account?.Id;
        review.DecidedAt = DateTimeOffset.UtcNow;
        audit.Add(AuditCategory.Change, "cmdb.review.decided", "ci_review_item", review.Id.ToString(),
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["kind"] = review.Kind.ToString(), ["decision"] = state.ToString() });
    }

    private static async Task<ProblemHttpResult?> ApplyAsync(
        CiDiscoverySource source, string? name, Guid? connectorId, bool creating, JsonElement? settingsJson, string? credential, bool? active, int? priority,
        Dictionary<string, int>? attributePriorities, int? syncMinutes, ZelvoDbContext db, ISecretProtector secrets, CancellationToken cancellationToken)
    {
        if (name is not null)
        {
            var trimmed = name.Trim();
            if (trimmed.Length is 0 or > CiDiscoverySource.NameMaxLength)
            {
                return Invalid("name", "name-required");
            }

            if (await db.CiDiscoverySources.AnyAsync(s => s.Name == trimmed && s.Id != source.Id, cancellationToken))
            {
                return (ProblemHttpResult)ApiProblem.Create(StatusCodes.Status409Conflict, "name-taken", "Another source has this name.");
            }

            source.Name = trimmed;
        }

        var needsConnector = source.Kind switch
        {
            DiscoverySourceKind.Intune or DiscoverySourceKind.EntraDevices or DiscoverySourceKind.Azure => ConnectorKind.EntraId,
            DiscoverySourceKind.ActiveDirectory => ConnectorKind.Ldap,
            _ => (ConnectorKind?)null,
        };
        if (creating || connectorId is not null)
        {
            if (needsConnector is null)
            {
                if (connectorId is not null && connectorId != Guid.Empty)
                {
                    return Invalid("connectorId", "connector-not-used");
                }
            }
            else
            {
                var connector = connectorId is { } cid ? await db.Connectors.AsNoTracking().SingleOrDefaultAsync(c => c.Id == cid, cancellationToken) : null;
                if (connector is null || connector.Kind != needsConnector)
                {
                    return Invalid("connectorId", "connector-required");
                }

                source.ConnectorId = connector.Id;
            }
        }

        var previous = DiscoverySettings.Read(source.Settings);
        if (settingsJson is { } json)
        {
            DiscoverySettings parsed;
            try
            {
                parsed = DiscoverySettings.Read(json.GetRawText());
            }
            catch (JsonException)
            {
                return Invalid("settings", "settings-invalid");
            }

            if (parsed.Problems(source.Kind) is { Count: > 0 } problems)
            {
                return Invalid($"settings.{problems[0].Field}", problems[0].Code, problems);
            }

            source.Settings = parsed.Write();
        }
        else if (creating && DiscoverySettings.Read(source.Settings).Problems(source.Kind) is { Count: > 0 } problems)
        {
            return Invalid($"settings.{problems[0].Field}", problems[0].Code, problems);
        }

        if (source.Kind == DiscoverySourceKind.VCenter)
        {
            var now = DiscoverySettings.Read(source.Settings);
            // A new address or account never gets the password stored for the old one.
            var moved = !creating && (!string.Equals(now.Url, previous.Url, StringComparison.OrdinalIgnoreCase) || !string.Equals(now.Username, previous.Username, StringComparison.Ordinal));
            if (credential is { Length: > 0 })
            {
                if (credential.Length > 1000)
                {
                    return Invalid("credential", "credential-invalid");
                }

                source.CredentialCiphertext = await secrets.ProtectAsync(CmdbDiscoveryRunner.Purpose(source.Id), credential, cancellationToken);
                source.CredentialSetAt = DateTimeOffset.UtcNow;
            }
            else if (creating || moved || source.CredentialCiphertext is null)
            {
                return Invalid("credential", "credential-required");
            }
        }
        else if (credential is { Length: > 0 })
        {
            return Invalid("credential", "credential-not-used");
        }

        if (active is { } on)
        {
            source.Active = on;
            if (!on)
            {
                source.NextSyncAt = null;
            }
        }

        if (priority is { } p)
        {
            if (p is < 1 or > CiDiscoverySource.LowestPriority)
            {
                return Invalid("priority", "priority-invalid");
            }

            source.Priority = p;
        }

        if (attributePriorities is not null)
        {
            if (attributePriorities.Count > 100 || attributePriorities.Any(a => a.Key.Length is 0 or > 100 || a.Value is < 1 or > CiDiscoverySource.LowestPriority))
            {
                return Invalid("attributePriorities", "priority-invalid");
            }

            source.AttributePriorities = JsonSerializer.Serialize(attributePriorities);
        }

        if (syncMinutes is { } minutes)
        {
            if (minutes is < 15 or > 10080)
            {
                return Invalid("syncMinutes", "sync-minutes-invalid");
            }

            source.SyncMinutes = minutes;
        }

        return null;
    }

    private static ProblemHttpResult Invalid(string field, string code, IEnumerable<(string Field, string Code)>? all = null) =>
        TypedResults.Problem(
            statusCode: StatusCodes.Status400BadRequest, title: "The source's settings have a problem.", type: ApiProblem.TypeFor(code),
            extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["code"] = code,
                ["field"] = field,
                ["problems"] = (all ?? [(field, code)]).Select(p => new { field = p.Field, code = p.Code }).ToArray(),
            });

    internal static ProblemHttpResult SourceNotFound() => FormsCommon.NotFound("source");

    private static ProblemHttpResult ReviewNotFound() => FormsCommon.NotFound("review");

    private static ProblemHttpResult NotAPullSource() =>
        (ProblemHttpResult)ApiProblem.Create(StatusCodes.Status409Conflict, "not-a-pull-source", "Tools push records to discovery API sources; Zelvo does not read them.");

    private static async Task<Dictionary<Guid, string>> ConnectorNamesAsync(ZelvoDbContext db, CancellationToken cancellationToken) =>
        await db.Connectors.AsNoTracking().ToDictionaryAsync(c => c.Id, c => c.Name, cancellationToken);

    private static async Task<Dictionary<Guid, (int Records, int Missing, int Review)>> CountsAsync(ZelvoDbContext db, List<Guid> ids, CancellationToken cancellationToken) =>
        (await db.CiSourceRecords.AsNoTracking().Where(r => ids.Contains(r.SourceId)).GroupBy(r => r.SourceId)
            .Select(g => new { g.Key, Records = g.Count(), Missing = g.Count(r => r.MissingSince != null), Review = g.Count(r => r.State == SourceRecordState.Review) })
            .ToListAsync(cancellationToken)).ToDictionary(g => g.Key, g => (g.Records, g.Missing, g.Review));
}

// --- Requests ---

/// <summary>A new discovery source.</summary>
public sealed class CreateDiscoverySourceRequest
{
    /// <summary>Name shown to administrators, unique in the tenant.</summary>
    [Required, StringLength(CiDiscoverySource.NameMaxLength)]
    public string? Name { get; init; }

    /// <summary>What it reads, or DiscoveryApi for a tool that pushes.</summary>
    [Required]
    public DiscoverySourceKind? Kind { get; init; }

    /// <summary>Intune, Entra ID devices and Azure: an Entra ID connector; Active Directory: an LDAP connector.</summary>
    public Guid? ConnectorId { get; init; }

    /// <summary>
    /// Settings by kind: <c>classMap</c> (Zelvo's class key → yours), Intune <c>includeApps</c>, Azure
    /// <c>subscriptionIds</c> and <c>resourceTypes</c>, Active Directory <c>baseDn</c> and
    /// <c>computerFilter</c>, vCenter <c>url</c>, <c>username</c> and <c>certificateThumbprint</c>.
    /// </summary>
    public JsonElement? Settings { get; init; }

    /// <summary>vCenter: the account's password (stored encrypted, never returned).</summary>
    public string? Credential { get; init; }

    /// <summary>On (default yes).</summary>
    public bool? Active { get; init; }

    /// <summary>Precedence of its values, 1 (most trusted) to 100 (default 50).</summary>
    public int? Priority { get; init; }

    /// <summary>Precedence per attribute key (and <c>name</c>).</summary>
    public Dictionary<string, int>? AttributePriorities { get; init; }

    /// <summary>Minutes between syncs, 15 to 10,080 (default 1,440: daily).</summary>
    public int? SyncMinutes { get; init; }
}

/// <summary>Changes to a source; what is left out stays.</summary>
public sealed class UpdateDiscoverySourceRequest
{
    /// <summary>New name.</summary>
    [StringLength(CiDiscoverySource.NameMaxLength)]
    public string? Name { get; init; }

    /// <summary>Another connector of the same kind.</summary>
    public Guid? ConnectorId { get; init; }

    /// <summary>All settings (they replace the stored ones).</summary>
    public JsonElement? Settings { get; init; }

    /// <summary>vCenter: a new password; needed with a new address or account.</summary>
    public string? Credential { get; init; }

    /// <summary>On or off.</summary>
    public bool? Active { get; init; }

    /// <summary>Precedence of its values.</summary>
    public int? Priority { get; init; }

    /// <summary>Precedence per attribute key (all of them).</summary>
    public Dictionary<string, int>? AttributePriorities { get; init; }

    /// <summary>Minutes between syncs.</summary>
    public int? SyncMinutes { get; init; }
}

/// <summary>Records a tool sends through the discovery API.</summary>
public sealed class PushRecordsRequest
{
    /// <summary>Up to 1,000 records.</summary>
    [Required]
    public List<PushedRecord>? Records { get; init; }
}

/// <summary>One record of a push.</summary>
public sealed class PushedRecord
{
    /// <summary>The tool's own id of the record (stable between pushes).</summary>
    [StringLength(CiSourceRecord.ExternalIdMaxLength)]
    public string? Id { get; init; }

    /// <summary>The class key, for example <c>server</c> or <c>laptop</c>.</summary>
    [StringLength(CiClass.KeyMaxLength)]
    public string? Class { get; init; }

    /// <summary>The item's name.</summary>
    [StringLength(ConfigurationItem.NameMaxLength)]
    public string? Name { get; init; }

    /// <summary>Attribute values by key; keys the class does not have are left out.</summary>
    public JsonObject? Attributes { get; init; }

    /// <summary>The person who uses it (email), for assets.</summary>
    [StringLength(CiSourceRecord.PrimaryUserMaxLength)]
    public string? User { get; init; }

    /// <summary>Software installed on it (all of it: what is left out is removed).</summary>
    public List<PushedSoftware>? Software { get; init; }

    /// <summary>Relationships to other records of the same source.</summary>
    public List<PushedLink>? Relationships { get; init; }
}

/// <summary>Software of a pushed record.</summary>
public sealed class PushedSoftware
{
    /// <summary>Product name.</summary>
    [StringLength(CiInstalledSoftware.NameMaxLength)]
    public string? Name { get; init; }

    /// <summary>Publisher.</summary>
    [StringLength(CiInstalledSoftware.NameMaxLength)]
    public string? Publisher { get; init; }

    /// <summary>Version.</summary>
    [StringLength(CiInstalledSoftware.VersionMaxLength)]
    public string? Version { get; init; }
}

/// <summary>A relationship of a pushed record.</summary>
public sealed class PushedLink
{
    /// <summary>Relationship type key, for example <c>runs-on</c>.</summary>
    [StringLength(CiRelationshipType.KeyMaxLength)]
    public string? Type { get; init; }

    /// <summary>The other record's id at the tool.</summary>
    [StringLength(CiSourceRecord.ExternalIdMaxLength)]
    public string? Target { get; init; }
}

/// <summary>The end of a complete snapshot pushed in several calls.</summary>
public sealed class SnapshotCompleteRequest
{
    /// <summary>When the tool started sending the snapshot.</summary>
    [Required]
    public DateTimeOffset? StartedAt { get; init; }
}

/// <summary>Which item to keep.</summary>
public sealed class MergeCiRequest
{
    /// <summary>One of the two items.</summary>
    [Required]
    public Guid? KeepId { get; init; }
}

/// <summary>Which item a record is.</summary>
public sealed class LinkCiRecordRequest
{
    /// <summary>One of the items it matched.</summary>
    [Required]
    public Guid? ItemId { get; init; }
}

/// <summary>Filters of a source's records.</summary>
public sealed class SourceRecordQuery
{
    /// <summary>Words in the name or id.</summary>
    [MaxLength(200)]
    public string? Q { get; init; }

    /// <summary>Only records no longer reported (true) or still reported (false).</summary>
    public bool? Missing { get; init; }

    /// <summary>Linked or waiting for review.</summary>
    public SourceRecordState? State { get; init; }
}

/// <summary>Filters of the review queue.</summary>
public sealed class CiReviewQuery
{
    /// <summary>Open (default) or decided ones.</summary>
    public CiReviewState? State { get; init; }

    /// <summary>Only one kind.</summary>
    public CiReviewKind? Kind { get; init; }
}

// --- Responses ---

/// <summary>A discovery source (its credential is never returned).</summary>
internal sealed record DiscoverySourceResponse(
    Guid Id, string Name, DiscoverySourceKind Kind, Guid? ConnectorId, string? ConnectorName, JsonNode? Settings, bool HasCredential, DateTimeOffset? CredentialSetAt,
    bool Active, int Priority, IReadOnlyDictionary<string, int> AttributePriorities, int SyncMinutes, DateTimeOffset? NextSyncAt, DateTimeOffset? LastSyncAt,
    bool? LastSyncSucceeded, ReconcileStats? LastSyncStats, string? LastError, int RecordCount, int MissingCount, int ReviewCount, string Etag)
{
    public static DiscoverySourceResponse From(CiDiscoverySource s, IReadOnlyDictionary<Guid, string> connectors, IReadOnlyDictionary<Guid, (int Records, int Missing, int Review)> counts)
    {
        var count = counts.GetValueOrDefault(s.Id);
        return new(s.Id, s.Name, s.Kind, s.ConnectorId, s.ConnectorId is { } c ? connectors.GetValueOrDefault(c) : null, JsonNode.Parse(DiscoverySettings.Read(s.Settings).Write()),
            s.CredentialCiphertext is not null, s.CredentialSetAt, s.Active, s.Priority, CmdbPrecedence.ReadPriorities(s.AttributePriorities), s.SyncMinutes, s.NextSyncAt,
            s.LastSyncAt, s.LastSyncSucceeded, ReconcileStats.Read(s.LastSyncStats), s.LastError, count.Records, count.Missing, count.Review, $"\"{s.Version}\"");
    }
}

/// <summary>What a test read.</summary>
internal sealed record DiscoveryTestResponse(bool Ok, string? Error, int Count, IReadOnlyDictionary<string, int> Classes, IReadOnlyList<DiscoveryTestRecord> Sample);

/// <summary>A record a test read.</summary>
internal sealed record DiscoveryTestRecord(string Id, string Class, string Name, JsonNode Attributes);

/// <summary>A class as a reference.</summary>
internal sealed record CiClassRefResponse(Guid Id, string Key, CiFamily Family, LocalizedTextDto Path)
{
    public static CiClassRefResponse? From(CiClassIndex classes, Guid id) =>
        classes.Find(id) is { } c ? new CiClassRefResponse(c.Id, c.Key, c.Family, LocalizedTextDto.From(classes.Path(id))) : null;
}

/// <summary>An item as a reference.</summary>
internal sealed record CiItemRef(Guid Id, string Number, string Name, CiStatus Status)
{
    public static CiItemRef From(ConfigurationItem i) => new(i.Id, i.Number, i.Name, i.Status);
}

/// <summary>A record of a source.</summary>
internal sealed record SourceRecordResponse(
    Guid Id, string ExternalId, string Name, CiClassRefResponse? Class, SourceRecordState State, CiItemRef? Item, string? PrimaryUser, JsonNode? Attributes,
    DateTimeOffset FirstSeenAt, DateTimeOffset LastSeenAt, DateTimeOffset? MissingSince);

/// <summary>A source as a reference.</summary>
internal sealed record DiscoverySourceRef(Guid Id, string Name, DiscoverySourceKind Kind);

/// <summary>A source's record of an item.</summary>
internal sealed record CiSourceRecordRef(DiscoverySourceRef? Source, string ExternalId, string Name, DateTimeOffset FirstSeenAt, DateTimeOffset LastSeenAt, DateTimeOffset? MissingSince);

/// <summary>Who set a value last: a person (or import) or a source.</summary>
internal sealed record ValueSetterResponse(bool Manual, DiscoverySourceRef? Source);

/// <summary>Software found on an item.</summary>
internal sealed record InstalledSoftwareResponse(string Name, string? Publisher, string? Version, DiscoverySourceRef? Source, DateTimeOffset LastSeenAt);

/// <summary>Discovery of an item.</summary>
internal sealed record CiDiscoveryResponse(
    DateTimeOffset? MissingSince, CiItemRef? MergedInto, IReadOnlyList<CiSourceRecordRef> Records, IReadOnlyDictionary<string, ValueSetterResponse> ValueSetters,
    IReadOnlyList<InstalledSoftwareResponse> Software);

/// <summary>An item of a review, with what it needs to be compared.</summary>
internal sealed record CiReviewItemSide(Guid Id, string Number, string Name, CiStatus Status, CiClassRefResponse? Class, JsonNode? Attributes, int TicketCount, IReadOnlyList<string> Sources);

/// <summary>The record of an ambiguous review.</summary>
internal sealed record CiReviewRecord(string Source, string ExternalId, string Name, CiClassRefResponse? Class, JsonNode? Attributes);

/// <summary>A review of the queue.</summary>
internal sealed record CiReviewResponse(
    Guid Id, CiReviewKind Kind, CiReviewState State, string MatchedOn, string? MatchedValue, IReadOnlyList<CiReviewItemSide> Items, CiReviewRecord? Record,
    DateTimeOffset CreatedAt, DateTimeOffset? DecidedAt, string? DecidedBy)
{
    public static async Task<List<CiReviewResponse>> LoadAsync(ZelvoDbContext db, IReadOnlyList<CiReviewItem> reviews, CancellationToken cancellationToken)
    {
        var classes = await CiClassIndex.LoadAsync(db, cancellationToken);
        var itemIds = reviews.SelectMany<CiReviewItem, Guid>(r => r.Kind == CiReviewKind.PossibleDuplicate
            ? [r.ConfigurationItemId!.Value, r.OtherItemId!.Value]
            : JsonSerializer.Deserialize<List<Guid>>(r.Candidates) ?? []).Distinct().ToList();
        var items = await db.ConfigurationItems.AsNoTracking().Where(i => itemIds.Contains(i.Id)).ToDictionaryAsync(i => i.Id, cancellationToken);
        var tickets = await db.TicketConfigurationItems.AsNoTracking().Where(l => itemIds.Contains(l.ConfigurationItemId)).GroupBy(l => l.ConfigurationItemId)
            .Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(g => g.Key, g => g.Count, cancellationToken);
        var sourceNames = await db.CiDiscoverySources.AsNoTracking().ToDictionaryAsync(s => s.Id, s => s.Name, cancellationToken);
        var itemSources = (await db.CiSourceRecords.AsNoTracking().Where(r => r.ConfigurationItemId != null && itemIds.Contains(r.ConfigurationItemId.Value))
            .Select(r => new { r.ConfigurationItemId, r.SourceId }).ToListAsync(cancellationToken))
            .GroupBy(r => r.ConfigurationItemId!.Value).ToDictionary(g => g.Key, g => g.Select(r => sourceNames.GetValueOrDefault(r.SourceId) ?? "?").Distinct().ToList());
        var recordIds = reviews.Where(r => r.SourceRecordId is not null).Select(r => r.SourceRecordId!.Value).ToList();
        var records = await db.CiSourceRecords.AsNoTracking().Where(r => recordIds.Contains(r.Id)).ToDictionaryAsync(r => r.Id, cancellationToken);
        var deciders = reviews.Where(r => r.DecidedById is not null).Select(r => r.DecidedById!.Value).Distinct().ToList();
        var names = await db.Users.AsNoTracking().Where(u => deciders.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, cancellationToken);

        CiReviewItemSide? Side(Guid id) => items.TryGetValue(id, out var i)
            ? new CiReviewItemSide(i.Id, i.Number, i.Name, i.Status, CiClassRefResponse.From(classes, i.ClassId), JsonNode.Parse(i.Attributes), tickets.GetValueOrDefault(i.Id),
                itemSources.GetValueOrDefault(i.Id) ?? [])
            : null;

        return [.. reviews.Select(r =>
        {
            var sides = r.Kind == CiReviewKind.PossibleDuplicate
                ? new[] { r.ConfigurationItemId!.Value, r.OtherItemId!.Value }
                : (JsonSerializer.Deserialize<List<Guid>>(r.Candidates) ?? []).ToArray();
            var record = r.SourceRecordId is { } rid && records.TryGetValue(rid, out var sr)
                ? new CiReviewRecord(sourceNames.GetValueOrDefault(sr.SourceId) ?? "?", sr.ExternalId, sr.Name, CiClassRefResponse.From(classes, sr.ClassId), JsonNode.Parse(sr.Attributes))
                : null;
            return new CiReviewResponse(r.Id, r.Kind, r.State, r.MatchedOn, r.MatchedValue, [.. sides.Select(Side).Where(s => s is not null).Select(s => s!)], record,
                r.CreatedAt, r.DecidedAt, r.DecidedById is { } d ? names.GetValueOrDefault(d) : null);
        })];
    }
}
