using Microsoft.EntityFrameworkCore;
using Zelvo.Data;
using Zelvo.Data.Auditing;
using Zelvo.Data.Localization;
using Zelvo.Data.Sla;
using Zelvo.Tickets.Sla;

namespace Zelvo.Api.Demo;

/// <summary>
/// Task B3: business calendars (UAE government week for the government entity, office hours and a
/// 24×7 calendar for the company), UAE public holidays, a customer group with the demo requester,
/// a VIP and a standard SLA policy with 75 % and 100 % escalations, and SLA clocks for the sample
/// incidents. Islamic holidays depend on the moon sighting; the dates here are expected dates for
/// the demo, which administrators correct when they are announced.
/// </summary>
internal sealed class DemoSlaSeeder : IDemoSeeder
{
    public string Key => "b3-sla";

    public int Order => 120;

    private static readonly (DateOnly Start, DateOnly End, string En, string Ar)[] Holidays =
    [
        (new(2026, 12, 1), new(2026, 12, 1), "Commemoration Day", "يوم الشهيد"),
        (new(2026, 12, 2), new(2026, 12, 3), "National Day", "عيد الاتحاد"),
        (new(2027, 1, 1), new(2027, 1, 1), "New Year's Day", "رأس السنة الميلادية"),
        (new(2027, 3, 9), new(2027, 3, 11), "Eid al-Fitr (expected)", "عيد الفطر (متوقع)"),
        (new(2027, 5, 15), new(2027, 5, 15), "Arafat Day (expected)", "يوم عرفة (متوقع)"),
        (new(2027, 5, 16), new(2027, 5, 18), "Eid al-Adha (expected)", "عيد الأضحى (متوقع)"),
        (new(2027, 6, 6), new(2027, 6, 6), "Islamic New Year (expected)", "رأس السنة الهجرية (متوقع)"),
        (new(2027, 8, 15), new(2027, 8, 15), "Prophet's Birthday (expected)", "المولد النبوي الشريف (متوقع)"),
    ];

    public async Task SeedAsync(DemoSeedContext context, CancellationToken cancellationToken)
    {
        var db = context.Get<ZelvoDbContext>();
        var government = context.Organisation.Kind == DemoOrganisationKind.Government;

        var main = Calendar(db,
            government ? "Government working hours" : "Office hours",
            government ? "ساعات العمل الحكومية" : "ساعات العمل المكتبية",
            government ? CalendarTemplate.UaeGovernment : CalendarTemplate.UaePrivate, isDefault: true);
        var allDay = Calendar(db, "Around the clock", "على مدار الساعة", CalendarTemplate.AllDay, isDefault: false);

        var requesterEmail = context.Organisation.UsernameOf(context.Organisation.User("requester")!);
        var requester = await db.Users.SingleAsync(u => u.Email == requesterEmail, cancellationToken).ConfigureAwait(false);
        var vip = new CustomerGroup
        {
            Name = government ? new LocalizedText("Senior officials", "كبار المسؤولين") : new LocalizedText("Key accounts", "كبار العملاء"),
        };
        vip.Members.Add(new CustomerGroupMember { GroupId = vip.Id, UserId = requester.Id });
        db.CustomerGroups.Add(vip);

        // Infrastructure works around the clock; its tickets count every hour.
        var groups = await db.SupportGroups.ToListAsync(cancellationToken).ConfigureAwait(false);
        var infrastructure = groups.FirstOrDefault(g => g.Name.En is "Infrastructure team" or "Network team");
        if (infrastructure is not null)
        {
            infrastructure.CalendarId = allDay.Id;
        }

        var leads = groups.FirstOrDefault(g => g.Name.En == "Service desk")?.Id;
        Policy(db, 0, "VIP", "كبار العملاء", vip.Id, main.Id, leads, [(1, 15, 120), (2, 30, 240), (3, 60, 480), (4, 120, 960), (5, 240, 2400)]);
        Policy(db, 1, "Standard", "قياسي", null, main.Id, leads, [(1, 30, 240), (2, 60, 480), (3, 240, 1440), (4, 480, 2400), (5, 960, 4800)]);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        // The sample incidents get their clocks.
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var tickets = await db.Tickets.Include(t => t.Incident).ToListAsync(cancellationToken).ConfigureAwait(false);
        // Built here rather than resolved: migrate seeds without the ticket services of the API and worker.
        var tracker = new SlaTracker(db, context.Get<IAuditLog>());
        await tracker.UpdateAsync(tickets, DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static BusinessCalendar Calendar(ZelvoDbContext db, string en, string ar, CalendarTemplate template, bool isDefault)
    {
        var calendar = new BusinessCalendar { Name = new LocalizedText(en, ar), IsDefault = isDefault };
        foreach (var (day, period) in template.Periods)
        {
            calendar.Periods.Add(new BusinessCalendarPeriod { CalendarId = calendar.Id, Day = day, StartMinute = period.Start, EndMinute = period.End });
        }

        if (template != CalendarTemplate.AllDay)
        {
            foreach (var (start, end, holidayEn, holidayAr) in Holidays)
            {
                calendar.Holidays.Add(new BusinessCalendarHoliday { CalendarId = calendar.Id, StartDate = start, EndDate = end, Name = new LocalizedText(holidayEn, holidayAr) });
            }
        }

        db.BusinessCalendars.Add(calendar);
        return calendar;
    }

    private static void Policy(
        ZelvoDbContext db, int position, string en, string ar, Guid? customerGroup, Guid calendar, Guid? alsoAlert,
        (int Priority, int Response, int Resolution)[] targets)
    {
        var policy = new SlaPolicy { Name = new LocalizedText(en, ar), Position = position, CustomerGroupId = customerGroup, CalendarId = calendar };
        foreach (var (priority, response, resolution) in targets)
        {
            policy.Targets.Add(new SlaPolicyTarget { PolicyId = policy.Id, Priority = priority, ResponseMinutes = response, ResolutionMinutes = resolution });
        }

        policy.Escalations.Add(new SlaEscalation { PolicyId = policy.Id, Percent = 75, NotifyAssignee = true, NotifyGroupLeads = true });
        policy.Escalations.Add(new SlaEscalation { PolicyId = policy.Id, Percent = 100, NotifyGroupLeads = true, NotifyGroupId = customerGroup is null ? null : alsoAlert });
        db.SlaPolicies.Add(policy);
    }
}
