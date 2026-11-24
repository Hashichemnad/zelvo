using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Zelvo.Api.Catalog;
using Zelvo.Api.Conventions;
using Zelvo.Api.Forms;
using Zelvo.Api.Identity;
using Zelvo.Api.Incidents;
using Zelvo.Api.Integrations;
using Zelvo.Api.Settings;
using Zelvo.Api.Tenancy;
using Zelvo.Data;
using Zelvo.Data.Auditing;
using Zelvo.Data.Tenancy;
using Zelvo.Data.Tickets;
using Zelvo.Data.Voice;
using Zelvo.Search;
using Zelvo.Tickets;

namespace Zelvo.Api.Voice;

/// <summary>
/// Anonymous submission (module 3, task C6; product owner decision 2026-11-24): when the tenant
/// allows it, visitors send complaints, inquiries and suggestions without signing in and follow
/// them with the number and a one-time tracking code: status, public replies, their own replies,
/// a complaint's confirmation and the happiness meter. The code is shown once and stored only as
/// a hash; nothing identifies the visitor.
/// </summary>
internal static class VoicePublicEndpoints
{
    public static RouteGroupBuilder MapVoicePublicEndpoints(this RouteGroupBuilder group)
    {
        var anonymous = group.MapGroup("/public/customer-voice").WithTags("Customer voice").RequireTenant();
        anonymous.MapPost("/", SubmitAsync).WithName("SubmitAnonymousCustomerVoice")
            .WithSummary("A visitor sends a complaint, inquiry or suggestion without signing in, when Settings → Customer voice allows it (403 anonymous-disabled otherwise). Title, description and optionally a public service; the record type's forms and categories are for people who sign in (staff classify it). Answers the number and a tracking code, shown only now. Audited as anonymous.")
            .AllowAnonymousAccess().Produces<AnonymousSubmissionResponse>(StatusCodes.Status201Created).ProducesValidationProblem();
        anonymous.MapPost("/track", TrackAsync).WithName("TrackAnonymousCustomerVoice")
            .WithSummary("The record of a number and tracking code, with its public replies. Wrong numbers and codes answer 404 alike.")
            .AllowAnonymousAccess().Produces<TrackedVoiceResponse>();
        anonymous.MapPost("/track/comments", TrackCommentAsync).WithName("ReplyToAnonymousCustomerVoice")
            .WithSummary("The anonymous submitter replies with the number and tracking code.")
            .AllowAnonymousAccess().Produces<VoiceCommentResponse>(StatusCodes.Status201Created).ProducesValidationProblem();
        anonymous.MapPost("/track/confirmation", TrackConfirmAsync).WithName("ConfirmAnonymousComplaint")
            .WithSummary("The anonymous submitter accepts or rejects a resolved complaint's resolution.")
            .AllowAnonymousAccess().Produces<TrackedVoiceResponse>().ProducesValidationProblem();
        anonymous.MapPost("/track/happiness", TrackRateAsync).WithName("RateAnonymousCustomerVoice")
            .WithSummary("The anonymous submitter answers the happiness meter once the record is resolved.")
            .AllowAnonymousAccess().Produces<RatingResponse>(StatusCodes.Status201Created).ProducesValidationProblem();

        group.MapGet("/tickets/{id:guid}/happiness", GetRatingAsync).WithTags("Customer voice").RequireTenant().WithName("GetTicketHappiness")
            .WithSummary("The happiness rating of a ticket of any record type, and whether the caller (its requester) may still give it. Requesters and staff who see the ticket.")
            .RequireAuthorization().Produces<TicketRatingResponse>();
        group.MapPost("/tickets/{id:guid}/happiness", RateAsync).WithTags("Customer voice").RequireTenant().WithName("RateTicket")
            .WithSummary("The requester answers the happiness meter (GOV-2) for their resolved or closed ticket of any record type, once. Audited.")
            .RequirePerson().RequireAuthorization().Produces<RatingResponse>(StatusCodes.Status201Created).ProducesValidationProblem();
        group.MapPost("/happiness/portal", RatePortalAsync).WithTags("Customer voice").RequireTenant().WithName("RatePortal")
            .WithSummary("The general happiness rating in the portal: once a day per signed-in person, or per device (deviceId, stored only as a hash) for visitors. Audited.")
            .AllowAnonymousAccess().Produces<RatingResponse>(StatusCodes.Status201Created).ProducesValidationProblem();
        return group;
    }

