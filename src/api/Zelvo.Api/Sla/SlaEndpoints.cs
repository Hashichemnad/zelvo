using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Zelvo.Api.Conventions;
using Zelvo.Api.Identity;
using Zelvo.Api.ServiceDesk;
using Zelvo.Api.Tenancy;
using Zelvo.Data;
using Zelvo.Data.Directory;
using Zelvo.Data.Identity;
using Zelvo.Data.Localization;
using Zelvo.Data.Sla;
using Zelvo.Data.Tickets;
using Zelvo.Tickets.Sla;

namespace Zelvo.Api.Sla;

/// <summary>
/// SLA management (module 8, GOV-7; task B3): business calendars with UAE weekend patterns and
/// public holidays, customer groups, SLA policies with targets per priority and escalation
/// thresholds, and the calendars of groups. Staff with <c>directory.read</c> read them;
/// <c>sla.manage</c> changes them. Every change is audited and applies to open tickets within moments.
/// </summary>
internal static partial class SlaEndpoints
{
    private const int MaxPeriods = 28;
    private const int MaxHolidays = 400;
    private const int MaxEscalations = 10;

    public static RouteGroupBuilder MapSlaEndpoints(this RouteGroupBuilder group)
    {
        var sla = group.MapGroup("/sla").WithTags("SLA").RequireTenant();

        sla.MapGet("/calendar-templates", Templates).WithName("ListCalendarTemplates")
            .WithSummary("Ready-made weekly patterns: UAE government (Monday–Thursday 07:30–15:30, Friday 07:30–12:00), UAE private sector (Monday–Friday 09:00–18:00) and around the clock. Needs directory.read.")
            .RequirePermission(Permissions.DirectoryRead).Produces<IReadOnlyList<CalendarTemplateResponse>>();
        sla.MapGet("/calendars", ListCalendarsAsync).WithName("ListBusinessCalendars")
            .WithSummary("Business calendars with their working periods and holidays. Needs directory.read.")
            .RequirePermission(Permissions.DirectoryRead).Produces<IReadOnlyList<CalendarResponse>>();
        sla.MapGet("/calendars/{id:guid}", GetCalendarAsync).WithName("GetBusinessCalendar")
            .WithSummary("A business calendar. Needs directory.read.").RequirePermission(Permissions.DirectoryRead).Produces<CalendarResponse>();
        sla.MapPost("/calendars", CreateCalendarAsync).WithName("CreateBusinessCalendar")
            .WithSummary("Add a calendar: name, IANA time zone (Asia/Dubai by default) and working periods per day. The first calendar becomes the default. Needs sla.manage; audited.")
            .RequirePermission(Permissions.SlaManage).Produces<CalendarResponse>(StatusCodes.Status201Created).ProducesValidationProblem();
        sla.MapPut("/calendars/{id:guid}", UpdateCalendarAsync).WithName("UpdateBusinessCalendar")
            .WithSummary("Replace a calendar's name, time zone, working periods and default flag. Needs sla.manage; audited.")
            .RequirePermission(Permissions.SlaManage).Produces<CalendarResponse>().ProducesValidationProblem();
        sla.MapDelete("/calendars/{id:guid}", DeleteCalendarAsync).WithName("DeleteBusinessCalendar")
            .WithSummary("Delete a calendar that is not the default and that no group or policy uses. Needs sla.manage; audited.")
            .RequirePermission(Permissions.SlaManage).Produces(StatusCodes.Status204NoContent);
        sla.MapPost("/calendars/{id:guid}/holidays", AddHolidayAsync).WithName("AddCalendarHoliday")
            .WithSummary("Add a public holiday or other days off (first and last day, both included). Needs sla.manage; audited.")
            .RequirePermission(Permissions.SlaManage).Produces<CalendarResponse>(StatusCodes.Status201Created).ProducesValidationProblem();
        sla.MapPut("/calendars/{id:guid}/holidays/{holidayId:guid}", UpdateHolidayAsync).WithName("UpdateCalendarHoliday")
            .WithSummary("Change a holiday. Needs sla.manage; audited.")
            .RequirePermission(Permissions.SlaManage).Produces<CalendarResponse>().ProducesValidationProblem();
        sla.MapDelete("/calendars/{id:guid}/holidays/{holidayId:guid}", DeleteHolidayAsync).WithName("DeleteCalendarHoliday")
            .WithSummary("Remove a holiday. Needs sla.manage; audited.")
            .RequirePermission(Permissions.SlaManage).Produces(StatusCodes.Status204NoContent);

        sla.MapGet("/customer-groups", ListCustomerGroupsAsync).WithName("ListCustomerGroups")
            .WithSummary("Customer groups with their email domains and requesters added by hand. Needs directory.read.")
            .RequirePermission(Permissions.DirectoryRead).Produces<IReadOnlyList<CustomerGroupResponse>>();
        sla.MapPost("/customer-groups", CreateCustomerGroupAsync).WithName("CreateCustomerGroup")
            .WithSummary("Add a customer group: name and email domains whose requesters belong to it. Needs sla.manage; audited.")
            .RequirePermission(Permissions.SlaManage).Produces<CustomerGroupResponse>(StatusCodes.Status201Created).ProducesValidationProblem();
        sla.MapPut("/customer-groups/{id:guid}", UpdateCustomerGroupAsync).WithName("UpdateCustomerGroup")
            .WithSummary("Replace a customer group's name, active flag and email domains. Needs sla.manage; audited.")
            .RequirePermission(Permissions.SlaManage).Produces<CustomerGroupResponse>().ProducesValidationProblem();
        sla.MapDelete("/customer-groups/{id:guid}", DeleteCustomerGroupAsync).WithName("DeleteCustomerGroup")
            .WithSummary("Delete a customer group that no policy uses. Needs sla.manage; audited.")
            .RequirePermission(Permissions.SlaManage).Produces(StatusCodes.Status204NoContent);
        sla.MapPut("/customer-groups/{id:guid}/members/{userId:guid}", AddCustomerAsync).WithName("AddCustomerGroupMember")
            .WithSummary("Add a requester to a customer group by hand. Needs sla.manage; audited.")
            .RequirePermission(Permissions.SlaManage).Produces<CustomerGroupResponse>().ProducesValidationProblem();
        sla.MapDelete("/customer-groups/{id:guid}/members/{userId:guid}", RemoveCustomerAsync).WithName("RemoveCustomerGroupMember")
            .WithSummary("Remove a requester added by hand. Needs sla.manage; audited.")
            .RequirePermission(Permissions.SlaManage).Produces<CustomerGroupResponse>();

        sla.MapGet("/policies", ListPoliciesAsync).WithName("ListSlaPolicies")
            .WithSummary("SLA policies in the order they are tried; the first match wins. Needs directory.read.")
            .RequirePermission(Permissions.DirectoryRead).Produces<IReadOnlyList<PolicyResponse>>();
        sla.MapPost("/policies", CreatePolicyAsync).WithName("CreateSlaPolicy")
            .WithSummary("Add a policy; it is tried last. Needs sla.manage; audited.")
            .RequirePermission(Permissions.SlaManage).Produces<PolicyResponse>(StatusCodes.Status201Created).ProducesValidationProblem();
        sla.MapPut("/policies/order", OrderPoliciesAsync).WithName("OrderSlaPolicies")
            .WithSummary("Set the order of all policies (every policy exactly once). Needs sla.manage; audited.")
            .RequirePermission(Permissions.SlaManage).Produces<IReadOnlyList<PolicyResponse>>().ProducesValidationProblem();
        sla.MapPost("/policies/test", TestAsync).WithName("TestSlaPolicies")
            .WithSummary("Which policy, calendar and targets a ticket with these details would get now, and when its targets would fall due. Changes nothing. Needs directory.read.")
            .RequirePermission(Permissions.DirectoryRead).Produces<SlaPreviewResponse>().ProducesValidationProblem();
        sla.MapPut("/policies/{id:guid}", UpdatePolicyAsync).WithName("UpdateSlaPolicy")
            .WithSummary("Replace a policy's name, conditions, calendar, targets and escalations. Needs sla.manage; audited.")
            .RequirePermission(Permissions.SlaManage).Produces<PolicyResponse>().ProducesValidationProblem();
        sla.MapDelete("/policies/{id:guid}", DeletePolicyAsync).WithName("DeleteSlaPolicy")
            .WithSummary("Delete a policy; open tickets it applied to get the next matching one. Needs sla.manage; audited.")
            .RequirePermission(Permissions.SlaManage).Produces(StatusCodes.Status204NoContent);

        group.MapPut("/groups/{id:guid}/calendar", SetGroupCalendarAsync).WithTags("SLA").RequireTenant().WithName("SetGroupCalendar")
            .WithSummary("The team's own business hours for SLA clocks (null: the policy's or the tenant default). Needs sla.manage; audited.")
            .RequirePermission(Permissions.SlaManage).Produces<GroupCalendarResponse>().ProducesValidationProblem();
        return group;
    }

