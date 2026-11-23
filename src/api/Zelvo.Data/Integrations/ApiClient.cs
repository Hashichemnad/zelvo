using Zelvo.Data.Auditing;
using Zelvo.Data.Tenancy;

namespace Zelvo.Data.Integrations;

/// <summary>How an integration signs in (module 14).</summary>
public enum ApiClientKind
{
    /// <summary>A Zelvo API key sent in the <c>X-Api-Key</c> header. Only a hash is stored.</summary>
    ApiKey,

    /// <summary>
    /// An OAuth 2.0 client (client credentials grant) in the tenant's Keycloak realm. Keycloak
    /// issues the access tokens; Zelvo keeps the role, scopes and limits.
    /// </summary>
    OAuthClient,
}

/// <summary>Whether an integration may call the API.</summary>
public enum ApiClientStatus
{
    /// <summary>Calls are accepted (until <see cref="ApiClient.ExpiresAt"/>, if set).</summary>
    Active,

    /// <summary>Revoked by an administrator: calls are refused at once. Kept for the audit log.</summary>
    Revoked,
}

/// <summary>
/// An integration that calls the Zelvo API: an API key or an OAuth client. It acts with one role,
/// narrowed by its scopes (permission keys), and has its own rate limit. Every change is audited;
/// every call is logged in the integration log and the audit log.
/// </summary>
public sealed class ApiClient : ITenantOwned, IAuditedEntity
{
    /// <summary>Maximum length of <see cref="Name"/>.</summary>
    public const int NameMaxLength = 100;

    /// <summary>Maximum length of <see cref="Description"/>.</summary>
    public const int DescriptionMaxLength = 500;

    /// <summary>Length of <see cref="KeyPrefix"/>.</summary>
    public const int KeyPrefixLength = 12;

    /// <summary>Maximum length of <see cref="KeycloakClientId"/> and the other Keycloak ids.</summary>
    public const int KeycloakIdMaxLength = 100;

    /// <summary>Lowest per-minute limit an administrator can set.</summary>
    public const int MinRateLimit = 1;

    /// <summary>Highest per-minute limit an administrator can set.</summary>
    public const int MaxRateLimit = 100_000;

    /// <summary>Per-minute limit of a new integration.</summary>
    public const int DefaultRateLimit = 600;

    /// <summary>Unique id (UUID version 7).</summary>
    public Guid Id { get; init; } = Guid.CreateVersion7();

    /// <inheritdoc />
    public Guid TenantId { get; set; }

    /// <summary>API key or OAuth client.</summary>
    public ApiClientKind Kind { get; init; }

    /// <summary>Name shown to administrators, for example "CRM sync".</summary>
    public required string Name { get; set; }

    /// <summary>What the integration is for.</summary>
    public string? Description { get; set; }

    /// <summary>The role the integration acts with.</summary>
    public Guid RoleId { get; set; }

    /// <summary>
    /// Permission keys the integration may use; what applies is the role's permissions that are
    /// also in this list, never more.
    /// </summary>
    public string[] Scopes { get; set; } = [];

    /// <summary>Requests per minute this integration may make (set by the administrator).</summary>
    public int RateLimitPerMinute { get; set; } = DefaultRateLimit;

    /// <summary>Active or revoked.</summary>
    public ApiClientStatus Status { get; set; } = ApiClientStatus.Active;

    /// <summary>After this time calls are refused; <see langword="null"/> for no end date.</summary>
    public DateTimeOffset? ExpiresAt { get; set; }

    /// <summary>API keys: the public part of the key (<c>zvk_&lt;prefix&gt;_…</c>), used to find it.</summary>
    public string? KeyPrefix { get; set; }

    /// <summary>API keys: SHA-256 of the whole key. The key itself is shown once and never stored.</summary>
    [SensitiveData]
    public byte[]? KeyHash { get; set; }

    /// <summary>OAuth clients: the client id in Keycloak (<c>zelvo-int-…</c>).</summary>
    public string? KeycloakClientId { get; set; }

    /// <summary>OAuth clients: Keycloak's internal id of the client.</summary>
    public string? KeycloakInternalId { get; set; }

    /// <summary>OAuth clients: the <c>sub</c> of the client's access tokens (its service account).</summary>
    public string? ServiceAccountSubject { get; set; }

    /// <summary>The Zelvo user who created it.</summary>
    public Guid? CreatedBy { get; init; }

    /// <summary>When it was created.</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>When it was last changed.</summary>
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>When the key or secret was last replaced.</summary>
    public DateTimeOffset? RotatedAt { get; set; }

    /// <summary>When it was revoked.</summary>
    public DateTimeOffset? RevokedAt { get; set; }

    /// <summary>When it last called the API (updated at most every few minutes; not audited).</summary>
    public DateTimeOffset? LastUsedAt { get; set; }

    /// <summary>Optimistic concurrency token (PostgreSQL <c>xmin</c>).</summary>
    public uint Version { get; init; }

    /// <summary>Whether calls are accepted at <paramref name="now"/>.</summary>
    public bool IsUsable(DateTimeOffset now) => Status == ApiClientStatus.Active && (ExpiresAt is null || ExpiresAt > now);
}
