using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;
using Zelvo.Api.Conventions;
using Zelvo.Api.Forms;
using Zelvo.Api.Identity;
using Zelvo.Api.ServiceDesk;
using Zelvo.Api.Tenancy;
using Zelvo.Data;
using Zelvo.Data.Auditing;
using Zelvo.Data.Catalog;
using Zelvo.Data.Directory;
using Zelvo.Data.Identity;
using Zelvo.Data.Tickets;
using Zelvo.Tickets;
using Zelvo.Tickets.Forms;

namespace Zelvo.Api.Catalog;

/// <summary>
/// The catalog as requesters see it (task C2): active catalogs and the published services the caller
/// may see, the full service card with its FAQ, and the form to request it. Hidden services answer
/// 404 to anyone outside the allowed groups. Visitors who are not signed in see public services
/// and sign in to request them (product owner decision 2026-11-24).
/// </summary>
internal static class CatalogBrowseEndpoints
{
    public static RouteGroupBuilder MapCatalogBrowseEndpoints(this RouteGroupBuilder group)
    {
        var published = group.MapGroup("/catalog/published").WithTags("Catalog").RequireTenant();
        published.MapGet("/", ListAsync).WithName("BrowseCatalog")
            .WithSummary("Active catalogs and the published services the caller may see (visitors: public services only), filtered by catalog, category (or below), main service, audience or text. Suspended services are listed with their message.")
            .AllowAnonymousAccess().Produces<CatalogBrowseResponse>();
        published.MapGet("/{id:guid}", GetAsync).WithName("GetPublishedService")
            .WithSummary("A published service's card for requesters: description, documents with templates, steps, fee, days, channels, audience, partners, FAQ, its main service and sub-services, related and prerequisite services the caller may see, and whether the caller may request it (also on behalf of someone). 404 when hidden from the caller.")
            .AllowAnonymousAccess().Produces<PublishedServiceResponse>();
        published.MapGet("/{id:guid}/request-form", RequestFormAsync).WithName("GetPublishedServiceRequestForm")
            .WithSummary("The service's published form with the requester's defaults. People requesting on behalf of someone (roles the service allows) name the requester.")
            .RequireAuthorization().Produces<ResolvedFormResponse>();
        published.MapGet("/{id:guid}/files/{fileId:guid}", FileAsync).WithName("GetPublishedServiceFile")
            .WithSummary("The image or a document template of a published service, for whoever may see the service. Only once scanned clean.")
            .AllowAnonymousAccess().Produces<byte[]>(contentType: "application/octet-stream");
        return group;
    }

    internal static async Task<IResult> ListAsync(
        [AsParameters] BrowseQuery query, ZelvoDbContext db, CurrentUser user, CancellationToken cancellationToken)
    {
        var scope = await CatalogScope.OfAsync(db, user, cancellationToken);
        var visible = await PublishedServices.VisibleAsync(db, scope, cancellationToken);
        var catalogs = await db.ServiceCatalogs.AsNoTracking().Where(c => c.Active).OrderBy(c => c.Position).ThenBy(c => c.Key).ToListAsync(cancellationToken);
        var ids = visible.Select(v => v.Service.Id).ToHashSet();
        IEnumerable<PublishedService> items = visible;
        if (query.CatalogId is { } catalogId)
        {
            items = items.Where(v => v.Service.CatalogIds.Contains(catalogId));
        }

        if (query.CategoryId is { } categoryId)
        {
            var tree = new TreeIndex<Category>(await db.Categories.AsNoTracking().ToListAsync(cancellationToken));
            items = items.Where(v => v.Service.CategoryIds.Any(c => tree.Find(c) is { } node && tree.Chain(node).Any(n => n.Id == categoryId)));
        }

        if (query.ParentId is { } parentId)
        {
            items = items.Where(v => v.Service.ParentId == parentId);
        }
        else if (query.TopLevel == true)
        {
            // A sub-service whose main service the caller cannot see is shown at the top.
            items = items.Where(v => v.Service.ParentId is not { } parent || !ids.Contains(parent));
        }

        if (query.Audience is { } audience)
        {
            items = items.Where(v => v.Card.Audiences.Contains(audience));
        }

        if (query.Q?.Trim() is { Length: > 0 } q)
        {
            items = items.Where(v => Matches(v, q));
        }

        var children = visible.Where(v => v.Service.ParentId is not null).GroupBy(v => v.Service.ParentId!.Value).ToDictionary(g => g.Key, g => g.Count());
        var counts = catalogs.ToDictionary(c => c.Id, c => visible.Count(v => v.Service.CatalogIds.Contains(c.Id)));
        return TypedResults.Ok(new CatalogBrowseResponse(
            [.. catalogs.Where(c => counts[c.Id] > 0).Select(c => new CatalogBrowseItem(c.Id, c.Key, LocalizedTextDto.From(c.Name), LocalizedTextDto.From(c.Description), counts[c.Id]))],
            [.. items.OrderBy(v => v.Card.Name.En ?? v.Card.Name.Ar, StringComparer.OrdinalIgnoreCase)
                .Select(v => PublishedServiceSummary.From(v, ids, children.GetValueOrDefault(v.Service.Id)))]));
    }

