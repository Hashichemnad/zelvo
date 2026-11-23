using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Zelvo.Api.Conventions;
using Zelvo.Api.Identity;
using Zelvo.Api.Tenancy;
using Zelvo.Data;
using Zelvo.Data.Auditing;
using Zelvo.Data.Identity;
using Zelvo.Data.Integrations;
using Zelvo.Data.Tenancy;
using Zelvo.Integrations;
using Zelvo.Licensing;

namespace Zelvo.Api.Integrations;

/// <summary>
/// Settings → Integrations → API keys and OAuth clients (module 14): each integration has its own
/// role, scopes and rate limit, can be revoked, and has its key or secret shown only once.
/// </summary>
internal static partial class ApiClientEndpoints
{
    [LoggerMessage(Level = LogLevel.Error, Message = "Keycloak refused a change to an integration's OAuth client.")]
    private static partial void LogKeycloakRefused(ILogger logger, Exception exception);

    public static RouteGroupBuilder MapApiClientEndpoints(this RouteGroupBuilder group)
    {
        var integrations = group.MapGroup("/integrations").WithTags("Integrations").RequireTenant();

        integrations.MapGet("/info", InfoAsync)
            .WithName("GetIntegrationInfo")
            .WithSummary("What integrations need to know: this installation's environment (production, test, demo or development), the OAuth token URL, the API key header, the scopes an integration can be given, the webhook event types, and how long the integration log is kept. Needs integrations.manage.")
            .RequirePermission(Permissions.IntegrationsManage)
            .Produces<IntegrationInfoResponse>();

        var clients = integrations.MapGroup("/clients");
        clients.MapGet("/", ListAsync)
            .WithName("ListApiClients")
            .WithSummary("API keys and OAuth clients of the tenant, newest first. Keys and secrets are never shown again after creation. Needs integrations.manage.")
            .RequirePermission(Permissions.IntegrationsManage)
            .Produces<PageResult<ApiClientResponse>>();

        clients.MapGet("/{id:guid}", GetAsync)
            .WithName("GetApiClient")
            .WithSummary("One API key or OAuth client. Needs integrations.manage.")
            .RequirePermission(Permissions.IntegrationsManage)
            .Produces<ApiClientResponse>();

        clients.MapPost("/", CreateAsync)
            .WithName("CreateApiClient")
            .WithSummary("Creates an API key (sent in X-Api-Key) or an OAuth client (client credentials grant at the token URL). The key or client secret is in this response only. The integration acts with one role, narrowed by its scopes; users.manage, roles.manage, identity.manage and integrations.manage can never be given. Needs integrations.manage; audited.")
            .RequirePermission(Permissions.IntegrationsManage)
            .Produces<ApiClientSecretResponse>(StatusCodes.Status201Created)
            .ProducesValidationProblem();

        clients.MapPatch("/{id:guid}", UpdateAsync)
            .WithName("UpdateApiClient")
            .WithSummary("Changes an integration's name, description, role, scopes, rate limit or expiry. Revoked integrations cannot be changed. Needs integrations.manage; audited.")
            .RequirePermission(Permissions.IntegrationsManage)
            .Produces<ApiClientResponse>()
            .ProducesValidationProblem();

        clients.MapPost("/{id:guid}/revoke", RevokeAsync)
            .WithName("RevokeApiClient")
            .WithSummary("Revokes an API key or OAuth client: its calls are refused from now on, including with OAuth tokens issued earlier. Cannot be undone. Needs integrations.manage; audited.")
            .RequirePermission(Permissions.IntegrationsManage)
            .Produces<ApiClientResponse>();

        clients.MapPost("/{id:guid}/rotate", RotateAsync)
            .WithName("RotateApiClientSecret")
            .WithSummary("Replaces the API key or client secret; the old one stops working at once. The new one is in this response only. Needs integrations.manage; audited.")
            .RequirePermission(Permissions.IntegrationsManage)
            .Produces<ApiClientSecretResponse>();

        return group;
    }

