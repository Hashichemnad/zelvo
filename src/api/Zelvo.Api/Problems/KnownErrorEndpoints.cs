using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;
using Zelvo.Api.Conventions;
using Zelvo.Api.Forms;
using Zelvo.Api.Identity;
using Zelvo.Api.Incidents;
using Zelvo.Api.Knowledge;
using Zelvo.Api.Settings;
using Zelvo.Api.Tenancy;
using Zelvo.Data;
using Zelvo.Data.Auditing;
using Zelvo.Data.Knowledge;
using Zelvo.Data.Localization;
using Zelvo.Data.Problems;
using Zelvo.Data.Tenancy;
using Zelvo.Data.Tickets;
using Zelvo.Search;
using Zelvo.Tickets;

namespace Zelvo.Api.Problems;

/// <summary>
/// The known error database (module 16, task H1): one known error per problem (<c>KE-000123</c>)
/// with a workaround in English and Arabic. Agents see the active ones on matching incidents (the
/// incident is linked to the problem, shares a configuration item with it, or is in the problem's
/// category or below) and paste the workaround into a reply. A known error reaches the portal as an
/// external knowledge article that goes through D1's four-eyes review (product owner decision 9).
/// </summary>
internal static class KnownErrorEndpoints
{
    public static RouteGroupBuilder MapKnownErrorEndpoints(this RouteGroupBuilder group)
    {
        var errors = group.MapGroup("/known-errors").WithTags("Problems").RequireTenant();
        errors.MapGet("/", ListAsync).WithName("ListKnownErrors")
            .WithSummary("The known error database, newest first. Filters q (number, title, symptoms or workaround, either language) and status. Needs problems.read.")
            .RequirePermission(Permissions.ProblemsRead).Produces<PageResult<KnownErrorResponse>>();
        errors.MapGet("/{id:guid}", GetAsync).WithName("GetKnownError")
            .WithSummary("One known error. Needs problems.read.")
            .RequirePermission(Permissions.ProblemsRead).Produces<KnownErrorResponse>();
        errors.MapPost("/{id:guid}/retire", (Guid id, HttpContext http, ZelvoDbContext db, ISearchIndexer indexer, CancellationToken ct) =>
                SetStatusAsync(id, KnownErrorStatus.Retired, http, db, indexer, ct))
            .WithName("RetireKnownError")
            .WithSummary("Stop offering the workaround on incidents. Needs problems.manage; audited.")
            .RequirePermission(Permissions.ProblemsManage).Produces<KnownErrorResponse>();
        errors.MapPost("/{id:guid}/activate", (Guid id, HttpContext http, ZelvoDbContext db, ISearchIndexer indexer, CancellationToken ct) =>
                SetStatusAsync(id, KnownErrorStatus.Active, http, db, indexer, ct))
            .WithName("ActivateKnownError")
            .WithSummary("Offer a retired known error's workaround again (its incidents' assignees are told). Needs problems.manage; audited.")
            .RequirePermission(Permissions.ProblemsManage).Produces<KnownErrorResponse>();
        errors.MapPost("/{id:guid}/article", ArticleAsync).WithName("DraftKnownErrorArticle")
            .WithSummary("Make an external knowledge article draft from the known error (title, symptoms and workaround in both languages, the problem's category). It reaches the portal once a publisher other than its author approves it (four-eyes review). Needs problems.manage and knowledge.write; audited.")
            .RequirePermission(Permissions.ProblemsManage).RequirePermission(Permissions.KnowledgeWrite).Produces<ArticleDetailResponse>(StatusCodes.Status201Created);

        group.MapPut("/problems/{id:guid}/known-error", SaveAsync).WithName("SaveProblemKnownError").WithTags("Problems").RequireTenant()
            .WithSummary("Record or change the problem's known error: title, symptoms and the workaround, at least one language each for title and workaround. A new one gets a number and tells the assignees of the problem's open incidents. Needs problems.edit; audited.")
            .RequirePermission(Permissions.ProblemsEdit).Produces<KnownErrorResponse>().ProducesValidationProblem();
        group.MapGet("/incidents/{id:guid}/known-errors", MatchingAsync).WithName("ListIncidentKnownErrors").WithTags("Problems").RequireTenant()
            .WithSummary("Active known errors that match an incident: it is linked to the problem, shares a configuration item with it, or is in the problem's category or below. Each says why it matches. Needs incidents.read.")
            .RequirePermission(Permissions.IncidentsRead).Produces<IReadOnlyList<MatchingKnownErrorResponse>>();
        return group;
    }

