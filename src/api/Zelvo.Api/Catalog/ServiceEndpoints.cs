using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Zelvo.Api.Conventions;
using Zelvo.Api.Forms;
using Zelvo.Api.Identity;
using Zelvo.Api.Incidents;
using Zelvo.Api.Integrations;
using Zelvo.Api.Settings;
using Zelvo.Api.Tenancy;
using Zelvo.Data;
using Zelvo.Data.Auditing;
using Zelvo.Data.Catalog;
using Zelvo.Data.Localization;
using Zelvo.Data.Tickets;
using Zelvo.Search;
using Zelvo.Tickets;
using Zelvo.Tickets.Forms;

namespace Zelvo.Api.Catalog;

/// <summary>
/// Building services (task C2, module 2): draft, review, publish, suspend and retire; copy; save as
/// template; images and document templates. A card's changes are a working version that a
/// publisher other than its author approves (four-eyes, product owner decision 2026-11-24);
/// requesters keep the published card meanwhile. Catalog managers work on every service, catalog
/// editors on the services of their catalogs. Every change is audited.
/// </summary>
internal static class ServiceEndpoints
{
    public static RouteGroupBuilder MapServiceEndpoints(this RouteGroupBuilder group)
    {
        var services = group.MapGroup("/catalog/services").WithTags("Catalog").RequireTenant();
        services.MapGet("/", ListAsync).WithName("ListCatalogServices")
            .WithSummary("Services the caller works on (every service for catalog.manage and catalog.publish, their catalogs' services for editors), with state, versions and parent. Filter by state, catalog or text.")
            .RequireAuthorization().Produces<IReadOnlyList<ServiceSummaryResponse>>();
        services.MapPost("/", CreateAsync).WithName("CreateCatalogService")
            .WithSummary("Create a draft service with a unique code, empty or from a template (templateId: builtin:<key> or a saved template's id). Editors put it in catalogs they edit. Needs catalog.manage or editing a catalog; audited.")
            .RequirePerson().RequireAuthorization().Produces<ServiceResponse>(StatusCodes.Status201Created).ProducesValidationProblem();
        services.MapGet("/review-queue", ReviewQueueAsync).WithName("ListServicesInReview")
            .WithSummary("Service versions waiting for review that the caller may approve (catalog.publish, not written by the caller).")
            .RequirePermission(Permissions.CatalogPublish).Produces<IReadOnlyList<ServiceSummaryResponse>>();
        services.MapGet("/{id:guid}", GetAsync).WithName("GetCatalogService")
            .WithSummary("A service with its published card, the card being written or reviewed, its problems, its files and what the caller may do. Returns an ETag to send as If-Match.")
            .RequireAuthorization().Produces<ServiceResponse>();
        services.MapPut("/{id:guid}/draft", SaveDraftAsync).WithName("SaveCatalogServiceDraft")
            .WithSummary("Save the working card. On a published service the first save starts a new version from the published one; requesters keep the published card until the new one is approved. Not while in review. Drafts may have problems; they stop submitting. If-Match checked when sent; audited.")
            .RequirePerson().RequireAuthorization().Produces<ServiceResponse>().ProducesValidationProblem();
        services.MapPatch("/{id:guid}", UpdateCodeAsync).WithName("ChangeCatalogServiceCode")
            .WithSummary("Change the code of a service that was never published (codes of published services stay, as requests refer to them). Audited.")
            .RequirePerson().RequireAuthorization().Produces<ServiceResponse>().ProducesValidationProblem();
        services.MapPost("/{id:guid}/submit", SubmitAsync).WithName("SubmitCatalogService")
            .WithSummary("Send the working card for review. Refused with its problems while there are any; audited.")
            .RequirePerson().RequireAuthorization().Produces<ServiceResponse>();
        services.MapPost("/{id:guid}/approve", ApproveAsync).WithName("ApproveCatalogService")
            .WithSummary("Publish the card in review: requesters see it at once and the service opens for requests (a suspended service stays suspended). The approver may not have written the version (four-eyes). Needs catalog.publish; audited.")
            .RequirePerson().RequirePermission(Permissions.CatalogPublish).Produces<ServiceResponse>();
        services.MapPost("/{id:guid}/send-back", SendBackAsync).WithName("SendBackCatalogService")
            .WithSummary("Return the card in review to its authors with a comment (required). Needs catalog.publish; audited.")
            .RequirePerson().RequirePermission(Permissions.CatalogPublish).Produces<ServiceResponse>().ProducesValidationProblem();
        services.MapPost("/{id:guid}/discard", DiscardAsync).WithName("DiscardCatalogServiceDraft")
            .WithSummary("Throw away the working card. A service that was never published is deleted with it. Audited.")
            .RequirePerson().RequireAuthorization().Produces<ServiceResponse>();
        services.MapPost("/{id:guid}/suspend", SuspendAsync).WithName("SuspendCatalogService")
            .WithSummary("Close an active service for new requests; requesters see the message (required, English or Arabic). Needs catalog.publish; audited.")
            .RequirePerson().RequirePermission(Permissions.CatalogPublish).Produces<ServiceResponse>().ProducesValidationProblem();
        services.MapPost("/{id:guid}/resume", ResumeAsync).WithName("ResumeCatalogService")
            .WithSummary("Open a suspended service for requests again. Needs catalog.publish; audited.")
            .RequirePerson().RequirePermission(Permissions.CatalogPublish).Produces<ServiceResponse>();
        services.MapPost("/{id:guid}/retire", RetireAsync).WithName("RetireCatalogService")
            .WithSummary("Take a service out of use: it is no longer shown, found or requested, and a pending working card is discarded. Requests already made keep it. Needs catalog.publish; audited.")
            .RequirePerson().RequirePermission(Permissions.CatalogPublish).Produces<ServiceResponse>();
        services.MapPost("/{id:guid}/reopen", ReopenAsync).WithName("ReopenCatalogService")
            .WithSummary("Start a new draft from a retired service's last card; it is published again only through review. Audited.")
            .RequirePerson().RequireAuthorization().Produces<ServiceResponse>();
        services.MapDelete("/{id:guid}", DeleteAsync).WithName("DeleteCatalogService")
            .WithSummary("Delete a service that was never published. Published services are retired instead. Audited.")
            .RequirePerson().RequireAuthorization().Produces(StatusCodes.Status204NoContent);
        services.MapPost("/{id:guid}/copy", CopyAsync).WithName("CopyCatalogService")
            .WithSummary("Make a new draft service from this service's card (the working card when there is one) with a new code; files are not copied. Audited.")
            .RequirePerson().RequireAuthorization().Produces<ServiceResponse>(StatusCodes.Status201Created).ProducesValidationProblem();
        services.MapPost("/{id:guid}/save-as-template", SaveAsTemplateAsync).WithName("SaveCatalogServiceAsTemplate")
            .WithSummary("Save this service's card as a tenant template (without its code, files, related services and named users). Needs catalog.manage; audited.")
            .RequirePerson().RequirePermission(Permissions.CatalogManage).Produces<ServiceTemplateResponse>(StatusCodes.Status201Created).ProducesValidationProblem();
        services.MapGet("/{id:guid}/versions", VersionsAsync).WithName("ListCatalogServiceVersions")
            .WithSummary("Every version of the card, newest first, with who wrote, submitted and reviewed it.")
            .RequireAuthorization().Produces<IReadOnlyList<ServiceVersionResponse>>();
        services.MapGet("/{id:guid}/versions/{number:int}", VersionAsync).WithName("GetCatalogServiceVersion")
            .WithSummary("One version's card.")
            .RequireAuthorization().Produces<ServiceVersionResponse>();
        services.MapPost("/{id:guid}/files", UploadFileAsync).WithName("UploadCatalogServiceFile")
            .WithSummary("Add a file to a service: its image (PNG, JPEG, GIF or WebP) or a required document's template (the tenant's allowed types and size). The content must match the type; scanned for malware before anyone can download it. Audited.")
            .RequirePerson().RequireAuthorization().DisableAntiforgery().Produces<ServiceFileResponse>(StatusCodes.Status201Created);
        services.MapDelete("/{id:guid}/files/{fileId:guid}", DeleteFileAsync).WithName("DeleteCatalogServiceFile")
            .WithSummary("Remove a file that no published card uses. Audited.")
            .RequirePerson().RequireAuthorization().Produces(StatusCodes.Status204NoContent);
        services.MapGet("/{id:guid}/files/{fileId:guid}", FileAsync).WithName("GetCatalogServiceFileForEditors")
            .WithSummary("A file of a service for those who work on it, once scanned clean.")
            .RequireAuthorization().Produces<byte[]>(contentType: "application/octet-stream");
        return group;
    }

