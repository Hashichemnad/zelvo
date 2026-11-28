using Zelvo.Data.Auditing;
using Zelvo.Data.Localization;
using Zelvo.Data.Tenancy;

namespace Zelvo.Data.Changes;

/// <summary>The two boards of a tenant (task H4).</summary>
public enum ChangeBoardKind
{
    /// <summary>The change advisory board: meets, votes, and its chair decides (decision 8).</summary>
    Advisory,

    /// <summary>The emergency board: a smaller membership that decides emergency changes without a meeting, within hours.</summary>
    Emergency,
}

/// <summary>
/// A change board of a tenant (task H4): its members, who hold <c>changes.approve</c>, and for the
/// advisory board the chair and deputy chair who record its decisions.
/// </summary>
public sealed class ChangeBoard : ITenantOwned, IAuditedEntity
{
    /// <summary>Maximum length of each language of <see cref="Name"/>.</summary>
    public const int NameMaxLength = 200;

    /// <summary>Most members of a board.</summary>
    public const int MaxMembers = 50;

    /// <summary>Unique id (UUID version 7).</summary>
    public Guid Id { get; init; } = Guid.CreateVersion7();

    /// <inheritdoc />
    public Guid TenantId { get; set; }

    /// <summary>Advisory or emergency (one of each per tenant).</summary>
    public ChangeBoardKind Kind { get; init; }

    /// <summary>Name in English and Arabic.</summary>
    public required LocalizedText Name { get; set; }

    /// <summary>Who chairs the meetings and records the board's decisions.</summary>
    public Guid? ChairId { get; set; }

    /// <summary>Who chairs when the chair is away.</summary>
    public Guid? DeputyChairId { get; set; }

    /// <summary>Emergency board: hours within which a decision is expected.</summary>
    public int DecideWithinHours { get; set; } = 4;

    /// <summary>Members.</summary>
    public List<ChangeBoardMember> Members { get; init; } = [];

    /// <summary>When it last changed.</summary>
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Optimistic concurrency token (PostgreSQL <c>xmin</c>).</summary>
    public uint Version { get; init; }
}

/// <summary>A member of a change board.</summary>
public sealed class ChangeBoardMember : ITenantOwned, IAuditedEntity
{
    /// <inheritdoc />
    public Guid TenantId { get; set; }

    /// <summary>The board.</summary>
    public Guid BoardId { get; init; }

    /// <summary>The member.</summary>
    public Guid UserId { get; init; }
}

/// <summary>Where a board meeting is.</summary>
public enum CabMeetingState
{
    /// <summary>Planned: members are invited, vote and the chair decides.</summary>
    Planned,

    /// <summary>Held: the minutes are final.</summary>
    Held,

    /// <summary>Called off; members got a cancellation.</summary>
    Cancelled,
}

/// <summary>
/// A meeting of the change advisory board (task H4): bilingual title, date, members and chair, the
/// agenda built from the changes waiting for the board, and the minutes.
/// </summary>
public sealed class CabMeeting : ITenantOwned, IAuditedEntity
{
    /// <summary>Maximum length of each language of <see cref="Title"/>.</summary>
    public const int TitleMaxLength = 200;

    /// <summary>Maximum length of <see cref="Location"/>.</summary>
    public const int LocationMaxLength = 500;

    /// <summary>Maximum length of <see cref="Minutes"/>.</summary>
    public const int MinutesMaxLength = 20_000;

    /// <summary>Unique id (UUID version 7); the calendar invitation's UID.</summary>
    public Guid Id { get; init; } = Guid.CreateVersion7();

    /// <inheritdoc />
    public Guid TenantId { get; set; }

    /// <summary>Title in English and Arabic.</summary>
    public required LocalizedText Title { get; set; }

    /// <summary>Start.</summary>
    public DateTimeOffset StartsAt { get; set; }

    /// <summary>End.</summary>
    public DateTimeOffset EndsAt { get; set; }

    /// <summary>A room or an online meeting address.</summary>
    public string? Location { get; set; }

