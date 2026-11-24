using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Zelvo.Api.Catalog;
using Zelvo.Api.Conventions;
using Zelvo.Api.Forms;
using Zelvo.Api.Identity;
using Zelvo.Api.Incidents;
using Zelvo.Api.ServiceDesk;
using Zelvo.Api.Settings;
using Zelvo.Api.Sla;
using Zelvo.Api.Tenancy;
using Zelvo.Data;
using Zelvo.Data.Catalog;
using Zelvo.Data.Identity;
using Zelvo.Data.Tenancy;
using Zelvo.Data.Tickets;
using Zelvo.Data.Voice;
using Zelvo.Search;
using Zelvo.Tickets;
using Zelvo.Tickets.Sla;

namespace Zelvo.Api.Voice;

/// <summary>What a new complaint, inquiry or suggestion is made of.</summary>
internal sealed record VoiceDraft(
    TicketType Type, string Title, string? Description, Guid RequesterId, Guid? CreatedById, TicketChannel Channel,
    Guid? CategoryId, Guid? LocationId, Guid? ServiceId, int? Severity, bool Anonymous, string? TrackingCodeHash);

/// <summary>Shared pieces of the customer voice endpoints (task C6).</summary>
internal static class VoiceCore
{
    /// <summary>Length of anonymous tracking codes (31 symbols each: about 59 bits).</summary>
    public const int TrackingCodeLength = 12;

    // No 0/O, 1/I/L: codes are read and typed by people.
    private const string CodeAlphabet = "23456789ABCDEFGHJKMNPQRSTUVWXYZ";

    /// <summary>Complaints, inquiries and suggestions with their details.</summary>
    public static IQueryable<Ticket> Records(ZelvoDbContext db) =>
        db.Tickets.Include(t => t.Voice).Where(t => t.Type == TicketType.Complaint || t.Type == TicketType.Inquiry || t.Type == TicketType.Suggestion);

    /// <summary>Active people whose roles may work on customer voice records.</summary>
    public static IQueryable<UserAccount> Assignable(ZelvoDbContext db) =>
        db.Users.Where(u => u.Status == UserStatus.Active
            && u.Roles.Any(r => r.Role!.Permissions.Any(p => p.Permission == Permissions.VoiceEdit)));

    /// <summary>A record the caller may see: staff with voice.read, the requester or who logged it for them; anyone else gets null (404).</summary>
    public static async Task<Ticket?> FindVisibleAsync(ZelvoDbContext db, CurrentUser user, Guid id, CancellationToken cancellationToken, bool tracked = false)
    {
        var query = tracked ? Records(db) : Records(db).AsNoTracking();
        var ticket = await query.SingleOrDefaultAsync(t => t.Id == id, cancellationToken).ConfigureAwait(false);
        if (ticket is null)
        {
            return null;
        }

        var me = user.Account?.Id;
        return user.Has(Permissions.VoiceRead) || (me is not null && (ticket.RequesterId == me || ticket.CreatedById == me)) ? ticket : null;
    }

    public static ProblemHttpResult NotFound() => FormsCommon.NotFound("customer-voice-record");

    /// <summary>The If-Match header against the ticket's version, when sent.</summary>
    public static ProblemHttpResult? VersionProblem(HttpContext http, ZelvoDbContext db, Ticket ticket)
    {
        var ifMatch = http.Request.Headers.IfMatch.ToString();
        if (string.IsNullOrEmpty(ifMatch))
        {
            return null;
        }

        if (!uint.TryParse(ifMatch.Trim().Replace("W/", "", StringComparison.Ordinal).Trim('"'), out var version) || version != ticket.Version)
        {
            return FormsCommon.VersionMismatch();
        }

        db.Entry(ticket).Property(t => t.Version).OriginalValue = version;
        return null;
    }

