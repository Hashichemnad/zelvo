using Microsoft.AspNetCore.Http.HttpResults;
using Zelvo.Api.Conventions;
using Zelvo.Api.Identity;
using Zelvo.Api.Settings;
using Zelvo.Api.Tenancy;
using Zelvo.Data.Auditing;
using Zelvo.Data.Tenancy;
using Zelvo.Licensing;

namespace Zelvo.Api.Demo;

/// <summary>
/// Demo mode: the sample organisations and their demo users (for one-click switching), and
/// "Reset demo". Only demo installations answer; every other installation answers 404 <c>not-demo</c>.
/// </summary>
internal static class DemoEndpoints
{
    public static RouteGroupBuilder MapDemoEndpoints(this RouteGroupBuilder group)
    {
        var demo = group.MapGroup("/demo").WithTags("Demo").RequireTenant();

        demo.MapGet("/", Get)
            .WithName("GetDemo")
            .WithSummary("Demo installations only: the sample organisations, the demo users of this one, and when the demo was last reset. Open to anonymous visitors.")
            .AllowAnonymousAccess();

        demo.MapPost("/reset", ResetAsync)
            .WithName("ResetDemo")
            .WithSummary("Demo installations only: restores every sample organisation to its clean sample state. The audit log is kept. Needs demo.reset; audited.")
            .RequirePermission(Permissions.DemoReset);

        return group;
    }

    internal static async Task<Results<Ok<DemoResponse>, ProblemHttpResult>> Get(
        ILicenseService license, ITenantContext tenant, TenantSettingsStore settings, CancellationToken cancellationToken)
    {
        if (!license.IsDemo() || DemoCatalog.Find(tenant.Slug ?? string.Empty) is not { } organisation)
        {
            return NotDemo();
        }

        var lastReset = await settings.GetAsync(DemoSettings.LastResetAt, cancellationToken);
        return TypedResults.Ok(new DemoResponse(
            organisation.Slug,
            DemoCatalog.Organisations.Select(DemoOrganisationResponse.From).ToList(),
            organisation.Users.Select(u => DemoUserResponse.From(organisation, u)).ToList(),
            lastReset == DateTimeOffset.MinValue ? null : lastReset));
    }

    internal static async Task<Results<Ok<DemoResetResponse>, ProblemHttpResult>> ResetAsync(
        ILicenseService license, ITenantContext tenant, CurrentUser user, DemoReset reset, IAuditLog audit, IHostApplicationLifetime lifetime)
    {
        // A reset empties and reseeds every sample organisation, which takes longer than a browser or
        // the web proxy may wait; it is never stopped half-way because the caller went away, only when
        // the API shuts down.
        var cancellationToken = lifetime.ApplicationStopping;
        if (!license.IsDemo() || DemoCatalog.Find(tenant.Slug ?? string.Empty) is null)
        {
            return NotDemo();
        }

        var requestedBy = user.Account?.Id.ToString() ?? "unknown";
        var results = await reset.ResetAsync(requestedBy, tenant.Slug!, cancellationToken);
        if (results is null)
        {
            return (ProblemHttpResult)ApiProblem.Create(StatusCodes.Status409Conflict, "demo-reset-running", "A reset of the demo is already running.");
        }

        // The requester's own record was recreated by the reset; the entry names who asked.
        await audit.RecordAsync(AuditCategory.Admin, "demo.reset-requested", "demo", null,
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["organisations"] = results.Select(r => r.Slug).ToList(),
            }, cancellationToken);

        return TypedResults.Ok(new DemoResetResponse(DateTimeOffset.UtcNow, results.Select(r => r.Slug).ToList()));
    }

    private static ProblemHttpResult NotDemo() =>
        (ProblemHttpResult)ApiProblem.Create(StatusCodes.Status404NotFound, "not-demo", "This is not a demo installation.");
}

/// <summary>Demo mode for the request's sample organisation.</summary>
/// <param name="Organisation">Slug of this sample organisation (the request's tenant).</param>
/// <param name="Organisations">All sample organisations; the demo panel switches between them.</param>
/// <param name="Users">This organisation's demo users, one per persona, in persona order.</param>
/// <param name="LastResetAt">When the demo was last reset or first seeded.</param>
internal sealed record DemoResponse(
    string Organisation,
    IReadOnlyList<DemoOrganisationResponse> Organisations,
    IReadOnlyList<DemoUserResponse> Users,
    DateTimeOffset? LastResetAt);

/// <summary>A sample organisation.</summary>
internal sealed record DemoOrganisationResponse(string Slug, DemoOrganisationKind Kind, LocalizedTextDto Name)
{
    public static DemoOrganisationResponse From(DemoOrganisation organisation) =>
        new(organisation.Slug, organisation.Kind, LocalizedTextDto.From(organisation.Name));
}

/// <summary>A demo user (fictional person) and the persona they show.</summary>
/// <param name="Persona">Persona key used to sign in as this user.</param>
/// <param name="RoleKey">Built-in role.</param>
/// <param name="RoleName">The role's built-in name in English and Arabic.</param>
/// <param name="DisplayName">The user's name.</param>
/// <param name="Username">Sign-in name (a reserved .example address).</param>
internal sealed record DemoUserResponse(string Persona, string RoleKey, LocalizedTextDto RoleName, string DisplayName, string Username)
{
    public static DemoUserResponse From(DemoOrganisation organisation, DemoUser user)
    {
        var role = PermissionCatalog.Roles.Single(r => r.Key == user.RoleKey);
        return new(user.Persona, user.RoleKey, LocalizedTextDto.From(role.Name), user.DisplayName, organisation.UsernameOf(user));
    }
}

/// <summary>A finished reset.</summary>
internal sealed record DemoResetResponse(DateTimeOffset ResetAt, IReadOnlyList<string> Organisations);
