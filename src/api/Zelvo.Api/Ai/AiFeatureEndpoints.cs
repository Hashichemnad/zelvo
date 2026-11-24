using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Zelvo.Ai;
using Zelvo.Ai.Features;
using Zelvo.Api.Catalog;
using Zelvo.Api.Conventions;
using Zelvo.Api.Identity;
using Zelvo.Api.Incidents;
using Zelvo.Api.Integrations;
using Zelvo.Api.Settings;
using Zelvo.Api.Tenancy;
using Zelvo.Data;
using Zelvo.Data.Ai;
using Zelvo.Data.Auditing;
using Zelvo.Data.Localization;
using Zelvo.Data.Tickets;
using Zelvo.Search;

namespace Zelvo.Api.Ai;

/// <summary>
/// The AI features on incidents (module 9, task E5): what the AI found (triage suggestion,
/// sentiment, possible duplicates), the agent's decisions on it, thread summaries, suggested
/// replies and one-click translation. Every AI call goes through the gateway (masked, guarded,
/// logged); nothing changes a ticket until a person decides, except triage when the administrator
/// turned on "apply automatically".
/// </summary>
internal static class AiFeatureEndpoints
{
    /// <summary>"Apply triage automatically to new incidents" as a tenant setting (off by default).</summary>
    public static SettingDefinition<bool> TriageAutoApply { get; } = new(AiFeatureSettingKeys.TriageAutoApply, false, Permissions.AiManage);

    public static RouteGroupBuilder MapAiFeatureEndpoints(this RouteGroupBuilder group)
    {
        var incident = group.MapGroup("/incidents/{id:guid}/ai").WithTags("AI").RequireTenant();

        incident.MapGet("/", InsightsAsync)
            .WithName("GetIncidentAiInsights")
            .WithSummary("What the AI found about an incident: the waiting triage suggestion, the requester's sentiment (frustrated requesters alert the group's team leads), possible duplicates waiting for a decision and the cached thread summary. Needs incidents.read.")
            .RequirePermission(Permissions.IncidentsRead)
            .Produces<AiInsightsResponse>();

        incident.MapPost("/triage", TriageNowAsync)
            .WithName("SuggestIncidentTriage")
            .WithSummary("Asks the AI for a triage suggestion now (new incidents get one on their own while triage is on). Applies it only when the tenant applies triage automatically. Needs incidents.edit.")
            .RequirePermission(Permissions.IncidentsEdit)
            .Produces<TriageSuggestionResponse>()
            .ProducesProblem(StatusCodes.Status409Conflict);

        incident.MapPost("/triage/{suggestionId:guid}/accept", AcceptTriageAsync)
            .WithName("AcceptIncidentTriage")
            // The decision records the person.
            .RequirePerson()
            .WithSummary("Accepts a triage suggestion, as it is or corrected: leave a field out to take the suggestion's value, or give another (a correction, which teaches later suggestions). Sets category, impact and urgency (priority from the tenant's matrix) and group; a group change needs incidents.assign and takes off an assignee who is not a member. Needs incidents.edit; audited.")
            .RequirePermission(Permissions.IncidentsEdit)
            .Produces<TriageSuggestionResponse>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status409Conflict);

        incident.MapPost("/triage/{suggestionId:guid}/dismiss", DismissTriageAsync)
            .WithName("DismissIncidentTriage")
            .RequirePerson()
            .WithSummary("Turns a triage suggestion down; the incident stays as it is. Needs incidents.edit; audited.")
            .RequirePermission(Permissions.IncidentsEdit)
            .Produces<TriageSuggestionResponse>();

        incident.MapPost("/summary", SummaryAsync)
            .WithName("SummarizeIncident")
            .WithSummary("A summary of the incident's thread (public replies and internal notes) in the agent's language, or the one asked for (en, ar). Kept until a comment is added or the incident changes; refresh makes a new one. Needs incidents.read.")
            .RequirePermission(Permissions.IncidentsRead)
            .Produces<AiSummaryResponse>()
            .ProducesProblem(StatusCodes.Status409Conflict);

        incident.MapPost("/reply", ReplyAsync)
            .WithName("SuggestIncidentReply")
            .WithSummary("A suggested public reply in the agent's chosen language (en or ar; default the agent's language), drafted from the conversation and the published knowledge articles the agent may read, with the articles it used. Nothing is sent: the agent edits and sends it. Needs incidents.edit.")
            .RequirePermission(Permissions.IncidentsEdit)
            .Produces<AiReplyResponse>()
            .ProducesProblem(StatusCodes.Status409Conflict);