    /// <summary>Who chairs it (the board's chair or deputy); records the decisions.</summary>
    public Guid ChairId { get; set; }

    /// <summary>Planned, held or cancelled.</summary>
    public CabMeetingState State { get; set; } = CabMeetingState.Planned;

    /// <summary>The meeting's minutes.</summary>
    public string? Minutes { get; set; }

    /// <summary>Calendar sequence: grows with each change sent to the members.</summary>
    public int Sequence { get; set; }

    /// <summary>Who planned it.</summary>
    public Guid? CreatedById { get; init; }

    /// <summary>When it was planned.</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>When it last changed.</summary>
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Optimistic concurrency token (PostgreSQL <c>xmin</c>).</summary>
    public uint Version { get; init; }
}

/// <summary>A member invited to a meeting.</summary>
public sealed class CabMeetingMember : ITenantOwned, IAuditedEntity
{
    /// <inheritdoc />
    public Guid TenantId { get; set; }

    /// <summary>The meeting.</summary>
    public Guid MeetingId { get; init; }

    /// <summary>The member.</summary>
    public Guid UserId { get; init; }
}

/// <summary>The chair's decision on an agenda item.</summary>
public enum CabDecision
{
    /// <summary>Approved: the change's board level is approved through C3.</summary>
    Approved,

    /// <summary>Rejected: the change is rejected through C3, with the reason.</summary>
    Rejected,

    /// <summary>Deferred to a later meeting; the change keeps waiting for the board.</summary>
    Deferred,
}

/// <summary>A change on a meeting's agenda, with the chair's decision and the minutes about it.</summary>
public sealed class CabAgendaItem : ITenantOwned, IAuditedEntity
{
    /// <summary>Maximum length of <see cref="Comment"/> and <see cref="Minutes"/>.</summary>
    public const int TextMaxLength = 4000;

    /// <summary>Unique id (UUID version 7).</summary>
    public Guid Id { get; init; } = Guid.CreateVersion7();

    /// <inheritdoc />
    public Guid TenantId { get; set; }

    /// <summary>The meeting.</summary>
    public Guid MeetingId { get; init; }

    /// <summary>The change.</summary>
    public Guid ChangeId { get; init; }

    /// <summary>Order on the agenda.</summary>
    public int Position { get; set; }

    /// <summary>The chair's decision; none until decided.</summary>
    public CabDecision? Decision { get; set; }

    /// <summary>The decision's comment (the reason of a rejection).</summary>
    public string? Comment { get; set; }

    /// <summary>The minutes about this change.</summary>
    public string? Minutes { get; set; }

    /// <summary>Who decided.</summary>
    public Guid? DecidedById { get; set; }

    /// <summary>When it was decided.</summary>
    public DateTimeOffset? DecidedAt { get; set; }

    /// <summary>When it was put on the agenda.</summary>
    public DateTimeOffset AddedAt { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>A member's vote on an agenda item (advice to the chair, decision 8).</summary>
public enum CabVoteChoice
{
    /// <summary>Approve.</summary>
    Approve,

    /// <summary>Reject.</summary>
    Reject,

    /// <summary>Abstain.</summary>
    Abstain,
}

/// <summary>A member's vote on a change of the agenda, with a comment; one per member, changed until the chair decides.</summary>
public sealed class CabVote : ITenantOwned, IAuditedEntity
{
    /// <summary>Maximum length of <see cref="Comment"/>.</summary>
    public const int CommentMaxLength = 2000;

    /// <inheritdoc />
    public Guid TenantId { get; set; }

    /// <summary>The agenda item.</summary>
    public Guid ItemId { get; init; }

    /// <summary>The member.</summary>
    public Guid UserId { get; init; }

    /// <summary>Approve, reject or abstain.</summary>
    public CabVoteChoice Choice { get; set; }

    /// <summary>Why.</summary>
    public string? Comment { get; set; }

    /// <summary>When the vote was last given.</summary>
    public DateTimeOffset VotedAt { get; set; } = DateTimeOffset.UtcNow;
}
