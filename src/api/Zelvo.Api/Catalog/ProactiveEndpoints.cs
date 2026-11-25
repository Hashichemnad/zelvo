using System.ComponentModel.DataAnnotations;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Zelvo.Api.Conventions;
using Zelvo.Api.Forms;
using Zelvo.Api.Identity;
using Zelvo.Api.Incidents;
using Zelvo.Api.Integrations;
using Zelvo.Api.Tenancy;
using Zelvo.Data;
using Zelvo.Data.Auditing;
using Zelvo.Data.Catalog;
using Zelvo.Data.Identity;
using Zelvo.Data.Localization;

namespace Zelvo.Api.Catalog;

/// <summary>
/// Proactive services through the API (task C5): other systems send dates and events
/// (<c>proactive.trigger</c>), people see and decline the offers made to them, and catalog
/// managers follow the runs.
/// </summary>
internal static class ProactiveEndpoints
{
    public static RouteGroupBuilder MapProactiveEndpoints(this RouteGroupBuilder group)
    {
        var proactive = group.MapGroup("/proactive").WithTags("Proactive services").RequireTenant();
        proactive.MapPost("/events", EventAsync).WithName("SendProactiveEvent")
            .WithSummary("Another system tells Zelvo that a proactive service is due for a person: an Event trigger starts now; a Date trigger takes the date (yyyy-MM-dd) and its offset. The person is named by id or email and must be an active user. The reference (for example the HR system's record id) makes the call idempotent: the same reference never starts the service twice. Answers fill in the service's form. Needs proactive.trigger; audited.")
            .RequirePermission(Permissions.ProactiveTrigger).Produces<ProactiveRunResponse>(StatusCodes.Status201Created).Produces<ProactiveRunResponse>().ProducesValidationProblem();
        proactive.MapGet("/runs", RunsAsync).WithName("ListProactiveRuns")
            .WithSummary("Times proactive services were due for people, newest first, with what happened (offered, started, accepted, declined, expired, failed and why). Needs catalog.manage.")
            .RequirePermission(Permissions.CatalogManage).Produces<PageResult<ProactiveRunResponse>>();

        var offers = group.MapGroup("/me/offers").WithTags("Proactive services").RequireTenant();
        offers.MapGet("/", OffersAsync).WithName("ListMyOffers")
            .WithSummary("Services Zelvo offers the caller (proactive offers still open), soonest to expire first.")
            .RequirePerson().RequireAuthorization().Produces<IReadOnlyList<OfferResponse>>();
        offers.MapGet("/{id:guid}", OfferAsync).WithName("GetMyOffer")
            .WithSummary("One open offer with the answers Zelvo fills in; request the service with offerId to accept it.")
            .RequirePerson().RequireAuthorization().Produces<OfferResponse>();
        offers.MapPost("/{id:guid}/decline", DeclineAsync).WithName("DeclineMyOffer")
            .WithSummary("Decline an open offer; audited.")
            .RequirePerson().RequireAuthorization().Produces<OfferResponse>();
        return group;
    }