        incident.MapPost("/translation", TranslateAsync)
            .WithName("TranslateIncidentText")
            .WithSummary("One-click translation (LANG-7) of the incident's title and description, or of one of its comments, into en or ar; Modern Standard Arabic and Gulf dialect are understood (LANG-10). Staff translate everything they see; requesters translate their own incident and its public replies. Cached per language.")
            .RequireAuthorization()
            .Produces<AiTranslationResponse>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status409Conflict);

        incident.MapPost("/duplicates/{candidateId:guid}/link", LinkDuplicateAsync)
            .WithName("LinkIncidentDuplicate")
            .RequirePerson()
            .WithSummary("Confirms a possible duplicate: the incident becomes a child of the other one (resolved with it). Needs incidents.edit; audited.")
            .RequirePermission(Permissions.IncidentsEdit)
            .Produces<DuplicateResponse>()
            .ProducesProblem(StatusCodes.Status409Conflict);

        incident.MapPost("/duplicates/{candidateId:guid}/dismiss", DismissDuplicateAsync)
            .WithName("DismissIncidentDuplicate")
            .RequirePerson()
            .WithSummary("Says a possible duplicate is not one. Needs incidents.edit; audited.")
            .RequirePermission(Permissions.IncidentsEdit)
            .Produces<DuplicateResponse>();

        var ai = group.MapGroup("/ai").WithTags("AI").RequireTenant();
        ai.MapPost("/translate", TranslateDraftAsync)
            .WithName("TranslateDraft")
            .WithSummary("Translates text that is not saved yet, such as a reply the agent is writing, into en or ar (LANG-7), so the requester receives it in their language. Not cached. Needs incidents.edit or chat.handle.")
            .RequireAuthorization()
            .Produces<AiTranslationResponse>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status409Conflict);

        ai.MapGet("/triage/settings", TriageSettingsAsync)
            .WithName("GetAiTriageSettings")
            .WithSummary("Whether triage applies automatically to new incidents (the rule an administrator approves; off by default). Needs ai.manage.")
            .RequirePermission(Permissions.AiManage)
            .Produces<TriageSettingsResponse>();

        ai.MapPut("/triage/settings", SetTriageSettingsAsync)
            .WithName("SetAiTriageSettings")
            .WithSummary("Turns automatic triage of new incidents on or off. When on, the AI fills in the category when none was chosen, impact and urgency, and the group unless someone took the incident; agents can still correct it. Needs ai.manage; audited.")
            .RequirePermission(Permissions.AiManage)
            .Produces<TriageSettingsResponse>()
            .ProducesValidationProblem();

        ai.MapGet("/triage/accuracy", TriageAccuracyAsync)
            .WithName("GetAiTriageAccuracy")
            .WithSummary("How often agents took triage suggestions as they were, over the last days (default 30, up to 365): suggestions accepted, corrected, dismissed and applied automatically, and per field how often the suggestion was kept. Needs ai.manage.")
            .RequirePermission(Permissions.AiManage)
            .Produces<TriageAccuracyResponse>();

        return group;
    }

    internal static async Task<IResult> InsightsAsync(Guid id, ZelvoDbContext db, IAiGateway gateway, CancellationToken cancellationToken)
    {
        if (!await db.Tickets.AnyAsync(t => t.Id == id && t.Type == TicketType.Incident, cancellationToken))
        {
            return IncidentEndpoints.NotFound();
        }

        var triage = await db.AiTriageSuggestions.AsNoTracking().Where(s => s.TicketId == id)
            .OrderByDescending(s => s.CreatedAt).FirstOrDefaultAsync(cancellationToken);
        var insight = await db.AiTicketInsights.AsNoTracking().SingleOrDefaultAsync(i => i.TicketId == id, cancellationToken);
        var duplicates = await DuplicatesAsync(db, id, cancellationToken);
        var available = (await gateway.AvailabilityAsync(cancellationToken)).Where(a => a.Available).Select(a => a.Feature).ToList();
        return TypedResults.Ok(new AiInsightsResponse(
            available,
            triage is null ? null : await TriageResponseAsync(db, triage, cancellationToken),
            insight?.Sentiment, insight?.SentimentReason, insight?.SentimentAt, insight?.FrustratedSince,
            duplicates,
            insight?.Summary is { } summary ? new AiSummaryResponse(summary, insight.SummaryLanguage!, insight.SummaryAt!.Value, false) : null));
    }

    internal static async Task<IResult> TriageNowAsync(Guid id, ZelvoDbContext db, TicketTriage triage, CancellationToken cancellationToken)
    {
        var ticket = await db.Tickets.AsNoTracking().SingleOrDefaultAsync(t => t.Id == id && t.Type == TicketType.Incident, cancellationToken);
        if (ticket is null)
        {
            return IncidentEndpoints.NotFound();
        }

        if (ticket.State is TicketState.Resolved or TicketState.Closed)
        {
            return ApiProblem.Create(StatusCodes.Status409Conflict, "ticket-not-open", "Only open incidents are triaged.");
        }

        var suggestion = await triage.SuggestAsync(id, cancellationToken);
        return suggestion is null
            ? AiProblem(triage.LastCall)
            : TypedResults.Ok(await TriageResponseAsync(db, suggestion, cancellationToken));
    }

    internal static async Task<IResult> AcceptTriageAsync(
        Guid id, Guid suggestionId, AcceptTriageRequest request, ZelvoDbContext db, CurrentUser user, TicketTriage triage, CancellationToken cancellationToken)
    {
        var (suggestion, ticket, problem) = await PendingAsync(db, id, suggestionId, cancellationToken);
        if (problem is not null)
        {
            return problem;
        }

        var choice = new TriageChoice(
            request.CategoryId ?? suggestion!.CategoryId, request.Impact ?? suggestion!.Impact, request.Urgency ?? suggestion!.Urgency, request.GroupId ?? suggestion!.GroupId);
        // Field rules (task A2) apply to the AI's values like to any change.
        var fields = new List<string>();
        if (choice.CategoryId is not null && choice.CategoryId != ticket!.CategoryId) fields.Add("category");
        if (choice.Impact is not null && choice.Impact != ticket!.Incident!.Impact) fields.Add("impact");
        if (choice.Urgency is not null && choice.Urgency != ticket!.Incident!.Urgency) fields.Add("urgency");
        if (FieldFilter.NotEditable(user, PermissionCatalog.Incidents, fields) is { Count: > 0 } refused)
        {
            return ApiProblem.Create(StatusCodes.Status403Forbidden, "fields-read-only", $"You cannot change: {string.Join(", ", refused)}.");
        }

        if (choice.GroupId is { } group && group != ticket!.GroupId && !user.Has(Permissions.IncidentsAssign))
        {
            return ApiProblem.Create(StatusCodes.Status403Forbidden, "forbidden", "Changing the group needs incidents.assign; accept without the group or ask a colleague.");
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        switch (await triage.DecideAsync(suggestion!, ticket!, choice, user.Account!.Id, cancellationToken))
        {
            case TriageProblem.Category:
                return UserEndpoints.Validation("categoryId", "Choose an active category.");
            case TriageProblem.Group:
                return UserEndpoints.Validation("groupId", "Choose an active group.");
            case TriageProblem.Level:
                return UserEndpoints.Validation(request.Impact is { } i && !Zelvo.Tickets.PriorityMatrix.IsLevel(i) ? "impact" : "urgency", "Use a level from 1 to 3.");
            case TriageProblem.Closed:
                return ApiProblem.Create(StatusCodes.Status409Conflict, "ticket-not-open", "Only open incidents are triaged.");
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return ApiProblem.Create(StatusCodes.Status409Conflict, "version-mismatch", "The incident changed meanwhile. Reload it and decide again.");
        }

        return TypedResults.Ok(await TriageResponseAsync(db, suggestion!, cancellationToken));
    }

    internal static async Task<IResult> DismissTriageAsync(Guid id, Guid suggestionId, ZelvoDbContext db, CurrentUser user, TicketTriage triage, CancellationToken cancellationToken)
    {
        var (suggestion, _, problem) = await PendingAsync(db, id, suggestionId, cancellationToken);
        if (problem is not null)
        {
            return problem;
        }

        triage.Dismiss(suggestion!, user.Account!.Id);
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Ok(await TriageResponseAsync(db, suggestion!, cancellationToken));
    }

    internal static async Task<IResult> SummaryAsync(
        Guid id, AiSummaryRequest? request, ZelvoDbContext db, CurrentUser user, TenantSettingsStore settings, AgentAssist assist, CancellationToken cancellationToken)
    {
        if (!await db.Tickets.AnyAsync(t => t.Id == id && t.Type == TicketType.Incident, cancellationToken))
        {
            return IncidentEndpoints.NotFound();
        }

        var language = await LanguageAsync(request?.Language, user, settings, cancellationToken);
        var outcome = await assist.SummarizeAsync(id, language, request?.Refresh ?? false, cancellationToken);
        if (outcome.Value is not { } insight)
        {
            return AiProblem(outcome.ErrorCode, outcome.Error);
        }

        return TypedResults.Ok(new AiSummaryResponse(insight.Summary!, insight.SummaryLanguage!, insight.SummaryAt!.Value, false));
    }

    internal static async Task<IResult> ReplyAsync(
        Guid id, AiLanguageRequest? request, ZelvoDbContext db, CurrentUser user, TenantSettingsStore settings, AgentAssist assist, CancellationToken cancellationToken)
    {
        var ticket = await db.Tickets.AsNoTracking().SingleOrDefaultAsync(t => t.Id == id && t.Type == TicketType.Incident, cancellationToken);
        if (ticket is null)
        {
            return IncidentEndpoints.NotFound();
        }

        if (ticket.State == TicketState.Closed)
        {
            return IncidentEndpoints.Closed();
        }

        var language = await LanguageAsync(request?.Language, user, settings, cancellationToken);
        var readers = (await CatalogScope.OfAsync(db, user, cancellationToken)).Readers;
        var outcome = await assist.DraftReplyAsync(id, language, readers, cancellationToken);
        return outcome.Value is { } draft
            ? TypedResults.Ok(new AiReplyResponse(draft.Text, draft.Language, [.. draft.Articles.Select(a => new AiArticleRef(a.Id, a.Number, a.Title))]))
            : AiProblem(outcome.ErrorCode, outcome.Error);
    }

    internal static async Task<IResult> TranslateAsync(
        Guid id, AiTranslationRequest request, ZelvoDbContext db, CurrentUser user, AgentAssist assist, CancellationToken cancellationToken)
    {
        if (!LocalizationSettings.IsLanguage(request.Language))
        {
            return UserEndpoints.Validation("language", "Translate into en or ar.");
        }

        var ticket = await IncidentEndpoints.FindVisibleAsync(db, user, id, cancellationToken);
        if (ticket is null)
        {
            return IncidentEndpoints.NotFound();
        }

        var staff = user.Has(Permissions.IncidentsRead);
        TranslationSource source;
        Guid sourceId;
        string text;
        if (request.CommentId is { } commentId)
        {
            var comment = await db.TicketComments.AsNoTracking()
                .SingleOrDefaultAsync(c => c.Id == commentId && c.TicketId == id && (staff || c.Visibility == CommentVisibility.Public), cancellationToken);
            if (comment is null)
            {
                return ApiProblem.Create(StatusCodes.Status404NotFound, "comment-not-found", "The comment does not exist.");
            }

            (source, sourceId, text) = (TranslationSource.Comment, comment.Id, comment.Body);
        }
        else
        {
            (source, sourceId, text) = (TranslationSource.Ticket, ticket.Id, $"{ticket.Title}\n\n{ticket.Description}".TrimEnd());
        }

        var outcome = await assist.TranslateAsync(source, sourceId, text, request.Language!, AgentAssist.People(ticket.Requester, ticket.Assignee), cancellationToken);
        if (outcome.Value is not { } translation)
        {
            return AiProblem(outcome.ErrorCode, outcome.Error);
        }

        return TypedResults.Ok(new AiTranslationResponse(translation.Text, translation.Language, translation.Cached));
    }

    internal static async Task<IResult> TranslateDraftAsync(AiDraftTranslationRequest request, ZelvoDbContext db, CurrentUser user, AgentAssist assist, CancellationToken cancellationToken)
    {
        if (!user.Has(Permissions.IncidentsEdit) && !user.Has(Permissions.ChatHandle))
        {
            return ApiProblem.Create(StatusCodes.Status403Forbidden, "forbidden", "Translating drafts needs incidents.edit or chat.handle.");
        }

        if (!LocalizationSettings.IsLanguage(request.Language))
        {
            return UserEndpoints.Validation("language", "Translate into en or ar.");
        }

        var text = request.Text?.Trim() ?? string.Empty;
        if (text.Length is 0 or > TicketComment.BodyMaxLength)
        {
            return UserEndpoints.Validation("text", $"Give the text to translate (at most {TicketComment.BodyMaxLength} characters).");
        }

        IReadOnlyCollection<string> people = [];
        string entityType = "draft";
        string? entityId = null;
        if (request.IncidentId is { } incidentId && await IncidentEndpoints.FindVisibleAsync(db, user, incidentId, cancellationToken) is { } ticket)
        {
            people = AgentAssist.People(ticket.Requester, ticket.Assignee);
            (entityType, entityId) = ("ticket", ticket.Id.ToString());
        }

        var outcome = await assist.TranslateTextAsync(text, request.Language!, people, entityType, entityId, cancellationToken);
        return outcome.Value is { } translation
            ? TypedResults.Ok(new AiTranslationResponse(translation.Text, translation.Language, false))
            : AiProblem(outcome.ErrorCode, outcome.Error);
    }

    internal static async Task<IResult> LinkDuplicateAsync(
        Guid id, Guid candidateId, HttpContext http, ZelvoDbContext db, CurrentUser user, ISearchIndexer indexer, IAuditLog audit, TimeProvider time,
        CancellationToken cancellationToken)
    {
        var candidate = await db.AiDuplicateCandidates.SingleOrDefaultAsync(c => c.Id == candidateId && c.TicketId == id, cancellationToken);
        if (candidate is null || candidate.Status != DuplicateStatus.Pending)
        {
            return ApiProblem.Create(StatusCodes.Status404NotFound, "duplicate-not-found", "There is no such possible duplicate waiting for a decision.");
        }

        candidate.Status = DuplicateStatus.Linked;
        candidate.DecidedById = user.Account!.Id;
        candidate.DecidedAt = time.GetUtcNow();
        audit.Add(AuditCategory.Ai, "ai.duplicate.linked", "ticket", id.ToString(),
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["parentId"] = candidate.CandidateId.ToString(), ["confidence"] = candidate.Confidence });
        // The same checks and save as "set parent", in one transaction with the decision.
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var result = await IncidentEndpoints.SetParentAsync(id, new SetIncidentParentRequest { ParentId = candidate.CandidateId }, http, db, user, indexer, cancellationToken);
        if (result.Result is not Ok<JsonObject>)
        {
            return result.Result;
        }

        await transaction.CommitAsync(cancellationToken);
        return TypedResults.Ok(await DuplicateResponseAsync(db, candidate, cancellationToken));
    }

    internal static async Task<IResult> DismissDuplicateAsync(Guid id, Guid candidateId, ZelvoDbContext db, CurrentUser user, IAuditLog audit, TimeProvider time, CancellationToken cancellationToken)
    {
        var candidate = await db.AiDuplicateCandidates.SingleOrDefaultAsync(c => c.Id == candidateId && c.TicketId == id, cancellationToken);
        if (candidate is null || candidate.Status != DuplicateStatus.Pending)
        {
            return ApiProblem.Create(StatusCodes.Status404NotFound, "duplicate-not-found", "There is no such possible duplicate waiting for a decision.");
        }

        candidate.Status = DuplicateStatus.Dismissed;
        candidate.DecidedById = user.Account!.Id;
        candidate.DecidedAt = time.GetUtcNow();
        audit.Add(AuditCategory.Ai, "ai.duplicate.dismissed", "ticket", id.ToString(),
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["otherId"] = candidate.CandidateId.ToString() });
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Ok(await DuplicateResponseAsync(db, candidate, cancellationToken));
    }

    internal static async Task<IResult> TriageSettingsAsync(TenantSettingsStore settings, CancellationToken cancellationToken) =>
        TypedResults.Ok(new TriageSettingsResponse(await settings.GetAsync(TriageAutoApply, cancellationToken)));

    internal static async Task<IResult> SetTriageSettingsAsync(TriageSettingsRequest request, ZelvoDbContext db, TenantSettingsStore settings, CancellationToken cancellationToken)
    {
        if (request.AutoApply is not { } autoApply)
        {
            return UserEndpoints.Validation("autoApply", "Say whether triage applies automatically.");
        }

        await settings.SetAsync(TriageAutoApply, autoApply, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Ok(new TriageSettingsResponse(autoApply));
    }

    internal static async Task<IResult> TriageAccuracyAsync(int? days, ZelvoDbContext db, TimeProvider time, CancellationToken cancellationToken)
    {
        var period = Math.Clamp(days ?? 30, 1, 365);
        var since = time.GetUtcNow().AddDays(-period);
        var rows = await db.AiTriageSuggestions.AsNoTracking().Where(s => s.CreatedAt >= since)
            .Select(s => new { s.Status, s.CategoryId, s.FinalCategoryId, s.GroupId, s.FinalGroupId, s.Impact, s.FinalImpact, s.Urgency, s.FinalUrgency })
            .ToListAsync(cancellationToken);
        var decided = rows.Where(r => r.Status is TriageStatus.Accepted or TriageStatus.Corrected).ToList();
        return TypedResults.Ok(new TriageAccuracyResponse(
            period,
            rows.Count,
            rows.Count(r => r.Status == TriageStatus.Pending),
            rows.Count(r => r.Status == TriageStatus.Accepted),
            rows.Count(r => r.Status == TriageStatus.Corrected),
            rows.Count(r => r.Status == TriageStatus.Dismissed),
            rows.Count(r => r.Status == TriageStatus.AutoApplied),
            decided.Count == 0 ? null : (int)Math.Round(100.0 * decided.Count(r => r.CategoryId == r.FinalCategoryId) / decided.Count),
            decided.Count == 0 ? null : (int)Math.Round(100.0 * decided.Count(r => r.GroupId == r.FinalGroupId) / decided.Count),
            decided.Count == 0 ? null : (int)Math.Round(100.0 * decided.Count(r => r.Impact == r.FinalImpact && r.Urgency == r.FinalUrgency) / decided.Count)));
    }

    // --- Helpers ---

    private static async Task<(AiTriageSuggestion? Suggestion, Ticket? Ticket, IResult? Problem)> PendingAsync(
        ZelvoDbContext db, Guid ticketId, Guid suggestionId, CancellationToken cancellationToken)
    {
        var suggestion = await db.AiTriageSuggestions.SingleOrDefaultAsync(s => s.Id == suggestionId && s.TicketId == ticketId, cancellationToken);
        if (suggestion is null || suggestion.Status != TriageStatus.Pending)
        {
            return (null, null, ApiProblem.Create(StatusCodes.Status404NotFound, "suggestion-not-found", "There is no such triage suggestion waiting for a decision."));
        }

        var ticket = await IncidentEndpoints.Incidents(db).SingleOrDefaultAsync(t => t.Id == ticketId, cancellationToken);
        return ticket is null ? (null, null, IncidentEndpoints.NotFound()) : (suggestion, ticket, null);
    }

    /// <summary>The language an agent works in: the one asked for, else their profile's, else the tenant default.</summary>
    internal static async Task<string> LanguageAsync(string? asked, CurrentUser user, TenantSettingsStore settings, CancellationToken cancellationToken) =>
        LocalizationSettings.IsLanguage(asked) ? asked!
        : LocalizationSettings.IsLanguage(user.Account?.PreferredLanguage) ? user.Account!.PreferredLanguage!
        : await settings.GetAsync(LocalizationSettings.DefaultLanguage, cancellationToken);

    /// <summary>Why the AI gave no answer, as a problem: 409 <c>ai-off</c> or <c>limit-reached</c>, 502 for provider errors.</summary>
    internal static IResult AiProblem(string? code, string? message) => code switch
    {
        "ai-off" => ApiProblem.Create(StatusCodes.Status409Conflict, "ai-off", "This AI feature is off. An administrator turns it on in Settings → AI."),
        "limit-reached" => ApiProblem.Create(StatusCodes.Status409Conflict, "limit-reached", "The monthly AI limit is reached. AI starts again next month or when an administrator raises the limit."),
        "not-found" => IncidentEndpoints.NotFound(),
        _ => ApiProblem.Create(StatusCodes.Status502BadGateway, code ?? "ai-failed", message ?? "The AI provider gave no usable answer. Try again."),
    };

    private static IResult AiProblem(AiResult? call) => call is null
        ? ApiProblem.Create(StatusCodes.Status409Conflict, "ticket-not-open", "Only open incidents are triaged.")
        : AiProblem(call.ErrorCode, call.Error);

    private static async Task<TriageSuggestionResponse> TriageResponseAsync(ZelvoDbContext db, AiTriageSuggestion s, CancellationToken cancellationToken)
    {
        var categoryIds = new[] { s.CategoryId, s.FinalCategoryId }.OfType<Guid>().ToList();
        var groupIds = new[] { s.GroupId, s.FinalGroupId }.OfType<Guid>().ToList();
        var categories = await db.Categories.AsNoTracking().Where(c => categoryIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => LocalizedTextDto.From(c.Name), cancellationToken);
        var groups = await db.SupportGroups.AsNoTracking().Where(g => groupIds.Contains(g.Id)).ToDictionaryAsync(g => g.Id, g => LocalizedTextDto.From(g.Name), cancellationToken);
        AiNamedRef? Ref(Dictionary<Guid, LocalizedTextDto> names, Guid? id) => id is { } value && names.TryGetValue(value, out var name) ? new AiNamedRef(value, name) : null;
        return new TriageSuggestionResponse(s.Id, s.Status, Ref(categories, s.CategoryId), s.Impact, s.Urgency, Ref(groups, s.GroupId), s.Confidence, s.Reason, s.CreatedAt, s.DecidedAt,
            s.DecidedAt is null ? null : new TriageFinalResponse(Ref(categories, s.FinalCategoryId), s.FinalImpact, s.FinalUrgency, Ref(groups, s.FinalGroupId)));
    }

    private static async Task<List<DuplicateResponse>> DuplicatesAsync(ZelvoDbContext db, Guid ticketId, CancellationToken cancellationToken)
    {
        var rows = await db.AiDuplicateCandidates.AsNoTracking().Where(c => c.TicketId == ticketId && c.Status == DuplicateStatus.Pending)
            .OrderByDescending(c => c.Confidence).ToListAsync(cancellationToken);
        var result = new List<DuplicateResponse>();
        foreach (var row in rows)
        {
            result.Add(await DuplicateResponseAsync(db, row, cancellationToken));
        }

        return result;
    }

    private static async Task<DuplicateResponse> DuplicateResponseAsync(ZelvoDbContext db, AiDuplicateCandidate c, CancellationToken cancellationToken)
    {
        var other = await db.Tickets.AsNoTracking().Where(t => t.Id == c.CandidateId)
            .Select(t => new TicketLinkResponse(t.Id, t.Number, t.Title, t.State)).SingleAsync(cancellationToken);
        return new DuplicateResponse(c.Id, other, c.Confidence, c.Reason, c.Status, c.CreatedAt);
    }
}

