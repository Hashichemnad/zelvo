using Zelvo.Data.Sla;

namespace Zelvo.Tickets.Sla;

/// <summary>A working period in minutes after local midnight; an end of 1440 is the end of the day.</summary>
/// <param name="Start">Start minute (0–1439).</param>
/// <param name="End">End minute (1–1440), after <paramref name="Start"/>.</param>
public readonly record struct WorkingPeriod(int Start, int End);

/// <summary>
/// Business time on a calendar (module 8, GOV-7): working periods per day of the week in the
/// calendar's time zone, minus holidays. SLA clocks count only this time. Times are converted
/// through the time zone for each day, so daylight-saving changes are handled where a zone has them
/// (the UAE has none).
/// </summary>
public sealed class WorkingCalendar
{
    /// <summary>How far ahead a due time is looked for: ten years.</summary>
    public const int MaxDays = 3660;

    private readonly WorkingPeriod[][] _week;
    private readonly HashSet<DateOnly> _holidays;

    /// <summary>A calendar from its time zone, periods and holidays. Overlapping periods of a day are merged.</summary>
    public WorkingCalendar(TimeZoneInfo timeZone, IEnumerable<(DayOfWeek Day, WorkingPeriod Period)> periods, IEnumerable<DateOnly> holidays)
    {
        ArgumentNullException.ThrowIfNull(timeZone);
        ArgumentNullException.ThrowIfNull(periods);
        ArgumentNullException.ThrowIfNull(holidays);
        TimeZone = timeZone;
        var byDay = periods.ToLookup(p => p.Day, p => p.Period);
        _week = [.. Enumerable.Range(0, 7).Select(d => Merge(byDay[(DayOfWeek)d]))];
        _holidays = [.. holidays];
    }

    /// <summary>Around the clock in UTC, without holidays: used when a tenant has no calendar.</summary>
    public static WorkingCalendar AllDay { get; } = new(TimeZoneInfo.Utc,
        Enum.GetValues<DayOfWeek>().Select(d => (d, new WorkingPeriod(0, BusinessCalendarPeriod.MinutesPerDay))), []);

    /// <summary>The calendar's time zone.</summary>
    public TimeZoneInfo TimeZone { get; }

    /// <summary>Whether the calendar has any working time at all.</summary>
    public bool HasWorkingTime => _week.Any(d => d.Length > 0);

    /// <summary>Builds the calendar of a stored <see cref="BusinessCalendar"/> (periods and holidays loaded).</summary>
    public static WorkingCalendar From(BusinessCalendar calendar)
    {
        ArgumentNullException.ThrowIfNull(calendar);
        var zone = FindTimeZone(calendar.TimeZone) ?? TimeZoneInfo.Utc;
        var holidays = calendar.Holidays.SelectMany(h => Days(h.StartDate, h.EndDate));
        return new WorkingCalendar(zone, calendar.Periods.Select(p => (p.Day, new WorkingPeriod(p.StartMinute, p.EndMinute))), holidays);
    }

    /// <summary>The time zone with this IANA (or Windows) id, or <see langword="null"/>.</summary>
    public static TimeZoneInfo? FindTimeZone(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (TimeZoneNotFoundException)
        {
            return null;
        }
        catch (InvalidTimeZoneException)
        {
            return null;
        }
    }

    /// <summary>Whether <paramref name="date"/> (local) has working time: a working day of the week and not a holiday.</summary>
    public bool IsWorkingDay(DateOnly date) => !_holidays.Contains(date) && _week[(int)date.DayOfWeek].Length > 0;

    /// <summary>Business seconds between two instants (0 when <paramref name="to"/> is not later).</summary>
    public long SecondsBetween(DateTimeOffset from, DateTimeOffset to)
    {
        if (to <= from)
        {
            return 0;
        }

        long seconds = 0;
        foreach (var (start, end) in Intervals(from))
        {
            if (start >= to)
            {
                break;
            }

            var s = start < from ? from : start;
            var e = end > to ? to : end;
            if (e > s)
            {
                seconds += (long)(e - s).TotalSeconds;
            }
        }

        return seconds;
    }