    internal static async Task<IResult> ListAsync(
        [AsParameters] PageQuery page, string? q, KnownErrorStatus? status, ZelvoDbContext db, CancellationToken cancellationToken)
    {
        var errors = db.KnownErrors.AsNoTracking();
        if (status is { } s)
        {
            errors = errors.Where(e => e.Status == s);
        }

        if (q is { Length: > 0 })
        {
            var pattern = $"%{q.Trim().Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal)}%";
            errors = errors.Where(e => EF.Functions.ILike(e.Number, pattern)
                || EF.Functions.ILike(e.Title.En ?? "", pattern) || EF.Functions.ILike(e.Title.Ar ?? "", pattern)
                || EF.Functions.ILike(e.Symptoms.En ?? "", pattern) || EF.Functions.ILike(e.Symptoms.Ar ?? "", pattern)
                || EF.Functions.ILike(e.Workaround.En ?? "", pattern) || EF.Functions.ILike(e.Workaround.Ar ?? "", pattern));
        }

        var ordered = errors.OrderByDescending(e => e.Sequence);
        var total = await ordered.LongCountAsync(cancellationToken);
        var rows = await KnownErrorResponses.LoadAsync(db, ordered.Skip(page.Skip).Take(page.Size), cancellationToken);
        return TypedResults.Ok(new PageResult<KnownErrorResponse>(rows, page.Number, page.Size, total));
    }

    internal static async Task<IResult> GetAsync(Guid id, ZelvoDbContext db, CancellationToken cancellationToken) =>
        (await KnownErrorResponses.LoadAsync(db, db.KnownErrors.AsNoTracking().Where(e => e.Id == id), cancellationToken)).SingleOrDefault() is { } error
            ? TypedResults.Ok(error)
            : FormsCommon.NotFound("known-error");

    internal static async Task<IResult> SaveAsync(
        Guid id, SaveKnownErrorRequest request, HttpContext http, ZelvoDbContext db, CurrentUser user, ITenantContext tenant, TenantSettingsStore settings,
        ISearchIndexer indexer, CancellationToken cancellationToken)
    {
        var problem = await db.Tickets.AsNoTracking().SingleOrDefaultAsync(t => t.Id == id && t.Type == TicketType.Problem, cancellationToken);
        if (problem is null)
        {
            return ProblemCore.NotFound();
        }

        if (problem.State == TicketState.Closed)
        {
            return IncidentEndpoints.Closed();
        }

        var title = Clean(request.Title, KnownError.TitleMaxLength);
        var workaround = Clean(request.Workaround, KnownError.TextMaxLength);
        var symptoms = Clean(request.Symptoms, KnownError.TextMaxLength);
        if (title is null || !title.HasAnyValue)
        {
            return UserEndpoints.Validation("title", $"Give a title in English or Arabic, up to {KnownError.TitleMaxLength} characters.");
        }

        if (workaround is null || !workaround.HasAnyValue)
        {
            return UserEndpoints.Validation("workaround", $"Describe the workaround in English or Arabic, up to {KnownError.TextMaxLength} characters.");
        }

        if (symptoms is null)
        {
            return UserEndpoints.Validation("symptoms", $"Symptoms are at most {KnownError.TextMaxLength} characters.");
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var error = await db.KnownErrors.SingleOrDefaultAsync(e => e.ProblemId == id, cancellationToken);
        var now = DateTimeOffset.UtcNow;
        if (error is null)
        {
            var prefix = (await settings.GetAsync(ProblemSettings.Problems, cancellationToken)).KnownErrorPrefix;
            var sequence = (await db.Database.SqlQuery<long>($"""
                INSERT INTO known_error_number_sequences (tenant_id, last_value) VALUES ({tenant.TenantId!.Value}, 1)
                ON CONFLICT (tenant_id) DO UPDATE SET last_value = known_error_number_sequences.last_value + 1
                RETURNING last_value AS "Value"
                """).ToListAsync(cancellationToken)).Single();
            error = new KnownError
            {
                Number = TicketNumbers.Format(prefix, sequence),
                Sequence = sequence,
                ProblemId = id,
                Title = title,
                Symptoms = symptoms,
                Workaround = workaround,
                CreatedById = user.Account?.Id,
                CreatedAt = now,
                UpdatedAt = now,
            };
            db.KnownErrors.Add(error);
        }
        else
        {
            var ifMatch = http.Request.Headers.IfMatch.ToString();
            if (ifMatch.Length > 0)
            {
                if (!uint.TryParse(ifMatch.Trim().Replace("W/", "", StringComparison.Ordinal).Trim('"'), out var version) || version != error.Version)
                {
                    return FormsCommon.VersionMismatch();
                }

                db.Entry(error).Property(e => e.Version).OriginalValue = version;
            }

            error.Title = title;
            error.Symptoms = symptoms;
            error.Workaround = workaround;
            error.UpdatedAt = now;
        }

        indexer.Upsert(ProblemSearch.Document(error, await ProblemSearch.StaffRolesAsync(db, cancellationToken)));
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return FormsCommon.VersionMismatch();
        }

        return await GetAsync(error.Id, db, cancellationToken);
    }

