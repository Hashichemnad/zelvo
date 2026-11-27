using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;
using Zelvo.Api.Conventions;
using Zelvo.Api.Forms;
using Zelvo.Api.Identity;
using Zelvo.Api.Settings;
using Zelvo.Api.Tenancy;
using Zelvo.Data;
using Zelvo.Data.Auditing;
using Zelvo.Data.Identity;
using Zelvo.Data.Tickets;
using Zelvo.Data.Wellbeing;
using Zelvo.Notifications;
using Zelvo.Tickets;

namespace Zelvo.Api.Wellbeing;

/// <summary>
/// Agent wellbeing (module 21, task K4; product owner decisions 2026-11-26). For workload
/// balancing, <b>not for ranking</b>: people are listed by name only, with no ordering by figures,
/// no league tables, no export and no scheduled reports, and the figures are not part of F2's agent
/// performance. Off by default; an administrator turns it on, confirming that staff were told
/// (UAE PDPL), and every agent is notified. Team leads see each agent of the groups they lead;
/// others with <c>wellbeing.read</c> (executives, administrators) see group totals only; every agent
/// sees their own figures. Every view of a person's figures is audited.
/// </summary>
internal static class WellbeingEndpoints
{
    public static RouteGroupBuilder MapWellbeingEndpoints(this RouteGroupBuilder group)
    {
        var wellbeing = group.MapGroup("/wellbeing").WithTags("Wellbeing").RequireTenant();
        wellbeing.MapGet("/settings", GetSettingsAsync).WithName("GetWellbeingSettings")
            .WithSummary("Whether wellbeing is on, and the default thresholds. Needs wellbeing.read or wellbeing.manage.").Produces<WellbeingSettingsResponse>();
        wellbeing.MapPut("/settings", PutSettingsAsync).WithName("SetWellbeingSettings").RequirePermission(Permissions.WellbeingManage)
            .WithSummary("Turn wellbeing on (staffInformed must be true; every agent is notified) or off (all figures and alerts are removed), and set the default thresholds. Audited.")
            .Produces<WellbeingSettingsResponse>().ProducesValidationProblem();
        wellbeing.MapGet("/groups", ListGroupsAsync).WithName("ListWellbeingGroups").RequirePermission(Permissions.WellbeingRead)
            .WithSummary("Active groups with their totals over the period (days 7, 28 or 90); 'led' marks the groups whose agents the caller sees one by one.")
            .Produces<IReadOnlyList<WellbeingGroupSummary>>();
        wellbeing.MapGet("/groups/{id:guid}", GetGroupAsync).WithName("GetWellbeingGroup").RequirePermission(Permissions.WellbeingRead)
            .WithSummary("A group's totals, thresholds and open alerts; for its team leads also each agent's figures, listed by name. Every agent shown is audited as a view.")
            .Produces<WellbeingGroupDetail>();
        wellbeing.MapPut("/groups/{id:guid}/thresholds", PutThresholdsAsync).WithName("SetWellbeingThresholds").RequirePermission(Permissions.WellbeingRead)
            .WithSummary("Set a group's thresholds (empty values take the defaults). For the group's team leads and wellbeing.manage. Audited.")
            .Produces<WellbeingThresholdsResponse>().ProducesValidationProblem();
        wellbeing.MapGet("/groups/{id:guid}/agents/{userId:guid}/suggestions", SuggestAsync).WithName("SuggestWellbeingReassignment").RequirePermission(Permissions.WellbeingRead)
            .WithSummary("For the group's team leads: the agent's open tickets in the group that have not been started, each with available, qualified members to give it to (lightest work first). Nothing changes until the team lead reassigns.")
            .Produces<IReadOnlyList<ReassignmentSuggestion>>();
        wellbeing.MapPost("/reassign", ReassignAsync).WithName("WellbeingReassign").RequirePermission(Permissions.WellbeingRead)
            .WithSummary("For the group's team leads: give one of an agent's tickets to another available member of the group. Never automatic. Audited.")
            .Produces<WellbeingReassigned>().ProducesValidationProblem();

        group.MapGet("/me/wellbeing", MineAsync).WithTags("Wellbeing").RequireTenant().WithName("GetMyWellbeing")
            .WithSummary("The caller's own wellbeing figures (Account → My workload) and the alerts about them, per group they belong to.")
            .Produces<MyWellbeing>();
        return group;
    }

