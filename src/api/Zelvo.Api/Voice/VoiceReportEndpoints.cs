using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Zelvo.Api.Conventions;
using Zelvo.Api.Identity;
using Zelvo.Api.Tenancy;
using Zelvo.Data;
using Zelvo.Data.Auditing;
using Zelvo.Data.Tickets;
using Zelvo.Data.Voice;

namespace Zelvo.Api.Voice;

/// <summary>
/// Happiness and customer-voice reporting (GOV-3, task C6): complaints, inquiries and suggestions
/// and happiness trends per service and channel, for a period. Dashboards and scheduled reports
/// come with task F2.
/// </summary>
internal static class VoiceReportEndpoints
{
    /// <summary>Longest period of one report.</summary>
    public const int MaxDays = 731;

    public static RouteGroupBuilder MapVoiceReportEndpoints(this RouteGroupBuilder group)
    {
        var report = group.MapGroup("/customer-voice/report").WithTags("Customer voice").RequireTenant();
        report.MapGet("/", GetAsync).WithName("GetCustomerVoiceReport")
            .WithSummary("Customer voice and happiness for a period (from, to: dates, UTC; the last 12 months by default, at most 2 years): records per type, state, channel and service; complaints per severity, root cause and confirmation; suggestion reviews; happiness per record type, channel and service; and a month-by-month trend. Happiness is the average score as a percentage of the scale (worst 0 %, best 100 %). Needs voice.report; audited.")
            .RequirePermission(Permissions.VoiceReport).Produces<VoiceReport>().ProducesValidationProblem();
        report.MapGet("/csv", CsvAsync).WithName("ExportCustomerVoiceReport")
            .WithSummary("The month-by-month trend of the report as CSV (UTF-8 with BOM, for Excel). Needs voice.report; audited.")
            .RequirePermission(Permissions.VoiceReport).Produces<string>(contentType: "text/csv");
        return group;
    }