    internal static async Task<IntegrationInfoResponse> InfoAsync(
        ZelvoDbContext db, ITenantContext tenant, IOptions<IdentityOptions> identity, ILicenseService licenses, IntegrationOptions options,
        CancellationToken cancellationToken)
    {
        var roles = await db.Roles.AsNoTracking().Include(r => r.Permissions).OrderBy(r => r.Key).ToListAsync(cancellationToken);
        return new(licenses.EnvironmentName(), TokenUrl(identity.Value, tenant.Slug!), ApiKeys.HeaderName,
            PermissionCatalog.IntegrationScopes, WebhookEventTypes.All, (int)options.LogRetention.TotalDays,
            ApiClient.DefaultRateLimit, ApiClient.MaxRateLimit,
            [.. roles.Select(r => new IntegrationRoleResponse(r.Key, LocalizedTextDto.From(r.Name),
                [.. r.Permissions.Select(p => p.Permission).Where(p => PermissionCatalog.IntegrationScopes.Contains(p, StringComparer.Ordinal)).Order(StringComparer.Ordinal)]))]);
    }

    internal static async Task<IResult> ListAsync(
        [AsParameters] PageQuery page, [AsParameters] ApiClientListQuery filter, ZelvoDbContext db,
        ITenantContext tenant, IOptions<IdentityOptions> identity, CancellationToken cancellationToken)
    {
        var query = db.ApiClients.AsNoTracking();
        if (filter.Status is { } status)
        {
            query = query.Where(c => c.Status == status);
        }

        query = query.OrderByDescending(c => c.CreatedAt).ThenByDescending(c => c.Id);
        var total = await query.LongCountAsync(cancellationToken);
        var items = await query.Skip(page.Skip).Take(page.Size).ToListAsync(cancellationToken);
        var roles = await RoleKeysAsync(db, cancellationToken);
        var tokenUrl = TokenUrl(identity.Value, tenant.Slug!);
        return TypedResults.Ok(new PageResult<ApiClientResponse>(
            [.. items.Select(c => ApiClientResponse.From(c, roles, tokenUrl))], page.Number, page.Size, total));
    }

    internal static async Task<IResult> GetAsync(
        Guid id, ZelvoDbContext db, ITenantContext tenant, IOptions<IdentityOptions> identity, CancellationToken cancellationToken)
    {
        var client = await db.ApiClients.AsNoTracking().SingleOrDefaultAsync(c => c.Id == id, cancellationToken);
        return client is null
            ? NotFound()
            : TypedResults.Ok(ApiClientResponse.From(client, await RoleKeysAsync(db, cancellationToken), TokenUrl(identity.Value, tenant.Slug!)));
    }

    internal static async Task<IResult> CreateAsync(
        CreateApiClientRequest request, ZelvoDbContext db, CurrentUser user, ITenantContext tenant, IKeycloakAdmin keycloak,
        IOptions<IdentityOptions> identity, IAuditLog audit, ILoggerFactory loggers, CancellationToken cancellationToken)
    {
        var (role, problem) = await CheckRoleAndScopesAsync(db, request.Role!, request.Scopes!, cancellationToken);
        if (problem is not null)
        {
            return problem;
        }

        if (request.ExpiresAt is { } expires && expires <= DateTimeOffset.UtcNow)
        {
            return UserEndpoints.Validation("expiresAt", "The expiry must be in the future.");
        }

        var client = new ApiClient
        {
            Kind = request.Kind!.Value,
            Name = request.Name!.Trim(),
            Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim(),
            RoleId = role!.Id,
            Scopes = [.. request.Scopes!.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)],
            RateLimitPerMinute = request.RateLimitPerMinute ?? ApiClient.DefaultRateLimit,
            ExpiresAt = request.ExpiresAt,
            CreatedBy = user.Account?.Id,
        };

