using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Zelvo.Data;
using Zelvo.Data.Ai;
using Zelvo.Data.Auditing;
using Zelvo.Data.Channels;
using Zelvo.Data.Identity;
using Zelvo.Data.Tickets;
using Zelvo.Tickets;

namespace Zelvo.Channels.Messaging;

/// <summary>Teams' details of an incoming activity.</summary>
/// <param name="ServiceUrl">Where to answer.</param>
/// <param name="ConversationId">The personal conversation.</param>
/// <param name="UserId">The person's id in the bot's conversations.</param>
public sealed record TeamsDetails(string ServiceUrl, string ConversationId, string UserId);

/// <summary>A message that came in through a channel.</summary>
/// <param name="ProviderId">The provider's id of the message (taken once).</param>
/// <param name="Address">The sender: phone number in E.164, or the Teams account's Entra object id.</param>
/// <param name="Name">The sender's name as the app shows it.</param>
/// <param name="Kind">Text, voice note or file.</param>
/// <param name="Text">The text (or a file's caption).</param>
/// <param name="Media">The voice note or file, downloaded.</param>
/// <param name="FileName">The file's name, if it had one.</param>
/// <param name="Teams">Teams' details.</param>
public sealed record InboundMessage(
    string ProviderId, string Address, string? Name, ChannelMessageKind Kind, string? Text, MessagingMedia? Media = null, string? FileName = null, TeamsDetails? Teams = null);

