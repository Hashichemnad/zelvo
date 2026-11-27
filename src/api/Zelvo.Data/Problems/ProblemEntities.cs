using Zelvo.Data.Auditing;
using Zelvo.Data.Localization;
using Zelvo.Data.Tenancy;

namespace Zelvo.Data.Problems;

/// <summary>
/// Where an open problem is in its investigation (module 16, task H1). The ticket core's state
/// decides New (nobody started), Resolved and Closed; while the problem is being worked on, its
/// phase names the step: Assessment, Root-cause analysis or Known error.
/// </summary>
public enum ProblemPhase
{
    /// <summary>Understanding the problem: scope, impact, the incidents it covers.</summary>
    Assessment,

    /// <summary>Looking for the root cause (5 Whys, fishbone, the timeline of linked incidents).</summary>
    RootCauseAnalysis,

    /// <summary>The cause is known and a workaround recorded; the permanent fix is still to come.</summary>
    KnownError,
}

/// <summary>Whether a known error's workaround is offered to agents and, through its article, in the portal.</summary>
public enum KnownErrorStatus
{
    /// <summary>Offered on matching incidents.</summary>
    Active,

    /// <summary>No longer offered (the problem was fixed or the workaround stopped working).</summary>
    Retired,
}

/// <summary>
/// Problem details of a ticket of record type <c>Problem</c> (task H1): its phase, priority for SLA
/// targets and the root-cause tools stored as structured data (5 Whys and fishbone, JSON).
/// </summary>
public sealed class ProblemRecord : ITenantOwned, IAuditedEntity
{
    /// <summary>Maximum length of <see cref="RootCause"/>.</summary>
    public const int RootCauseMaxLength = 4000;

    /// <summary>The ticket.</summary>
    public Guid TicketId { get; init; }

    /// <inheritdoc />
    public Guid TenantId { get; set; }

    /// <summary>The investigation step while the problem is open.</summary>
    public ProblemPhase Phase { get; set; } = ProblemPhase.Assessment;

    /// <summary>Priority for SLA targets, 1 (highest) to 5.</summary>
    public int Priority { get; set; } = 3;

    /// <summary>The root cause as found, in the language it was written.</summary>
    public string? RootCause { get; set; }

    /// <summary>The 5 Whys (JSON object: the problem statement and up to seven why / because steps).</summary>
    public string FiveWhys { get; set; } = "{}";

    /// <summary>The fishbone (JSON object: the effect and causes per category of the template).</summary>
    public string Fishbone { get; set; } = "{}";
}

/// <summary>
/// A known error (task H1): a problem whose cause is known, with a workaround in English and Arabic
/// that agents see on matching incidents (same category, configuration item or linked problem) and
/// can paste into a reply. Published to the portal as an external knowledge article through the
/// knowledge base's four-eyes review (product owner decision 9 of 2026-11-26).
/// </summary>
public sealed class KnownError : ITenantOwned, IAuditedEntity
{
    /// <summary>Maximum length of <see cref="Number"/>.</summary>
    public const int NumberMaxLength = 20;

    /// <summary>Maximum length of each language of <see cref="Title"/>.</summary>
    public const int TitleMaxLength = 300;

    /// <summary>Maximum length of each language of <see cref="Symptoms"/> and <see cref="Workaround"/>.</summary>
    public const int TextMaxLength = 10_000;

    /// <summary>Unique id (UUID version 7).</summary>
    public Guid Id { get; init; } = Guid.CreateVersion7();

    /// <inheritdoc />
    public Guid TenantId { get; set; }

    /// <summary>Number shown to people, for example <c>KE-000123</c>; never reused.</summary>
    public required string Number { get; init; }

    /// <summary>Sequence behind <see cref="Number"/>, per tenant.</summary>
    public long Sequence { get; init; }

    /// <summary>The problem it belongs to (one known error per problem).</summary>
    public Guid ProblemId { get; init; }

    /// <summary>Title in English and Arabic (at least one).</summary>
    public required LocalizedText Title { get; set; }

    /// <summary>How the error shows itself, so agents recognise it.</summary>
    public LocalizedText Symptoms { get; set; } = new(null, null);

    /// <summary>What to do until the problem is fixed (at least one language).</summary>
    public required LocalizedText Workaround { get; set; }

    /// <summary>Offered on incidents or not.</summary>
    public KnownErrorStatus Status { get; set; } = KnownErrorStatus.Active;

    /// <summary>The knowledge article that publishes it in the portal, once made.</summary>
    public Guid? ArticleId { get; set; }

    /// <summary>Who wrote it.</summary>
    public Guid? CreatedById { get; init; }

    /// <summary>When it was recorded.</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>When it last changed.</summary>
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Optimistic concurrency token (PostgreSQL <c>xmin</c>).</summary>
    public uint Version { get; init; }
}

/// <summary>Last known error number given, per tenant.</summary>
public sealed class KnownErrorNumberSequence : ITenantOwned
{
    /// <inheritdoc />
    public Guid TenantId { get; set; }

    /// <summary>Last number given.</summary>
    public long LastValue { get; set; }
}

/// <summary>An incident linked to a problem (task H1). An incident can belong to several problems.</summary>
public sealed class ProblemIncident : ITenantOwned, IAuditedEntity
{
    /// <inheritdoc />
    public Guid TenantId { get; set; }

    /// <summary>The problem's ticket.</summary>
    public Guid ProblemId { get; init; }

    /// <summary>The incident's ticket.</summary>
    public Guid IncidentId { get; init; }

    /// <summary>Who linked it.</summary>
    public Guid? LinkedById { get; init; }

    /// <summary>When it was linked.</summary>
    public DateTimeOffset LinkedAt { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>A knowledge article linked to a problem (task H1).</summary>
public sealed class ProblemArticle : ITenantOwned, IAuditedEntity
{
    /// <inheritdoc />
    public Guid TenantId { get; set; }

    /// <summary>The problem's ticket.</summary>
    public Guid ProblemId { get; init; }

    /// <summary>The article.</summary>
    public Guid ArticleId { get; init; }

    /// <summary>Who linked it.</summary>
    public Guid? LinkedById { get; init; }

    /// <summary>When it was linked.</summary>
    public DateTimeOffset LinkedAt { get; init; } = DateTimeOffset.UtcNow;
}
