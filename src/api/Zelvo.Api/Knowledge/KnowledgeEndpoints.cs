using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;
using Zelvo.Api.Conventions;
using Zelvo.Api.Identity;
using Zelvo.Api.Settings;
using Zelvo.Api.Tenancy;
using Zelvo.Data;
using Zelvo.Data.Auditing;
using Zelvo.Data.Knowledge;
using Zelvo.Data.Localization;
using Zelvo.Data.Tenancy;
using Zelvo.Data.Tickets;
using Zelvo.Integrations;
using Zelvo.Search;
using Zelvo.Tickets;

namespace Zelvo.Api.Knowledge;

/// <summary>
/// Knowledge base for staff (module 7): articles with draft, review, published and retired states,
/// versions, the review queue with the four-eyes rule, feedback, and "turn this ticket into an
/// article". Portal reading is in <see cref="KnowledgePortalEndpoints"/>.
/// </summary>
internal static class KnowledgeEndpoints
{
    public static RouteGroupBuilder MapKnowledgeEndpoints(this RouteGroupBuilder group)
    {
        var knowledge = group.MapGroup("/knowledge").WithTags("Knowledge").RequireTenant();
        var articles = knowledge.MapGroup("/articles");

        articles.MapGet("/", ListAsync)
            .WithName("ListKnowledgeArticles")
            .WithSummary("Articles for staff, newest change first, in every state: filter by state, visibility, category, text in the title, or your own. Needs knowledge.read, knowledge.write or knowledge.publish.")
            .RequireAuthorization()
            .Produces<PageResult<ArticleSummaryResponse>>();

        articles.MapGet("/{id:guid}", GetAsync)
            .WithName("GetKnowledgeArticle")
            .WithSummary("An article with its published version, the version being written or reviewed, what the caller may do, and its images. Returns an ETag to send as If-Match with changes. Needs a knowledge permission.")
            .RequireAuthorization()
            .Produces<ArticleDetailResponse>();

        articles.MapPost("/", CreateAsync)
            .WithName("CreateKnowledgeArticle")
            .WithSummary("Starts an article as a draft: title, summary and Markdown body in English and/or Arabic, keywords, visibility (Internal or External) and category. Gets the next number (KB-000123). Needs knowledge.write; audited.")
            .RequirePermission(Permissions.KnowledgeWrite)
            .Produces<ArticleDetailResponse>(StatusCodes.Status201Created)
            .ProducesValidationProblem();

        articles.MapPut("/{id:guid}/draft", SaveDraftAsync)
            .WithName("SaveKnowledgeDraft")
            .WithSummary("Saves the article's working version. On a published article the first save starts a new version from the published one; readers keep seeing the published version until the new one is approved. Not while it is in review. If-Match is checked when sent. Needs knowledge.write; audited.")
            .RequirePermission(Permissions.KnowledgeWrite)
            .Produces<ArticleDetailResponse>()
            .ProducesValidationProblem();

        articles.MapDelete("/{id:guid}/draft", DiscardDraftAsync)
            .WithName("DiscardKnowledgeDraft")
            .WithSummary("Discards the working version of an article. An article that was never published is deleted with it (and keeps its number unused). Needs knowledge.write; audited.")
            .RequirePermission(Permissions.KnowledgeWrite)
            .Produces(StatusCodes.Status204NoContent);

        articles.MapPost("/{id:guid}/submit", SubmitAsync)
            .WithName("SubmitKnowledgeArticle")
            .WithSummary("Sends the working version for review. It needs a title and a body in at least one language. Needs knowledge.write; audited.")
            .RequirePermission(Permissions.KnowledgeWrite)
            .Produces<ArticleDetailResponse>()
            .ProducesValidationProblem();

        articles.MapPost("/{id:guid}/approve", ApproveAsync)
            .WithName("ApproveKnowledgeArticle")
            .WithSummary("Approves the version in review and publishes it, replacing the previous published version (kept as history). Four-eyes rule: the reviewer may not have created or last changed that version. Needs knowledge.publish; audited.")
            .RequirePermission(Permissions.KnowledgePublish)
            .Produces<ArticleDetailResponse>()
            .ProducesValidationProblem();

        articles.MapPost("/{id:guid}/reject", RejectAsync)
            .WithName("RejectKnowledgeArticle")
            .WithSummary("Sends the version in review back to its author as a draft, with a comment (required). Same four-eyes rule as approving. Needs knowledge.publish; audited.")
            .RequirePermission(Permissions.KnowledgePublish)
            .Produces<ArticleDetailResponse>()
            .ProducesValidationProblem();

        articles.MapPost("/{id:guid}/retire", RetireAsync)
            .WithName("RetireKnowledgeArticle")
            .WithSummary("Retires a published article: readers no longer see or find it; its versions stay as history. Needs knowledge.publish; audited.")
            .RequirePermission(Permissions.KnowledgePublish)
            .Produces<ArticleDetailResponse>();

        articles.MapPost("/{id:guid}/reopen", ReopenAsync)
            .WithName("ReopenKnowledgeArticle")
            .WithSummary("Reopens a retired article as a draft made from its last published version; it is published again only through review. Needs knowledge.write; audited.")
            .RequirePermission(Permissions.KnowledgeWrite)
            .Produces<ArticleDetailResponse>();

        articles.MapGet("/{id:guid}/versions", VersionsAsync)
            .WithName("ListKnowledgeVersions")
            .WithSummary("Every version of an article, newest first, with who wrote, submitted and reviewed it. Needs a knowledge permission.")
            .RequireAuthorization()
            .Produces<IReadOnlyList<VersionResponse>>();

        articles.MapGet("/{id:guid}/feedback", FeedbackAsync)
            .WithName("ListKnowledgeFeedback")
            .WithSummary("\"Was this helpful?\" answers of an article, newest first, with comments. Needs knowledge.read or knowledge.publish.")
            .RequireAuthorization()
            .Produces<PageResult<FeedbackResponse>>();

        knowledge.MapGet("/review-queue", ReviewQueueAsync)
            .WithName("KnowledgeReviewQueue")
            .WithSummary("Articles waiting for review, oldest first, marked with whether the caller may review each (four-eyes rule). Needs knowledge.publish.")
            .RequirePermission(Permissions.KnowledgePublish)
            .Produces<IReadOnlyList<ArticleSummaryResponse>>();

        group.MapPost("/incidents/{id:guid}/knowledge-article", FromIncidentAsync)
            .WithTags("Knowledge")
            .RequireTenant()
            .WithName("CreateKnowledgeArticleFromIncident")
            .WithSummary("\"Turn this ticket into an article\": a draft from a resolved or closed incident's title, description and resolution notes, with personal data replaced by placeholders such as [PERSON_1] for the author to edit; with draftWithAi=true the AI (feature articleDrafts) writes it in English and Arabic from the whole conversation, still a draft for the four-eyes review. Linked to the incident. Needs knowledge.write and incidents.read; audited.")
            .RequirePermission(Permissions.KnowledgeWrite)
            .Produces<ArticleDetailResponse>(StatusCodes.Status201Created);

        group.MapGet("/incidents/{id:guid}/knowledge-articles", ArticlesOfIncidentAsync)
            .WithTags("Knowledge")
            .RequireTenant()
            .WithName("ListKnowledgeArticlesOfIncident")
            .WithSummary("Articles made from an incident. Needs incidents.read and a knowledge permission.")
            .RequirePermission(Permissions.IncidentsRead)
            .Produces<IReadOnlyList<ArticleSummaryResponse>>();

        return group;
    }