    internal static async Task<IResult> GetAsync(DateOnly? from, DateOnly? to, ZelvoDbContext db, IAuditLog audit, CancellationToken cancellationToken)
    {
        if (Period(from, to) is not { } period)
        {
            return UserEndpoints.Validation("from", $"Give a period of at most {MaxDays} days with from before to.");
        }

        var report = await BuildAsync(db, period.From, period.To, cancellationToken);
        await audit.RecordAsync(AuditCategory.View, "voice.report.viewed", "voice_report", null,
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["from"] = report.From.ToString("O"), ["to"] = report.To.ToString("O") }, cancellationToken);
        return TypedResults.Ok(report);
    }

    internal static async Task<IResult> CsvAsync(DateOnly? from, DateOnly? to, ZelvoDbContext db, IAuditLog audit, CancellationToken cancellationToken)
    {
        if (Period(from, to) is not { } period)
        {
            return UserEndpoints.Validation("from", $"Give a period of at most {MaxDays} days with from before to.");
        }

        var report = await BuildAsync(db, period.From, period.To, cancellationToken);
        await audit.RecordAsync(AuditCategory.View, "voice.report.exported", "voice_report", null,
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["from"] = report.From.ToString("O"), ["to"] = report.To.ToString("O") }, cancellationToken);
        var csv = new StringBuilder("﻿month,complaints,inquiries,suggestions,ratings,happiness_percent\r\n");
        foreach (var m in report.Months)
        {
            csv.Append(CultureInfo.InvariantCulture, $"{m.Month},{m.Complaints},{m.Inquiries},{m.Suggestions},{m.Ratings},{m.HappinessPercent?.ToString("0.0", CultureInfo.InvariantCulture)}\r\n");
        }

        return TypedResults.File(Encoding.UTF8.GetBytes(csv.ToString()), "text/csv; charset=utf-8", $"customer-voice-{report.From:yyyy-MM-dd}-{report.To:yyyy-MM-dd}.csv");
    }

    private static (DateTimeOffset From, DateTimeOffset To)? Period(DateOnly? from, DateOnly? to)
    {
        var end = (to ?? DateOnly.FromDateTime(DateTime.UtcNow)).AddDays(1);
        var start = from ?? end.AddMonths(-12);
        if (start >= end || end.DayNumber - start.DayNumber > MaxDays)
        {
            return null;
        }

        return (new DateTimeOffset(start.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero), new DateTimeOffset(end.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero));
    }

    /// <summary>The report of records created and ratings given in [from, to).</summary>
    internal static async Task<VoiceReport> BuildAsync(ZelvoDbContext db, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken)
    {
        var records = await db.Tickets.AsNoTracking()
            .Where(t => (t.Type == TicketType.Complaint || t.Type == TicketType.Inquiry || t.Type == TicketType.Suggestion) && t.CreatedAt >= from && t.CreatedAt < to)
            .Select(t => new Row(t.Type, t.State, t.Channel, t.CreatedAt, t.ResolvedAt, t.Voice!.ServiceId, t.Voice.Severity, t.Voice.RootCauseKey,
                t.Voice.Confirmation, t.Voice.Review, t.Voice.Anonymous))
            .ToListAsync(cancellationToken);
        var ratings = await db.HappinessRatings.AsNoTracking().Where(r => r.CreatedAt >= from && r.CreatedAt < to)
            .Select(r => new RatingRow(r.RecordType, r.Channel, r.ServiceId, r.Score, r.Points, r.CreatedAt))
            .ToListAsync(cancellationToken);
        var serviceIds = records.Select(r => r.ServiceId).Concat(ratings.Select(r => r.ServiceId)).OfType<Guid>().Distinct().ToList();
        var services = await db.CatalogServices.AsNoTracking().Where(s => serviceIds.Contains(s.Id))
            .ToDictionaryAsync(s => s.Id, s => (s.Code, s.Name), cancellationToken);

        var types = new[] { TicketType.Complaint, TicketType.Inquiry, TicketType.Suggestion };
        var byType = types.Select(type =>
        {
            var rows = records.Where(r => r.Type == type).ToList();
            var resolved = rows.Where(r => r.ResolvedAt is not null).ToList();
            return new TypeCount(type, rows.Count, rows.Count(r => r.State is not (TicketState.Resolved or TicketState.Closed)), resolved.Count,
                rows.Count(r => r.Anonymous),
                resolved.Count == 0 ? null : Math.Round(resolved.Average(r => (r.ResolvedAt!.Value - r.CreatedAt).TotalHours), 1));
        }).ToList();

        var complaints = records.Where(r => r.Type == TicketType.Complaint).ToList();
        var months = new List<MonthRow>();
        for (var month = new DateTimeOffset(from.Year, from.Month, 1, 0, 0, 0, TimeSpan.Zero); month < to; month = month.AddMonths(1))
        {
            var next = month.AddMonths(1);
            bool In(DateTimeOffset at) => at >= month && at < next;
            var inMonth = ratings.Where(r => In(r.CreatedAt)).ToList();
            months.Add(new MonthRow(month.ToString("yyyy-MM", CultureInfo.InvariantCulture),
                records.Count(r => r.Type == TicketType.Complaint && In(r.CreatedAt)),
                records.Count(r => r.Type == TicketType.Inquiry && In(r.CreatedAt)),
                records.Count(r => r.Type == TicketType.Suggestion && In(r.CreatedAt)),
                inMonth.Count, Percent(inMonth)));
        }

        var serviceRows = serviceIds.Select(id =>
        {
            var rs = ratings.Where(r => r.ServiceId == id).ToList();
            var (code, name) = services.GetValueOrDefault(id);
            return new ServiceRow(id, code ?? "", name is null ? null : LocalizedTextDto.From(name),
                records.Count(r => r.ServiceId == id && r.Type == TicketType.Complaint),
                records.Count(r => r.ServiceId == id && r.Type == TicketType.Inquiry),
                records.Count(r => r.ServiceId == id && r.Type == TicketType.Suggestion),
                rs.Count, Percent(rs));
        }).OrderByDescending(s => s.Complaints + s.Inquiries + s.Suggestions + s.Ratings).Take(50).ToList();

        return new VoiceReport(
            from, to, byType,
            [.. records.GroupBy(r => (r.Type, r.State)).Select(g => new StateCount(g.Key.Type, g.Key.State, g.Count())).OrderBy(s => s.Type).ThenBy(s => s.State)],
            [.. records.GroupBy(r => (r.Type, r.Channel)).Select(g => new ChannelCount(g.Key.Type, g.Key.Channel, g.Count())).OrderBy(c => c.Type).ThenByDescending(c => c.Count)],
            serviceRows,
            [.. complaints.Where(c => c.Severity is not null).GroupBy(c => c.Severity!.Value).Select(g => new KeyCount(g.Key.ToString(CultureInfo.InvariantCulture), g.Count())).OrderBy(k => k.Key, StringComparer.Ordinal)],
            [.. complaints.Where(c => c.RootCauseKey is not null).GroupBy(c => c.RootCauseKey!).Select(g => new KeyCount(g.Key, g.Count())).OrderByDescending(k => k.Count)],
            [.. complaints.Where(c => c.Confirmation is not null).GroupBy(c => c.Confirmation!.Value).Select(g => new KeyCount(g.Key.ToString(), g.Count())).OrderByDescending(k => k.Count)],
            [.. records.Where(r => r.Review is not null).GroupBy(r => r.Review!.Value).Select(g => new KeyCount(g.Key.ToString(), g.Count())).OrderByDescending(k => k.Count)],
            new HappinessSummary(
                ratings.Count, Percent(ratings),
                [.. ratings.GroupBy(r => (r.Points, r.Score)).Select(g => new ScoreCount(g.Key.Points, g.Key.Score, g.Count())).OrderBy(s => s.Points).ThenBy(s => s.Score)],
                [.. ratings.GroupBy(r => r.RecordType?.ToString() ?? "Portal").Select(g => new GroupPercent(g.Key, g.Count(), Percent([.. g]))).OrderByDescending(g => g.Count)],
                [.. ratings.Where(r => r.Channel is not null).GroupBy(r => r.Channel!.Value.ToString()).Select(g => new GroupPercent(g.Key, g.Count(), Percent([.. g]))).OrderByDescending(g => g.Count)]),
            months);
    }

    /// <summary>Average score as a percentage of each rating's scale: worst 0, best 100.</summary>
    private static double? Percent(IReadOnlyCollection<RatingRow> ratings) =>
        ratings.Count == 0 ? null : Math.Round(ratings.Average(r => (r.Score - 1) * 100.0 / (r.Points - 1)), 1);

    private sealed record Row(
        TicketType Type, TicketState State, TicketChannel Channel, DateTimeOffset CreatedAt, DateTimeOffset? ResolvedAt, Guid? ServiceId,
        int? Severity, string? RootCauseKey, ComplaintConfirmation? Confirmation, SuggestionReview? Review, bool Anonymous);

    private sealed record RatingRow(TicketType? RecordType, TicketChannel? Channel, Guid? ServiceId, int Score, int Points, DateTimeOffset CreatedAt);
}