    /// <summary>
    /// A new category must be selectable; a new service must exist (staff) or be offered to the
    /// requester (<paramref name="scope"/>, people sending their own).
    /// </summary>
    public static async Task<IResult?> CheckClassificationAsync(
        ZelvoDbContext db, CatalogScope? scope, Guid? categoryId, Guid? serviceId, Ticket? current, CancellationToken cancellationToken)
    {
        if (categoryId is { } category && category != current?.CategoryId && !await TreeEndpoints.IsSelectableAsync(db.Categories, category, cancellationToken).ConfigureAwait(false))
        {
            return UserEndpoints.Validation("categoryId", "Choose an active category.");
        }

        if (serviceId is { } service)
        {
            var known = scope is null
                ? await db.CatalogServices.AnyAsync(s => s.Id == service, cancellationToken).ConfigureAwait(false)
                : await PublishedServices.FindAsync(db, scope, service, cancellationToken).ConfigureAwait(false) is not null;
            if (!known)
            {
                return UserEndpoints.Validation("serviceId", "Choose a service of the catalog.");
            }
        }

        return null;
    }

    /// <summary>
    /// Stages a new record inside the caller's transaction: its number, its details with the
    /// priority for SLA targets, routing by the assignment rules, the search document and the SLA clocks.
    /// </summary>
    public static async Task<Ticket> CreateAsync(
        HttpContext http, ZelvoDbContext db, TenantSettingsStore settings, ITenantContext tenant, ISearchIndexer indexer, VoiceDraft draft,
        CancellationToken cancellationToken)
    {
        var prefixes = await settings.GetAsync(IncidentSettings.NumberPrefixes, cancellationToken).ConfigureAwait(false);
        var voiceSettings = await settings.GetAsync(VoiceSettings.Voice, cancellationToken).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        var sequence = await TicketNumbers.NextAsync(db, tenant.TenantId!.Value, draft.Type, cancellationToken).ConfigureAwait(false);
        var title = draft.Title.Trim();
        var ticket = new Ticket
        {
            Type = draft.Type,
            Number = TicketNumbers.Format(TicketNumbers.PrefixOf(prefixes, draft.Type), sequence),
            Sequence = sequence,
            Title = title[..Math.Min(title.Length, Ticket.TitleMaxLength)],
            Description = string.IsNullOrWhiteSpace(draft.Description) ? null : draft.Description.Trim(),
            Channel = draft.Channel,
            RequesterId = draft.RequesterId,
            CreatedById = draft.CreatedById,
            CategoryId = draft.CategoryId,
            LocationId = draft.LocationId,
            CreatedAt = now,
            UpdatedAt = now,
        };
        var severity = draft.Type == TicketType.Complaint ? draft.Severity ?? 2 : (int?)null;
        ticket.Voice = new CustomerVoiceRecord
        {
            TicketId = ticket.Id,
            Priority = voiceSettings.PriorityOf(draft.Type, severity),
            Severity = severity,
            Review = draft.Type == TicketType.Suggestion ? SuggestionReview.UnderReview : null,
            ServiceId = draft.ServiceId,
            Anonymous = draft.Anonymous,
            TrackingCodeHash = draft.TrackingCodeHash,
        };
        await IncidentEndpoints.RouteAsync(db, settings, ticket, now, cancellationToken).ConfigureAwait(false);
        db.Tickets.Add(ticket);
        indexer.Upsert(VoiceSearch.Document(ticket, await VoiceSearch.StaffRolesAsync(db, cancellationToken).ConfigureAwait(false)));
        await http.RequestServices.GetRequiredService<SlaTracker>().UpdateAsync([ticket], now, cancellationToken).ConfigureAwait(false);
        return ticket;
    }

    /// <summary>Saves a changed record with its SLA clocks and search document; 412 when it changed meanwhile.</summary>
    public static async Task<IResult> SaveAsync(HttpContext http, ZelvoDbContext db, CurrentUser user, ISearchIndexer indexer, Ticket ticket, CancellationToken cancellationToken)
    {
        var own = db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false) : null;
        try
        {
            await http.RequestServices.GetRequiredService<SlaTracker>().UpdateAsync([ticket], DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);
            indexer.Upsert(VoiceSearch.Document(ticket, await VoiceSearch.StaffRolesAsync(db, cancellationToken).ConfigureAwait(false)));
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await (own ?? db.Database.CurrentTransaction!).CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            return FormsCommon.VersionMismatch();
        }
        finally
        {
            if (own is not null)
            {
                await own.DisposeAsync().ConfigureAwait(false);
            }
        }