    // --- Calendars ---

    internal static Ok<List<CalendarTemplateResponse>> Templates() =>
        TypedResults.Ok(CalendarTemplate.All.Select(t => new CalendarTemplateResponse(t.Key,
            [.. t.Periods.Select(p => PeriodDto.From(p.Day, p.Period.Start, p.Period.End))])).ToList());

    internal static async Task<Ok<List<CalendarResponse>>> ListCalendarsAsync(ZelvoDbContext db, CancellationToken cancellationToken) =>
        TypedResults.Ok(await CalendarsAsync(db, null, cancellationToken));

    internal static async Task<Results<Ok<CalendarResponse>, ProblemHttpResult>> GetCalendarAsync(Guid id, ZelvoDbContext db, CancellationToken cancellationToken) =>
        (await CalendarsAsync(db, id, cancellationToken)).SingleOrDefault() is { } calendar ? TypedResults.Ok(calendar) : DirectoryProblems.NotFound("calendar");

    internal static async Task<Results<Created<CalendarResponse>, ValidationProblem, ProblemHttpResult>> CreateCalendarAsync(
        CalendarInput request, ZelvoDbContext db, CancellationToken cancellationToken)
    {
        if (await ValidateCalendarAsync(request, db, null, cancellationToken) is { } problem)
        {
            return problem;
        }

        var calendar = new BusinessCalendar { Name = DirectoryProblems.Clean(request.Name!), TimeZone = request.TimeZone!.Trim() };
        var first = !await db.BusinessCalendars.AnyAsync(cancellationToken);
        await SetDefaultAsync(db, calendar, request.IsDefault == true || first, cancellationToken);
        foreach (var period in request.Periods!.Select(p => p.ToValue()!.Value))
        {
            calendar.Periods.Add(new BusinessCalendarPeriod { CalendarId = calendar.Id, Day = period.Day, StartMinute = period.Period.Start, EndMinute = period.Period.End });
        }

        db.BusinessCalendars.Add(calendar);
        await SaveAndRecheckAsync(db, cancellationToken);
        return TypedResults.Created($"/api/v1/sla/calendars/{calendar.Id}", (await CalendarsAsync(db, calendar.Id, cancellationToken)).Single());
    }

    internal static async Task<Results<Ok<CalendarResponse>, ValidationProblem, ProblemHttpResult>> UpdateCalendarAsync(
        Guid id, CalendarInput request, ZelvoDbContext db, CancellationToken cancellationToken)
    {
        var calendar = await db.BusinessCalendars.Include(c => c.Periods).SingleOrDefaultAsync(c => c.Id == id, cancellationToken);
        if (calendar is null)
        {
            return DirectoryProblems.NotFound("calendar");
        }

        if (await ValidateCalendarAsync(request, db, calendar, cancellationToken) is { } problem)
        {
            return problem;
        }

        if (calendar.IsDefault && request.IsDefault == false)
        {
            return UserEndpoints.Validation("isDefault", "Make another calendar the default instead.");
        }

        calendar.Name = DirectoryProblems.Clean(request.Name!);
        calendar.TimeZone = request.TimeZone!.Trim();
        await SetDefaultAsync(db, calendar, request.IsDefault == true || calendar.IsDefault, cancellationToken);
        var wanted = request.Periods!.Select(p => p.ToValue()!.Value).ToList();
        var current = calendar.Periods.Select(p => (Day: p.Day, Period: new WorkingPeriod(p.StartMinute, p.EndMinute))).ToList();
        if (!wanted.OrderBy(p => p.Day).ThenBy(p => p.Period.Start).SequenceEqual(current.OrderBy(p => p.Day).ThenBy(p => p.Period.Start)))
        {
            db.BusinessCalendarPeriods.RemoveRange(calendar.Periods);
            foreach (var (day, period) in wanted)
            {
                db.BusinessCalendarPeriods.Add(new BusinessCalendarPeriod { CalendarId = calendar.Id, Day = day, StartMinute = period.Start, EndMinute = period.End });
            }
        }

        calendar.UpdatedAt = DateTimeOffset.UtcNow;
        await SaveAndRecheckAsync(db, cancellationToken);
        return TypedResults.Ok((await CalendarsAsync(db, id, cancellationToken)).Single());
    }

    internal static async Task<Results<NoContent, ProblemHttpResult>> DeleteCalendarAsync(Guid id, ZelvoDbContext db, CancellationToken cancellationToken)
    {
        var calendar = await db.BusinessCalendars.SingleOrDefaultAsync(c => c.Id == id, cancellationToken);
        if (calendar is null)
        {
            return DirectoryProblems.NotFound("calendar");
        }

        if (calendar.IsDefault)
        {
            return DirectoryProblems.InUse("This is the default calendar; make another calendar the default first.");
        }

        if (await db.SupportGroups.AnyAsync(g => g.CalendarId == id, cancellationToken) || await db.SlaPolicies.AnyAsync(p => p.CalendarId == id, cancellationToken))
        {
            return DirectoryProblems.InUse("Groups or SLA policies use this calendar; change them first.");
        }

        db.BusinessCalendars.Remove(calendar);
        await SaveAndRecheckAsync(db, cancellationToken);
        return TypedResults.NoContent();
    }

