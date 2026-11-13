using Zelvo.Data.Auditing;
using Zelvo.Data.Tenancy;

namespace Zelvo.Data.Identity;

/// <summary>
/// Sign-in settings of a tenant. Zelvo is the source of truth; the API copies them into the
/// tenant's Keycloak realm when they change.
/// </summary>
public sealed class IdentitySettings : ITenantOwned, IAuditedEntity
{
    /// <summary>Most allowed email domains.</summary>
    public const int MaxAllowedDomains = 50;

    /// <inheritdoc />
    public Guid TenantId { get; set; }

    /// <summary>Who may sign in with a one-time code sent by email.</summary>
    public EmailCodeSignIn EmailCodeSignIn { get; set; } = EmailCodeSignIn.KnownUsers;

    /// <summary>With <see cref="EmailCodeSignIn.AllowedDomains"/>: domains whose people get a requester account.</summary>
    public List<string> AllowedDomains { get; set; } = [];

    /// <summary>When the settings were last changed.</summary>
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Optimistic concurrency token (PostgreSQL <c>xmin</c>).</summary>
    public uint Version { get; init; }
}

/// <summary>Who may sign in with an emailed one-time code (product owner decision 2026-11-13).</summary>
public enum EmailCodeSignIn
{
    /// <summary>Only people who already have an account (default).</summary>
    KnownUsers,

    /// <summary>Also anyone with a listed email domain; they get a requester account automatically.</summary>
    AllowedDomains,

    /// <summary>Anyone with an email address (public portals).</summary>
    Anyone,
}
