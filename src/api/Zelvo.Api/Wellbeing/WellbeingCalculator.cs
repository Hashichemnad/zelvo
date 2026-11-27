using Microsoft.EntityFrameworkCore;
using Zelvo.Api.Settings;
using Zelvo.Data;
using Zelvo.Data.Identity;
using Zelvo.Data.Tickets;
using Zelvo.Data.Wellbeing;
using Zelvo.Tickets.Reports;
using Zelvo.Tickets.Sla;

namespace Zelvo.Api.Wellbeing;

/// <summary>
/// Works out agents' daily wellbeing figures and alerts (module 21, task K4) from what Zelvo keeps
/// already, on the tenant's connection (row-level security applies):
/// <list type="bullet">
/// <item>workload: open tickets assigned to the agent, weighted by priority, and new assignments
/// (the audit log's assignee changes);</item>
/// <item>after-hours work: the agent's own changes in the audit log (replies and notes, ticket and
/// incident changes, work on tasks) outside the working time of their group's business calendar
/// (else the tenant default), counted as changes and as clock hours with a change; never presence
/// or keystrokes;</item>
/// <item>angry or escalated tickets: tickets the agent worked on that day with a frustrated
/// requester (E5), an SLA alert (B3), a major incident or a complaint of the highest severity (C6).</item>
/// </list>
/// Only daily figures are stored, kept <see cref="WellbeingDay.KeepDays"/> days.
/// </summary>
internal sealed class WellbeingCalculator(ZelvoDbContext db, TenantSettingsStore settings)
{
    /// <summary>Days looked at for alerts on new assignments, after-hours work and difficult tickets.</summary>
    public const int AlertWindowDays = 7;

    /// <summary>Fewest tickets handled in the window before the difficult share can raise an alert.</summary>
    public const int MinHandledForDifficult = 5;

    private static readonly string[] ActivityEntities = ["ticket", "incident", "ticket_comment", "request_task"];

    /// <summary>The weight of an open ticket by priority (P1 5 … P5 1; none 3).</summary>
    public static int Weight(int? priority) => priority is >= 1 and <= 5 ? 6 - priority.Value : 3;