    internal static async Task<IResult> EventAsync(
        ProactiveEventRequest request, ZelvoDbContext db, CurrentUser user, ProactiveEngine engine, IAuditLog audit, CancellationToken cancellationToken)
    {
        var key = request.Service!.Trim();
        var code = key.ToUpper(System.Globalization.CultureInfo.InvariantCulture);
        var service = Guid.TryParse(key, out var serviceId)
            ? await db.CatalogServices.AsNoTracking().SingleOrDefaultAsync(s => s.Id == serviceId, cancellationToken)
            : await db.CatalogServices.AsNoTracking().SingleOrDefaultAsync(s => s.Code == code, cancellationToken);
        if (service is not { PublishedVersionId: { } versionId } || !CatalogAccess.IsListed(service.State))
        {
            return CatalogBrowseEndpoints.NotFound();
        }

        var version = await db.CatalogServiceVersions.AsNoTracking().SingleAsync(v => v.Id == versionId, cancellationToken);
        var published = new PublishedService(service, version, ServiceCards.Read(version.Content));
        var trigger = published.Card.Proactive.Enabled ? published.Card.Proactive.Triggers.FirstOrDefault(t => t.Key == request.Trigger) : null;
        if (trigger is null || trigger.Kind == ProactiveTriggerKind.Schedule)
        {
            return UserEndpoints.Validation("trigger", "The service has no date or event trigger with this key.");
        }

        if (trigger.Kind == ProactiveTriggerKind.Date && request.Date is null)
        {
            return UserEndpoints.Validation("date", "A date trigger needs the date.");
        }

        // Exact email match, ignoring case (ILIKE with its wildcards escaped).
        var email = request.PersonEmail?.Trim().Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal);
        var person = request.PersonId is { } personId
            ? await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == personId && u.Status == UserStatus.Active, cancellationToken)
            : email is { Length: > 0 }
                ? await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Email != null && EF.Functions.ILike(u.Email, email) && u.Status == UserStatus.Active, cancellationToken)
                : null;
        if (person is null)
        {
            return UserEndpoints.Validation("person", "Name an active user of this organisation by personId or personEmail.");
        }

        var now = DateTimeOffset.UtcNow;
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var (run, created) = await engine.RecordAsync(published, trigger, person, request.Reference!.Trim(), request.Date, request.Answers, user.ApiClient?.Id, now, cancellationToken);
        if (created)
        {
            await audit.RecordAsync(AuditCategory.Change, "catalog.proactive.event", "proactive-run", run.Id.ToString(), new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["service"] = service.Code,
                ["trigger"] = trigger.Key,
                ["personId"] = person.Id.ToString(),
                ["state"] = run.State.ToString(),
            }, cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        var response = (await ResponsesAsync(db, [run], cancellationToken)).Single();
        return created ? TypedResults.Created($"/api/v1/proactive/runs/{run.Id}", response) : TypedResults.Ok(response);
    }

    internal static async Task<IResult> RunsAsync([AsParameters] PageQuery page, Guid? serviceId, ProactiveRunState? state, ZelvoDbContext db, CancellationToken cancellationToken)
    {
        var runs = db.ProactiveRuns.AsNoTracking();
        if (serviceId is { } id)
        {
            runs = runs.Where(r => r.ServiceId == id);
        }

        if (state is { } s)
        {
            runs = runs.Where(r => r.State == s);
        }

        var ordered = runs.OrderByDescending(r => r.CreatedAt).ThenByDescending(r => r.Id);
        var total = await ordered.LongCountAsync(cancellationToken);
        var rows = await ordered.Skip(page.Skip).Take(page.Size).ToListAsync(cancellationToken);
        return TypedResults.Ok(new PageResult<ProactiveRunResponse>(await ResponsesAsync(db, rows, cancellationToken), page.Number, page.Size, total));
    }

    internal static async Task<IResult> OffersAsync(ZelvoDbContext db, CurrentUser user, CancellationToken cancellationToken)
    {
        var me = user.Account!.Id;
        var now = DateTimeOffset.UtcNow;
        var rows = await db.ProactiveRuns.AsNoTracking().Where(r => r.PersonId == me && r.State == ProactiveRunState.Offered && r.ExpiresAt > now)
            .OrderBy(r => r.ExpiresAt).Take(50).ToListAsync(cancellationToken);
        return TypedResults.Ok(await OfferResponsesAsync(db, user, rows, withAnswers: false, cancellationToken));
    }

    internal static async Task<IResult> OfferAsync(Guid id, ZelvoDbContext db, CurrentUser user, CancellationToken cancellationToken)
    {
        var run = await OpenOfferAsync(db, user, id, tracked: false, cancellationToken);
        return run is null ? FormsCommon.NotFound("offer") : TypedResults.Ok((await OfferResponsesAsync(db, user, [run], withAnswers: true, cancellationToken)).Single());
    }

    internal static async Task<IResult> DeclineAsync(Guid id, ZelvoDbContext db, CurrentUser user, IAuditLog audit, CancellationToken cancellationToken)
    {
        var run = await OpenOfferAsync(db, user, id, tracked: true, cancellationToken);
        if (run is null)
        {
            return FormsCommon.NotFound("offer");
        }

        run.State = ProactiveRunState.Declined;
        run.EndedAt = DateTimeOffset.UtcNow;
        audit.Add(AuditCategory.Change, "catalog.proactive.declined", "proactive-run", run.Id.ToString(),
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["serviceId"] = run.ServiceId.ToString() });
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Ok((await OfferResponsesAsync(db, user, [run], withAnswers: false, cancellationToken)).Single());
    }

    private static async Task<ProactiveRun?> OpenOfferAsync(ZelvoDbContext db, CurrentUser user, Guid id, bool tracked, CancellationToken cancellationToken)
    {
        var me = user.Account!.Id;
        var query = tracked ? db.ProactiveRuns : db.ProactiveRuns.AsNoTracking();
        var run = await query.SingleOrDefaultAsync(r => r.Id == id && r.PersonId == me, cancellationToken);
        return run is { State: ProactiveRunState.Offered } && run.ExpiresAt > DateTimeOffset.UtcNow ? run : null;
    }

    private static async Task<List<OfferResponse>> OfferResponsesAsync(ZelvoDbContext db, CurrentUser user, IReadOnlyList<ProactiveRun> runs, bool withAnswers, CancellationToken cancellationToken)
    {
        var scope = await CatalogScope.OfAsync(db, user, cancellationToken);
        var result = new List<OfferResponse>();
        foreach (var run in runs)
        {
            // Hidden services stay hidden, also in offers made before access changed.
            if (await PublishedServices.FindAsync(db, scope, run.ServiceId, cancellationToken) is not { } service)
            {
                continue;
            }

            result.Add(new OfferResponse(
                run.Id, new ServiceRefResponse(service.Service.Id, service.Service.Code, LocalizedTextDto.From(service.Card.Name)), LocalizedTextDto.From(service.Card.Summary),
                run.State, run.OfferedAt, run.ExpiresAt, service.Card.Kind,
                withAnswers ? JsonNode.Parse(run.Answers) as JsonObject : null));
        }

        return result;
    }

    private static async Task<List<ProactiveRunResponse>> ResponsesAsync(ZelvoDbContext db, IReadOnlyList<ProactiveRun> runs, CancellationToken cancellationToken)
    {
        var people = runs.Select(r => r.PersonId).Distinct().ToList();
        var names = await db.Users.AsNoTracking().Where(u => people.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, cancellationToken);
        var serviceIds = runs.Select(r => r.ServiceId).Distinct().ToList();
        var services = await db.CatalogServices.AsNoTracking().Where(s => serviceIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id, cancellationToken);
        var ticketIds = runs.Select(r => r.TicketId).Concat(runs.Select(r => r.SourceTicketId)).OfType<Guid>().Distinct().ToList();
        var numbers = await db.Tickets.AsNoTracking().Where(t => ticketIds.Contains(t.Id)).ToDictionaryAsync(t => t.Id, t => t.Number, cancellationToken);
        return [.. runs.Select(r => new ProactiveRunResponse(
            r.Id,
            services.TryGetValue(r.ServiceId, out var s) ? new ServiceRefResponse(s.Id, s.Code, LocalizedTextDto.From(s.Name)) : null,
            r.TriggerKey, r.Kind, new PersonResponse(r.PersonId, names.GetValueOrDefault(r.PersonId)), r.Occurrence, r.DueOn, r.State, r.Reason,
            r.TicketId is { } t ? new TicketNumberRef(t, numbers.GetValueOrDefault(t) ?? string.Empty) : null,
            r.SourceTicketId is { } st ? new TicketNumberRef(st, numbers.GetValueOrDefault(st) ?? string.Empty) : null,
            r.OfferedAt, r.ExpiresAt, r.EndedAt, r.CreatedAt))];
    }
}