    internal static async Task<IResult> SubmitAsync(
        AnonymousSubmissionRequest request, HttpContext http, ZelvoDbContext db, CurrentUser user, ITenantContext tenant, TenantSettingsStore settings,
        IAuditLog audit, ISearchIndexer indexer, CancellationToken cancellationToken)
    {
        var voiceSettings = await settings.GetAsync(VoiceSettings.Voice, cancellationToken);
        if (!voiceSettings.AnonymousEnabled)
        {
            return ApiProblem.Create(StatusCodes.Status403Forbidden, "anonymous-disabled", "Sign in to send a complaint, inquiry or suggestion.");
        }

        var type = request.Type!.Value;
        if (!VoiceTypes.Is(type))
        {
            return UserEndpoints.Validation("type", "Choose Complaint, Inquiry or Suggestion.");
        }

        var problem = await VoiceCore.CheckClassificationAsync(db, CatalogScope.Visitor, null, request.ServiceId, null, cancellationToken);
        if (problem is not null)
        {
            return problem;
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var anonymousId = await AnonymousAccounts.GetOrCreateAsync(db, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        var code = VoiceCore.NewTrackingCode();
        var ticket = await VoiceCore.CreateAsync(http, db, settings, tenant, indexer,
            new VoiceDraft(type, request.Title!, request.Description, anonymousId, null, TicketChannel.Portal, null, null,
                request.ServiceId, null, Anonymous: true, TrackingCodeHash: VoiceCore.HashCode(code)), cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return TypedResults.Created("/api/v1/public/customer-voice/track", new AnonymousSubmissionResponse(ticket.Number, code, type));
    }

    internal static async Task<IResult> TrackAsync(
        TrackRequest request, HttpContext http, ZelvoDbContext db, CurrentUser user, TenantSettingsStore settings, CancellationToken cancellationToken)
    {
        var ticket = await FindTrackedAsync(db, request, tracked: false, cancellationToken);
        return ticket is null ? NotTracked() : TypedResults.Ok(await TrackedAsync(db, user, settings, ticket.Id, cancellationToken));
    }

    internal static async Task<IResult> TrackCommentAsync(
        TrackCommentRequest request, HttpContext http, ZelvoDbContext db, TenantSettingsStore settings, CancellationToken cancellationToken)
    {
        var ticket = await FindTrackedAsync(db, request, tracked: true, cancellationToken);
        if (ticket is null)
        {
            return NotTracked();
        }

        var language = LocalizationSettings.IsLanguage(request.Language) ? request.Language! : await settings.GetAsync(LocalizationSettings.DefaultLanguage, cancellationToken);
        return await VoiceCore.AddCommentAsync(http, db, ticket, ticket.RequesterId, byStaff: false, CommentVisibility.Public, request.Body, language, cancellationToken);
    }

    internal static async Task<IResult> TrackConfirmAsync(
        TrackConfirmRequest request, HttpContext http, ZelvoDbContext db, CurrentUser user, TenantSettingsStore settings, ISearchIndexer indexer,
        CancellationToken cancellationToken)
    {
        var ticket = await FindTrackedAsync(db, request, tracked: true, cancellationToken);
        if (ticket is null)
        {
            return NotTracked();
        }

        var language = LocalizationSettings.IsLanguage(request.Language) ? request.Language! : await settings.GetAsync(LocalizationSettings.DefaultLanguage, cancellationToken);
        var result = await VoiceCore.ConfirmAsync(http, db, user, indexer, ticket,
            new ConfirmComplaintRequest { Accept = request.Accept, Comment = request.Comment }, ticket.RequesterId, language, cancellationToken);
        return result is Microsoft.AspNetCore.Http.HttpResults.Ok<VoiceResponse>
            ? TypedResults.Ok(await TrackedAsync(db, user, settings, ticket.Id, cancellationToken))
            : result;
    }

    internal static async Task<IResult> TrackRateAsync(
        TrackRateRequest request, ZelvoDbContext db, TenantSettingsStore settings, CancellationToken cancellationToken)
    {
        var ticket = await FindTrackedAsync(db, request, tracked: false, cancellationToken);
        return ticket is null ? NotTracked() : await AddTicketRatingAsync(db, settings, ticket, null, request.Score, request.Comment, cancellationToken);
    }

    internal static async Task<IResult> GetRatingAsync(Guid id, ZelvoDbContext db, CurrentUser user, TenantSettingsStore settings, CancellationToken cancellationToken)
    {
        var ticket = await VisibleTicketAsync(db, user, id, cancellationToken);
        if (ticket is null)
        {
            return FormsCommon.NotFound("ticket");
        }

        var rating = await db.HappinessRatings.AsNoTracking().SingleOrDefaultAsync(r => r.TicketId == id, cancellationToken);
        var happiness = await settings.GetAsync(VoiceSettings.Happiness, cancellationToken);
        var canRate = rating is null && happiness is { Enabled: true, AskOnClosure: true } && ticket.RequesterId == user.Account?.Id
            && ticket.State is TicketState.Resolved or TicketState.Closed;
        return TypedResults.Ok(new TicketRatingResponse(
            rating is null ? null : new RatingResponse(rating.Score, rating.Points, rating.Comment, rating.CreatedAt), canRate));
    }

    internal static async Task<IResult> RateAsync(
        Guid id, RateRequest request, ZelvoDbContext db, CurrentUser user, TenantSettingsStore settings, CancellationToken cancellationToken)
    {
        var ticket = await VisibleTicketAsync(db, user, id, cancellationToken);
        if (ticket is null)
        {
            return FormsCommon.NotFound("ticket");
        }

        if (ticket.RequesterId != user.Account!.Id)
        {
            return ApiProblem.Create(StatusCodes.Status403Forbidden, "forbidden", "Only the person the ticket is for rates it.");
        }

        return await AddTicketRatingAsync(db, settings, ticket, user.Account.Id, request.Score, request.Comment, cancellationToken);
    }

    internal static async Task<IResult> RatePortalAsync(
        PortalRateRequest request, ZelvoDbContext db, CurrentUser user, TenantSettingsStore settings, CancellationToken cancellationToken)
    {
        var happiness = await settings.GetAsync(VoiceSettings.Happiness, cancellationToken);
        if (happiness is not { Enabled: true, InPortal: true })
        {
            return ApiProblem.Create(StatusCodes.Status409Conflict, "happiness-off", "The happiness meter is not shown in the portal.");
        }

        if (ScoreProblem(request.Score, happiness) is { } scoreProblem)
        {
            return scoreProblem;
        }

        var me = user.Account?.Id;
        string? device = null;
        if (me is null)
        {
            if (request.DeviceId is not { } deviceId)
            {
                return UserEndpoints.Validation("deviceId", "Send the device id the portal keeps for visitors.");
            }

            device = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"happiness:{deviceId:N}")));
        }

        var since = DateTimeOffset.UtcNow.AddDays(-1);
        var already = await db.HappinessRatings.AnyAsync(r => r.TicketId == null && r.CreatedAt > since
            && (me != null ? r.UserId == me : r.DeviceHash == device), cancellationToken);
        if (already)
        {
            return ApiProblem.Create(StatusCodes.Status409Conflict, "already-rated", "Thank you; you rated our services today already.");
        }

        var rating = new HappinessRating
        {
            UserId = me,
            DeviceHash = device,
            Score = request.Score!.Value,
            Points = happiness.Points,
            Comment = Clean(request.Comment),
        };
        db.HappinessRatings.Add(rating);
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Created("/api/v1/happiness/portal", new RatingResponse(rating.Score, rating.Points, rating.Comment, rating.CreatedAt));
    }