    internal static async Task<IResult> ListAsync([AsParameters] ServiceListQuery query, ZelvoDbContext db, CurrentUser user, CancellationToken cancellationToken)
    {
        var edited = await CatalogAccess.EditedCatalogsAsync(db, user, cancellationToken);
        var everything = CatalogAccess.IsManager(user) || user.Has(Permissions.CatalogPublish);
        if (!everything && edited.Count == 0)
        {
            return CatalogEndpoints.Forbidden();
        }

        var services = db.CatalogServices.AsNoTracking();
        if (query.State is { } state)
        {
            services = services.Where(s => s.State == state);
        }

        if (query.CatalogId is { } catalogId)
        {
            services = services.Where(s => s.EditorCatalogIds.Contains(catalogId));
        }

        var rows = await services.OrderBy(s => s.Code).ToListAsync(cancellationToken);
        rows = [.. rows.Where(s => everything || s.EditorCatalogIds.Any(edited.Contains))];
        if (query.Q is { Length: > 0 } q)
        {
            rows = [.. rows.Where(s => s.Code.Contains(q, StringComparison.OrdinalIgnoreCase)
                || (s.Name.En?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false) || (s.Name.Ar?.Contains(q, StringComparison.Ordinal) ?? false))];
        }

        return TypedResults.Ok(await SummariesAsync(db, user, edited, rows, cancellationToken));
    }

    internal static async Task<IResult> ReviewQueueAsync(ZelvoDbContext db, CurrentUser user, CancellationToken cancellationToken)
    {
        var waiting = await db.CatalogServiceVersions.AsNoTracking().Where(v => v.State == ServiceVersionState.UnderReview).ToListAsync(cancellationToken);
        var ids = waiting.Where(v => CatalogAccess.MayReview(user, v)).Select(v => v.ServiceId).ToList();
        var rows = await db.CatalogServices.AsNoTracking().Where(s => ids.Contains(s.Id)).OrderBy(s => s.UpdatedAt).ToListAsync(cancellationToken);
        return TypedResults.Ok(await SummariesAsync(db, user, await CatalogAccess.EditedCatalogsAsync(db, user, cancellationToken), rows, cancellationToken));
    }

    internal static async Task<IResult> CreateAsync(
        CreateServiceRequest request, ZelvoDbContext db, CurrentUser user, CancellationToken cancellationToken)
    {
        var edited = await CatalogAccess.EditedCatalogsAsync(db, user, cancellationToken);
        if (!CatalogAccess.IsManager(user) && edited.Count == 0)
        {
            return CatalogEndpoints.Forbidden();
        }

        var code = request.Code?.Trim().ToUpperInvariant();
        if (!ServiceCards.IsCode(code))
        {
            return UserEndpoints.Validation("code", "Use 2–30 capital letters, digits and hyphens, for example IT-LAPTOP.");
        }

        if (await db.CatalogServices.AnyAsync(s => s.Code == code, cancellationToken))
        {
            return ApiProblem.Create(StatusCodes.Status409Conflict, "code-taken", "Another service already has this code.");
        }

        var card = new ServiceCard();
        if (request.TemplateId is { Length: > 0 } templateId)
        {
            var template = await ServiceTemplates.FindAsync(db, templateId, cancellationToken);
            if (template is null)
            {
                return UserEndpoints.Validation("templateId", "The template does not exist.");
            }

            card = template;
        }

        if (FormsCommon.Clean(request.Name) is { HasAnyValue: true } name)
        {
            card = card with { Name = name };
        }

        if (request.CatalogIds is { } catalogIds)
        {
            card = card with { CatalogIds = [.. catalogIds.Distinct()] };
        }

        if (!CatalogAccess.IsManager(user) && (card.CatalogIds.Count == 0 || card.CatalogIds.Any(c => !edited.Contains(c))))
        {
            return UserEndpoints.Validation("catalogIds", "Put the service in catalogs you edit.");
        }

        if (card.CatalogIds.Count > 0 && await db.ServiceCatalogs.CountAsync(c => card.CatalogIds.Contains(c.Id), cancellationToken) != card.CatalogIds.Count)
        {
            return UserEndpoints.Validation("catalogIds", "A catalog does not exist.");
        }

        var created = await NewDraftAsync(db, user, code!, card, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Created($"/api/v1/catalog/services/{created.Id}", await ResponseAsync(db, user, created, cancellationToken));
    }

    /// <summary>Stages a new draft service with its first version (also used by copy and import).</summary>
    internal static Task<CatalogService> NewDraftAsync(ZelvoDbContext db, CurrentUser user, string code, ServiceCard card, CancellationToken cancellationToken)
    {
        card = ServiceCards.Normalise(card);
        var service = new CatalogService
        {
            Code = code,
            Name = card.Name.HasAnyValue ? card.Name : new LocalizedText(code, null),
            ParentId = card.ParentId,
            CatalogIds = [.. card.CatalogIds],
            CategoryIds = [.. card.CategoryIds],
            EditorCatalogIds = [.. card.CatalogIds],
            CreatedById = user.Account?.Id,
        };
        var version = new CatalogServiceVersion
        {
            ServiceId = service.Id,
            Number = 1,
            Content = ServiceCards.Write(card),
            CreatedById = user.Account?.Id,
            LastEditedById = user.Account?.Id,
        };
        service.WorkingVersionId = version.Id;
        db.CatalogServices.Add(service);
        db.CatalogServiceVersions.Add(version);
        return Task.FromResult(service);
    }

    internal static async Task<IResult> GetAsync(Guid id, HttpContext http, ZelvoDbContext db, CurrentUser user, CancellationToken cancellationToken)
    {
        var (service, problem) = await FindAsync(db, user, id, edit: false, cancellationToken);
        if (service is null)
        {
            return problem!;
        }

        return await OkAsync(http, db, user, service, cancellationToken);
    }

    internal static async Task<IResult> SaveDraftAsync(
        Guid id, SaveServiceDraftRequest request, HttpContext http, ZelvoDbContext db, CurrentUser user, CancellationToken cancellationToken)
    {
        var (service, problem) = await FindAsync(db, user, id, edit: true, cancellationToken);
        if (service is null)
        {
            return problem!;
        }

        if (await VersionProblemAsync(http, db, service, cancellationToken) is { } versionProblem)
        {
            return versionProblem;
        }

        var working = await WorkingAsync(db, service, cancellationToken);
        if (working?.State == ServiceVersionState.UnderReview)
        {
            return FormsCommon.State("The service is in review. Wait for the review, or ask the reviewer to send it back.");
        }

        if (service.State == ServiceState.Retired && working is null)
        {
            return FormsCommon.State("Reopen the retired service as a draft first.");
        }

        var card = ServiceCards.Normalise(request.Card!);
        if (ServiceCards.Write(card).Length > 500_000)
        {
            return UserEndpoints.Validation("card", "The card is too large.");
        }

        // Editors who are not managers change only the catalogs they edit.
        var edited = await CatalogAccess.EditedCatalogsAsync(db, user, cancellationToken);
        var before = working is not null ? ServiceCards.Read(working.Content).CatalogIds : service.CatalogIds;
        if (!CatalogAccess.IsManager(user) && before.Except(card.CatalogIds).Concat(card.CatalogIds.Except(before)).Any(c => !edited.Contains(c)))
        {
            return UserEndpoints.Validation("card.catalogIds", "You may add or remove only catalogs you edit.");
        }

        if (!CatalogAccess.IsManager(user) && !card.CatalogIds.Any(edited.Contains))
        {
            return UserEndpoints.Validation("card.catalogIds", "Keep the service in at least one catalog you edit.");
        }

        var now = DateTimeOffset.UtcNow;
        if (working is null)
        {
            working = new CatalogServiceVersion
            {
                ServiceId = service.Id,
                Number = await NextNumberAsync(db, service.Id, cancellationToken),
                Content = ServiceCards.Write(card),
                CreatedById = user.Account?.Id,
                LastEditedById = user.Account?.Id,
                ChangeNote = Note(request.ChangeNote),
            };
            db.CatalogServiceVersions.Add(working);
            service.WorkingVersionId = working.Id;
        }
        else
        {
            working.Content = ServiceCards.Write(card);
            working.LastEditedById = user.Account?.Id;
            working.ChangeNote = Note(request.ChangeNote) ?? working.ChangeNote;
            working.ReviewComment = null;
            working.UpdatedAt = now;
        }

        CopyWorking(service, card);
        service.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken);
        return await OkAsync(http, db, user, service, cancellationToken);
    }