/// <summary>A category or group the AI named.</summary>
internal sealed record AiNamedRef(Guid Id, LocalizedTextDto Name);

/// <summary>What the incident finally got.</summary>
internal sealed record TriageFinalResponse(AiNamedRef? Category, int? Impact, int? Urgency, AiNamedRef? Group);

/// <summary>A triage suggestion.</summary>
/// <param name="Id">The suggestion.</param>
/// <param name="Status">Pending, Accepted, Corrected, Dismissed, AutoApplied or Outdated.</param>
/// <param name="Category">Suggested category.</param>
/// <param name="Impact">Suggested impact (1 highest).</param>
/// <param name="Urgency">Suggested urgency (1 highest).</param>
/// <param name="Group">Suggested group.</param>
/// <param name="Confidence">How sure the AI was, 0–100.</param>
/// <param name="Reason">The AI's short explanation (English).</param>
/// <param name="CreatedAt">When it was made.</param>
/// <param name="DecidedAt">When it was decided.</param>
/// <param name="Final">What the incident got, once decided.</param>
internal sealed record TriageSuggestionResponse(
    Guid Id, TriageStatus Status, AiNamedRef? Category, int? Impact, int? Urgency, AiNamedRef? Group, int Confidence, string? Reason,
    DateTimeOffset CreatedAt, DateTimeOffset? DecidedAt, TriageFinalResponse? Final);