    private static async Task<IResult> AddTicketRatingAsync(
        ZelvoDbContext db, TenantSettingsStore settings, Ticket ticket, Guid? userId, int? score, string? comment, CancellationToken cancellationToken)
    {
        var happiness = await settings.GetAsync(VoiceSettings.Happiness, cancellationToken);
        if (happiness is not { Enabled: true, AskOnClosure: true })
        {
            return ApiProblem.Create(StatusCodes.Status409Conflict, "happiness-off", "The happiness meter is not asked on tickets.");
        }

        if (ticket.State is not (TicketState.Resolved or TicketState.Closed))
        {
            return ApiProblem.Create(StatusCodes.Status409Conflict, "not-resolved", "Rate it once it is resolved.");
        }

        if (ScoreProblem(score, happiness) is { } scoreProblem)
        {
            return scoreProblem;
        }

        if (await db.HappinessRatings.AnyAsync(r => r.TicketId == ticket.Id, cancellationToken))
        {
            return ApiProblem.Create(StatusCodes.Status409Conflict, "already-rated", "This was rated already. Thank you.");
        }

        var serviceId = ticket.Type == TicketType.ServiceRequest
            ? await db.ServiceRequests.Where(r => r.TicketId == ticket.Id).Select(r => (Guid?)r.ServiceId).SingleOrDefaultAsync(cancellationToken)
            : await db.CustomerVoiceRecords.Where(v => v.TicketId == ticket.Id).Select(v => v.ServiceId).SingleOrDefaultAsync(cancellationToken);
        var rating = new HappinessRating
        {
            TicketId = ticket.Id,
            RecordType = ticket.Type,
            Channel = ticket.Channel,
            ServiceId = serviceId,
            UserId = userId,
            Score = score!.Value,
            Points = happiness.Points,
            Comment = Clean(comment),
        };
        db.HappinessRatings.Add(rating);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            return ApiProblem.Create(StatusCodes.Status409Conflict, "already-rated", "This was rated already. Thank you.");
        }

