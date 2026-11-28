using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Zelvo.Ai;
using Zelvo.Api.Conventions;
using Zelvo.Api.Forms;
using Zelvo.Api.Identity;
using Zelvo.Api.Incidents;
using Zelvo.Api.Integrations;
using Zelvo.Api.ServiceDesk;
using Zelvo.Api.Settings;
using Zelvo.Api.Tenancy;
using Zelvo.Data;
using Zelvo.Data.Auditing;
using Zelvo.Data.Cmdb;
using Zelvo.Data.Localization;
using Zelvo.Data.Problems;
using Zelvo.Data.Tenancy;
using Zelvo.Data.Tickets;
using Zelvo.Search;
using Zelvo.Tickets;

namespace Zelvo.Api.Problems;

/// <summary>
/// The problem managers' queue of suggested problems (module 16, task H2): groups of similar
/// incidents judged through the AI gateway and rising trends per category, configuration item and
/// business service. Accepting raises the problem with the incidents linked; dismissing keeps the
/// reason so the same group is not suggested again. Zelvo never raises a problem by itself.
/// </summary>
internal static class ProblemSuggestionEndpoints
{
    public static RouteGroupBuilder MapProblemSuggestionEndpoints(this RouteGroupBuilder group)
    {
        var suggestions = group.MapGroup("/problem-suggestions").WithTags("Problems").RequireTenant();
        suggestions.MapGet("/", ListAsync).WithName("ListProblemSuggestions")
            .WithSummary("Suggested problems, newest first: status (Open by default, Accepted, Dismissed) and kind (SimilarIncidents, Trend). Each names its incidents. Needs problems.manage.")
            .RequirePermission(Permissions.ProblemsManage).Produces<PageResult<ProblemSuggestionResponse>>();
        suggestions.MapGet("/{id:guid}", GetAsync).WithName("GetProblemSuggestion")
            .WithSummary("One suggested problem with its incidents. Returns an ETag to send as If-Match when deciding. Needs problems.manage.")
            .RequirePermission(Permissions.ProblemsManage).Produces<ProblemSuggestionResponse>();
        suggestions.MapPost("/{id:guid}/accept", AcceptAsync).WithName("AcceptProblemSuggestion")
            .WithSummary("Raise the problem with the suggestion's incidents linked (and the trend's configuration item or service). Title, description, category and priority may be changed. Needs problems.manage and a person; audited.")
            .RequirePerson().RequirePermission(Permissions.ProblemsManage).RequirePermission(Permissions.ProblemsEdit)
            .Produces<ProblemResponse>(StatusCodes.Status201Created).ProducesValidationProblem();
        suggestions.MapPost("/{id:guid}/dismiss", DismissAsync).WithName("DismissProblemSuggestion")
            .WithSummary("Dismiss with a reason. The reason is kept and the same group or trend is not suggested again. Needs problems.manage; audited.")
            .RequirePerson().RequirePermission(Permissions.ProblemsManage).Produces<ProblemSuggestionResponse>().ProducesValidationProblem();
        suggestions.MapPost("/detect", DetectAsync).WithName("DetectProblems")
            .WithSummary("Look for problems to suggest now instead of waiting for the next run (every 6 hours). 409 while a run is going on. Needs problems.manage; audited.")
            .RequirePerson().RequirePermission(Permissions.ProblemsManage).Produces<ProblemDetectionRun>();

        var settings = group.MapGroup("/settings/problems/detection").WithTags("Settings").RequireTenant();
        settings.MapGet("/", SettingsAsync).WithName("GetProblemDetectionSettings")
            .WithSummary("Problem detection settings: similar incidents (on, 30–90 days, fewest incidents) and trends (on, fewest incidents in 30 days, times the usual level). Needs problems.manage.")
            .RequirePermission(Permissions.ProblemsManage).Produces<ProblemDetectionSettingsResponse>();
        settings.MapPut("/", UpdateSettingsAsync).WithName("UpdateProblemDetectionSettings")
            .WithSummary("Change the problem detection settings. Needs problems.manage; audited.")
            .RequirePermission(Permissions.ProblemsManage).Produces<ProblemDetectionSettingsResponse>().ProducesValidationProblem();
        return group;
    }