/// <summary>A possible duplicate.</summary>
internal sealed record DuplicateResponse(Guid Id, TicketLinkResponse Incident, int Confidence, string? Reason, DuplicateStatus Status, DateTimeOffset CreatedAt);

/// <summary>A thread summary.</summary>
/// <param name="Text">Bullet points.</param>
/// <param name="Language">Its language.</param>
/// <param name="CreatedAt">When it was made.</param>
/// <param name="Stale">Made before the latest change (only in lists).</param>
internal sealed record AiSummaryResponse(string Text, string Language, DateTimeOffset CreatedAt, bool Stale);

/// <summary>What the AI found about an incident.</summary>
/// <param name="Available">AI features that can be used now.</param>
/// <param name="Triage">The latest triage suggestion.</param>
/// <param name="Sentiment">The requester's sentiment in their latest message read.</param>
/// <param name="SentimentReason">Why (English).</param>
/// <param name="SentimentAt">When it was read.</param>
/// <param name="FrustratedSince">Since when the requester sounds frustrated.</param>
/// <param name="Duplicates">Possible duplicates waiting for a decision.</param>
/// <param name="Summary">The cached thread summary.</param>
internal sealed record AiInsightsResponse(
    IReadOnlyList<string> Available, TriageSuggestionResponse? Triage, AiSentiment? Sentiment, string? SentimentReason, DateTimeOffset? SentimentAt,
    DateTimeOffset? FrustratedSince, IReadOnlyList<DuplicateResponse> Duplicates, AiSummaryResponse? Summary);