/// <summary>Records of one type: all, open, resolved, anonymous, and the average hours to resolve.</summary>
internal sealed record TypeCount(TicketType Type, int Total, int Open, int Resolved, int Anonymous, double? AverageHoursToResolve);

/// <summary>Records of a type in a state.</summary>
internal sealed record StateCount(TicketType Type, TicketState State, int Count);

/// <summary>Records of a type from a channel.</summary>
internal sealed record ChannelCount(TicketType Type, TicketChannel Channel, int Count);

/// <summary>A count per key.</summary>
internal sealed record KeyCount(string Key, int Count);

/// <summary>Ratings with a score on a scale.</summary>
internal sealed record ScoreCount(int Points, int Score, int Count);

/// <summary>Ratings of a group and their happiness percentage.</summary>
internal sealed record GroupPercent(string Key, int Count, double? HappinessPercent);

/// <summary>Records and happiness of a service.</summary>
internal sealed record ServiceRow(Guid Id, string Code, LocalizedTextDto? Name, int Complaints, int Inquiries, int Suggestions, int Ratings, double? HappinessPercent);

/// <summary>One month of the trend.</summary>
internal sealed record MonthRow(string Month, int Complaints, int Inquiries, int Suggestions, int Ratings, double? HappinessPercent);

/// <summary>Happiness over the period.</summary>
internal sealed record HappinessSummary(int Ratings, double? HappinessPercent, IReadOnlyList<ScoreCount> Scores, IReadOnlyList<GroupPercent> ByRecordType, IReadOnlyList<GroupPercent> ByChannel);

/// <summary>The customer voice report (GOV-3).</summary>
internal sealed record VoiceReport(
    DateTimeOffset From, DateTimeOffset To, IReadOnlyList<TypeCount> Types, IReadOnlyList<StateCount> States, IReadOnlyList<ChannelCount> Channels,
    IReadOnlyList<ServiceRow> Services, IReadOnlyList<KeyCount> Severities, IReadOnlyList<KeyCount> RootCauses, IReadOnlyList<KeyCount> Confirmations,
    IReadOnlyList<KeyCount> Reviews, HappinessSummary Happiness, IReadOnlyList<MonthRow> Months);
