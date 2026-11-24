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

    /// <summary>How it was written: in Zelvo, by email or in a live chat (task E2).</summary>
    public CommentSource Source { get; init; } = CommentSource.Web;

    /// <summary>
    /// Task E2: the mailbox it came in through, or, for a staff reply on a ticket that came in by
    /// email, the mailbox it goes out from. Kept as a plain id (no foreign key): comments never
    /// change, and the record stays when a mailbox is removed.
    /// </summary>
    public Guid? MailboxId { get; init; }

    /// <summary>Its email <c>Message-ID</c> (without angle brackets): received, or given to the email that sends it.</summary>
    public string? EmailMessageId { get; init; }

    /// <summary>The <c>Message-ID</c> of the email it answers (<c>In-Reply-To</c>), for threading.</summary>
    public string? EmailInReplyTo { get; init; }

    /// <summary>The live chat it was written in, when a chat's transcript was added to the ticket (plain id, as <see cref="MailboxId"/>).</summary>
    public Guid? ChatSessionId { get; init; }

    /// <summary>Maximum length of <see cref="EmailMessageId"/> and <see cref="EmailInReplyTo"/>.</summary>
    public const int MessageIdMaxLength = 500;

    /// <summary>When it was written.</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>How a comment was written (task E2).</summary>
public enum CommentSource
{
    /// <summary>In Zelvo (the workspace, the portal or the API).</summary>
    Web,

    /// <summary>An email to one of the tenant's mailboxes.</summary>
    Email,

    /// <summary>A live chat, added to the ticket as its transcript.</summary>
    Chat,
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