    internal static async Task<IResult> UpdateCodeAsync(
        Guid id, UpdateServiceCodeRequest request, HttpContext http, ZelvoDbContext db, CurrentUser user, CancellationToken cancellationToken)
    {
        var (service, problem) = await FindAsync(db, user, id, edit: true, cancellationToken);
        if (service is null)
        {
            return problem!;
        }

        var code = request.Code?.Trim().ToUpperInvariant();
        if (!ServiceCards.IsCode(code))
        {
            return UserEndpoints.Validation("code", "Use 2–30 capital letters, digits and hyphens, for example IT-LAPTOP.");
        }

        if (service.PublishedVersionId is not null && code != service.Code)
        {
            return FormsCommon.State("The code of a published service cannot change.");
        }

        if (await db.CatalogServices.AnyAsync(s => s.Code == code && s.Id != id, cancellationToken))
        {
            return ApiProblem.Create(StatusCodes.Status409Conflict, "code-taken", "Another service already has this code.");
        }

        service.Code = code!;
        service.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return await OkAsync(http, db, user, service, cancellationToken);
    }

    internal static async Task<IResult> SubmitAsync(
        Guid id, HttpContext http, ZelvoDbContext db, CurrentUser user, TenantSettingsStore settings, IAuditLog audit, CancellationToken cancellationToken)
    {
        var (service, problem) = await FindAsync(db, user, id, edit: true, cancellationToken);
        if (service is null)
        {
            return problem!;
        }

        if (await VersionProblemAsync(http, db, service, cancellationToken) is { } versionProblem)
        {
            return versionProblem;
        }

        var working = await WorkingAsync(db, service, cancellationToken);
        if (working is not { State: ServiceVersionState.Draft })
        {
            return FormsCommon.State("There are no changes to send for review.");
        }

        var problems = await ServiceCardCheck.CheckAsync(db, service, ServiceCards.Read(working.Content),
            await settings.GetAsync(IncidentSettings.Attachments, cancellationToken), forPublishing: true, cancellationToken);
        if (problems.Count > 0)
        {
            return FormsCommon.Problems("service-has-problems", "Fix the card's problems before sending it for review.", problems);
        }

        var now = DateTimeOffset.UtcNow;
        working.State = ServiceVersionState.UnderReview;
        working.SubmittedById = user.Account?.Id;
        working.SubmittedAt = now;
        working.UpdatedAt = now;
        if (service.State == ServiceState.Draft)
        {
            service.State = ServiceState.UnderReview;
        }

        service.UpdatedAt = now;
        audit.Add(AuditCategory.Change, "catalog.service.submitted", "catalog_service", service.Id.ToString(), Details(service, working));
        await db.SaveChangesAsync(cancellationToken);
        return await OkAsync(http, db, user, service, cancellationToken);
    }

    internal static async Task<IResult> ApproveAsync(
        Guid id, HttpContext http, ZelvoDbContext db, CurrentUser user, TenantSettingsStore settings, ISearchIndexer indexer, IAuditLog audit,
        CancellationToken cancellationToken)
    {
        var service = await db.CatalogServices.SingleOrDefaultAsync(s => s.Id == id, cancellationToken);
        if (service is null)
        {
            return FormsCommon.NotFound("service");
        }

        if (await VersionProblemAsync(http, db, service, cancellationToken) is { } versionProblem)
        {
            return versionProblem;
        }

        var working = await WorkingAsync(db, service, cancellationToken);
        if (working is not { State: ServiceVersionState.UnderReview })
        {
            return FormsCommon.State("Nothing is waiting for review.");
        }

        if (!CatalogAccess.MayReview(user, working))
        {
            return ApiProblem.Create(StatusCodes.Status403Forbidden, "four-eyes",
                "Another publisher reviews this version: you started or last changed it.");
        }

        var card = ServiceCards.Read(working.Content);
        var problems = await ServiceCardCheck.CheckAsync(db, service, card, await settings.GetAsync(IncidentSettings.Attachments, cancellationToken), forPublishing: true, cancellationToken);
        if (problems.Count > 0)
        {
            return FormsCommon.Problems("service-has-problems", "The card has problems; send it back to its authors.", problems);
        }

        var now = DateTimeOffset.UtcNow;
        if (service.PublishedVersionId is { } previousId)
        {
            var previous = await db.CatalogServiceVersions.SingleAsync(v => v.Id == previousId, cancellationToken);
            previous.State = ServiceVersionState.Superseded;
            previous.UpdatedAt = now;
        }

        working.State = ServiceVersionState.Published;
        working.ReviewedById = user.Account?.Id;
        working.PublishedAt = now;
        working.UpdatedAt = now;
        service.PublishedVersionId = working.Id;
        service.WorkingVersionId = null;
        service.State = service.State == ServiceState.Suspended ? ServiceState.Suspended : ServiceState.Active;
        service.PublishedAt = now;
        service.RetiredAt = null;
        CopyPublished(service, card);
        service.UpdatedAt = now;
        await CatalogSearch.IndexAsync(db, indexer, service, cancellationToken);
        audit.Add(AuditCategory.Change, "catalog.service.published", "catalog_service", service.Id.ToString(), Details(service, working));
        await db.SaveChangesAsync(cancellationToken);
        return await OkAsync(http, db, user, service, cancellationToken);
    }