    internal static async Task<IResult> ListAsync(
        [AsParameters] PageQuery page, [AsParameters] ArticleListQuery filter, ZelvoDbContext db, CurrentUser user, CancellationToken cancellationToken)
    {
        if (!KnowledgeAccess.IsStaff(user))
        {
            return Forbidden();
        }

        var query = db.KbArticles.AsNoTracking();
        if (filter.State is { } state) query = query.Where(a => a.State == state);
        if (filter.Visibility is { } visibility) query = query.Where(a => a.Visibility == visibility);
        if (filter.CategoryId is { } category) query = query.Where(a => a.CategoryId == category);
        if (filter.Mine == true && user.Account is { } me) query = query.Where(a => a.CreatedById == me.Id);
        if (!user.Has(Permissions.KnowledgeRead) && !user.Has(Permissions.KnowledgePublish))
        {
            // Writers without read see published external articles and their own work.
            var mine = user.Account?.Id;
            query = query.Where(a => a.CreatedById == mine || (a.State == ArticleState.Published && a.Visibility == ArticleVisibility.External));
        }

        if (filter.Text is { Length: > 0 } text)
        {
            var pattern = "%" + text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal) + "%";
            var number = text.Trim().ToUpperInvariant();
            var matching = db.KbArticleVersions.Where(v => EF.Functions.ILike(v.Title.En ?? string.Empty, pattern) || EF.Functions.ILike(v.Title.Ar ?? string.Empty, pattern)).Select(v => v.Id);
            query = query.Where(a => a.Number == number || matching.Contains(a.PublishedVersionId ?? Guid.Empty) || matching.Contains(a.WorkingVersionId ?? Guid.Empty));
        }