        string? apiKey = null;
        string? clientSecret = null;
        var settings = identity.Value;
        if (client.Kind == ApiClientKind.ApiKey)
        {
            (client.KeyPrefix, client.KeyHash, apiKey) = ApiKeys.Create();
        }
        else
        {
            client.KeycloakClientId = $"zelvo-int-{client.Id:N}";
            try
            {
                var created = await keycloak.CreateIntegrationClientAsync(settings.RealmFor(tenant.Slug!),
                    new IntegrationClientSpec(client.KeycloakClientId, $"Zelvo integration: {client.Name}", tenant.TenantId!.Value, client.Id, settings.Audience),
                    cancellationToken);
                client.KeycloakInternalId = created.InternalId;
                client.ServiceAccountSubject = created.ServiceAccountSubject;
                clientSecret = created.Secret;
            }
            catch (Exception ex) when (ex is KeycloakAdminException or HttpRequestException)
            {
                LogKeycloakRefused(loggers.CreateLogger("Zelvo.Integrations"), ex);
                return KeycloakUnavailable();
            }
        }

        db.ApiClients.Add(client);
        audit.Add(AuditCategory.Admin, "integrations.client.created", "api_client", client.Id.ToString(),
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["kind"] = client.Kind.ToString(), ["role"] = role.Key });
        await db.SaveChangesAsync(cancellationToken);

        var response = ApiClientResponse.From(client, new Dictionary<Guid, string> { [role.Id] = role.Key }, TokenUrl(settings, tenant.Slug!));
        return TypedResults.Created($"/api/v1/integrations/clients/{client.Id}", new ApiClientSecretResponse(response, apiKey, clientSecret));
    }

    internal static async Task<IResult> UpdateAsync(
        Guid id, UpdateApiClientRequest request, ZelvoDbContext db, ITenantContext tenant, IOptions<IdentityOptions> identity,
        CancellationToken cancellationToken)
    {
        var client = await db.ApiClients.SingleOrDefaultAsync(c => c.Id == id, cancellationToken);
        if (client is null)
        {
            return NotFound();
        }

        if (client.Status == ApiClientStatus.Revoked)
        {
            return Revoked();
        }

        var roleKey = request.Role ?? (await db.Roles.SingleAsync(r => r.Id == client.RoleId, cancellationToken)).Key;
        var (role, problem) = await CheckRoleAndScopesAsync(db, roleKey, request.Scopes ?? client.Scopes, cancellationToken);
        if (problem is not null)
        {
            return problem;
        }

        if (request.ExpiresAt is { } expires && expires <= DateTimeOffset.UtcNow)
        {
            return UserEndpoints.Validation("expiresAt", "The expiry must be in the future.");
        }

        if (request.Name is not null)
        {
            client.Name = request.Name.Trim();
        }

        if (request.Description is not null)
        {
            client.Description = request.Description.Length == 0 ? null : request.Description.Trim();
        }

        client.RoleId = role!.Id;
        if (request.Scopes is not null)
        {
            client.Scopes = [.. request.Scopes.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
        }

        if (request.RateLimitPerMinute is { } limit)
        {
            client.RateLimitPerMinute = limit;
        }

        if (request.NoExpiry == true)
        {
            client.ExpiresAt = null;
        }
        else if (request.ExpiresAt is not null)
        {
            client.ExpiresAt = request.ExpiresAt;
        }

        client.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Ok(ApiClientResponse.From(client, await RoleKeysAsync(db, cancellationToken), TokenUrl(identity.Value, tenant.Slug!)));
    }

    internal static async Task<IResult> RevokeAsync(
        Guid id, ZelvoDbContext db, ITenantContext tenant, IKeycloakAdmin keycloak, IOptions<IdentityOptions> identity,
        IAuditLog audit, ILoggerFactory loggers, CancellationToken cancellationToken)
    {
        var client = await db.ApiClients.SingleOrDefaultAsync(c => c.Id == id, cancellationToken);
        if (client is null)
        {
            return NotFound();
        }

        if (client.Status != ApiClientStatus.Revoked)
        {
            if (client is { Kind: ApiClientKind.OAuthClient, KeycloakInternalId: { } internalId })
            {
                try
                {
                    await keycloak.SetClientEnabledAsync(identity.Value.RealmFor(tenant.Slug!), internalId, false, cancellationToken);
                }
                catch (Exception ex) when (ex is KeycloakAdminException or HttpRequestException)
                {
                    // Zelvo refuses the client's tokens either way; Keycloak is switched off when it answers again.
                    LogKeycloakRefused(loggers.CreateLogger("Zelvo.Integrations"), ex);
                }
            }

            client.Status = ApiClientStatus.Revoked;
            client.RevokedAt = DateTimeOffset.UtcNow;
            client.UpdatedAt = client.RevokedAt.Value;
            audit.Add(AuditCategory.Security, "integrations.client.revoked", "api_client", client.Id.ToString());
            await db.SaveChangesAsync(cancellationToken);
        }

        return TypedResults.Ok(ApiClientResponse.From(client, await RoleKeysAsync(db, cancellationToken), TokenUrl(identity.Value, tenant.Slug!)));
    }

    internal static async Task<IResult> RotateAsync(
        Guid id, ZelvoDbContext db, ITenantContext tenant, IKeycloakAdmin keycloak, IOptions<IdentityOptions> identity,
        IAuditLog audit, ILoggerFactory loggers, CancellationToken cancellationToken)
    {
        var client = await db.ApiClients.SingleOrDefaultAsync(c => c.Id == id, cancellationToken);
        if (client is null)
        {
            return NotFound();
        }

        if (client.Status == ApiClientStatus.Revoked)
        {
            return Revoked();
        }

        string? apiKey = null;
        string? clientSecret = null;
        if (client.Kind == ApiClientKind.ApiKey)
        {
            (client.KeyPrefix, client.KeyHash, apiKey) = ApiKeys.Create();
        }
        else
        {
            try
            {
                clientSecret = await keycloak.RegenerateClientSecretAsync(identity.Value.RealmFor(tenant.Slug!), client.KeycloakInternalId!, cancellationToken);
            }
            catch (Exception ex) when (ex is KeycloakAdminException or HttpRequestException)
            {
                LogKeycloakRefused(loggers.CreateLogger("Zelvo.Integrations"), ex);
                return KeycloakUnavailable();
            }
        }

        client.RotatedAt = DateTimeOffset.UtcNow;
        client.UpdatedAt = client.RotatedAt.Value;
        audit.Add(AuditCategory.Security, "integrations.client.rotated", "api_client", client.Id.ToString());
        await db.SaveChangesAsync(cancellationToken);
        var response = ApiClientResponse.From(client, await RoleKeysAsync(db, cancellationToken), TokenUrl(identity.Value, tenant.Slug!));
        return TypedResults.Ok(new ApiClientSecretResponse(response, apiKey, clientSecret));
    }

    /// <summary>The role must exist; every scope must be grantable to integrations and held by the role.</summary>
    private static async Task<(Role? Role, IResult? Problem)> CheckRoleAndScopesAsync(
        ZelvoDbContext db, string roleKey, IReadOnlyCollection<string> scopes, CancellationToken cancellationToken)
    {
        var role = await db.Roles.AsNoTracking().Include(r => r.Permissions).SingleOrDefaultAsync(r => r.Key == roleKey, cancellationToken);
        if (role is null)
        {
            return (null, UserEndpoints.Validation("role", $"Unknown role '{roleKey}'."));
        }

        var notAllowed = scopes.Where(s => !PermissionCatalog.IntegrationScopes.Contains(s, StringComparer.Ordinal)).ToList();
        if (notAllowed.Count > 0)
        {
            return (role, UserEndpoints.Validation("scopes", $"These cannot be given to integrations: {string.Join(", ", notAllowed)}."));
        }

        var held = role.Permissions.Select(p => p.Permission).ToHashSet(StringComparer.Ordinal);
        var notHeld = scopes.Where(s => !held.Contains(s)).ToList();
        return notHeld.Count > 0
            ? (role, UserEndpoints.Validation("scopes", $"The role '{role.Key}' does not have: {string.Join(", ", notHeld)}."))
            : (role, null);
    }

    private static async Task<Dictionary<Guid, string>> RoleKeysAsync(ZelvoDbContext db, CancellationToken cancellationToken) =>
        await db.Roles.AsNoTracking().ToDictionaryAsync(r => r.Id, r => r.Key, cancellationToken);

    internal static string TokenUrl(IdentityOptions identity, string slug) => $"{identity.IssuerFor(slug)}/protocol/openid-connect/token";

    private static IResult NotFound() =>
        ApiProblem.Create(StatusCodes.Status404NotFound, "api-client-not-found", "The API key or OAuth client does not exist.");

    private static IResult Revoked() =>
        ApiProblem.Create(StatusCodes.Status409Conflict, "api-client-revoked", "This API key or OAuth client is revoked and cannot be changed.");

    private static IResult KeycloakUnavailable() =>
        ApiProblem.Create(StatusCodes.Status502BadGateway, "identity-provider-unavailable",
            "The sign-in service did not accept the change. Nothing was changed.");
}