    internal static async Task<IResult> SendBackAsync(
        Guid id, ReviewCommentRequest request, HttpContext http, ZelvoDbContext db, CurrentUser user, IAuditLog audit, CancellationToken cancellationToken)
    {
        var service = await db.CatalogServices.SingleOrDefaultAsync(s => s.Id == id, cancellationToken);
        if (service is null)
        {
            return FormsCommon.NotFound("service");
        }

        var comment = Note(request.Comment);
        if (comment is null)
        {
            return UserEndpoints.Validation("comment", "Say what to change.");
        }

        var working = await WorkingAsync(db, service, cancellationToken);
        if (working is not { State: ServiceVersionState.UnderReview })
        {
            return FormsCommon.State("Nothing is waiting for review.");
        }

        if (!CatalogAccess.MayReview(user, working))
        {
            return ApiProblem.Create(StatusCodes.Status403Forbidden, "four-eyes", "Another publisher reviews this version: you started or last changed it.");
        }

        var now = DateTimeOffset.UtcNow;
        working.State = ServiceVersionState.Draft;
        working.ReviewedById = user.Account?.Id;
        working.ReviewComment = comment;
        working.UpdatedAt = now;
        if (service.State == ServiceState.UnderReview)
        {
            service.State = ServiceState.Draft;
        }

        service.UpdatedAt = now;
        audit.Add(AuditCategory.Change, "catalog.service.sent-back", "catalog_service", service.Id.ToString(), Details(service, working));
        await db.SaveChangesAsync(cancellationToken);
        return await OkAsync(http, db, user, service, cancellationToken);
    }

    internal static async Task<IResult> DiscardAsync(
        Guid id, HttpContext http, ZelvoDbContext db, CurrentUser user, IAttachmentStore store, IAuditLog audit, CancellationToken cancellationToken)
    {
        var (service, problem) = await FindAsync(db, user, id, edit: true, cancellationToken);
        if (service is null)
        {
            return problem!;
        }

        var working = await WorkingAsync(db, service, cancellationToken);
        if (working is null)
        {
            return FormsCommon.State("There are no changes to discard.");
        }

        if (service.PublishedVersionId is null && service.State != ServiceState.Retired)
        {
            return await DeleteServiceAsync(db, store, audit, service, cancellationToken);
        }

        service.WorkingVersionId = null;
        db.CatalogServiceVersions.Remove(working);
        var published = service.PublishedVersionId is { } p ? await db.CatalogServiceVersions.AsNoTracking().SingleAsync(v => v.Id == p, cancellationToken) : null;
        service.EditorCatalogIds = published is null ? [.. service.CatalogIds] : [.. ServiceCards.Read(published.Content).CatalogIds];
        service.UpdatedAt = DateTimeOffset.UtcNow;
        audit.Add(AuditCategory.Change, "catalog.service.discarded", "catalog_service", service.Id.ToString(), Details(service, working));
        await db.SaveChangesAsync(cancellationToken);
        return await OkAsync(http, db, user, service, cancellationToken);
    }

    internal static async Task<IResult> SuspendAsync(
        Guid id, SuspendServiceRequest request, HttpContext http, ZelvoDbContext db, CurrentUser user, ISearchIndexer indexer, IAuditLog audit,
        CancellationToken cancellationToken)
    {
        var service = await db.CatalogServices.SingleOrDefaultAsync(s => s.Id == id, cancellationToken);
        if (service is null)
        {
            return FormsCommon.NotFound("service");
        }

        var message = FormsCommon.Clean(request.Message);
        if (message is null || !message.HasAnyValue || !ServiceDesk.DirectoryProblems.Fits(message, CatalogService.SuspendedMessageMaxLength))
        {
            return UserEndpoints.Validation("message", $"Tell requesters why, in English or Arabic, at most {CatalogService.SuspendedMessageMaxLength} characters.");
        }

        if (service.State is not (ServiceState.Active or ServiceState.Suspended))
        {
            return FormsCommon.State("Only active services can be suspended.");
        }

        service.State = ServiceState.Suspended;
        service.SuspendedMessage = message;
        service.UpdatedAt = DateTimeOffset.UtcNow;
        await CatalogSearch.IndexAsync(db, indexer, service, cancellationToken);
        audit.Add(AuditCategory.Change, "catalog.service.suspended", "catalog_service", service.Id.ToString(), Details(service, null));
        await db.SaveChangesAsync(cancellationToken);
        return await OkAsync(http, db, user, service, cancellationToken);
    }

    internal static async Task<IResult> ResumeAsync(
        Guid id, HttpContext http, ZelvoDbContext db, CurrentUser user, ISearchIndexer indexer, IAuditLog audit, CancellationToken cancellationToken)
    {
        var service = await db.CatalogServices.SingleOrDefaultAsync(s => s.Id == id, cancellationToken);
        if (service is null)
        {
            return FormsCommon.NotFound("service");
        }

        if (service.State != ServiceState.Suspended)
        {
            return FormsCommon.State("Only suspended services can be resumed.");
        }

        service.State = ServiceState.Active;
        service.SuspendedMessage = new LocalizedText(null, null);
        service.UpdatedAt = DateTimeOffset.UtcNow;
        await CatalogSearch.IndexAsync(db, indexer, service, cancellationToken);
        audit.Add(AuditCategory.Change, "catalog.service.resumed", "catalog_service", service.Id.ToString(), Details(service, null));
        await db.SaveChangesAsync(cancellationToken);
        return await OkAsync(http, db, user, service, cancellationToken);
    }

    internal static async Task<IResult> RetireAsync(
        Guid id, HttpContext http, ZelvoDbContext db, CurrentUser user, ISearchIndexer indexer, IAuditLog audit, CancellationToken cancellationToken)
    {
        var service = await db.CatalogServices.SingleOrDefaultAsync(s => s.Id == id, cancellationToken);
        if (service is null)
        {
            return FormsCommon.NotFound("service");
        }

        if (service.PublishedVersionId is not { } publishedId || service.State == ServiceState.Retired)
        {
            return FormsCommon.State("Only published services can be retired; delete drafts instead.");
        }

        var now = DateTimeOffset.UtcNow;
        if (await WorkingAsync(db, service, cancellationToken) is { } working)
        {
            service.WorkingVersionId = null;
            db.CatalogServiceVersions.Remove(working);
        }

        var published = await db.CatalogServiceVersions.SingleAsync(v => v.Id == publishedId, cancellationToken);
        published.State = ServiceVersionState.Retired;
        published.UpdatedAt = now;
        service.State = ServiceState.Retired;
        service.RetiredAt = now;
        service.SuspendedMessage = new LocalizedText(null, null);
        service.EditorCatalogIds = [.. service.CatalogIds];
        service.UpdatedAt = now;
        await CatalogSearch.IndexAsync(db, indexer, service, cancellationToken);
        audit.Add(AuditCategory.Change, "catalog.service.retired", "catalog_service", service.Id.ToString(), Details(service, published));
        await db.SaveChangesAsync(cancellationToken);
        return await OkAsync(http, db, user, service, cancellationToken);
    }