        query = query.OrderByDescending(a => a.UpdatedAt).ThenByDescending(a => a.Id);
        var total = await query.LongCountAsync(cancellationToken);
        var items = await query.Skip(page.Skip).Take(page.Size).ToListAsync(cancellationToken);
        return TypedResults.Ok(new PageResult<ArticleSummaryResponse>(await SummariesAsync(db, user, items, cancellationToken), page.Number, page.Size, total));
    }

    internal static async Task<IResult> GetAsync(Guid id, HttpContext http, ZelvoDbContext db, CurrentUser user, CancellationToken cancellationToken)
    {
        if (!KnowledgeAccess.IsStaff(user))
        {
            return Forbidden();
        }

        var article = await db.KbArticles.AsNoTracking().SingleOrDefaultAsync(a => a.Id == id, cancellationToken);
        return article is null || !MaySee(user, article) ? NotFound() : TypedResults.Ok(await DetailAsync(http, db, user, article, cancellationToken));
    }

    internal static async Task<IResult> CreateAsync(
        ArticleContentRequest request, HttpContext http, ZelvoDbContext db, CurrentUser user, ITenantContext tenant, TenantSettingsStore settings,
        ISearchIndexer indexer, IAuditLog audit, CancellationToken cancellationToken)
    {
        if (await ContentProblemAsync(db, request, cancellationToken) is { } problem)
        {
            return problem;
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var article = await NewArticleAsync(db, tenant, settings, user, request, null, cancellationToken);
        audit.Add(AuditCategory.Create, "knowledge.article.created", "kb_article", article.Id.ToString());
        var result = await SaveAsync(http, db, user, settings, indexer, article, cancellationToken, created: true);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    internal static async Task<IResult> SaveDraftAsync(
        Guid id, ArticleContentRequest request, HttpContext http, ZelvoDbContext db, CurrentUser user, TenantSettingsStore settings,
        ISearchIndexer indexer, CancellationToken cancellationToken)
    {
        var article = await db.KbArticles.SingleOrDefaultAsync(a => a.Id == id, cancellationToken);
        if (article is null || !MaySee(user, article))
        {
            return NotFound();
        }

        if (await VersionProblemAsync(http, db, article, cancellationToken) is { } versionProblem)
        {
            return versionProblem;
        }

        if (await ContentProblemAsync(db, request, cancellationToken) is { } problem)
        {
            return problem;
        }

        var working = await WorkingAsync(db, article, cancellationToken);
        if (working is { State: ArticleVersionState.InReview })
        {
            return Conflict("article-in-review", "The article is in review. A reviewer approves it or sends it back before it can change.");
        }

        if (working is null)
        {
            // A change to a published article starts its next version; the request has the whole content.
            working = new KbArticleVersion
            {
                ArticleId = article.Id,
                Number = await NextVersionAsync(db, article.Id, cancellationToken),
                Title = request.Title!.ToValue(),
                CreatedById = user.Account?.Id,
            };
            db.KbArticleVersions.Add(working);
            article.WorkingVersionId = working.Id;
        }

        Apply(request, working, user);
        if (article.PublishedVersionId is null)
        {
            article.Visibility = working.Visibility;
            article.CategoryId = working.CategoryId;
            article.State = ArticleState.Draft;
        }

        article.UpdatedAt = DateTimeOffset.UtcNow;
        return await SaveAsync(http, db, user, settings, indexer, article, cancellationToken);
    }

    internal static async Task<IResult> DiscardDraftAsync(
        Guid id, ZelvoDbContext db, CurrentUser user, TenantSettingsStore settings, ISearchIndexer indexer, IAuditLog audit, CancellationToken cancellationToken)
    {
        var article = await db.KbArticles.SingleOrDefaultAsync(a => a.Id == id, cancellationToken);
        if (article is null || !MaySee(user, article))
        {
            return NotFound();
        }

        var working = await WorkingAsync(db, article, cancellationToken);
        if (working is null)
        {
            return Conflict("no-draft", "The article has no draft to discard.");
        }

        if (working.CreatedById != user.Account?.Id && !user.Has(Permissions.KnowledgePublish))
        {
            return Forbidden("Only the draft's author or a publisher can discard it.");
        }

        if (article.PublishedVersionId is null && article.State != ArticleState.Retired)
        {
            // Never published: the whole article goes (versions and images with it).
            db.KbArticles.Remove(article);
            indexer.Delete(KnowledgeSearch.Type, article.Id.ToString());
            audit.Add(AuditCategory.Delete, "knowledge.article.deleted", "kb_article", article.Id.ToString());
            await db.SaveChangesAsync(cancellationToken);
            return TypedResults.NoContent();
        }

        article.WorkingVersionId = null;
        db.KbArticleVersions.Remove(working);
        article.UpdatedAt = DateTimeOffset.UtcNow;
        audit.Add(AuditCategory.Delete, "knowledge.draft.discarded", "kb_article", article.Id.ToString(),
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["version"] = working.Number });
        await KnowledgeSearch.IndexAsync(db, indexer, await KnowledgeSearch.AudienceAsync(db, settings, cancellationToken), article, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.NoContent();
    }

    internal static async Task<IResult> SubmitAsync(
        Guid id, HttpContext http, ZelvoDbContext db, CurrentUser user, TenantSettingsStore settings, ISearchIndexer indexer, IAuditLog audit,
        CancellationToken cancellationToken)
    {
        var article = await db.KbArticles.SingleOrDefaultAsync(a => a.Id == id, cancellationToken);
        if (article is null || !MaySee(user, article))
        {
            return NotFound();
        }

        if (await VersionProblemAsync(http, db, article, cancellationToken) is { } versionProblem)
        {
            return versionProblem;
        }

        var working = await WorkingAsync(db, article, cancellationToken);
        if (working is not { State: ArticleVersionState.Draft })
        {
            return Conflict("no-draft", "There is no draft to submit.");
        }

        if (!working.Title.HasAnyValue || !working.Body.HasAnyValue)
        {
            return UserEndpoints.Validation("body", "Write a title and the article in English or Arabic before submitting it.");
        }

        working.State = ArticleVersionState.InReview;
        working.SubmittedById = user.Account?.Id;
        working.SubmittedAt = DateTimeOffset.UtcNow;
        working.ReviewComment = null;
        if (article.PublishedVersionId is null)
        {
            article.State = ArticleState.InReview;
        }

        article.UpdatedAt = DateTimeOffset.UtcNow;
        audit.Add(AuditCategory.Change, "knowledge.article.submitted", "kb_article", article.Id.ToString(),
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["version"] = working.Number });
        return await SaveAsync(http, db, user, settings, indexer, article, cancellationToken);
    }

    internal static async Task<IResult> ApproveAsync(
        Guid id, ReviewRequest? request, HttpContext http, ZelvoDbContext db, CurrentUser user, TenantSettingsStore settings, ISearchIndexer indexer,
        IAuditLog audit, CancellationToken cancellationToken)
    {
        var (article, working, problem) = await InReviewAsync(id, http, db, user, cancellationToken);
        if (problem is not null)
        {
            return problem;
        }

        var now = DateTimeOffset.UtcNow;
        if (article!.PublishedVersionId is { } previousId)
        {
            var previous = await db.KbArticleVersions.SingleAsync(v => v.Id == previousId, cancellationToken);
            previous.State = ArticleVersionState.Superseded;
            previous.UpdatedAt = now;
        }

        working!.State = ArticleVersionState.Published;
        working.ReviewedById = user.Account!.Id;
        working.ReviewedAt = now;
        working.ReviewComment = Note(request?.Comment);
        working.PublishedAt = now;
        working.UpdatedAt = now;
        article.PublishedVersionId = working.Id;
        article.WorkingVersionId = null;
        article.State = ArticleState.Published;
        article.Visibility = working.Visibility;
        article.CategoryId = working.CategoryId;
        article.PublishedAt = now;
        article.RetiredAt = null;
        article.UpdatedAt = now;
        audit.Add(AuditCategory.Change, "knowledge.article.published", "kb_article", article.Id.ToString(),
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["version"] = working.Number, ["visibility"] = working.Visibility.ToString() });
        return await SaveAsync(http, db, user, settings, indexer, article, cancellationToken);
    }

    internal static async Task<IResult> RejectAsync(
        Guid id, ReviewRequest? request, HttpContext http, ZelvoDbContext db, CurrentUser user, TenantSettingsStore settings, ISearchIndexer indexer,
        IAuditLog audit, CancellationToken cancellationToken)
    {
        var comment = Note(request?.Comment);
        if (comment is null)
        {
            return UserEndpoints.Validation("comment", "Say what the author should change.");
        }

        var (article, working, problem) = await InReviewAsync(id, http, db, user, cancellationToken);
        if (problem is not null)
        {
            return problem;
        }

        working!.State = ArticleVersionState.Draft;
        working.ReviewedById = user.Account!.Id;
        working.ReviewedAt = DateTimeOffset.UtcNow;
        working.ReviewComment = comment;
        working.UpdatedAt = DateTimeOffset.UtcNow;
        if (article!.PublishedVersionId is null)
        {
            article.State = ArticleState.Draft;
        }

        article.UpdatedAt = DateTimeOffset.UtcNow;
        audit.Add(AuditCategory.Change, "knowledge.article.sent-back", "kb_article", article.Id.ToString(),
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["version"] = working.Number });
        return await SaveAsync(http, db, user, settings, indexer, article, cancellationToken);
    }

    internal static async Task<IResult> RetireAsync(
        Guid id, HttpContext http, ZelvoDbContext db, CurrentUser user, TenantSettingsStore settings, ISearchIndexer indexer, IAuditLog audit,
        CancellationToken cancellationToken)
    {
        var article = await db.KbArticles.SingleOrDefaultAsync(a => a.Id == id, cancellationToken);
        if (article is null || !MaySee(user, article))
        {
            return NotFound();
        }

        if (await VersionProblemAsync(http, db, article, cancellationToken) is { } versionProblem)
        {
            return versionProblem;
        }

        if (article.State != ArticleState.Published || article.PublishedVersionId is not { } publishedId)
        {
            return Conflict("not-published", "Only published articles can be retired; discard a draft instead.");
        }

        var published = await db.KbArticleVersions.SingleAsync(v => v.Id == publishedId, cancellationToken);
        published.State = ArticleVersionState.Retired;
        published.UpdatedAt = DateTimeOffset.UtcNow;
        if (await WorkingAsync(db, article, cancellationToken) is { } working)
        {
            db.KbArticleVersions.Remove(working);
            article.WorkingVersionId = null;
        }

        article.PublishedVersionId = null;
        article.State = ArticleState.Retired;
        article.RetiredAt = DateTimeOffset.UtcNow;
        article.UpdatedAt = DateTimeOffset.UtcNow;
        audit.Add(AuditCategory.Change, "knowledge.article.retired", "kb_article", article.Id.ToString());
        return await SaveAsync(http, db, user, settings, indexer, article, cancellationToken);
    }

    internal static async Task<IResult> ReopenAsync(
        Guid id, HttpContext http, ZelvoDbContext db, CurrentUser user, TenantSettingsStore settings, ISearchIndexer indexer, IAuditLog audit,
        CancellationToken cancellationToken)
    {
        var article = await db.KbArticles.SingleOrDefaultAsync(a => a.Id == id, cancellationToken);
        if (article is null || !MaySee(user, article))
        {
            return NotFound();
        }

        if (article.State != ArticleState.Retired)
        {
            return Conflict("not-retired", "Only retired articles can be reopened.");
        }

        var last = await db.KbArticleVersions.AsNoTracking().Where(v => v.ArticleId == article.Id && v.State == ArticleVersionState.Retired)
            .OrderByDescending(v => v.Number).FirstAsync(cancellationToken);
        var draft = new KbArticleVersion
        {
            ArticleId = article.Id,
            Number = await NextVersionAsync(db, article.Id, cancellationToken),
            Title = last.Title,
            Summary = last.Summary,
            Body = last.Body,
            Keywords = last.Keywords,
            Visibility = last.Visibility,
            CategoryId = last.CategoryId,
            ChangeNote = null,
            CreatedById = user.Account?.Id,
            LastEditedById = user.Account?.Id,
        };
        db.KbArticleVersions.Add(draft);
        article.WorkingVersionId = draft.Id;
        article.State = ArticleState.Draft;
        article.UpdatedAt = DateTimeOffset.UtcNow;
        audit.Add(AuditCategory.Change, "knowledge.article.reopened", "kb_article", article.Id.ToString());
        return await SaveAsync(http, db, user, settings, indexer, article, cancellationToken);
    }

    internal static async Task<IResult> VersionsAsync(Guid id, ZelvoDbContext db, CurrentUser user, CancellationToken cancellationToken)
    {
        var article = await db.KbArticles.AsNoTracking().SingleOrDefaultAsync(a => a.Id == id, cancellationToken);
        if (article is null || !KnowledgeAccess.IsStaff(user) || !MaySee(user, article))
        {
            return NotFound();
        }

        var versions = await db.KbArticleVersions.AsNoTracking().Where(v => v.ArticleId == id).OrderByDescending(v => v.Number).ToListAsync(cancellationToken);
        var names = await NamesAsync(db, versions, cancellationToken);
        return TypedResults.Ok(versions.Select(v => VersionResponse.From(v, names)).ToList());
    }

    internal static async Task<IResult> FeedbackAsync(
        Guid id, [AsParameters] PageQuery page, ZelvoDbContext db, CurrentUser user, CancellationToken cancellationToken)
    {
        if (!user.Has(Permissions.KnowledgeRead) && !user.Has(Permissions.KnowledgePublish))
        {
            return Forbidden();
        }

        if (!await db.KbArticles.AnyAsync(a => a.Id == id, cancellationToken))
        {
            return NotFound();
        }

        var query = db.KbFeedbackEntries.AsNoTracking().Where(f => f.ArticleId == id).OrderByDescending(f => f.UpdatedAt).ThenByDescending(f => f.Id);
        var total = await query.LongCountAsync(cancellationToken);
        var items = await query.Skip(page.Skip).Take(page.Size).ToListAsync(cancellationToken);
        var userIds = items.Where(f => f.UserId is not null).Select(f => f.UserId!.Value).Distinct().ToList();
        var names = await db.Users.AsNoTracking().Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, cancellationToken);
        var versionIds = items.Where(f => f.VersionId is not null).Select(f => f.VersionId!.Value).Distinct().ToList();
        var numbers = await db.KbArticleVersions.AsNoTracking().Where(v => versionIds.Contains(v.Id)).ToDictionaryAsync(v => v.Id, v => v.Number, cancellationToken);
        return TypedResults.Ok(new PageResult<FeedbackResponse>([.. items.Select(f => new FeedbackResponse(
            f.Id, f.Helpful, f.Comment, f.UserId is { } u ? names.GetValueOrDefault(u) : null, f.UserId is null,
            f.VersionId is { } v ? numbers.GetValueOrDefault(v) : null, f.CreatedAt, f.UpdatedAt))], page.Number, page.Size, total));
    }

    internal static async Task<IResult> ReviewQueueAsync(ZelvoDbContext db, CurrentUser user, CancellationToken cancellationToken)
    {
        var inReview = await db.KbArticleVersions.AsNoTracking().Where(v => v.State == ArticleVersionState.InReview).Select(v => v.ArticleId).ToListAsync(cancellationToken);
        var articles = await db.KbArticles.AsNoTracking().Where(a => inReview.Contains(a.Id)).ToListAsync(cancellationToken);
        var summaries = await SummariesAsync(db, user, articles, cancellationToken);
        return TypedResults.Ok(summaries.OrderBy(s => s.SubmittedAt).ToList());
    }

    internal static async Task<IResult> FromIncidentAsync(
        Guid id, bool? draftWithAi, HttpContext http, ZelvoDbContext db, CurrentUser user, ITenantContext tenant, TenantSettingsStore settings, ISearchIndexer indexer,
        IAuditLog audit, CancellationToken cancellationToken)
    {
        if (!user.Has(Permissions.IncidentsRead))
        {
            return Forbidden("You need to see incidents to turn one into an article.");
        }

        var ticket = await db.Tickets.AsNoTracking().Include(t => t.Requester).Include(t => t.Assignee)
            .SingleOrDefaultAsync(t => t.Id == id && t.Type == TicketType.Incident, cancellationToken);
        if (ticket is null)
        {
            return ApiProblem.Create(StatusCodes.Status404NotFound, "incident-not-found", "The incident does not exist.");
        }

        if (ticket.State is not (TicketState.Resolved or TicketState.Closed))
        {
            return Conflict("incident-not-resolved", "Only resolved or closed incidents can be turned into an article.");
        }

        var request = FromTicket(ticket);
        long? aiCallId = null;
        if (draftWithAi == true)
        {
            // Task E5: the AI writes the draft in both languages; it is still a draft for the four-eyes review.
            var drafted = await http.RequestServices.GetRequiredService<Zelvo.Ai.Features.AgentAssist>().DraftArticleAsync(ticket.Id, cancellationToken);
            if (drafted.Value is not { } draft)
            {
                return Zelvo.Api.Ai.AiFeatureEndpoints.AiProblem(drafted.ErrorCode, drafted.Error);
            }

            aiCallId = draft.CallId;
            request = new ArticleContentRequest
            {
                Title = new LocalizedTextInput { En = draft.Title.En, Ar = draft.Title.Ar },
                Summary = new LocalizedTextInput { En = draft.Summary.En, Ar = draft.Summary.Ar },
                Body = new ArticleTextInput { En = draft.Body.En, Ar = draft.Body.Ar },
                Keywords = draft.Keywords,
                Visibility = ArticleVisibility.Internal,
                CategoryId = ticket.CategoryId,
                ChangeNote = $"Drafted by AI from {ticket.Number}.",
            };
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var article = await NewArticleAsync(db, tenant, settings, user, request, ticket.Id, cancellationToken);
        audit.Add(AuditCategory.Create, "knowledge.article.created-from-ticket", "kb_article", article.Id.ToString(),
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["ticketId"] = ticket.Id.ToString(), ["ticket"] = ticket.Number, ["aiCallId"] = aiCallId });
        var result = await SaveAsync(http, db, user, settings, indexer, article, cancellationToken, created: true);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    internal static async Task<IResult> ArticlesOfIncidentAsync(Guid id, ZelvoDbContext db, CurrentUser user, CancellationToken cancellationToken)
    {
        if (!KnowledgeAccess.IsStaff(user))
        {
            return TypedResults.Ok(Array.Empty<ArticleSummaryResponse>());
        }

        var articles = await db.KbArticles.AsNoTracking().Where(a => a.SourceTicketId == id).OrderBy(a => a.Sequence).ToListAsync(cancellationToken);
        return TypedResults.Ok(await SummariesAsync(db, user, [.. articles.Where(a => MaySee(user, a))], cancellationToken));
    }

    /// <summary>
    /// The draft "turn this ticket into an article" starts from. Personal data (names, email
    /// addresses, phone numbers, Emirates IDs…) is replaced by placeholders: the article is for
    /// everyone, the ticket was about one person.
    /// </summary>
    internal static ArticleContentRequest FromTicket(Ticket ticket)
    {
        var pseudonyms = new PersonalDataPseudonymizer();
        foreach (var person in new[] { ticket.Requester, ticket.Assignee })
        {
            pseudonyms.AddKnownValue(person?.DisplayName);
            pseudonyms.AddKnownValue(person?.Email, "EMAIL");
        }

        var title = pseudonyms.Mask(ticket.Title);
        var arabic = ContainsArabic(ticket.Title + ticket.Description + ticket.ResolutionNotes);
        var body = string.Join("\n\n", new[]
        {
            arabic ? "## المشكلة" : "## Problem",
            ticket.Description is { Length: > 0 } d ? pseudonyms.Mask(d) : arabic ? "_صف المشكلة._" : "_Describe the problem._",
            arabic ? "## الحل" : "## Solution",
            ticket.ResolutionNotes is { Length: > 0 } n ? pseudonyms.Mask(n) : arabic ? "_صف الحل._" : "_Describe the solution._",
        });
        var titleInput = arabic ? new LocalizedTextInput { Ar = Cut(title, KbArticleVersion.TitleMaxLength) } : new LocalizedTextInput { En = Cut(title, KbArticleVersion.TitleMaxLength) };
        return new ArticleContentRequest
        {
            Title = titleInput,
            Body = arabic ? new ArticleTextInput { Ar = body } : new ArticleTextInput { En = body },
            Visibility = ArticleVisibility.Internal,
            CategoryId = ticket.CategoryId,
            ChangeNote = $"From {ticket.Number}.",
        };
    }

    private static bool ContainsArabic(string? text) => text?.Any(c => c is >= '؀' and <= 'ۿ') ?? false;

    private static string Cut(string value, int max) => value.Length <= max ? value : value[..max];

    internal static async Task<KbArticle> NewArticleAsync(
        ZelvoDbContext db, ITenantContext tenant, TenantSettingsStore settings, CurrentUser user, ArticleContentRequest request, Guid? sourceTicketId,
        CancellationToken cancellationToken)
    {
        var prefix = await settings.GetAsync(KnowledgeSettings.NumberPrefix, cancellationToken);

        // Numbers are taken under a transaction-level advisory lock per tenant (the caller's
        // transaction), so they are unique; published articles are retired, never deleted.
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtext({"kb_articles:" + tenant.TenantId}))", cancellationToken);
        var sequence = (await db.KbArticles.MaxAsync(a => (long?)a.Sequence, cancellationToken) ?? 0) + 1;
        var article = new KbArticle
        {
            Number = TicketNumbers.Format(prefix, sequence),
            Sequence = sequence,
            CreatedById = user.Account?.Id,
            SourceTicketId = sourceTicketId,
            Visibility = request.Visibility ?? ArticleVisibility.Internal,
            CategoryId = request.CategoryId,
        };
        var version = new KbArticleVersion
        {
            ArticleId = article.Id,
            Number = 1,
            Title = request.Title!.ToValue(),
            CreatedById = user.Account?.Id,
        };
        Apply(request, version, user);
        article.WorkingVersionId = version.Id;
        db.KbArticles.Add(article);
        db.KbArticleVersions.Add(version);
        return article;
    }

    private static void Apply(ArticleContentRequest request, KbArticleVersion version, CurrentUser user)
    {
        version.Title = request.Title!.ToValue();
        version.Summary = request.Summary?.ToValue() ?? new LocalizedText(null, null);
        version.Body = request.Body?.ToValue() ?? new LocalizedText(null, null);
        version.Keywords = [.. (request.Keywords ?? []).Select(k => k.Trim()).Where(k => k.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)];
        version.Visibility = request.Visibility ?? version.Visibility;
        version.CategoryId = request.CategoryId;
        version.ChangeNote = Note(request.ChangeNote);
        version.LastEditedById = user.Account?.Id;
        version.UpdatedAt = DateTimeOffset.UtcNow;
    }

    private static async Task<IResult?> ContentProblemAsync(ZelvoDbContext db, ArticleContentRequest request, CancellationToken cancellationToken)
    {
        if (request.Keywords is { } keywords && (keywords.Count > KbArticleVersion.MaxKeywords || keywords.Any(k => k is null || k.Trim().Length > KbArticleVersion.KeywordMaxLength)))
        {
            return UserEndpoints.Validation("keywords", $"Give at most {KbArticleVersion.MaxKeywords} keywords of up to {KbArticleVersion.KeywordMaxLength} characters.");
        }

        if (request.CategoryId is { } categoryId && !await db.Categories.AnyAsync(c => c.Id == categoryId && c.Active, cancellationToken))
        {
            return UserEndpoints.Validation("categoryId", "Choose an active category.");
        }

        return null;
    }

    private static async Task<(KbArticle? Article, KbArticleVersion? Working, IResult? Problem)> InReviewAsync(
        Guid id, HttpContext http, ZelvoDbContext db, CurrentUser user, CancellationToken cancellationToken)
    {
        var article = await db.KbArticles.SingleOrDefaultAsync(a => a.Id == id, cancellationToken);
        if (article is null || !MaySee(user, article))
        {
            return (null, null, NotFound());
        }

        if (await VersionProblemAsync(http, db, article, cancellationToken) is { } versionProblem)
        {
            return (null, null, versionProblem);
        }

        var working = await WorkingAsync(db, article, cancellationToken);
        if (working is not { State: ArticleVersionState.InReview })
        {
            return (null, null, Conflict("not-in-review", "The article is not waiting for review."));
        }

        if (!KnowledgeAccess.MayReview(user, working))
        {
            return (null, null, ApiProblem.Create(StatusCodes.Status403Forbidden, "own-article",
                "Someone other than the author reviews an article (four-eyes rule). Ask another publisher."));
        }

        return (article, working, null);
    }

    private static Task<KbArticleVersion?> WorkingAsync(ZelvoDbContext db, KbArticle article, CancellationToken cancellationToken) =>
        article.WorkingVersionId is { } workingId
            ? db.KbArticleVersions.SingleOrDefaultAsync(v => v.Id == workingId, cancellationToken)
            : Task.FromResult<KbArticleVersion?>(null);

    private static async Task<int> NextVersionAsync(ZelvoDbContext db, Guid articleId, CancellationToken cancellationToken) =>
        (await db.KbArticleVersions.Where(v => v.ArticleId == articleId).MaxAsync(v => (int?)v.Number, cancellationToken) ?? 0) + 1;

    /// <summary>
    /// Staff with read or publish see everything; writers without read see their own articles and
    /// published external ones.
    /// </summary>
    internal static bool MaySee(CurrentUser user, KbArticle article) =>
        user.Has(Permissions.KnowledgeRead) || user.Has(Permissions.KnowledgePublish)
        || (user.Has(Permissions.KnowledgeWrite) && (article.CreatedById == user.Account?.Id
            || article is { State: ArticleState.Published, Visibility: ArticleVisibility.External }));

    /// <summary>
    /// Checks If-Match (when sent) against the version being changed: the working version, or the
    /// published one when there is none. Not against the article row, whose view and feedback
    /// counters change all the time.
    /// </summary>
    private static async Task<IResult?> VersionProblemAsync(HttpContext http, ZelvoDbContext db, KbArticle article, CancellationToken cancellationToken)
    {
        var ifMatch = http.Request.Headers.IfMatch.ToString();
        if (string.IsNullOrEmpty(ifMatch))
        {
            return null;
        }

        var targetId = article.WorkingVersionId ?? article.PublishedVersionId;
        var target = targetId is { } id ? await db.KbArticleVersions.SingleOrDefaultAsync(v => v.Id == id, cancellationToken) : null;
        if (target is null || !uint.TryParse(ifMatch.Trim().Replace("W/", "", StringComparison.Ordinal).Trim('"'), out var version) || version != target.Version)
        {
            return VersionMismatch();
        }

        db.Entry(target).Property(v => v.Version).OriginalValue = version;

        // Saving the article must not fail because a counter changed meanwhile.
        return null;
    }

    /// <summary>The ETag of an article: the version that changes next.</summary>
    internal static string ETagOf(KbArticleVersion? working, KbArticleVersion? published) =>
        $"\"{(working ?? published)?.Version ?? 0}\"";

    internal static async Task<IResult> SaveAsync(
        HttpContext http, ZelvoDbContext db, CurrentUser user, TenantSettingsStore settings, ISearchIndexer indexer, KbArticle article,
        CancellationToken cancellationToken, bool created = false)
    {
        await KnowledgeSearch.IndexAsync(db, indexer, await KnowledgeSearch.AudienceAsync(db, settings, cancellationToken), article, cancellationToken);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return VersionMismatch();
        }

        var saved = await db.KbArticles.AsNoTracking().SingleAsync(a => a.Id == article.Id, cancellationToken);
        var detail = await DetailAsync(http, db, user, saved, cancellationToken);
        return created ? TypedResults.Created($"/api/v1/knowledge/articles/{article.Id}", detail) : TypedResults.Ok(detail);
    }

    internal static async Task<ArticleDetailResponse> DetailAsync(HttpContext http, ZelvoDbContext db, CurrentUser user, KbArticle article, CancellationToken cancellationToken)
    {
        var ids = new[] { article.PublishedVersionId, article.WorkingVersionId }.Where(i => i is not null).Select(i => i!.Value).ToList();
        var versions = await db.KbArticleVersions.AsNoTracking().Where(v => ids.Contains(v.Id)).ToListAsync(cancellationToken).ConfigureAwait(false);
        var published = versions.FirstOrDefault(v => v.Id == article.PublishedVersionId);
        var working = versions.FirstOrDefault(v => v.Id == article.WorkingVersionId);
        var names = await NamesAsync(db, versions, cancellationToken).ConfigureAwait(false);
        var source = article.SourceTicketId is { } ticketId
            ? await db.Tickets.AsNoTracking().Where(t => t.Id == ticketId).Select(t => new SourceTicketResponse(t.Id, t.Number)).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false)
            : null;
        var images = await KnowledgePortalEndpoints.ImagesAsync(db, article.Id, cancellationToken).ConfigureAwait(false);
        var me = user.Account?.Id;
        var mayWrite = user.Has(Permissions.KnowledgeWrite) && MaySee(user, article);
        var actions = new ArticleActions(
            Edit: mayWrite && working?.State != ArticleVersionState.InReview && article.State != ArticleState.Retired,
            Submit: mayWrite && working?.State == ArticleVersionState.Draft,
            Review: working is { State: ArticleVersionState.InReview } && KnowledgeAccess.MayReview(user, working),
            Retire: user.Has(Permissions.KnowledgePublish) && article.State == ArticleState.Published,
            Reopen: mayWrite && article.State == ArticleState.Retired,
            Discard: working is not null && (working.CreatedById == me && me is not null || user.Has(Permissions.KnowledgePublish)));
        http.Response.Headers.ETag = ETagOf(working, published);
        return new ArticleDetailResponse(
            Summary(article, published, working),
            published is null ? null : VersionResponse.From(published, names),
            working is null ? null : VersionResponse.From(working, names),
            source,
            images,
            actions);
    }

    private static async Task<List<ArticleSummaryResponse>> SummariesAsync(ZelvoDbContext db, CurrentUser user, IReadOnlyList<KbArticle> articles, CancellationToken cancellationToken)
    {
        var ids = articles.SelectMany(a => new[] { a.PublishedVersionId, a.WorkingVersionId }).Where(i => i is not null).Select(i => i!.Value).ToList();
        var versions = await db.KbArticleVersions.AsNoTracking().Where(v => ids.Contains(v.Id)).ToDictionaryAsync(v => v.Id, cancellationToken).ConfigureAwait(false);
        return [.. articles.Select(a =>
        {
            var working = a.WorkingVersionId is { } w ? versions.GetValueOrDefault(w) : null;
            var summary = Summary(a, a.PublishedVersionId is { } p ? versions.GetValueOrDefault(p) : null, working);
            return summary with { CanReview = working is { State: ArticleVersionState.InReview } && KnowledgeAccess.MayReview(user, working) };
        })];
    }

    private static ArticleSummaryResponse Summary(KbArticle article, KbArticleVersion? published, KbArticleVersion? working)
    {
        var shown = published ?? working;
        return new ArticleSummaryResponse(
            article.Id, article.Number, article.State, article.Visibility, article.CategoryId,
            LocalizedTextDto.From(shown?.Title ?? new LocalizedText(null, null)),
            LocalizedTextDto.From(shown?.Summary ?? new LocalizedText(null, null)),
            working?.State, working?.SubmittedAt, working?.ReviewComment, false,
            article.ViewCount, article.HelpfulCount, article.NotHelpfulCount,
            article.CreatedById, article.SourceTicketId, article.PublishedAt, article.RetiredAt, article.CreatedAt, article.UpdatedAt);
    }

    private static async Task<Dictionary<Guid, string?>> NamesAsync(ZelvoDbContext db, IEnumerable<KbArticleVersion> versions, CancellationToken cancellationToken)
    {
        var ids = versions.SelectMany(v => new[] { v.CreatedById, v.LastEditedById, v.SubmittedById, v.ReviewedById })
            .Where(i => i is not null).Select(i => i!.Value).Distinct().ToList();
        return await db.Users.AsNoTracking().Where(u => ids.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, cancellationToken).ConfigureAwait(false);
    }

    private static string? Note(string? note) => note?.Trim() is { Length: > 0 } trimmed ? trimmed : null;

    internal static IResult NotFound() =>
        ApiProblem.Create(StatusCodes.Status404NotFound, "article-not-found", "The article does not exist.");

    private static IResult Forbidden(string message = "You need a knowledge permission for this.") =>
        ApiProblem.Create(StatusCodes.Status403Forbidden, "forbidden", message);

    private static IResult Conflict(string code, string message) => ApiProblem.Create(StatusCodes.Status409Conflict, code, message);

    private static IResult VersionMismatch() =>
        ApiProblem.Create(StatusCodes.Status412PreconditionFailed, "version-mismatch", "The article changed since you loaded it. Load it again and repeat your change.");
}

