using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;

namespace Zelvo.Tickets;

/// <summary>
/// Zelvo's own Message-IDs (task E2): <c>zelvo.t{ticket}.{random}@{mailbox domain}</c>. Replies name
/// them in <c>In-Reply-To</c> and <c>References</c>, which finds the ticket even after the logs are
/// purged. The ticket id alone grants nothing: only the requester and staff are added to a ticket.
/// </summary>
public static partial class MessageIds
{
    /// <summary>A new Message-ID for an email about <paramref name="ticketId"/> from <paramref name="mailboxAddress"/>.</summary>
    public static string Create(Guid ticketId, string mailboxAddress)
    {
        ArgumentNullException.ThrowIfNull(mailboxAddress);
        var domain = mailboxAddress.Split('@') is [_, { Length: > 0 } host] ? host.ToLowerInvariant() : "zelvo.invalid";
        return $"zelvo.t{ticketId:N}.{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8))}@{domain}";
    }

    /// <summary>The ticket named by one of Zelvo's Message-IDs, or null.</summary>
    public static Guid? TicketOf(string? messageId) =>
        messageId is not null && Ours().Match(messageId) is { Success: true } match && Guid.TryParseExact(match.Groups[1].Value, "N", out var id) ? id : null;

    /// <summary>Whether <paramref name="messageId"/> is one of Zelvo's.</summary>
    public static bool IsOurs(string? messageId) => messageId is not null && messageId.StartsWith("zelvo.", StringComparison.Ordinal) && messageId.Contains('@', StringComparison.Ordinal);

    [GeneratedRegex(@"^zelvo\.t([0-9a-f]{32})\.[0-9a-f]+@", RegexOptions.CultureInvariant)]
    private static partial Regex Ours();
}

/// <summary>The email headers of a reply in a ticket's email thread (task E2).</summary>
/// <param name="MailboxId">The mailbox it goes out from.</param>
/// <param name="MessageId">Its own Message-ID.</param>
/// <param name="InReplyTo">The last email of the thread, which it answers.</param>
public sealed record EmailReplyHeaders(Guid MailboxId, string MessageId, string? InReplyTo);

/// <summary>A ticket's email thread (task E2): the emails of its conversation, oldest first.</summary>
public static class EmailThread
{
    /// <summary>Message-IDs of the thread: the email that opened the ticket, then every comment's email, oldest first.</summary>
    public static async Task<List<string>> IdsAsync(Zelvo.Data.ZelvoDbContext db, Guid ticketId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        var first = await EntityFrameworkQueryableExtensions.FirstOrDefaultAsync(
            db.InboundEmails.Where(e => e.TicketId == ticketId && e.MessageId != null && e.CommentId == null).OrderBy(e => e.Id).Select(e => e.MessageId!),
            cancellationToken).ConfigureAwait(false);
        var comments = await EntityFrameworkQueryableExtensions.ToListAsync(
            db.TicketComments.Where(c => c.TicketId == ticketId && c.EmailMessageId != null).OrderBy(c => c.CreatedAt).ThenBy(c => c.Id).Select(c => c.EmailMessageId!),
            cancellationToken).ConfigureAwait(false);
        return [.. (first is null ? comments : [first, .. comments]).Distinct(StringComparer.Ordinal)];
    }

    /// <summary>Headers for a new reply from <paramref name="mailboxId"/>, or null when the mailbox is gone.</summary>
    public static async Task<EmailReplyHeaders?> ReplyHeadersAsync(Zelvo.Data.ZelvoDbContext db, Guid ticketId, Guid mailboxId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        var address = await EntityFrameworkQueryableExtensions.FirstOrDefaultAsync(
            db.Mailboxes.Where(m => m.Id == mailboxId).Select(m => m.Address), cancellationToken).ConfigureAwait(false);
        if (address is null)
        {
            return null;
        }

        var thread = await IdsAsync(db, ticketId, cancellationToken).ConfigureAwait(false);
        return new(mailboxId, MessageIds.Create(ticketId, address), thread.Count > 0 ? thread[^1] : null);
    }
}
