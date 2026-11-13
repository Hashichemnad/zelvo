using Zelvo.Api.Conventions;
using Zelvo.Data.Tenancy;

namespace Zelvo.Api.Tenancy;

/// <summary>
/// Sets the tenant of each API request from the <c>X-Zelvo-Tenant</c> header (a tenant slug), or
/// from <c>Tenancy:DefaultTenant</c> for single-tenant installations. Unknown tenants get 404 and
/// suspended tenants 403. Signed-in requests must carry a token from this tenant's own Keycloak realm
/// (<see cref="Identity.ZelvoBearerHandler"/>), so the header cannot switch tenants. Later tasks add custom domains.
/// </summary>
internal sealed class TenantResolutionMiddleware(RequestDelegate next, IConfiguration configuration)
{
    public const string HeaderName = "X-Zelvo-Tenant";

    public async Task InvokeAsync(HttpContext context, TenantResolver resolver, ITenantContext tenantContext)
    {
        if (!context.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase))
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        var slug = context.Request.Headers[HeaderName].ToString();
        if (string.IsNullOrEmpty(slug))
        {
            slug = configuration["Tenancy:DefaultTenant"] ?? string.Empty;
        }

        if (slug.Length > 0)
        {
            if (!TenantProvisioning.SlugPattern().IsMatch(slug))
            {
                await ApiProblem.WriteAsync(context, StatusCodes.Status400BadRequest, "tenant-invalid", "The tenant identifier is not valid.").ConfigureAwait(false);
                return;
            }

            var tenant = await resolver.ResolveAsync(slug, context.RequestAborted).ConfigureAwait(false);
            if (tenant is null)
            {
                await ApiProblem.WriteAsync(context, StatusCodes.Status404NotFound, "tenant-not-found", "The tenant does not exist.").ConfigureAwait(false);
                return;
            }

            if (tenant.Status != nameof(TenantStatus.Active))
            {
                await ApiProblem.WriteAsync(context, StatusCodes.Status403Forbidden, "tenant-suspended", "The tenant is suspended.").ConfigureAwait(false);
                return;
            }

            tenantContext.SetTenant(tenant.Id, tenant.Slug);
        }

        await next(context).ConfigureAwait(false);
    }
}

/// <summary>Endpoint filter: answers 400 <c>tenant-required</c> when the request has no tenant.</summary>
internal static class RequireTenantExtensions
{
    public static TBuilder RequireTenant<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
    {
        builder.AddEndpointFilter(async (context, next) =>
        {
            var tenantContext = context.HttpContext.RequestServices.GetRequiredService<ITenantContext>();
            return tenantContext.TenantId is null
                ? ApiProblem.Create(StatusCodes.Status400BadRequest, "tenant-required",
                    $"This endpoint needs a tenant: send the {TenantResolutionMiddleware.HeaderName} header.")
                : await next(context).ConfigureAwait(false);
        });
        return builder;
    }
}