        return TypedResults.Ok(await ResponseAsync(http, db, user, ticket.Id, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>The response for <paramref name="id"/> as the caller sees it, with the ETag header.</summary>
    public static async Task<VoiceResponse> ResponseAsync(HttpContext http, ZelvoDbContext db, CurrentUser user, Guid id, CancellationToken cancellationToken)
    {
        var ticket = await Records(db).AsNoTracking().SingleAsync(t => t.Id == id, cancellationToken).ConfigureAwait(false);
        var context = await VoiceResponseContext.LoadAsync(db, user, http.RequestServices.GetRequiredService<TenantSettingsStore>(), [ticket], cancellationToken).ConfigureAwait(false);
        http.Response.Headers.ETag = $"\"{ticket.Version}\"";
        return context.From(ticket);
    }

    /// <summary>
    /// States the caller can move the record to: staff the ticket core's, except that suggestions
    /// resolve by their review and resolved complaints close by the customer's confirmation;
    /// requesters may reopen their resolved inquiries.
    /// </summary>
    public static IReadOnlyList<TicketState> NextStates(Ticket ticket, bool staff, Guid? me)
    {
        if (!staff)
        {
            return ticket is { Type: TicketType.Inquiry, State: TicketState.Resolved } && ticket.RequesterId == me ? [TicketState.Reopened] : [];
        }

        return [.. TicketWorkflow.NextStates(ticket.State).Where(s =>
            !(ticket.Type == TicketType.Suggestion && s == TicketState.Resolved)
            && !(ticket.Type == TicketType.Complaint && s == TicketState.Closed))];
    }

    /// <summary>The conversation: staff see notes too.</summary>
    public static async Task<List<VoiceCommentResponse>> CommentsAsync(ZelvoDbContext db, Guid ticketId, bool staff, CancellationToken cancellationToken)
    {
        var rows = await db.TicketComments.AsNoTracking()
            .Where(c => c.TicketId == ticketId && (staff || c.Visibility == CommentVisibility.Public))
            .OrderBy(c => c.CreatedAt).ThenBy(c => c.Id).Take(500).ToListAsync(cancellationToken).ConfigureAwait(false);
        var names = await IncidentEndpoints.Names(db, rows.Select(c => c.AuthorId), cancellationToken).ConfigureAwait(false);
        var anonymous = await db.Users.AsNoTracking().Where(u => u.Subject == AnonymousAccounts.Subject).Select(u => (Guid?)u.Id).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return [.. rows.Select(c => new VoiceCommentResponse(
            c.Id, c.Visibility, c.Body, c.Language, c.AuthorId == anonymous ? null : new PersonResponse(c.AuthorId, names.GetValueOrDefault(c.AuthorId)),
            c.ByStaff, c.CreatedAt))];
    }

    /// <summary>Adds a reply or note: a staff public reply is the response (B3), the requester's answer moves a record waiting for them back to work.</summary>
    public static async Task<IResult> AddCommentAsync(
        HttpContext http, ZelvoDbContext db, Ticket ticket, Guid authorId, bool byStaff, CommentVisibility visibility, string? body, string language,
        CancellationToken cancellationToken)
    {
        if (ticket.State == TicketState.Closed)
        {
            return IncidentEndpoints.Closed();
        }

        var text = body?.Trim() ?? "";
        if (text.Length == 0)
        {
            return UserEndpoints.Validation("body", "Write the reply or note.");
        }

        var now = DateTimeOffset.UtcNow;
        var comment = new TicketComment
        {
            TicketId = ticket.Id,
            Visibility = visibility,
            Body = text,
            Language = language,
            AuthorId = authorId,
            ByStaff = byStaff,
            CreatedAt = now,
        };
        db.TicketComments.Add(comment);
        var own = db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false) : null;
        await using (own)
        {
            var sla = http.RequestServices.GetRequiredService<SlaTracker>();
            if (byStaff && visibility == CommentVisibility.Public && ticket.RespondedAt is null)
            {
                ticket.RespondedAt = now;
                ticket.UpdatedAt = now;
                await sla.UpdateAsync([ticket], now, cancellationToken).ConfigureAwait(false);
            }
            else if (!byStaff && ticket is { State: TicketState.OnHold, OnHoldReason: OnHoldReason.AwaitingRequester }
                && TicketWorkflow.Check(ticket, TicketState.InProgress, null, null) == TransitionError.None)
            {
                TicketWorkflow.Apply(ticket, TicketState.InProgress, null, null, now);
                await sla.UpdateAsync([ticket], now, cancellationToken).ConfigureAwait(false);
            }

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await (own ?? db.Database.CurrentTransaction!).CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        var names = await IncidentEndpoints.Names(db, [authorId], cancellationToken).ConfigureAwait(false);
        var anonymous = await db.Users.AsNoTracking().AnyAsync(u => u.Id == authorId && u.Subject == AnonymousAccounts.Subject, cancellationToken).ConfigureAwait(false);
        return TypedResults.Created($"/api/v1/customer-voice/{ticket.Id}/comments",
            new VoiceCommentResponse(comment.Id, comment.Visibility, comment.Body, comment.Language,
                anonymous ? null : new PersonResponse(authorId, names.GetValueOrDefault(authorId)), comment.ByStaff, comment.CreatedAt));
    }

    /// <summary>The customer accepts (closes) or rejects (reopens, with the reason in the conversation) a resolved complaint.</summary>
    public static async Task<IResult> ConfirmAsync(
        HttpContext http, ZelvoDbContext db, CurrentUser user, ISearchIndexer indexer, Ticket ticket, ConfirmComplaintRequest request,
        Guid authorId, string language, CancellationToken cancellationToken)
    {
        if (ticket.Type != TicketType.Complaint || ticket.State != TicketState.Resolved || ticket.Voice!.Confirmation != ComplaintConfirmation.Pending)
        {
            return ApiProblem.Create(StatusCodes.Status409Conflict, "confirmation-not-pending", "Only a resolved complaint waiting for your confirmation can be confirmed or rejected.");
        }

        var comment = string.IsNullOrWhiteSpace(request.Comment) ? null : request.Comment.Trim();
        var now = DateTimeOffset.UtcNow;
        var voice = ticket.Voice;
        if (request.Accept == true)
        {
            TicketWorkflow.Apply(ticket, TicketState.Closed, null, null, now);
            voice.Confirmation = ComplaintConfirmation.Confirmed;
        }
        else
        {
            if (comment is null)
            {
                return UserEndpoints.Validation("comment", "Tell us why the resolution does not settle your complaint.");
            }

            TicketWorkflow.Apply(ticket, TicketState.Reopened, null, null, now);
            voice.Confirmation = ComplaintConfirmation.Rejected;
            db.TicketComments.Add(new TicketComment
            {
                TicketId = ticket.Id,
                Visibility = CommentVisibility.Public,
                Body = comment,
                Language = language,
                AuthorId = authorId,
                ByStaff = false,
                CreatedAt = now,
            });
        }

        voice.ConfirmationAt = now;
        voice.ConfirmationComment = comment;
        return await SaveAsync(http, db, user, indexer, ticket, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>A new tracking code for an anonymous submitter, for example <c>K7P2-QX9M-4HTA</c> without the hyphens.</summary>
    public static string NewTrackingCode()
    {
        var chars = new char[TrackingCodeLength];
        for (var i = 0; i < chars.Length; i++)
        {
            chars[i] = CodeAlphabet[RandomNumberGenerator.GetInt32(CodeAlphabet.Length)];
        }

        return new string(chars);
    }

    /// <summary>The code as people type it: capitals, without spaces and hyphens.</summary>
    public static string NormalizeCode(string? code) =>
        new([.. (code ?? "").ToUpperInvariant().Where(c => c is not (' ' or '-'))]);

    /// <summary>SHA-256 of a normalised tracking code, hex.</summary>
    public static string HashCode(string code) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(NormalizeCode(code))));
}

/// <summary>A link to a related ticket.</summary>
internal sealed record VoiceLinkResponse(Guid Id, string Number, TicketType Type, TicketState State);

/// <summary>A reply or note; <c>author</c> is null for anonymous submitters.</summary>
internal sealed record VoiceCommentResponse(Guid Id, CommentVisibility Visibility, string Body, string Language, PersonResponse? Author, bool ByStaff, DateTimeOffset CreatedAt);

/// <summary>A happiness rating.</summary>
internal sealed record RatingResponse(int Score, int Points, string? Comment, DateTimeOffset CreatedAt);

/// <summary>
/// A complaint, inquiry or suggestion. Requesters see the team (group) but not the assignee, SLA clocks or
/// internal links; anonymous ones have no requester.
/// </summary>
internal sealed record VoiceResponse(
    Guid Id, string Number, TicketType Type, string Title, string? Description, TicketState State, TicketChannel Channel,
    PersonResponse? Requester, bool Anonymous, PersonResponse? LoggedBy, PersonResponse? Assignee, NamedRef? Group, NodeRef? Category,
    ServiceRefResponse? Service, int Priority, int? Severity, string? RootCauseKey,
    ComplaintConfirmation? Confirmation, DateTimeOffset? ConfirmationAt, string? ConfirmationComment,
    SuggestionReview? Review, string? ReviewFeedback, VoiceLinkResponse? ConvertedTo, VoiceLinkResponse? ConvertedFrom,
    string? OnHoldReason, string? ResolutionNotes, IReadOnlyList<TicketState> NextStates, bool CanConfirm,
    RatingResponse? Rating, bool CanRate, IReadOnlyList<SlaClockResponse> Sla, string Etag,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, DateTimeOffset? ResolvedAt, DateTimeOffset? ClosedAt);

/// <summary>What responses need, loaded once for a page of records.</summary>
internal sealed class VoiceResponseContext
{
    private Dictionary<Guid, string?> _names = [];
    private Dictionary<Guid, (string Code, Data.Localization.LocalizedText Name)> _services = [];
    private Dictionary<Guid, VoiceLinkResponse> _links = [];
    private Dictionary<Guid, Guid> _convertedFrom = [];
    private Dictionary<Guid, HappinessRating> _ratings = [];
    private DirectoryRefs _refs = null!;
    private TicketSlaView _slas = null!;
    private CurrentUser _user = null!;
    private bool _staff;
    private bool _askRating;

    /// <summary>Whether the caller is the anonymous submitter of every record (tracking code).</summary>
    public bool Tracking { get; init; }

    public static async Task<VoiceResponseContext> LoadAsync(
        ZelvoDbContext db, CurrentUser user, TenantSettingsStore settings, IReadOnlyList<Ticket> tickets, CancellationToken cancellationToken, bool tracking = false)
    {
        var ids = tickets.Select(t => t.Id).ToList();
        var staff = user.Has(Permissions.VoiceRead);
        var serviceIds = tickets.Select(t => t.Voice?.ServiceId).OfType<Guid>().Distinct().ToList();
        var services = await db.CatalogServices.AsNoTracking().Where(s => serviceIds.Contains(s.Id))
            .ToDictionaryAsync(s => s.Id, s => (s.Code, s.Name), cancellationToken).ConfigureAwait(false);
        var from = await db.CustomerVoiceRecords.AsNoTracking().Where(v => v.ConvertedToId != null && ids.Contains(v.ConvertedToId.Value))
            .ToDictionaryAsync(v => v.ConvertedToId!.Value, v => v.TicketId, cancellationToken).ConfigureAwait(false);
        var linkIds = tickets.Select(t => t.Voice?.ConvertedToId).OfType<Guid>().Concat(from.Values).Distinct().ToList();
        var links = await db.Tickets.AsNoTracking().Where(t => linkIds.Contains(t.Id))
            .ToDictionaryAsync(t => t.Id, t => new VoiceLinkResponse(t.Id, t.Number, t.Type, t.State), cancellationToken).ConfigureAwait(false);
        var ratings = await db.HappinessRatings.AsNoTracking().Where(r => r.TicketId != null && ids.Contains(r.TicketId.Value))
            .ToDictionaryAsync(r => r.TicketId!.Value, cancellationToken).ConfigureAwait(false);
        var happiness = await settings.GetAsync(VoiceSettings.Happiness, cancellationToken).ConfigureAwait(false);
        return new VoiceResponseContext
        {
            Tracking = tracking,
            _names = await IncidentEndpoints.Names(db, tickets.SelectMany(t => new[] { t.RequesterId, t.AssigneeId ?? Guid.Empty, t.CreatedById ?? Guid.Empty }), cancellationToken).ConfigureAwait(false),
            _services = services,
            _links = links,
            _convertedFrom = from,
            _ratings = ratings,
            _refs = await DirectoryRefs.LoadAsync(db, cancellationToken).ConfigureAwait(false),
            _slas = await TicketSlaView.LoadAsync(db, staff ? ids : [], cancellationToken).ConfigureAwait(false),
            _user = user,
            _staff = staff,
            _askRating = happiness is { Enabled: true, AskOnClosure: true },
        };
    }

    public VoiceResponse From(Ticket t)
    {
        var voice = t.Voice ?? new CustomerVoiceRecord { TicketId = t.Id };
        var me = _user.Account?.Id;
        var isRequester = Tracking || (me is not null && t.RequesterId == me);
        var rating = _ratings.GetValueOrDefault(t.Id);
        var canRate = _askRating && isRequester && rating is null && t.State is TicketState.Resolved or TicketState.Closed;
        var editor = _staff && _user.Has(Permissions.VoiceEdit);
        var next = Tracking ? [] : VoiceCore.NextStates(t, editor, me);
        var service = voice.ServiceId is { } sid && _services.TryGetValue(sid, out var s)
            ? new ServiceRefResponse(sid, s.Code, LocalizedTextDto.From(s.Name)) : null;
        return new VoiceResponse(
            t.Id, t.Number, t.Type, t.Title, t.Description, t.State, t.Channel,
            voice.Anonymous ? null : new PersonResponse(t.RequesterId, _names.GetValueOrDefault(t.RequesterId)),
            voice.Anonymous,
            t.CreatedById is { } by && by != t.RequesterId && _staff ? new PersonResponse(by, _names.GetValueOrDefault(by)) : null,
            _staff && t.AssigneeId is { } a ? new PersonResponse(a, _names.GetValueOrDefault(a)) : null,
            // The team that has it, also for requesters; never the person (decision 2026-11-24).
            _refs.Group(t.GroupId),
            _refs.Categories.RefOf(t.CategoryId),
            service, voice.Priority, voice.Severity, voice.RootCauseKey,
            voice.Confirmation, voice.ConfirmationAt, voice.ConfirmationComment,
            voice.Review, voice.ReviewFeedback,
            voice.ConvertedToId is { } to ? _links.GetValueOrDefault(to) : null,
            _convertedFrom.TryGetValue(t.Id, out var fromId) ? _links.GetValueOrDefault(fromId) : null,
            t.OnHoldReason, t.ResolutionNotes, next,
            isRequester && t is { Type: TicketType.Complaint, State: TicketState.Resolved } && voice.Confirmation == ComplaintConfirmation.Pending,
            rating is null ? null : new RatingResponse(rating.Score, rating.Points, _staff || isRequester ? rating.Comment : null, rating.CreatedAt),
            canRate,
            _staff ? _slas.For(t.Id, DateTimeOffset.UtcNow) : [],
            $"\"{t.Version}\"", t.CreatedAt, t.UpdatedAt, t.ResolvedAt, t.ClosedAt);
    }
}