    internal static async Task<IResult> ListAsync(
        [AsParameters] PageQuery page, ProblemSuggestionStatus? status, ProblemSuggestionKind? kind, ZelvoDbContext db, CancellationToken cancellationToken)
    {
        var wanted = status is null or ProblemSuggestionStatus.NotConfirmed ? ProblemSuggestionStatus.Open : status.Value;
        var query = db.ProblemSuggestions.AsNoTracking().Where(s => s.Status == wanted);
        if (kind is { } k)
        {
            query = query.Where(s => s.Kind == k);
        }

        var ordered = wanted == ProblemSuggestionStatus.Open
            ? query.OrderByDescending(s => s.IncidentCount).ThenByDescending(s => s.CreatedAt)
            : query.OrderByDescending(s => s.DecidedAt).ThenByDescending(s => s.Id);
        var total = await ordered.LongCountAsync(cancellationToken);
        var rows = await ordered.Skip(page.Skip).Take(page.Size).ToListAsync(cancellationToken);
        var responses = await ResponsesAsync(db, rows, cancellationToken);
        return TypedResults.Ok(new PageResult<ProblemSuggestionResponse>(responses, page.Number, page.Size, total));
    }

    internal static async Task<IResult> GetAsync(Guid id, HttpContext http, ZelvoDbContext db, CancellationToken cancellationToken)
    {
        var suggestion = await db.ProblemSuggestions.AsNoTracking().SingleOrDefaultAsync(s => s.Id == id && s.Status != ProblemSuggestionStatus.NotConfirmed, cancellationToken);
        if (suggestion is null)
        {
            return FormsCommon.NotFound("problem-suggestion");
        }

        FormsCommon.SetETag(http, suggestion.Version);
        return TypedResults.Ok((await ResponsesAsync(db, [suggestion], cancellationToken)).Single());
    }