    internal static async Task<IResult> ReopenAsync(Guid id, HttpContext http, ZelvoDbContext db, CurrentUser user, IAuditLog audit, CancellationToken cancellationToken)
    {
        var (service, problem) = await FindAsync(db, user, id, edit: true, cancellationToken);
        if (service is null)
        {
            return problem!;
        }

        if (service.State != ServiceState.Retired || service.WorkingVersionId is not null || service.PublishedVersionId is not { } lastId)
        {
            return FormsCommon.State("Only retired services can be reopened.");
        }

        var last = await db.CatalogServiceVersions.AsNoTracking().SingleAsync(v => v.Id == lastId, cancellationToken);
        var working = new CatalogServiceVersion
        {
            ServiceId = service.Id,
            Number = await NextNumberAsync(db, service.Id, cancellationToken),
            Content = last.Content,
            CreatedById = user.Account?.Id,
            LastEditedById = user.Account?.Id,
            ChangeNote = "Reopened",
        };
        db.CatalogServiceVersions.Add(working);
        service.WorkingVersionId = working.Id;
        service.UpdatedAt = DateTimeOffset.UtcNow;
        audit.Add(AuditCategory.Change, "catalog.service.reopened", "catalog_service", service.Id.ToString(), Details(service, working));
        await db.SaveChangesAsync(cancellationToken);
        return await OkAsync(http, db, user, service, cancellationToken);
    }

    internal static async Task<IResult> DeleteAsync(Guid id, ZelvoDbContext db, CurrentUser user, IAttachmentStore store, IAuditLog audit, CancellationToken cancellationToken)
    {
        var (service, problem) = await FindAsync(db, user, id, edit: true, cancellationToken);
        if (service is null)
        {
            return problem!;
        }

        if (service.PublishedVersionId is not null)
        {
            return FormsCommon.State("Published services are retired, not deleted.");
        }

        return await DeleteServiceAsync(db, store, audit, service, cancellationToken);
    }