        return TypedResults.Created($"/api/v1/tickets/{ticket.Id}/happiness", new RatingResponse(rating.Score, rating.Points, rating.Comment, rating.CreatedAt));
    }

    private static Microsoft.AspNetCore.Http.HttpResults.ValidationProblem? ScoreProblem(int? score, HappinessSettingsValue happiness) =>
        score is { } s && s >= 1 && s <= happiness.Points ? null : UserEndpoints.Validation("score", $"Choose a score from 1 to {happiness.Points}.");

    private static string? Clean(string? comment) =>
        string.IsNullOrWhiteSpace(comment) ? null : IncidentIntake.Clean(comment, HappinessRating.CommentMaxLength);

    /// <summary>A ticket of any record type the caller may see (its requester, or staff who read that record type).</summary>
    private static async Task<Ticket?> VisibleTicketAsync(ZelvoDbContext db, CurrentUser user, Guid id, CancellationToken cancellationToken)
    {
        var ticket = await db.Tickets.AsNoTracking().SingleOrDefaultAsync(t => t.Id == id, cancellationToken);
        if (ticket is null)
        {
            return null;
        }

        var me = user.Account?.Id;
        var staff = ticket.Type switch
        {
            TicketType.Incident => user.Has(Permissions.IncidentsRead),
            TicketType.ServiceRequest => user.Has(Permissions.RequestsRead),
            _ => user.Has(Permissions.VoiceRead),
        };
        return staff || (me is not null && ticket.RequesterId == me) ? ticket : null;
    }

    private static async Task<Ticket?> FindTrackedAsync(ZelvoDbContext db, ITrackRequest request, bool tracked, CancellationToken cancellationToken)
    {
        var number = request.Number?.Trim().ToUpperInvariant();
        var code = VoiceCore.NormalizeCode(request.Code);
        if (string.IsNullOrEmpty(number) || code.Length != VoiceCore.TrackingCodeLength)
        {
            return null;
        }

        var query = tracked ? VoiceCore.Records(db) : VoiceCore.Records(db).AsNoTracking();
        var ticket = await query.SingleOrDefaultAsync(t => t.Number == number, cancellationToken);
        var expected = Encoding.ASCII.GetBytes(ticket?.Voice?.TrackingCodeHash ?? new string('0', 64));
        var given = Encoding.ASCII.GetBytes(VoiceCore.HashCode(code));
        // Compared in constant time; a record without a code never matches.
        return CryptographicOperations.FixedTimeEquals(expected, given) && ticket?.Voice is { Anonymous: true } ? ticket : null;
    }

    private static async Task<TrackedVoiceResponse> TrackedAsync(ZelvoDbContext db, CurrentUser user, TenantSettingsStore settings, Guid id, CancellationToken cancellationToken)
    {
        var ticket = await VoiceCore.Records(db).AsNoTracking().SingleAsync(t => t.Id == id, cancellationToken);
        var context = await VoiceResponseContext.LoadAsync(db, user, settings, [ticket], cancellationToken, tracking: true);
        return new TrackedVoiceResponse(context.From(ticket), await VoiceCore.CommentsAsync(db, id, staff: false, cancellationToken));
    }

    private static Microsoft.AspNetCore.Http.HttpResults.ProblemHttpResult NotTracked() =>
        (Microsoft.AspNetCore.Http.HttpResults.ProblemHttpResult)ApiProblem.Create(StatusCodes.Status404NotFound, "not-found",
            "No record matches this number and tracking code.");
}

