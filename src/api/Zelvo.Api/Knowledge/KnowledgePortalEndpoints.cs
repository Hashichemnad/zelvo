using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Zelvo.Api.Conventions;
using Zelvo.Api.Identity;
using Zelvo.Api.Incidents;
using Zelvo.Api.Settings;
using Zelvo.Api.Tenancy;
using Zelvo.Data;
using Zelvo.Data.Auditing;
using Zelvo.Data.Knowledge;
using Zelvo.Data.Localization;
using Zelvo.Data.Tenancy;
using Zelvo.Data.Tickets;
using Zelvo.Search;
using Zelvo.Tickets;

namespace Zelvo.Api.Knowledge;

/// <summary>
/// Reading published articles (module 7): external articles in the portal (also for visitors when
/// the tenant allows it), internal ones for staff; "Was this helpful?", view counts, article
/// images; and Settings → Knowledge.
/// </summary>
internal static class KnowledgePortalEndpoints
{
    /// <summary>Image types an article may contain (no SVG: it can carry scripts).</summary>
    public static IReadOnlyList<string> ImageExtensions { get; } = ["png", "jpg", "jpeg", "gif", "webp"];

    public static RouteGroupBuilder MapKnowledgePortalEndpoints(this RouteGroupBuilder group)
    {
        var published = group.MapGroup("/knowledge/published").WithTags("Knowledge").RequireTenant();

        published.MapGet("/", ListAsync)
            .WithName("ListPublishedKnowledgeArticles")
            .WithSummary("Published articles the caller may read, most viewed first or newest first: external articles for everyone signed in and, when Settings → Knowledge allows it, for visitors; internal articles too for staff with knowledge.read. Filter by text, category or visibility.")
            .AllowAnonymousAccess()
            .Produces<PageResult<PublishedArticleSummary>>();

        published.MapGet("/{id:guid}", GetAsync)
            .WithName("GetPublishedKnowledgeArticle")
            .WithSummary("The published version of an article (Markdown body), with its images. Readers of the other language see the language that exists (missing lists the missing ones).")
            .AllowAnonymousAccess()
            .Produces<PublishedArticleResponse>();

        published.MapPost("/{id:guid}/views", ViewAsync)
            .WithName("CountKnowledgeView")
            .WithSummary("Counts a view: once per signed-in user, or per visitor device (visitorId, a random id the browser keeps), and day.")
            .AllowAnonymousAccess()
            .Produces<ViewCountResponse>();

        published.MapGet("/{id:guid}/feedback", MyFeedbackAsync)
            .WithName("GetMyKnowledgeFeedback")
            .WithSummary("The caller's own \"Was this helpful?\" answer, if any (visitors pass visitorId).")
            .AllowAnonymousAccess()
            .Produces<MyFeedbackResponse>();

        published.MapPut("/{id:guid}/feedback", GiveFeedbackAsync)
            .WithName("GiveKnowledgeFeedback")
            .WithSummary("\"Was this helpful?\": yes or no with an optional comment. One answer per signed-in user, or per visitor device; giving it again changes it.")
            .AllowAnonymousAccess()
            .Produces<MyFeedbackResponse>()
            .ProducesValidationProblem();

        var images = group.MapGroup("/knowledge/articles/{id:guid}/images").WithTags("Knowledge").RequireTenant();
        images.MapPost("/", UploadImageAsync)
            .WithName("UploadKnowledgeImage")
            .WithSummary("Adds an image (PNG, JPEG, GIF or WebP, up to the tenant's attachment size) to an article, for use in its Markdown as ![text](zelvo-image:<id>). The content must match the type and is scanned for malware before anyone can see it. Needs knowledge.write; audited.")
            .RequirePermission(Permissions.KnowledgeWrite)
            .DisableAntiforgery()
            .Produces<ArticleImageResponse>(StatusCodes.Status201Created);

        images.MapGet("/{imageId:guid}", ImageAsync)
            .WithName("GetKnowledgeImage")
            .WithSummary("An article image, for whoever may read the article (visitors too for public external articles). Only once scanned clean.")
            .AllowAnonymousAccess()
            .Produces<byte[]>(contentType: "image/png");

        images.MapDelete("/{imageId:guid}", DeleteImageAsync)
            .WithName("DeleteKnowledgeImage")
            .WithSummary("Removes an image from an article; versions that show it then show its text instead. Needs knowledge.write; audited.")
            .RequirePermission(Permissions.KnowledgeWrite)
            .Produces(StatusCodes.Status204NoContent);

        var settings = group.MapGroup("/settings/knowledge").WithTags("Settings").RequireTenant();
        settings.MapGet("/", GetSettingsAsync)
            .WithName("GetKnowledgeSettings")
            .WithSummary("Whether visitors who are not signed in can read external articles, and the article number prefix. Open to visitors (the portal needs it).")
            .AllowAnonymousAccess()
            .Produces<KnowledgeSettingsResponse>();

        settings.MapPut("/", UpdateSettingsAsync)
            .WithName("UpdateKnowledgeSettings")
            .WithSummary("Turns public reading of external articles on or off (the search index follows) and sets the number prefix of new articles (1–10 capital letters or digits). Needs settings.manage; audited.")
            .RequirePermission(Permissions.SettingsManage)
            .Produces<KnowledgeSettingsResponse>()
            .ProducesValidationProblem();

        return group;
    }

