using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Zelvo.Api.Identity;
using Zelvo.Data;
using Zelvo.Data.Catalog;
using Zelvo.Data.Forms;
using Zelvo.Data.Identity;
using Zelvo.Data.Localization;
using Zelvo.Data.Tickets;
using Zelvo.Search;
using Zelvo.Tickets;
using Zelvo.Tickets.Forms;

namespace Zelvo.Api.Catalog;

/// <summary>Who a caller is for access checks: roles, groups, department and location with the locations above it.</summary>
/// <param name="UserId">The person; null for integrations and visitors.</param>
/// <param name="SignedIn">A person or an integration.</param>
/// <param name="Roles">Active role keys.</param>
/// <param name="Groups">Support groups the person is a member of.</param>
/// <param name="DepartmentId">The person's department.</param>
/// <param name="Locations">The person's location and every location above it.</param>
internal sealed record CatalogScope(
    Guid? UserId, bool SignedIn, IReadOnlySet<string> Roles, IReadOnlySet<Guid> Groups, Guid? DepartmentId, IReadOnlySet<Guid> Locations)
{
    /// <summary>A visitor who is not signed in.</summary>
    public static CatalogScope Visitor { get; } = new(null, false, new HashSet<string>(), new HashSet<Guid>(), null, new HashSet<Guid>());

    /// <summary>The scope of the current caller.</summary>
    public static async Task<CatalogScope> OfAsync(ZelvoDbContext db, CurrentUser user, CancellationToken cancellationToken)
    {
        var roles = user.ActiveRoles.Select(r => r.Key).ToHashSet(StringComparer.Ordinal);
        if (user.Account is not { } account)
        {
            return user.IsSignedIn ? Visitor with { SignedIn = true, Roles = roles } : Visitor;
        }

        return await OfAsync(db, account.Id, account.DepartmentId, account.LocationId, roles, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The scope of a person who is not the caller (a request on their behalf).</summary>
    public static async Task<CatalogScope> OfUserAsync(ZelvoDbContext db, UserAccount person, CancellationToken cancellationToken)
    {
        var roles = await db.UserRoles.AsNoTracking().Where(r => r.UserId == person.Id).Select(r => r.Role!.Key)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return await OfAsync(db, person.Id, person.DepartmentId, person.LocationId, roles.ToHashSet(StringComparer.Ordinal), cancellationToken).ConfigureAwait(false);
    }

    private static async Task<CatalogScope> OfAsync(
        ZelvoDbContext db, Guid userId, Guid? departmentId, Guid? locationId, HashSet<string> roles, CancellationToken cancellationToken)
    {
        var groups = await db.SupportGroupMembers.AsNoTracking().Where(m => m.UserId == userId).Select(m => m.GroupId)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var locations = await TicketRouter.AncestryAsync(db.Locations.AsNoTracking(), locationId, cancellationToken).ConfigureAwait(false);
        return new CatalogScope(userId, true, roles, groups.ToHashSet(), departmentId, locations.ToHashSet());
    }

    /// <summary>The search readers this scope is (integrations, like visitors, find public documents only, as before C2).</summary>
    public IReadOnlyList<string> Readers => SearchReaders.ForCaller(UserId, Roles, Groups, DepartmentId, Locations);
}

/// <summary>
/// Who may see, request, edit and approve services (module 2: access control per service). Hidden
/// services are left out of the catalog, search, the API and, from E5, the virtual agent: every
/// reader goes through <see cref="MaySee(ServiceAccess, CatalogScope)"/> or the search readers of
/// <see cref="CatalogSearch"/>, which follow the same rules.
/// </summary>
internal static class CatalogAccess
{
    /// <summary>Whether <paramref name="scope"/> is allowed by <paramref name="access"/>.</summary>
    public static bool MaySee(ServiceAccess access, CatalogScope scope) => access.Mode switch
    {
        ServiceAccessMode.Public => true,
        ServiceAccessMode.Everyone => scope.SignedIn,
        _ => scope.SignedIn && (
            access.RoleKeys.Any(scope.Roles.Contains)
            || (scope.UserId is { } user && access.UserIds.Contains(user))
            || access.GroupIds.Any(scope.Groups.Contains)
            || (scope.DepartmentId is { } department && access.DepartmentIds.Contains(department))
            || access.LocationIds.Any(scope.Locations.Contains)),
    };

    /// <summary>Published services requesters may find: Active or Suspended (suspended ones show their message).</summary>
    public static bool IsListed(ServiceState state) => state is ServiceState.Active or ServiceState.Suspended;

    /// <summary>Holds catalog.manage: everything in the catalog.</summary>
    public static bool IsManager(CurrentUser user) => user.Has(Permissions.CatalogManage);

    /// <summary>Catalogs the caller edits (directly or through a group); empty for managers, who edit all.</summary>
    public static async Task<IReadOnlySet<Guid>> EditedCatalogsAsync(ZelvoDbContext db, CurrentUser user, CancellationToken cancellationToken)
    {
        if (user.Account is not { } account)
        {
            return new HashSet<Guid>();
        }

        var groups = db.SupportGroupMembers.Where(m => m.UserId == account.Id).Select(m => m.GroupId);
        var catalogs = await db.CatalogEditors.AsNoTracking()
            .Where(e => e.UserId == account.Id || (e.GroupId != null && groups.Contains(e.GroupId.Value)))
            .Select(e => e.CatalogId).Distinct().ToListAsync(cancellationToken).ConfigureAwait(false);
        return catalogs.ToHashSet();
    }

    /// <summary>Whether the caller may see the service in Settings → Catalog: managers, publishers and editors of one of its catalogs.</summary>
    public static bool MayAdminister(CurrentUser user, IReadOnlySet<Guid> edited, CatalogService service) =>
        IsManager(user) || user.Has(Permissions.CatalogPublish) || MayEdit(user, edited, service);

    /// <summary>Whether the caller may write and submit the service: managers, and editors of one of its catalogs.</summary>
    public static bool MayEdit(CurrentUser user, IReadOnlySet<Guid> edited, CatalogService service) =>
        IsManager(user) || service.EditorCatalogIds.Any(edited.Contains);

    /// <summary>Four-eyes rule: a publisher who neither started nor last changed the version.</summary>
    public static bool MayReview(CurrentUser user, CatalogServiceVersion version) =>
        user.Has(Permissions.CatalogPublish) && user.Account is { } account
        && version.CreatedById != account.Id && version.LastEditedById != account.Id;

    /// <summary>Whether the caller may request the service on behalf of someone else.</summary>
    public static bool MayRequestOnBehalf(ServiceCard card, CatalogScope scope) =>
        scope.UserId is not null && card.OnBehalfRoleKeys.Any(scope.Roles.Contains);
}

/// <summary>A listed service with its published card, for readers.</summary>
internal sealed record PublishedService(CatalogService Service, CatalogServiceVersion Version, ServiceCard Card);

/// <summary>Reads published services for readers, already filtered by access.</summary>
internal static class PublishedServices
{
    /// <summary>
    /// Every listed service <paramref name="scope"/> may see. A service in catalogs is listed only
    /// while one of them is active. Catalogs are small (hundreds of services), so access is checked
    /// in memory with the same rules as the search readers.
    /// </summary>
    public static async Task<List<PublishedService>> VisibleAsync(ZelvoDbContext db, CatalogScope scope, CancellationToken cancellationToken)
    {
        var rows = await db.CatalogServices.AsNoTracking()
            .Where(s => s.State == ServiceState.Active || s.State == ServiceState.Suspended)
            .Join(db.CatalogServiceVersions.AsNoTracking(), s => s.PublishedVersionId, v => v.Id, (s, v) => new { Service = s, Version = v })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var active = (await db.ServiceCatalogs.AsNoTracking().Where(c => c.Active).Select(c => c.Id).ToListAsync(cancellationToken).ConfigureAwait(false)).ToHashSet();
        var visible = new List<PublishedService>();
        foreach (var row in rows)
        {
            var card = ServiceCards.Read(row.Version.Content);
            if ((row.Service.CatalogIds.Count == 0 || row.Service.CatalogIds.Any(active.Contains)) && CatalogAccess.MaySee(card.Access, scope))
            {
                visible.Add(new PublishedService(row.Service, row.Version, card));
            }
        }

        return visible;
    }

    /// <summary>One listed service, when <paramref name="scope"/> may see it; null otherwise (hidden services answer 404).</summary>
    public static async Task<PublishedService?> FindAsync(ZelvoDbContext db, CatalogScope scope, Guid id, CancellationToken cancellationToken)
    {
        var service = await db.CatalogServices.AsNoTracking().SingleOrDefaultAsync(s => s.Id == id, cancellationToken).ConfigureAwait(false);
        if (service is not { PublishedVersionId: { } versionId } || !CatalogAccess.IsListed(service.State))
        {
            return null;
        }

        if (service.CatalogIds.Count > 0
            && !await db.ServiceCatalogs.AnyAsync(c => c.Active && service.CatalogIds.Contains(c.Id), cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var version = await db.CatalogServiceVersions.AsNoTracking().SingleAsync(v => v.Id == versionId, cancellationToken).ConfigureAwait(false);
        var card = ServiceCards.Read(version.Content);
        return CatalogAccess.MaySee(card.Access, scope) ? new PublishedService(service, version, card) : null;
    }
}

/// <summary>Checks of a card against the tenant's data: everything it refers to must exist and be usable.</summary>
internal static class ServiceCardCheck
{
    /// <summary>
    /// Problems that stop the card from being submitted or approved. <paramref name="forPublishing"/>
    /// adds what must hold for requesters: the form must be published.
    /// </summary>
    public static async Task<List<FormProblem>> CheckAsync(
        ZelvoDbContext db, CatalogService service, ServiceCard card, AttachmentPolicy policy, bool forPublishing, CancellationToken cancellationToken)
    {
        var problems = ServiceCards.CheckShape(card, policy.AllowedExtensions);
        void Add(string field, string code, string message) => problems.Add(new FormProblem(field, code, message));

        if (card.ParentId is { } parentId)
        {
            if (parentId == service.Id)
            {
                Add("parentId", "parent-self", "A service cannot be its own main service.");
            }
            else
            {
                var parents = await db.CatalogServices.AsNoTracking().Select(s => new { s.Id, s.ParentId, s.State })
                    .ToDictionaryAsync(s => s.Id, cancellationToken).ConfigureAwait(false);
                if (!parents.TryGetValue(parentId, out var parent) || parent.State == ServiceState.Retired)
                {
                    Add("parentId", "parent-unknown", "Choose a main service that is not retired.");
                }
                else
                {
                    // Any depth, but never a circle.
                    var seen = new HashSet<Guid> { service.Id };
                    Guid? current = parentId;
                    while (current is { } id && parents.TryGetValue(id, out var node))
                    {
                        if (!seen.Add(id))
                        {
                            Add("parentId", "parent-cycle", "The main service is below this service.");
                            break;
                        }

                        current = node.ParentId;
                    }
                }
            }
        }

        await KnownAsync(db.ServiceCatalogs.Select(c => c.Id), card.CatalogIds, "catalogIds", "catalog-unknown", "A catalog does not exist.").ConfigureAwait(false);
        await KnownAsync(db.Categories.Where(c => c.Active).Select(c => c.Id), card.CategoryIds, "categoryIds", "category-unknown", "Choose active categories.").ConfigureAwait(false);
        var departments = new List<Guid>();
        if (card.DepartmentId is { } d) departments.Add(d);
        await KnownAsync(db.Departments.Select(x => x.Id), departments, "departmentId", "department-unknown", "Choose a department.").ConfigureAwait(false);
        await KnownAsync(db.Departments.Select(x => x.Id), [.. card.Partners.Where(p => p.DepartmentId is not null).Select(p => p.DepartmentId!.Value)],
            "partners", "department-unknown", "A partner department does not exist.").ConfigureAwait(false);
        if (card.OwnerId is { } owner && !await db.Users.AnyAsync(u => u.Id == owner && u.Status == UserStatus.Active, cancellationToken).ConfigureAwait(false))
        {
            Add("ownerId", "owner-unknown", "The service owner is an active user.");
        }

        if (card.FormId is { } formId)
        {
            var form = await db.Forms.AsNoTracking().SingleOrDefaultAsync(f => f.Id == formId, cancellationToken).ConfigureAwait(false);
            if (form is null || form.State == FormState.Retired)
            {
                Add("formId", "form-unknown", "Choose a form that is not retired.");
            }
            else if (forPublishing && form.State != FormState.Published)
            {
                Add("formId", "form-not-published", "Publish the service's form first.");
            }
        }

        var links = card.RelatedServiceIds.Concat(card.PrerequisiteServiceIds).ToList();
        if (links.Contains(service.Id))
        {
            Add("relatedServiceIds", "link-self", "A service cannot be related to itself or be its own prerequisite.");
        }

        await KnownAsync(db.CatalogServices.Where(s => s.State != ServiceState.Retired).Select(s => s.Id), [.. links.Where(l => l != service.Id)],
            "relatedServiceIds", "service-unknown", "A related or prerequisite service does not exist or is retired.").ConfigureAwait(false);

        var access = card.Access;
        await KnownAsync(db.SupportGroups.Select(g => g.Id), access.GroupIds, "access.groupIds", "group-unknown", "A group does not exist.").ConfigureAwait(false);
        await KnownAsync(db.Departments.Select(x => x.Id), access.DepartmentIds, "access.departmentIds", "department-unknown", "A department does not exist.").ConfigureAwait(false);
        await KnownAsync(db.Locations.Select(x => x.Id), access.LocationIds, "access.locationIds", "location-unknown", "A location does not exist.").ConfigureAwait(false);
        await KnownAsync(db.Users.Select(x => x.Id), access.UserIds, "access.userIds", "user-unknown", "A user does not exist.").ConfigureAwait(false);
        var roles = (await db.Roles.AsNoTracking().Select(r => r.Key).ToListAsync(cancellationToken).ConfigureAwait(false)).ToHashSet(StringComparer.Ordinal);
        if (access.RoleKeys.Any(r => !roles.Contains(r)))
        {
            Add("access.roleKeys", "role-unknown", "A role does not exist.");
        }

        if (card.OnBehalfRoleKeys.Any(r => !roles.Contains(r)))
        {
            Add("onBehalfRoleKeys", "role-unknown", "A role does not exist.");
        }

        await Workflows.WorkflowReferences.CheckLevelsAsync(db, card.Approvals.BeforeAssignment, "approvals.beforeAssignment", problems, cancellationToken).ConfigureAwait(false);
        await Workflows.WorkflowReferences.CheckLevelsAsync(db, card.Approvals.AfterFulfilment, "approvals.afterFulfilment", problems, cancellationToken).ConfigureAwait(false);

        var files = card.Documents.Where(x => x.TemplateFileId is not null).Select(x => x.TemplateFileId!.Value).ToList();
        if (card.ImageFileId is { } image)
        {
            files.Add(image);
        }

        if (files.Count > 0)
        {
            var own = await db.TicketAttachments.AsNoTracking().Where(a => files.Contains(a.Id) && a.ServiceId == service.Id)
                .Select(a => new { a.Id, a.ContentType, a.ScanStatus }).ToDictionaryAsync(a => a.Id, cancellationToken).ConfigureAwait(false);
            for (var i = 0; i < card.Documents.Count; i++)
            {
                if (card.Documents[i].TemplateFileId is { } file && (!own.TryGetValue(file, out var f) || f.ScanStatus == AttachmentScanStatus.Infected))
                {
                    Add($"documents.{i}.templateFileId", "file-unknown", "Upload the template to this service again.");
                }
            }

            if (card.ImageFileId is { } img && (!own.TryGetValue(img, out var i2) || !i2.ContentType.StartsWith("image/", StringComparison.Ordinal) || i2.ScanStatus == AttachmentScanStatus.Infected))
            {
                Add("imageFileId", "image-unknown", "Upload the image to this service again.");
            }
        }

        return problems;

        async Task KnownAsync(IQueryable<Guid> known, IReadOnlyCollection<Guid> wanted, string field, string code, string message)
        {
            if (wanted.Count == 0)
            {
                return;
            }

            var ids = wanted.ToList();
            var found = await known.Where(k => ids.Contains(k)).CountAsync(cancellationToken).ConfigureAwait(false);
            if (found != ids.Distinct().Count())
            {
                Add(field, code, message);
            }
        }
    }
}

/// <summary>Services in the search index (A4), with readers that follow the service's access.</summary>
internal static class CatalogSearch
{
    /// <summary>Document type.</summary>
    public const string Type = "service";

    /// <summary>Readers of a published card: visitors, everyone signed in, or the groups, roles, departments, locations and users named.</summary>
    public static IReadOnlyList<string> ReadersOf(ServiceAccess access) => access.Mode switch
    {
        ServiceAccessMode.Public => [SearchReaders.Public],
        ServiceAccessMode.Everyone => [SearchReaders.SignedIn],
        _ =>
        [
            .. access.GroupIds.Select(SearchReaders.Group),
            .. access.RoleKeys.Select(SearchReaders.Role),
            .. access.DepartmentIds.Select(SearchReaders.Department),
            .. access.LocationIds.Select(SearchReaders.Location),
            .. access.UserIds.Select(SearchReaders.User),
        ],
    };

    /// <summary>The document of a listed service (its published card only); null when it must not be found.</summary>
    public static SearchDocument? Document(CatalogService service, ServiceCard? published, bool inActiveCatalog)
    {
        if (published is null || !CatalogAccess.IsListed(service.State) || !inActiveCatalog)
        {
            return null;
        }

        var readers = ReadersOf(published.Access);
        if (readers.Count == 0)
        {
            return null;
        }

        var faqEn = string.Join("\n", published.Faq.SelectMany(f => new[] { f.Question.En, f.Answer.En }).Where(t => t is not null));
        var faqAr = string.Join("\n", published.Faq.SelectMany(f => new[] { f.Question.Ar, f.Answer.Ar }).Where(t => t is not null));
        return new SearchDocument
        {
            Type = Type,
            Id = service.Id.ToString(),
            Title = published.Name,
            Body = new LocalizedText(
                Join(published.Summary.En, Knowledge.KnowledgeSearch.PlainText(published.Description.En), faqEn),
                Join(published.Summary.Ar, Knowledge.KnowledgeSearch.PlainText(published.Description.Ar), faqAr)),
            Keywords = [service.Code, .. published.Keywords.Take(SearchDocument.ListMaxCount - 1)],
            Readers = [.. readers.Distinct(StringComparer.Ordinal)],
            Attributes = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["code"] = service.Code,
                ["state"] = service.State.ToString(),
                ["type"] = published.Type.ToString(),
            },
            Url = $"/catalog/{service.Id}",
            UpdatedAt = service.UpdatedAt,
        };
    }

    /// <summary>Stages the index update of <paramref name="service"/> (before <c>SaveChanges</c>).</summary>
    public static async Task IndexAsync(ZelvoDbContext db, ISearchIndexer indexer, CatalogService service, CancellationToken cancellationToken)
    {
        ServiceCard? card = null;
        if (service.PublishedVersionId is { } versionId)
        {
            var version = db.ChangeTracker.Entries<CatalogServiceVersion>().Select(e => e.Entity).FirstOrDefault(v => v.Id == versionId)
                ?? await db.CatalogServiceVersions.AsNoTracking().SingleOrDefaultAsync(v => v.Id == versionId, cancellationToken).ConfigureAwait(false);
            card = version is null ? null : ServiceCards.Read(version.Content);
        }

        var inActive = service.CatalogIds.Count == 0
            || await db.ServiceCatalogs.AnyAsync(c => c.Active && service.CatalogIds.Contains(c.Id), cancellationToken).ConfigureAwait(false);
        if (Document(service, card, inActive) is { } document)
        {
            indexer.Upsert(document);
        }
        else
        {
            indexer.Delete(Type, service.Id.ToString());
        }
    }

    private static string? Join(params string?[] parts)
    {
        var present = parts.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
        return present.Count == 0 ? null : string.Join("\n", present);
    }
}

/// <summary>Every listed service of the tenant, for rebuilding the search index.</summary>
internal sealed class CatalogSearchSource(ZelvoDbContext db) : ISearchDocumentSource
{
    /// <inheritdoc />
    public string Type => CatalogSearch.Type;

    /// <inheritdoc />
    public async IAsyncEnumerable<SearchDocument> ReadAllAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var active = (await db.ServiceCatalogs.AsNoTracking().Where(c => c.Active).Select(c => c.Id).ToListAsync(cancellationToken).ConfigureAwait(false)).ToHashSet();
        var rows = await db.CatalogServices.AsNoTracking()
            .Where(s => s.State == ServiceState.Active || s.State == ServiceState.Suspended)
            .Join(db.CatalogServiceVersions.AsNoTracking(), s => s.PublishedVersionId, v => v.Id, (s, v) => new { Service = s, v.Content })
            .OrderBy(x => x.Service.Id)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var row in rows)
        {
            var inActive = row.Service.CatalogIds.Count == 0 || row.Service.CatalogIds.Any(active.Contains);
            if (CatalogSearch.Document(row.Service, ServiceCards.Read(row.Content), inActive) is { } document)
            {
                yield return document;
            }
        }
    }
}

/// <summary>Catalogs and published services that miss a language (LANG-5), for Settings → Missing translations.</summary>
internal sealed class CatalogMissingTranslations : IMissingTranslationSource
{
    public async Task<IReadOnlyList<MissingTranslation>> FindAsync(ZelvoDbContext db, CancellationToken cancellationToken)
    {
        var missing = new List<MissingTranslation>();
        var catalogs = await db.ServiceCatalogs.AsNoTracking().Where(c => c.Active).OrderBy(c => c.Key).ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var catalog in catalogs)
        {
            Check("service_catalogs", catalog.Key, "name", catalog.Name);
            Check("service_catalogs", catalog.Key, "description", catalog.Description);
        }

        var services = await db.CatalogServices.AsNoTracking()
            .Where(s => s.State == ServiceState.Active || s.State == ServiceState.Suspended)
            .Join(db.CatalogServiceVersions.AsNoTracking(), s => s.PublishedVersionId, v => v.Id, (s, v) => new { s.Code, v.Content })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var row in services.OrderBy(r => r.Code, StringComparer.Ordinal))
        {
            var card = ServiceCards.Read(row.Content);
            Check("catalog_services", row.Code, "name", card.Name);
            Check("catalog_services", row.Code, "summary", card.Summary);
            Check("catalog_services", row.Code, "description", card.Description);
            for (var i = 0; i < card.Documents.Count; i++)
            {
                Check("catalog_services", row.Code, $"documents.{i}.name", card.Documents[i].Name);
            }

            for (var i = 0; i < card.Steps.Count; i++)
            {
                Check("catalog_services", row.Code, $"steps.{i}.name", card.Steps[i].Name);
            }

            for (var i = 0; i < card.Faq.Count; i++)
            {
                Check("catalog_services", row.Code, $"faq.{i}.question", card.Faq[i].Question);
                Check("catalog_services", row.Code, $"faq.{i}.answer", card.Faq[i].Answer);
            }

            Check("catalog_services", row.Code, "fee.notes", card.Fee.Notes);
        }

        return missing;

        void Check(string entity, string id, string field, LocalizedText text)
        {
            if (text.HasAnyValue && !text.IsComplete)
            {
                missing.Add(new MissingTranslation(entity, id, field, text.MissingLanguages));
            }
        }
    }
}