    internal static async Task<Results<Created<CalendarResponse>, ValidationProblem, ProblemHttpResult>> AddHolidayAsync(
        Guid id, HolidayInput request, ZelvoDbContext db, CancellationToken cancellationToken)
    {
        if (!await db.BusinessCalendars.AnyAsync(c => c.Id == id, cancellationToken))
        {
            return DirectoryProblems.NotFound("calendar");
        }

        if (ValidateHoliday(request) is { } problem)
        {
            return problem;
        }

        if (await db.BusinessCalendarHolidays.CountAsync(h => h.CalendarId == id, cancellationToken) >= MaxHolidays)
        {
            return UserEndpoints.Validation("startDate", $"A calendar has at most {MaxHolidays} holidays; remove old ones first.");
        }

        db.BusinessCalendarHolidays.Add(new BusinessCalendarHoliday
        {
            CalendarId = id, StartDate = request.StartDate!.Value, EndDate = request.EndDate ?? request.StartDate.Value, Name = DirectoryProblems.Clean(request.Name!),
        });
        await TouchCalendarAsync(db, id, cancellationToken);
        await SaveAndRecheckAsync(db, cancellationToken);
        return TypedResults.Created($"/api/v1/sla/calendars/{id}", (await CalendarsAsync(db, id, cancellationToken)).Single());
    }

    internal static async Task<Results<Ok<CalendarResponse>, ValidationProblem, ProblemHttpResult>> UpdateHolidayAsync(
        Guid id, Guid holidayId, HolidayInput request, ZelvoDbContext db, CancellationToken cancellationToken)
    {
        var holiday = await db.BusinessCalendarHolidays.SingleOrDefaultAsync(h => h.Id == holidayId && h.CalendarId == id, cancellationToken);
        if (holiday is null)
        {
            return DirectoryProblems.NotFound("holiday");
        }

        if (ValidateHoliday(request) is { } problem)
        {
            return problem;
        }

        holiday.StartDate = request.StartDate!.Value;
        holiday.EndDate = request.EndDate ?? request.StartDate.Value;
        holiday.Name = DirectoryProblems.Clean(request.Name!);
        await TouchCalendarAsync(db, id, cancellationToken);
        await SaveAndRecheckAsync(db, cancellationToken);
        return TypedResults.Ok((await CalendarsAsync(db, id, cancellationToken)).Single());
    }

    internal static async Task<Results<NoContent, ProblemHttpResult>> DeleteHolidayAsync(
        Guid id, Guid holidayId, ZelvoDbContext db, CancellationToken cancellationToken)
    {
        var holiday = await db.BusinessCalendarHolidays.SingleOrDefaultAsync(h => h.Id == holidayId && h.CalendarId == id, cancellationToken);
        if (holiday is null)
        {
            return DirectoryProblems.NotFound("holiday");
        }

        db.BusinessCalendarHolidays.Remove(holiday);
        await TouchCalendarAsync(db, id, cancellationToken);
        await SaveAndRecheckAsync(db, cancellationToken);
        return TypedResults.NoContent();
    }

    private static async Task TouchCalendarAsync(ZelvoDbContext db, Guid id, CancellationToken cancellationToken)
    {
        var calendar = await db.BusinessCalendars.SingleAsync(c => c.Id == id, cancellationToken);
        calendar.UpdatedAt = DateTimeOffset.UtcNow;
    }

    private static async Task SetDefaultAsync(ZelvoDbContext db, BusinessCalendar calendar, bool isDefault, CancellationToken cancellationToken)
    {
        if (isDefault && !calendar.IsDefault)
        {
            foreach (var other in await db.BusinessCalendars.Where(c => c.IsDefault && c.Id != calendar.Id).ToListAsync(cancellationToken))
            {
                other.IsDefault = false;
                other.UpdatedAt = DateTimeOffset.UtcNow;
            }

            // The unique index allows one default; clear the old one before setting the new one.
            await db.SaveChangesAsync(cancellationToken);
        }

        calendar.IsDefault = isDefault;
    }

    private static async Task<ValidationProblem?> ValidateCalendarAsync(CalendarInput request, ZelvoDbContext db, BusinessCalendar? existing, CancellationToken cancellationToken)
    {
        var name = DirectoryProblems.Clean(request.Name!);
        if (!DirectoryProblems.Fits(name, BusinessCalendar.NameMaxLength))
        {
            return UserEndpoints.Validation("name", $"Use at most {BusinessCalendar.NameMaxLength} characters in each language.");
        }

        if ((await db.BusinessCalendars.AsNoTracking().Where(c => existing == null || c.Id != existing.Id).ToListAsync(cancellationToken)).Any(c => DirectoryProblems.SameName(c.Name, name)))
        {
            return UserEndpoints.Validation("name", "Another calendar already has this name in English or Arabic.");
        }

        if (WorkingCalendar.FindTimeZone(request.TimeZone?.Trim()) is null)
        {
            return UserEndpoints.Validation("timeZone", "Use an IANA time zone, for example Asia/Dubai.");
        }

        var periods = request.Periods!;
        if (periods.Count == 0 || periods.Count > MaxPeriods)
        {
            return UserEndpoints.Validation("periods", $"Give 1–{MaxPeriods} working periods.");
        }

        if (periods.Any(p => p.ToValue() is null))
        {
            return UserEndpoints.Validation("periods", "Give each period a day and a start and end time (HH:mm, 00:00–24:00) with the end after the start.");
        }

        var values = periods.Select(p => p.ToValue()!.Value).ToList();
        foreach (var day in values.GroupBy(v => v.Day))
        {
            var sorted = day.OrderBy(v => v.Period.Start).ToList();
            if (sorted.Zip(sorted.Skip(1)).Any(pair => pair.Second.Period.Start < pair.First.Period.End))
            {
                return UserEndpoints.Validation("periods", $"Working periods on {day.Key} overlap.");
            }
        }

        return null;
    }

    private static ValidationProblem? ValidateHoliday(HolidayInput request)
    {
        var name = DirectoryProblems.Clean(request.Name!);
        if (!DirectoryProblems.Fits(name, BusinessCalendarHoliday.NameMaxLength))
        {
            return UserEndpoints.Validation("name", $"Use at most {BusinessCalendarHoliday.NameMaxLength} characters in each language.");
        }

        var start = request.StartDate!.Value;
        var end = request.EndDate ?? start;
        if (end < start || end.DayNumber - start.DayNumber > 60)
        {
            return UserEndpoints.Validation("endDate", "The last day is on or after the first day, at most 60 days later.");
        }

        return start.Year is < 2000 or > 2100 ? UserEndpoints.Validation("startDate", "Use a date between 2000 and 2100.") : null;
    }

