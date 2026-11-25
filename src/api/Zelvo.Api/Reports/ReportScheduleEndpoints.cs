using System.ComponentModel.DataAnnotations;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Zelvo.Api.Conventions;
using Zelvo.Api.Identity;
using Zelvo.Api.Integrations;
using Zelvo.Api.Tenancy;
using Zelvo.Data;
using Zelvo.Data.Auditing;
using Zelvo.Data.Identity;
using Zelvo.Data.Reports;
using Zelvo.Data.Tickets;
using Zelvo.Tickets.Reports;

namespace Zelvo.Api.Reports;

/// <summary>
/// Scheduled reports (module 10, task F2): a dashboard with filters emailed daily, weekly or monthly
/// as a branded PDF. Recipients are tenant users who may see reports, and each report is built with
/// the recipient's own access, in their language (product owner decision 2026-11-25). People who
/// see reports schedule them for themselves; <c>reports.manage</c> adds other recipients and sees
/// every schedule. Changes are audited.
/// </summary>
internal static class ReportScheduleEndpoints
{
    public static RouteGroupBuilder MapReportScheduleEndpoints(this RouteGroupBuilder group)
    {
        var schedules = group.MapGroup("/reports/schedules").WithTags("Reports").RequireTenant().RequireAuthorization().RequirePerson();
        schedules.MapGet("/", ListAsync).WithName("ListReportSchedules")
            .WithSummary("Scheduled reports the caller owns or receives; every schedule with reports.manage.")
            .Produces<IReadOnlyList<ReportScheduleResponse>>();
        schedules.MapPost("/", CreateAsync).WithName("CreateReportSchedule")
            .WithSummary("Schedule a dashboard: name, report (Overview, Volumes, Backlog, Sla, Agents, Happiness), frequency (Daily: the day before; Weekly with dayOfWeek 1–7: the 7 days before; Monthly with dayOfMonth 1–28: the month before), time (HH:mm in the tenant's time zone), filters and recipients. Without reports.manage the only recipient is the caller. Recipients must be active users who may see reports. Audited.")
            .Produces<ReportScheduleResponse>(StatusCodes.Status201Created).ProducesValidationProblem();
        schedules.MapGet("/recipients", RecipientsAsync).WithName("ListReportRecipients")
            .WithSummary("People who may receive scheduled reports (active, holding reports.read or reports.all), by name; search narrows the list. Needs reports.manage.")
            .RequirePermission(Permissions.ReportsManage).Produces<IReadOnlyList<ReportRecipientResponse>>();
        schedules.MapGet("/{id:guid}", GetAsync).WithName("GetReportSchedule")
            .WithSummary("A scheduled report the caller owns or receives (any with reports.manage).").Produces<ReportScheduleResponse>();
        schedules.MapPut("/{id:guid}", UpdateAsync).WithName("UpdateReportSchedule")
            .WithSummary("Change a schedule (its owner or reports.manage). Audited.")
            .Produces<ReportScheduleResponse>().ProducesValidationProblem();
        schedules.MapDelete("/{id:guid}", DeleteAsync).WithName("DeleteReportSchedule")
            .WithSummary("Remove a schedule and its delivery log (its owner or reports.manage). Audited.")
            .Produces(StatusCodes.Status204NoContent);
        schedules.MapPost("/{id:guid}/send-now", SendNowAsync).WithName("SendReportScheduleNow")
            .WithSummary("Queue the report for every recipient now, for the period its next run would cover so far (the last complete day, 7 days or month). Audited.")
            .Produces<ReportScheduleResponse>(StatusCodes.Status202Accepted);
        schedules.MapGet("/{id:guid}/deliveries", DeliveriesAsync).WithName("ListReportDeliveries")
            .WithSummary("The delivery log of a schedule: the last 100 emails with recipient, period, status and error; kept 30 days.")
            .Produces<IReadOnlyList<ReportDeliveryResponse>>();
        return group;
    }

