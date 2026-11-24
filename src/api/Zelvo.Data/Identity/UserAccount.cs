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

    /// <summary>Length of <see cref="EmiratesId"/>: 15 digits.</summary>
    public const int EmiratesIdLength = 15;

    /// <summary>Maximum length of <see cref="EmployeeId"/>.</summary>
    public const int EmployeeIdMaxLength = 50;

    /// <summary>Maximum length of <see cref="Phone"/>: <c>+</c> and up to 15 digits (E.164).</summary>
    public const int PhoneMaxLength = 16;

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

    /// <summary>
    /// Emirates ID number (15 digits starting with 784), recorded by an administrator so that a
    /// verified UAE PASS sign-in finds this account (GOV-1; how staff accounts are matched).
    /// Unique within the tenant. Copied to the user in Keycloak.
    /// </summary>
    [SensitiveData]
    public string? EmiratesId { get; set; }

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

    /// <summary>
    /// Round-robin may give this person tickets (task B2). Staff switch it off when they are away;
    /// administrators and the leads of their groups can change it too.
    /// </summary>
    public bool AvailableForAssignment { get; set; } = true;

    /// <summary>Languages the person handles tickets in (<c>en</c>, <c>ar</c>); used by round-robin rules with a language.</summary>
    public List<string> Languages { get; set; } = [];

    /// <summary>Skills of the user (task B2).</summary>
    public List<Directory.UserSkill> Skills { get; init; } = [];

    /// <summary>The person's department (task C1; filled into forms from the profile).</summary>
    public Guid? DepartmentId { get; set; }

    /// <summary>The person's manager, another user of the tenant (task C1).</summary>
    public Guid? ManagerId { get; set; }

    /// <summary>Where the person works, a node of the location tree (task C1).</summary>
    public Guid? LocationId { get; set; }

    /// <summary>The organisation's employee number (task C1).</summary>
    public string? EmployeeId { get; set; }

    /// <summary>Phone number in international form, for example <c>+971501234567</c> (task C1). Personal data.</summary>
    [SensitiveData]
    public string? Phone { get; set; }

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
