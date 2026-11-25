using Zelvo.Data.Auditing;
using Zelvo.Data.Tenancy;

namespace Zelvo.Data.Ai;

/// <summary>What an AI job does with a ticket (task E5).</summary>
public enum AiJobKind
{
    /// <summary>Suggest category, impact, urgency and group for a new incident.</summary>
    Triage,

    /// <summary>Read the requester's latest message for sentiment.</summary>
    Sentiment,

    /// <summary>Look for open incidents about the same problem.</summary>
    Duplicates,

    /// <summary>Transcribe a voice note (task E3).</summary>
    Transcription,
}

/// <summary>Where an AI job is.</summary>
public enum AiJobStatus
{
    /// <summary>Waiting for the worker.</summary>
    Pending,

    /// <summary>Done (also when the AI was off or the ticket no longer needed it).</summary>
    Done,

    /// <summary>Given up after its attempts.</summary>
    Failed,
}

/// <summary>
/// Background AI work for a ticket (task E5), queued in the same transaction as the change that
/// causes it (a new incident, a requester's message) and run by the worker, so saving a ticket
/// never waits for an AI provider. Not audited: the AI call and what it changes are.
/// </summary>
public sealed class AiJob : ITenantOwned
{
    /// <summary>Maximum length of <see cref="LastError"/>.</summary>
    public const int ErrorMaxLength = 500;

    /// <summary>Sequence id.</summary>
    public long Id { get; init; }

    /// <inheritdoc />
    public Guid TenantId { get; set; }

    /// <summary>What to do.</summary>
    public AiJobKind Kind { get; init; }

    /// <summary>The ticket.</summary>
    public Guid TicketId { get; init; }

    /// <summary>The requester's message that caused it (sentiment), if any.</summary>
    public Guid? CommentId { get; init; }

    /// <summary>Where it is.</summary>
    public AiJobStatus Status { get; set; } = AiJobStatus.Pending;

    /// <summary>Attempts made.</summary>
    public int Attempts { get; set; }

    /// <summary>When it may run (again).</summary>
    public DateTimeOffset NextAttemptAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>A worker holds it until then.</summary>
    public DateTimeOffset? LeaseUntil { get; set; }

    /// <summary>Why the last attempt failed.</summary>
    public string? LastError { get; set; }

    /// <summary>When it was queued.</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>When it finished.</summary>
    public DateTimeOffset? FinishedAt { get; set; }
}

/// <summary>What happened to a triage suggestion.</summary>
public enum TriageStatus
{
    /// <summary>Waiting for an agent.</summary>
    Pending,

    /// <summary>An agent took it as it was.</summary>
    Accepted,

    /// <summary>An agent took it with changes; the changes teach later suggestions.</summary>
    Corrected,

    /// <summary>An agent turned it down.</summary>
    Dismissed,

    /// <summary>Applied by the administrator's rule "apply triage automatically" (product owner decision 2026-11-24).</summary>
    AutoApplied,

    /// <summary>Someone set the ticket by hand before deciding, or it was closed.</summary>
    Outdated,
}

/// <summary>
/// The AI's suggestion for a new incident (module 9: auto-categorise, prioritise and route): a
/// category, impact and urgency (so a priority from the tenant's matrix) and a group. Agents
/// accept or correct it; what they finally chose is kept and given to later suggestions as
/// examples, so corrections improve accuracy. Audited.
/// </summary>
public sealed class AiTriageSuggestion : ITenantOwned, IAuditedEntity
{
    /// <summary>Maximum length of <see cref="Reason"/>.</summary>
    public const int ReasonMaxLength = 500;

    /// <summary>Unique id (UUID version 7).</summary>
    public Guid Id { get; init; } = Guid.CreateVersion7();

    /// <inheritdoc />
    public Guid TenantId { get; set; }

    /// <summary>The incident.</summary>
    public Guid TicketId { get; init; }

    /// <summary>Suggested category.</summary>
    public Guid? CategoryId { get; init; }

    /// <summary>Suggested impact (1 highest).</summary>
    public int? Impact { get; init; }

    /// <summary>Suggested urgency (1 highest).</summary>
    public int? Urgency { get; init; }

    /// <summary>Suggested group.</summary>
    public Guid? GroupId { get; init; }

    /// <summary>How sure the AI said it was, 0–100.</summary>
    public int Confidence { get; init; }

    /// <summary>The AI's short explanation, in English.</summary>
    public string? Reason { get; init; }

    /// <summary>What happened to it.</summary>
    public TriageStatus Status { get; set; } = TriageStatus.Pending;

    /// <summary>The AI call that made it.</summary>
    public long? AiCallId { get; init; }

    /// <summary>When it was made.</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Who decided (empty for the automatic rule).</summary>
    public Guid? DecidedById { get; set; }

    /// <summary>When it was decided.</summary>
    public DateTimeOffset? DecidedAt { get; set; }

    /// <summary>The category the ticket finally got.</summary>
    public Guid? FinalCategoryId { get; set; }

    /// <summary>The impact the ticket finally got.</summary>
    public int? FinalImpact { get; set; }

    /// <summary>The urgency the ticket finally got.</summary>
    public int? FinalUrgency { get; set; }

    /// <summary>The group the ticket finally got.</summary>
    public Guid? FinalGroupId { get; set; }

    /// <summary>Optimistic concurrency token (PostgreSQL <c>xmin</c>).</summary>
    public uint Version { get; init; }
}

/// <summary>How the requester sounds in their latest message.</summary>
public enum AiSentiment
{
    /// <summary>Pleased or thankful.</summary>
    Positive,

