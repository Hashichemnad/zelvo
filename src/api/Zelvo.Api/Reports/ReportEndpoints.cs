using System.Globalization;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zelvo.Api.Branding;
using Zelvo.Api.Conventions;
using Zelvo.Api.Integrations;
using Zelvo.Api.Identity;
using Zelvo.Api.Integrations.Bulk;
using Zelvo.Api.Settings;
using Zelvo.Api.Tenancy;
using Zelvo.Data;
using Zelvo.Data.Auditing;
using Zelvo.Data.Branding;
using Zelvo.Data.Localization;
using Zelvo.Data.Tickets;
using Zelvo.Tickets.Reports;

namespace Zelvo.Api.Reports;

/// <summary>
/// Dashboards of module 10 (task F2): volumes, backlog, SLA compliance, agent performance and
/// happiness, plus an overview, with filters for period, record types, groups and categories;
/// exports as CSV or Excel and as a branded PDF. What a caller sees follows
/// <see cref="ReportAccess"/>: <c>reports.all</c> the whole tenant, <c>reports.read</c> the groups
/// they lead, other staff their own figures. Every view and export is audited.
/// </summary>
internal static class ReportEndpoints
{
    private const string FilterHelp =
        " Filter: from, to (dates in the tenant's time zone, both included; the last 30 days by default, at most 731 days), types (comma-separated record types), groups and categories (comma-separated ids; a category includes the ones below it), interval (Day, Week or Month; chosen from the period by default).";

