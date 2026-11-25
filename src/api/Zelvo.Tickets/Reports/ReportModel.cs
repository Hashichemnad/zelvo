using System.Globalization;
using Zelvo.Data.Localization;
using Zelvo.Data.Tickets;

namespace Zelvo.Tickets.Reports;

/// <summary>The built-in dashboards of module 10 (task F2; fixed dashboards with filters, decided 2026-11-25).</summary>
public enum ReportKind
{
    /// <summary>Key figures of every dashboard.</summary>
    Overview,

    /// <summary>Tickets created, resolved and closed over time.</summary>
    Volumes,

    /// <summary>Open tickets now and over time.</summary>
    Backlog,

    /// <summary>Response and resolution targets met.</summary>
    Sla,

    /// <summary>Figures per agent.</summary>
    Agents,

    /// <summary>Happiness meter ratings (GOV-2, GOV-3).</summary>
    Happiness,
}

/// <summary>How the period is split for trends.</summary>
public enum ReportInterval
{
    /// <summary>One bucket per day.</summary>
    Day,

    /// <summary>One bucket per ISO week (Monday first).</summary>
    Week,

    /// <summary>One bucket per calendar month.</summary>
    Month,
}

/// <summary>Permissions of reporting (task F2).</summary>
public static class ReportPermissions
{
    /// <summary>Dashboards of the groups the person leads (team leads) and the agents in them.</summary>
    public const string Read = "reports.read";

    /// <summary>Dashboards of the whole tenant, every group and agent (executives, administrators).</summary>
    public const string All = "reports.all";

    /// <summary>Scheduled reports for other people and every schedule of the tenant; report targets.</summary>
    public const string Manage = "reports.manage";

    /// <summary>The record types a person with <paramref name="permissions"/> may see in reports.</summary>
    public static IReadOnlySet<TicketType> TypesFor(IReadOnlySet<string> permissions)
    {
        ArgumentNullException.ThrowIfNull(permissions);
        var types = new HashSet<TicketType>();
        if (permissions.Contains(IncidentPermissions.Read))
        {
            types.Add(TicketType.Incident);
        }

        if (permissions.Contains(RequestSearch.ReadPermission))
        {
            types.Add(TicketType.ServiceRequest);
        }

        if (permissions.Contains(VoicePermissions.Read))
        {
            types.UnionWith([TicketType.Complaint, TicketType.Inquiry, TicketType.Suggestion]);
        }

        return types;
    }
}

/// <summary>
/// What a dashboard shows: a period of local dates in the tenant's time zone (both included) and
/// optional record types, groups and categories (a category includes the ones below it).
/// </summary>
public sealed record ReportFilter(
    DateOnly From, DateOnly To, IReadOnlyList<TicketType> Types, IReadOnlyList<Guid> Groups, IReadOnlyList<Guid> Categories, ReportInterval? Interval = null)
{
    /// <summary>Longest period (two years).</summary>
    public const int MaxDays = 731;

    /// <summary>Most groups or categories in a filter.</summary>
    public const int MaxIds = 50;

    /// <summary>Most buckets of a trend.</summary>
    public const int MaxBuckets = 400;

    /// <summary>The last 30 days up to <paramref name="today"/>.</summary>
    public static ReportFilter LastDays(DateOnly today, int days = 30) => new(today.AddDays(1 - days), today, [], [], []);

    /// <summary>Why the filter cannot be used, or null.</summary>
    public string? Problem()
    {
        if (To < From)
        {
            return "The period ends before it starts.";
        }

        if (To.DayNumber - From.DayNumber + 1 > MaxDays)
        {
            return $"Choose a period of at most {MaxDays} days.";
        }

        if (Groups.Count > MaxIds || Categories.Count > MaxIds)
        {
            return $"Choose at most {MaxIds} groups and {MaxIds} categories.";
        }

        return Buckets().Count > MaxBuckets ? "The period has too many points for this interval; choose a longer interval." : null;
    }

    /// <summary>The interval asked for, or one that suits the period: days up to two months, weeks up to a year, then months.</summary>
    public ReportInterval EffectiveInterval =>
        Interval ?? ((To.DayNumber - From.DayNumber + 1) switch
        {
            <= 62 => ReportInterval.Day,
            <= 366 => ReportInterval.Week,
            _ => ReportInterval.Month,
        });

    /// <summary>The first day of each bucket of the period.</summary>
    public IReadOnlyList<DateOnly> Buckets()
    {
        var interval = EffectiveInterval;
        var start = BucketOf(From, interval);
        var buckets = new List<DateOnly>();
        for (var day = start; day <= To && buckets.Count <= MaxBuckets; day = Next(day, interval))
        {
            buckets.Add(day);
        }

        return buckets;
    }

    /// <summary>The first day of the bucket that holds <paramref name="day"/>.</summary>
    public static DateOnly BucketOf(DateOnly day, ReportInterval interval) => interval switch
    {
        ReportInterval.Week => day.AddDays(-(((int)day.DayOfWeek + 6) % 7)),
        ReportInterval.Month => new DateOnly(day.Year, day.Month, 1),
        _ => day,
    };

    /// <summary>The first day of the next bucket.</summary>
    public static DateOnly Next(DateOnly bucket, ReportInterval interval) => interval switch
    {
        ReportInterval.Week => bucket.AddDays(7),
        ReportInterval.Month => bucket.AddMonths(1),
        _ => bucket.AddDays(1),
    };

    /// <summary>A short text of the filter for audit entries.</summary>
    public IReadOnlyDictionary<string, object?> AuditDetails() => new Dictionary<string, object?>
    {
        ["from"] = From.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        ["to"] = To.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        ["types"] = Types.Count == 0 ? null : string.Join(",", Types),
        ["groups"] = Groups.Count,
        ["categories"] = Categories.Count,
    };
}