/// <summary>Article text in English and Arabic, longer than <see cref="LocalizedTextInput"/> allows.</summary>
public sealed class ArticleTextInput
{
    /// <summary>English (Markdown).</summary>
    [StringLength(KbArticleVersion.BodyMaxLength)]
    public string? En { get; init; }

    /// <summary>Arabic (Markdown).</summary>
    [StringLength(KbArticleVersion.BodyMaxLength)]
    public string? Ar { get; init; }

    /// <summary>As a bilingual value.</summary>
    public LocalizedText ToValue() => new(En, Ar);
}

/// <summary>Content of an article version.</summary>
public sealed class ArticleContentRequest : IValidatableObject
{
    /// <summary>Title in English and/or Arabic, up to 200 characters each.</summary>
    [Required]
    public LocalizedTextInput? Title { get; init; }

    /// <summary>Short summary for lists and search results, up to 500 characters each.</summary>
    public LocalizedTextInput? Summary { get; init; }

    /// <summary>The article in Markdown (headings, lists, links, tables, code, images as <c>![text](zelvo-image:&lt;id&gt;)</c>); raw HTML is shown as text.</summary>
    public ArticleTextInput? Body { get; init; }

    /// <summary>Search keywords.</summary>
    [MaxLength(KbArticleVersion.MaxKeywords)]
    public IReadOnlyList<string>? Keywords { get; init; }