    internal static async Task<IResult> ListAsync(ZelvoDbContext db, CurrentUser user, CancellationToken cancellationToken)
    {
        var me = user.Account!.Id;
        var all = user.Has(Permissions.ReportsManage);
        var rows = await db.ReportSchedules.AsNoTracking().Include(s => s.Recipients)
            .Where(s => all || s.OwnerId == me || s.Recipients.Any(r => r.UserId == me))
            .OrderBy(s => s.Name).Take(500).ToListAsync(cancellationToken);
        return TypedResults.Ok(await ResponsesAsync(db, rows, user, cancellationToken));
    }

    internal static async Task<IResult> GetAsync(Guid id, ZelvoDbContext db, CurrentUser user, CancellationToken cancellationToken)
    {
        var schedule = await VisibleAsync(db, user, id, cancellationToken);
        return schedule is null ? NotFound() : TypedResults.Ok((await ResponsesAsync(db, [schedule], user, cancellationToken))[0]);
    }

    internal static async Task<IResult> CreateAsync(
        ReportScheduleRequest request, ZelvoDbContext db, CurrentUser user, ReportBuilder builder, TimeProvider time, CancellationToken cancellationToken)
    {
        if (!MaySchedule(user))
        {
            return Forbidden();
        }

        var schedule = new ReportSchedule { Name = string.Empty, Report = string.Empty, Filters = "{}", OwnerId = user.Account!.Id };
        if (await ApplyAsync(schedule, request, db, user, builder, time, cancellationToken) is { } problem)
        {
            return problem;
        }

        db.ReportSchedules.Add(schedule);
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Created($"/api/v1/reports/schedules/{schedule.Id}", (await ResponsesAsync(db, [schedule], user, cancellationToken))[0]);
    }

    internal static async Task<IResult> UpdateAsync(
        Guid id, ReportScheduleRequest request, ZelvoDbContext db, CurrentUser user, ReportBuilder builder, TimeProvider time, CancellationToken cancellationToken)
    {
        var schedule = await EditableAsync(db, user, id, cancellationToken);
        if (schedule is null)
        {
            return NotFound();
        }

        if (await ApplyAsync(schedule, request, db, user, builder, time, cancellationToken) is { } problem)
        {
            return problem;
        }

        schedule.UpdatedAt = time.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Ok((await ResponsesAsync(db, [schedule], user, cancellationToken))[0]);
    }

    internal static async Task<IResult> DeleteAsync(Guid id, ZelvoDbContext db, CurrentUser user, CancellationToken cancellationToken)
    {
        var schedule = await EditableAsync(db, user, id, cancellationToken);
        if (schedule is null)
        {
            return NotFound();
        }

        db.ReportSchedules.Remove(schedule);
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.NoContent();
    }

    internal static async Task<IResult> SendNowAsync(
        Guid id, ZelvoDbContext db, CurrentUser user, ReportBuilder builder, IAuditLog audit, TimeProvider time, CancellationToken cancellationToken)
    {
        var schedule = await EditableAsync(db, user, id, cancellationToken);
        if (schedule is null)
        {
            return NotFound();
        }

        var now = time.GetUtcNow();
        var (from, to) = ReportScheduleTimes.Period(schedule.Frequency, now, await builder.TimeZoneAsync(cancellationToken));
        foreach (var recipient in schedule.Recipients)
        {
            db.ReportDeliveries.Add(new ReportDelivery { ScheduleId = schedule.Id, RecipientId = recipient.UserId, RunAt = now, PeriodFrom = from, PeriodTo = to, NextAttemptAt = now });
        }

        audit.Add(AuditCategory.Admin, "report.schedule.sent-now", "report_schedule", schedule.Id.ToString(),
            new Dictionary<string, object?> { ["recipients"] = schedule.Recipients.Count });
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Accepted($"/api/v1/reports/schedules/{schedule.Id}/deliveries", (await ResponsesAsync(db, [schedule], user, cancellationToken))[0]);
    }