    public static RouteGroupBuilder MapReportEndpoints(this RouteGroupBuilder group)
    {
        var reports = group.MapGroup("/reports").WithTags("Reports").RequireTenant().RequireAuthorization();
        reports.MapGet("/scope", ScopeAsync).WithName("GetReportScope")
            .WithSummary("What the caller sees in reports: the record types, and all groups, the groups they lead, or only their own figures. 403 when they may see no reports.")
            .Produces<ReportScopeResponse>();
        reports.MapGet("/overview", ([AsParameters] ReportQuery q, ZelvoDbContext db, CurrentUser user, IAuditLog audit, TimeProvider time, CancellationToken ct) => GetAsync(ReportKind.Overview, q, new(db, user, audit, time), ct))
            .WithName("GetOverviewReport").WithSummary("Key figures: tickets created and resolved in the period, open now, SLA targets met, happiness, median first response, and the tenant's targets. Audited." + FilterHelp)
            .Produces<OverviewReport>().ProducesValidationProblem();
        reports.MapGet("/volumes", ([AsParameters] ReportQuery q, ZelvoDbContext db, CurrentUser user, IAuditLog audit, TimeProvider time, CancellationToken ct) => GetAsync(ReportKind.Volumes, q, new(db, user, audit, time), ct))
            .WithName("GetVolumesReport").WithSummary("Tickets created, resolved and closed per day, week or month, and created tickets per record type, channel, priority, group and category. Audited." + FilterHelp)
            .Produces<VolumesReport>().ProducesValidationProblem();
        reports.MapGet("/backlog", ([AsParameters] ReportQuery q, ZelvoDbContext db, CurrentUser user, IAuditLog audit, TimeProvider time, CancellationToken ct) => GetAsync(ReportKind.Backlog, q, new(db, user, audit, time), ct))
            .WithName("GetBacklogReport").WithSummary("Open tickets now per state, age, record type, priority and group, how many are not assigned, and the open count at the end of each day, week or month of the period. Audited." + FilterHelp)
            .Produces<BacklogReport>().ProducesValidationProblem();
        reports.MapGet("/sla", ([AsParameters] ReportQuery q, ZelvoDbContext db, CurrentUser user, IAuditLog audit, TimeProvider time, CancellationToken ct) => GetAsync(ReportKind.Sla, q, new(db, user, audit, time), ct))
            .WithName("GetSlaReport").WithSummary("SLA compliance of tickets created in the period: response and resolution clocks met (stopped without a breach), breached and still running, per priority, record type, group and SLA policy, and over time, with the tenant's target. Audited." + FilterHelp)
            .Produces<SlaReport>().ProducesValidationProblem();
        reports.MapGet("/agents", ([AsParameters] ReportQuery q, ZelvoDbContext db, CurrentUser user, IAuditLog audit, TimeProvider time, CancellationToken ct) => GetAsync(ReportKind.Agents, q, new(db, user, audit, time), ct))
            .WithName("GetAgentsReport").WithSummary("Agent performance: per agent, tickets assigned, resolved and open now, median minutes to the first response and hours to resolve, SLA targets met, share reopened and happiness. Team leads see the agents of the groups they lead, other staff only themselves. Audited." + FilterHelp)
            .Produces<AgentsReport>().ProducesValidationProblem();
        reports.MapGet("/happiness", ([AsParameters] ReportQuery q, ZelvoDbContext db, CurrentUser user, IAuditLog audit, TimeProvider time, CancellationToken ct) => GetAsync(ReportKind.Happiness, q, new(db, user, audit, time), ct))
            .WithName("GetHappinessReport").WithSummary("Happiness meter ratings given in the period: average as a percentage of the scale (worst 0 %, best 100 %), scores, per record type, channel, group and service, and over time, with the tenant's target. General portal ratings only for reports.all. Audited." + FilterHelp)
            .Produces<HappinessReport>().ProducesValidationProblem();
        reports.MapGet("/{kind}/table", TableAsync).WithName("ExportReportTable")
            .WithSummary("The dashboard's main table as CSV (UTF-8 with BOM) or Excel (format=csv|xlsx; csv by default); cells that look like formulas are neutralised. Audited." + FilterHelp)
            .Produces<string>(contentType: "text/csv");
        reports.MapPost("/{kind}/exports", ExportAsync).WithName("ExportReportPdf").RequirePerson()
            .WithSummary("Asks for a branded PDF of the dashboard in en or ar (language; the caller's by default) with the filter in the query. Built by the worker with the caller's access; poll GET /exports/{id}. Audited." + FilterHelp)
            .Produces<ExportResponse>(StatusCodes.Status202Accepted).ProducesValidationProblem();

        var settings = group.MapGroup("/settings/reports").WithTags("Settings").RequireTenant();
        settings.MapGet("/", TargetsAsync).WithName("GetReportSettings")
            .WithSummary("Reporting targets: SLA targets that should be met and happiness that should be reached, in percent. Anyone who sees reports.")
            .RequireAuthorization().Produces<ReportTargets>();
        settings.MapPut("/", UpdateTargetsAsync).WithName("UpdateReportSettings")
            .WithSummary("Change the reporting targets (1–100 % each). Needs reports.manage; audited.")
            .RequirePermission(Permissions.ReportsManage).Produces<ReportTargets>().ProducesValidationProblem();
        return group;
    }

    /// <summary>The services a dashboard needs.</summary>
    internal sealed record ReportContext(ZelvoDbContext Db, CurrentUser User, IAuditLog Audit, TimeProvider Time);

    internal static async Task<IResult> ScopeAsync(ZelvoDbContext db, CurrentUser user, CancellationToken cancellationToken)
    {
        var scope = await CallerScopeAsync(db, user, cancellationToken);
        if (scope is null)
        {
            return NoReports();
        }

        var groupIds = scope.Groups?.ToList() ?? [];
        var groups = await db.SupportGroups.AsNoTracking().Where(g => groupIds.Contains(g.Id)).OrderBy(g => g.Name.En)
            .Select(g => new ReportGroupResponse(g.Id, ReportName.From(g.Name))).ToListAsync(cancellationToken);
        var mode = scope.OwnFiguresOnly ? "Own" : scope.Groups is null ? "All" : "Groups";
        return TypedResults.Ok(new ReportScopeResponse(mode, [.. scope.Types.Order()], groups,
            user.Has(Permissions.ReportsManage), user.Has(Permissions.ReportsRead) || user.Has(Permissions.ReportsAll)));
    }

