using System.Security.Cryptography;
using System.Text.RegularExpressions;

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