/// <summary>A date or an event from another system (task C5).</summary>
internal sealed class ProactiveEventRequest
{
    /// <summary>The proactive service: its id or code.</summary>
    [Required]
    [StringLength(60)]
    public string? Service { get; init; }

    /// <summary>The trigger's key on the service card.</summary>
    [Required]
    [StringLength(ProactiveRun.KeyMaxLength)]
    public string? Trigger { get; init; }

    /// <summary>The person, by id.</summary>
    public Guid? PersonId { get; init; }

    /// <summary>The person, by email (when no id is given).</summary>
    [StringLength(320)]
    public string? PersonEmail { get; init; }

    /// <summary>The other system's reference (for example its record id); the same reference is processed once.</summary>
    [Required]
    [StringLength(150, MinimumLength = 1)]
    public string? Reference { get; init; }

    /// <summary>For date triggers: the date (for example a contract's end date); the trigger's offset applies.</summary>
    public DateOnly? Date { get; init; }

    /// <summary>Answers to fill in the service's form, by field key; unknown keys are ignored.</summary>
    public JsonObject? Answers { get; init; }
}

/// <summary>A ticket by number.</summary>
internal sealed record TicketNumberRef(Guid Id, string Number);

/// <summary>A time a proactive service was due for a person.</summary>
internal sealed record ProactiveRunResponse(
    Guid Id, ServiceRefResponse? Service, string Trigger, ProactiveTriggerKind Kind, PersonResponse Person, string Occurrence, DateOnly DueOn,
    ProactiveRunState State, string? Reason, TicketNumberRef? Request, TicketNumberRef? Source,
    DateTimeOffset? OfferedAt, DateTimeOffset? ExpiresAt, DateTimeOffset? EndedAt, DateTimeOffset CreatedAt);

/// <summary>A service offered to the caller; <see cref="Answers"/> fill in its form.</summary>
internal sealed record OfferResponse(
    Guid Id, ServiceRefResponse Service, LocalizedTextDto Summary, ProactiveRunState State, DateTimeOffset? OfferedAt, DateTimeOffset? ExpiresAt,
    ServiceKind Kind, JsonObject? Answers);