    /// <summary>Matter-of-fact.</summary>
    Neutral,

    /// <summary>Unhappy.</summary>
    Negative,

    /// <summary>Frustrated or angry: the group's team leads are alerted (product owner decision 2026-11-24).</summary>
    Frustrated,
}

/// <summary>
/// What the AI knows about one ticket (task E5): the requester's sentiment and a cached thread
/// summary. One row per ticket. Audited (the summary by length only).
/// </summary>
public sealed class AiTicketInsight : ITenantOwned, IAuditedEntity
{
    /// <summary>Maximum length of <see cref="SentimentReason"/>.</summary>
    public const int ReasonMaxLength = 300;

    /// <summary>Maximum length of <see cref="Summary"/>.</summary>
    public const int SummaryMaxLength = 4000;

    /// <summary>Maximum length of <see cref="SummaryBasis"/>.</summary>
    public const int BasisMaxLength = 100;

    /// <summary>The ticket.</summary>
    public Guid TicketId { get; init; }

    /// <inheritdoc />
    public Guid TenantId { get; set; }

    /// <summary>Sentiment of the requester's latest message read.</summary>
    public AiSentiment? Sentiment { get; set; }

    /// <summary>Short explanation, in English.</summary>
    public string? SentimentReason { get; set; }

    /// <summary>When sentiment was last read.</summary>
    public DateTimeOffset? SentimentAt { get; set; }

    /// <summary>The message read (empty for the ticket's description).</summary>
    public Guid? SentimentCommentId { get; set; }

    /// <summary>Since when the requester sounds frustrated; cleared when a later message does not.</summary>
    public DateTimeOffset? FrustratedSince { get; set; }

    /// <summary>The thread summary, in <see cref="SummaryLanguage"/>.</summary>
    [AuditLengthOnly]
    public string? Summary { get; set; }

    /// <summary>Language of the summary (<c>en</c> or <c>ar</c>).</summary>
    public string? SummaryLanguage { get; set; }

    /// <summary>What the summary was made from (description, comments); a new comment makes it stale.</summary>
    public string? SummaryBasis { get; set; }

    /// <summary>When the summary was made.</summary>
    public DateTimeOffset? SummaryAt { get; set; }

    /// <summary>Optimistic concurrency token (PostgreSQL <c>xmin</c>).</summary>
    public uint Version { get; init; }
}

/// <summary>What happened to a possible duplicate.</summary>
public enum DuplicateStatus
{
    /// <summary>Waiting for an agent.</summary>
    Pending,

    /// <summary>The agent made the new incident a child of the other one.</summary>
    Linked,

    /// <summary>The agent said it is not a duplicate.</summary>
    Dismissed,
}

/// <summary>
/// An open incident the AI thinks is about the same problem as a new one (module 9: detect
/// duplicate tickets). Agents link it (the new incident becomes a child, resolved with its parent)
/// or dismiss it. Audited.
/// </summary>
public sealed class AiDuplicateCandidate : ITenantOwned, IAuditedEntity
{
    /// <summary>Maximum length of <see cref="Reason"/>.</summary>
    public const int ReasonMaxLength = 300;

    /// <summary>Unique id (UUID version 7).</summary>
    public Guid Id { get; init; } = Guid.CreateVersion7();

    /// <inheritdoc />
    public Guid TenantId { get; set; }

    /// <summary>The new incident.</summary>
    public Guid TicketId { get; init; }

    /// <summary>The open incident it may duplicate.</summary>
    public Guid CandidateId { get; init; }

    /// <summary>How sure the AI said it was, 0–100.</summary>
    public int Confidence { get; init; }

    /// <summary>The AI's short explanation, in English.</summary>
    public string? Reason { get; init; }

    /// <summary>What happened to it.</summary>
    public DuplicateStatus Status { get; set; } = DuplicateStatus.Pending;

    /// <summary>When it was found.</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Who decided.</summary>
    public Guid? DecidedById { get; set; }

    /// <summary>When it was decided.</summary>
    public DateTimeOffset? DecidedAt { get; set; }

    /// <summary>Optimistic concurrency token (PostgreSQL <c>xmin</c>).</summary>
    public uint Version { get; init; }
}

/// <summary>What a translation is of.</summary>
public enum TranslationSource
{
    /// <summary>A ticket's title and description.</summary>
    Ticket,

    /// <summary>A reply or note on a ticket.</summary>
    Comment,

    /// <summary>A live chat message.</summary>
    ChatMessage,
}

/// <summary>
/// A cached one-click translation (LANG-7), so the same message is translated once per language.
/// Kept while its text is unchanged (<see cref="SourceHash"/>). Not audited; the AI call is.
/// </summary>
public sealed class AiTranslation : ITenantOwned
{
    /// <summary>Maximum length of <see cref="Text"/>.</summary>
    public const int TextMaxLength = 24_000;

    /// <inheritdoc />
    public Guid TenantId { get; set; }

    /// <summary>What it is of.</summary>
    public TranslationSource Source { get; init; }

    /// <summary>The ticket, comment or chat message.</summary>
    public Guid SourceId { get; init; }

    /// <summary>Language translated into (<c>en</c> or <c>ar</c>).</summary>
    public required string Language { get; init; }

    /// <summary>SHA-256 of the text that was translated.</summary>
    public required string SourceHash { get; set; }

    /// <summary>The translation.</summary>
    public required string Text { get; set; }

    /// <summary>When it was made.</summary>
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