/// <summary>Filters of the integration list.</summary>
public sealed class ApiClientListQuery
{
    /// <summary>Only active or only revoked integrations.</summary>
    public ApiClientStatus? Status { get; init; }
}

/// <summary>A new API key or OAuth client.</summary>
public sealed class CreateApiClientRequest
{
    /// <summary><c>ApiKey</c> or <c>OAuthClient</c>.</summary>
    [Required]
    public ApiClientKind? Kind { get; init; }

    /// <summary>Name, 1 to 100 characters.</summary>
    [Required]
    [StringLength(ApiClient.NameMaxLength, MinimumLength = 1)]
    public string? Name { get; init; }

    /// <summary>What the integration is for, up to 500 characters.</summary>
    [StringLength(ApiClient.DescriptionMaxLength)]
    public string? Description { get; init; }

    /// <summary>Key of the role it acts with, for example <c>agent</c>.</summary>
    [Required]
    [StringLength(Zelvo.Data.Identity.Role.KeyMaxLength, MinimumLength = 1)]
    public string? Role { get; init; }

    /// <summary>Permission keys it may use (each held by the role).</summary>
    [Required]
    [MaxLength(100)]
    public IReadOnlyList<string>? Scopes { get; init; }

    /// <summary>Requests per minute, 1 to 100,000 (default 600).</summary>
    [Range(ApiClient.MinRateLimit, ApiClient.MaxRateLimit)]
    public int? RateLimitPerMinute { get; init; }

