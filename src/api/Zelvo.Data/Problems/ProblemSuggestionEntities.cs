using Zelvo.Data.Auditing;
using Zelvo.Data.Tenancy;

namespace Zelvo.Data.Problems;

/// <summary>Why Zelvo suggests a problem (module 16, task H2).</summary>
public enum ProblemSuggestionKind
{
    /// <summary>A group of similar incidents, shortlisted by their words and judged through the AI gateway.</summary>
    SimilarIncidents,

    /// <summary>A category, configuration item or business service whose incident volume rose well above its usual level.</summary>
    Trend,
}

/// <summary>Where a suggestion stands.</summary>
public enum ProblemSuggestionStatus
{
    /// <summary>Waiting in the problem managers' queue.</summary>
    Open,

    /// <summary>A problem manager raised the problem (<see cref="ProblemSuggestion.ProblemId"/>).</summary>
    Accepted,

    /// <summary>A problem manager dismissed it with a reason; the same group is not suggested again.</summary>
    Dismissed,

    /// <summary>The AI did not find a shared cause; never shown, kept so the same group is not judged again.</summary>
    NotConfirmed,
}

/// <summary>What a trend counts incidents of.</summary>
public enum ProblemTrendDimension
{
    /// <summary>Incidents of one category.</summary>
    Category,

    /// <summary>Incidents linked to one configuration item (not a business service).</summary>
    ConfigurationItem,

    /// <summary>Incidents linked to a business service, or to an item whose fault reaches it.</summary>
    Service,
}

/// <summary>
/// A suggested problem (module 16, task H2): offered to problem managers, never a problem made by
/// itself. Accepting raises the problem with the incidents linked; dismissing keeps the reason, so
/// the same group of incidents or the same trend is not suggested again.
/// </summary>
public sealed class ProblemSuggestion : ITenantOwned, IAuditedEntity
{
    /// <summary>Maximum length of <see cref="Title"/>.</summary>
    public const int TitleMaxLength = 300;

    /// <summary>Maximum length of <see cref="Reason"/> and <see cref="DismissReason"/>.</summary>
    public const int ReasonMaxLength = 1000;

    /// <summary>Most incidents kept with one suggestion (and linked when it is accepted).</summary>
    public const int MaxIncidents = 100;

    /// <summary>Unique id (UUID version 7).</summary>
    public Guid Id { get; init; } = Guid.CreateVersion7();

    /// <inheritdoc />
    public Guid TenantId { get; set; }

    /// <summary>Why it was suggested.</summary>
    public ProblemSuggestionKind Kind { get; init; }

    /// <summary>Where it stands.</summary>
    public ProblemSuggestionStatus Status { get; set; } = ProblemSuggestionStatus.Open;

    /// <summary>A short title for the problem (similar incidents: from the AI, in the incidents' language). Trends have none: screens name the subject.</summary>
    public string? Title { get; set; }

    /// <summary>Why the incidents belong together (from the AI, masked), or null.</summary>
    public string? Reason { get; set; }

    /// <summary>How sure the AI is, 0–100 (similar incidents only).</summary>
    public int? Confidence { get; set; }

    /// <summary>The language of <see cref="Title"/> (<c>en</c> or <c>ar</c>).</summary>
    public string? Language { get; set; }

    /// <summary>What a trend counts (trends only).</summary>
    public ProblemTrendDimension? Dimension { get; init; }

    /// <summary>The category the incidents share, or the trend's category.</summary>
    public Guid? CategoryId { get; set; }

    /// <summary>The trend's configuration item or business service.</summary>
    public Guid? ConfigurationItemId { get; init; }

    /// <summary>Start of the period the incidents were raised in.</summary>
    public DateTimeOffset WindowStart { get; set; }

    /// <summary>End of that period.</summary>
    public DateTimeOffset WindowEnd { get; set; }

    /// <summary>How many incidents (the trend's count in the period; may be more than are kept).</summary>
    public int IncidentCount { get; set; }

    /// <summary>A trend's usual level: the average per period of the periods before.</summary>
    public double? Baseline { get; set; }

    /// <summary>The AI call that judged the group, in the AI log.</summary>
    public long? AiCallId { get; set; }

    /// <summary>The problem raised when it was accepted.</summary>
    public Guid? ProblemId { get; set; }

    /// <summary>Who accepted or dismissed it.</summary>
    public Guid? DecidedById { get; set; }

    /// <summary>When it was accepted or dismissed.</summary>
    public DateTimeOffset? DecidedAt { get; set; }

    /// <summary>Why it was dismissed.</summary>
    public string? DismissReason { get; set; }

    /// <summary>When it was suggested.</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>When it last changed (new incidents joined, or decided).</summary>
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Optimistic concurrency token (PostgreSQL <c>xmin</c>): one decision only.</summary>
    public uint Version { get; init; }
}

/// <summary>An incident named by a suggestion (task H2).</summary>
public sealed class ProblemSuggestionIncident : ITenantOwned
{
    /// <inheritdoc />
    public Guid TenantId { get; set; }

    /// <summary>The suggestion.</summary>
    public Guid SuggestionId { get; init; }

    /// <summary>The incident's ticket.</summary>
    public Guid IncidentId { get; init; }
}