    /// <summary>Today in the tenant's time zone.</summary>
    public async Task<(DateOnly Today, TimeZoneInfo Zone)> TodayAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var zone = await new ReportBuilder(db).TimeZoneAsync(cancellationToken);
        return (DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime), zone);
    }

    /// <summary>
    /// Works out yesterday's and today's figures for every active agent (members of support
    /// groups), raises and ends alerts, and removes figures older than 90 days. Returns how many
    /// agents were looked at.
    /// </summary>
    public async Task<int> RunAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var (today, zone) = await TodayAsync(now, cancellationToken);
        var members = await db.SupportGroupMembers.AsNoTracking()
            .Where(m => m.User!.Status == UserStatus.Active && m.User.ErasedAt == null)
            .Join(db.SupportGroups.AsNoTracking().Where(g => g.Active), m => m.GroupId, g => g.Id, (m, g) => new { m.UserId, m.GroupId, GroupName = g.Name.En, g.CalendarId })
            .ToListAsync(cancellationToken);
        var agents = members.Select(m => m.UserId).Distinct().ToList();

        var calendars = (await db.BusinessCalendars.AsNoTracking().Include(c => c.Periods).Include(c => c.Holidays).AsSplitQuery().ToListAsync(cancellationToken))
            .ToDictionary(c => c.Id, c => (Calendar: WorkingCalendar.From(c), c.IsDefault));
        var fallback = calendars.Values.FirstOrDefault(c => c.IsDefault).Calendar ?? WorkingCalendar.AllDay;
        var calendarOf = members.GroupBy(m => m.UserId).ToDictionary(g => g.Key, g =>
            g.OrderBy(m => m.GroupName, StringComparer.Ordinal).Select(m => m.CalendarId).FirstOrDefault(c => c is not null && calendars.ContainsKey(c.Value)) is { } id
                ? calendars[id].Calendar
                : fallback);

        foreach (var day in new[] { today.AddDays(-1), today })
        {
            await ComputeDayAsync(day, day == today, zone, agents, calendarOf, now, cancellationToken);
        }

        await AlertAsync(today, now, members.Select(m => (m.UserId, m.GroupId)).ToList(), cancellationToken);

        var oldest = today.AddDays(-WellbeingDay.KeepDays);
        await db.WellbeingDays.Where(d => d.Day < oldest).ExecuteDeleteAsync(cancellationToken);
        var cutoff = now.AddDays(-WellbeingDay.KeepDays);
        await db.WellbeingAlerts.Where(a => a.RaisedAt < cutoff && (a.EndedAt == null || a.EndedAt < cutoff)).ExecuteDeleteAsync(cancellationToken);
        return agents.Count;
    }

    private async Task ComputeDayAsync(
        DateOnly day, bool isToday, TimeZoneInfo zone, List<Guid> agents, Dictionary<Guid, WorkingCalendar> calendarOf, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var from = new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), zone.GetUtcOffset(day.ToDateTime(TimeOnly.MinValue))).ToUniversalTime();
        var to = new DateTimeOffset(day.AddDays(1).ToDateTime(TimeOnly.MinValue), zone.GetUtcOffset(day.AddDays(1).ToDateTime(TimeOnly.MinValue))).ToUniversalTime();
        var agentIds = agents.Select(a => a.ToString()).ToList();

        var load = isToday
            ? (await db.Tickets.AsNoTracking()
                .Where(t => t.AssigneeId != null && agents.Contains(t.AssigneeId.Value) && t.State != TicketState.Resolved && t.State != TicketState.Closed && !t.Held)
                .Select(t => new { Agent = t.AssigneeId!.Value, Priority = t.Incident != null ? (int?)t.Incident.Priority : t.Voice != null ? (int?)t.Voice.Priority : null })
                .ToListAsync(cancellationToken))
                .GroupBy(t => t.Agent).ToDictionary(g => g.Key, g => (Open: g.Count(), Weighted: g.Sum(t => Weight(t.Priority))))
            : [];

        var assigned = (await db.Database.SqlQuery<AssigneeCount>($"""
                SELECT changes -> 'assigneeId' ->> 'new' AS assignee, count(*)::int AS count
                FROM audit_events
                WHERE entity_type = 'ticket' AND occurred_at >= {from} AND occurred_at < {to}
                  AND changes ? 'assigneeId' AND changes -> 'assigneeId' ->> 'new' = ANY({agentIds})
                GROUP BY 1
                """).ToListAsync(cancellationToken))
            .ToDictionary(r => Guid.Parse(r.Assignee), r => r.Count);

        var actions = await db.Database.SqlQuery<Activity>($"""
                SELECT actor_id AS actor, occurred_at AS at, entity_type AS entity, entity_id
                FROM audit_events
                WHERE actor_type = 'User' AND actor_id = ANY({agentIds}) AND occurred_at >= {from} AND occurred_at < {to}
                  AND category IN ('Create', 'Change') AND entity_type = ANY({ActivityEntities})
                """).ToListAsync(cancellationToken);

        // The tickets each change belongs to.
        var commentIds = Ids(actions, "ticket_comment");
        var taskIds = Ids(actions, "request_task");
        var commentTickets = await db.TicketComments.AsNoTracking().Where(c => commentIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.TicketId, cancellationToken);
        var taskTickets = await db.RequestTasks.AsNoTracking().Where(t => taskIds.Contains(t.Id)).ToDictionaryAsync(t => t.Id, t => t.TicketId, cancellationToken);
        Guid? TicketOf(Activity a) => !Guid.TryParse(a.EntityId, out var id) ? null : a.Entity switch
        {
            "ticket" or "incident" => id,
            "ticket_comment" => commentTickets.TryGetValue(id, out var t) ? t : null,
            _ => taskTickets.TryGetValue(id, out var r) ? r : null,
        };

        var handled = actions.Where(a => Guid.TryParse(a.Actor, out _)).GroupBy(a => Guid.Parse(a.Actor))
            .ToDictionary(g => g.Key, g => g.Select(TicketOf).OfType<Guid>().ToHashSet());
        var touched = handled.Values.SelectMany(t => t).Distinct().ToList();
        var difficult = new HashSet<Guid>();
        difficult.UnionWith(await db.AiTicketInsights.AsNoTracking().Where(i => touched.Contains(i.TicketId) && i.FrustratedSince != null).Select(i => i.TicketId).ToListAsync(cancellationToken));
        difficult.UnionWith(await db.SlaAlerts.AsNoTracking().Where(a => touched.Contains(a.TicketId)).Select(a => a.TicketId).ToListAsync(cancellationToken));
        difficult.UnionWith(await db.Incidents.AsNoTracking().Where(i => touched.Contains(i.TicketId) && i.IsMajor).Select(i => i.TicketId).ToListAsync(cancellationToken));
        difficult.UnionWith(await db.CustomerVoiceRecords.AsNoTracking().Where(v => touched.Contains(v.TicketId) && v.Severity == 1).Select(v => v.TicketId).ToListAsync(cancellationToken));

        var existing = await db.WellbeingDays.Where(d => d.Day == day && agents.Contains(d.UserId)).ToDictionaryAsync(d => d.UserId, cancellationToken);
        foreach (var agent in agents)
        {
            var own = actions.Where(a => a.Actor == agent.ToString()).ToList();
            var calendar = calendarOf.GetValueOrDefault(agent) ?? WorkingCalendar.AllDay;
            var afterHours = own.Where(a => !calendar.IsWorkingTime(a.At)).ToList();
            var tickets = handled.GetValueOrDefault(agent) ?? [];
            var (open, weighted) = load.GetValueOrDefault(agent);
            var newAssignments = assigned.GetValueOrDefault(agent);
            if (!existing.TryGetValue(agent, out var row))
            {
                if (own.Count == 0 && newAssignments == 0 && open == 0)
                {
                    continue;
                }

                row = new WellbeingDay { UserId = agent, Day = day };
                db.WellbeingDays.Add(row);
            }

            if (isToday)
            {
                row.OpenAssigned = open;
                row.WeightedLoad = weighted;
            }

            row.NewAssignments = newAssignments;
            row.Actions = own.Count;
            row.AfterHoursActions = afterHours.Count;
            row.AfterHoursHours = afterHours.Select(a => TimeZoneInfo.ConvertTime(a.At, calendar.TimeZone))
                .Select(t => new DateTime(t.Year, t.Month, t.Day, t.Hour, 0, 0, DateTimeKind.Unspecified)).Distinct().Count();
            row.HandledTickets = tickets.Count;
            row.DifficultTickets = tickets.Count(difficult.Contains);
            row.ComputedAt = now;
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    private static List<Guid> Ids(IEnumerable<Activity> actions, string entity) =>
        [.. actions.Where(a => a.Entity == entity && Guid.TryParse(a.EntityId, out _)).Select(a => Guid.Parse(a.EntityId!)).Distinct()];

    /// <summary>The figures of the last <paramref name="days"/> days up to <paramref name="today"/>, per agent.</summary>
    public async Task<Dictionary<Guid, List<WellbeingDay>>> DaysAsync(IReadOnlyCollection<Guid> agents, DateOnly today, int days, CancellationToken cancellationToken)
    {
        var first = today.AddDays(1 - days);
        return (await db.WellbeingDays.AsNoTracking().Where(d => agents.Contains(d.UserId) && d.Day >= first && d.Day <= today).OrderBy(d => d.Day)
                .ToListAsync(cancellationToken))
            .GroupBy(d => d.UserId).ToDictionary(g => g.Key, g => g.ToList());
    }

    /// <summary>The thresholds of each group: its own values, the rest from the tenant's defaults.</summary>
    public async Task<Func<Guid, WellbeingThresholds>> ThresholdsAsync(CancellationToken cancellationToken)
    {
        var defaults = await settings.GetAsync(WellbeingSettings.Defaults, cancellationToken);
        var own = await db.WellbeingThresholds.AsNoTracking().ToDictionaryAsync(t => t.GroupId, cancellationToken);
        return group => defaults.For(own.GetValueOrDefault(group));
    }

    /// <summary>The values alerts compare: today's weighted load and the last 7 days' averages and totals.</summary>
    public static (int Load, int NewPerDay, int AfterHoursHours, int DifficultPercent, int Handled) AlertValues(IReadOnlyList<WellbeingDay> window, DateOnly today)
    {
        var load = window.FirstOrDefault(d => d.Day == today)?.WeightedLoad ?? 0;
        var handled = window.Sum(d => d.HandledTickets);
        var difficult = window.Sum(d => d.DifficultTickets);
        return (load, (int)Math.Round(window.Sum(d => d.NewAssignments) / (double)AlertWindowDays, MidpointRounding.AwayFromZero),
            window.Sum(d => d.AfterHoursHours), handled == 0 ? 0 : (int)Math.Round(100.0 * difficult / handled, MidpointRounding.AwayFromZero), handled);
    }

    private async Task AlertAsync(DateOnly today, DateTimeOffset now, List<(Guid UserId, Guid GroupId)> memberships, CancellationToken cancellationToken)
    {
        var thresholds = await ThresholdsAsync(cancellationToken);
        var windows = await DaysAsync([.. memberships.Select(m => m.UserId).Distinct()], today, AlertWindowDays, cancellationToken);
        var open = await db.WellbeingAlerts.Where(a => a.EndedAt == null).ToListAsync(cancellationToken);
        foreach (var (user, group) in memberships)
        {
            var limit = thresholds(group);
            var values = AlertValues(windows.GetValueOrDefault(user) ?? [], today);
            var checks = new (WellbeingAlertKind Kind, int Value, int Threshold, bool Over)[]
            {
                (WellbeingAlertKind.Workload, values.Load, limit.MaxWeightedLoad, values.Load > limit.MaxWeightedLoad),
                (WellbeingAlertKind.NewAssignments, values.NewPerDay, limit.MaxNewPerDay, values.NewPerDay > limit.MaxNewPerDay),
                (WellbeingAlertKind.AfterHours, values.AfterHoursHours, limit.MaxAfterHoursHours, values.AfterHoursHours > limit.MaxAfterHoursHours),
                (WellbeingAlertKind.Difficult, values.DifficultPercent, limit.MaxDifficultPercent,
                    values.Handled >= MinHandledForDifficult && values.DifficultPercent > limit.MaxDifficultPercent),
            };
            foreach (var (kind, value, threshold, over) in checks)
            {
                var current = open.FirstOrDefault(a => a.UserId == user && a.GroupId == group && a.Kind == kind);
                if (over && current is null)
                {
                    db.WellbeingAlerts.Add(new WellbeingAlert { UserId = user, GroupId = group, Kind = kind, Value = value, Threshold = threshold, RaisedAt = now, UpdatedAt = now });
                }
                else if (over)
                {
                    current!.Value = value;
                    current.Threshold = threshold;
                    current.UpdatedAt = now;
                }
                else if (current is not null)
                {
                    current.Value = value;
                    current.UpdatedAt = now;
                    current.EndedAt = now;
                }
            }
        }

        // Alerts of people who left the group, or groups that were removed, end too.
        foreach (var stale in open.Where(a => !memberships.Contains((a.UserId, a.GroupId))))
        {
            stale.EndedAt = now;
            stale.UpdatedAt = now;
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    private sealed record AssigneeCount(string Assignee, int Count);

    private sealed record Activity(string Actor, DateTimeOffset At, string Entity, string? EntityId);
}
