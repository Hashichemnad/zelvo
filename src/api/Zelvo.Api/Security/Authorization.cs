using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using Zelvo.Data.Tenancy;

namespace Zelvo.Api.Security;

/// <summary>Role names. Task A2 maps the six personas from Keycloak onto these.</summary>
internal static class ZelvoRoles
{
    public const string TenantAdmin = "tenant-admin";
    public const string Auditor = "auditor";
}

/// <summary>Claim types issued for Zelvo users.</summary>
internal static class ZelvoClaims
{
    /// <summary>Id of the tenant the signed-in user belongs to.</summary>
    public const string TenantId = "zelvo_tenant_id";
}

/// <summary>Authorization policies used by endpoints.</summary>
internal static class ZelvoPolicies
{
    /// <summary>Tenant administrators of the current tenant.</summary>
    public const string TenantAdmin = "tenant-admin";

    /// <summary>Auditors and tenant administrators of the current tenant.</summary>
    public const string AuditRead = "audit-read";

    public static IServiceCollection AddZelvoAuthorization(this IServiceCollection services)
    {
        // Sign-in arrives with task A2 (Keycloak). Until then no request is authenticated, so
        // every protected endpoint answers 401; tests register their own scheme.
        services.AddAuthentication(NoSignInHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, NoSignInHandler>(NoSignInHandler.SchemeName, null);

        services.AddScoped<IAuthorizationHandler, CurrentTenantMemberHandler>();
        services.AddAuthorizationBuilder()
            .AddPolicy(TenantAdmin, policy => policy
                .RequireAuthenticatedUser()
                .AddRequirements(new CurrentTenantMemberRequirement())
                .RequireRole(ZelvoRoles.TenantAdmin))
            .AddPolicy(AuditRead, policy => policy
                .RequireAuthenticatedUser()
                .AddRequirements(new CurrentTenantMemberRequirement())
                .RequireRole(ZelvoRoles.Auditor, ZelvoRoles.TenantAdmin));
        return services;
    }
}

/// <summary>
/// The user belongs to the tenant of the request. Stops a signed-in user of one tenant from
/// choosing another tenant with the tenant header.
/// </summary>
internal sealed class CurrentTenantMemberRequirement : IAuthorizationRequirement;

internal sealed class CurrentTenantMemberHandler(ITenantContext tenantContext)
    : AuthorizationHandler<CurrentTenantMemberRequirement>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context, CurrentTenantMemberRequirement requirement)
    {
        if (tenantContext.TenantId is { } tenantId
            && context.User.FindFirstValue(ZelvoClaims.TenantId) is { } claimed
            && Guid.TryParse(claimed, out var userTenant)
            && userTenant == tenantId)
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}

/// <summary>Placeholder scheme until task A2: authenticates nobody.</summary>
internal sealed class NoSignInHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "None";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync() =>
        Task.FromResult(AuthenticateResult.NoResult());
}