    internal static async Task<IResult> DeliveriesAsync(Guid id, ZelvoDbContext db, CurrentUser user, CancellationToken cancellationToken)
    {
        var schedule = await VisibleAsync(db, user, id, cancellationToken);
        if (schedule is null)
        {
            return NotFound();
        }

        // Recipients who only receive the report see their own emails.
        var me = user.Account!.Id;
        var own = schedule.OwnerId != me && !user.Has(Permissions.ReportsManage);
        var rows = await db.ReportDeliveries.AsNoTracking().Where(d => d.ScheduleId == id && (!own || d.RecipientId == me))
            .OrderByDescending(d => d.CreatedAt).Take(100).ToListAsync(cancellationToken);
        var ids = rows.Select(r => r.RecipientId).Distinct().ToList();
        var names = await db.Users.AsNoTracking().Where(u => ids.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName ?? u.Email ?? string.Empty, cancellationToken);
        return TypedResults.Ok(rows.Select(d => new ReportDeliveryResponse(d.Id, d.RecipientId, names.GetValueOrDefault(d.RecipientId) ?? string.Empty, d.RunAt,
            d.PeriodFrom, d.PeriodTo, d.Status, d.Attempts, d.Error, d.SentAt, d.CreatedAt)).ToList());
    }

    internal static async Task<IResult> RecipientsAsync(string? search, ZelvoDbContext db, CancellationToken cancellationToken)
    {
        var term = search?.Trim();
        var holders = db.RoleGrants.Where(g => g.Permission == Permissions.ReportsRead || g.Permission == Permissions.ReportsAll).Select(g => g.RoleId);
        var query = db.Users.AsNoTracking().Where(u => u.Status == UserStatus.Active && u.Roles.Any(r => holders.Contains(r.RoleId)));
        if (!string.IsNullOrEmpty(term))
        {
            var pattern = "%" + term.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal) + "%";
            query = query.Where(u => EF.Functions.ILike(u.DisplayName ?? "", pattern) || EF.Functions.ILike(u.Email ?? "", pattern));
        }

