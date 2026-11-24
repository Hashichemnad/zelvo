using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;
using Zelvo.Api.Conventions;
using Zelvo.Api.Identity;
using Zelvo.Api.Incidents;
using Zelvo.Api.Integrations;
using Zelvo.Api.Tenancy;
using Zelvo.Data;
using Zelvo.Data.Channels;
using Zelvo.Data.Tickets;
using Zelvo.Tickets;
using Zelvo.Tickets.Sla;

namespace Zelvo.Api.Channels;

/// <summary>
/// The public conversation of an incident (task E2, product owner decision 2026-11-24): messages
/// from the requester and staff written in the portal, by email or in a chat. Staff replies reach
/// the requester by email: from the ticket's mailbox for tickets that came in by email, otherwise as
/// a notification. Internal notes, mentions and canned responses come with the agent workspace (B4).
/// </summary>
internal static class TicketMessageEndpoints
{
    public static RouteGroupBuilder MapTicketMessageEndpoints(this RouteGroupBuilder group)
    {
        var messages = group.MapGroup("/incidents/{id:guid}/messages").WithTags("Incidents").RequireTenant();
        messages.MapGet("/", ListAsync)
            .WithName("ListIncidentMessages")
            .WithSummary("The incident's conversation, oldest first: messages of the requester and staff from the portal, email and chat, with their attachments. Staff (incidents.read) and the requester.")
            .RequireAuthorization()
            .Produces<IReadOnlyList<TicketMessageResponse>>();

        messages.MapPost("/", PostAsync)
            .WithName("PostIncidentMessage")
            .RequirePerson()
            .WithSummary("Write in the conversation. Staff with incidents.edit reply to the requester (by email from the ticket's mailbox when it came in by email; the first reply counts as the response for SLAs); the requester answers (an incident waiting for them goes back to In progress). Not on closed incidents; audited.")
            .RequireAuthorization()
            .Produces<TicketMessageResponse>(StatusCodes.Status201Created)
            .ProducesValidationProblem();
        return group;
    }

    internal static async Task<IResult> ListAsync(Guid id, ZelvoDbContext db, CurrentUser user, CancellationToken cancellationToken)
    {
        var ticket = await IncidentEndpoints.FindVisibleAsync(db, user, id, cancellationToken);
        if (ticket is null)
        {
            return IncidentEndpoints.NotFound();
        }

        var staff = user.Has(Permissions.IncidentsRead);
        var messages = await db.TicketMessages.AsNoTracking().Include(m => m.Author)
            .Where(m => m.TicketId == id).OrderBy(m => m.CreatedAt).ThenBy(m => m.Id).Take(1000).ToListAsync(cancellationToken);
        var files = await db.TicketAttachments.AsNoTracking().Where(a => a.TicketId == id && a.MessageId != null)
            .Select(a => new { a.MessageId, a.Id, a.FileName, a.ScanStatus }).ToListAsync(cancellationToken);
        return TypedResults.Ok(messages.Select(m => TicketMessageResponse.From(m, staff,
            [.. files.Where(f => f.MessageId == m.Id).Select(f => new MessageAttachmentResponse(f.Id, f.FileName, f.ScanStatus))])).ToList());
    }

    internal static async Task<IResult> PostAsync(
        Guid id, PostTicketMessageRequest request, ZelvoDbContext db, CurrentUser user, IncidentIntake intake, SlaTracker sla, CancellationToken cancellationToken)
    {
        var ticket = await IncidentEndpoints.FindVisibleAsync(db, user, id, cancellationToken);
        if (ticket is null)
        {
            return IncidentEndpoints.NotFound();
        }

        var me = user.Account!.Id;
        var staff = user.Has(Permissions.IncidentsEdit) && ticket.RequesterId != me;
        if (!staff && ticket.RequesterId != me)
        {
            return ApiProblem.Create(StatusCodes.Status403Forbidden, "forbidden", "Only staff who work on incidents and the requester write in the conversation.");
        }

        if (ticket.State == TicketState.Closed)
        {
            return IncidentEndpoints.Closed();
        }

        var body = IncidentIntake.Clean(request.Body!, TicketMessage.BodyMaxLength);
        if (body.Length == 0)
        {
            return UserEndpoints.Validation("body", "Write a message.");
        }

        var now = DateTimeOffset.UtcNow;
        var message = new TicketMessage
        {
            TicketId = ticket.Id,
            AuthorKind = staff ? MessageAuthorKind.Staff : MessageAuthorKind.Requester,
            AuthorId = me,
            Channel = TicketChannel.Portal,
            Body = body,
            MailboxId = staff ? ticket.MailboxId : null,
            Delivery = staff && ticket.MailboxId is not null ? MessageDelivery.Pending : MessageDelivery.None,
            CreatedAt = now,
        };
        db.TicketMessages.Add(message);
        ApplyToTicket(ticket, staff, now);
        await intake.IndexAsync([ticket], cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await sla.UpdateAsync([ticket], now, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        var saved = await db.TicketMessages.AsNoTracking().Include(m => m.Author).SingleAsync(m => m.Id == message.Id, cancellationToken);
        return TypedResults.Created($"/api/v1/incidents/{ticket.Id}/messages", TicketMessageResponse.From(saved, user.Has(Permissions.IncidentsRead), []));
    }

    /// <summary>
    /// What a new message does to its ticket: a staff reply is the first response when there was
    /// none (task B3, from E2 on); a requester's answer moves a ticket waiting for them back to work.
    /// </summary>
    internal static void ApplyToTicket(Ticket ticket, bool staff, DateTimeOffset now)
    {
        ticket.UpdatedAt = now;
        if (staff)
        {
            ticket.RespondedAt ??= now;
        }
        else if (ticket is { State: TicketState.OnHold, OnHoldReason: OnHoldReason.AwaitingRequester }
            && TicketWorkflow.Check(ticket, TicketState.InProgress, null, null) == TransitionError.None)
        {
            TicketWorkflow.Apply(ticket, TicketState.InProgress, null, null, now);
        }
    }
}

/// <summary>A message to write in an incident's conversation.</summary>
public sealed class PostTicketMessageRequest
{
    /// <summary>The text (plain text).</summary>
    [Required]
    [StringLength(TicketMessage.BodyMaxLength, MinimumLength = 1)]
    public string? Body { get; init; }
}

/// <summary>A file that came with a message.</summary>
internal sealed record MessageAttachmentResponse(Guid Id, string FileName, AttachmentScanStatus ScanStatus);

/// <summary>A message of an incident's conversation.</summary>
/// <param name="Delivery">Staff only: how the email of a staff reply went (None, Pending, Sent, Failed).</param>
internal sealed record TicketMessageResponse(
    Guid Id, MessageAuthorKind AuthorKind, PersonResponse? Author, TicketChannel Channel, string Body, MessageDelivery? Delivery, DateTimeOffset CreatedAt,
    IReadOnlyList<MessageAttachmentResponse> Attachments)
{
    public static TicketMessageResponse From(TicketMessage m, bool staff, IReadOnlyList<MessageAttachmentResponse> attachments) => new(
        m.Id, m.AuthorKind, m.AuthorId is { } author ? new PersonResponse(author, m.Author?.DisplayName ?? (staff ? m.Author?.Email : null)) : null,
        m.Channel, m.Body, staff ? m.Delivery : null, m.CreatedAt, attachments);
}