/// <summary>
/// What a person may see in reports, worked out from their permissions and groups: the record
/// types they can read; every group (<see cref="ReportPermissions.All"/>) or only the groups they
/// lead (<see cref="ReportPermissions.Read"/>); or only their own figures (<see cref="Agent"/>).
/// </summary>
/// <param name="Types">Record types they may see.</param>
/// <param name="Groups">Groups whose tickets they see; null for all tickets.</param>
/// <param name="Agent">Only tickets assigned to this person (their own figures).</param>
/// <param name="PortalRatings">General portal ratings (no ticket) are included.</param>
public sealed record ReportScope(IReadOnlySet<TicketType> Types, IReadOnlySet<Guid>? Groups, Guid? Agent, bool PortalRatings)
{
    /// <summary>The whole tenant, every record type.</summary>
    public static ReportScope Everything { get; } = new(Enum.GetValues<TicketType>().ToHashSet(), null, null, true);

    /// <summary>Whether this is the "my performance" scope of one agent.</summary>
    public bool OwnFiguresOnly => Agent is not null;
}

/// <summary>Reporting targets of a tenant (Settings → Reports).</summary>
/// <param name="SlaTargetPercent">Share of SLA targets that should be met (the requirements' 95 % first response).</param>
/// <param name="HappinessTargetPercent">Happiness that should be reached (4 out of 5 is 75 % of the scale).</param>
public sealed record ReportTargets(double SlaTargetPercent, double HappinessTargetPercent)
{
    /// <summary>Setting key.</summary>
    public const string Key = "reports.targets";

    /// <summary>Requirements' success metrics.</summary>
    public static ReportTargets Default { get; } = new(95, 75);

    /// <summary>Both between 1 and 100.</summary>
    public bool IsValid() => SlaTargetPercent is >= 1 and <= 100 && HappinessTargetPercent is >= 1 and <= 100;
}

/// <summary>A count per key (state, channel, priority, age band and so on).</summary>
public sealed record KeyCount(string Key, int Count);

/// <summary>A count per named thing (group, category, service); name null when it was removed.</summary>
public sealed record NamedCount(Guid? Id, ReportName? Name, int Count);

/// <summary>A bilingual name as reports return it.</summary>
public sealed record ReportName(string? En, string? Ar)
{
    /// <summary>From a stored value.</summary>
    public static ReportName From(LocalizedText text) => new(text.En, text.Ar);

    /// <summary>The name in <paramref name="language"/>, else the other one.</summary>
    public string In(string language) => (language == LocalizedText.Arabic ? Ar ?? En : En ?? Ar) ?? string.Empty;
}

