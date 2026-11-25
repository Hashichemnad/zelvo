using Zelvo.Data.Auditing;
using Zelvo.Data.Tenancy;

namespace Zelvo.Data.Voice;

/// <summary>
/// Customer voice details of a complaint, inquiry or suggestion ticket (module 3, task C6; GOV-3).
/// The ticket core (numbers, states, routing, SLA clocks, the conversation) is shared; this row
/// adds what each record type needs: a priority for SLA targets, a complaint's severity, root
/// cause and the customer's confirmation, an inquiry's conversion and a suggestion's review.
/// </summary>
public sealed class CustomerVoiceRecord : ITenantOwned, IAuditedEntity
{
    /// <summary>Maximum length of <see cref="RootCauseKey"/>.</summary>
    public const int KeyMaxLength = 50;

    /// <summary>Maximum length of <see cref="ConfirmationComment"/> and <see cref="ReviewFeedback"/>.</summary>
    public const int TextMaxLength = 4000;

    /// <summary>The ticket (same id).</summary>
    public Guid TicketId { get; init; }

    /// <inheritdoc />
    public Guid TenantId { get; set; }

    /// <summary>Priority 1 (highest) to 5 for SLA targets: a complaint's severity's, else the record type's.</summary>
    public int Priority { get; set; }

    /// <summary>Complaint severity, 1 (highest) to 3.</summary>
    public int? Severity { get; set; }

    /// <summary>Complaint root-cause category key (Settings → Customer voice); needed to resolve a complaint.</summary>
    public string? RootCauseKey { get; set; }

    /// <summary>Whether the customer confirmed a resolved complaint.</summary>
    public ComplaintConfirmation? Confirmation { get; set; }

    /// <summary>When the customer confirmed or rejected, or the complaint closed without an answer.</summary>
    public DateTimeOffset? ConfirmationAt { get; set; }

    /// <summary>Why the customer rejected the resolution.</summary>
    public string? ConfirmationComment { get; set; }

    /// <summary>A suggestion's review state.</summary>
    public SuggestionReview? Review { get; set; }

    /// <summary>Feedback to the submitter on the latest review.</summary>
    public string? ReviewFeedback { get; set; }

    /// <summary>The complaint or service request an inquiry was converted to.</summary>
    public Guid? ConvertedToId { get; set; }

    /// <summary>The catalog service it is about, for reporting per service (GOV-3).</summary>
    public Guid? ServiceId { get; set; }

    /// <summary>Sent by a visitor without signing in; the requester is the tenant's anonymous account.</summary>
    public bool Anonymous { get; init; }

    /// <summary>SHA-256 (hex) of the tracking code an anonymous submitter follows it with; the code itself is never stored.</summary>
    public string? TrackingCodeHash { get; init; }
}

/// <summary>The customer's answer to a resolved complaint (module 3: confirmation before closure).</summary>
public enum ComplaintConfirmation
{
    /// <summary>Resolved; waiting for the customer.</summary>
    Pending,

    /// <summary>The customer accepted the resolution; the complaint closed.</summary>
    Confirmed,

    /// <summary>The customer rejected it; the complaint was reopened.</summary>
    Rejected,

    /// <summary>
    /// The customer did not answer and the complaint closed after the auto-close period (product owner
    /// decision 2026-11-24).
    /// </summary>
    NotConfirmed,
}

/// <summary>Review states of a suggestion (module 3).</summary>
public enum SuggestionReview
{
    /// <summary>Received, being looked at.</summary>
    UnderReview,

    /// <summary>Accepted; to be implemented.</summary>
    Accepted,

    /// <summary>Implemented (resolves the suggestion).</summary>
    Implemented,

    /// <summary>Declined (resolves the suggestion).</summary>
    Declined,
}

/// <summary>
/// One answer to the happiness meter (GOV-2, task C6): on a ticket once it is resolved (once per
/// ticket) or a general rating in the portal. The score is 1 (worst) to <see cref="Points"/>.
/// </summary>
public sealed class HappinessRating : ITenantOwned, IAuditedEntity
{
    /// <summary>Maximum length of <see cref="Comment"/>.</summary>
    public const int CommentMaxLength = 2000;

    /// <summary>Unique id (UUID version 7).</summary>
    public Guid Id { get; init; } = Guid.CreateVersion7();

    /// <inheritdoc />
    public Guid TenantId { get; set; }

    /// <summary>The ticket rated; null for a portal rating.</summary>
    public Guid? TicketId { get; init; }

    /// <summary>The ticket's record type, kept for reporting.</summary>
    public Tickets.TicketType? RecordType { get; init; }

    /// <summary>The ticket's channel, kept for reporting (GOV-3: per channel).</summary>
    public Tickets.TicketChannel? Channel { get; init; }

    /// <summary>The catalog service, when the ticket is about one.</summary>
    public Guid? ServiceId { get; init; }

    /// <summary>Who rated; null for visitors and anonymous submitters.</summary>
    public Guid? UserId { get; init; }

    /// <summary>SHA-256 (hex) of a visitor's device id, for one portal rating per device and day.</summary>
    public string? DeviceHash { get; init; }

    /// <summary>1 (worst) to <see cref="Points"/>.</summary>
    public int Score { get; init; }

    /// <summary>Points of the scale when rated (3 or 5).</summary>
    public int Points { get; init; }

    /// <summary>What the person added.</summary>
    public string? Comment { get; init; }

    /// <summary>When.</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}