    /// <summary>A plain text filter for the catalog page; the search API (A4) handles Arabic word forms.</summary>
    private static bool Matches(PublishedService v, string q)
    {
        bool Has(string? text) => text is not null && text.Contains(q, StringComparison.OrdinalIgnoreCase);
        return Has(v.Service.Code) || Has(v.Card.Name.En) || Has(v.Card.Name.Ar) || Has(v.Card.Summary.En) || Has(v.Card.Summary.Ar)
            || v.Card.Keywords.Any(Has);
    }

    internal static async Task<IResult> GetAsync(Guid id, ZelvoDbContext db, CurrentUser user, CancellationToken cancellationToken)
    {
        var scope = await CatalogScope.OfAsync(db, user, cancellationToken);
        var found = await PublishedServices.FindAsync(db, scope, id, cancellationToken);
        if (found is null)
        {
            return NotFound();
        }

        var visible = await PublishedServices.VisibleAsync(db, scope, cancellationToken);
        var ids = visible.Select(v => v.Service.Id).ToHashSet();
        var card = found.Card;
        var parent = card.ParentId is { } p ? visible.FirstOrDefault(v => v.Service.Id == p) : null;
        var childList = visible.Where(v => v.Service.ParentId == id).ToList();
        var related = visible.Where(v => card.RelatedServiceIds.Contains(v.Service.Id)).ToList();
        var prerequisites = visible.Where(v => card.PrerequisiteServiceIds.Contains(v.Service.Id)).ToList();
        var done = scope.UserId is { } me ? await CompletedAsync(db, me, card.PrerequisiteServiceIds, cancellationToken) : new HashSet<Guid>();

        var departmentIds = card.Partners.Where(x => x.DepartmentId is not null).Select(x => x.DepartmentId!.Value).Append(card.DepartmentId ?? Guid.Empty).ToList();
        var departments = await db.Departments.AsNoTracking().Where(d => departmentIds.Contains(d.Id)).ToDictionaryAsync(d => d.Id, d => d.Name, cancellationToken);
        var tree = new TreeIndex<Category>(await db.Categories.AsNoTracking().ToListAsync(cancellationToken));
        var owner = card.OwnerId is { } o ? await db.Users.AsNoTracking().Where(u => u.Id == o).Select(u => u.DisplayName).SingleOrDefaultAsync(cancellationToken) : null;
        var catalogs = await db.ServiceCatalogs.AsNoTracking().Where(c => c.Active && found.Service.CatalogIds.Contains(c.Id)).ToListAsync(cancellationToken);

        var open = found.Service.State == ServiceState.Active && card.Type == ServiceType.Internal;
        return TypedResults.Ok(new PublishedServiceResponse(
            found.Service.Id, found.Service.Code, found.Service.State, LocalizedTextDto.From(found.Service.SuspendedMessage), card.Type, card.ExternalUrl,
            LocalizedTextDto.From(card.Name), LocalizedTextDto.From(card.Summary), LocalizedTextDto.From(card.Description),
            card.Icon, card.ImageFileId,
            card.DepartmentId is { } dep && departments.TryGetValue(dep, out var depName) ? LocalizedTextDto.From(depName) : null,
            owner,
            [.. card.CategoryIds.Select(c => tree.Find(c)).Where(c => c is not null).Select(c => new CatalogRef(c!.Id, LocalizedTextDto.From(tree.PathOf(c))))],
            [.. catalogs.Select(c => new CatalogRef(c.Id, LocalizedTextDto.From(c.Name)))],
            [.. card.Documents.Select(d => new PublishedDocument(d.Key, LocalizedTextDto.From(d.Name), LocalizedTextDto.From(d.Description), d.Required, d.FileTypes, d.MaxSizeMb, d.TemplateFileId))],
            [.. card.Steps.Select(s => new PublishedStep(LocalizedTextDto.From(s.Name), LocalizedTextDto.From(s.Description), s.Stage))],
            new PublishedFee(card.Fee.Paid, card.Fee.AmountAed, LocalizedTextDto.From(card.Fee.Notes)),
            card.Days, card.Channels, card.Audiences,
            [.. card.Partners.Select(x => new PublishedPartner(x.Kind,
                LocalizedTextDto.From(x.DepartmentId is { } pd && departments.TryGetValue(pd, out var pn) ? pn : x.Name)))],
            [.. card.Faq.Select(f => new PublishedFaq(LocalizedTextDto.From(f.Question), LocalizedTextDto.From(f.Answer)))],
            parent is null ? null : PublishedServiceSummary.From(parent, ids, 0),
            [.. childList.Select(c => PublishedServiceSummary.From(c, ids, 0))],
            [.. related.Select(r => PublishedServiceSummary.From(r, ids, 0))],
            [.. prerequisites.Select(r => new PublishedPrerequisite(PublishedServiceSummary.From(r, ids, 0), done.Contains(r.Service.Id)))],
            card.FormId is not null,
            open && scope.UserId is not null,
            open && CatalogAccess.MayRequestOnBehalf(card, scope),
            open && scope.UserId is null));
    }

