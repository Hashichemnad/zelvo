using Microsoft.EntityFrameworkCore;
using Zelvo.Api.Tenancy;
using Zelvo.Data;
using Zelvo.Data.Auditing;
using Zelvo.Data.Tenancy;
using Zelvo.Notifications;

namespace Zelvo.Api.Demo;

/// <summary>The state of a reset of the demo.</summary>
internal enum DemoResetState
{
    /// <summary>No reset since the API started.</summary>
    Idle,

    /// <summary>A reset is running.</summary>
    Running,

    /// <summary>The last reset finished.</summary>
    Succeeded,

    /// <summary>The last reset stopped with an error.</summary>
    Failed,
}

/// <summary>A reset of the demo and how far it has got.</summary>
/// <param name="Id">The reset.</param>
/// <param name="State">Running, finished or failed.</param>
/// <param name="StartedAt">When it started.</param>
/// <param name="FinishedAt">When it finished or failed.</param>
/// <param name="Organisation">The sample organisation being reset now (its slug).</param>
/// <param name="Stage">Emptying, seeding or indexing.</param>
/// <param name="Seeder">The seeder running now.</param>
/// <param name="Done">Steps done.</param>
/// <param name="Total">Steps in all: for each organisation, emptying, each seeder and indexing.</param>
/// <param name="Organisations">The sample organisations reset (finished resets).</param>
internal sealed record DemoResetRun(
    Guid Id,
    DemoResetState State,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt,
    string? Organisation,
    DemoResetStage? Stage,
    string? Seeder,
    int Done,
    int Total,
    IReadOnlyList<string> Organisations);