    // ---- settings ----

    internal static async Task<IResult> GetSettingsAsync(CurrentUser user, TenantSettingsStore settings, CancellationToken cancellationToken)
    {
        if (!user.Has(Permissions.WellbeingRead) && !user.Has(Permissions.WellbeingManage))
        {
            return ApiProblem.Create(StatusCodes.Status403Forbidden, "forbidden", "You may not see wellbeing settings.");
        }

        return TypedResults.Ok(await SettingsResponseAsync(settings, cancellationToken));
    }

    internal static async Task<IResult> PutSettingsAsync(
        WellbeingSettingsRequest request, ZelvoDbContext db, CurrentUser user, TenantSettingsStore settings, PendingNotifications pending, IAuditLog audit,
        TimeProvider time, CancellationToken cancellationToken)
    {
        var defaults = request.Defaults is { } d
            ? new WellbeingThresholds(d.MaxWeightedLoad ?? 0, d.MaxNewPerDay ?? 0, d.MaxAfterHoursHours ?? -1, d.MaxDifficultPercent ?? 0)
            : await settings.GetAsync(WellbeingSettings.Defaults, cancellationToken);
        if (!defaults.IsValid())
        {
            return UserEndpoints.Validation("defaults", "Weighted load 1–1000, new per day 1–500, after-hours hours 0–168 and the difficult share 1–100 %.");
        }

        var state = await settings.GetAsync(WellbeingSettings.State, cancellationToken);
        var enable = request.Enabled!.Value;
        var now = time.GetUtcNow();
        if (enable && !state.Enabled && request.StaffInformed != true)
        {
            return UserEndpoints.Validation("staffInformed", "Confirm that staff were told what is recorded and why before turning wellbeing on.");
        }

        await settings.SetAsync(WellbeingSettings.Defaults, defaults, cancellationToken);
        if (enable && !state.Enabled)
        {
            await settings.SetAsync(WellbeingSettings.State, new WellbeingState(true, now, user.Account?.Id, true), cancellationToken);
            // Every agent is told, in the app and by email (decided 2026-11-26).
            var agents = await db.SupportGroupMembers.AsNoTracking().Where(m => m.User!.Status == UserStatus.Active && m.User.ErasedAt == null)
                .Select(m => m.UserId).Distinct().ToListAsync(cancellationToken);
            if (agents.Count > 0)
            {
                pending.Add(new NotificationRequest(NotificationEvents.WellbeingEnabled, agents, new Dictionary<string, PlaceholderValue>(StringComparer.Ordinal), "/account/workload"));
            }

            audit.Add(AuditCategory.Admin, "wellbeing.enabled", "wellbeing", null, new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["staffInformed"] = true,
                ["agentsNotified"] = agents.Count,
            });
        }
        else if (!enable && state.Enabled)
        {
            // Off: what was recorded is no longer needed for its purpose, so it goes (UAE PDPL).
            await settings.SetAsync(WellbeingSettings.State, WellbeingState.Off, cancellationToken);
            var days = await db.WellbeingDays.ExecuteDeleteAsync(cancellationToken);
            var alerts = await db.WellbeingAlerts.ExecuteDeleteAsync(cancellationToken);
            audit.Add(AuditCategory.Admin, "wellbeing.disabled", "wellbeing", null, new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["daysRemoved"] = days,
                ["alertsRemoved"] = alerts,
            });
        }

        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Ok(await SettingsResponseAsync(settings, cancellationToken));
    }

    private static async Task<WellbeingSettingsResponse> SettingsResponseAsync(TenantSettingsStore settings, CancellationToken cancellationToken)
    {
        var state = await settings.GetAsync(WellbeingSettings.State, cancellationToken);
        return new WellbeingSettingsResponse(state.Enabled, state.EnabledAt, state.StaffInformed,
            WellbeingThresholdsResponse.From(await settings.GetAsync(WellbeingSettings.Defaults, cancellationToken)));
    }

    // ---- dashboard ----

    internal static async Task<IResult> ListGroupsAsync(
        int? days, ZelvoDbContext db, CurrentUser user, TenantSettingsStore settings, WellbeingCalculator calculator, TimeProvider time, CancellationToken cancellationToken)
    {
        if (await OffAsync(settings, cancellationToken) is { } off)
        {
            return off;
        }

        var period = Period(days);
        var (today, _) = await calculator.TodayAsync(time.GetUtcNow(), cancellationToken);
        var led = await LedAsync(db, user, cancellationToken);
        var groups = await db.SupportGroups.AsNoTracking().Where(g => g.Active).OrderBy(g => g.Name.En).ThenBy(g => g.Id).ToListAsync(cancellationToken);
        var members = await MembersAsync(db, [.. groups.Select(g => g.Id)], cancellationToken);
        var figures = await calculator.DaysAsync([.. members.Select(m => m.UserId).Distinct()], today, period, cancellationToken);
        var thresholds = await calculator.ThresholdsAsync(cancellationToken);
        var openAlerts = await db.WellbeingAlerts.AsNoTracking().Where(a => a.EndedAt == null).GroupBy(a => a.GroupId)
            .Select(g => new { g.Key, Count = g.Select(a => a.UserId).Distinct().Count() }).ToDictionaryAsync(g => g.Key, g => g.Count, cancellationToken);
        return TypedResults.Ok(groups.Select(g => new WellbeingGroupSummary(
            g.Id, LocalizedTextDto.From(g.Name), led.Contains(g.Id), WellbeingThresholdsResponse.From(thresholds(g.Id)),
            Totals(members.Where(m => m.GroupId == g.Id).Select(m => figures.GetValueOrDefault(m.UserId) ?? []).ToList(), today, period),
            openAlerts.GetValueOrDefault(g.Id))).ToList());
    }

    internal static async Task<IResult> GetGroupAsync(
        Guid id, int? days, ZelvoDbContext db, CurrentUser user, TenantSettingsStore settings, WellbeingCalculator calculator, IAuditLog audit, TimeProvider time,
        CancellationToken cancellationToken)
    {
        if (await OffAsync(settings, cancellationToken) is { } off)
        {
            return off;
        }

        var group = await db.SupportGroups.AsNoTracking().SingleOrDefaultAsync(g => g.Id == id && g.Active, cancellationToken);
        if (group is null)
        {
            return FormsCommon.NotFound("group");
        }

        var period = Period(days);
        var (today, _) = await calculator.TodayAsync(time.GetUtcNow(), cancellationToken);
        var leads = (await LedAsync(db, user, cancellationToken)).Contains(id);
        var members = await MembersAsync(db, [id], cancellationToken);
        var agentIds = members.Select(m => m.UserId).ToList();
        var figures = await calculator.DaysAsync(agentIds, today, period, cancellationToken);
        var window = await calculator.DaysAsync(agentIds, today, WellbeingCalculator.AlertWindowDays, cancellationToken);
        var limit = (await calculator.ThresholdsAsync(cancellationToken))(id);
        var alerts = await db.WellbeingAlerts.AsNoTracking().Where(a => a.GroupId == id && a.EndedAt == null).ToListAsync(cancellationToken);
        var totals = Totals([.. members.Select(m => figures.GetValueOrDefault(m.UserId) ?? [])], today, period);

        IReadOnlyList<WellbeingAgent>? agents = null;
        if (leads)
        {
            // Listed by name only: never ordered by their figures (not for ranking).
            agents = [.. members.OrderBy(m => m.Name ?? string.Empty, StringComparer.CurrentCulture).ThenBy(m => m.UserId)
                .Select(m => Agent(m, figures.GetValueOrDefault(m.UserId) ?? [], window.GetValueOrDefault(m.UserId) ?? [], alerts.Where(a => a.UserId == m.UserId), today, period))];
            foreach (var member in members)
            {
                audit.Add(AuditCategory.View, "wellbeing.viewed", "user", member.UserId.ToString(), new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["groupId"] = id.ToString(),
                    ["days"] = period,
                });
            }

            await db.SaveChangesAsync(cancellationToken);
        }

        return TypedResults.Ok(new WellbeingGroupDetail(group.Id, LocalizedTextDto.From(group.Name), leads, period, WellbeingThresholdsResponse.From(limit), totals,
            alerts.Select(a => a.UserId).Distinct().Count(), agents));
    }

    internal static async Task<IResult> PutThresholdsAsync(
        Guid id, WellbeingThresholdsRequest request, ZelvoDbContext db, CurrentUser user, TenantSettingsStore settings, WellbeingCalculator calculator,
        CancellationToken cancellationToken)
    {
        if (!await db.SupportGroups.AnyAsync(g => g.Id == id && g.Active, cancellationToken))
        {
            return FormsCommon.NotFound("group");
        }

        if (!user.Has(Permissions.WellbeingManage) && !(await LedAsync(db, user, cancellationToken)).Contains(id))
        {
            return ApiProblem.Create(StatusCodes.Status403Forbidden, "not-team-lead", "Only the group's team leads set its thresholds.");
        }

        if (request.MaxWeightedLoad is < 1 or > 1000 || request.MaxNewPerDay is < 1 or > 500 || request.MaxAfterHoursHours is < 0 or > 168
            || request.MaxDifficultPercent is < 1 or > 100)
        {
            return UserEndpoints.Validation("thresholds", "Weighted load 1–1000, new per day 1–500, after-hours hours 0–168 and the difficult share 1–100 %.");
        }

        var row = await db.WellbeingThresholds.SingleOrDefaultAsync(t => t.GroupId == id, cancellationToken);
        if (row is null)
        {
            row = new WellbeingThreshold { GroupId = id };
            db.WellbeingThresholds.Add(row);
        }

        row.MaxWeightedLoad = request.MaxWeightedLoad;
        row.MaxNewPerDay = request.MaxNewPerDay;
        row.MaxAfterHoursHours = request.MaxAfterHoursHours;
        row.MaxDifficultPercent = request.MaxDifficultPercent;
        row.UpdatedById = user.Account?.Id;
        row.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Ok(WellbeingThresholdsResponse.From((await calculator.ThresholdsAsync(cancellationToken))(id)));
    }

    // ---- suggested reassignment ----

    internal static async Task<IResult> SuggestAsync(
        Guid id, Guid userId, ZelvoDbContext db, CurrentUser user, TenantSettingsStore settings, WellbeingCalculator calculator, TimeProvider time,
        CancellationToken cancellationToken)
    {
        if (await OffAsync(settings, cancellationToken) is { } off)
        {
            return off;
        }

        if (!(await LedAsync(db, user, cancellationToken)).Contains(id))
        {
            return ApiProblem.Create(StatusCodes.Status403Forbidden, "not-team-lead", "Only the group's team leads reassign its work.");
        }

        var (today, _) = await calculator.TodayAsync(time.GetUtcNow(), cancellationToken);
        var tickets = await MovableAsync(db, id, userId).OrderBy(t => t.CreatedAt).Take(10)
            .Select(t => new
            {
                t.Id, t.Number, t.Title, t.Type, t.State, t.RequesterId,
                Priority = t.Incident != null ? (int?)t.Incident.Priority : t.Voice != null ? (int?)t.Voice.Priority : t.Problem != null ? (int?)t.Problem.Priority : null,
            })
            .ToListAsync(cancellationToken);
        var candidates = await CandidatesAsync(db, calculator, id, userId, today, cancellationToken);
        var languages = await db.Users.AsNoTracking().Where(u => tickets.Select(t => t.RequesterId).Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.PreferredLanguage, cancellationToken);
        return TypedResults.Ok(tickets.Select(t =>
        {
            var language = languages.GetValueOrDefault(t.RequesterId);
            var fitting = candidates.Where(c => language is null || c.Languages.Count == 0 || c.Languages.Contains(language)).Take(3)
                .Select(c => new ReassignmentCandidate(c.Id, c.Name, c.WeightedLoad)).ToList();
            return new ReassignmentSuggestion(t.Id, t.Number, t.Title, t.Type, t.State, t.Priority, fitting);
        }).ToList());
    }

    internal static async Task<IResult> ReassignAsync(
        WellbeingReassignRequest request, ZelvoDbContext db, CurrentUser user, TenantSettingsStore settings, WellbeingCalculator calculator, IAuditLog audit,
        TimeProvider time, CancellationToken cancellationToken)
    {
        if (await OffAsync(settings, cancellationToken) is { } off)
        {
            return off;
        }

        var groupId = request.GroupId!.Value;
        if (!(await LedAsync(db, user, cancellationToken)).Contains(groupId))
        {
            return ApiProblem.Create(StatusCodes.Status403Forbidden, "not-team-lead", "Only the group's team leads reassign its work.");
        }

        var ticket = await db.Tickets.Include(t => t.Incident).SingleOrDefaultAsync(t => t.Id == request.TicketId && t.GroupId == groupId, cancellationToken);
        if (ticket is null || ticket.AssigneeId is not { } from || !TicketWorkflow.IsOpen(ticket.State) || ticket.Held || ticket.AwaitingApproval)
        {
            return UserEndpoints.Validation("ticketId", "Choose an open ticket of the group that is assigned to someone.");
        }

        var (today, _) = await calculator.TodayAsync(time.GetUtcNow(), cancellationToken);
        var candidates = await CandidatesAsync(db, calculator, groupId, from, today, cancellationToken);
        if (candidates.All(c => c.Id != request.ToUserId))
        {
            return UserEndpoints.Validation("toUserId", "Choose an available member of the group.");
        }

        var now = time.GetUtcNow();
        TicketWorkflow.Assign(ticket, request.ToUserId, now);
        ticket.UpdatedAt = now;
        audit.Add(AuditCategory.Change, "wellbeing.reassigned", "ticket", ticket.Id.ToString(), new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["groupId"] = groupId.ToString(),
            ["from"] = from.ToString(),
            ["to"] = request.ToUserId!.Value.ToString(),
        });
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Ok(new WellbeingReassigned(ticket.Id, ticket.Number, request.ToUserId.Value));
    }

    /// <summary>The agent's open tickets in the group that nobody started yet (they move most easily).</summary>
    private static IQueryable<Ticket> MovableAsync(ZelvoDbContext db, Guid groupId, Guid agent) =>
        db.Tickets.AsNoTracking().Where(t => t.GroupId == groupId && t.AssigneeId == agent && !t.Held && !t.AwaitingApproval
            && (t.State == TicketState.New || t.State == TicketState.Assigned || t.State == TicketState.Reopened));

    /// <summary>
    /// Members of the group who could take work: active, available for assignment (B2), not the
    /// agent, and not past the group's workload threshold; lightest weighted work first.
    /// </summary>
    private static async Task<List<(Guid Id, string Name, int WeightedLoad, List<string> Languages)>> CandidatesAsync(
        ZelvoDbContext db, WellbeingCalculator calculator, Guid groupId, Guid agent, DateOnly today, CancellationToken cancellationToken)
    {
        var limit = (await calculator.ThresholdsAsync(cancellationToken))(groupId);
        var members = await db.SupportGroupMembers.AsNoTracking()
            .Where(m => m.GroupId == groupId && m.UserId != agent && m.User!.Status == UserStatus.Active && m.User.AvailableForAssignment && m.User.ErasedAt == null)
            .Select(m => new { m.UserId, Name = m.User!.DisplayName ?? m.User.Email ?? string.Empty, m.User.Languages }).ToListAsync(cancellationToken);
        var ids = members.Select(m => m.UserId).ToList();
        var load = (await db.Tickets.AsNoTracking()
                .Where(t => t.AssigneeId != null && ids.Contains(t.AssigneeId.Value) && t.State != TicketState.Resolved && t.State != TicketState.Closed && !t.Held)
                .Select(t => new { Agent = t.AssigneeId!.Value, Priority = t.Incident != null ? (int?)t.Incident.Priority : t.Voice != null ? (int?)t.Voice.Priority : t.Problem != null ? (int?)t.Problem.Priority : null })
                .ToListAsync(cancellationToken))
            .GroupBy(t => t.Agent).ToDictionary(g => g.Key, g => g.Sum(t => WellbeingCalculator.Weight(t.Priority)));
        _ = today;
        return [.. members.Select(m => (m.UserId, m.Name, load.GetValueOrDefault(m.UserId), m.Languages))
            .Where(m => m.Item3 < limit.MaxWeightedLoad).OrderBy(m => m.Item3).ThenBy(m => m.Name, StringComparer.CurrentCulture)];
    }

    // ---- my workload ----

    internal static async Task<IResult> MineAsync(
        int? days, ZelvoDbContext db, CurrentUser user, TenantSettingsStore settings, WellbeingCalculator calculator, TimeProvider time,
        CancellationToken cancellationToken)
    {
        if (user.Account is not { } me)
        {
            return ApiProblem.Create(StatusCodes.Status403Forbidden, "forbidden", "Only people have their own workload.");
        }

        var state = await settings.GetAsync(WellbeingSettings.State, cancellationToken);
        var period = Period(days);
        if (!state.Enabled)
        {
            return TypedResults.Ok(new MyWellbeing(false, null, period, null, []));
        }

        var (today, _) = await calculator.TodayAsync(time.GetUtcNow(), cancellationToken);
        var groups = await db.SupportGroupMembers.AsNoTracking().Where(m => m.UserId == me.Id)
            .Join(db.SupportGroups.AsNoTracking().Where(g => g.Active), m => m.GroupId, g => g.Id, (m, g) => new { g.Id, g.Name })
            .OrderBy(g => g.Name.En).ToListAsync(cancellationToken);
        var figures = (await calculator.DaysAsync([me.Id], today, period, cancellationToken)).GetValueOrDefault(me.Id) ?? [];
        var window = (await calculator.DaysAsync([me.Id], today, WellbeingCalculator.AlertWindowDays, cancellationToken)).GetValueOrDefault(me.Id) ?? [];
        var thresholds = await calculator.ThresholdsAsync(cancellationToken);
        var alerts = await db.WellbeingAlerts.AsNoTracking().Where(a => a.UserId == me.Id && a.EndedAt == null).ToListAsync(cancellationToken);
        var self = Agent(new Member(me.Id, Guid.Empty, me.DisplayName ?? me.Email, me.AvailableForAssignment), figures, window, alerts, today, period);
        return TypedResults.Ok(new MyWellbeing(true, state.EnabledAt, period, self,
            [.. groups.Select(g => new MyWellbeingGroup(g.Id, LocalizedTextDto.From(g.Name), WellbeingThresholdsResponse.From(thresholds(g.Id))))]));
    }

    // ---- helpers ----

    private sealed record Member(Guid UserId, Guid GroupId, string? Name, bool Available);

    private static async Task<IResult?> OffAsync(TenantSettingsStore settings, CancellationToken cancellationToken) =>
        (await settings.GetAsync(WellbeingSettings.State, cancellationToken)).Enabled
            ? null
            : ApiProblem.Create(StatusCodes.Status409Conflict, "wellbeing-off", "Wellbeing is off. An administrator turns it on in Settings → Wellbeing.");

    private static int Period(int? days) => days is { } d && WellbeingSettings.Periods.Contains(d) ? d : 7;

    /// <summary>Groups the caller leads (team leads see their agents one by one).</summary>
    private static async Task<HashSet<Guid>> LedAsync(ZelvoDbContext db, CurrentUser user, CancellationToken cancellationToken) =>
        user.Account is { } me
            ? [.. await db.SupportGroupMembers.AsNoTracking().Where(m => m.UserId == me.Id && m.IsLead).Select(m => m.GroupId).ToListAsync(cancellationToken)]
            : [];

    private static async Task<List<Member>> MembersAsync(ZelvoDbContext db, List<Guid> groups, CancellationToken cancellationToken) =>
        await db.SupportGroupMembers.AsNoTracking()
            .Where(m => groups.Contains(m.GroupId) && m.User!.Status == UserStatus.Active && m.User.ErasedAt == null)
            .Select(m => new Member(m.UserId, m.GroupId, m.User!.DisplayName ?? m.User.Email, m.User.AvailableForAssignment))
            .ToListAsync(cancellationToken);

    private static WellbeingFigures Figures(IReadOnlyList<WellbeingDay> days, DateOnly today, int period)
    {
        var handled = days.Sum(d => d.HandledTickets);
        var difficult = days.Sum(d => d.DifficultTickets);
        var current = days.FirstOrDefault(d => d.Day == today);
        return new WellbeingFigures(
            current?.OpenAssigned ?? 0, current?.WeightedLoad ?? 0,
            Math.Round(days.Sum(d => d.NewAssignments) / (decimal)period, 1),
            days.Sum(d => d.AfterHoursHours), days.Sum(d => d.AfterHoursActions), handled, difficult,
            handled == 0 ? null : (int)Math.Round(100m * difficult / handled, MidpointRounding.AwayFromZero));
    }

    private static WellbeingTotals Totals(List<List<WellbeingDay>> agents, DateOnly today, int period)
    {
        var each = agents.Select(a => Figures(a, today, period)).ToList();
        var handled = each.Sum(f => f.HandledTickets);
        var difficult = each.Sum(f => f.DifficultTickets);
        return new WellbeingTotals(agents.Count, each.Sum(f => f.OpenAssigned), each.Sum(f => f.WeightedLoad),
            agents.Count == 0 ? 0 : Math.Round(each.Sum(f => f.NewPerDay) / agents.Count, 1), each.Sum(f => f.AfterHoursHours),
            handled == 0 ? null : (int)Math.Round(100m * difficult / handled, MidpointRounding.AwayFromZero));
    }

    private static WellbeingAgent Agent(Member member, List<WellbeingDay> days, List<WellbeingDay> window, IEnumerable<WellbeingAlert> alerts, DateOnly today, int period) =>
        new(member.UserId, member.Name ?? string.Empty, member.Available, Figures(days, today, period),
            [.. alerts.OrderBy(a => a.Kind).Select(a => new WellbeingAlertResponse(a.Id, a.GroupId, a.Kind, a.Value, a.Threshold, a.RaisedAt))],
            [.. days.Select(d => new WellbeingDayResponse(d.Day, d.OpenAssigned, d.WeightedLoad, d.NewAssignments, d.AfterHoursHours, d.HandledTickets, d.DifficultTickets))],
            WellbeingCalculator.AlertValues(window, today).NewPerDay);
}

