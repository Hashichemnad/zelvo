using Zelvo.Data.Auditing;
using Zelvo.Data.Tenancy;

namespace Zelvo.Data.Tickets;

/// <summary>
/// A message on a ticket's conversation (module 5, task B4): a public reply, which the requester
/// sees, or an internal note for staff only. Comments are never changed or removed, so the
/// conversation stays a faithful record. The body is plain text; the audit log keeps its length.
/// </summary>
public sealed class TicketComment : ITenantOwned, IAuditedEntity
{
    /// <summary>Maximum length of <see cref="Body"/>.</summary>
    public const int BodyMaxLength = 20_000;

    /// <summary>Unique id (UUID version 7, so ids sort by time).</summary>
    public Guid Id { get; init; } = Guid.CreateVersion7();

    /// <inheritdoc />
    public Guid TenantId { get; set; }

    /// <summary>The ticket.</summary>
    public Guid TicketId { get; init; }

    /// <summary>Who may read it.</summary>
    public CommentVisibility Visibility { get; init; }

    /// <summary>The message, plain text, in the language its author wrote it.</summary>
    [AuditLengthOnly]
    public required string Body { get; init; }

    /// <summary>
    /// Language the author wrote in (<c>en</c> or <c>ar</c>), so the agent and the requester can
    /// each work in their own language (LANG-7) and translation (E5) knows its source.
    /// </summary>
    public required string Language { get; init; }

    /// <summary>Who wrote it.</summary>
    public Guid AuthorId { get; init; }

    /// <summary>Whether the author worked on the ticket as staff (else they are its requester).</summary>
    public bool ByStaff { get; init; }

    /// <summary>The canned response it started from, if any.</summary>
    public Guid? CannedResponseId { get; init; }

    /// <summary>When it was written.</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>Who may read a comment.</summary>
public enum CommentVisibility
{
    /// <summary>The requester and staff: a public reply.</summary>
    Public,

    /// <summary>Staff only: an internal note.</summary>
    Internal,
}

/// <summary>A staff member @mentioned in a comment; they are notified (task B4).</summary>
public sealed class TicketCommentMention : ITenantOwned, IAuditedEntity
{
    /// <summary>The comment.</summary>
    public Guid CommentId { get; init; }

    /// <inheritdoc />
    public Guid TenantId { get; set; }

    /// <summary>The ticket of the comment (for lookups without a join).</summary>
    public Guid TicketId { get; init; }

    /// <summary>The person mentioned.</summary>
    public Guid UserId { get; init; }
}
