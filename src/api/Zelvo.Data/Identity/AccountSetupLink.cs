using Zelvo.Data.Tenancy;

namespace Zelvo.Data.Identity;

/// <summary>
/// A one-time link that lets a person choose their password (task M2): made by <c>migrate</c> for
/// a hardened installation's first tenant administrator, who signs in afterwards and sets up the
/// authenticator app. Only the token's SHA-256 hash is kept; a link works once, until it expires,
/// and a newer link for the same person replaces it.
/// </summary>
public sealed class AccountSetupLink : ITenantOwned, Auditing.IAuditedEntity
{
    /// <summary>How long a link works.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(72);

    /// <summary>Unique id (UUID version 7).</summary>
    public Guid Id { get; init; } = Guid.CreateVersion7();

    /// <inheritdoc />
    public Guid TenantId { get; set; }

    /// <summary>The person the link is for.</summary>
    public Guid UserId { get; init; }

    /// <summary>Lower-case hex SHA-256 of the token; the token itself is shown once and never stored.</summary>
    [Auditing.SensitiveData]
    public required string TokenHash { get; init; }

    /// <summary>When it was made.</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>When it stops working.</summary>
    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>When the person used it; a used link never works again.</summary>
    public DateTimeOffset? UsedAt { get; set; }

    /// <summary>Whether it still works at <paramref name="now"/>.</summary>
    public bool WorksAt(DateTimeOffset now) => UsedAt is null && ExpiresAt > now;
}