    private static async Task<List<CalendarResponse>> CalendarsAsync(ZelvoDbContext db, Guid? id, CancellationToken cancellationToken)
    {
        var calendars = await db.BusinessCalendars.AsNoTracking().Where(c => id == null || c.Id == id)
            .Include(c => c.Periods).Include(c => c.Holidays).AsSplitQuery()
            .OrderByDescending(c => c.IsDefault).ThenBy(c => c.CreatedAt)
            .ToListAsync(cancellationToken);
        var groupUses = await db.SupportGroups.AsNoTracking().Where(g => g.CalendarId != null)
            .GroupBy(g => g.CalendarId!.Value).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(g => g.Key, g => g.Count, cancellationToken);
        var policyUses = await db.SlaPolicies.AsNoTracking().Where(p => p.CalendarId != null)
            .GroupBy(p => p.CalendarId!.Value).Select(p => new { p.Key, Count = p.Count() }).ToDictionaryAsync(p => p.Key, p => p.Count, cancellationToken);
        return [.. calendars.Select(c => CalendarResponse.From(c, groupUses.GetValueOrDefault(c.Id), policyUses.GetValueOrDefault(c.Id)))];
    }

    // --- Customer groups ---

    internal static async Task<Ok<List<CustomerGroupResponse>>> ListCustomerGroupsAsync(ZelvoDbContext db, CancellationToken cancellationToken) =>
        TypedResults.Ok(await CustomerGroupsAsync(db, null, cancellationToken));

    internal static async Task<Results<Created<CustomerGroupResponse>, ValidationProblem, ProblemHttpResult>> CreateCustomerGroupAsync(
        CustomerGroupInput request, ZelvoDbContext db, CancellationToken cancellationToken)
    {
        if (await ValidateCustomerGroupAsync(request, db, null, cancellationToken) is { } problem)
        {
            return problem;
        }

        var group = new CustomerGroup { Name = DirectoryProblems.Clean(request.Name!), Active = request.Active ?? true, EmailDomains = CleanDomains(request.EmailDomains) };
        db.CustomerGroups.Add(group);
        await SaveAndRecheckAsync(db, cancellationToken);
        return TypedResults.Created($"/api/v1/sla/customer-groups/{group.Id}", (await CustomerGroupsAsync(db, group.Id, cancellationToken)).Single());
    }

    internal static async Task<Results<Ok<CustomerGroupResponse>, ValidationProblem, ProblemHttpResult>> UpdateCustomerGroupAsync(
        Guid id, CustomerGroupInput request, ZelvoDbContext db, CancellationToken cancellationToken)
    {
        var group = await db.CustomerGroups.SingleOrDefaultAsync(g => g.Id == id, cancellationToken);
        if (group is null)
        {
            return DirectoryProblems.NotFound("customer-group");
        }

        if (await ValidateCustomerGroupAsync(request, db, group, cancellationToken) is { } problem)
        {
            return problem;
        }

        group.Name = DirectoryProblems.Clean(request.Name!);
        group.Active = request.Active ?? group.Active;
        var domains = CleanDomains(request.EmailDomains);
        if (!domains.SequenceEqual(group.EmailDomains))
        {
            group.EmailDomains = domains;
        }

        group.UpdatedAt = DateTimeOffset.UtcNow;
        await SaveAndRecheckAsync(db, cancellationToken);
        return TypedResults.Ok((await CustomerGroupsAsync(db, id, cancellationToken)).Single());
    }

    internal static async Task<Results<NoContent, ProblemHttpResult>> DeleteCustomerGroupAsync(Guid id, ZelvoDbContext db, CancellationToken cancellationToken)
    {
        var group = await db.CustomerGroups.Include(g => g.Members).SingleOrDefaultAsync(g => g.Id == id, cancellationToken);
        if (group is null)
        {
            return DirectoryProblems.NotFound("customer-group");
        }

        if (await db.SlaPolicies.AnyAsync(p => p.CustomerGroupId == id, cancellationToken))
        {
            return DirectoryProblems.InUse("SLA policies use this customer group; change them or make the group inactive.");
        }

        db.CustomerGroups.Remove(group);
        await SaveAndRecheckAsync(db, cancellationToken);
        return TypedResults.NoContent();
    }

    internal static async Task<Results<Ok<CustomerGroupResponse>, ValidationProblem, ProblemHttpResult>> AddCustomerAsync(
        Guid id, Guid userId, ZelvoDbContext db, CancellationToken cancellationToken)
    {
        var group = await db.CustomerGroups.Include(g => g.Members).SingleOrDefaultAsync(g => g.Id == id, cancellationToken);
        if (group is null)
        {
            return DirectoryProblems.NotFound("customer-group");
        }

        if (!await db.Users.AnyAsync(u => u.Id == userId && u.Status == UserStatus.Active, cancellationToken))
        {
            return UserEndpoints.Validation("userId", "Choose an active user of this organisation.");
        }

        if (group.Members.All(m => m.UserId != userId))
        {
            db.CustomerGroupMembers.Add(new CustomerGroupMember { GroupId = id, UserId = userId });
            group.UpdatedAt = DateTimeOffset.UtcNow;
            await SaveAndRecheckAsync(db, cancellationToken);
        }

        return TypedResults.Ok((await CustomerGroupsAsync(db, id, cancellationToken)).Single());
    }

    internal static async Task<Results<Ok<CustomerGroupResponse>, ProblemHttpResult>> RemoveCustomerAsync(
        Guid id, Guid userId, ZelvoDbContext db, CancellationToken cancellationToken)
    {
        var group = await db.CustomerGroups.Include(g => g.Members).SingleOrDefaultAsync(g => g.Id == id, cancellationToken);
        if (group is null)
        {
            return DirectoryProblems.NotFound("customer-group");
        }

        if (group.Members.FirstOrDefault(m => m.UserId == userId) is { } member)
        {
            db.CustomerGroupMembers.Remove(member);
            group.UpdatedAt = DateTimeOffset.UtcNow;
            await SaveAndRecheckAsync(db, cancellationToken);
        }

        return TypedResults.Ok((await CustomerGroupsAsync(db, id, cancellationToken)).Single());
    }

    private static List<string> CleanDomains(IReadOnlyList<string>? domains) =>
        [.. (domains ?? []).Select(d => d.Trim().TrimStart('@').ToLowerInvariant()).Where(d => d.Length > 0).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];

    private static async Task<ValidationProblem?> ValidateCustomerGroupAsync(CustomerGroupInput request, ZelvoDbContext db, CustomerGroup? existing, CancellationToken cancellationToken)
    {
        var name = DirectoryProblems.Clean(request.Name!);
        if (!DirectoryProblems.Fits(name, CustomerGroup.NameMaxLength))
        {
            return UserEndpoints.Validation("name", $"Use at most {CustomerGroup.NameMaxLength} characters in each language.");
        }

        if ((await db.CustomerGroups.AsNoTracking().Where(g => existing == null || g.Id != existing.Id).ToListAsync(cancellationToken)).Any(g => DirectoryProblems.SameName(g.Name, name)))
        {
            return UserEndpoints.Validation("name", "Another customer group already has this name in English or Arabic.");
        }

        var domains = CleanDomains(request.EmailDomains);
        if (domains.Count > CustomerGroup.MaxDomains || domains.Any(d => !DomainPattern().IsMatch(d)))
        {
            return UserEndpoints.Validation("emailDomains", $"Give at most {CustomerGroup.MaxDomains} email domains such as moi.gov.ae (no @ or spaces).");
        }

        return null;
    }