    internal static async Task<IResult> AcceptAsync(
        Guid id, AcceptProblemSuggestionRequest request, HttpContext http, ZelvoDbContext db, CurrentUser user, ITenantContext tenant,
        TenantSettingsStore settings, IAuditLog audit, ISearchIndexer indexer, CancellationToken cancellationToken)
    {
        var suggestion = await db.ProblemSuggestions.SingleOrDefaultAsync(s => s.Id == id && s.Status != ProblemSuggestionStatus.NotConfirmed, cancellationToken);
        if (suggestion is null)
        {
            return FormsCommon.NotFound("problem-suggestion");
        }

        if (http.Request.Headers.IfMatch.Count > 0 && !FormsCommon.Matches(http, suggestion.Version))
        {
            return FormsCommon.VersionMismatch();
        }

        if (suggestion.Status != ProblemSuggestionStatus.Open)
        {
            return Decided();
        }

        if (request.CategoryId is { } category && !await TreeEndpoints.IsSelectableAsync(db.Categories, category, cancellationToken))
        {
            return UserEndpoints.Validation("categoryId", "Choose an active category.");
        }

        var incidentIds = await db.ProblemSuggestionIncidents.AsNoTracking().Where(l => l.SuggestionId == id)
            .Join(db.Tickets.AsNoTracking().Where(t => t.Type == TicketType.Incident), l => l.IncidentId, t => t.Id, (l, t) => t.Id)
            .Take(ProblemCore.MaxIncidentsPerCall).ToListAsync(cancellationToken);
        var incidents = await db.Tickets.AsNoTracking().Include(t => t.Incident).Where(t => incidentIds.Contains(t.Id)).ToListAsync(cancellationToken);
        var language = user.Account?.PreferredLanguage == LocalizedText.Arabic ? LocalizedText.Arabic : LocalizedText.English;
        var title = request.Title?.Trim() is { Length: > 0 } given
            ? given
            : suggestion.Title ?? PickTitle((await ResponsesAsync(db, [suggestion], cancellationToken)).Single().SuggestedTitle, language);
        var categoryId = request.CategoryId
            ?? (suggestion.CategoryId is { } c && await TreeEndpoints.IsSelectableAsync(db.Categories, c, cancellationToken) ? c : null);
        var priority = request.Priority ?? incidents.Select(i => i.Incident?.Priority).OfType<int>().DefaultIfEmpty(3).Min();

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var ticket = await ProblemCore.CreateAsync(http, db, settings, tenant, indexer,
            new ProblemDraft(title, request.Description ?? suggestion.Reason, user.Account!.Id, categoryId, Math.Clamp(priority, 1, 5), incidentIds), cancellationToken);
        if (suggestion.ConfigurationItemId is { } item && await db.ConfigurationItems.AnyAsync(i => i.Id == item && i.Status != CiStatus.Retired, cancellationToken))
        {
            db.TicketConfigurationItems.Add(new TicketConfigurationItem { TicketId = ticket.Id, ConfigurationItemId = item, AddedById = user.Account.Id });
        }

        foreach (var incident in incidents)
        {
            audit.Add(AuditCategory.Change, "problems.incident.linked", "ticket", incident.Id.ToString(),
                new Dictionary<string, object?>(StringComparer.Ordinal) { ["problemId"] = ticket.Id, ["problem"] = ticket.Number, ["incident"] = incident.Number });
        }

        suggestion.Status = ProblemSuggestionStatus.Accepted;
        suggestion.ProblemId = ticket.Id;
        suggestion.DecidedById = user.Account.Id;
        suggestion.DecidedAt = DateTimeOffset.UtcNow;
        suggestion.UpdatedAt = DateTimeOffset.UtcNow;
        audit.Add(AuditCategory.Change, "problems.suggestion.accepted", "problem_suggestion", suggestion.Id.ToString(),
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["kind"] = suggestion.Kind.ToString(), ["problemId"] = ticket.Id, ["problem"] = ticket.Number, ["incidents"] = incidents.Count });
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Decided();
        }

        await transaction.CommitAsync(cancellationToken);
        return TypedResults.Created($"/api/v1/problems/{ticket.Id}", await ProblemCore.ResponseAsync(http, db, user, ticket.Id, cancellationToken));
    }

    internal static async Task<IResult> DismissAsync(
        Guid id, DismissProblemSuggestionRequest request, HttpContext http, ZelvoDbContext db, CurrentUser user, IAuditLog audit, CancellationToken cancellationToken)
    {
        var suggestion = await db.ProblemSuggestions.SingleOrDefaultAsync(s => s.Id == id && s.Status != ProblemSuggestionStatus.NotConfirmed, cancellationToken);
        if (suggestion is null)
        {
            return FormsCommon.NotFound("problem-suggestion");
        }

        if (http.Request.Headers.IfMatch.Count > 0 && !FormsCommon.Matches(http, suggestion.Version))
        {
            return FormsCommon.VersionMismatch();
        }

        if (suggestion.Status != ProblemSuggestionStatus.Open)
        {
            return Decided();
        }

        if (request.Reason?.Trim() is not { Length: > 0 } reason)
        {
            return UserEndpoints.Validation("reason", "Give the reason for dismissing the suggestion.");
        }

        suggestion.Status = ProblemSuggestionStatus.Dismissed;
        suggestion.DismissReason = reason;
        suggestion.DecidedById = user.Account!.Id;
        suggestion.DecidedAt = DateTimeOffset.UtcNow;
        suggestion.UpdatedAt = DateTimeOffset.UtcNow;
        audit.Add(AuditCategory.Change, "problems.suggestion.dismissed", "problem_suggestion", suggestion.Id.ToString(),
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["kind"] = suggestion.Kind.ToString(), ["reasonLength"] = reason.Length });
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Decided();
        }

        var saved = await db.ProblemSuggestions.AsNoTracking().SingleAsync(s => s.Id == id, cancellationToken);
        FormsCommon.SetETag(http, saved.Version);
        return TypedResults.Ok((await ResponsesAsync(db, [saved], cancellationToken)).Single());
    }

    internal static async Task<IResult> DetectAsync(
        ITenantContext tenant, ProblemDetectionJobs jobs, IAuditLog audit, ZelvoDbContext db, TimeProvider time, CancellationToken cancellationToken)
    {
        audit.Add(AuditCategory.Admin, "problems.detection.requested", "tenant", tenant.TenantId!.Value.ToString());
        await db.SaveChangesAsync(cancellationToken);
        var run = await jobs.RunTenantAsync(tenant.TenantId.Value, tenant.Slug!, time.GetUtcNow(), cancellationToken);
        return run is null
            ? ApiProblem.Create(StatusCodes.Status409Conflict, "detection-running", "Zelvo is already looking for problems. Try again in a minute.")
            : TypedResults.Ok(run);
    }

    internal static async Task<IResult> SettingsAsync(TenantSettingsStore store, IAiGateway gateway, CancellationToken cancellationToken) =>
        TypedResults.Ok(await SettingsResponseAsync(store, gateway, cancellationToken));

    internal static async Task<Results<Ok<ProblemDetectionSettingsResponse>, ValidationProblem>> UpdateSettingsAsync(
        UpdateProblemDetectionSettingsRequest request, TenantSettingsStore store, IAiGateway gateway, ZelvoDbContext db, CancellationToken cancellationToken)
    {
        var current = await store.GetAsync(ProblemDetector.Settings, cancellationToken);
        var value = new ProblemDetectionSettingsValue(
            request.SimilarEnabled ?? current.SimilarEnabled, request.WindowDays ?? current.WindowDays, request.MinimumIncidents ?? current.MinimumIncidents,
            request.TrendsEnabled ?? current.TrendsEnabled, request.TrendMinimum ?? current.TrendMinimum, request.TrendFactor ?? current.TrendFactor);
        if (!value.IsValid())
        {
            return UserEndpoints.Validation("problemDetection",
                "Compare similar incidents over 30–90 days with at least 3–100 incidents; trends need 2–10,000 incidents and 1.1 to 10 times the usual level.");
        }

        await store.SetAsync(ProblemDetector.Settings, value, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Ok(await SettingsResponseAsync(store, gateway, cancellationToken));
    }

    private static async Task<ProblemDetectionSettingsResponse> SettingsResponseAsync(TenantSettingsStore store, IAiGateway gateway, CancellationToken cancellationToken)
    {
        var value = await store.GetAsync(ProblemDetector.Settings, cancellationToken);
        var ai = (await gateway.AvailabilityAsync(cancellationToken)).FirstOrDefault(a => a.Feature == AiFeatures.ProblemDetection);
        return new ProblemDetectionSettingsResponse(value.SimilarEnabled, value.WindowDays, value.MinimumIncidents, value.TrendsEnabled, value.TrendMinimum,
            value.TrendFactor, ai is { Available: true }, ai is { Available: true } ? null : ai?.Reason ?? "ai-off");
    }

    private static ProblemHttpResult Decided() =>
        (ProblemHttpResult)ApiProblem.Create(StatusCodes.Status409Conflict, "suggestion-decided", "Someone already accepted or dismissed this suggestion.");

    private static async Task<List<ProblemSuggestionResponse>> ResponsesAsync(ZelvoDbContext db, IReadOnlyList<ProblemSuggestion> rows, CancellationToken cancellationToken)
    {
        var ids = rows.Select(r => r.Id).ToList();
        var links = await db.ProblemSuggestionIncidents.AsNoTracking().Where(l => ids.Contains(l.SuggestionId))
            .Join(db.Tickets.AsNoTracking(), l => l.IncidentId, t => t.Id, (l, t) => new { l.SuggestionId, t.Id, t.Number, t.Title, t.State, t.CreatedAt, Priority = (int?)t.Incident!.Priority })
            .ToListAsync(cancellationToken);
        var itemIds = rows.Select(r => r.ConfigurationItemId).OfType<Guid>().ToList();
        var items = await db.ConfigurationItems.AsNoTracking().Where(i => itemIds.Contains(i.Id))
            .ToDictionaryAsync(i => i.Id, i => new SuggestionItemRef(i.Id, i.Number, i.Name), cancellationToken);
        var problemIds = rows.Select(r => r.ProblemId).OfType<Guid>().ToList();
        var problems = await db.Tickets.AsNoTracking().Where(t => problemIds.Contains(t.Id)).ToDictionaryAsync(t => t.Id, t => t.Number, cancellationToken);
        var names = await IncidentEndpoints.Names(db, rows.Select(r => r.DecidedById ?? Guid.Empty), cancellationToken);
        var refs = await DirectoryRefs.LoadAsync(db, cancellationToken);
        return [.. rows.Select(s =>
        {
            var category = refs.Categories.RefOf(s.CategoryId);
            var item = s.ConfigurationItemId is { } ci ? items.GetValueOrDefault(ci) : null;
            var subjectEn = item is not null ? $"{item.Number} {item.Name}" : category?.Path.En ?? category?.Path.Ar ?? "";
            var subjectAr = item is not null ? $"{item.Number} {item.Name}" : category?.Path.Ar ?? category?.Path.En ?? "";
            var suggested = s.Kind == ProblemSuggestionKind.Trend
                ? new LocalizedText(Cut($"Rising incidents: {subjectEn}"), Cut($"ارتفاع في البلاغات: {subjectAr}"))
                : new LocalizedText(s.Language == LocalizedText.Arabic ? null : s.Title, s.Language == LocalizedText.Arabic ? s.Title : null);
            return new ProblemSuggestionResponse(
                s.Id, s.Kind, s.Status, s.Title, LocalizedTextDto.From(suggested), s.Reason, s.Confidence, s.Language, s.Dimension, category, item,
                s.WindowStart, s.WindowEnd, s.IncidentCount, s.Baseline,
                [.. links.Where(l => l.SuggestionId == s.Id).OrderByDescending(l => l.CreatedAt)
                    .Select(l => new SuggestionIncidentResponse(l.Id, l.Number, l.Title, l.State, l.Priority, l.CreatedAt))],
                s.ProblemId is { } p ? new SuggestionProblemRef(p, problems.GetValueOrDefault(p) ?? "") : null,
                s.DecidedById is { } by ? new PersonResponse(by, names.GetValueOrDefault(by)) : null,
                s.DecidedAt, s.DismissReason, $"\"{s.Version}\"", s.CreatedAt, s.UpdatedAt);
        })];
    }

    private static string PickTitle(LocalizedTextDto title, string language) =>
        (language == LocalizedText.Arabic ? title.Ar ?? title.En : title.En ?? title.Ar) ?? "Suggested problem";

    private static string Cut(string text) => text.Length <= Ticket.TitleMaxLength ? text : text[..Ticket.TitleMaxLength];
}

