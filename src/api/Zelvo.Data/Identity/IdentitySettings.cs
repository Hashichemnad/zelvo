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

    /// <summary>
    /// Task J3: passkeys (WebAuthn) for signing in, through Keycloak's own WebAuthn support;
    /// a passkey the device verified the person with counts as staff's second factor (decision 15).
    /// </summary>
    public PasskeySignIn Passkeys { get; set; } = PasskeySignIn.Allowed;

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

/// <summary>Passkeys for signing in (module 19, task J3; product owner decision 15 of 2026-11-26).</summary>
public enum PasskeySignIn
{
    /// <summary>Not offered: nobody adds a passkey or signs in with one; staff use an authenticator app.</summary>
    Off,

    /// <summary>
    /// Offered (default): anyone may add passkeys and sign in with one. A passkey sign-in counts as
    /// staff's second factor (the device verified the person); otherwise staff use an authenticator app.
    /// </summary>
    Allowed,

    /// <summary>
    /// Offered, and staff's second factor must be a passkey: staff who sign in with a password or an
    /// email code confirm with a passkey (and add one if they have none); authenticator apps no
    /// longer count. Requesters may still use passkeys or not.
    /// </summary>
    RequiredForStaff,
}