    private static async Task<List<CustomerGroupResponse>> CustomerGroupsAsync(ZelvoDbContext db, Guid? id, CancellationToken cancellationToken)
    {
        var groups = await db.CustomerGroups.AsNoTracking().Where(g => id == null || g.Id == id)
            .Include(g => g.Members).ThenInclude(m => m.User).AsSplitQuery()
            .OrderBy(g => g.CreatedAt).ToListAsync(cancellationToken);
        var uses = await db.SlaPolicies.AsNoTracking().Where(p => p.CustomerGroupId != null)
            .GroupBy(p => p.CustomerGroupId!.Value).Select(p => new { p.Key, Count = p.Count() }).ToDictionaryAsync(p => p.Key, p => p.Count, cancellationToken);
        return [.. groups.Select(g => CustomerGroupResponse.From(g, uses.GetValueOrDefault(g.Id)))];
    }

    [GeneratedRegex("^(?=.{3,253}$)([a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?\\.)+[a-z]{2,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex DomainPattern();

    // --- Policies ---

    internal static async Task<Ok<List<PolicyResponse>>> ListPoliciesAsync(ZelvoDbContext db, CancellationToken cancellationToken) =>
        TypedResults.Ok(await PoliciesAsync(db, cancellationToken));

    internal static async Task<Results<Created<PolicyResponse>, ValidationProblem, ProblemHttpResult>> CreatePolicyAsync(
        PolicyInput request, ZelvoDbContext db, CancellationToken cancellationToken)
    {
        if (await ValidatePolicyAsync(request, db, null, cancellationToken) is { } problem)
        {
            return problem;
        }

        var policy = new SlaPolicy { Name = DirectoryProblems.Clean(request.Name!) };
        policy.Position = await db.SlaPolicies.AnyAsync(cancellationToken) ? await db.SlaPolicies.MaxAsync(p => p.Position, cancellationToken) + 1 : 0;
        ApplyPolicy(db, policy, request);
        db.SlaPolicies.Add(policy);
        await SaveAndRecheckAsync(db, cancellationToken);
        return TypedResults.Created($"/api/v1/sla/policies/{policy.Id}", (await PoliciesAsync(db, cancellationToken)).Single(p => p.Id == policy.Id));
    }

    internal static async Task<Results<Ok<PolicyResponse>, ValidationProblem, ProblemHttpResult>> UpdatePolicyAsync(
        Guid id, PolicyInput request, ZelvoDbContext db, CancellationToken cancellationToken)
    {
        var policy = await db.SlaPolicies.Include(p => p.Targets).Include(p => p.Escalations).AsSplitQuery().SingleOrDefaultAsync(p => p.Id == id, cancellationToken);
        if (policy is null)
        {
            return DirectoryProblems.NotFound("policy");
        }

        if (await ValidatePolicyAsync(request, db, policy, cancellationToken) is { } problem)
        {
            return problem;
        }

        policy.Name = DirectoryProblems.Clean(request.Name!);
        ApplyPolicy(db, policy, request);
        policy.UpdatedAt = DateTimeOffset.UtcNow;
        await SaveAndRecheckAsync(db, cancellationToken);
        return TypedResults.Ok((await PoliciesAsync(db, cancellationToken)).Single(p => p.Id == id));
    }

    internal static async Task<Results<NoContent, ProblemHttpResult>> DeletePolicyAsync(Guid id, ZelvoDbContext db, CancellationToken cancellationToken)
    {
        var policy = await db.SlaPolicies.SingleOrDefaultAsync(p => p.Id == id, cancellationToken);
        if (policy is null)
        {
            return DirectoryProblems.NotFound("policy");
        }

        db.SlaPolicies.Remove(policy);
        await SaveAndRecheckAsync(db, cancellationToken);
        return TypedResults.NoContent();
    }

    internal static async Task<Results<Ok<List<PolicyResponse>>, ValidationProblem>> OrderPoliciesAsync(
        OrderPoliciesRequest request, ZelvoDbContext db, CancellationToken cancellationToken)
    {
        var policies = await db.SlaPolicies.ToListAsync(cancellationToken);
        var ids = request.Ids!;
        if (ids.Count != policies.Count || ids.Distinct().Count() != ids.Count || !policies.All(p => ids.Contains(p.Id)))
        {
            return UserEndpoints.Validation("ids", "List every policy exactly once.");
        }

        foreach (var policy in policies)
        {
            var position = ids.IndexOf(policy.Id);
            if (policy.Position != position)
            {
                policy.Position = position;
                policy.UpdatedAt = DateTimeOffset.UtcNow;
            }
        }

        await SaveAndRecheckAsync(db, cancellationToken);
        return TypedResults.Ok(await PoliciesAsync(db, cancellationToken));
    }

    internal static async Task<Results<Ok<SlaPreviewResponse>, ValidationProblem>> TestAsync(
        SlaTestRequest request, ZelvoDbContext db, CancellationToken cancellationToken)
    {
        if (request.Priority is { } priority && priority is < 1 or > 5)
        {
            return UserEndpoints.Validation("priority", "Use a priority from 1 to 5.");
        }

        var setup = await SlaSetup.LoadAsync(db, cancellationToken);
        var email = request.RequesterId is { } requesterId
            ? await db.Users.AsNoTracking().Where(u => u.Id == requesterId).Select(u => u.Email).SingleOrDefaultAsync(cancellationToken)
            : null;
        var customerGroups = request.RequesterId is { } r ? setup.CustomerGroupsOf(r, email) : [];
        var subject = new SlaSubject(request.RecordType ?? TicketType.Incident, request.Priority ?? 3, setup.CategoryAncestry(request.CategoryId), customerGroups)
        {
            CustomRecordTypeId = request.RecordType == TicketType.Custom ? request.CustomRecordTypeId : null,
        };
        var policy = SlaMatcher.FirstMatch(setup.Policies, subject);
        var calendarId = setup.CalendarIdFor(request.GroupId, policy);
        var calendar = setup.Calendar(calendarId);
        var now = DateTimeOffset.UtcNow;
        SlaPreviewTarget? Target(SlaMetric metric) => SlaMatcher.TargetOf(policy, metric, subject.Priority) is { } minutes
            ? new SlaPreviewTarget(minutes, calendar.Add(now, minutes * 60L))
            : null;
        var names = await db.BusinessCalendars.AsNoTracking().ToDictionaryAsync(c => c.Id, c => c.Name, cancellationToken);
        var groups = await db.CustomerGroups.AsNoTracking().Where(g => customerGroups.Contains(g.Id)).ToListAsync(cancellationToken);
        return TypedResults.Ok(new SlaPreviewResponse(
            policy is null ? null : NamedRef.From(policy.Id, policy.Name),
            calendarId is { } c && names.TryGetValue(c, out var calendarName) ? NamedRef.From(c, calendarName) : null,
            [.. groups.Select(g => NamedRef.From(g.Id, g.Name))],
            Target(SlaMetric.Response), Target(SlaMetric.Resolution)));
    }

    internal static async Task<Results<Ok<GroupCalendarResponse>, ValidationProblem, ProblemHttpResult>> SetGroupCalendarAsync(
        Guid id, GroupCalendarRequest request, ZelvoDbContext db, CancellationToken cancellationToken)
    {
        var group = await db.SupportGroups.SingleOrDefaultAsync(g => g.Id == id, cancellationToken);
        if (group is null)
        {
            return DirectoryProblems.NotFound("group");
        }

        if (request.CalendarId is { } calendarId && !await db.BusinessCalendars.AnyAsync(c => c.Id == calendarId, cancellationToken))
        {
            return UserEndpoints.Validation("calendarId", "Choose one of the business calendars.");
        }

        if (group.CalendarId != request.CalendarId)
        {
            group.CalendarId = request.CalendarId;
            group.UpdatedAt = DateTimeOffset.UtcNow;
            await SaveAndRecheckAsync(db, cancellationToken);
        }

        return TypedResults.Ok(new GroupCalendarResponse(group.Id, group.CalendarId));
    }

    private static void ApplyPolicy(ZelvoDbContext db, SlaPolicy policy, PolicyInput request)
    {
        policy.Active = request.Active ?? true;
        policy.RecordType = request.RecordType;
        policy.CustomRecordTypeId = request.RecordType == TicketType.Custom ? request.CustomRecordTypeId : null;
        policy.CategoryId = request.CategoryId;
        policy.CustomerGroupId = request.CustomerGroupId;
        policy.CalendarId = request.CalendarId;

        var targets = request.Targets!.Where(t => t.ResponseMinutes is not null || t.ResolutionMinutes is not null).ToList();
        foreach (var old in policy.Targets.Where(t => targets.All(n => n.Priority != t.Priority)).ToList())
        {
            db.SlaPolicyTargets.Remove(old);
            policy.Targets.Remove(old);
        }

        foreach (var target in targets)
        {
            if (policy.Targets.FirstOrDefault(t => t.Priority == target.Priority) is { } existing)
            {
                existing.ResponseMinutes = target.ResponseMinutes;
                existing.ResolutionMinutes = target.ResolutionMinutes;
            }
            else
            {
                policy.Targets.Add(new SlaPolicyTarget { PolicyId = policy.Id, Priority = target.Priority!.Value, ResponseMinutes = target.ResponseMinutes, ResolutionMinutes = target.ResolutionMinutes });
            }
        }

        var escalations = request.Escalations ?? [];
        foreach (var old in policy.Escalations.Where(e => escalations.All(n => n.Id != e.Id)).ToList())
        {
            db.SlaEscalations.Remove(old);
            policy.Escalations.Remove(old);
        }

        foreach (var input in escalations)
        {
            var escalation = input.Id is { } escalationId ? policy.Escalations.FirstOrDefault(e => e.Id == escalationId) : null;
            if (escalation is null)
            {
                escalation = new SlaEscalation { PolicyId = policy.Id };
                policy.Escalations.Add(escalation);
            }

            escalation.Metric = input.Metric;
            escalation.Percent = input.Percent!.Value;
            escalation.NotifyAssignee = input.NotifyAssignee;
            escalation.NotifyGroupLeads = input.NotifyGroupLeads;
            escalation.NotifyGroupId = input.NotifyGroupId;
        }
    }

    private static async Task<ValidationProblem?> ValidatePolicyAsync(PolicyInput request, ZelvoDbContext db, SlaPolicy? existing, CancellationToken cancellationToken)
    {
        var name = DirectoryProblems.Clean(request.Name!);
        if (!DirectoryProblems.Fits(name, SlaPolicy.NameMaxLength))
        {
            return UserEndpoints.Validation("name", $"Use at most {SlaPolicy.NameMaxLength} characters in each language.");
        }

        if (request.RecordType is { } type && !Enum.IsDefined(type))
        {
            return UserEndpoints.Validation("recordType", "Use a known record type.");
        }

        // Task K2: a policy for custom records may name one custom record type.
        if (request.RecordType == TicketType.Custom && request.CustomRecordTypeId is { } customType && !await db.RecordTypes.AnyAsync(t => t.Id == customType, cancellationToken))
        {
            return UserEndpoints.Validation("customRecordTypeId", "Choose a custom record type that exists.");
        }

        if (request.CategoryId is { } category && category != existing?.CategoryId && !await TreeEndpoints.IsSelectableAsync(db.Categories, category, cancellationToken))
        {
            return UserEndpoints.Validation("categoryId", "Choose an active category.");
        }

        if (request.CustomerGroupId is { } customerGroup && !await db.CustomerGroups.AnyAsync(g => g.Id == customerGroup, cancellationToken))
        {
            return UserEndpoints.Validation("customerGroupId", "Choose one of the customer groups.");
        }

        if (request.CalendarId is { } calendar && !await db.BusinessCalendars.AnyAsync(c => c.Id == calendar, cancellationToken))
        {
            return UserEndpoints.Validation("calendarId", "Choose one of the business calendars.");
        }

        var targets = request.Targets!;
        if (targets.Any(t => t.Priority is not (>= 1 and <= 5)) || targets.Select(t => t.Priority).Distinct().Count() != targets.Count)
        {
            return UserEndpoints.Validation("targets", "Give each priority from 1 to 5 at most once.");
        }

        if (targets.Any(t => t.ResponseMinutes is not null and not (>= 1 and <= SlaPolicyTarget.MaxMinutes)
                || t.ResolutionMinutes is not null and not (>= 1 and <= SlaPolicyTarget.MaxMinutes)))
        {
            return UserEndpoints.Validation("targets", $"Targets are 1 to {SlaPolicyTarget.MaxMinutes} business minutes.");
        }

        if (!targets.Any(t => t.ResponseMinutes is not null || t.ResolutionMinutes is not null))
        {
            return UserEndpoints.Validation("targets", "Give at least one response or resolution target.");
        }

        if (targets.Any(t => t is { ResponseMinutes: { } response, ResolutionMinutes: { } resolution } && response > resolution))
        {
            return UserEndpoints.Validation("targets", "A response target is not longer than the resolution target of the same priority.");
        }

        var escalations = request.Escalations ?? [];
        if (escalations.Count > MaxEscalations)
        {
            return UserEndpoints.Validation("escalations", $"Give at most {MaxEscalations} escalation thresholds.");
        }

        foreach (var escalation in escalations)
        {
            if (escalation.Percent is not (>= 1 and <= SlaEscalation.MaxPercent))
            {
                return UserEndpoints.Validation("escalations", $"Thresholds are 1 to {SlaEscalation.MaxPercent} % of the target.");
            }

            if (escalation.Metric is { } metric && !Enum.IsDefined(metric))
            {
                return UserEndpoints.Validation("escalations", "Use Response, Resolution or leave the target out for both.");
            }

            if (!escalation.NotifyAssignee && !escalation.NotifyGroupLeads && escalation.NotifyGroupId is null)
            {
                return UserEndpoints.Validation("escalations", "Say who each threshold alerts: the assignee, the group's team leads or another group.");
            }

            if (escalation.NotifyGroupId is { } group && !await db.SupportGroups.AnyAsync(g => g.Id == group && g.Active, cancellationToken))
            {
                return UserEndpoints.Validation("escalations", "Choose an active group to alert.");
            }

            if (escalation.Id is { } id && existing?.Escalations.Any(e => e.Id == id) != true)
            {
                return UserEndpoints.Validation("escalations", "An escalation id belongs to another policy; leave it out for new thresholds.");
            }
        }

        if (escalations.GroupBy(e => (e.Metric, e.Percent)).Any(g => g.Count() > 1))
        {
            return UserEndpoints.Validation("escalations", "Each threshold appears once per target.");
        }

        return null;
    }

    private static async Task<List<PolicyResponse>> PoliciesAsync(ZelvoDbContext db, CancellationToken cancellationToken)
    {
        var policies = await db.SlaPolicies.AsNoTracking().Include(p => p.Targets).Include(p => p.Escalations).AsSplitQuery()
            .OrderBy(p => p.Position).ThenBy(p => p.Id).ToListAsync(cancellationToken);
        var refs = await DirectoryRefs.LoadAsync(db, cancellationToken);
        var calendars = await db.BusinessCalendars.AsNoTracking().ToDictionaryAsync(c => c.Id, c => c.Name, cancellationToken);
        var customerGroups = await db.CustomerGroups.AsNoTracking().ToDictionaryAsync(g => g.Id, g => g.Name, cancellationToken);
        NamedRef? Named(Dictionary<Guid, LocalizedText> names, Guid? id) => id is { } i && names.TryGetValue(i, out var n) ? NamedRef.From(i, n) : null;
        return [.. policies.Select(p => new PolicyResponse(
            p.Id, LocalizedTextDto.From(p.Name), p.Position, p.Active, p.RecordType, p.CustomRecordTypeId,
            refs.Categories.RefOf(p.CategoryId), Named(customerGroups, p.CustomerGroupId), Named(calendars, p.CalendarId),
            [.. p.Targets.OrderBy(t => t.Priority).Select(t => new TargetDto { Priority = t.Priority, ResponseMinutes = t.ResponseMinutes, ResolutionMinutes = t.ResolutionMinutes })],
            [.. p.Escalations.OrderBy(e => e.Percent).ThenBy(e => e.Metric).Select(e => new EscalationResponse(
                e.Id, e.Metric, e.Percent, e.NotifyAssignee, e.NotifyGroupLeads, refs.Group(e.NotifyGroupId)))],
            p.UpdatedAt, p.Version))];
    }

    /// <summary>Saves and asks the timer job to look at every open clock, so the change applies within moments.</summary>
    private static async Task SaveAndRecheckAsync(ZelvoDbContext db, CancellationToken cancellationToken)
    {
        await using var transaction = db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(cancellationToken) : null;
        await db.SaveChangesAsync(cancellationToken);
        await SlaTimerJob.RecheckAllAsync(db, cancellationToken);
        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken);
        }
    }
}