/// <summary>Number and tracking code of an anonymous record.</summary>
internal interface ITrackRequest
{
    string? Number { get; }

    string? Code { get; }
}

/// <summary>A visitor's complaint, inquiry or suggestion.</summary>
internal sealed class AnonymousSubmissionRequest
{
    /// <summary>Complaint, Inquiry or Suggestion.</summary>
    [Required]
    public TicketType? Type { get; init; }

    /// <summary>Short summary.</summary>
    [Required]
    [StringLength(Ticket.TitleMaxLength, MinimumLength = 1)]
    public string? Title { get; init; }

    /// <summary>Details.</summary>
    [StringLength(Ticket.DescriptionMaxLength)]
    public string? Description { get; init; }

    /// <summary>A public service it is about.</summary>
    public Guid? ServiceId { get; init; }
}

/// <summary>Number and code.</summary>
internal class TrackRequest : ITrackRequest
{
    /// <summary>The number, for example CMP-000123.</summary>
    [Required]
    [StringLength(Ticket.NumberMaxLength)]
    public string? Number { get; init; }

    /// <summary>The tracking code given when it was sent.</summary>
    [Required]
    [StringLength(40)]
    public string? Code { get; init; }
}

/// <summary>A reply by the anonymous submitter.</summary>
internal sealed class TrackCommentRequest : TrackRequest
{
    /// <summary>The text.</summary>
    [Required]
    [StringLength(TicketComment.BodyMaxLength, MinimumLength = 1)]
    public string? Body { get; init; }

    /// <summary>en or ar.</summary>
    [StringLength(2)]
    public string? Language { get; init; }
}

/// <summary>The anonymous submitter's answer to a resolved complaint.</summary>
internal sealed class TrackConfirmRequest : TrackRequest
{
    /// <summary>Accept (close) or reject (reopen).</summary>
    [Required]
    public bool? Accept { get; init; }

    /// <summary>Why it is rejected.</summary>
    [StringLength(CustomerVoiceRecord.TextMaxLength)]
    public string? Comment { get; init; }

    /// <summary>en or ar.</summary>
    [StringLength(2)]
    public string? Language { get; init; }
}

/// <summary>The anonymous submitter's rating.</summary>
internal sealed class TrackRateRequest : TrackRequest
{
    /// <summary>1 to the scale's points.</summary>
    [Required]
    public int? Score { get; init; }

    /// <summary>A comment.</summary>
    [StringLength(HappinessRating.CommentMaxLength)]
    public string? Comment { get; init; }
}

/// <summary>A rating of a ticket.</summary>
internal sealed class RateRequest
{
    /// <summary>1 (worst) to the scale's points.</summary>
    [Required]
    public int? Score { get; init; }

    /// <summary>A comment.</summary>
    [StringLength(HappinessRating.CommentMaxLength)]
    public string? Comment { get; init; }
}

/// <summary>A portal rating.</summary>
internal sealed class PortalRateRequest
{
    /// <summary>1 (worst) to the scale's points.</summary>
    [Required]
    public int? Score { get; init; }

    /// <summary>A comment.</summary>
    [StringLength(HappinessRating.CommentMaxLength)]
    public string? Comment { get; init; }

    /// <summary>Visitors: the random id the portal keeps on the device.</summary>
    public Guid? DeviceId { get; init; }
}

/// <summary>What an anonymous submitter keeps: the number and the tracking code (shown once).</summary>
internal sealed record AnonymousSubmissionResponse(string Number, string TrackingCode, TicketType Type);

/// <summary>An anonymous record with its public replies.</summary>
internal sealed record TrackedVoiceResponse(VoiceResponse Record, IReadOnlyList<VoiceCommentResponse> Comments);

/// <summary>A ticket's rating and whether the caller may still give it.</summary>
internal sealed record TicketRatingResponse(RatingResponse? Rating, bool CanRate);