    internal static async Task<IResult> GetAsync(ReportKind kind, ReportQuery query, ReportContext context, CancellationToken cancellationToken)
    {
        var (filter, problem) = await query.ToFilterAsync(context.Db, context.Time, cancellationToken);
        if (problem is not null)
        {
            return problem;
        }

        var scope = await CallerScopeAsync(context.Db, context.User, cancellationToken);
        if (scope is null)
        {
            return NoReports();
        }

        var report = await new ReportRenderer(context.Db, context.Time).BuildAsync(new ReportRequest(kind, filter!), scope, cancellationToken);
        await context.Audit.RecordAsync(AuditCategory.View, "report.viewed", "report", kind.ToString(), Details(filter!, scope), cancellationToken);
        return TypedResults.Ok(report);
    }

    internal static async Task<IResult> TableAsync(
        string kind, string? format, [AsParameters] ReportQuery query, ZelvoDbContext db, CurrentUser user, IAuditLog audit, TimeProvider time, CancellationToken cancellationToken)
    {
        if (!Enum.TryParse<ReportKind>(kind, ignoreCase: true, out var reportKind))
        {
            return ApiProblem.Create(StatusCodes.Status404NotFound, "report-not-found", "There is no such report.");
        }

        var tabular = string.Equals(format, "xlsx", StringComparison.OrdinalIgnoreCase) ? TabularFormat.Xlsx : TabularFormat.Csv;
        var (filter, problem) = await query.ToFilterAsync(db, time, cancellationToken);
        if (problem is not null)
        {
            return problem;
        }

        var scope = await CallerScopeAsync(db, user, cancellationToken);
        if (scope is null)
        {
            return NoReports();
        }

        var report = await new ReportRenderer(db, time).BuildAsync(new ReportRequest(reportKind, filter!), scope, cancellationToken);
        var (header, rows) = ReportTables.Of(report);
        await audit.RecordAsync(AuditCategory.View, "report.exported", "report", reportKind.ToString(),
            new Dictionary<string, object?>(Details(filter!, scope)) { ["format"] = tabular.ToString() }, cancellationToken);
        var name = string.Create(CultureInfo.InvariantCulture, $"{reportKind.ToString().ToLowerInvariant()}-{filter!.From:yyyy-MM-dd}-{filter.To:yyyy-MM-dd}.{TabularFile.Extension(tabular)}");
        return TypedResults.File(TabularFile.Write(tabular, header, rows), TabularFile.ContentType(tabular), name);
    }

    internal static async Task<IResult> ExportAsync(
        string kind, string? language, [AsParameters] ReportQuery query, ZelvoDbContext db, CurrentUser user, IAuditLog audit, TimeProvider time, CancellationToken cancellationToken)
    {
        if (!Enum.TryParse<ReportKind>(kind, ignoreCase: true, out var reportKind))
        {
            return ApiProblem.Create(StatusCodes.Status404NotFound, "report-not-found", "There is no such report.");
        }

        var (filter, problem) = await query.ToFilterAsync(db, time, cancellationToken);
        if (problem is not null)
        {
            return problem;
        }

        var scope = await CallerScopeAsync(db, user, cancellationToken);
        if (scope is null)
        {
            return NoReports();
        }

        var me = user.Account!;
        if (await db.DocumentExports.CountAsync(e => e.RequestedById == me.Id && e.Status == ExportStatus.Pending, cancellationToken) >= 5)
        {
            return ApiProblem.Create(StatusCodes.Status429TooManyRequests, "too-many-exports", "Wait for your other exports to finish.");
        }

        var lang = language is LocalizedText.English or LocalizedText.Arabic ? language
            : me.PreferredLanguage is LocalizedText.English or LocalizedText.Arabic ? me.PreferredLanguage : LocalizedText.English;
        var export = new DocumentExport
        {
            Kind = ExportKind.Report, Parameters = new ReportRequest(reportKind, filter!).ToJson(), Language = lang, StaffView = true, RequestedById = me.Id,
        };
        db.DocumentExports.Add(export);
        audit.Add(AuditCategory.View, "export.requested", "report", reportKind.ToString(),
            new Dictionary<string, object?>(Details(filter!, scope)) { ["exportId"] = export.Id.ToString(), ["language"] = lang });
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Accepted($"/api/v1/exports/{export.Id}", ExportResponse.From(export));
    }