/// <summary>A working period: day of the week and local start and end time (<c>HH:mm</c>; an end of <c>24:00</c> is midnight).</summary>
public sealed class PeriodDto
{
    /// <summary>Day of the week (Monday … Sunday).</summary>
    [Required]
    public DayOfWeek? Day { get; init; }

    /// <summary>Start, <c>HH:mm</c>.</summary>
    [Required]
    [StringLength(5)]
    public string? Start { get; init; }

    /// <summary>End, <c>HH:mm</c> (up to <c>24:00</c>).</summary>
    [Required]
    [StringLength(5)]
    public string? End { get; init; }

    internal static PeriodDto From(DayOfWeek day, int start, int end) => new() { Day = day, Start = Format(start), End = Format(end) };

    internal (DayOfWeek Day, WorkingPeriod Period)? ToValue()
    {
        if (Day is not { } day || !Enum.IsDefined(day) || Parse(Start) is not { } start || Parse(End) is not { } end || start >= end || start >= BusinessCalendarPeriod.MinutesPerDay)
        {
            return null;
        }

        return (day, new WorkingPeriod(start, end));
    }

    private static string Format(int minutes) => $"{minutes / 60:D2}:{minutes % 60:D2}";

    private static int? Parse(string? value)
    {
        if (value is not { Length: 5 } || value[2] != ':'
            || !int.TryParse(value.AsSpan(0, 2), NumberStyles.None, CultureInfo.InvariantCulture, out var hours)
            || !int.TryParse(value.AsSpan(3, 2), NumberStyles.None, CultureInfo.InvariantCulture, out var minutes)
            || minutes > 59 || hours > 24 || (hours == 24 && minutes != 0))
        {
            return null;
        }

        return hours * 60 + minutes;
    }
}

