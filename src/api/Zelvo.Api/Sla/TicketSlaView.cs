using Microsoft.EntityFrameworkCore;
using Zelvo.Api.ServiceDesk;
using Zelvo.Data;
using Zelvo.Data.Localization;
using Zelvo.Data.Sla;
using Zelvo.Tickets.Sla;

namespace Zelvo.Api.Sla;

/// <summary>How an SLA clock stands, as screens show it.</summary>
internal enum SlaStatus
{
    /// <summary>No target applies.</summary>
    None,

    /// <summary>Counting, below the first threshold.</summary>
    Running,

    /// <summary>Counting and past an escalation threshold below 100 %.</summary>
    AtRisk,

    /// <summary>Paused while waiting (an on-hold reason that pauses SLAs).</summary>
    Paused,

    /// <summary>Stopped within the target.</summary>
    Met,

    /// <summary>The target was passed (the clock may still be counting).</summary>
    Breached,
}

/// <summary>An SLA clock of a ticket.</summary>
/// <param name="Metric">Response or Resolution.</param>
/// <param name="Status">How it stands.</param>
/// <param name="State">Running, Paused or Stopped.</param>
/// <param name="TargetMinutes">The target in business minutes; null without one.</param>
/// <param name="ElapsedSeconds">Business seconds counted so far (up to now while running).</param>
/// <param name="Percent">Share of the target used, in percent.</param>
/// <param name="DueAt">While running: when the target falls due.</param>
/// <param name="BreachedAt">When the target was passed.</param>
/// <param name="StoppedAt">When the clock last stopped.</param>
/// <param name="Policy">The policy that gave the target.</param>
/// <param name="Calendar">The calendar it counts on; null around the clock.</param>
internal sealed record SlaClockResponse(
    SlaMetric Metric, SlaStatus Status, SlaClockState State, int? TargetMinutes, long ElapsedSeconds, int Percent,
    DateTimeOffset? DueAt, DateTimeOffset? BreachedAt, DateTimeOffset? StoppedAt, NamedRef? Policy, NamedRef? Calendar);

/// <summary>SLA clocks of tickets for responses, with the time counted up to now.</summary>
internal sealed class TicketSlaView
{
    private readonly Dictionary<Guid, List<TicketSla>> _clocks;
    private readonly Dictionary<Guid, (LocalizedText Name, WorkingCalendar Calendar)> _calendars;
    private readonly Dictionary<Guid, LocalizedText> _policies;

    private TicketSlaView(Dictionary<Guid, List<TicketSla>> clocks, Dictionary<Guid, (LocalizedText, WorkingCalendar)> calendars, Dictionary<Guid, LocalizedText> policies)
    {
        _clocks = clocks;
        _calendars = calendars;
        _policies = policies;
    }

    public static async Task<TicketSlaView> LoadAsync(ZelvoDbContext db, IReadOnlyCollection<Guid> ticketIds, CancellationToken cancellationToken)
    {
        var ids = ticketIds.Distinct().ToList();
        var clocks = ids.Count == 0 ? [] : await db.TicketSlas.AsNoTracking().Where(c => ids.Contains(c.TicketId)).ToListAsync(cancellationToken).ConfigureAwait(false);
        var calendarIds = clocks.Where(c => c.CalendarId is not null).Select(c => c.CalendarId!.Value).Distinct().ToList();
        var calendars = calendarIds.Count == 0 ? [] : await db.BusinessCalendars.AsNoTracking().Where(c => calendarIds.Contains(c.Id))
            .Include(c => c.Periods).Include(c => c.Holidays).AsSplitQuery().ToListAsync(cancellationToken).ConfigureAwait(false);
        var policyIds = clocks.Where(c => c.PolicyId is not null).Select(c => c.PolicyId!.Value).Distinct().ToList();
        var policies = policyIds.Count == 0 ? [] : await db.SlaPolicies.AsNoTracking().Where(p => policyIds.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, p => p.Name, cancellationToken).ConfigureAwait(false);
        return new TicketSlaView(
            clocks.GroupBy(c => c.TicketId).ToDictionary(g => g.Key, g => g.OrderBy(c => c.Metric).ToList()),
            calendars.ToDictionary(c => c.Id, c => (c.Name, WorkingCalendar.From(c))),
            policies);
    }

    public IReadOnlyList<SlaClockResponse> For(Guid ticketId, DateTimeOffset now) =>
        _clocks.TryGetValue(ticketId, out var clocks) ? [.. clocks.Select(c => Response(c, now))] : [];

    private SlaClockResponse Response(TicketSla clock, DateTimeOffset now)
    {
        var known = clock.CalendarId is { } id && _calendars.TryGetValue(id, out var entry) && entry.Calendar.HasWorkingTime;
        var calendar = known ? _calendars[clock.CalendarId!.Value].Calendar : WorkingCalendar.AllDay;
        var elapsed = clock.ElapsedSeconds + (clock.State == SlaClockState.Running ? calendar.SecondsBetween(clock.CountedUntil, now) : 0);
        var percent = clock.TargetMinutes is { } target ? (int)Math.Min(999, elapsed * 100 / (target * 60L)) : 0;
        var status = clock.TargetMinutes is null ? SlaStatus.None
            : clock.BreachedAt is not null || percent >= 100 ? SlaStatus.Breached
            : clock.State == SlaClockState.Stopped ? SlaStatus.Met
            : clock.State == SlaClockState.Paused ? SlaStatus.Paused
            : clock.ReachedPercent > 0 ? SlaStatus.AtRisk
            : SlaStatus.Running;
        return new SlaClockResponse(
            clock.Metric, status, clock.State, clock.TargetMinutes, elapsed, percent, clock.DueAt, clock.BreachedAt, clock.StoppedAt,
            clock.PolicyId is { } p && _policies.TryGetValue(p, out var policyName) ? NamedRef.From(p, policyName) : null,
            known ? NamedRef.From(clock.CalendarId!.Value, _calendars[clock.CalendarId.Value].Name) : null);
    }
}