    internal static async Task<Ok<ReportTargets>> TargetsAsync(TenantSettingsStore store, CancellationToken cancellationToken) =>
        TypedResults.Ok(await store.GetAsync(Targets, cancellationToken));

    internal static async Task<Results<Ok<ReportTargets>, ValidationProblem>> UpdateTargetsAsync(
        ReportTargetsRequest request, TenantSettingsStore store, ZelvoDbContext db, CancellationToken cancellationToken)
    {
        var current = await store.GetAsync(Targets, cancellationToken);
        var targets = new ReportTargets(request.SlaTargetPercent ?? current.SlaTargetPercent, request.HappinessTargetPercent ?? current.HappinessTargetPercent);
        if (!targets.IsValid())
        {
            return UserEndpoints.Validation("slaTargetPercent", "Both targets are between 1 and 100 %.");
        }

        await store.SetAsync(Targets, targets, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Ok(targets);
    }

    /// <summary>The reporting targets setting.</summary>
    public static SettingDefinition<ReportTargets> Targets { get; } = new(ReportTargets.Key, ReportTargets.Default, Permissions.ReportsManage, t => t.IsValid());

    /// <summary>
    /// The caller's scope. People: <see cref="ReportAccess"/> with the permissions that apply now.
    /// Integrations need <c>reports.all</c> (they have no groups or figures of their own).
    /// </summary>
    internal static Task<ReportScope?> CallerScopeAsync(ZelvoDbContext db, CurrentUser user, CancellationToken cancellationToken)
    {
        if (user.Account is { } account)
        {
            return ReportAccess.ScopeAsync(db, account.Id, user.Permissions, cancellationToken);
        }

        return Task.FromResult(user.ApiClient is not null && user.Has(Permissions.ReportsAll)
            ? new ReportScope(ReportPermissions.TypesFor(user.Permissions), null, null, true)
            : null);
    }

    internal static IReadOnlyDictionary<string, object?> Details(ReportFilter filter, ReportScope scope) =>
        new Dictionary<string, object?>(filter.AuditDetails()) { ["scope"] = scope.OwnFiguresOnly ? "own" : scope.Groups is null ? "all" : "groups" };

    private static IResult NoReports() =>
        ApiProblem.Create(StatusCodes.Status403Forbidden, "forbidden", "You cannot see reports: they need permission to read incidents, service requests or customer voice.");
}

/// <summary>Query parameters of a dashboard.</summary>
internal sealed class ReportQuery
{
    /// <summary>First day.</summary>
    public DateOnly? From { get; init; }

    /// <summary>Last day.</summary>
    public DateOnly? To { get; init; }

    /// <summary>Comma-separated record types.</summary>
    public string? Types { get; init; }

    /// <summary>Comma-separated group ids.</summary>
    public string? Groups { get; init; }

    /// <summary>Comma-separated category ids.</summary>
    public string? Categories { get; init; }

    /// <summary>Day, Week or Month.</summary>
    public ReportInterval? Interval { get; init; }

    /// <summary>The filter, or a validation problem. The default period is the last 30 days in the tenant's time zone.</summary>
    public async Task<(ReportFilter? Filter, IResult? Problem)> ToFilterAsync(ZelvoDbContext db, TimeProvider time, CancellationToken cancellationToken)
    {
        var zone = await new ReportBuilder(db).TimeZoneAsync(cancellationToken);
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(time.GetUtcNow(), zone).DateTime);
        var to = To ?? today;
        var from = From ?? to.AddDays(-29);
        var types = new List<TicketType>();
        foreach (var part in Split(Types))
        {
            if (!Enum.TryParse<TicketType>(part, ignoreCase: false, out var type) || !Enum.IsDefined(type))
            {
                return (null, UserEndpoints.Validation("types", "Use Incident, ServiceRequest, Complaint, Inquiry or Suggestion."));
            }

            types.Add(type);
        }

