using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.Extensions.Options;
using Zelvo.Api.Conventions;
using Zelvo.Api.Tenancy;

namespace Zelvo.Api.Identity;

/// <summary>The signed-in user must hold a permission (through a role that applies now).</summary>
internal sealed class PermissionRequirement(string permission) : IAuthorizationRequirement
{
    public string Permission { get; } = permission;
}

/// <summary>
/// Grants <see cref="PermissionRequirement"/> from <see cref="CurrentUser"/>. When the permission
/// would apply after a second factor, the failure says so, and the API answers 401 with
/// <c>insufficient_user_authentication</c> (RFC 9470) instead of 403.
/// </summary>
internal sealed class PermissionHandler(CurrentUser currentUser) : AuthorizationHandler<PermissionRequirement>
{
    public const string MfaRequiredReason = "mfa-required";

    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, PermissionRequirement requirement)
    {
        if (currentUser.Has(requirement.Permission))
        {
            context.Succeed(requirement);
        }
        else if (currentUser.PendingMfaPermissions.Contains(requirement.Permission))
        {
            context.Fail(new AuthorizationFailureReason(this, MfaRequiredReason));
        }

        return Task.CompletedTask;
    }
}

/// <summary>Policies named <c>permission:&lt;key&gt;</c> are built on demand.</summary>
internal sealed class PermissionPolicyProvider(IOptions<AuthorizationOptions> options) : DefaultAuthorizationPolicyProvider(options)
{
    public const string Prefix = "permission:";

    public override async Task<AuthorizationPolicy?> GetPolicyAsync(string policyName)
    {
        if (policyName.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .AddRequirements(new PermissionRequirement(policyName[Prefix.Length..]))
                .Build();
        }

        return await base.GetPolicyAsync(policyName).ConfigureAwait(false);
    }
}

/// <summary>Writes problem details for refused requests, with the step-up challenge when a second factor would help.</summary>
internal sealed class ZelvoAuthorizationResultHandler : IAuthorizationMiddlewareResultHandler
{
    private readonly AuthorizationMiddlewareResultHandler _default = new();

    public async Task HandleAsync(RequestDelegate next, HttpContext context, AuthorizationPolicy policy, PolicyAuthorizationResult authorizeResult)
    {
        if (authorizeResult.Forbidden
            && authorizeResult.AuthorizationFailure?.FailureReasons.Any(r => r.Message == PermissionHandler.MfaRequiredReason) == true)
        {
            context.Response.Headers.WWWAuthenticate =
                "Bearer error=\"insufficient_user_authentication\", error_description=\"Sign in with a second factor.\", acr_values=\"mfa\"";
            await ApiProblem.WriteAsync(context, StatusCodes.Status401Unauthorized, "mfa-required",
                "Sign in with a second factor to do this.").ConfigureAwait(false);
            return;
        }

        if (authorizeResult.Challenged && context.User.Identity?.IsAuthenticated != true)
        {
            context.Response.Headers.WWWAuthenticate = "Bearer";
            await ApiProblem.WriteAsync(context, StatusCodes.Status401Unauthorized, "unauthenticated", "Sign in to do this.").ConfigureAwait(false);
            return;
        }

        if (authorizeResult.Forbidden)
        {
            await ApiProblem.WriteAsync(context, StatusCodes.Status403Forbidden, "forbidden", "You do not have permission to do this.").ConfigureAwait(false);
            return;
        }

        await _default.HandleAsync(next, context, policy, authorizeResult).ConfigureAwait(false);
    }
}

/// <summary>Endpoint conventions for access control.</summary>
internal static class AccessEndpointExtensions
{
    /// <summary>Signed-in users holding <paramref name="permission"/> only.</summary>
    public static TBuilder RequirePermission<TBuilder>(this TBuilder builder, string permission)
        where TBuilder : IEndpointConventionBuilder
    {
        if (!PermissionCatalog.IsPermission(permission))
        {
            throw new ArgumentException($"Unknown permission '{permission}'. Add it to PermissionCatalog.", nameof(permission));
        }

        return builder.RequireAuthorization(PermissionPolicyProvider.Prefix + permission);
    }

    /// <summary>
    /// Open to anonymous visitors of a tenant (for example public forms). The request still needs a
    /// tenant, and its audit entries record the actor as anonymous.
    /// </summary>
    public static TBuilder AllowAnonymousAccess<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder =>
        builder.AllowAnonymous().RequireTenant();
}