    /// <summary><c>Internal</c> (staff) or <c>External</c> (portal). Default Internal.</summary>
    public ArticleVisibility? Visibility { get; init; }

    /// <summary>A category of the tenant's category tree.</summary>
    public Guid? CategoryId { get; init; }

    /// <summary>What changed.</summary>
    [StringLength(KbArticleVersion.NoteMaxLength)]
    public string? ChangeNote { get; init; }

    /// <inheritdoc />
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Title is { } title && ((title.En?.Length ?? 0) > KbArticleVersion.TitleMaxLength || (title.Ar?.Length ?? 0) > KbArticleVersion.TitleMaxLength))
        {
            yield return new ValidationResult($"Titles are at most {KbArticleVersion.TitleMaxLength} characters.", [nameof(Title)]);
        }

        if (Summary is { } summary && ((summary.En?.Length ?? 0) > KbArticleVersion.SummaryMaxLength || (summary.Ar?.Length ?? 0) > KbArticleVersion.SummaryMaxLength))
        {
            yield return new ValidationResult($"Summaries are at most {KbArticleVersion.SummaryMaxLength} characters.", [nameof(Summary)]);
        }
    }
}

/// <summary>A reviewer's comment.</summary>
public sealed class ReviewRequest
{
    /// <summary>Comment for the author; required when sending the article back.</summary>
    [StringLength(KbArticleVersion.NoteMaxLength)]
    public string? Comment { get; init; }
}

