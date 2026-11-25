using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Zelvo.Api.Conventions;
using Zelvo.Api.Identity;
using Zelvo.Api.Incidents;
using Zelvo.Api.Integrations;
using Zelvo.Api.Settings;
using Zelvo.Api.Tenancy;
using Zelvo.Data;
using Zelvo.Data.Identity;
using Zelvo.Data.Localization;
using Zelvo.Data.Tickets;
using Zelvo.Tickets;
using Zelvo.Tickets.Sla;

namespace Zelvo.Api.Workspace;

/// <summary>
/// The conversation of an incident (module 5, task B4): public replies, which its requester sees,
/// and internal notes for staff, with @mentions. Comments are never changed or removed. A staff
/// member's first public reply is the ticket's response (decision 2026-11-24).
/// </summary>
internal static class CommentEndpoints
{
    /// <summary>Most people one comment can mention.</summary>
    internal const int MaxMentions = 20;

    public static RouteGroupBuilder MapCommentEndpoints(this RouteGroupBuilder group)
    {
        var comments = group.MapGroup("/incidents/{id:guid}/comments").WithTags("Agent workspace").RequireTenant();

        comments.MapGet("/", ListAsync)
            .WithName("ListIncidentComments")
            .WithSummary("The conversation of an incident, oldest first. Staff (incidents.read) see public replies and internal notes; the requester sees the public replies of their own incident.")
            .RequireAuthorization()
            .Produces<IReadOnlyList<CommentResponse>>();

        comments.MapPost("/", CreateAsync)
            .WithName("CreateIncidentComment")
            // Comments record the person who writes them.
            .RequirePerson()
            .WithSummary("Post a public reply or an internal note. Staff need incidents.edit; the requester posts public replies on their own incident. Staff may @mention other staff (mentionIds), who are notified. A staff member's first public reply stops the response clock. Closed incidents take no comments; audited.")
            .RequireAuthorization()
            .Produces<CommentResponse>(StatusCodes.Status201Created)
            .ProducesValidationProblem();

        return group;
    }

    internal static async Task<Results<Ok<List<CommentResponse>>, ProblemHttpResult>> ListAsync(
        Guid id, ZelvoDbContext db, CurrentUser user, CancellationToken cancellationToken)
    {
        if (await IncidentEndpoints.FindVisibleAsync(db, user, id, cancellationToken) is null)
        {
            return IncidentEndpoints.NotFound();
        }

        var staff = user.Has(Permissions.IncidentsRead);
        var rows = await db.TicketComments.AsNoTracking()
            .Where(c => c.TicketId == id && (staff || c.Visibility == CommentVisibility.Public))
            .OrderByDescending(c => c.CreatedAt).ThenByDescending(c => c.Id).Take(500)
            .ToListAsync(cancellationToken);
        rows.Reverse();
        return TypedResults.Ok(await ResponsesAsync(db, rows, staff, cancellationToken));
    }