        var people = await query.OrderBy(u => u.DisplayName).Take(50).Select(u => new ReportRecipientResponse(u.Id, u.DisplayName ?? u.Email ?? string.Empty)).ToListAsync(cancellationToken);
        return TypedResults.Ok(people);
    }

    // Validates the request and applies it; a problem or null.
    private static async Task<IResult?> ApplyAsync(
        ReportSchedule schedule, ReportScheduleRequest request, ZelvoDbContext db, CurrentUser user, ReportBuilder builder, TimeProvider time, CancellationToken cancellationToken)
    {
        var name = request.Name?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > ReportSchedule.NameMaxLength)
        {
            return UserEndpoints.Validation("name", $"Name the schedule (up to {ReportSchedule.NameMaxLength} characters).");
        }

        if (!Enum.TryParse<ReportKind>(request.Report, ignoreCase: false, out var kind) || !Enum.IsDefined(kind))
        {
            return UserEndpoints.Validation("report", "Choose Overview, Volumes, Backlog, Sla, Agents or Happiness.");
        }

        if (request.Frequency is not { } frequency || !Enum.IsDefined(frequency))
        {
            return UserEndpoints.Validation("frequency", "Choose Daily, Weekly or Monthly.");
        }

        int? dayOfWeek = frequency == ReportFrequency.Weekly ? request.DayOfWeek : null;
        int? dayOfMonth = frequency == ReportFrequency.Monthly ? request.DayOfMonth : null;
        if (frequency == ReportFrequency.Weekly && dayOfWeek is not (>= 1 and <= 7))
        {
            return UserEndpoints.Validation("dayOfWeek", "Choose a day of the week, 1 (Monday) to 7 (Sunday).");
        }

        if (frequency == ReportFrequency.Monthly && dayOfMonth is not (>= 1 and <= 28))
        {
            return UserEndpoints.Validation("dayOfMonth", "Choose a day of the month from 1 to 28.");
        }

        if (!TimeOnly.TryParseExact(request.Time ?? "", "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var at))
        {
            return UserEndpoints.Validation("time", "Give the time as HH:mm, for example 07:30.");
        }

        var types = request.Types ?? [];
        if (types.Any(t => !Enum.IsDefined(t)) || (request.Groups?.Count ?? 0) > ReportFilter.MaxIds || (request.Categories?.Count ?? 0) > ReportFilter.MaxIds)
        {
            return UserEndpoints.Validation("types", "Choose known record types and at most 50 groups and 50 categories.");
        }

        var groups = request.Groups?.Distinct().ToList() ?? [];
        var categories = request.Categories?.Distinct().ToList() ?? [];
        if (await db.SupportGroups.CountAsync(g => groups.Contains(g.Id), cancellationToken) != groups.Count
            || await db.Categories.CountAsync(c => categories.Contains(c.Id), cancellationToken) != categories.Count)
        {
            return UserEndpoints.Validation("groups", "A group or category does not exist.");
        }

        var me = user.Account!.Id;
        var recipients = request.RecipientIds?.Distinct().ToList() ?? [me];
        if (recipients.Count == 0 || recipients.Count > ReportSchedule.MaxRecipients)
        {
            return UserEndpoints.Validation("recipientIds", $"Choose 1–{ReportSchedule.MaxRecipients} recipients.");
        }

        if (!user.Has(Permissions.ReportsManage) && recipients.Any(r => r != me))
        {
            return UserEndpoints.Validation("recipientIds", "You can schedule reports for yourself only.");
        }

        foreach (var recipient in recipients)
        {
            if (!ReportAccess.MayReceive(await ReportAccess.PermissionsOfAsync(db, recipient, cancellationToken)))
            {
                return UserEndpoints.Validation("recipientIds", "Every recipient must be an active user who may see reports.");
            }
        }

        schedule.Name = name;
        schedule.Report = kind.ToString();
        schedule.Filters = new ScheduleFilters([.. types.Distinct()], groups, categories).ToJson();
        schedule.Frequency = frequency;
        schedule.DayOfWeek = dayOfWeek;
        schedule.DayOfMonth = dayOfMonth;
        schedule.TimeOfDay = at.Hour * 60 + at.Minute;
        schedule.Active = request.Active ?? true;
        schedule.NextRunAt = schedule.Active
            ? ReportScheduleTimes.NextRun(frequency, dayOfWeek, dayOfMonth, schedule.TimeOfDay, await builder.TimeZoneAsync(cancellationToken), time.GetUtcNow())
            : null;
        schedule.Recipients.RemoveAll(r => !recipients.Contains(r.UserId));
        foreach (var recipient in recipients.Where(r => schedule.Recipients.All(x => x.UserId != r)))
        {
            schedule.Recipients.Add(new ReportScheduleRecipient { ScheduleId = schedule.Id, UserId = recipient });
        }

        return null;
    }

    private static bool MaySchedule(CurrentUser user) => user.Has(Permissions.ReportsRead) || user.Has(Permissions.ReportsAll);

    private static Task<ReportSchedule?> VisibleAsync(ZelvoDbContext db, CurrentUser user, Guid id, CancellationToken cancellationToken)
    {
        var me = user.Account!.Id;
        var all = user.Has(Permissions.ReportsManage);
        return db.ReportSchedules.Include(s => s.Recipients)
            .SingleOrDefaultAsync(s => s.Id == id && (all || s.OwnerId == me || s.Recipients.Any(r => r.UserId == me)), cancellationToken);
    }

    private static Task<ReportSchedule?> EditableAsync(ZelvoDbContext db, CurrentUser user, Guid id, CancellationToken cancellationToken)
    {
        var me = user.Account!.Id;
        var all = user.Has(Permissions.ReportsManage);
        var own = MaySchedule(user);
        return db.ReportSchedules.Include(s => s.Recipients).SingleOrDefaultAsync(s => s.Id == id && (all || (own && s.OwnerId == me)), cancellationToken);
    }

    private static async Task<List<ReportScheduleResponse>> ResponsesAsync(ZelvoDbContext db, IReadOnlyList<ReportSchedule> schedules, CurrentUser user, CancellationToken cancellationToken)
    {
        var ids = schedules.SelectMany(s => s.Recipients.Select(r => r.UserId).Append(s.OwnerId)).Distinct().ToList();
        var names = await db.Users.AsNoTracking().Where(u => ids.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName ?? u.Email ?? string.Empty, cancellationToken);
        var me = user.Account!.Id;
        var all = user.Has(Permissions.ReportsManage);
        return [.. schedules.Select(s =>
        {
            var filters = ScheduleFilters.FromJson(s.Filters);
            return new ReportScheduleResponse(s.Id, s.Name, s.Report, s.Frequency, s.DayOfWeek, s.DayOfMonth,
                $"{s.TimeOfDay / 60:00}:{s.TimeOfDay % 60:00}", filters.Types, filters.Groups, filters.Categories, s.Active,
                new ReportRecipientResponse(s.OwnerId, names.GetValueOrDefault(s.OwnerId) ?? string.Empty),
                [.. s.Recipients.Select(r => new ReportRecipientResponse(r.UserId, names.GetValueOrDefault(r.UserId) ?? string.Empty)).OrderBy(r => r.Name, StringComparer.CurrentCulture)],
                s.NextRunAt, s.LastRunAt, all || (s.OwnerId == me && MaySchedule(user)), s.UpdatedAt);
        })];
    }

    private static IResult NotFound() => ApiProblem.Create(StatusCodes.Status404NotFound, "schedule-not-found", "There is no such scheduled report, or you cannot see it.");

    private static IResult Forbidden() => ApiProblem.Create(StatusCodes.Status403Forbidden, "forbidden", "Scheduling reports needs reports.read or reports.all.");
}