    internal static async Task<IResult> ListAsync(
        [AsParameters] PageQuery page, [AsParameters] PublishedListQuery filter, ZelvoDbContext db, CurrentUser user, TenantSettingsStore settings,
        CancellationToken cancellationToken)
    {
        if (await SignInProblemAsync(user, settings, cancellationToken) is { } signIn)
        {
            return signIn;
        }

        var staff = user.Has(Permissions.KnowledgeRead) || user.Has(Permissions.KnowledgePublish);
        var query = db.KbArticles.AsNoTracking().Where(a => a.State == ArticleState.Published);
        var visibility = filter.Visibility ?? (staff ? null : ArticleVisibility.External);
        if (!staff || visibility is not null)
        {
            var only = staff ? visibility!.Value : ArticleVisibility.External;
            query = query.Where(a => a.Visibility == only);
        }

        if (filter.CategoryId is { } category)
        {
            query = query.Where(a => a.CategoryId == category);
        }

        var versions = db.KbArticleVersions.AsNoTracking().Where(v => v.State == ArticleVersionState.Published);
        if (filter.Text is { Length: > 0 } text)
        {
            var pattern = "%" + text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal) + "%";
            versions = versions.Where(v => EF.Functions.ILike(v.Title.En ?? string.Empty, pattern) || EF.Functions.ILike(v.Title.Ar ?? string.Empty, pattern)
                || EF.Functions.ILike(v.Summary.En ?? string.Empty, pattern) || EF.Functions.ILike(v.Summary.Ar ?? string.Empty, pattern));
        }