/// <summary>A knowledge article a suggestion used.</summary>
internal sealed record AiArticleRef(Guid Id, string Number, string Title);

/// <summary>A suggested reply.</summary>
internal sealed record AiReplyResponse(string Text, string Language, IReadOnlyList<AiArticleRef> Articles);

/// <summary>A translation.</summary>
/// <param name="Text">Translated text.</param>
/// <param name="Language">Language translated into.</param>
/// <param name="Cached">Taken from an earlier translation.</param>
internal sealed record AiTranslationResponse(string Text, string Language, bool Cached);

/// <summary>Triage settings.</summary>
/// <param name="AutoApply">Triage applies automatically to new incidents.</param>
internal sealed record TriageSettingsResponse(bool AutoApply);

/// <summary>How agents received triage suggestions.</summary>
/// <param name="Days">Period.</param>
/// <param name="Total">Suggestions made.</param>
/// <param name="Pending">Waiting.</param>
/// <param name="Accepted">Taken as they were.</param>
/// <param name="Corrected">Taken with changes.</param>
/// <param name="Dismissed">Turned down.</param>
/// <param name="AutoApplied">Applied by the automatic rule.</param>
/// <param name="CategoryKeptPercent">Of accepted and corrected ones, how often the category was kept.</param>
/// <param name="GroupKeptPercent">How often the group was kept.</param>
/// <param name="PriorityKeptPercent">How often impact and urgency were both kept.</param>
internal sealed record TriageAccuracyResponse(
    int Days, int Total, int Pending, int Accepted, int Corrected, int Dismissed, int AutoApplied,
    int? CategoryKeptPercent, int? GroupKeptPercent, int? PriorityKeptPercent);