/// <summary>Key figures of every dashboard.</summary>
public sealed record OverviewReport(
    DateOnly From, DateOnly To, int Created, int Resolved, int Open, double? ResponsePercent, double? ResolutionPercent,
    int Ratings, double? HappinessPercent, double? MedianResponseMinutes, ReportTargets Targets);

/// <summary>One bucket of the volume trend.</summary>
public sealed record VolumePoint(DateOnly Bucket, int Created, int Resolved, int Closed);

/// <summary>Tickets created, resolved and closed in the period.</summary>
public sealed record VolumesReport(
    DateOnly From, DateOnly To, ReportInterval Interval, int Created, int Resolved, int Closed, IReadOnlyList<VolumePoint> Series,
    IReadOnlyList<KeyCount> ByType, IReadOnlyList<KeyCount> ByChannel, IReadOnlyList<KeyCount> ByPriority, IReadOnlyList<NamedCount> ByGroup,
    IReadOnlyList<NamedCount> ByCategory);

/// <summary>Open tickets at the end of a bucket.</summary>
public sealed record BacklogPoint(DateOnly Bucket, int Open);

/// <summary>Open tickets now, and the open count at the end of each bucket of the period.</summary>
public sealed record BacklogReport(
    DateOnly From, DateOnly To, ReportInterval Interval, int Open, int Unassigned, IReadOnlyList<KeyCount> ByState, IReadOnlyList<KeyCount> ByAge,
    IReadOnlyList<KeyCount> ByType, IReadOnlyList<KeyCount> ByPriority, IReadOnlyList<NamedCount> ByGroup, IReadOnlyList<BacklogPoint> Trend);

/// <summary>Clocks of one metric: met, breached, still running; percentage met of those decided.</summary>
public sealed record SlaFigures(int Met, int Breached, int Running)
{
    /// <summary>Met as a share of met and breached; null when none is decided yet.</summary>
    public double? Percent => Met + Breached == 0 ? null : Math.Round(Met * 100.0 / (Met + Breached), 1);
}

/// <summary>Response and resolution figures of a group of tickets.</summary>
public sealed record SlaRow(string Key, Guid? Id, ReportName? Name, SlaFigures Response, SlaFigures Resolution);

/// <summary>Compliance per bucket of the period (by when the ticket was created).</summary>
public sealed record SlaPoint(DateOnly Bucket, double? ResponsePercent, double? ResolutionPercent);

/// <summary>SLA compliance of tickets created in the period.</summary>
public sealed record SlaReport(
    DateOnly From, DateOnly To, ReportInterval Interval, SlaFigures Response, SlaFigures Resolution, IReadOnlyList<SlaRow> ByPriority,
    IReadOnlyList<SlaRow> ByType, IReadOnlyList<SlaRow> ByGroup, IReadOnlyList<SlaRow> ByPolicy, IReadOnlyList<SlaPoint> Trend, double TargetPercent);

/// <summary>Figures of one agent in the period.</summary>
public sealed record AgentRow(
    Guid Id, string Name, int Assigned, int Resolved, int OpenNow, double? MedianResponseMinutes, double? MedianResolutionHours,
    double? SlaPercent, double? ReopenPercent, int Ratings, double? HappinessPercent);

/// <summary>Agent performance in the period.</summary>
public sealed record AgentsReport(DateOnly From, DateOnly To, IReadOnlyList<AgentRow> Agents);

/// <summary>Ratings of a group and their happiness percentage.</summary>
public sealed record HappinessGroup(string Key, Guid? Id, ReportName? Name, int Ratings, double? HappinessPercent);

/// <summary>Ratings with a score on a scale.</summary>
public sealed record ScoreCount(int Points, int Score, int Count);

/// <summary>Ratings of a bucket.</summary>
public sealed record HappinessPoint(DateOnly Bucket, int Ratings, double? HappinessPercent);

/// <summary>Happiness meter ratings given in the period (GOV-2, GOV-3).</summary>
public sealed record HappinessReport(
    DateOnly From, DateOnly To, ReportInterval Interval, int Ratings, double? HappinessPercent, IReadOnlyList<ScoreCount> Scores,
    IReadOnlyList<HappinessGroup> ByType, IReadOnlyList<HappinessGroup> ByChannel, IReadOnlyList<HappinessGroup> ByGroup,
    IReadOnlyList<HappinessGroup> ByService, IReadOnlyList<HappinessPoint> Trend, double TargetPercent);