    private static async Task<IResult> DeleteServiceAsync(ZelvoDbContext db, IAttachmentStore store, IAuditLog audit, CatalogService service, CancellationToken cancellationToken)
    {
        if (await db.CatalogServices.AnyAsync(s => s.ParentId == service.Id, cancellationToken))
        {
            return ApiProblem.Create(StatusCodes.Status409Conflict, "service-has-children", "Sub-services are below this service. Move them first.");
        }

        var files = await db.TicketAttachments.Where(a => a.ServiceId == service.Id).ToListAsync(cancellationToken);
        foreach (var file in files)
        {
            await store.RemoveAsync(file.Id, cancellationToken);
        }

        db.TicketAttachments.RemoveRange(files);
        service.WorkingVersionId = null;
        await db.SaveChangesAsync(cancellationToken);
        db.CatalogServiceVersions.RemoveRange(await db.CatalogServiceVersions.Where(v => v.ServiceId == service.Id).ToListAsync(cancellationToken));
        db.CatalogServices.Remove(service);
        audit.Add(AuditCategory.Delete, "catalog.service.deleted", "catalog_service", service.Id.ToString(),
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["code"] = service.Code });
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.NoContent();
    }

    internal static async Task<IResult> CopyAsync(
        Guid id, CopyServiceRequest request, ZelvoDbContext db, CurrentUser user, IAuditLog audit, CancellationToken cancellationToken)
    {
        var (service, problem) = await FindAsync(db, user, id, edit: false, cancellationToken);
        if (service is null)
        {
            return problem!;
        }

        var edited = await CatalogAccess.EditedCatalogsAsync(db, user, cancellationToken);
        if (!CatalogAccess.IsManager(user) && edited.Count == 0)
        {
            return CatalogEndpoints.Forbidden();
        }

        var code = request.Code?.Trim().ToUpperInvariant();
        if (!ServiceCards.IsCode(code))
        {
            return UserEndpoints.Validation("code", "Use 2–30 capital letters, digits and hyphens, for example IT-LAPTOP.");
        }

        if (await db.CatalogServices.AnyAsync(s => s.Code == code, cancellationToken))
        {
            return ApiProblem.Create(StatusCodes.Status409Conflict, "code-taken", "Another service already has this code.");
        }

        var sourceId = service.WorkingVersionId ?? service.PublishedVersionId!.Value;
        var source = ServiceCards.Read(await db.CatalogServiceVersions.Where(v => v.Id == sourceId).Select(v => v.Content).SingleAsync(cancellationToken));
        var card = source with
        {
            Name = FormsCommon.Clean(request.Name) is { HasAnyValue: true } name ? name : source.Name,
            ImageFileId = null,
            Documents = [.. source.Documents.Select(d => d with { TemplateFileId = null })],
            CatalogIds = CatalogAccess.IsManager(user) ? source.CatalogIds : [.. source.CatalogIds.Where(edited.Contains)],
        };
        if (!CatalogAccess.IsManager(user) && card.CatalogIds.Count == 0)
        {
            card = card with { CatalogIds = [edited.First()] };
        }

        var copy = await NewDraftAsync(db, user, code!, card, cancellationToken);
        audit.Add(AuditCategory.Create, "catalog.service.copied", "catalog_service", copy.Id.ToString(),
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["code"] = code, ["copyOf"] = service.Code });
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Created($"/api/v1/catalog/services/{copy.Id}", await ResponseAsync(db, user, copy, cancellationToken));
    }

    internal static async Task<IResult> SaveAsTemplateAsync(
        Guid id, SaveTemplateRequest request, ZelvoDbContext db, CurrentUser user, IAuditLog audit, CancellationToken cancellationToken)
    {
        var service = await db.CatalogServices.AsNoTracking().SingleOrDefaultAsync(s => s.Id == id, cancellationToken);
        if (service is null)
        {
            return FormsCommon.NotFound("service");
        }

        var name = FormsCommon.Clean(request.Name);
        if (name is null || !name.HasAnyValue || !ServiceDesk.DirectoryProblems.Fits(name, CatalogService.NameMaxLength))
        {
            return UserEndpoints.Validation("name", "Name the template in English or Arabic.");
        }

        var sourceId = service.WorkingVersionId ?? service.PublishedVersionId!.Value;
        var source = ServiceCards.Read(await db.CatalogServiceVersions.Where(v => v.Id == sourceId).Select(v => v.Content).SingleAsync(cancellationToken));
        var template = new CatalogServiceTemplate
        {
            Name = name,
            Description = FormsCommon.Clean(request.Description) ?? new(null, null),
            Content = ServiceCards.Write(ServiceTemplates.Generalise(source)),
            CreatedById = user.Account?.Id,
        };
        db.CatalogServiceTemplates.Add(template);
        audit.Add(AuditCategory.Create, "catalog.template.saved", "catalog_service_template", template.Id.ToString(),
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["from"] = service.Code });
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Created($"/api/v1/catalog/templates/{template.Id}", ServiceTemplateResponse.From(template));
    }

    internal static async Task<IResult> VersionsAsync(Guid id, ZelvoDbContext db, CurrentUser user, CancellationToken cancellationToken)
    {
        var (service, problem) = await FindAsync(db, user, id, edit: false, cancellationToken);
        if (service is null)
        {
            return problem!;
        }

        var versions = await db.CatalogServiceVersions.AsNoTracking().Where(v => v.ServiceId == id).OrderByDescending(v => v.Number).ToListAsync(cancellationToken);
        var names = await IncidentEndpoints.Names(db, versions.SelectMany(v => new[] { v.CreatedById, v.LastEditedById, v.SubmittedById, v.ReviewedById })
            .Where(x => x is not null).Select(x => x!.Value), cancellationToken);
        return TypedResults.Ok(versions.Select(v => ServiceVersionResponse.From(v, names, withCard: false)).ToList());
    }

    internal static async Task<IResult> VersionAsync(Guid id, int number, ZelvoDbContext db, CurrentUser user, CancellationToken cancellationToken)
    {
        var (service, problem) = await FindAsync(db, user, id, edit: false, cancellationToken);
        if (service is null)
        {
            return problem!;
        }

        var version = await db.CatalogServiceVersions.AsNoTracking().SingleOrDefaultAsync(v => v.ServiceId == id && v.Number == number, cancellationToken);
        if (version is null)
        {
            return FormsCommon.NotFound("version");
        }

        var names = await IncidentEndpoints.Names(db, new[] { version.CreatedById, version.LastEditedById, version.SubmittedById, version.ReviewedById }
            .Where(x => x is not null).Select(x => x!.Value), cancellationToken);
        return TypedResults.Ok(ServiceVersionResponse.From(version, names, withCard: true));
    }

    internal static async Task<IResult> UploadFileAsync(
        Guid id, IFormFile? file, ZelvoDbContext db, CurrentUser user, TenantSettingsStore settings, IAttachmentStore store,
        IAuditLog audit, IOptions<AttachmentScanningOptions> scanning, CancellationToken cancellationToken)
    {
        var (service, problem) = await FindAsync(db, user, id, edit: true, cancellationToken);
        if (service is null)
        {
            return problem!;
        }

        if (file is null || file.Length == 0)
        {
            return UserEndpoints.Validation("file", "Choose a file.");
        }

        if (await db.TicketAttachments.CountAsync(a => a.ServiceId == id, cancellationToken) >= 100)
        {
            return ApiProblem.Create(StatusCodes.Status409Conflict, "too-many-files", "A service has at most 100 files. Remove some first.");
        }

        var policy = await settings.GetAsync(IncidentSettings.Attachments, cancellationToken);
        if (file.Length > policy.MaxBytes)
        {
            return ApiProblem.Create(StatusCodes.Status422UnprocessableEntity, "file-too-large", $"Files can be at most {policy.MaxSizeMb} MB.");
        }

        var name = FileTypes.CleanFileName(file.FileName);
        var type = FileTypes.Find(FileTypes.ExtensionOf(name));
        var allowed = policy.AllowedExtensions.Concat(Knowledge.KnowledgePortalEndpoints.ImageExtensions).ToHashSet(StringComparer.Ordinal);
        if (name.Length == 0 || type is null || !allowed.Contains(type.Extension))
        {
            return ApiProblem.Create(StatusCodes.Status422UnprocessableEntity, "file-type-not-allowed", $"Accepted file types: {string.Join(", ", allowed.Order(StringComparer.Ordinal))}.");
        }

        byte[] content;
        await using (var stream = file.OpenReadStream())
        using (var buffer = new MemoryStream((int)file.Length))
        {
            await stream.CopyToAsync(buffer, cancellationToken);
            content = buffer.ToArray();
        }

        if (!type.Check(content))
        {
            return ApiProblem.Create(StatusCodes.Status422UnprocessableEntity, "file-content-mismatch", "The file's content does not match its type.");
        }

        var attachment = new TicketAttachment
        {
            ServiceId = service.Id,
            FileName = name,
            ContentType = type.ContentType,
            Size = content.LongLength,
            Sha256 = Convert.ToHexStringLower(SHA256.HashData(content)),
            ScanStatus = scanning.Value.Enabled ? AttachmentScanStatus.Pending : AttachmentScanStatus.NotScanned,
            UploadedById = user.Account?.Id,
        };
        db.TicketAttachments.Add(attachment);
        store.Add(attachment.Id, content);
        audit.Add(AuditCategory.Change, "catalog.file.added", "catalog_service", service.Id.ToString(),
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["fileId"] = attachment.Id.ToString(), ["fileName"] = name, ["size"] = attachment.Size, ["sha256"] = attachment.Sha256 });
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Created($"/api/v1/catalog/services/{service.Id}/files/{attachment.Id}", ServiceFileResponse.From(attachment));
    }

    internal static async Task<IResult> DeleteFileAsync(
        Guid id, Guid fileId, ZelvoDbContext db, CurrentUser user, IAttachmentStore store, IAuditLog audit, CancellationToken cancellationToken)
    {
        var (service, problem) = await FindAsync(db, user, id, edit: true, cancellationToken);
        if (service is null)
        {
            return problem!;
        }

        var file = await db.TicketAttachments.SingleOrDefaultAsync(a => a.Id == fileId && a.ServiceId == id, cancellationToken);
        if (file is null)
        {
            return FormsCommon.NotFound("file");
        }

        if (service.PublishedVersionId is { } published
            && ServiceCards.Read(await db.CatalogServiceVersions.Where(v => v.Id == published).Select(v => v.Content).SingleAsync(cancellationToken)) is var card
            && (card.ImageFileId == fileId || card.Documents.Any(d => d.TemplateFileId == fileId)))
        {
            return ApiProblem.Create(StatusCodes.Status409Conflict, "file-in-use", "The published card uses this file. Publish a card without it first.");
        }

        await store.RemoveAsync(file.Id, cancellationToken);
        db.TicketAttachments.Remove(file);
        audit.Add(AuditCategory.Change, "catalog.file.removed", "catalog_service", id.ToString(),
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["fileId"] = file.Id.ToString(), ["fileName"] = file.FileName });
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.NoContent();
    }

    internal static async Task<IResult> FileAsync(
        Guid id, Guid fileId, HttpContext http, ZelvoDbContext db, CurrentUser user, IAttachmentStore store, CancellationToken cancellationToken)
    {
        var (service, problem) = await FindAsync(db, user, id, edit: false, cancellationToken);
        if (service is null)
        {
            return problem!;
        }

        return await ServiceFiles.SendAsync(http, db, store, id, fileId, cancellationToken);
    }

    // ---- helpers ----

    /// <summary>The service when the caller works on it (edit: may change it); 404 for anyone else, so ids cannot be probed.</summary>
    internal static async Task<(CatalogService? Service, IResult? Problem)> FindAsync(
        ZelvoDbContext db, CurrentUser user, Guid id, bool edit, CancellationToken cancellationToken)
    {
        var service = await db.CatalogServices.SingleOrDefaultAsync(s => s.Id == id, cancellationToken);
        if (service is null)
        {
            return (null, FormsCommon.NotFound("service"));
        }

        var edited = await CatalogAccess.EditedCatalogsAsync(db, user, cancellationToken);
        if (!CatalogAccess.MayAdminister(user, edited, service))
        {
            return (null, FormsCommon.NotFound("service"));
        }

        if (edit && !CatalogAccess.MayEdit(user, edited, service))
        {
            return (null, ApiProblem.Create(StatusCodes.Status403Forbidden, "forbidden", "Only catalog managers and editors of the service's catalogs change it."));
        }

        return (service, null);
    }

    private static async Task<CatalogServiceVersion?> WorkingAsync(ZelvoDbContext db, CatalogService service, CancellationToken cancellationToken) =>
        service.WorkingVersionId is { } id ? await db.CatalogServiceVersions.SingleOrDefaultAsync(v => v.Id == id, cancellationToken) : null;

    private static async Task<int> NextNumberAsync(ZelvoDbContext db, Guid serviceId, CancellationToken cancellationToken) =>
        (await db.CatalogServiceVersions.Where(v => v.ServiceId == serviceId).MaxAsync(v => (int?)v.Number, cancellationToken) ?? 0) + 1;

    private static string? Note(string? note) =>
        string.IsNullOrWhiteSpace(note) ? null : note.Trim()[..Math.Min(note.Trim().Length, CatalogServiceVersion.NoteMaxLength)];

    /// <summary>Copies from a working card what lists and editors need; the published values stay until approval.</summary>
    private static void CopyWorking(CatalogService service, ServiceCard card)
    {
        service.EditorCatalogIds = [.. card.CatalogIds.Union(service.PublishedVersionId is null ? [] : service.CatalogIds)];
        if (service.PublishedVersionId is null)
        {
            CopyPublished(service, card);
        }
    }

    /// <summary>Copies the values that lists, the hierarchy and access checks need from the published card.</summary>
    internal static void CopyPublished(CatalogService service, ServiceCard card)
    {
        service.Name = card.Name.HasAnyValue ? card.Name : new LocalizedText(service.Code, null);
        service.ParentId = card.ParentId;
        service.CatalogIds = [.. card.CatalogIds];
        service.CategoryIds = [.. card.CategoryIds];
        service.EditorCatalogIds = [.. card.CatalogIds];
    }

    /// <summary>
    /// Checks If-Match (when sent) against the version that changes next: the working version, or
    /// the published one when there is none.
    /// </summary>
    private static async Task<IResult?> VersionProblemAsync(HttpContext http, ZelvoDbContext db, CatalogService service, CancellationToken cancellationToken)
    {
        var ifMatch = http.Request.Headers.IfMatch.ToString();
        if (string.IsNullOrEmpty(ifMatch))
        {
            return null;
        }

        var targetId = service.WorkingVersionId ?? service.PublishedVersionId;
        var target = targetId is { } t ? await db.CatalogServiceVersions.SingleOrDefaultAsync(v => v.Id == t, cancellationToken) : null;
        if (target is null || !uint.TryParse(ifMatch.Trim().Replace("W/", "", StringComparison.Ordinal).Trim('"'), out var version) || version != target.Version)
        {
            return FormsCommon.VersionMismatch();
        }

        db.Entry(target).Property(v => v.Version).OriginalValue = version;
        return null;
    }

    private static Dictionary<string, object?> Details(CatalogService service, CatalogServiceVersion? version) => new(StringComparer.Ordinal)
    {
        ["code"] = service.Code,
        ["version"] = version?.Number,
    };

    private static async Task<IResult> OkAsync(HttpContext http, ZelvoDbContext db, CurrentUser user, CatalogService service, CancellationToken cancellationToken)
    {
        var response = await ResponseAsync(db, user, service, cancellationToken);
        http.Response.Headers.ETag = response.Etag;
        return TypedResults.Ok(response);
    }

    internal static async Task<ServiceResponse> ResponseAsync(ZelvoDbContext db, CurrentUser user, CatalogService service, CancellationToken cancellationToken)
    {
        var fresh = await db.CatalogServices.AsNoTracking().SingleAsync(s => s.Id == service.Id, cancellationToken);
        var ids = new[] { fresh.PublishedVersionId, fresh.WorkingVersionId }.Where(i => i is not null).Select(i => i!.Value).ToList();
        var versions = await db.CatalogServiceVersions.AsNoTracking().Where(v => ids.Contains(v.Id)).ToListAsync(cancellationToken);
        var published = versions.FirstOrDefault(v => v.Id == fresh.PublishedVersionId);
        var working = versions.FirstOrDefault(v => v.Id == fresh.WorkingVersionId);
        var policy = await TicketSettingKeys.ReadAsync(db, TicketSettingKeys.AttachmentPolicyKey, AttachmentPolicy.Default, p => p.IsValid(), cancellationToken);
        var problems = working is null ? [] : await ServiceCardCheck.CheckAsync(db, fresh, ServiceCards.Read(working.Content), policy, forPublishing: true, cancellationToken);
        var files = await db.TicketAttachments.AsNoTracking().Where(a => a.ServiceId == fresh.Id).OrderBy(a => a.UploadedAt).ToListAsync(cancellationToken);
        var names = await IncidentEndpoints.Names(db, versions.SelectMany(v => new[] { v.CreatedById, v.LastEditedById, v.SubmittedById, v.ReviewedById })
            .Where(x => x is not null).Select(x => x!.Value), cancellationToken);
        var edited = await CatalogAccess.EditedCatalogsAsync(db, user, cancellationToken);
        var mayEdit = CatalogAccess.MayEdit(user, edited, fresh);
        var mayPublish = user.Has(Permissions.CatalogPublish);
        var requests = await db.ServiceRequests.CountAsync(r => r.ServiceId == fresh.Id, cancellationToken);
        var allowed = new List<string>();
        if (mayEdit && working?.State != ServiceVersionState.UnderReview && !(fresh.State == ServiceState.Retired && working is null)) allowed.Add("edit");
        if (mayEdit && working?.State == ServiceVersionState.Draft) allowed.Add("submit");
        if (working?.State == ServiceVersionState.UnderReview && CatalogAccess.MayReview(user, working)) allowed.AddRange(["approve", "sendBack"]);
        if (mayEdit && working is not null) allowed.Add("discard");
        if (mayPublish && fresh.State == ServiceState.Active) allowed.Add("suspend");
        if (mayPublish && fresh.State == ServiceState.Suspended) allowed.Add("resume");
        if (mayPublish && fresh.State is ServiceState.Active or ServiceState.Suspended) allowed.Add("retire");
        if (mayEdit && fresh.State == ServiceState.Retired && working is null) allowed.Add("reopen");
        if (mayEdit && fresh.PublishedVersionId is null && fresh.State != ServiceState.Retired) allowed.Add("delete");
        if (CatalogAccess.IsManager(user) || edited.Count > 0) allowed.Add("copy");
        if (CatalogAccess.IsManager(user)) allowed.Add("saveAsTemplate");

        return new ServiceResponse(
            fresh.Id, fresh.Code, fresh.State, LocalizedTextDto.From(fresh.Name), fresh.ParentId, fresh.CatalogIds, LocalizedTextDto.From(fresh.SuspendedMessage),
            published is null ? null : ServiceVersionResponse.From(published, names, withCard: true),
            working is null ? null : ServiceVersionResponse.From(working, names, withCard: true),
            problems, [.. files.Select(ServiceFileResponse.From)], allowed, requests,
            $"\"{(working ?? published)?.Version ?? 0}\"", fresh.CreatedAt, fresh.UpdatedAt, fresh.PublishedAt, fresh.RetiredAt);
    }

    private static async Task<List<ServiceSummaryResponse>> SummariesAsync(
        ZelvoDbContext db, CurrentUser user, IReadOnlySet<Guid> edited, IReadOnlyList<CatalogService> rows, CancellationToken cancellationToken)
    {
        var ids = rows.SelectMany(s => new[] { s.PublishedVersionId, s.WorkingVersionId }).Where(i => i is not null).Select(i => i!.Value).ToList();
        var versions = await db.CatalogServiceVersions.AsNoTracking().Where(v => ids.Contains(v.Id))
            .Select(v => new { v.Id, v.Number, v.State, v.SubmittedAt }).ToDictionaryAsync(v => v.Id, cancellationToken);
        return [.. rows.Select(s =>
        {
            var published = s.PublishedVersionId is { } p ? versions.GetValueOrDefault(p) : null;
            var working = s.WorkingVersionId is { } w ? versions.GetValueOrDefault(w) : null;
            return new ServiceSummaryResponse(s.Id, s.Code, s.State, LocalizedTextDto.From(s.Name), s.ParentId, s.EditorCatalogIds, s.CategoryIds,
                published?.Number, working?.Number, working?.State, working?.SubmittedAt, CatalogAccess.MayEdit(user, edited, s), s.UpdatedAt);
        })];
    }
}