/// <summary>A business calendar.</summary>
public sealed class CalendarInput
{
    /// <summary>Name in English and Arabic.</summary>
    [Required]
    public LocalizedTextInput? Name { get; init; }

    /// <summary>IANA time zone, for example <c>Asia/Dubai</c>.</summary>
    [Required]
    [StringLength(BusinessCalendar.TimeZoneMaxLength)]
    public string? TimeZone { get; init; }

    /// <summary>Make it the tenant's default calendar.</summary>
    public bool? IsDefault { get; init; }

    /// <summary>Working periods of the week.</summary>
    [Required]
    public IReadOnlyList<PeriodDto>? Periods { get; init; }
}

/// <summary>A holiday.</summary>
public sealed class HolidayInput
{
    /// <summary>First day off.</summary>
    [Required]
    public DateOnly? StartDate { get; init; }

    /// <summary>Last day off (default: the first day).</summary>
    public DateOnly? EndDate { get; init; }

    /// <summary>Name in English and Arabic.</summary>
    [Required]
    public LocalizedTextInput? Name { get; init; }
}

/// <summary>A customer group.</summary>
public sealed class CustomerGroupInput
{
    /// <summary>Name in English and Arabic.</summary>
    [Required]
    public LocalizedTextInput? Name { get; init; }

    /// <summary>Active (default true).</summary>
    public bool? Active { get; init; }

    /// <summary>Email domains whose requesters belong to the group, for example <c>moi.gov.ae</c>.</summary>
    [MaxLength(CustomerGroup.MaxDomains)]
    public IReadOnlyList<string>? EmailDomains { get; init; }
}

/// <summary>Targets of one priority, in business minutes; leave one out for no target.</summary>
public sealed class TargetDto
{
    /// <summary>Priority 1 (highest) to 5.</summary>
    [Required]
    public int? Priority { get; init; }