        var joined = query.Join(versions, a => a.PublishedVersionId, v => v.Id, (a, v) => new { Article = a, Version = v });
        joined = filter.Sort == PublishedSort.Newest
            ? joined.OrderByDescending(x => x.Article.PublishedAt).ThenByDescending(x => x.Article.Id)
            : joined.OrderByDescending(x => x.Article.ViewCount).ThenByDescending(x => x.Article.PublishedAt).ThenByDescending(x => x.Article.Id);
        var total = await joined.LongCountAsync(cancellationToken);
        var items = await joined.Skip(page.Skip).Take(page.Size).ToListAsync(cancellationToken);
        return TypedResults.Ok(new PageResult<PublishedArticleSummary>(
            [.. items.Select(x => PublishedArticleSummary.From(x.Article, x.Version))], page.Number, page.Size, total));
    }

    internal static async Task<IResult> GetAsync(Guid id, ZelvoDbContext db, CurrentUser user, TenantSettingsStore settings, CancellationToken cancellationToken)
    {
        var (article, version, problem) = await ReadableAsync(id, db, user, settings, cancellationToken);
        if (problem is not null)
        {
            return problem;
        }

        return TypedResults.Ok(new PublishedArticleResponse(
            PublishedArticleSummary.From(article!, version!), LocalizedTextDto.From(version!.Body), version.Keywords,
            await ImagesAsync(db, article!.Id, cancellationToken)));
    }

    internal static async Task<IResult> ViewAsync(
        Guid id, VisitorRequest? request, ZelvoDbContext db, CurrentUser user, ITenantContext tenant, TenantSettingsStore settings,
        CancellationToken cancellationToken)
    {
        var (article, _, problem) = await ReadableAsync(id, db, user, settings, cancellationToken);
        if (problem is not null)
        {
            return problem;
        }

        if (ViewerHash(user, tenant, request?.VisitorId) is not { } viewer)
        {
            return UserEndpoints.Validation("visitorId", "Visitors send the random device id their browser keeps.");
        }

        var day = DateOnly.FromDateTime(DateTime.UtcNow);
        var added = await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO kb_article_views (tenant_id, article_id, viewer_hash, day)
            VALUES ({tenant.TenantId!.Value}, {article!.Id}, {viewer}, {day})
            ON CONFLICT DO NOTHING
            """, cancellationToken);
        if (added > 0)
        {
            // Counted in the database so that concurrent views all count; not audited (not a change by anyone).
            await db.KbArticles.Where(a => a.Id == article.Id).ExecuteUpdateAsync(s => s.SetProperty(a => a.ViewCount, a => a.ViewCount + 1), cancellationToken);
        }

        var count = await db.KbArticles.AsNoTracking().Where(a => a.Id == article.Id).Select(a => a.ViewCount).SingleAsync(cancellationToken);
        return TypedResults.Ok(new ViewCountResponse(count, added > 0));
    }

    internal static async Task<IResult> MyFeedbackAsync(
        Guid id, [AsParameters] VisitorQuery query, ZelvoDbContext db, CurrentUser user, ITenantContext tenant, TenantSettingsStore settings,
        CancellationToken cancellationToken)
    {
        var (article, _, problem) = await ReadableAsync(id, db, user, settings, cancellationToken);
        if (problem is not null)
        {
            return problem;
        }

        var mine = await FindFeedbackAsync(db, user, tenant, article!.Id, query.VisitorId, cancellationToken);
        return TypedResults.Ok(new MyFeedbackResponse(mine?.Helpful, mine?.Comment, mine?.UpdatedAt, article.HelpfulCount, article.NotHelpfulCount));
    }

    internal static async Task<IResult> GiveFeedbackAsync(
        Guid id, FeedbackRequest request, ZelvoDbContext db, CurrentUser user, ITenantContext tenant, TenantSettingsStore settings,
        CancellationToken cancellationToken)
    {
        var (article, version, problem) = await ReadableAsync(id, db, user, settings, cancellationToken);
        if (problem is not null)
        {
            return problem;
        }

        var visitor = user.Account is null ? request.VisitorId : null;
        if (user.Account is null && !ViewerKeys.IsVisitorId(visitor))
        {
            return UserEndpoints.Validation("visitorId", "Visitors send the random device id their browser keeps.");
        }

        var comment = request.Comment?.Trim() is { Length: > 0 } c ? c : null;
        var feedback = await FindFeedbackAsync(db, user, tenant, article!.Id, visitor, cancellationToken, tracking: true);
        if (feedback is null)
        {
            feedback = new KbFeedback
            {
                ArticleId = article.Id,
                UserId = user.Account?.Id,
                VisitorHash = user.Account is null ? ViewerKeys.ForVisitor(tenant.TenantId!.Value, visitor!) : null,
            };
            db.KbFeedbackEntries.Add(feedback);
        }

        feedback.Helpful = request.Helpful!.Value;
        feedback.Comment = comment;
        feedback.VersionId = version!.Id;
        feedback.UpdatedAt = DateTimeOffset.UtcNow;
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException) when (db.ChangeTracker.Entries<KbFeedback>().Any(e => e.State == EntityState.Added))
        {
            // The same person answered twice at once; the other answer won.
            return ApiProblem.Create(StatusCodes.Status409Conflict, "feedback-conflict", "Your answer was given at the same time from elsewhere. Load the article again.");
        }

        // Totals are counted again in the database, so answers given at the same time all count.
        await db.KbArticles.Where(a => a.Id == article.Id).ExecuteUpdateAsync(s => s
            .SetProperty(a => a.HelpfulCount, a => db.KbFeedbackEntries.Count(f => f.ArticleId == a.Id && f.Helpful))
            .SetProperty(a => a.NotHelpfulCount, a => db.KbFeedbackEntries.Count(f => f.ArticleId == a.Id && !f.Helpful)), cancellationToken);
        var totals = await db.KbArticles.AsNoTracking().Where(a => a.Id == article.Id).Select(a => new { a.HelpfulCount, a.NotHelpfulCount }).SingleAsync(cancellationToken);
        return TypedResults.Ok(new MyFeedbackResponse(feedback.Helpful, feedback.Comment, feedback.UpdatedAt, totals.HelpfulCount, totals.NotHelpfulCount));
    }

    internal static async Task<IResult> UploadImageAsync(
        Guid id, IFormFile? file, ZelvoDbContext db, CurrentUser user, TenantSettingsStore settings, IAttachmentStore store,
        IAuditLog audit, IOptions<AttachmentScanningOptions> scanning, CancellationToken cancellationToken)
    {
        var article = await db.KbArticles.AsNoTracking().SingleOrDefaultAsync(a => a.Id == id, cancellationToken);
        if (article is null || !KnowledgeEndpoints.MaySee(user, article))
        {
            return KnowledgeEndpoints.NotFound();
        }

        if (file is null || file.Length == 0)
        {
            return UserEndpoints.Validation("file", "Choose an image.");
        }

        var policy = await settings.GetAsync(IncidentSettings.Attachments, cancellationToken);
        if (file.Length > policy.MaxBytes)
        {
            return ApiProblem.Create(StatusCodes.Status422UnprocessableEntity, "image-too-large", $"Images can be at most {policy.MaxSizeMb} MB.");
        }

        var name = FileTypes.CleanFileName(file.FileName);
        var type = FileTypes.Find(FileTypes.ExtensionOf(name));
        if (name.Length == 0 || type is null || !ImageExtensions.Contains(type.Extension, StringComparer.Ordinal))
        {
            return ApiProblem.Create(StatusCodes.Status422UnprocessableEntity, "image-type-not-allowed", "Use a PNG, JPEG, GIF or WebP image.");
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
            return ApiProblem.Create(StatusCodes.Status422UnprocessableEntity, "image-content-mismatch", "The file's content does not match its type.");
        }

        var image = new TicketAttachment
        {
            ArticleId = article.Id,
            FileName = name,
            ContentType = type.ContentType,
            Size = content.LongLength,
            Sha256 = Convert.ToHexStringLower(SHA256.HashData(content)),
            ScanStatus = scanning.Value.Enabled ? AttachmentScanStatus.Pending : AttachmentScanStatus.NotScanned,
            UploadedById = user.Account?.Id,
        };
        db.TicketAttachments.Add(image);
        store.Add(image.Id, content);
        audit.Add(AuditCategory.Change, "knowledge.image.added", "kb_article", article.Id.ToString(),
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["imageId"] = image.Id.ToString(), ["fileName"] = name, ["size"] = image.Size, ["sha256"] = image.Sha256 });
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Created($"/api/v1/knowledge/articles/{article.Id}/images/{image.Id}", ArticleImageResponse.From(image));
    }

    internal static async Task<IResult> ImageAsync(
        Guid id, Guid imageId, HttpContext http, ZelvoDbContext db, CurrentUser user, TenantSettingsStore settings, IAttachmentStore store,
        CancellationToken cancellationToken)
    {
        var article = await db.KbArticles.AsNoTracking().SingleOrDefaultAsync(a => a.Id == id, cancellationToken);
        var publicAccess = await settings.GetAsync(KnowledgeSettings.PublicAccess, cancellationToken);
        if (article is null || !(KnowledgeAccess.CanReadPublished(user, article, publicAccess) || (KnowledgeAccess.IsStaff(user) && KnowledgeEndpoints.MaySee(user, article))))
        {
            return ImageNotFound();
        }

        var image = await db.TicketAttachments.AsNoTracking().SingleOrDefaultAsync(a => a.Id == imageId && a.ArticleId == id, cancellationToken);
        if (image is null)
        {
            return ImageNotFound();
        }

        if (!IncidentAttachmentEndpoints.IsDownloadable(image))
        {
            return ApiProblem.Create(StatusCodes.Status409Conflict, "image-not-available",
                image.ScanStatus == AttachmentScanStatus.Infected ? "The image was rejected: malware was found in it." : "The image is still being checked for malware.");
        }

        var content = await store.ReadAsync(image.Id, cancellationToken);
        if (content is null)
        {
            return ImageNotFound();
        }

        // Shown inline in articles; only raster image types are accepted, and nothing in the response may run.
        http.Response.Headers.ContentSecurityPolicy = "default-src 'none'; sandbox";
        http.Response.Headers.XContentTypeOptions = "nosniff";
        http.Response.Headers.CacheControl = "private, max-age=300";
        return TypedResults.File(content, image.ContentType);
    }

    internal static async Task<IResult> DeleteImageAsync(
        Guid id, Guid imageId, ZelvoDbContext db, CurrentUser user, IAttachmentStore store, IAuditLog audit, CancellationToken cancellationToken)
    {
        var article = await db.KbArticles.AsNoTracking().SingleOrDefaultAsync(a => a.Id == id, cancellationToken);
        var image = article is null || !KnowledgeEndpoints.MaySee(user, article)
            ? null
            : await db.TicketAttachments.SingleOrDefaultAsync(a => a.Id == imageId && a.ArticleId == id, cancellationToken);
        if (image is null)
        {
            return ImageNotFound();
        }

        await store.RemoveAsync(image.Id, cancellationToken);
        db.TicketAttachments.Remove(image);
        audit.Add(AuditCategory.Change, "knowledge.image.removed", "kb_article", id.ToString(),
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["imageId"] = image.Id.ToString(), ["fileName"] = image.FileName });
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.NoContent();
    }

    internal static async Task<IResult> GetSettingsAsync(TenantSettingsStore settings, CancellationToken cancellationToken) =>
        TypedResults.Ok(await KnowledgeSettingsResponse.LoadAsync(settings, cancellationToken));

    internal static async Task<IResult> UpdateSettingsAsync(
        UpdateKnowledgeSettingsRequest request, ZelvoDbContext db, TenantSettingsStore settings, ISearchIndexer indexer, CancellationToken cancellationToken)
    {
        var prefix = request.NumberPrefix?.Trim().ToUpperInvariant();
        if (!KnowledgeSettings.IsPrefix(prefix))
        {
            return UserEndpoints.Validation("numberPrefix", "Use 1–10 capital letters or digits, starting with a letter, for example KB.");
        }

        var publicBefore = await settings.GetAsync(KnowledgeSettings.PublicAccess, cancellationToken);
        await settings.SetAsync(KnowledgeSettings.PublicAccess, request.PublicAccess!.Value, cancellationToken);
        await settings.SetAsync(KnowledgeSettings.NumberPrefix, prefix!, cancellationToken);
        if (publicBefore != request.PublicAccess.Value)
        {
            // External articles change who finds them: visitors, or signed-in users only.
            var audience = await KnowledgeSearch.AudienceAsync(db, settings, cancellationToken) with { PublicAccess = request.PublicAccess.Value };
            var external = await db.KbArticles.Where(a => a.State == ArticleState.Published && a.Visibility == ArticleVisibility.External).ToListAsync(cancellationToken);
            foreach (var article in external)
            {
                await KnowledgeSearch.IndexAsync(db, indexer, audience, article, cancellationToken);
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Ok(await KnowledgeSettingsResponse.LoadAsync(settings, cancellationToken));
    }

    internal static async Task<IReadOnlyList<ArticleImageResponse>> ImagesAsync(ZelvoDbContext db, Guid articleId, CancellationToken cancellationToken)
    {
        var images = await db.TicketAttachments.AsNoTracking().Where(a => a.ArticleId == articleId).OrderBy(a => a.UploadedAt).ToListAsync(cancellationToken).ConfigureAwait(false);
        return [.. images.Select(ArticleImageResponse.From)];
    }

    private static async Task<(KbArticle? Article, KbArticleVersion? Version, IResult? Problem)> ReadableAsync(
        Guid id, ZelvoDbContext db, CurrentUser user, TenantSettingsStore settings, CancellationToken cancellationToken)
    {
        var publicAccess = await settings.GetAsync(KnowledgeSettings.PublicAccess, cancellationToken);
        var article = await db.KbArticles.AsNoTracking().SingleOrDefaultAsync(a => a.Id == id, cancellationToken);
        if (article is { State: ArticleState.Published, Visibility: ArticleVisibility.External } && !user.IsSignedIn && !publicAccess)
        {
            return (null, null, SignInRequired());
        }

        if (article is null || !KnowledgeAccess.CanReadPublished(user, article, publicAccess))
        {
            return (null, null, KnowledgeEndpoints.NotFound());
        }

        var version = await db.KbArticleVersions.AsNoTracking().SingleAsync(v => v.Id == article.PublishedVersionId, cancellationToken);
        return (article, version, null);
    }

    private static async Task<IResult?> SignInProblemAsync(CurrentUser user, TenantSettingsStore settings, CancellationToken cancellationToken) =>
        !user.IsSignedIn && !await settings.GetAsync(KnowledgeSettings.PublicAccess, cancellationToken) ? SignInRequired() : null;

    private static async Task<KbFeedback?> FindFeedbackAsync(
        ZelvoDbContext db, CurrentUser user, ITenantContext tenant, Guid articleId, string? visitorId, CancellationToken cancellationToken, bool tracking = false)
    {
        var set = tracking ? db.KbFeedbackEntries : db.KbFeedbackEntries.AsNoTracking();
        if (user.Account is { } account)
        {
            return await set.SingleOrDefaultAsync(f => f.ArticleId == articleId && f.UserId == account.Id, cancellationToken);
        }

        if (!ViewerKeys.IsVisitorId(visitorId))
        {
            return null;
        }

        var hash = ViewerKeys.ForVisitor(tenant.TenantId!.Value, visitorId!);
        return await set.SingleOrDefaultAsync(f => f.ArticleId == articleId && f.VisitorHash == hash, cancellationToken);
    }

    private static string? ViewerHash(CurrentUser user, ITenantContext tenant, string? visitorId) =>
        user.Account is { } account
            ? ViewerKeys.ForUser(tenant.TenantId!.Value, account.Id)
            : user.ApiClient is { } client
                ? ViewerKeys.ForUser(tenant.TenantId!.Value, client.Id)
                : ViewerKeys.IsVisitorId(visitorId) ? ViewerKeys.ForVisitor(tenant.TenantId!.Value, visitorId!) : null;

    private static IResult SignInRequired() =>
        ApiProblem.Create(StatusCodes.Status401Unauthorized, "sign-in-required", "Sign in to read the knowledge base.");

    private static IResult ImageNotFound() =>
        ApiProblem.Create(StatusCodes.Status404NotFound, "image-not-found", "The image does not exist.");
}

/// <summary>How the published list is sorted.</summary>
public enum PublishedSort
{
    /// <summary>Most viewed first (default).</summary>
    Popular,

    /// <summary>Most recently published first.</summary>
    Newest,
}

/// <summary>Filters of the published list.</summary>
public sealed class PublishedListQuery
{
    /// <summary>Text in the title or summary (either language).</summary>
    [StringLength(200)]
    public string? Text { get; init; }

    /// <summary>Only this category.</summary>
    public Guid? CategoryId { get; init; }

    /// <summary>Staff: only Internal or External. Others always get External.</summary>
    public ArticleVisibility? Visibility { get; init; }

    /// <summary>Popular (default) or Newest.</summary>
    public PublishedSort? Sort { get; init; }
}

/// <summary>A visitor's device id.</summary>
public sealed class VisitorRequest
{
    /// <summary>A random id the browser keeps (visitors who are not signed in only).</summary>
    [StringLength(ViewerKeys.MaxVisitorIdLength)]
    public string? VisitorId { get; init; }
}

/// <summary>A visitor's device id in the query.</summary>
public sealed class VisitorQuery
{
    /// <summary>A random id the browser keeps (visitors who are not signed in only).</summary>
    [StringLength(ViewerKeys.MaxVisitorIdLength)]
    public string? VisitorId { get; init; }
}

/// <summary>A "Was this helpful?" answer.</summary>
public sealed class FeedbackRequest
{
    /// <summary>Yes (true) or no (false).</summary>
    [Required]
    public bool? Helpful { get; init; }

    /// <summary>Optional comment.</summary>
    [StringLength(KbFeedback.CommentMaxLength)]
    public string? Comment { get; init; }

    /// <summary>Visitors who are not signed in: the random device id their browser keeps.</summary>
    [StringLength(ViewerKeys.MaxVisitorIdLength)]
    public string? VisitorId { get; init; }
}

/// <summary>New knowledge settings.</summary>
public sealed class UpdateKnowledgeSettingsRequest
{
    /// <summary>Visitors who are not signed in can read and find external articles.</summary>
    [Required]
    public bool? PublicAccess { get; init; }

    /// <summary>Prefix of new article numbers, for example KB.</summary>
    [Required]
    [StringLength(10, MinimumLength = 1)]
    public string? NumberPrefix { get; init; }
}

/// <summary>Knowledge settings.</summary>
/// <param name="PublicAccess">Visitors who are not signed in can read and find external articles.</param>
/// <param name="NumberPrefix">Prefix of new article numbers.</param>
/// <param name="UpdatedAt">Last change; null while at the defaults.</param>
internal sealed record KnowledgeSettingsResponse(bool PublicAccess, string NumberPrefix, DateTimeOffset? UpdatedAt)
{
    public static async Task<KnowledgeSettingsResponse> LoadAsync(TenantSettingsStore settings, CancellationToken cancellationToken) => new(
        await settings.GetAsync(KnowledgeSettings.PublicAccess, cancellationToken).ConfigureAwait(false),
        await settings.GetAsync(KnowledgeSettings.NumberPrefix, cancellationToken).ConfigureAwait(false),
        await settings.LastChangedAsync(KnowledgeSettings.All, cancellationToken).ConfigureAwait(false));
}

/// <summary>A published article in lists.</summary>
/// <param name="Id">Id.</param>
/// <param name="Number">Number.</param>
/// <param name="Visibility"><c>Internal</c> or <c>External</c>.</param>
/// <param name="CategoryId">Category.</param>
/// <param name="Title">Title in English and Arabic (missing lists the missing ones).</param>
/// <param name="Summary">Summary.</param>
/// <param name="VersionNumber">The published version's number.</param>
/// <param name="ViewCount">Views.</param>
/// <param name="HelpfulCount">"Helpful" answers.</param>
/// <param name="NotHelpfulCount">"Not helpful" answers.</param>
/// <param name="PublishedAt">When this version was published.</param>
internal sealed record PublishedArticleSummary(
    Guid Id, string Number, ArticleVisibility Visibility, Guid? CategoryId, LocalizedTextDto Title, LocalizedTextDto Summary, int VersionNumber,
    long ViewCount, int HelpfulCount, int NotHelpfulCount, DateTimeOffset? PublishedAt)
{
    public static PublishedArticleSummary From(KbArticle a, KbArticleVersion v) => new(
        a.Id, a.Number, a.Visibility, a.CategoryId, LocalizedTextDto.From(v.Title), LocalizedTextDto.From(v.Summary), v.Number,
        a.ViewCount, a.HelpfulCount, a.NotHelpfulCount, v.PublishedAt);
}

/// <summary>A published article to read.</summary>
/// <param name="Article">The article.</param>
/// <param name="Body">Markdown body; images as <c>zelvo-image:&lt;id&gt;</c>.</param>
/// <param name="Keywords">Keywords.</param>
/// <param name="Images">Its images.</param>
internal sealed record PublishedArticleResponse(PublishedArticleSummary Article, LocalizedTextDto Body, IReadOnlyList<string> Keywords, IReadOnlyList<ArticleImageResponse> Images);

/// <summary>An article image.</summary>
/// <param name="Id">Image id, used in Markdown as <c>zelvo-image:&lt;id&gt;</c>.</param>
/// <param name="FileName">File name.</param>
/// <param name="ContentType">Media type.</param>
/// <param name="Size">Size in bytes.</param>
/// <param name="ScanStatus">Pending, Clean, Infected or NotScanned.</param>
/// <param name="Available">Whether it can be shown now.</param>
/// <param name="UploadedAt">When it was added.</param>
internal sealed record ArticleImageResponse(Guid Id, string FileName, string ContentType, long Size, AttachmentScanStatus ScanStatus, bool Available, DateTimeOffset UploadedAt)
{
    public static ArticleImageResponse From(TicketAttachment a) =>
        new(a.Id, a.FileName, a.ContentType, a.Size, a.ScanStatus, IncidentAttachmentEndpoints.IsDownloadable(a), a.UploadedAt);
}

/// <summary>The view count.</summary>
/// <param name="ViewCount">Views.</param>
/// <param name="Counted">This view was counted (the first today from this viewer).</param>
internal sealed record ViewCountResponse(long ViewCount, bool Counted);

/// <summary>The caller's answer and the totals.</summary>
/// <param name="Helpful">The caller's answer, if any.</param>
/// <param name="Comment">The caller's comment.</param>
/// <param name="AnsweredAt">When the caller last answered.</param>
/// <param name="HelpfulCount">"Helpful" answers in total.</param>
/// <param name="NotHelpfulCount">"Not helpful" answers in total.</param>
internal sealed record MyFeedbackResponse(bool? Helpful, string? Comment, DateTimeOffset? AnsweredAt, int HelpfulCount, int NotHelpfulCount);