    internal static async Task<IResult> RequestFormAsync(
        Guid id, Guid? requesterId, string? language, ZelvoDbContext db, CurrentUser user, IAuditLog audit, CancellationToken cancellationToken)
    {
        var scope = await CatalogScope.OfAsync(db, user, cancellationToken);
        var found = await PublishedServices.FindAsync(db, scope, id, cancellationToken);
        if (found is null)
        {
            return NotFound();
        }

        UserAccount? requester = user.Account;
        if (requesterId is { } other && other != user.Account?.Id)
        {
            if (!CatalogAccess.MayRequestOnBehalf(found.Card, scope))
            {
                return ApiProblem.Create(StatusCodes.Status403Forbidden, "forbidden", "Your roles may not request this service on behalf of someone else.");
            }

            requester = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == other && u.Status == UserStatus.Active, cancellationToken);
            if (requester is null)
            {
                return UserEndpoints.Validation("requesterId", "The requester is not an active user of this organisation.");
            }
        }

        var form = found.Card.FormId is { } formId ? await FormResolver.PublishedAsync(db, formId, cancellationToken) : null;
        var defaults = form is null || requester is null ? [] : await FormSubmission.DefaultsForAsync(db, user, audit, form.PlainFields, requester, language, cancellationToken);
        return TypedResults.Ok(await FormEndpoints.ResolvedResponseAsync(db, form, defaults, language, cancellationToken));
    }

    internal static async Task<IResult> FileAsync(
        Guid id, Guid fileId, HttpContext http, ZelvoDbContext db, CurrentUser user, IAttachmentStore store, CancellationToken cancellationToken)
    {
        var scope = await CatalogScope.OfAsync(db, user, cancellationToken);
        var found = await PublishedServices.FindAsync(db, scope, id, cancellationToken);
        if (found is null || (found.Card.ImageFileId != fileId && !found.Card.Documents.Any(d => d.TemplateFileId == fileId)))
        {
            return FormsCommon.NotFound("file");
        }

        return await ServiceFiles.SendAsync(http, db, store, id, fileId, cancellationToken);
    }

    /// <summary>Services the person has a resolved or closed request of.</summary>
    internal static async Task<HashSet<Guid>> CompletedAsync(ZelvoDbContext db, Guid requesterId, IReadOnlyCollection<Guid> services, CancellationToken cancellationToken)
    {
        if (services.Count == 0)
        {
            return [];
        }

        var ids = services.ToList();
        return (await db.ServiceRequests.AsNoTracking()
            .Where(r => ids.Contains(r.ServiceId))
            .Join(db.Tickets.AsNoTracking(), r => r.TicketId, t => t.Id, (r, t) => new { r.ServiceId, t.RequesterId, t.State })
            .Where(x => x.RequesterId == requesterId && (x.State == TicketState.Resolved || x.State == TicketState.Closed))
            .Select(x => x.ServiceId).Distinct().ToListAsync(cancellationToken)).ToHashSet();
    }

    internal static IResult NotFound() => FormsCommon.NotFound("service");
}

/// <summary>Filters of the catalog.</summary>
internal sealed class BrowseQuery
{
    /// <summary>Only services in this catalog.</summary>
    public Guid? CatalogId { get; init; }