/// <summary>Turning wellbeing on or off, and the default thresholds.</summary>
internal sealed class WellbeingSettingsRequest
{
    /// <summary>On or off.</summary>
    [Required]
    public bool? Enabled { get; init; }

    /// <summary>Needed to turn it on: staff were told what is recorded and why (UAE PDPL).</summary>
    public bool? StaffInformed { get; init; }

    /// <summary>Default thresholds (all four).</summary>
    public WellbeingThresholdsRequest? Defaults { get; init; }
}

/// <summary>Thresholds; empty values of a group take the tenant's defaults.</summary>
internal sealed class WellbeingThresholdsRequest
{
    /// <summary>Most weighted open work per agent (1–1000).</summary>
    public int? MaxWeightedLoad { get; init; }

    /// <summary>Most new assignments per day on average over 7 days (1–500).</summary>
    public int? MaxNewPerDay { get; init; }

    /// <summary>Most after-hours hours in 7 days (0–168).</summary>
    public int? MaxAfterHoursHours { get; init; }

    /// <summary>Highest share of angry or escalated tickets over 7 days, in percent (1–100).</summary>
    public int? MaxDifficultPercent { get; init; }
}

/// <summary>Give one ticket to another member.</summary>
internal sealed class WellbeingReassignRequest
{
    /// <summary>The group the team lead leads.</summary>
    [Required]
    public Guid? GroupId { get; init; }