    /// <summary>Business minutes until the first response.</summary>
    public int? ResponseMinutes { get; init; }

    /// <summary>Business minutes until resolution.</summary>
    public int? ResolutionMinutes { get; init; }
}

/// <summary>An escalation threshold.</summary>
public sealed class EscalationInput
{
    /// <summary>Id of an existing threshold of this policy; leave it out for a new one.</summary>
    public Guid? Id { get; init; }

    /// <summary>Response, Resolution, or null for both.</summary>
    public SlaMetric? Metric { get; init; }

    /// <summary>Share of the target, 1–300 %; 100 is the breach.</summary>
    [Required]
    public int? Percent { get; init; }

    /// <summary>Alert the ticket's assignee.</summary>
    public bool NotifyAssignee { get; init; }

    /// <summary>Alert the team leads of the ticket's group.</summary>
    public bool NotifyGroupLeads { get; init; }

    /// <summary>Alert the members of this group too.</summary>
    public Guid? NotifyGroupId { get; init; }
}

/// <summary>An SLA policy.</summary>
public sealed class PolicyInput
{
    /// <summary>Name in English and Arabic.</summary>
    [Required]
    public LocalizedTextInput? Name { get; init; }

    /// <summary>Active (default true).</summary>
    public bool? Active { get; init; }

    /// <summary>Only this record type; null: any.</summary>
    public TicketType? RecordType { get; init; }

    /// <summary>With record type Custom: only this custom record type (task K2); null: every custom type.</summary>
    public Guid? CustomRecordTypeId { get; init; }

    /// <summary>Only this category or below; null: any.</summary>
    public Guid? CategoryId { get; init; }

    /// <summary>Only requesters in this customer group; null: any.</summary>
    public Guid? CustomerGroupId { get; init; }

    /// <summary>Business hours when the ticket's group has none; null: the tenant default.</summary>
    public Guid? CalendarId { get; init; }

    /// <summary>Targets per priority.</summary>
    [Required]
    [MaxLength(5)]
    public IReadOnlyList<TargetDto>? Targets { get; init; }

    /// <summary>Escalation thresholds.</summary>
    [MaxLength(10)]
    public IReadOnlyList<EscalationInput>? Escalations { get; init; }
}

/// <summary>The order of all policies.</summary>
public sealed class OrderPoliciesRequest
{
    /// <summary>Every policy's id, first tried first.</summary>
    [Required]
    [MaxLength(500)]
    public List<Guid>? Ids { get; init; }
}

/// <summary>A ticket to try the policies with.</summary>
public sealed class SlaTestRequest
{
    /// <summary>Record type (default Incident).</summary>
    public TicketType? RecordType { get; init; }

    /// <summary>With record type Custom: the custom record type (task K2).</summary>
    public Guid? CustomRecordTypeId { get; init; }

    /// <summary>Priority 1–5 (default 3).</summary>
    public int? Priority { get; init; }

    /// <summary>Category.</summary>
    public Guid? CategoryId { get; init; }

    /// <summary>Requester (for their customer groups).</summary>
    public Guid? RequesterId { get; init; }

    /// <summary>Group (for its calendar).</summary>
    public Guid? GroupId { get; init; }
}

/// <summary>A group's calendar.</summary>
public sealed class GroupCalendarRequest
{
    /// <summary>The calendar, or null for none.</summary>
    public Guid? CalendarId { get; init; }
}

/// <summary>A ready-made weekly pattern.</summary>
internal sealed record CalendarTemplateResponse(string Key, IReadOnlyList<PeriodDto> Periods);

/// <summary>A holiday.</summary>
internal sealed record HolidayResponse(Guid Id, DateOnly StartDate, DateOnly EndDate, LocalizedTextDto Name);

/// <summary>A business calendar.</summary>
internal sealed record CalendarResponse(
    Guid Id, LocalizedTextDto Name, string TimeZone, bool IsDefault, IReadOnlyList<PeriodDto> Periods, IReadOnlyList<HolidayResponse> Holidays,
    int GroupCount, int PolicyCount, DateTimeOffset UpdatedAt, uint Version)
{
    public static CalendarResponse From(BusinessCalendar c, int groups, int policies) => new(
        c.Id, LocalizedTextDto.From(c.Name), c.TimeZone, c.IsDefault,
        [.. c.Periods.OrderBy(p => ((int)p.Day + 6) % 7).ThenBy(p => p.StartMinute).Select(p => PeriodDto.From(p.Day, p.StartMinute, p.EndMinute))],
        [.. c.Holidays.OrderBy(h => h.StartDate).Select(h => new HolidayResponse(h.Id, h.StartDate, h.EndDate, LocalizedTextDto.From(h.Name)))],
        groups, policies, c.UpdatedAt, c.Version);
}

/// <summary>A requester added to a customer group by hand.</summary>
internal sealed record CustomerResponse(Guid UserId, string? DisplayName);

/// <summary>A customer group.</summary>
internal sealed record CustomerGroupResponse(
    Guid Id, LocalizedTextDto Name, bool Active, IReadOnlyList<string> EmailDomains, IReadOnlyList<CustomerResponse> Members, int PolicyCount,
    DateTimeOffset UpdatedAt, uint Version)
{
    public static CustomerGroupResponse From(CustomerGroup g, int policies) => new(
        g.Id, LocalizedTextDto.From(g.Name), g.Active, g.EmailDomains,
        [.. g.Members.OrderBy(m => m.User?.DisplayName ?? string.Empty, StringComparer.OrdinalIgnoreCase).Select(m => new CustomerResponse(m.UserId, m.User?.DisplayName))],
        policies, g.UpdatedAt, g.Version);
}

/// <summary>An escalation threshold.</summary>
internal sealed record EscalationResponse(Guid Id, SlaMetric? Metric, int Percent, bool NotifyAssignee, bool NotifyGroupLeads, NamedRef? NotifyGroup);

/// <summary>An SLA policy.</summary>
internal sealed record PolicyResponse(
    Guid Id, LocalizedTextDto Name, int Position, bool Active, TicketType? RecordType, Guid? CustomRecordTypeId, NodeRef? Category, NamedRef? CustomerGroup, NamedRef? Calendar,
    IReadOnlyList<TargetDto> Targets, IReadOnlyList<EscalationResponse> Escalations, DateTimeOffset UpdatedAt, uint Version);

/// <summary>A target and when it would fall due if the ticket were logged now.</summary>
internal sealed record SlaPreviewTarget(int Minutes, DateTimeOffset? DueAt);

/// <summary>What a ticket would get.</summary>
internal sealed record SlaPreviewResponse(
    NamedRef? Policy, NamedRef? Calendar, IReadOnlyList<NamedRef> CustomerGroups, SlaPreviewTarget? Response, SlaPreviewTarget? Resolution);

/// <summary>A group's calendar.</summary>
internal sealed record GroupCalendarResponse(Guid GroupId, Guid? CalendarId);