/// <summary>Sends a service's file once it is scanned clean.</summary>
internal static class ServiceFiles
{
    public static async Task<IResult> SendAsync(HttpContext http, ZelvoDbContext db, IAttachmentStore store, Guid serviceId, Guid fileId, CancellationToken cancellationToken)
    {
        var file = await db.TicketAttachments.AsNoTracking().SingleOrDefaultAsync(a => a.Id == fileId && a.ServiceId == serviceId, cancellationToken);
        if (file is null)
        {
            return FormsCommon.NotFound("file");
        }

        if (!IncidentAttachmentEndpoints.IsDownloadable(file))
        {
            return ApiProblem.Create(StatusCodes.Status409Conflict, "file-not-available",
                file.ScanStatus == AttachmentScanStatus.Infected ? "The file was rejected: malware was found in it." : "The file is still being checked for malware.");
        }

        var content = await store.ReadAsync(file.Id, cancellationToken);
        if (content is null)
        {
            return FormsCommon.NotFound("file");
        }

        http.Response.Headers.ContentSecurityPolicy = "default-src 'none'; sandbox";
        http.Response.Headers.XContentTypeOptions = "nosniff";
        http.Response.Headers.CacheControl = "private, max-age=300";
        var image = Knowledge.KnowledgePortalEndpoints.ImageExtensions.Contains(FileTypes.ExtensionOf(file.FileName), StringComparer.Ordinal);
        // Images are shown inline; anything else is saved by the browser.
        return image ? TypedResults.File(content, file.ContentType) : TypedResults.File(content, file.ContentType, file.FileName);
    }
}