    /// <summary>The ticket.</summary>
    [Required]
    public Guid? TicketId { get; init; }

    /// <summary>The member to give it to.</summary>
    [Required]
    public Guid? ToUserId { get; init; }
}

/// <summary>Wellbeing settings.</summary>
internal sealed record WellbeingSettingsResponse(bool Enabled, DateTimeOffset? EnabledAt, bool StaffInformed, WellbeingThresholdsResponse Defaults);

/// <summary>Thresholds in force.</summary>
internal sealed record WellbeingThresholdsResponse(int MaxWeightedLoad, int MaxNewPerDay, int MaxAfterHoursHours, int MaxDifficultPercent)
{
    public static WellbeingThresholdsResponse From(WellbeingThresholds t) => new(t.MaxWeightedLoad, t.MaxNewPerDay, t.MaxAfterHoursHours, t.MaxDifficultPercent);
}

/// <summary>An agent's figures: open work today, and over the period the new assignments per day, after-hours work and the difficult share.</summary>
internal sealed record WellbeingFigures(
    int OpenAssigned, int WeightedLoad, decimal NewPerDay, int AfterHoursHours, int AfterHoursActions, int HandledTickets, int DifficultTickets, int? DifficultPercent);

/// <summary>A group's totals: agents, open work, new assignments per agent and day, after-hours hours and the difficult share.</summary>
internal sealed record WellbeingTotals(int Agents, int OpenAssigned, int WeightedLoad, decimal NewPerAgentPerDay, int AfterHoursHours, int? DifficultPercent);

