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

        demo.MapPost("/reset", Reset)
            .WithName("ResetDemo")
            .WithSummary("Demo installations only: starts restoring every sample organisation to its clean sample state, as a background job (202 with its progress; 409 demo-reset-running while one runs). The audit log is kept. Needs demo.reset; audited. The person who asked is notified when it ends.")
            .RequirePermission(Permissions.DemoReset);

        demo.MapGet("/reset", GetReset)
            .WithName("GetDemoReset")
            .WithSummary("Demo installations only: the running or last reset of the demo and how far it has got (organisation, step, steps done of all). Open to anonymous visitors, as the demo panel follows it while the sample data is being replaced.")
            .AllowAnonymousAccess();

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

    internal static async Task<Results<Accepted<DemoResetStatusResponse>, Conflict<DemoResetStatusResponse>, ProblemHttpResult>> Reset(
        ILicenseService license, ITenantContext tenant, CurrentUser user, DemoResetJobs jobs, IAuditLog audit, CancellationToken cancellationToken)
    {
        if (!license.IsDemo() || DemoCatalog.Find(tenant.Slug ?? string.Empty) is null)
        {
            return NotDemo();
        }

        // Recorded first: the reset recreates the requester's own record, and the entry names who asked.
        await audit.RecordAsync(AuditCategory.Admin, "demo.reset-requested", "demo", null,
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["organisations"] = DemoCatalog.Organisations.Select(o => o.Slug).ToList(),
            }, cancellationToken);

        var requestedBy = user.Account?.Id.ToString() ?? "unknown";
        if (jobs.Start(requestedBy, tenant.Slug!, user.Account?.Subject) is not { } run)
        {
            return TypedResults.Conflict(DemoResetStatusResponse.From(jobs.Current));
        }

        return TypedResults.Accepted("/api/v1/demo/reset", DemoResetStatusResponse.From(run));
    }

    internal static Results<Ok<DemoResetStatusResponse>, ProblemHttpResult> GetReset(ILicenseService license, ITenantContext tenant, DemoResetJobs jobs)
    {
        if (!license.IsDemo() || DemoCatalog.Find(tenant.Slug ?? string.Empty) is null)
        {
            return NotDemo();
        }

        return TypedResults.Ok(DemoResetStatusResponse.From(jobs.Current));
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

/// <summary>A reset of the demo and how far it has got.</summary>
/// <param name="State">Idle (none since the API started), Running, Succeeded or Failed.</param>
/// <param name="Id">The reset.</param>
/// <param name="StartedAt">When it started.</param>
/// <param name="FinishedAt">When it finished or failed.</param>
/// <param name="Organisation">The sample organisation being reset now, or where it failed.</param>
/// <param name="Stage">Emptying, Seeding or Indexing.</param>
/// <param name="Seeder">The seeder running now, for example <c>f2-reporting</c>.</param>
/// <param name="Done">Steps done.</param>
/// <param name="Total">Steps in all.</param>
/// <param name="Percent">Steps done as a whole percentage.</param>
/// <param name="Organisations">The sample organisations a finished reset restored.</param>
internal sealed record DemoResetStatusResponse(
    DemoResetState State,
    Guid? Id,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    string? Organisation,
    DemoResetStage? Stage,
    string? Seeder,
    int Done,
    int Total,
    int Percent,
    IReadOnlyList<string> Organisations)
{
    public static DemoResetStatusResponse From(DemoResetRun? run) => run is null
        ? new(DemoResetState.Idle, null, null, null, null, null, null, 0, 0, 0, [])
        : new(run.State, run.Id, run.StartedAt, run.FinishedAt, run.Organisation, run.Stage, run.Seeder, run.Done, run.Total,
            run.Total == 0 ? 0 : (int)Math.Floor(run.Done * 100.0 / run.Total), run.Organisations);
}