    internal static async Task<IResult> SetStatusAsync(Guid id, KnownErrorStatus status, HttpContext http, ZelvoDbContext db, ISearchIndexer indexer, CancellationToken cancellationToken)
    {
        var error = await db.KnownErrors.SingleOrDefaultAsync(e => e.Id == id, cancellationToken);
        if (error is null)
        {
            return FormsCommon.NotFound("known-error");
        }

        if (error.Status != status)
        {
            error.Status = status;
            error.UpdatedAt = DateTimeOffset.UtcNow;
            // A problem that is a known error cannot keep that stage without an active one.
            if (status == KnownErrorStatus.Retired
                && await db.Problems.SingleOrDefaultAsync(p => p.TicketId == error.ProblemId && p.Phase == ProblemPhase.KnownError, cancellationToken) is { } problem)
            {
                problem.Phase = ProblemPhase.RootCauseAnalysis;
            }

            indexer.Upsert(ProblemSearch.Document(error, await ProblemSearch.StaffRolesAsync(db, cancellationToken)));
            await db.SaveChangesAsync(cancellationToken);
        }

        return await GetAsync(id, db, cancellationToken);
    }

    internal static async Task<IResult> ArticleAsync(
        Guid id, HttpContext http, ZelvoDbContext db, CurrentUser user, ITenantContext tenant, TenantSettingsStore settings, ISearchIndexer indexer,
        IAuditLog audit, CancellationToken cancellationToken)
    {
        var error = await db.KnownErrors.SingleOrDefaultAsync(e => e.Id == id, cancellationToken);
        if (error is null)
        {
            return FormsCommon.NotFound("known-error");
        }

        if (error.ArticleId is { } existing && await db.KbArticles.AnyAsync(a => a.Id == existing && a.State != ArticleState.Retired, cancellationToken))
        {
            return ApiProblem.Create(StatusCodes.Status409Conflict, "article-exists", "The known error already has an article; change that article instead.");
        }

        var problem = await db.Tickets.AsNoTracking().SingleAsync(t => t.Id == error.ProblemId, cancellationToken);
        var request = new ArticleContentRequest
        {
            Title = new LocalizedTextInput { En = Cut(error.Title.En), Ar = Cut(error.Title.Ar) },
            Body = new ArticleTextInput { En = Body(error.Symptoms.En, error.Workaround.En, "en"), Ar = Body(error.Symptoms.Ar, error.Workaround.Ar, "ar") },
            Keywords = [error.Number],
            Visibility = ArticleVisibility.External,
            CategoryId = problem.CategoryId is { } c && await db.Categories.AnyAsync(x => x.Id == c && x.Active, cancellationToken) ? c : null,
            ChangeNote = $"From known error {error.Number} ({problem.Number}).",
        };
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var article = await KnowledgeEndpoints.NewArticleAsync(db, tenant, settings, user, request, problem.Id, cancellationToken);
        error.ArticleId = article.Id;
        error.UpdatedAt = DateTimeOffset.UtcNow;
        db.ProblemArticles.Add(new ProblemArticle { ProblemId = problem.Id, ArticleId = article.Id, LinkedById = user.Account?.Id });
        audit.Add(AuditCategory.Create, "problems.known-error.article-drafted", "kb_article", article.Id.ToString(),
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["knownErrorId"] = error.Id, ["knownError"] = error.Number, ["problem"] = problem.Number });
        var result = await KnowledgeEndpoints.SaveAsync(http, db, user, settings, indexer, article, cancellationToken, created: true);
        await transaction.CommitAsync(cancellationToken);
        return result;

