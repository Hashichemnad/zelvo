namespace Zelvo.Data.Auditing;

/// <summary>
/// One entry of a tenant's tamper-evident audit log. Rows are append-only: the database refuses
/// updates, deletes and truncation. A database trigger numbers each tenant's entries in sequence
/// and stores the SHA-256 hash of the entry together with the previous entry's hash, so a changed,
/// removed or reordered entry breaks the chain (see <c>zelvo_verify_audit_chain</c>).
/// </summary>
public sealed class AuditEvent
{
    /// <summary>Maximum length of short text columns.</summary>
    public const int TextMaxLength = 200;

    /// <summary>Database id.</summary>
    public long Id { get; init; }

    /// <summary>Tenant the entry belongs to.</summary>
    public Guid TenantId { get; init; }

    /// <summary>Position in the tenant's chain, starting at 1. Set by the database.</summary>
    public long Sequence { get; init; }

    /// <summary>When the entry was written. Set by the database; clients cannot backdate it.</summary>
    public DateTimeOffset OccurredAt { get; init; }

    /// <summary>Kind of event.</summary>
    public required AuditCategory Category { get; init; }

    /// <summary>Stable, dotted event name, for example <c>tenant.updated</c>.</summary>
    public required string Action { get; init; }

    /// <summary>Who or what acted.</summary>
    public required AuditActorType ActorType { get; init; }

    /// <summary>Actor id (user id, API client id), when known.</summary>
    public string? ActorId { get; init; }

    /// <summary>Actor display name at the time of the event, when known.</summary>
    public string? ActorName { get; init; }

    /// <summary>Type of the record acted on, for example <c>tenant</c>.</summary>
    public string? EntityType { get; init; }

    /// <summary>Id of the record acted on.</summary>
    public string? EntityId { get; init; }

    /// <summary>
    /// Field changes as JSON: <c>{"field": {"old": …, "new": …}}</c>. Fields marked
    /// <see cref="SensitiveDataAttribute"/> are recorded as changed with their values masked.
    /// </summary>
    public string? Changes { get; init; }

    /// <summary>Request context as JSON: correlation id, client address, user agent, details.</summary>
    public string? Metadata { get; init; }

    /// <summary>Hash of the previous entry in the tenant's chain; <see langword="null"/> for the first.</summary>
    public byte[]? PreviousHash { get; init; }

    /// <summary>SHA-256 hash of this entry, including <see cref="PreviousHash"/>. Set by the database.</summary>
    public byte[]? Hash { get; init; }
}

/// <summary>Kinds of audited events (non-functional requirement "Audit").</summary>
public enum AuditCategory
{
    /// <summary>A record was created.</summary>
    Create,

    /// <summary>A record was changed.</summary>
    Change,

    /// <summary>A record was deleted.</summary>
    Delete,

    /// <summary>An administrative action, for example provisioning or verifying the audit log.</summary>
    Admin,

    /// <summary>Sensitive data was viewed.</summary>
    View,

    /// <summary>A security event, for example a refused access.</summary>
    Security,
}

/// <summary>Kinds of actors.</summary>
public enum AuditActorType
{
    /// <summary>The platform itself, for example a migration or a scheduled job.</summary>
    System,

    /// <summary>A signed-in person.</summary>
    User,

    /// <summary>A visitor who is not signed in, for example on a public form.</summary>
    Anonymous,

    /// <summary>An integration using an API key or OAuth client.</summary>
    ApiClient,

    /// <summary>An AI agent acting on someone's behalf (module 9).</summary>
    AiAgent,
}
