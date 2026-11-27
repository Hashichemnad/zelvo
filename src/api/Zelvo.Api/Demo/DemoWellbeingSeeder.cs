using Microsoft.EntityFrameworkCore;
using Zelvo.Api.Settings;
using Zelvo.Api.Wellbeing;
using Zelvo.Data;
using Zelvo.Data.Wellbeing;

namespace Zelvo.Api.Demo;

/// <summary>
/// Task K4 (module 21): agent wellbeing turned on in each sample organisation (as if the
/// administrator had told staff), with two weeks of daily figures for the service desk: the sample
/// agent is overloaded (heavy open work, many new assignments, after-hours replies and a high share
/// of escalated tickets) with open alerts, so the team lead can try suggested reassignment; the
/// others are within the thresholds. The hourly job works out today's figures from the real sample
/// tickets as usual.
/// </summary>
internal sealed class DemoWellbeingSeeder : IDemoSeeder
{
    public string Key => "k4-wellbeing";

    public int Order => 280;

    public async Task SeedAsync(DemoSeedContext context, CancellationToken cancellationToken)
    {
        var db = context.Get<ZelvoDbContext>();
        var settings = context.Get<TenantSettingsStore>();
        var agentEmail = context.Organisation.User("agent") is { } a ? context.Organisation.UsernameOf(a) : null;
        var agent = agentEmail is null ? null : await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Email == agentEmail, cancellationToken).ConfigureAwait(false);
        var groupId = agent is null ? (Guid?)null
            : await db.SupportGroupMembers.AsNoTracking().Where(m => m.UserId == agent.Id).Select(m => (Guid?)m.GroupId).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (agent is null || groupId is not { } group)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        await settings.SetAsync(WellbeingSettings.State, new WellbeingState(true, now.AddDays(-14), null, true), cancellationToken).ConfigureAwait(false);
        var colleagues = await db.SupportGroupMembers.AsNoTracking().Where(m => m.GroupId == group && m.UserId != agent.Id).Select(m => m.UserId)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, TimeZoneInfo.FindSystemTimeZoneById("Asia/Dubai")).DateTime);
        for (var back = 13; back >= 1; back--)
        {
            var day = today.AddDays(-back);
            var weekend = day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
            db.WellbeingDays.Add(new WellbeingDay
            {
                UserId = agent.Id, Day = day, OpenAssigned = 11 + (back % 3), WeightedLoad = 44 + (back % 4) * 2, NewAssignments = weekend ? 4 : 15 + (back % 3),
                Actions = weekend ? 6 : 38, AfterHoursActions = weekend ? 6 : 9, AfterHoursHours = weekend ? 2 : 2, HandledTickets = weekend ? 2 : 14,
                DifficultTickets = weekend ? 1 : 8, ComputedAt = now,
            });
            foreach (var colleague in colleagues)
            {
                db.WellbeingDays.Add(new WellbeingDay
                {
                    UserId = colleague, Day = day, OpenAssigned = 4, WeightedLoad = 12, NewAssignments = weekend ? 0 : 5, Actions = weekend ? 0 : 22,
                    AfterHoursActions = 0, AfterHoursHours = 0, HandledTickets = weekend ? 0 : 7, DifficultTickets = weekend ? 0 : 1, ComputedAt = now,
                });
            }
        }

        foreach (var (kind, value, threshold) in new[] { (WellbeingAlertKind.Workload, 48, 40), (WellbeingAlertKind.NewAssignments, 13, 12), (WellbeingAlertKind.AfterHours, 14, 5), (WellbeingAlertKind.Difficult, 56, 50) })
        {
            db.WellbeingAlerts.Add(new WellbeingAlert
            {
                UserId = agent.Id, GroupId = group, Kind = kind, Value = value, Threshold = threshold, RaisedAt = now.AddDays(-3), UpdatedAt = now,
            });
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