/// <summary>Filters of the staff article list.</summary>
public sealed class ArticleListQuery
{
    /// <summary>Only articles in this state.</summary>
    public ArticleState? State { get; init; }

    /// <summary>Only Internal or External articles.</summary>
    public ArticleVisibility? Visibility { get; init; }

    /// <summary>Only this category.</summary>
    public Guid? CategoryId { get; init; }

    /// <summary>Text in the title (either language), or the article number.</summary>
    [StringLength(200)]
    public string? Text { get; init; }

    /// <summary>True: only articles the caller created.</summary>
    public bool? Mine { get; init; }
}

/// <summary>An article in lists.</summary>
/// <param name="Id">Id.</param>
/// <param name="Number">Number, for example KB-000123.</param>
/// <param name="State"><c>Draft</c>, <c>InReview</c>, <c>Published</c> or <c>Retired</c>.</param>
/// <param name="Visibility"><c>Internal</c> or <c>External</c>.</param>
/// <param name="CategoryId">Category.</param>
/// <param name="Title">Title of the published version, or of the working version until first published.</param>
/// <param name="Summary">Summary, likewise.</param>
/// <param name="PendingState">State of the working version, if there is one (<c>Draft</c> or <c>InReview</c>).</param>
/// <param name="SubmittedAt">When the working version was submitted.</param>
/// <param name="ReviewComment">The reviewer's last comment on the working version.</param>
/// <param name="CanReview">The caller may approve or send back the version in review.</param>
/// <param name="ViewCount">Views (distinct viewers per day).</param>
/// <param name="HelpfulCount">"Helpful" answers.</param>
/// <param name="NotHelpfulCount">"Not helpful" answers.</param>
/// <param name="CreatedById">Who created it.</param>
/// <param name="SourceTicketId">The ticket it was made from.</param>
/// <param name="PublishedAt">When a version was last published.</param>
/// <param name="RetiredAt">When it was retired.</param>
/// <param name="CreatedAt">When it was created.</param>
/// <param name="UpdatedAt">When it last changed.</param>
internal sealed record ArticleSummaryResponse(
    Guid Id, string Number, ArticleState State, ArticleVisibility Visibility, Guid? CategoryId, LocalizedTextDto Title, LocalizedTextDto Summary,
    ArticleVersionState? PendingState, DateTimeOffset? SubmittedAt, string? ReviewComment, bool CanReview, long ViewCount, int HelpfulCount, int NotHelpfulCount,
    Guid? CreatedById, Guid? SourceTicketId, DateTimeOffset? PublishedAt, DateTimeOffset? RetiredAt, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

/// <summary>A version of an article.</summary>
/// <param name="Id">Id.</param>
/// <param name="Number">1, 2, 3…</param>
/// <param name="State"><c>Draft</c>, <c>InReview</c>, <c>Published</c>, <c>Superseded</c> or <c>Retired</c>.</param>
/// <param name="Title">Title.</param>
/// <param name="Summary">Summary.</param>
/// <param name="Body">Markdown body.</param>
/// <param name="Keywords">Keywords.</param>
/// <param name="Visibility">Visibility.</param>
/// <param name="CategoryId">Category.</param>
/// <param name="ChangeNote">What changed.</param>
/// <param name="CreatedBy">Who created it.</param>
/// <param name="LastEditedBy">Who last changed it.</param>
/// <param name="SubmittedBy">Who submitted it.</param>
/// <param name="SubmittedAt">When.</param>
/// <param name="ReviewedBy">Who approved or sent it back.</param>
/// <param name="ReviewedAt">When.</param>
/// <param name="ReviewComment">The reviewer's comment.</param>
/// <param name="PublishedAt">When it was published.</param>
/// <param name="CreatedAt">When it was created.</param>
/// <param name="UpdatedAt">When it last changed.</param>
internal sealed record VersionResponse(
    Guid Id, int Number, ArticleVersionState State, LocalizedTextDto Title, LocalizedTextDto Summary, LocalizedTextDto Body, IReadOnlyList<string> Keywords,
    ArticleVisibility Visibility, Guid? CategoryId, string? ChangeNote, PersonRef? CreatedBy, PersonRef? LastEditedBy, PersonRef? SubmittedBy,
    DateTimeOffset? SubmittedAt, PersonRef? ReviewedBy, DateTimeOffset? ReviewedAt, string? ReviewComment, DateTimeOffset? PublishedAt,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt)
{
    public static VersionResponse From(KbArticleVersion v, IReadOnlyDictionary<Guid, string?> names) => new(
        v.Id, v.Number, v.State, LocalizedTextDto.From(v.Title), LocalizedTextDto.From(v.Summary), LocalizedTextDto.From(v.Body), v.Keywords,
        v.Visibility, v.CategoryId, v.ChangeNote, PersonRef.Of(v.CreatedById, names), PersonRef.Of(v.LastEditedById, names),
        PersonRef.Of(v.SubmittedById, names), v.SubmittedAt, PersonRef.Of(v.ReviewedById, names), v.ReviewedAt, v.ReviewComment, v.PublishedAt,
        v.CreatedAt, v.UpdatedAt);
}

/// <summary>A person by id and name.</summary>
/// <param name="Id">User id.</param>
/// <param name="Name">Display name.</param>
internal sealed record PersonRef(Guid Id, string? Name)
{
    public static PersonRef? Of(Guid? id, IReadOnlyDictionary<Guid, string?> names) => id is { } value ? new(value, names.GetValueOrDefault(value)) : null;
}

/// <summary>What the caller may do with an article now.</summary>
/// <param name="Edit">Change the working version (or start one).</param>
/// <param name="Submit">Send the draft for review.</param>
/// <param name="Review">Approve or send back the version in review (four-eyes rule).</param>
/// <param name="Retire">Retire the published article.</param>
/// <param name="Reopen">Reopen the retired article as a draft.</param>
/// <param name="Discard">Discard the working version.</param>
internal sealed record ArticleActions(bool Edit, bool Submit, bool Review, bool Retire, bool Reopen, bool Discard);

/// <summary>The ticket an article was made from.</summary>
/// <param name="Id">Id.</param>
/// <param name="Number">Number, for example INC-000123.</param>
internal sealed record SourceTicketResponse(Guid Id, string Number);

/// <summary>An article for staff.</summary>
/// <param name="Article">The article.</param>
/// <param name="Published">The live version.</param>
/// <param name="Working">The version being written or reviewed.</param>
/// <param name="SourceTicket">The ticket it was made from.</param>
/// <param name="Images">Its images.</param>
/// <param name="Actions">What the caller may do.</param>
internal sealed record ArticleDetailResponse(
    ArticleSummaryResponse Article, VersionResponse? Published, VersionResponse? Working, SourceTicketResponse? SourceTicket,
    IReadOnlyList<ArticleImageResponse> Images, ArticleActions Actions);

/// <summary>A "Was this helpful?" answer.</summary>
/// <param name="Id">Id.</param>
/// <param name="Helpful">Yes or no.</param>
/// <param name="Comment">Comment.</param>
/// <param name="UserName">Who answered, if signed in.</param>
/// <param name="Visitor">Answered by a visitor who was not signed in.</param>
/// <param name="VersionNumber">The version that was read.</param>
/// <param name="CreatedAt">When it was first given.</param>
/// <param name="UpdatedAt">When it last changed.</param>
internal sealed record FeedbackResponse(Guid Id, bool Helpful, string? Comment, string? UserName, bool Visitor, int? VersionNumber, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