    /// <summary>When calls stop being accepted; none when left out.</summary>
    public DateTimeOffset? ExpiresAt { get; init; }
}

/// <summary>Changes to an integration. Leave a field out to keep it.</summary>
public sealed class UpdateApiClientRequest
{
    /// <summary>Name, 1 to 100 characters.</summary>
    [StringLength(ApiClient.NameMaxLength, MinimumLength = 1)]
    public string? Name { get; init; }

    /// <summary>Description; empty string removes it.</summary>
    [StringLength(ApiClient.DescriptionMaxLength)]
    public string? Description { get; init; }

    /// <summary>Key of the role it acts with.</summary>
    [StringLength(Zelvo.Data.Identity.Role.KeyMaxLength, MinimumLength = 1)]
    public string? Role { get; init; }

    /// <summary>The complete list of scopes.</summary>
    [MaxLength(100)]
    public IReadOnlyList<string>? Scopes { get; init; }

    /// <summary>Requests per minute, 1 to 100,000.</summary>
    [Range(ApiClient.MinRateLimit, ApiClient.MaxRateLimit)]
    public int? RateLimitPerMinute { get; init; }

    /// <summary>New expiry.</summary>
    public DateTimeOffset? ExpiresAt { get; init; }

    /// <summary>True removes the expiry.</summary>
    public bool? NoExpiry { get; init; }
}