    /// <summary>Only services in this category or below it.</summary>
    public Guid? CategoryId { get; init; }

    /// <summary>Only the sub-services of this main service.</summary>
    public Guid? ParentId { get; init; }

    /// <summary>Only main services (and sub-services whose main service the caller cannot see).</summary>
    public bool? TopLevel { get; init; }

    /// <summary>Only services for this audience.</summary>
    public TargetAudience? Audience { get; init; }

    /// <summary>Text in the code, name, summary or keywords.</summary>
    [StringLength(100)]
    public string? Q { get; init; }
}

/// <summary>An id with a bilingual name.</summary>
internal sealed record CatalogRef(Guid Id, LocalizedTextDto Name);

/// <summary>A catalog in the browse view.</summary>
internal sealed record CatalogBrowseItem(Guid Id, string Key, LocalizedTextDto Name, LocalizedTextDto Description, int ServiceCount);

/// <summary>A service in lists for requesters.</summary>
internal sealed record PublishedServiceSummary(
    Guid Id, string Code, ServiceState State, ServiceType Type, LocalizedTextDto Name, LocalizedTextDto Summary, string? Icon, Guid? ImageFileId,
    Guid? ParentId, IReadOnlyList<Guid> CatalogIds, IReadOnlyList<Guid> CategoryIds, bool Paid, decimal? AmountAed, int? Days, int SubServiceCount)
{
    public static PublishedServiceSummary From(PublishedService v, IReadOnlySet<Guid> visible, int children) =>
        new(v.Service.Id, v.Service.Code, v.Service.State, v.Card.Type, LocalizedTextDto.From(v.Card.Name), LocalizedTextDto.From(v.Card.Summary),
            v.Card.Icon, v.Card.ImageFileId, v.Service.ParentId is { } p && visible.Contains(p) ? p : null, v.Service.CatalogIds, v.Service.CategoryIds,
            v.Card.Fee.Paid, v.Card.Fee.AmountAed, v.Card.Days, children);
}

/// <summary>The catalog for requesters.</summary>
internal sealed record CatalogBrowseResponse(IReadOnlyList<CatalogBrowseItem> Catalogs, IReadOnlyList<PublishedServiceSummary> Services);

/// <summary>A document to upload, for requesters.</summary>
internal sealed record PublishedDocument(string Key, LocalizedTextDto Name, LocalizedTextDto Description, bool Required, IReadOnlyList<string> FileTypes, int? MaxSizeMb, Guid? TemplateFileId);

/// <summary>A step of the journey.</summary>
internal sealed record PublishedStep(LocalizedTextDto Name, LocalizedTextDto Description, ServiceStepStage Stage);

/// <summary>The fee.</summary>
internal sealed record PublishedFee(bool Paid, decimal? AmountAed, LocalizedTextDto Notes);

/// <summary>A partner with its name.</summary>
internal sealed record PublishedPartner(PartnerKind Kind, LocalizedTextDto Name);

/// <summary>A question and answer.</summary>
internal sealed record PublishedFaq(LocalizedTextDto Question, LocalizedTextDto Answer);

/// <summary>A prerequisite and whether the caller completed it.</summary>
internal sealed record PublishedPrerequisite(PublishedServiceSummary Service, bool Completed);

/// <summary>A service's card for requesters.</summary>
internal sealed record PublishedServiceResponse(
    Guid Id, string Code, ServiceState State, LocalizedTextDto SuspendedMessage, ServiceType Type, string? ExternalUrl,
    LocalizedTextDto Name, LocalizedTextDto Summary, LocalizedTextDto Description, string? Icon, Guid? ImageFileId,
    LocalizedTextDto? Department, string? Owner, IReadOnlyList<CatalogRef> Categories, IReadOnlyList<CatalogRef> Catalogs,
    IReadOnlyList<PublishedDocument> Documents, IReadOnlyList<PublishedStep> Steps, PublishedFee Fee, int? Days,
    IReadOnlyList<DeliveryChannel> Channels, IReadOnlyList<TargetAudience> Audiences, IReadOnlyList<PublishedPartner> Partners,
    IReadOnlyList<PublishedFaq> Faq, PublishedServiceSummary? Parent, IReadOnlyList<PublishedServiceSummary> SubServices,
    IReadOnlyList<PublishedServiceSummary> Related, IReadOnlyList<PublishedPrerequisite> Prerequisites,
    bool HasForm, bool MayRequest, bool MayRequestOnBehalf, bool SignInToRequest);