/// <summary>
/// Turns a WhatsApp, SMS or Teams message into a ticket or a message of a ticket's conversation
/// (module 6, task E3). Run with the channel's tenant set and the system as audit actor.
/// <list type="bullet">
/// <item>The sender is found by phone number (WhatsApp, SMS) or, for Teams, by the Entra account's
/// email, linked once; unknown senders get a requester account when the channel accepts anyone.</item>
/// <item>A message that starts with the number of one of the sender's own tickets (<c>INC-000123 …</c>) goes to that ticket.</item>
/// <item>WhatsApp and SMS: other messages go to the sender's latest ticket of the channel while
/// it is not closed; Teams: other messages raise a new incident each.</item>
/// <item>A message to a closed ticket becomes a follow-up incident, as for email.</item>
/// <item>Voice notes are kept as recordings and transcribed by the AI when that feature is on.</item>
/// </list>
/// </summary>
public sealed partial class MessagingIntake(
    ZelvoDbContext db, IncidentIntake incidents, IAttachmentStore attachments, IAuditLog audit, IOptions<AttachmentScanningOptions> scanning,
    IMessagingClients clients, TimeProvider time)
{
    /// <summary>Feature key of voice-note transcription in Settings → AI (task E3).</summary>
    public const string TranscriptionFeature = "transcription";

    /// <summary>Takes <paramref name="message"/> in; returns its log entry (saved).</summary>
    public async Task<ChannelMessage> ProcessAsync(MessagingChannel channel, MessagingSecrets secrets, InboundMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(message);
        var now = time.GetUtcNow();
        var providerId = Cut(message.ProviderId, ChannelMessage.ProviderIdMaxLength);
        var existing = await db.ChannelMessages.FirstOrDefaultAsync(
            m => m.ChannelId == channel.Id && m.Direction == MessageDirection.In && m.ProviderMessageId == providerId, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            return existing;
        }

        var entry = new ChannelMessage
        {
            TenantId = channel.TenantId,
            ChannelId = channel.Id,
            Direction = MessageDirection.In,
            Kind = message.Kind,
            ProviderMessageId = providerId,
            Text = message.Text is null ? null : Cut(message.Text, ChannelMessage.TextMaxLength),
            Status = ChannelMessageStatus.Received,
            CreatedAt = now,
        };
        db.ChannelMessages.Add(entry);
        channel.LastInboundAt = now;

        var contact = await db.MessagingContacts.FirstOrDefaultAsync(c => c.ChannelId == channel.Id && c.Address == message.Address, cancellationToken).ConfigureAwait(false);
        if (contact is null)
        {
            contact = new MessagingContact { TenantId = channel.TenantId, ChannelId = channel.Id, Address = message.Address, CreatedAt = now };
            db.MessagingContacts.Add(contact);
        }

        entry.ContactId = contact.Id;
        contact.LastInboundAt = now;
        if (message.Teams is { } teams)
        {
            contact.TeamsServiceUrl = Cut(teams.ServiceUrl, MessagingContact.TeamsValueMaxLength);
            contact.TeamsConversationId = Cut(teams.ConversationId, MessagingContact.TeamsValueMaxLength);
            contact.TeamsUserId = Cut(teams.UserId, MessagingContact.TeamsValueMaxLength);
        }

        if (!channel.Active)
        {
            return await FinishAsync(entry, ChannelMessageStatus.Ignored, "channel-off", cancellationToken).ConfigureAwait(false);
        }

        var text = message.Text is null ? null : IncidentIntake.Clean(message.Text, TicketComment.BodyMaxLength);
        if (string.IsNullOrEmpty(text) && message.Media is null)
        {
            return await FinishAsync(entry, ChannelMessageStatus.Ignored, message.Kind == ChannelMessageKind.Event ? "event" : "empty", cancellationToken).ConfigureAwait(false);
        }

        var sender = await SenderAsync(channel, secrets, contact, message, cancellationToken).ConfigureAwait(false);
        if (sender is null)
        {
            // As for email: no automatic answer, only the channel log.
            return await FinishAsync(entry, ChannelMessageStatus.Rejected, "sender-not-allowed", cancellationToken).ConfigureAwait(false);
        }

        if (sender.Status != UserStatus.Active)
        {
            return await FinishAsync(entry, ChannelMessageStatus.Rejected, "sender-disabled", cancellationToken).ConfigureAwait(false);
        }

        contact.UserId = sender.Id;
        var language = text is { Length: > 0 } && EmailParsing.LooksArabic(text) ? "ar" : sender.PreferredLanguage is "ar" ? "ar" : "en";
        var ticketChannel = channel.Kind switch { MessagingKind.WhatsApp => TicketChannel.WhatsApp, MessagingKind.Sms => TicketChannel.Sms, _ => TicketChannel.Teams };
        var source = channel.Kind switch { MessagingKind.WhatsApp => CommentSource.WhatsApp, MessagingKind.Sms => CommentSource.Sms, _ => CommentSource.Teams };

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var (thread, rest) = await ThreadAsync(channel, contact, sender, text, cancellationToken).ConfigureAwait(false);
        text = rest;
        if (string.IsNullOrEmpty(text) && message.Media is null)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return await FinishAsync(entry, ChannelMessageStatus.Ignored, "empty", cancellationToken).ConfigureAwait(false);
        }

        Ticket ticket;
        TicketComment? comment = null;
        if (thread is { State: not TicketState.Closed })
        {
            ticket = thread;
            comment = AddComment(ticket, sender, text, language, source, channel, now);
            ticket.MessagingContactId ??= contact.Id;
            ticket.UpdatedAt = now;
            if (ticket is { State: TicketState.OnHold, OnHoldReason: OnHoldReason.AwaitingRequester }
                && TicketWorkflow.Check(ticket, TicketState.InProgress, null, null) == TransitionError.None)
            {
                TicketWorkflow.Apply(ticket, TicketState.InProgress, null, null, now);
                await db.Entry(ticket).Reference(t => t.Incident).LoadAsync(cancellationToken).ConfigureAwait(false);
            }

            await incidents.UpdateClocksAsync([ticket], now, cancellationToken).ConfigureAwait(false);
            await incidents.IndexAsync([ticket], cancellationToken).ConfigureAwait(false);
        }
        else
        {
            var title = text is { Length: > 0 } ? TitleOf(text) : $"{channel.Name}: {sender.DisplayName ?? message.Name ?? message.Address}";
            ticket = await incidents.CreateAsync(channel.TenantId, new IncidentIntakeRequest(
                title, text, ticketChannel, sender.Id, CategoryId: thread?.CategoryId ?? channel.CategoryId, GroupId: channel.GroupId,
                FollowUpOfId: thread?.Id, MessagingContactId: contact.Id), now, cancellationToken).ConfigureAwait(false);
            // The first message is the description; a voice note or file needs a comment of its own.
            if (message.Media is not null)
            {
                comment = AddComment(ticket, sender, null, language, source, channel, now);
            }
        }

        contact.TicketId = ticket.Id;
        entry.TicketId = ticket.Id;
        entry.CommentId = comment?.Id;
        string? reason = null;
        if (message.Media is { } media)
        {
            reason = await AttachAsync(ticket, comment!, sender.Id, message, media, channel, cancellationToken).ConfigureAwait(false);
        }

        await FinishAsync(entry, ChannelMessageStatus.Received, reason, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return entry;
    }

    /// <summary>A delivery report: marks the outgoing message sent, delivered, read or failed.</summary>
    public async Task ReportAsync(MessagingChannel channel, string providerId, ChannelMessageStatus status, string? error, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(channel);
        var message = await db.ChannelMessages.FirstOrDefaultAsync(
            m => m.ChannelId == channel.Id && m.Direction == MessageDirection.Out && m.ProviderMessageId == providerId, cancellationToken).ConfigureAwait(false);
        // Reports may arrive out of order: never step back from read to delivered.
        if (message is null || Rank(status) <= Rank(message.Status))
        {
            return;
        }

        message.Status = status;
        if (error is not null)
        {
            message.Reason = Cut(error, ChannelMessage.ReasonMaxLength);
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private static int Rank(ChannelMessageStatus status) => status switch
    {
        ChannelMessageStatus.Sent => 1,
        ChannelMessageStatus.Delivered => 2,
        ChannelMessageStatus.Read => 3,
        ChannelMessageStatus.Failed => 4,
        _ => 0,
    };

    // Who wrote: the contact's account, else found by phone or Teams email, else made (when allowed).
    private async Task<UserAccount?> SenderAsync(MessagingChannel channel, MessagingSecrets secrets, MessagingContact contact, InboundMessage message, CancellationToken cancellationToken)
    {
        if (contact.UserId is { } known && await db.Users.FirstOrDefaultAsync(u => u.Id == known, cancellationToken).ConfigureAwait(false) is { } user)
        {
            return user;
        }

        UserAccount? found;
        string? email = null;
        if (channel.Kind == MessagingKind.Teams)
        {
            (found, email) = message.Teams is { } teams ? await TeamsAccountAsync(channel, secrets, teams, cancellationToken).ConfigureAwait(false) : (null, null);
        }
        else
        {
            found = await ContactAccounts.FindByPhoneAsync(db, message.Address, cancellationToken).ConfigureAwait(false);
        }

        if (found is not null)
        {
            audit.Add(AuditCategory.Change, "channels.messaging.contact-linked", "user", found.Id.ToString(),
                new Dictionary<string, object?>(StringComparer.Ordinal) { ["channelId"] = channel.Id.ToString(), ["kind"] = channel.Kind.ToString() });
            return found;
        }

        if (channel.NewSenders != MessagingSenders.Anyone || (channel.Kind == MessagingKind.Teams && email is null))
        {
            return null;
        }

        var language = message.Text is { Length: > 0 } t && EmailParsing.LooksArabic(t) ? "ar" : null;
        var account = channel.Kind == MessagingKind.Teams
            ? await ContactAccounts.CreateAsync(db, email!, message.Name, language, cancellationToken).ConfigureAwait(false)
            : await ContactAccounts.CreateForPhoneAsync(db, message.Address, message.Name, language, cancellationToken).ConfigureAwait(false);
        audit.Add(AuditCategory.Create, "channels.messaging.requester-created", "user", account.Id.ToString(),
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["channelId"] = channel.Id.ToString(), ["kind"] = channel.Kind.ToString() });
        return account;
    }

    /// <summary>
    /// The Zelvo account of a Teams user: the one with the email or sign-in name that Teams reports
    /// for them in the customer's own directory (Entra), if any.
    /// </summary>
    public async Task<UserAccount?> FindTeamsUserAsync(MessagingChannel channel, MessagingSecrets secrets, TeamsDetails teams, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(teams);
        var (user, _) = await TeamsAccountAsync(channel, secrets, teams, cancellationToken).ConfigureAwait(false);
        if (user is not null)
        {
            audit.Add(AuditCategory.Change, "channels.messaging.contact-linked", "user", user.Id.ToString(),
                new Dictionary<string, object?>(StringComparer.Ordinal) { ["channelId"] = channel.Id.ToString(), ["kind"] = channel.Kind.ToString() });
        }

        return user;
    }

    private async Task<(UserAccount? User, string? Email)> TeamsAccountAsync(MessagingChannel channel, MessagingSecrets secrets, TeamsDetails teams, CancellationToken cancellationToken)
    {
        TeamsMember? member;
        try
        {
            member = await clients.TeamsMemberAsync(channel, secrets, teams.ServiceUrl, teams.ConversationId, teams.UserId, cancellationToken).ConfigureAwait(false);
        }
        catch (MessagingException)
        {
            return (null, null);
        }

        var email = member?.Email ?? member?.UserPrincipalName;
        var user = email is { Length: > 0 } && email.Contains('@', StringComparison.Ordinal)
            ? await ContactAccounts.FindByEmailAsync(db, email, cancellationToken).ConfigureAwait(false)
            : null;
        return (user, email);
    }

    // The ticket a message belongs to, and the text without a leading ticket number.
    private async Task<(Ticket? Ticket, string? Text)> ThreadAsync(MessagingChannel channel, MessagingContact contact, UserAccount sender, string? text, CancellationToken cancellationToken)
    {
        if (text is { Length: > 0 } && LeadingNumber().Match(text) is { Success: true } match)
        {
            var number = match.Groups[1].Value;
            // Only the sender's own tickets: nobody writes into someone else's by guessing a number.
            var named = await db.Tickets.Include(t => t.Incident).FirstOrDefaultAsync(t => t.Number == number && t.RequesterId == sender.Id, cancellationToken)
                .ConfigureAwait(false);
            if (named is not null)
            {
                return (named, text[match.Length..].TrimStart(' ', ':', '-', '\n'));
            }
        }

        if (channel.Kind == MessagingKind.Teams || contact.TicketId is not { } current)
        {
            return (null, text);
        }

        var ticket = await db.Tickets.Include(t => t.Incident).FirstOrDefaultAsync(t => t.Id == current && t.RequesterId == sender.Id, cancellationToken).ConfigureAwait(false);
        return (ticket, text);
    }

    private TicketComment AddComment(Ticket ticket, UserAccount sender, string? text, string language, CommentSource source, MessagingChannel channel, DateTimeOffset now)
    {
        var comment = new TicketComment
        {
            TenantId = channel.TenantId,
            TicketId = ticket.Id,
            Visibility = CommentVisibility.Public,
            // A voice note without words is the recording (shown with its transcript).
            Body = text ?? string.Empty,
            Language = language,
            AuthorId = sender.Id,
            ByStaff = false,
            Source = source,
            MessagingChannelId = channel.Id,
            CreatedAt = now,
        };
        db.TicketComments.Add(comment);
        return comment;
    }

    private async Task<string?> AttachAsync(Ticket ticket, TicketComment comment, Guid uploaderId, InboundMessage message, MessagingMedia media, MessagingChannel channel, CancellationToken cancellationToken)
    {
        var voice = message.Kind == ChannelMessageKind.Voice;
        var extension = ExtensionFor(media.ContentType, message.FileName);
        var type = FileTypes.Find(extension);
        var policy = await TicketSettingKeys.ReadAsync(db, TicketSettingKeys.AttachmentPolicyKey, AttachmentPolicy.Default, p => p.IsValid(), cancellationToken).ConfigureAwait(false);
        // Voice notes are the message itself: accepted as audio whatever the allowlist; other files follow the tenant's rules.
        var allowed = type is not null && (voice ? FileTypes.AudioExtensions.Contains(type.Extension, StringComparer.Ordinal) : policy.AllowedExtensions.Contains(type.Extension, StringComparer.Ordinal));
        if (!allowed || media.Content.LongLength == 0 || media.Content.LongLength > policy.MaxBytes || !type!.Check(media.Content))
        {
            return "file-skipped";
        }

        var name = FileTypes.CleanFileName(message.FileName is { Length: > 0 } given ? given : $"{(voice ? "voice-note" : "file")}-{comment.CreatedAt:yyyyMMdd-HHmmss}.{type.Extension}");
        var attachment = new TicketAttachment
        {
            TenantId = ticket.TenantId,
            TicketId = ticket.Id,
            CommentId = comment.Id,
            FileName = name,
            ContentType = type.ContentType,
            Size = media.Content.LongLength,
            Sha256 = Convert.ToHexStringLower(SHA256.HashData(media.Content)),
            ScanStatus = scanning.Value.Enabled ? AttachmentScanStatus.Pending : AttachmentScanStatus.NotScanned,
            UploadedById = uploaderId,
        };
        db.TicketAttachments.Add(attachment);
        attachments.Add(attachment.Id, media.Content);
        audit.Add(AuditCategory.Change, "tickets.attachment.added", "ticket", ticket.Id.ToString(), new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["details"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["attachmentId"] = attachment.Id.ToString(), ["fileName"] = attachment.FileName, ["size"] = attachment.Size, ["channel"] = channel.Kind.ToString(),
            },
        });

        if (voice)
        {
            // Transcription is its own AI feature, off by default: the audio goes to the provider
            // unmasked (product owner decision 2026-11-25).
            var on = await db.AiFeatureSettings.AsNoTracking().AnyAsync(f => f.Feature == TranscriptionFeature && f.Enabled, cancellationToken).ConfigureAwait(false);
            db.VoiceNotes.Add(new VoiceNote
            {
                TenantId = ticket.TenantId,
                CommentId = comment.Id,
                TicketId = ticket.Id,
                AttachmentId = attachment.Id,
                Status = on ? TranscriptStatus.Pending : TranscriptStatus.Off,
                CreatedAt = comment.CreatedAt,
            });
            if (on)
            {
                db.AiJobs.Add(new AiJob { TenantId = ticket.TenantId, Kind = AiJobKind.Transcription, TicketId = ticket.Id, CommentId = comment.Id, CreatedAt = comment.CreatedAt, NextAttemptAt = comment.CreatedAt });
            }
        }

        return null;
    }

    private static string? ExtensionFor(string contentType, string? fileName)
    {
        var media = contentType.Split(';')[0].Trim().ToLowerInvariant();
        return media switch
        {
            "audio/ogg" or "audio/opus" => "ogg",
            "audio/amr" => "amr",
            "audio/mpeg" or "audio/mp3" => "mp3",
            "audio/mp4" or "audio/aac" or "audio/x-m4a" => "m4a",
            "audio/wav" or "audio/x-wav" => "wav",
            _ => fileName is { Length: > 0 } ? FileTypes.ExtensionOf(fileName) : FileTypes.All.FirstOrDefault(t => t.ContentType.StartsWith(media, StringComparison.Ordinal))?.Extension,
        };
    }

    private async Task<ChannelMessage> FinishAsync(ChannelMessage entry, ChannelMessageStatus status, string? reason, CancellationToken cancellationToken)
    {
        entry.Status = status;
        entry.Reason = reason is null ? null : Cut(reason, ChannelMessage.ReasonMaxLength);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return entry;
    }

    /// <summary>The first line of <paramref name="text"/>, as a title.</summary>
    internal static string TitleOf(string text)
    {
        var line = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? text;
        return line.Length <= 120 ? line : string.Concat(line.AsSpan(0, 119), "…");
    }

    private static string Cut(string value, int max) => value.Length <= max ? value : value[..max];

    [GeneratedRegex(@"^\s*\[?([A-Z][A-Z0-9]{0,9}-\d{6,})\]?", RegexOptions.CultureInvariant)]
    private static partial Regex LeadingNumber();
}