/// <summary>An API key or OAuth client. Keys and secrets are never included.</summary>
/// <param name="Id">Zelvo id.</param>
/// <param name="Kind"><c>ApiKey</c> or <c>OAuthClient</c>.</param>
/// <param name="Name">Name.</param>
/// <param name="Description">What it is for.</param>
/// <param name="Role">Key of its role.</param>
/// <param name="Scopes">Permission keys it may use.</param>
/// <param name="RateLimitPerMinute">Requests per minute.</param>
/// <param name="Status"><c>Active</c> or <c>Revoked</c>.</param>
/// <param name="Expired">Active but past its expiry.</param>
/// <param name="ExpiresAt">Expiry, if any.</param>
/// <param name="KeyHint">API keys: the start of the key (<c>zvk_&lt;prefix&gt;_…</c>), to recognise it.</param>
/// <param name="ClientId">OAuth clients: the client id to use at the token URL.</param>
/// <param name="TokenUrl">OAuth clients: where to get access tokens (client credentials grant).</param>
/// <param name="CreatedAt">When it was created.</param>
/// <param name="UpdatedAt">When it was last changed.</param>
/// <param name="RotatedAt">When its key or secret was last replaced.</param>
/// <param name="RevokedAt">When it was revoked.</param>
/// <param name="LastUsedAt">When it last called the API (to within five minutes).</param>
internal sealed record ApiClientResponse(
    Guid Id, ApiClientKind Kind, string Name, string? Description, string? Role, IReadOnlyList<string> Scopes, int RateLimitPerMinute,
    ApiClientStatus Status, bool Expired, DateTimeOffset? ExpiresAt, string? KeyHint, string? ClientId, string? TokenUrl,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, DateTimeOffset? RotatedAt, DateTimeOffset? RevokedAt, DateTimeOffset? LastUsedAt)
{
    public static ApiClientResponse From(ApiClient client, IReadOnlyDictionary<Guid, string> roles, string tokenUrl) => new(
        client.Id, client.Kind, client.Name, client.Description, roles.GetValueOrDefault(client.RoleId), client.Scopes,
        client.RateLimitPerMinute, client.Status,
        client.Status == ApiClientStatus.Active && client.ExpiresAt <= DateTimeOffset.UtcNow,
        client.ExpiresAt,
        client.KeyPrefix is { } prefix ? $"{ApiKeys.Prefix}{prefix}_…" : null,
        client.KeycloakClientId,
        client.Kind == ApiClientKind.OAuthClient ? tokenUrl : null,
        client.CreatedAt, client.UpdatedAt, client.RotatedAt, client.RevokedAt, client.LastUsedAt);
}

/// <summary>An integration with its new key or secret, shown this once.</summary>
/// <param name="Client">The integration.</param>
/// <param name="ApiKey">API keys: the key, for the <c>X-Api-Key</c> header.</param>
/// <param name="ClientSecret">OAuth clients: the client secret.</param>
internal sealed record ApiClientSecretResponse(ApiClientResponse Client, string? ApiKey, string? ClientSecret);

/// <summary>What integrations need to know about this installation and tenant.</summary>
/// <param name="Environment"><c>production</c>, <c>test</c>, <c>demo</c> or <c>development</c>; also in every API response (<c>Zelvo-Environment</c>) and webhook.</param>
/// <param name="TokenUrl">OAuth token URL of the tenant (client credentials grant).</param>
/// <param name="ApiKeyHeader">Header for API keys.</param>
/// <param name="Scopes">Permission keys an integration can be given.</param>
/// <param name="Events">Webhook event types.</param>
/// <param name="LogRetentionDays">Days the integration log and webhook deliveries are kept.</param>
/// <param name="DefaultRateLimit">Requests per minute of a new integration.</param>
/// <param name="MaxRateLimit">Highest per-minute limit that can be set.</param>
/// <param name="Roles">Roles an integration can act with, each with the scopes it can give.</param>
internal sealed record IntegrationInfoResponse(
    string Environment, string TokenUrl, string ApiKeyHeader, IReadOnlyList<string> Scopes, IReadOnlyList<string> Events,
    int LogRetentionDays, int DefaultRateLimit, int MaxRateLimit, IReadOnlyList<IntegrationRoleResponse> Roles);

/// <summary>A role an integration can act with.</summary>
/// <param name="Key">Role key.</param>
/// <param name="Name">Role name in English and Arabic.</param>
/// <param name="Scopes">Permissions of the role that can be given to integrations.</param>
internal sealed record IntegrationRoleResponse(string Key, LocalizedTextDto Name, IReadOnlyList<string> Scopes);