/// <summary>A scheduled report to create or change.</summary>
internal sealed class ReportScheduleRequest
{
    /// <summary>Name.</summary>
    [StringLength(ReportSchedule.NameMaxLength)]
    public string? Name { get; init; }

    /// <summary>Overview, Volumes, Backlog, Sla, Agents or Happiness.</summary>
    [StringLength(20)]
    public string? Report { get; init; }

    /// <summary>Daily, Weekly or Monthly.</summary>
    public ReportFrequency? Frequency { get; init; }

    /// <summary>Weekly: 1 (Monday) to 7 (Sunday).</summary>
    public int? DayOfWeek { get; init; }

    /// <summary>Monthly: 1–28.</summary>
    public int? DayOfMonth { get; init; }

    /// <summary>HH:mm in the tenant's time zone.</summary>
    [StringLength(5)]
    public string? Time { get; init; }

    /// <summary>Record types; none for all.</summary>
    public IReadOnlyList<TicketType>? Types { get; init; }

    /// <summary>Groups; none for all.</summary>
    public IReadOnlyList<Guid>? Groups { get; init; }

    /// <summary>Categories; none for all.</summary>
    public IReadOnlyList<Guid>? Categories { get; init; }

    /// <summary>Recipients; the caller when left out.</summary>
    public IReadOnlyList<Guid>? RecipientIds { get; init; }

    /// <summary>Whether it goes out (default true).</summary>
    public bool? Active { get; init; }
}

/// <summary>A person of a schedule.</summary>
internal sealed record ReportRecipientResponse(Guid Id, string Name);

/// <summary>A scheduled report.</summary>
internal sealed record ReportScheduleResponse(
    Guid Id, string Name, string Report, ReportFrequency Frequency, int? DayOfWeek, int? DayOfMonth, string Time, IReadOnlyList<TicketType> Types,
    IReadOnlyList<Guid> Groups, IReadOnlyList<Guid> Categories, bool Active, ReportRecipientResponse Owner, IReadOnlyList<ReportRecipientResponse> Recipients,
    DateTimeOffset? NextRunAt, DateTimeOffset? LastRunAt, bool CanEdit, DateTimeOffset UpdatedAt);

/// <summary>One email of the delivery log.</summary>
internal sealed record ReportDeliveryResponse(
    Guid Id, Guid RecipientId, string RecipientName, DateTimeOffset RunAt, DateOnly PeriodFrom, DateOnly PeriodTo, ReportDeliveryStatus Status, int Attempts,
    string? Error, DateTimeOffset? SentAt, DateTimeOffset CreatedAt);