        static string? Cut(string? value) => value is null ? null : value.Length <= KbArticleVersion.TitleMaxLength ? value : value[..KbArticleVersion.TitleMaxLength];
    }

    // The article's Markdown: symptoms and workaround under headings of the article's language.
    private static string? Body(string? symptoms, string? workaround, string language)
    {
        if (workaround is null && symptoms is null)
        {
            return null;
        }

        var parts = new List<string>();
        if (symptoms is not null)
        {
            parts.Add(language == "ar" ? "## الأعراض" : "## Symptoms");
            parts.Add(symptoms);
        }

        if (workaround is not null)
        {
            parts.Add(language == "ar" ? "## الحل المؤقت" : "## Workaround");
            parts.Add(workaround);
        }

        var body = string.Join("\n\n", parts);
        return body.Length <= KbArticleVersion.BodyMaxLength ? body : body[..KbArticleVersion.BodyMaxLength];
    }

    internal static async Task<IResult> MatchingAsync(Guid id, ZelvoDbContext db, CancellationToken cancellationToken)
    {
        var incident = await db.Tickets.AsNoTracking().SingleOrDefaultAsync(t => t.Id == id && t.Type == TicketType.Incident, cancellationToken);
        if (incident is null)
        {
            return IncidentEndpoints.NotFound();
        }

        var reasons = new Dictionary<Guid, HashSet<string>>();
        void Add(IEnumerable<Guid> problems, string reason)
        {
            foreach (var problem in problems)
            {
                (reasons.TryGetValue(problem, out var set) ? set : reasons[problem] = []).Add(reason);
            }
        }

        Add(await db.ProblemIncidents.AsNoTracking().Where(l => l.IncidentId == id).Select(l => l.ProblemId).ToListAsync(cancellationToken), "problem");
        var items = db.TicketConfigurationItems.Where(l => l.TicketId == id).Select(l => l.ConfigurationItemId);
        Add(await db.TicketConfigurationItems.AsNoTracking()
            .Where(l => l.TicketId != id && items.Contains(l.ConfigurationItemId) && db.Tickets.Any(t => t.Id == l.TicketId && t.Type == TicketType.Problem))
            .Select(l => l.TicketId).Distinct().ToListAsync(cancellationToken), "configurationItem");
        if (incident.CategoryId is { } categoryId)
        {
            // The incident's category and those above it: a problem raised on a parent covers its children.
            var parents = await db.Categories.AsNoTracking().ToDictionaryAsync(c => c.Id, c => c.ParentId, cancellationToken);
            var ancestry = new List<Guid>();
            for (Guid? current = categoryId; current is { } node && ancestry.Count < 10; current = parents.GetValueOrDefault(node))
            {
                ancestry.Add(node);
            }

            Add(await db.Tickets.AsNoTracking().Where(t => t.Type == TicketType.Problem && t.CategoryId != null && ancestry.Contains(t.CategoryId.Value))
                .Select(t => t.Id).ToListAsync(cancellationToken), "category");
        }

        var problemIds = reasons.Keys.ToList();
        var errors = await KnownErrorResponses.LoadAsync(db,
            db.KnownErrors.AsNoTracking().Where(e => e.Status == KnownErrorStatus.Active && problemIds.Contains(e.ProblemId)).OrderByDescending(e => e.Sequence).Take(50),
            cancellationToken);
        // Linked problems first, then shared items, then category.
        static int Rank(HashSet<string> r) => r.Contains("problem") ? 0 : r.Contains("configurationItem") ? 1 : 2;
        return TypedResults.Ok(errors
            .Select(e => new MatchingKnownErrorResponse(e, [.. reasons[e.ProblemId].Order(StringComparer.Ordinal)]))
            .OrderBy(m => Rank(reasons[m.KnownError.ProblemId])).ToList());
    }

    private static LocalizedText? Clean(KnownErrorTextInput? input, int max)
    {
        var en = string.IsNullOrWhiteSpace(input?.En) ? null : input.En.Trim();
        var ar = string.IsNullOrWhiteSpace(input?.Ar) ? null : input.Ar.Trim();
        return en?.Length > max || ar?.Length > max ? null : new LocalizedText(en, ar);
    }
}

/// <summary>A known error matching an incident, with why: <c>problem</c> (linked), <c>configurationItem</c> (shared) or <c>category</c>.</summary>
internal sealed record MatchingKnownErrorResponse(KnownErrorResponse KnownError, IReadOnlyList<string> Reasons);

/// <summary>A problem's known error.</summary>
public sealed class SaveKnownErrorRequest
{
    /// <summary>Title in English and/or Arabic.</summary>
    [Required]
    public KnownErrorTextInput? Title { get; init; }

    /// <summary>How it shows itself.</summary>
    public KnownErrorTextInput? Symptoms { get; init; }

    /// <summary>What to do until it is fixed, in English and/or Arabic.</summary>
    [Required]
    public KnownErrorTextInput? Workaround { get; init; }
}

/// <summary>Text in English and Arabic, up to 10,000 characters each.</summary>
public sealed class KnownErrorTextInput
{
    /// <summary>English.</summary>
    [StringLength(KnownError.TextMaxLength)]
    public string? En { get; init; }

    /// <summary>Arabic.</summary>
    [StringLength(KnownError.TextMaxLength)]
    public string? Ar { get; init; }
}
