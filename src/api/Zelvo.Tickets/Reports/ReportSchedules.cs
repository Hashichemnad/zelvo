using System.Text.Json;
using Zelvo.Data.Reports;
using Zelvo.Data.Tickets;

namespace Zelvo.Tickets.Reports;

/// <summary>What a scheduled report shows besides its period (stored as JSON on the schedule).</summary>
/// <param name="Types">Record types; empty for every type the recipient can see.</param>
/// <param name="Groups">Group ids; empty for every group the recipient can see.</param>
/// <param name="Categories">Category ids (with the ones below them); empty for all.</param>
public sealed record ScheduleFilters(IReadOnlyList<TicketType> Types, IReadOnlyList<Guid> Groups, IReadOnlyList<Guid> Categories)
{
    /// <summary>No filter.</summary>
    public static ScheduleFilters None { get; } = new([], [], []);

    /// <summary>JSON.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, TicketSettingKeys.Json);

    /// <summary>The filters in <paramref name="json"/>, or none.</summary>
    public static ScheduleFilters FromJson(string? json)
    {
        try
        {
            return string.IsNullOrEmpty(json) ? None : JsonSerializer.Deserialize<ScheduleFilters>(json, TicketSettingKeys.Json) ?? None;
        }
        catch (JsonException)
        {
            return None;
        }
    }
}

/// <summary>
/// When scheduled reports go out and what period they cover, in the tenant's time zone: daily
/// reports cover the day before, weekly ones the seven days before, monthly ones the month before.
/// </summary>
public static class ReportScheduleTimes
{
    /// <summary>The first time after <paramref name="after"/> the schedule goes out; null for invalid settings.</summary>
    public static DateTimeOffset? NextRun(ReportFrequency frequency, int? dayOfWeek, int? dayOfMonth, int timeOfDay, TimeZoneInfo zone, DateTimeOffset after)
    {
        ArgumentNullException.ThrowIfNull(zone);
        if (timeOfDay is < 0 or > 1439 || (frequency == ReportFrequency.Weekly && dayOfWeek is not (>= 1 and <= 7))
            || (frequency == ReportFrequency.Monthly && dayOfMonth is not (>= 1 and <= 28)))
        {
            return null;
        }

        var localToday = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(after, zone).DateTime);
        // Look a little over a month ahead; every valid schedule has a run in that window.
        for (var day = localToday.AddDays(-1); day <= localToday.AddDays(40); day = day.AddDays(1))
        {
            var matches = frequency switch
            {
                ReportFrequency.Weekly => ((int)day.DayOfWeek + 6) % 7 + 1 == dayOfWeek,
                ReportFrequency.Monthly => day.Day == dayOfMonth,
                _ => true,
            };
            if (!matches)
            {
                continue;
            }

            var at = Instant(day, timeOfDay, zone);
            if (at > after)
            {
                return at;
            }
        }

        return null;
    }

    /// <summary>The period a run at <paramref name="runAt"/> covers.</summary>
    public static (DateOnly From, DateOnly To) Period(ReportFrequency frequency, DateTimeOffset runAt, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        var day = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(runAt, zone).DateTime);
        return frequency switch
        {
            ReportFrequency.Weekly => (day.AddDays(-7), day.AddDays(-1)),
            ReportFrequency.Monthly => (new DateOnly(day.Year, day.Month, 1).AddMonths(-1), new DateOnly(day.Year, day.Month, 1).AddDays(-1)),
            _ => (day.AddDays(-1), day.AddDays(-1)),
        };
    }

    /// <summary>The report request of a run: the schedule's dashboard and filters with the run's period.</summary>
    public static ReportRequest? Request(ReportSchedule schedule, DateOnly from, DateOnly to)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        if (!Enum.TryParse<ReportKind>(schedule.Report, out var kind))
        {
            return null;
        }

        var filters = ScheduleFilters.FromJson(schedule.Filters);
        return new ReportRequest(kind, new ReportFilter(from, to, filters.Types, filters.Groups, filters.Categories));
    }

    private static DateTimeOffset Instant(DateOnly day, int minutes, TimeZoneInfo zone)
    {
        var local = day.ToDateTime(new TimeOnly(minutes / 60, minutes % 60), DateTimeKind.Unspecified);
        // A time skipped by a clock change goes out an hour later (the UAE has none).
        while (zone.IsInvalidTime(local))
        {
            local = local.AddHours(1);
        }

        return new DateTimeOffset(local, zone.GetUtcOffset(local)).ToUniversalTime();
    }
}
