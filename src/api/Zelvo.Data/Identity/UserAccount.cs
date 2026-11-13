using Zelvo.Data.Auditing;
using Zelvo.Data.Localization;
using Zelvo.Data.Tenancy;

namespace Zelvo.Data.Identity;

/// <summary>
/// A person who signs in to a tenant. Keycloak (the tenant's realm) checks who they are; this
/// record holds what Zelvo needs: their roles and status. Created on first sign-in.
/// </summary>
public sealed class UserAccount : ITenantOwned, IAuditedEntity
{
    /// <summary>Maximum length of <see cref="Subject"/>.</summary>
    public const int SubjectMaxLength = 255;

    /// <summary>Maximum length of <see cref="Email"/>.</summary>
    public const int EmailMaxLength = 254;

    /// <summary>Maximum length of <see cref="DisplayName"/>.</summary>
    public const int DisplayNameMaxLength = 200;

    /// <summary>Unique id (UUID version 7).</summary>
    public Guid Id { get; init; } = Guid.CreateVersion7();

    /// <inheritdoc />
    public Guid TenantId { get; set; }

    /// <summary>The user's id in the tenant's Keycloak realm (the token's <c>sub</c>).</summary>
    public required string Subject { get; init; }

    /// <summary>Email address, kept in step with Keycloak at sign-in.</summary>
    [SensitiveData]
    public string? Email { get; set; }

    /// <summary>Name shown to other users.</summary>
    public string? DisplayName { get; set; }

    /// <summary>Whether the user may use Zelvo.</summary>
    public UserStatus Status { get; set; } = UserStatus.Active;

    /// <summary>When the record was created (first sign-in).</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>When the record was last changed.</summary>
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>When the user last used Zelvo (updated at most once an hour).</summary>
    public DateTimeOffset? LastSeenAt { get; set; }

    /// <summary>Personal display language (<c>en</c> or <c>ar</c>); <see langword="null"/> uses the tenant default (LANG-2).</summary>
    public string? PreferredLanguage { get; set; }

    /// <summary>Personal calendar; <see langword="null"/> uses the tenant default (LANG-8).</summary>
    public CalendarSystem? Calendar { get; set; }

    /// <summary>Personal digits; <see langword="null"/> uses the tenant default (LANG-8).</summary>
    public DigitSystem? Digits { get; set; }

    /// <summary>Roles of the user.</summary>
    public List<UserRole> Roles { get; init; } = [];

    /// <summary>Optimistic concurrency token (PostgreSQL <c>xmin</c>).</summary>
    public uint Version { get; init; }
}

/// <summary>Whether a user may use Zelvo.</summary>
public enum UserStatus
{
    /// <summary>May sign in and use Zelvo.</summary>
    Active,

    /// <summary>Refused by the API even with a valid sign-in.</summary>
    Disabled,
}

/// <summary>A role given to a user.</summary>
public sealed class UserRole : ITenantOwned, IAuditedEntity
{
    /// <inheritdoc />
    public Guid TenantId { get; set; }

    /// <summary>The user.</summary>
    public Guid UserId { get; init; }

    /// <summary>The role.</summary>
    public Guid RoleId { get; init; }

    /// <summary>The role (navigation).</summary>
    public Role? Role { get; init; }
}
