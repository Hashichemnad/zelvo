using Zelvo.Api.Conventions;
using Zelvo.Api.Identity;
using Zelvo.Data.Tenancy;
using Zelvo.Licensing;

namespace Zelvo.Api.Installation;

/// <summary>
/// Which permissions the license lets apply (task F3, module gating): a permission of a module the
/// license does not include never applies, and <c>installation.manage</c> applies only in the
/// installation's operator tenant, because licenses and updates belong to the whole installation.
/// </summary>
internal sealed class LicenseGate(ILicenseService license, ITenantContext tenant, IConfiguration configuration)
{
    public bool Allows(string permission)
    {
        if (permission == Permissions.InstallationManage)
        {
            return IsOperatorTenant(tenant, configuration);
        }

        return PermissionCatalog.ModuleOf(permission) is not { } module || license.IsModuleLicensed(module);
    }

    /// <summary>
    /// The tenant whose administrators manage the installation: <c>Installation:OperatorTenant</c>,
    /// else <c>Tenancy:DefaultTenant</c>; with neither set (a single-tenant development set-up), any.
    /// </summary>
    public static bool IsOperatorTenant(ITenantContext tenant, IConfiguration configuration)
    {
        var operatorTenant = configuration["Installation:OperatorTenant"] is { Length: > 0 } o ? o : configuration["Tenancy:DefaultTenant"];
        return string.IsNullOrEmpty(operatorTenant) || string.Equals(tenant.Slug, operatorTenant, StringComparison.Ordinal);
    }
}

/// <summary>Marks endpoints that belong to a licensed module (task F3).</summary>
/// <param name="Module">The module (<see cref="LicensedModules"/>).</param>
internal sealed record LicensedModuleMetadata(string Module);

/// <summary>Endpoint conventions for module gating.</summary>
internal static class LicensedModuleExtensions
{
    /// <summary>
    /// A group of the module's endpoints: they answer 403 <c>module-not-licensed</c> when the license
    /// does not include it. Use it as <c>v1.ForModule(LicensedModules.Payments).MapPaymentEndpoints()</c>.
    /// </summary>
    public static RouteGroupBuilder ForModule(this IEndpointRouteBuilder builder, string module)
    {
        if (!LicensedModules.Phase1.Contains(module))
        {
            throw new ArgumentException($"Unknown module '{module}'.", nameof(module));
        }

        return builder.MapGroup(string.Empty).WithMetadata(new LicensedModuleMetadata(module));
    }
}

/// <summary>
/// Enforces the license on every API request (task F3). While the installation waits for activation
/// (no license, or a license for other servers) only what the activation needs answers: sign-in and
/// the person's own details, the tenant's name and look, the published privacy notice, the setup wizard
/// and the license screens;
/// everything else answers 503 <c>license-required</c>. Endpoints of a module the license does not
/// include answer 403 <c>module-not-licensed</c>, and integrations (API keys and OAuth clients) need
/// the integrations module (14).
/// </summary>
internal sealed class LicenseEnforcementMiddleware(RequestDelegate next)
{
    private static readonly string[] OpenWhileNotActivated =
    [
        "/api/v1/system", "/api/v1/tenant", "/api/v1/me", "/api/v1/license", "/api/v1/setup", "/api/v1/branding", "/api/v1/translations",
        "/api/v1/demo",
        // Task F5: the published privacy notice people accept (GOV-5) is shown before activation too.
        "/api/v1/privacy/notice",
    ];

    public async Task InvokeAsync(HttpContext context, ILicenseService license, CurrentUser user)
    {
        var path = context.Request.Path;
        if (!path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase))
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        if (!license.Current.IsValid)
        {
            var open = user.ApiClient is null && OpenWhileNotActivated.Any(p => path.StartsWithSegments(p, StringComparison.OrdinalIgnoreCase));
            if (!open)
            {
                await ApiProblem.WriteAsync(context, StatusCodes.Status503ServiceUnavailable, "license-required",
                    "Zelvo is not activated on these servers. An administrator imports the license in Settings → License.").ConfigureAwait(false);
                return;
            }

            await next(context).ConfigureAwait(false);
            return;
        }

        if (user.ApiClient is not null && !license.IsModuleLicensed(LicensedModules.Integrations)
            && !path.StartsWithSegments("/api/v1/system", StringComparison.OrdinalIgnoreCase))
        {
            await ApiProblem.WriteAsync(context, StatusCodes.Status403Forbidden, "module-not-licensed",
                "API keys and OAuth clients need the integrations module, which this installation's license does not include.").ConfigureAwait(false);
            return;
        }

        if (context.GetEndpoint()?.Metadata.GetMetadata<LicensedModuleMetadata>() is { } module && !license.IsModuleLicensed(module.Module))
        {
            await ApiProblem.WriteAsync(context, StatusCodes.Status403Forbidden, "module-not-licensed",
                "This installation's license does not include this module.").ConfigureAwait(false);
            return;
        }

        await next(context).ConfigureAwait(false);
    }
}