/// <summary>
/// "Reset demo" as a background job: emptying and reseeding both sample organisations takes longer
/// than a browser, the web proxy or an HTTP client waits for an answer, so the request only starts
/// it (202) and the demo panel follows its steps (<c>GET /api/v1/demo/reset</c>). One reset runs at
/// a time in this API; it stops only when the API shuts down, never because the person who asked
/// went away. When it ends, the person who asked gets a notification (<c>demo.reset-finished</c> or
/// <c>demo.reset-failed</c>), in the app and by email. Demo installations run one API, so the
/// state is kept in memory; after a restart the panel shows the last reset from the tenant settings.
/// </summary>
internal sealed partial class DemoResetJobs(
    IServiceScopeFactory scopes,
    DemoSeeding seeding,
    IHostApplicationLifetime lifetime,
    ILogger<DemoResetJobs> logger)
{
    private readonly Lock _lock = new();
    private DemoResetRun? _run;
    private Task _task = Task.CompletedTask;

    /// <summary>The current or last reset; <see langword="null"/> when none ran since the API started.</summary>
    public DemoResetRun? Current
    {
        get
        {
            lock (_lock)
            {
                return _run;
            }
        }
    }

    /// <summary>Whether a reset is running now (the sample organisations' tables may be empty).</summary>
    public bool IsRunning => Current?.State == DemoResetState.Running;

    /// <summary>The running job, for tests that wait for it.</summary>
    internal Task Completion
    {
        get
        {
            lock (_lock)
            {
                return _task;
            }
        }
    }

    /// <summary>
    /// Starts a reset asked for by <paramref name="requestedBy"/> (a Zelvo user id, recorded in the
    /// audit log) in <paramref name="requestedIn"/>, whose Keycloak subject <paramref name="subject"/>
    /// is told when it ends. <see langword="null"/> when a reset is already running.
    /// </summary>
    public DemoResetRun? Start(string requestedBy, string requestedIn, string? subject)
    {
        DemoResetRun run;
        lock (_lock)
        {
            if (_run?.State == DemoResetState.Running)
            {
                return null;
            }

            var total = DemoCatalog.Organisations.Count * (seeding.Seeders.Count + DemoReset.StepsBesidesSeeders);
            run = new DemoResetRun(Guid.NewGuid(), DemoResetState.Running, DateTimeOffset.UtcNow, null,
                DemoCatalog.Organisations[0].Slug, DemoResetStage.Emptying, null, 0, total, []);
            _run = run;
            _task = Task.Run(() => RunAsync(run.Id, requestedBy, requestedIn, subject, lifetime.ApplicationStopping));
        }

        return run;
    }

    private async Task RunAsync(Guid id, string requestedBy, string requestedIn, string? subject, CancellationToken cancellationToken)
    {
        var succeeded = false;
        IReadOnlyList<string> organisations = [];
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var reset = scope.ServiceProvider.GetRequiredService<DemoReset>();
            var results = await reset.ResetAsync(requestedBy, requestedIn, new StepProgress(step => Advance(id, step)), cancellationToken).ConfigureAwait(false);
            organisations = results.Select(r => r.Slug).ToList();
            succeeded = true;
        }
#pragma warning disable CA1031 // A failed reset is reported to the person who asked and shown in the demo panel.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            LogResetFailed(logger, exception);
        }

        lock (_lock)
        {
            if (_run?.Id == id)
            {
                _run = _run with
                {
                    State = succeeded ? DemoResetState.Succeeded : DemoResetState.Failed,
                    FinishedAt = DateTimeOffset.UtcNow,
                    Done = succeeded ? _run.Total : _run.Done,
                    Stage = succeeded ? null : _run.Stage,
                    Seeder = succeeded ? null : _run.Seeder,
                    Organisation = succeeded ? null : _run.Organisation,
                    Organisations = organisations,
                };
            }
        }

        try
        {
            await TellAsync(succeeded, requestedIn, subject, organisations).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // The reset itself is done; a notification that cannot be queued is only logged.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            LogNotifyFailed(logger, exception);
        }
    }

    private void Advance(Guid id, DemoResetStep step)
    {
        lock (_lock)
        {
            if (_run is not { State: DemoResetState.Running } run || run.Id != id)
            {
                return;
            }

            // Each report starts a step, so the steps before it are done.
            var position = DemoCatalog.Organisations.ToList().FindIndex(o => o.Slug == step.Organisation);
            var perOrganisation = seeding.Seeders.Count + DemoReset.StepsBesidesSeeders;
            var within = step.Stage switch
            {
                DemoResetStage.Emptying => 0,
                DemoResetStage.Seeding => 1 + Math.Max(0, seeding.Seeders.ToList().FindIndex(s => s.Key == step.Seeder)),
                _ => perOrganisation - 1,
            };
            _run = run with
            {
                Organisation = step.Organisation, Stage = step.Stage, Seeder = step.Seeder,
                Done = Math.Max(run.Done, Math.Min(run.Total, (Math.Max(0, position) * perOrganisation) + within)),
            };
        }
    }

    // The person who asked, by their sign-in: the reset recreated their Zelvo record.
    private async Task TellAsync(bool succeeded, string requestedIn, string? subject, IReadOnlyList<string> organisations)
    {
        if (subject is null)
        {
            return;
        }

        await using var lookup = scopes.CreateAsyncScope();
        var resolved = await lookup.ServiceProvider.GetRequiredService<TenantResolver>().ResolveAsync(requestedIn, CancellationToken.None).ConfigureAwait(false);
        if (resolved is null)
        {
            return;
        }

        await using var scope = scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().SetTenant(resolved.Id, resolved.Slug);
        scope.ServiceProvider.GetRequiredService<AuditContext>().SetActor(AuditActorType.System, "demo-reset", "Reset demo");
        var db = scope.ServiceProvider.GetRequiredService<ZelvoDbContext>();
        var user = await db.Users.AsNoTracking().Where(u => u.Subject == subject).Select(u => (Guid?)u.Id).SingleOrDefaultAsync(CancellationToken.None).ConfigureAwait(false);
        if (user is { } recipient)
        {
            scope.ServiceProvider.GetRequiredService<PendingNotifications>().Add(new NotificationRequest(
                succeeded ? NotificationEvents.DemoResetFinished : NotificationEvents.DemoResetFailed, [recipient],
                new Dictionary<string, PlaceholderValue>(StringComparer.Ordinal), "/"));
        }

        if (!succeeded)
        {
            scope.ServiceProvider.GetRequiredService<IAuditLog>().Add(AuditCategory.Admin, "demo.reset-failed", "demo", null,
                new Dictionary<string, object?>(StringComparer.Ordinal) { ["organisations"] = organisations });
        }

        await db.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Resetting the demo failed.")]
    private static partial void LogResetFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The demo reset's notification could not be queued.")]
    private static partial void LogNotifyFailed(ILogger logger, Exception exception);

    // Reports at once, in order (Progress<T> would post each report to the thread pool).
    private sealed class StepProgress(Action<DemoResetStep> report) : IProgress<DemoResetStep>
    {
        public void Report(DemoResetStep value) => report(value);
    }
}

/// <summary>
/// While a reset runs, the sample organisations' tables are empty or half seeded: requests to them
/// would see missing data, and a signed-in person's first request would even make them a new
/// requester account. So every request to a sample organisation answers 503 <c>demo-reset-running</c>
/// (with <c>Retry-After</c>) except the demo panel's own (<c>/api/v1/demo</c> and its reset status),
/// which are answered without the sign-in, and health checks.
/// </summary>
internal sealed class DemoResetGuardMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, DemoResetJobs jobs, ITenantContext tenant)
    {
        if (!jobs.IsRunning || DemoCatalog.Find(tenant.Slug ?? string.Empty) is null)
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        var path = context.Request.Path;
        if (HttpMethods.IsGet(context.Request.Method)
            && (path.Equals("/api/v1/demo", StringComparison.OrdinalIgnoreCase) || path.Equals("/api/v1/demo/", StringComparison.OrdinalIgnoreCase)
                || path.Equals("/api/v1/demo/reset", StringComparison.OrdinalIgnoreCase)))
        {
            // Open to visitors anyway; without the sign-in no account is looked up or made.
            context.Request.Headers.Authorization = default;
            await next(context).ConfigureAwait(false);
            return;
        }

        if (!path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase))
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        context.Response.Headers.RetryAfter = "5";
        await Conventions.ApiProblem.WriteAsync(context, StatusCodes.Status503ServiceUnavailable, "demo-reset-running",
            "The demo is being reset. Try again in a moment.").ConfigureAwait(false);
    }
}
