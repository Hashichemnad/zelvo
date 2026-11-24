using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MimeKit;
using Zelvo.Data;
using Zelvo.Data.Auditing;
using Zelvo.Data.Channels;
using Zelvo.Data.Identity;
using Zelvo.Data.Tickets;
using Zelvo.Tickets;

namespace Zelvo.Channels;

/// <summary>
/// Turns one email of a mailbox into a ticket or a message of an existing ticket's conversation
/// (module 6, task E2; product owner decisions 2026-11-24). Run with the tenant set and the system
/// as audit actor; the caller marks the email read when this returns.
/// </summary>
public sealed partial class EmailIntake(
    ZelvoDbContext db, IncidentIntake incidents, IAttachmentStore attachments, IAuditLog audit, IOptions<AttachmentScanningOptions> scanning, TimeProvider time)
{
    /// <summary>Processes the email with <paramref name="providerId"/>; <paramref name="message"/> is null when it was too large to read.</summary>
    public async Task<InboundEmail> ProcessAsync(Mailbox mailbox, string providerId, MimeMessage? message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(mailbox);
        ArgumentNullException.ThrowIfNull(providerId);
        var now = time.GetUtcNow();
        var entry = await db.InboundEmails.SingleOrDefaultAsync(e => e.MailboxId == mailbox.Id && e.ProviderMessageId == providerId, cancellationToken)
            .ConfigureAwait(false);
        if (entry is { Outcome: not InboundEmailOutcome.Failed })
        {
            return entry;
        }

        if (entry is null)
        {
            entry = new InboundEmail { TenantId = mailbox.TenantId, MailboxId = mailbox.Id, ProviderMessageId = Cut(providerId, InboundEmail.ProviderIdMaxLength), ReceivedAt = now };
            db.InboundEmails.Add(entry);
        }

        entry.ProcessedAt = now;
        if (message is null)
        {
            return await FinishAsync(entry, InboundEmailOutcome.Rejected, "too-large", cancellationToken).ConfigureAwait(false);
        }

        var email = EmailParsing.Parse(message);
        entry.MessageId = email.MessageId;
        entry.FromAddress = email.FromAddress;
        entry.Subject = Cut(email.Subject, InboundEmail.SubjectMaxLength);

        if (email.AutoReason is { } auto)
        {
            return await FinishAsync(entry, InboundEmailOutcome.Ignored, auto, cancellationToken).ConfigureAwait(false);
        }

        if (email.FromAddress is not { } from)
        {
            return await FinishAsync(entry, InboundEmailOutcome.Rejected, "no-sender", cancellationToken).ConfigureAwait(false);
        }

        // Mail loops: Zelvo's own emails, and mail from one of the tenant's mailboxes.
        if (MessageIds.IsOurs(email.MessageId) || await db.Mailboxes.AnyAsync(m => m.Address == from, cancellationToken).ConfigureAwait(false))
        {
            return await FinishAsync(entry, InboundEmailOutcome.Ignored, "own-mail", cancellationToken).ConfigureAwait(false);
        }

        if (email.DmarcFailed)
        {
            return await FinishAsync(entry, InboundEmailOutcome.Rejected, "dmarc-failed", cancellationToken).ConfigureAwait(false);
        }

        // The same email sent to two of the tenant's mailboxes becomes one ticket.
        if (email.MessageId is { } messageId
            && (await db.TicketComments.AnyAsync(c => c.EmailMessageId == messageId, cancellationToken).ConfigureAwait(false)
                || await db.InboundEmails.AnyAsync(e => e.MessageId == messageId && e.Id != entry.Id
                    && (e.Outcome == InboundEmailOutcome.Created || e.Outcome == InboundEmailOutcome.Added || e.Outcome == InboundEmailOutcome.FollowUp), cancellationToken).ConfigureAwait(false)))
        {
            return await FinishAsync(entry, InboundEmailOutcome.Ignored, "duplicate", cancellationToken).ConfigureAwait(false);
        }

        var sender = await ContactAccounts.FindByEmailAsync(db, from, cancellationToken).ConfigureAwait(false);
        if (sender is { Status: not UserStatus.Active })
        {
            return await FinishAsync(entry, InboundEmailOutcome.Rejected, "sender-disabled", cancellationToken).ConfigureAwait(false);
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var thread = await FindThreadAsync(email, sender, cancellationToken).ConfigureAwait(false);
        string? skipped;
        if (thread is { State: not TicketState.Closed })
        {
            var staff = sender!.Id != thread.RequesterId;
            // The email becomes a public reply of the ticket's conversation (task B4's thread).
            var reply = new TicketComment
            {
                TenantId = mailbox.TenantId,
                TicketId = thread.Id,
                Visibility = CommentVisibility.Public,
                Body = BodyOf(email),
                Language = email.Language,
                AuthorId = sender.Id,
                ByStaff = staff,
                Source = CommentSource.Email,
                MailboxId = mailbox.Id,
                EmailMessageId = email.MessageId,
                EmailInReplyTo = email.ThreadIds.Count > 0 ? email.ThreadIds[0] : null,
                CreatedAt = now,
            };
            db.TicketComments.Add(reply);
            thread.MailboxId ??= mailbox.Id;
            thread.UpdatedAt = now;
            if (staff)
            {
                // As for replies in Zelvo (task B4): a staff member's first public reply is the response.
                thread.RespondedAt ??= now;
            }
            else
            {
                await ResumeAsync(thread, now, cancellationToken).ConfigureAwait(false);
            }

            await incidents.UpdateClocksAsync([thread], now, cancellationToken).ConfigureAwait(false);

            skipped = await AttachAsync(thread, reply.Id, sender.Id, email.Files, cancellationToken).ConfigureAwait(false);
            await incidents.IndexAsync([thread], cancellationToken).ConfigureAwait(false);
            entry.TicketId = thread.Id;
            entry.CommentId = reply.Id;
            await FinishAsync(entry, InboundEmailOutcome.Added, skipped, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return entry;
        }

        // A reply to a closed ticket becomes a follow-up (product owner decision 2026-11-24).
        if (thread is not null)
        {
            var followUp = await incidents.CreateAsync(mailbox.TenantId, new IncidentIntakeRequest(
                TitleOf(email, thread.Title), email.Text, TicketChannel.Email, thread.RequesterId,
                CategoryId: thread.CategoryId ?? mailbox.CategoryId, GroupId: mailbox.GroupId, MailboxId: mailbox.Id, FollowUpOfId: thread.Id), now, cancellationToken)
                .ConfigureAwait(false);
            skipped = await AttachAsync(followUp, null, thread.RequesterId, email.Files, cancellationToken).ConfigureAwait(false);
            entry.TicketId = followUp.Id;
            await FinishAsync(entry, InboundEmailOutcome.FollowUp, skipped, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return entry;
        }

        if (string.IsNullOrWhiteSpace(email.Subject) && string.IsNullOrWhiteSpace(email.Text))
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return await FinishAsync(entry, InboundEmailOutcome.Rejected, "empty", cancellationToken).ConfigureAwait(false);
        }

        if (sender is null)
        {
            var domain = from.Split('@')[^1];
            var allowed = mailbox.NewSenders switch
            {
                NewSenderPolicy.Anyone => true,
                NewSenderPolicy.AllowedDomains => mailbox.AllowedDomains.Any(d => string.Equals(d, domain, StringComparison.OrdinalIgnoreCase)),
                _ => false,
            };
            if (!allowed)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return await FinishAsync(entry, InboundEmailOutcome.Rejected, "sender-not-allowed", cancellationToken).ConfigureAwait(false);
            }

            sender = await ContactAccounts.CreateAsync(db, from, email.FromName, email.Language, cancellationToken).ConfigureAwait(false);
            audit.Add(AuditCategory.Create, "channels.email.requester-created", "user", sender.Id.ToString(),
                new Dictionary<string, object?>(StringComparer.Ordinal) { ["mailboxId"] = mailbox.Id.ToString() });
        }

        var ticket = await incidents.CreateAsync(mailbox.TenantId, new IncidentIntakeRequest(
            TitleOf(email, null), email.Text, TicketChannel.Email, sender.Id, CategoryId: mailbox.CategoryId, GroupId: mailbox.GroupId, MailboxId: mailbox.Id),
            now, cancellationToken).ConfigureAwait(false);
        skipped = await AttachAsync(ticket, null, sender.Id, email.Files, cancellationToken).ConfigureAwait(false);
        entry.TicketId = ticket.Id;
        await FinishAsync(entry, InboundEmailOutcome.Created, skipped, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return entry;
    }

    /// <summary>Records a failure of <paramref name="providerId"/> so it is tried again with the next read.</summary>
    public async Task RecordFailureAsync(Mailbox mailbox, string providerId, string reason, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(mailbox);
        db.ChangeTracker.Clear();
        var entry = await db.InboundEmails.SingleOrDefaultAsync(e => e.MailboxId == mailbox.Id && e.ProviderMessageId == providerId, cancellationToken).ConfigureAwait(false);
        if (entry is null)
        {
            entry = new InboundEmail { TenantId = mailbox.TenantId, MailboxId = mailbox.Id, ProviderMessageId = Cut(providerId, InboundEmail.ProviderIdMaxLength) };
            db.InboundEmails.Add(entry);
        }

        entry.Outcome = InboundEmailOutcome.Failed;
        entry.Reason = Cut(reason, InboundEmail.ReasonMaxLength);
        entry.Attempts++;
        entry.ProcessedAt = time.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The ticket an email answers: one named by Zelvo's Message-IDs or by earlier emails of its
    /// thread, else the number in brackets in the subject. Only the requester and staff are added to a
    /// ticket; anyone else's email becomes a ticket of its own.
    /// </summary>
    internal async Task<Ticket?> FindThreadAsync(ParsedEmail email, UserAccount? sender, CancellationToken cancellationToken)
    {
        if (sender is null)
        {
            return null;
        }

        Guid? ticketId = null;
        foreach (var id in email.ThreadIds)
        {
            ticketId = MessageIds.TicketOf(id)
                ?? await db.TicketComments.Where(c => c.EmailMessageId == id).Select(c => (Guid?)c.TicketId).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false)
                ?? await db.InboundEmails.Where(e => e.MessageId == id && e.TicketId != null).OrderByDescending(e => e.Id).Select(e => e.TicketId)
                    .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false)
                ?? await db.OutboundEmails.Where(e => e.MessageId == id).Select(e => (Guid?)e.TicketId).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            if (ticketId is not null)
            {
                break;
            }
        }

        Ticket? ticket = null;
        if (ticketId is { } found)
        {
            ticket = await db.Tickets.Include(t => t.Incident).SingleOrDefaultAsync(t => t.Id == found, cancellationToken).ConfigureAwait(false);
        }

        if (ticket is null && NumberInSubject().Match(email.Subject) is { Success: true } match)
        {
            var number = match.Groups[1].Value;
            ticket = await db.Tickets.Include(t => t.Incident).SingleOrDefaultAsync(t => t.Number == number, cancellationToken).ConfigureAwait(false);
        }

        if (ticket is null)
        {
            return null;
        }

        return ticket.RequesterId == sender.Id || IsStaff(sender) ? ticket : null;
    }

    private static bool IsStaff(UserAccount user) =>
        user.Roles.Any(r => r.Role?.Permissions.Any(p => p.Permission == IncidentPermissions.Edit) == true);

    /// <summary>A requester's answer moves a ticket waiting for them back to work.</summary>
    private async Task ResumeAsync(Ticket ticket, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (ticket is { State: TicketState.OnHold, OnHoldReason: OnHoldReason.AwaitingRequester }
            && TicketWorkflow.Check(ticket, TicketState.InProgress, null, null) == TransitionError.None)
        {
            TicketWorkflow.Apply(ticket, TicketState.InProgress, null, null, now);
            await db.Entry(ticket).Reference(t => t.Incident).LoadAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Attaches the files the tenant accepts; returns a note of those left out, or null.</summary>
    private async Task<string?> AttachAsync(Ticket ticket, Guid? commentId, Guid uploaderId, IReadOnlyList<EmailFile> files, CancellationToken cancellationToken)
    {
        if (files.Count == 0)
        {
            return null;
        }

        var policy = await TicketSettingKeys.ReadAsync(db, TicketSettingKeys.AttachmentPolicyKey, AttachmentPolicy.Default, p => p.IsValid(), cancellationToken).ConfigureAwait(false);
        var skipped = 0;
        foreach (var file in files)
        {
            var name = FileTypes.CleanFileName(file.FileName);
            var type = FileTypes.Find(FileTypes.ExtensionOf(name));
            if (file.Content.LongLength == 0 || file.Content.LongLength > policy.MaxBytes || name.Length == 0 || type is null
                || !policy.AllowedExtensions.Contains(type.Extension, StringComparer.Ordinal) || !type.Check(file.Content))
            {
                skipped++;
                continue;
            }

            var attachment = new TicketAttachment
            {
                TenantId = ticket.TenantId,
                TicketId = ticket.Id,
                CommentId = commentId,
                FileName = name,
                ContentType = type.ContentType,
                Size = file.Content.LongLength,
                Sha256 = Convert.ToHexStringLower(SHA256.HashData(file.Content)),
                ScanStatus = scanning.Value.Enabled ? AttachmentScanStatus.Pending : AttachmentScanStatus.NotScanned,
                UploadedById = uploaderId,
            };
            db.TicketAttachments.Add(attachment);
            attachments.Add(attachment.Id, file.Content);
            audit.Add(AuditCategory.Change, "tickets.attachment.added", "ticket", ticket.Id.ToString(),
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["details"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["attachmentId"] = attachment.Id.ToString(), ["fileName"] = attachment.FileName, ["size"] = attachment.Size, ["channel"] = "Email",
                    },
                });
        }

        return skipped == 0 ? null : $"files-skipped:{skipped}";
    }

    private async Task<InboundEmail> FinishAsync(InboundEmail entry, InboundEmailOutcome outcome, string? reason, CancellationToken cancellationToken)
    {
        entry.Outcome = outcome;
        entry.Reason = reason is null ? null : Cut(reason, InboundEmail.ReasonMaxLength);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return entry;
    }

    /// <summary>The subject without reply prefixes and the ticket number; else the text's first line; else <paramref name="fallback"/>.</summary>
    internal static string TitleOf(ParsedEmail email, string? fallback)
    {
        var subject = NumberInSubject().Replace(ReplyPrefix().Replace(email.Subject, string.Empty), string.Empty).Trim();
        if (subject.Length >= 3)
        {
            return subject;
        }

        var firstLine = email.Text.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? string.Empty;
        return firstLine.Length >= 3 ? Cut(firstLine, 120) : fallback ?? (subject.Length > 0 ? subject : firstLine);
    }

    private static string BodyOf(ParsedEmail email) =>
        IncidentIntake.Clean(email.Text.Length > 0 ? email.Text : email.FullText, TicketComment.BodyMaxLength) is { Length: > 0 } body ? body : "…";

    private static string Cut(string value, int max) => value.Length <= max ? value : value[..max];

    [GeneratedRegex(@"\[([A-Z][A-Z0-9]{0,9}-\d{6,})\]", RegexOptions.CultureInvariant)]
    private static partial Regex NumberInSubject();

    [GeneratedRegex(@"^\s*((re|fw|fwd|aw|wg|رد|إعادة توجيه|تحويل)\s*(\[\d+\])?\s*[:：]\s*)+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ReplyPrefix();
}