/// <summary>A group on the dashboard.</summary>
internal sealed record WellbeingGroupSummary(Guid Id, LocalizedTextDto Name, bool Led, WellbeingThresholdsResponse Thresholds, WellbeingTotals Totals, int AgentsWithAlerts);

/// <summary>A group with, for its team leads, each agent by name.</summary>
internal sealed record WellbeingGroupDetail(
    Guid Id, LocalizedTextDto Name, bool Led, int Days, WellbeingThresholdsResponse Thresholds, WellbeingTotals Totals, int AgentsWithAlerts,
    IReadOnlyList<WellbeingAgent>? Agents);

/// <summary>One agent's figures, open alerts and days.</summary>
internal sealed record WellbeingAgent(
    Guid Id, string Name, bool Available, WellbeingFigures Figures, IReadOnlyList<WellbeingAlertResponse> Alerts, IReadOnlyList<WellbeingDayResponse> Days,
    int NewPerDayThisWeek);

/// <summary>An open alert.</summary>
internal sealed record WellbeingAlertResponse(Guid Id, Guid GroupId, WellbeingAlertKind Kind, int Value, int Threshold, DateTimeOffset RaisedAt);

/// <summary>One day's figures.</summary>
internal sealed record WellbeingDayResponse(DateOnly Day, int OpenAssigned, int WeightedLoad, int NewAssignments, int AfterHoursHours, int HandledTickets, int DifficultTickets);

/// <summary>A ticket that could move, with members who could take it.</summary>
internal sealed record ReassignmentSuggestion(Guid TicketId, string Number, string Title, TicketType Type, TicketState State, int? Priority, IReadOnlyList<ReassignmentCandidate> Candidates);

/// <summary>A ticket given to another member.</summary>
internal sealed record WellbeingReassigned(Guid TicketId, string Number, Guid AssigneeId);

/// <summary>A member who could take a ticket.</summary>
internal sealed record ReassignmentCandidate(Guid Id, string Name, int WeightedLoad);

/// <summary>The caller's own figures.</summary>
internal sealed record MyWellbeing(bool Enabled, DateTimeOffset? EnabledAt, int Days, WellbeingAgent? Figures, IReadOnlyList<MyWellbeingGroup> Groups);

/// <summary>A group the caller belongs to, with its thresholds.</summary>
internal sealed record MyWellbeingGroup(Guid Id, LocalizedTextDto Name, WellbeingThresholdsResponse Thresholds);