/// <summary>Filters of the service list.</summary>
internal sealed class ServiceListQuery
{
    /// <summary>Only services in this state.</summary>
    public ServiceState? State { get; init; }

    /// <summary>Only services in this catalog.</summary>
    public Guid? CatalogId { get; init; }

    /// <summary>Text in the code or name.</summary>
    [StringLength(100)]
    public string? Q { get; init; }
}

/// <summary>A new service.</summary>
internal sealed class CreateServiceRequest
{
    /// <summary>Unique code, for example IT-LAPTOP.</summary>
    [Required, StringLength(CatalogService.CodeMaxLength)]
    public string? Code { get; init; }

    /// <summary>Name; from the template when left out.</summary>
    public LocalizedTextInput? Name { get; init; }

    /// <summary>Catalogs to put it in.</summary>
    public IReadOnlyList<Guid>? CatalogIds { get; init; }

    /// <summary>A template: builtin:&lt;key&gt; or a saved template's id.</summary>
    [StringLength(80)]
    public string? TemplateId { get; init; }
}

/// <summary>The working card to save.</summary>
internal sealed class SaveServiceDraftRequest
{
    /// <summary>The whole card.</summary>
    [Required]
    public ServiceCard? Card { get; init; }

    /// <summary>What changed.</summary>
    [StringLength(CatalogServiceVersion.NoteMaxLength)]
    public string? ChangeNote { get; init; }
}

/// <summary>A new code.</summary>
internal sealed class UpdateServiceCodeRequest
{
    /// <summary>Unique code.</summary>
    [Required, StringLength(CatalogService.CodeMaxLength)]
    public string? Code { get; init; }
}

/// <summary>Why a version is sent back.</summary>
internal sealed class ReviewCommentRequest
{
    /// <summary>What to change.</summary>
    [StringLength(CatalogServiceVersion.NoteMaxLength)]
    public string? Comment { get; init; }
}

/// <summary>Why a service is suspended.</summary>
internal sealed class SuspendServiceRequest
{
    /// <summary>Shown to requesters.</summary>
    [Required]
    public LocalizedTextInput? Message { get; init; }
}

/// <summary>A copy's code and name.</summary>
internal sealed class CopyServiceRequest
{
    /// <summary>Unique code of the copy.</summary>
    [Required, StringLength(CatalogService.CodeMaxLength)]
    public string? Code { get; init; }

    /// <summary>Name of the copy; the original's when left out.</summary>
    public LocalizedTextInput? Name { get; init; }
}

/// <summary>A template's name.</summary>
internal sealed class SaveTemplateRequest
{
    /// <summary>Name.</summary>
    [Required]
    public LocalizedTextInput? Name { get; init; }

    /// <summary>What it is for.</summary>
    public LocalizedTextInput? Description { get; init; }
}

/// <summary>A service in lists.</summary>
internal sealed record ServiceSummaryResponse(
    Guid Id, string Code, ServiceState State, LocalizedTextDto Name, Guid? ParentId, IReadOnlyList<Guid> CatalogIds, IReadOnlyList<Guid> CategoryIds,
    int? PublishedVersion, int? WorkingVersion, ServiceVersionState? WorkingState, DateTimeOffset? SubmittedAt, bool MayEdit, DateTimeOffset UpdatedAt);

/// <summary>One version of a card.</summary>
internal sealed record ServiceVersionResponse(
    Guid Id, int Number, ServiceVersionState State, ServiceCard? Card, string? ChangeNote, Guid? CreatedById, string? CreatedBy,
    Guid? LastEditedById, string? LastEditedBy, string? SubmittedBy, DateTimeOffset? SubmittedAt, string? ReviewedBy, string? ReviewComment,
    DateTimeOffset? PublishedAt, DateTimeOffset UpdatedAt)
{
    public static ServiceVersionResponse From(CatalogServiceVersion v, IReadOnlyDictionary<Guid, string?> names, bool withCard)
    {
        string? Name(Guid? id) => id is { } g ? names.GetValueOrDefault(g) : null;
        return new(v.Id, v.Number, v.State, withCard ? ServiceCards.Read(v.Content) : null, v.ChangeNote,
            v.CreatedById, Name(v.CreatedById), v.LastEditedById, Name(v.LastEditedById), Name(v.SubmittedById), v.SubmittedAt,
            Name(v.ReviewedById), v.ReviewComment, v.PublishedAt, v.UpdatedAt);
    }
}

/// <summary>A file of a service.</summary>
internal sealed record ServiceFileResponse(Guid Id, string FileName, string ContentType, long Size, AttachmentScanStatus ScanStatus, DateTimeOffset UploadedAt)
{
    public static ServiceFileResponse From(TicketAttachment a) => new(a.Id, a.FileName, a.ContentType, a.Size, a.ScanStatus, a.UploadedAt);
}

/// <summary>A service as those who build it see it.</summary>
internal sealed record ServiceResponse(
    Guid Id, string Code, ServiceState State, LocalizedTextDto Name, Guid? ParentId, IReadOnlyList<Guid> CatalogIds, LocalizedTextDto SuspendedMessage,
    ServiceVersionResponse? Published, ServiceVersionResponse? Working, IReadOnlyList<FormProblem> Problems, IReadOnlyList<ServiceFileResponse> Files,
    IReadOnlyList<string> Actions, int RequestCount, string Etag, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, DateTimeOffset? PublishedAt,
    DateTimeOffset? RetiredAt);