    internal static async Task<Results<Created<CommentResponse>, ValidationProblem, ProblemHttpResult>> CreateAsync(
        Guid id, CreateCommentRequest request, HttpContext http, ZelvoDbContext db, CurrentUser user,
        TenantSettingsStore settings, CancellationToken cancellationToken)
    {
        var ticket = await IncidentEndpoints.FindVisibleAsync(db, user, id, cancellationToken);
        if (ticket is null)
        {
            return IncidentEndpoints.NotFound();
        }

        var me = user.Account!;
        var byStaff = user.Has(Permissions.IncidentsEdit);
        if (!byStaff && ticket.RequesterId != me.Id)
        {
            return (ProblemHttpResult)ApiProblem.Create(StatusCodes.Status403Forbidden, "forbidden", "Only staff who work on incidents and the incident's requester may write on it.");
        }

        var visibility = request.Visibility ?? CommentVisibility.Public;
        if (!byStaff && (visibility == CommentVisibility.Internal || request.MentionIds is { Count: > 0 } || request.CannedResponseId is not null))
        {
            return (ProblemHttpResult)ApiProblem.Create(StatusCodes.Status403Forbidden, "forbidden", "Requesters post public replies only.");
        }

        if (ticket.State == TicketState.Closed)
        {
            return IncidentEndpoints.Closed();
        }

        var body = request.Body!.Trim();
        if (body.Length == 0)
        {
            return UserEndpoints.Validation("body", "Write the reply or note.");
        }

        var language = LocalizationSettings.IsLanguage(request.Language) ? request.Language!
            : LocalizationSettings.IsLanguage(me.PreferredLanguage) ? me.PreferredLanguage!
            : await settings.GetAsync(LocalizationSettings.DefaultLanguage, cancellationToken);

        var mentionIds = (request.MentionIds ?? []).Distinct().ToList();
        if (mentionIds.Count > MaxMentions)
        {
            return UserEndpoints.Validation("mentionIds", $"Mention at most {MaxMentions} people.");
        }

        if (mentionIds.Count > 0)
        {
            var known = await Mentionable(db).Where(u => mentionIds.Contains(u.Id)).Select(u => u.Id).ToListAsync(cancellationToken);
            if (known.Count != mentionIds.Count)
            {
                return UserEndpoints.Validation("mentionIds", "Only active staff who can see incidents can be mentioned.");
            }
        }

        if (request.CannedResponseId is { } responseId
            && !await CannedResponseEndpoints.Usable(db, me.Id).AnyAsync(r => r.Id == responseId, cancellationToken))
        {
            return UserEndpoints.Validation("cannedResponseId", "Choose a canned response you can use.");
        }

        var now = DateTimeOffset.UtcNow;
        // Task E2: a staff reply on a ticket that came in by email goes out from its mailbox, in the
        // same thread; its Message-ID is fixed now, because comments never change.
        var email = byStaff && visibility == CommentVisibility.Public && ticket.MailboxId is { } mailboxId
            ? await EmailThread.ReplyHeadersAsync(db, ticket.Id, mailboxId, cancellationToken)
            : null;
        var comment = new TicketComment
        {
            TicketId = ticket.Id,
            Visibility = visibility,
            Body = body,
            Language = language,
            AuthorId = me.Id,
            ByStaff = byStaff,
            CannedResponseId = request.CannedResponseId,
            MailboxId = email?.MailboxId,
            EmailMessageId = email?.MessageId,
            EmailInReplyTo = email?.InReplyTo,
            CreatedAt = now,
        };
        db.TicketComments.Add(comment);
        foreach (var mentioned in mentionIds)
        {
            db.TicketCommentMentions.Add(new TicketCommentMention { CommentId = comment.Id, TicketId = ticket.Id, UserId = mentioned });
        }

        var transaction = db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(cancellationToken) : null;
        await using (transaction)
        {
            // Task B3 (decision 2026-11-24): a staff member's public reply is the response, whichever comes first.
            if (byStaff && visibility == CommentVisibility.Public && ticket.RespondedAt is null && ticket.RequesterId != me.Id)
            {
                ticket.RespondedAt = now;
                ticket.UpdatedAt = now;
                await http.RequestServices.GetRequiredService<SlaTracker>().UpdateAsync([ticket], now, cancellationToken);
            }
            else if (ticket.RequesterId == me.Id && ticket is { State: TicketState.OnHold, OnHoldReason: OnHoldReason.AwaitingRequester }
                && TicketWorkflow.Check(ticket, TicketState.InProgress, null, null) == TransitionError.None)
            {
                // Task E2, as for answers by email: the requester's answer moves a ticket waiting for them back to work.
                TicketWorkflow.Apply(ticket, TicketState.InProgress, null, null, now);
                await http.RequestServices.GetRequiredService<SlaTracker>().UpdateAsync([ticket], now, cancellationToken);
            }

            await db.SaveChangesAsync(cancellationToken);
            if (transaction is not null)
            {
                await transaction.CommitAsync(cancellationToken);
            }
        }

        var response = (await ResponsesAsync(db, [comment], staff: true, cancellationToken))[0];
        return TypedResults.Created($"/api/v1/incidents/{ticket.Id}/comments", response);
    }

    /// <summary>Active people whose roles let them see incidents: who can be @mentioned.</summary>
    internal static IQueryable<UserAccount> Mentionable(ZelvoDbContext db) =>
        db.Users.Where(u => u.Status == UserStatus.Active
            && u.Roles.Any(r => r.Role!.Permissions.Any(p => p.Permission == Permissions.IncidentsRead)));