    /// <summary>
    /// The instant when <paramref name="seconds"/> of business time have passed after
    /// <paramref name="from"/>; <see langword="null"/> when the calendar has no working time within
    /// <see cref="MaxDays"/> days. Zero seconds is <paramref name="from"/> itself.
    /// </summary>
    public DateTimeOffset? Add(DateTimeOffset from, long seconds)
    {
        if (seconds <= 0)
        {
            return from;
        }

        var left = seconds;
        foreach (var (start, end) in Intervals(from))
        {
            var s = start < from ? from : start;
            if (end <= s)
            {
                continue;
            }

            var length = (long)(end - s).TotalSeconds;
            if (length >= left)
            {
                return s.AddSeconds(left);
            }

            left -= length;
        }

        return null;
    }

    /// <summary>
    /// The same local time of day <paramref name="days"/> working days after <paramref name="from"/>
    /// (auto-close in business days): resolved on Thursday at 10:00 with Friday, Monday, Tuesday,
    /// Wednesday and Thursday working, five working days later is the next Thursday at 10:00.
    /// </summary>
    public DateTimeOffset AddWorkingDays(DateTimeOffset from, int days)
    {
        var local = TimeZoneInfo.ConvertTime(from, TimeZone);
        var date = DateOnly.FromDateTime(local.DateTime);
        var counted = 0;
        for (var i = 0; i < MaxDays && counted < days; i++)
        {
            date = date.AddDays(1);
            if (IsWorkingDay(date))
            {
                counted++;
            }
        }

        if (counted < days)
        {
            return DateTimeOffset.MaxValue;
        }

        return ToInstant(date, (int)local.TimeOfDay.TotalMinutes, local.TimeOfDay.Seconds);
    }

    /// <summary>
    /// The close of the <paramref name="days"/>th working day after the day of <paramref name="from"/>
    /// (task C4, product owner decision 2026-11-24: a service's number of working days as its
    /// default completion target). Requested on Monday with Monday to Friday working, one working
    /// day is the end of Tuesday's working hours; holidays are skipped, and a short Friday counts as
    /// a day. Around the clock, a day ends at midnight.
    /// </summary>
    public DateTimeOffset EndOfWorkingDay(DateTimeOffset from, int days)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(days, 1);
        var date = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(from, TimeZone).DateTime);
        var counted = 0;
        for (var i = 0; i < MaxDays && counted < days; i++)
        {
            date = date.AddDays(1);
            if (IsWorkingDay(date))
            {
                counted++;
            }
        }

        if (counted < days)
        {
            return DateTimeOffset.MaxValue;
        }

        var close = _week[(int)date.DayOfWeek].Max(p => p.End);
        return ToInstant(date, close, 0);
    }

    /// <summary>Working intervals (UTC) from the local day of <paramref name="from"/> on, in order, for at most <see cref="MaxDays"/> days.</summary>
    private IEnumerable<(DateTimeOffset Start, DateTimeOffset End)> Intervals(DateTimeOffset from)
    {
        var first = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(from, TimeZone).DateTime);
        for (var i = 0; i < MaxDays; i++)
        {
            var date = first.AddDays(i);
            if (_holidays.Contains(date))
            {
                continue;
            }

            foreach (var period in _week[(int)date.DayOfWeek])
            {
                var start = ToInstant(date, period.Start, 0);
                var end = ToInstant(date, period.End, 0);
                if (end > start)
                {
                    yield return (start, end);
                }
            }
        }
    }

    /// <summary>A local date and minute of the day as an instant; times skipped by a clock change move to the first valid time after.</summary>
    private DateTimeOffset ToInstant(DateOnly date, int minute, int second)
    {
        var local = date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified).AddMinutes(minute).AddSeconds(second);
        var guard = 0;
        while (TimeZone.IsInvalidTime(local) && guard++ < 180)
        {
            local = local.AddMinutes(1);
        }

        var offset = TimeZone.GetUtcOffset(local);
        return new DateTimeOffset(local, offset).ToUniversalTime();
    }

    private static WorkingPeriod[] Merge(IEnumerable<WorkingPeriod> periods)
    {
        var sorted = periods.Where(p => p.Start < p.End).OrderBy(p => p.Start).ToList();
        var merged = new List<WorkingPeriod>();
        foreach (var period in sorted)
        {
            if (merged.Count > 0 && period.Start <= merged[^1].End)
            {
                merged[^1] = merged[^1] with { End = Math.Max(merged[^1].End, period.End) };
            }
            else
            {
                merged.Add(period);
            }
        }

        return [.. merged];
    }

    private static IEnumerable<DateOnly> Days(DateOnly start, DateOnly end)
    {
        for (var d = start; d <= end && d.DayNumber - start.DayNumber <= 366; d = d.AddDays(1))
        {
            yield return d;
        }
    }
}