/// <summary>Accepting a triage suggestion; a field left out takes the suggestion's value.</summary>
internal sealed class AcceptTriageRequest
{
    /// <summary>Category.</summary>
    public Guid? CategoryId { get; init; }

    /// <summary>Impact 1–3.</summary>
    public int? Impact { get; init; }

    /// <summary>Urgency 1–3.</summary>
    public int? Urgency { get; init; }

    /// <summary>Group.</summary>
    public Guid? GroupId { get; init; }
}

/// <summary>A language to work in.</summary>
internal class AiLanguageRequest
{
    /// <summary><c>en</c> or <c>ar</c>; default the agent's language.</summary>
    public string? Language { get; init; }
}

/// <summary>A summary request.</summary>
internal sealed class AiSummaryRequest : AiLanguageRequest
{
    /// <summary>Make a new summary even if the cached one is current.</summary>
    public bool? Refresh { get; init; }
}

/// <summary>What to translate.</summary>
internal sealed class AiTranslationRequest
{
    /// <summary><c>en</c> or <c>ar</c>.</summary>
    public string? Language { get; init; }

    /// <summary>A comment of the incident; empty for its title and description.</summary>
    public Guid? CommentId { get; init; }
}

/// <summary>A draft to translate.</summary>
internal sealed class AiDraftTranslationRequest
{
    /// <summary>The text.</summary>
    public string? Text { get; init; }

    /// <summary><c>en</c> or <c>ar</c>.</summary>
    public string? Language { get; init; }

    /// <summary>The incident it is for, if any (its people are masked).</summary>
    public Guid? IncidentId { get; init; }
}

/// <summary>Triage settings.</summary>
internal sealed class TriageSettingsRequest
{
    /// <summary>Apply triage automatically to new incidents.</summary>
    public bool? AutoApply { get; init; }
}