    private static async Task<List<CommentResponse>> ResponsesAsync(ZelvoDbContext db, List<TicketComment> rows, bool staff, CancellationToken cancellationToken)
    {
        var ids = rows.Select(c => c.Id).ToList();
        var mentions = staff
            ? (await db.TicketCommentMentions.AsNoTracking().Where(m => ids.Contains(m.CommentId)).ToListAsync(cancellationToken)).ToLookup(m => m.CommentId, m => m.UserId)
            : Enumerable.Empty<TicketCommentMention>().ToLookup(m => m.CommentId, m => m.UserId);
        var names = await IncidentEndpoints.Names(db, rows.Select(c => c.AuthorId).Concat(mentions.SelectMany(m => m)), cancellationToken);
        // Task E2: files that came with an email, and how the email of a staff reply went (staff only).
        var files = (await db.TicketAttachments.AsNoTracking().Where(a => a.CommentId != null && ids.Contains(a.CommentId.Value))
            .Select(a => new { a.CommentId, a.Id, a.FileName }).ToListAsync(cancellationToken)).ToLookup(a => a.CommentId!.Value);
        var deliveries = staff
            ? (await db.OutboundEmails.AsNoTracking().Where(e => e.CommentId != null && ids.Contains(e.CommentId.Value))
                .Select(e => new { e.CommentId, e.Status }).ToListAsync(cancellationToken)).ToDictionary(e => e.CommentId!.Value, e => e.Status)
            : [];
        // Task E3: voice notes with their transcripts, and how a staff reply went out by WhatsApp, SMS or Teams.
        var voices = await db.VoiceNotes.AsNoTracking().Where(v => ids.Contains(v.CommentId)).ToDictionaryAsync(v => v.CommentId, cancellationToken);
        var messages = staff
            ? (await db.ChannelMessages.AsNoTracking().Where(m => m.Direction == Data.Channels.MessageDirection.Out && m.CommentId != null && ids.Contains(m.CommentId.Value))
                .Select(m => new { m.CommentId, m.Status }).ToListAsync(cancellationToken)).GroupBy(m => m.CommentId!.Value).ToDictionary(g => g.Key, g => g.Last().Status.ToString())
            : [];
        return [.. rows.Select(c => new CommentResponse(
            c.Id, c.TicketId, c.Visibility, c.Body, c.Language,
            new PersonResponse(c.AuthorId, names.GetValueOrDefault(c.AuthorId)), c.ByStaff,
            [.. mentions[c.Id].Select(u => new PersonResponse(u, names.GetValueOrDefault(u)))],
            staff ? c.CannedResponseId : null, c.CreatedAt, c.Source,
            [.. files[c.Id].Select(f => new CommentAttachmentResponse(f.Id, f.FileName))],
            staff && c.EmailMessageId is not null && c.ByStaff && c.Source == CommentSource.Web
                ? deliveries.TryGetValue(c.Id, out var status) ? status.ToString() : "Pending"
                : null,
            voices.TryGetValue(c.Id, out var voice) ? new VoiceNoteResponse(voice.AttachmentId, voice.Status, voice.Transcript, voice.Language) : null,
            messages.GetValueOrDefault(c.Id)))];
    }
}

/// <summary>A public reply or internal note.</summary>
/// <param name="Id">Comment id.</param>
/// <param name="TicketId">The incident.</param>
/// <param name="Visibility">Public (the requester sees it) or Internal (staff only).</param>
/// <param name="Body">The text, plain, as written.</param>
/// <param name="Language">Language it was written in: <c>en</c> or <c>ar</c>.</param>
/// <param name="Author">Who wrote it.</param>
/// <param name="ByStaff">Written by staff (else by the requester).</param>
/// <param name="Mentions">Staff mentioned (staff only).</param>
/// <param name="CannedResponseId">The canned response it started from (staff only).</param>
/// <param name="CreatedAt">When it was written.</param>
/// <param name="Source">Task E2: Web (in Zelvo), Email or Chat (a live chat's transcript).</param>
/// <param name="Attachments">Task E2: files that came with an email.</param>
/// <param name="EmailDelivery">Task E2, staff only: for a staff reply sent from the ticket's mailbox, Pending, Sent or Failed.</param>
/// <param name="VoiceNote">Task E3: the voice note this comment is, with its transcript.</param>
/// <param name="MessageDelivery">Task E3, staff only: how the reply went out by WhatsApp, SMS or Teams (Pending, Sent, Delivered, Read, Failed).</param>
internal sealed record CommentResponse(
    Guid Id, Guid TicketId, CommentVisibility Visibility, string Body, string Language, PersonResponse Author, bool ByStaff,
    IReadOnlyList<PersonResponse> Mentions, Guid? CannedResponseId, DateTimeOffset CreatedAt,
    CommentSource Source, IReadOnlyList<CommentAttachmentResponse> Attachments, string? EmailDelivery,
    VoiceNoteResponse? VoiceNote = null, string? MessageDelivery = null);

/// <summary>Task E3: a voice note sent by WhatsApp: the recording and what the AI heard.</summary>
/// <param name="AttachmentId">The recording (an attachment of the ticket).</param>
/// <param name="Status">Pending, Done, Failed, or Off (transcription was off).</param>
/// <param name="Transcript">What the AI heard.</param>
/// <param name="Language">The language it heard.</param>
internal sealed record VoiceNoteResponse(Guid AttachmentId, Data.Channels.TranscriptStatus Status, string? Transcript, string? Language);

/// <summary>A file that came with a comment (the attachments of an email).</summary>
internal sealed record CommentAttachmentResponse(Guid Id, string FileName);

/// <summary>A new public reply or internal note.</summary>
public sealed class CreateCommentRequest
{
    /// <summary>The text, plain, 1 to 20,000 characters.</summary>
    [Required]
    [StringLength(TicketComment.BodyMaxLength, MinimumLength = 1)]
    public string? Body { get; init; }

    /// <summary>Public (default) or Internal (staff only).</summary>
    public CommentVisibility? Visibility { get; init; }

    /// <summary>Language it is written in, <c>en</c> or <c>ar</c>; by default the author's language.</summary>
    [AllowedValues(null, LocalizedText.English, LocalizedText.Arabic)]
    public string? Language { get; init; }

    /// <summary>Staff only: people to @mention (active staff who can see incidents), at most 20.</summary>
    public List<Guid>? MentionIds { get; init; }

    /// <summary>Staff only: the canned response the text started from.</summary>
    public Guid? CannedResponseId { get; init; }
}