        if (!Ids(Groups, out var groups) || !Ids(Categories, out var categories))
        {
            return (null, UserEndpoints.Validation("groups", "Groups and categories are comma-separated ids."));
        }

        var filter = new ReportFilter(from, to, [.. types.Distinct()], groups, categories, Interval);
        return filter.Problem() is { } problem ? (null, UserEndpoints.Validation("from", problem)) : (filter, null);
    }

    private static string[] Split(string? value) =>
        (value ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static bool Ids(string? value, out List<Guid> ids)
    {
        ids = [];
        foreach (var part in Split(value))
        {
            if (!Guid.TryParse(part, out var id))
            {
                return false;
            }

            ids.Add(id);
        }

        ids = [.. ids.Distinct()];
        return true;
    }
}

/// <summary>Reporting targets to change; leave one out to keep it.</summary>
internal sealed record ReportTargetsRequest(double? SlaTargetPercent, double? HappinessTargetPercent);

/// <summary>A group the caller's reports cover.</summary>
internal sealed record ReportGroupResponse(Guid Id, ReportName Name);

/// <summary>What the caller sees in reports.</summary>
/// <param name="Mode"><c>All</c>, <c>Groups</c> (the groups they lead) or <c>Own</c> (their own figures).</param>
/// <param name="Types">Record types.</param>
/// <param name="Groups">The groups, for <c>Groups</c>.</param>
/// <param name="CanManageSchedules">May schedule reports for other people.</param>
/// <param name="CanSchedule">May schedule reports for themselves.</param>
internal sealed record ReportScopeResponse(string Mode, IReadOnlyList<TicketType> Types, IReadOnlyList<ReportGroupResponse> Groups, bool CanManageSchedules, bool CanSchedule);

/// <summary>The main table of each dashboard, for CSV and Excel.</summary>
internal static class ReportTables
{
    public static (IReadOnlyList<string> Header, IEnumerable<IReadOnlyList<string?>> Rows) Of(object report) => report switch
    {
        OverviewReport o => (
            ["from", "to", "created", "resolved", "open", "responsePercent", "resolutionPercent", "ratings", "happinessPercent", "medianResponseMinutes"],
            [[D(o.From), D(o.To), N(o.Created), N(o.Resolved), N(o.Open), P(o.ResponsePercent), P(o.ResolutionPercent), N(o.Ratings), P(o.HappinessPercent), P(o.MedianResponseMinutes)]]),
        VolumesReport v => (["period", "created", "resolved", "closed"], v.Series.Select(p => (IReadOnlyList<string?>)[D(p.Bucket), N(p.Created), N(p.Resolved), N(p.Closed)])),
        BacklogReport b => (["period", "open"], b.Trend.Select(p => (IReadOnlyList<string?>)[D(p.Bucket), N(p.Open)])),
        SlaReport s => (["period", "responsePercent", "resolutionPercent"], s.Trend.Select(p => (IReadOnlyList<string?>)[D(p.Bucket), P(p.ResponsePercent), P(p.ResolutionPercent)])),
        AgentsReport a => (
            ["agent", "assigned", "resolved", "openNow", "medianResponseMinutes", "medianResolutionHours", "slaPercent", "reopenPercent", "ratings", "happinessPercent"],
            a.Agents.Select(r => (IReadOnlyList<string?>)[r.Name, N(r.Assigned), N(r.Resolved), N(r.OpenNow), P(r.MedianResponseMinutes), P(r.MedianResolutionHours), P(r.SlaPercent), P(r.ReopenPercent), N(r.Ratings), P(r.HappinessPercent)])),
        HappinessReport h => (["period", "ratings", "happinessPercent"], h.Trend.Select(p => (IReadOnlyList<string?>)[D(p.Bucket), N(p.Ratings), P(p.HappinessPercent)])),
        _ => throw new ArgumentException("Unknown report.", nameof(report)),
    };

    private static string D(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string N(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string? P(double? value) => value?.ToString("0.0", CultureInfo.InvariantCulture);
}