/// <summary>An incident named by a suggestion.</summary>
internal sealed record SuggestionIncidentResponse(Guid Id, string Number, string Title, TicketState State, int? Priority, DateTimeOffset CreatedAt);

/// <summary>A trend's configuration item or business service.</summary>
internal sealed record SuggestionItemRef(Guid Id, string Number, string Name);

/// <summary>The problem an accepted suggestion became.</summary>
internal sealed record SuggestionProblemRef(Guid Id, string Number);

/// <summary>A suggested problem (task H2).</summary>
/// <param name="Id">Id.</param>
/// <param name="Kind">Similar incidents or a trend.</param>
/// <param name="Status">Open, Accepted or Dismissed.</param>
/// <param name="Title">The AI's title for similar incidents, in <paramref name="Language"/>.</param>
/// <param name="SuggestedTitle">A title for the problem in English and Arabic as far as known (trends: "Rising incidents: …").</param>
/// <param name="Reason">What the incidents share (AI, masked).</param>
/// <param name="Confidence">The AI's confidence, 0–100.</param>
/// <param name="Language">Language of <paramref name="Title"/>.</param>
/// <param name="Dimension">What a trend counts.</param>
/// <param name="Category">The shared or trend category.</param>
/// <param name="ConfigurationItem">The trend's configuration item or business service.</param>
/// <param name="WindowStart">Start of the period.</param>
/// <param name="WindowEnd">End of the period.</param>
/// <param name="IncidentCount">Incidents in the group, or the trend's count in the last 30 days.</param>
/// <param name="Baseline">A trend's average per 30 days in the three periods before.</param>
/// <param name="Incidents">The incidents named (at most 100, newest first).</param>
/// <param name="Problem">The problem raised on acceptance.</param>
/// <param name="DecidedBy">Who accepted or dismissed it.</param>
/// <param name="DecidedAt">When.</param>
/// <param name="DismissReason">Why it was dismissed.</param>
/// <param name="Etag">Send as If-Match when deciding.</param>
/// <param name="CreatedAt">When it was suggested.</param>
/// <param name="UpdatedAt">When it last changed.</param>
internal sealed record ProblemSuggestionResponse(
    Guid Id, ProblemSuggestionKind Kind, ProblemSuggestionStatus Status, string? Title, LocalizedTextDto SuggestedTitle, string? Reason, int? Confidence,
    string? Language, ProblemTrendDimension? Dimension, NodeRef? Category, SuggestionItemRef? ConfigurationItem, DateTimeOffset WindowStart,
    DateTimeOffset WindowEnd, int IncidentCount, double? Baseline, IReadOnlyList<SuggestionIncidentResponse> Incidents, SuggestionProblemRef? Problem,
    PersonResponse? DecidedBy, DateTimeOffset? DecidedAt, string? DismissReason, string Etag, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

/// <summary>Problem detection settings with whether the AI feature can be used.</summary>
internal sealed record ProblemDetectionSettingsResponse(
    bool SimilarEnabled, int WindowDays, int MinimumIncidents, bool TrendsEnabled, int TrendMinimum, decimal TrendFactor, bool AiAvailable, string? AiReason);

/// <summary>Accepting a suggested problem; everything is optional.</summary>
public sealed class AcceptProblemSuggestionRequest
{
    /// <summary>The problem's title (default: the suggestion's).</summary>
    [StringLength(Ticket.TitleMaxLength)]
    public string? Title { get; init; }

    /// <summary>The problem's description (default: what the incidents share).</summary>
    [StringLength(Ticket.DescriptionMaxLength)]
    public string? Description { get; init; }

    /// <summary>Category (default: the shared or trend category).</summary>
    public Guid? CategoryId { get; init; }

    /// <summary>Priority 1–5 (default: the incidents' highest).</summary>
    [Range(1, 5)]
    public int? Priority { get; init; }
}

/// <summary>Dismissing a suggested problem.</summary>
public sealed class DismissProblemSuggestionRequest
{
    /// <summary>Why; kept with the suggestion.</summary>
    [StringLength(ProblemSuggestion.ReasonMaxLength)]
    public string? Reason { get; init; }
}

/// <summary>Changes to the problem detection settings.</summary>
public sealed class UpdateProblemDetectionSettingsRequest
{
    /// <summary>Suggest problems from similar incidents.</summary>
    public bool? SimilarEnabled { get; init; }

    /// <summary>Days compared, 30–90.</summary>
    public int? WindowDays { get; init; }

    /// <summary>Fewest similar incidents, 3–100.</summary>
    public int? MinimumIncidents { get; init; }

    /// <summary>Suggest problems from trends.</summary>
    public bool? TrendsEnabled { get; init; }

    /// <summary>Fewest incidents in 30 days, 2–10,000.</summary>
    public int? TrendMinimum { get; init; }

    /// <summary>Times the usual level, 1.1–10.</summary>
    public decimal? TrendFactor { get; init; }
}
