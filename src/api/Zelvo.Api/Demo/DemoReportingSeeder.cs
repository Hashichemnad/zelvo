using Microsoft.EntityFrameworkCore;
using Zelvo.Api.Settings;
using Zelvo.Api.Setup;
using Zelvo.Data;
using Zelvo.Data.Identity;
using Zelvo.Data.Reports;
using Zelvo.Data.Sla;
using Zelvo.Data.Tickets;
using Zelvo.Data.Voice;
using Zelvo.Tickets;
using Zelvo.Tickets.Reports;

namespace Zelvo.Api.Demo;

/// <summary>
/// Task F2: dashboards with realistic trends for the demo (requirements, demo mode): about six months
/// of resolved incidents and service requests spread over the sample groups and agents, with SLA
/// results that improve month by month, happiness ratings and a few still open; the setup wizard
/// marked as finished; and a weekly overview emailed to the executive.
/// </summary>
internal sealed class DemoReportingSeeder : IDemoSeeder
{
    public string Key => "f2-reporting";

    public int Order => 230;

    private static readonly (string En, string Ar)[] IncidentTitles =
    [
        ("Laptop does not start", "الحاسوب المحمول لا يعمل"),
        ("Cannot connect to the VPN", "تعذر الاتصال بالشبكة الافتراضية الخاصة"),
        ("Printer on the second floor is offline", "طابعة الطابق الثاني غير متصلة"),
        ("Email is not syncing on my phone", "البريد الإلكتروني لا يتزامن على الهاتف"),
        ("Application shows an error when saving", "يظهر خطأ في التطبيق عند الحفظ"),
        ("Wi-Fi drops in the meeting room", "انقطاع الشبكة اللاسلكية في قاعة الاجتماعات"),
        ("Account locked after password change", "قفل الحساب بعد تغيير كلمة المرور"),
        ("Screen flickers", "وميض في الشاشة"),
    ];

    public async Task SeedAsync(DemoSeedContext context, CancellationToken cancellationToken)
    {
        var db = context.Get<ZelvoDbContext>();
        var settings = context.Get<TenantSettingsStore>();
        var users = await db.Users.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
        UserAccount? Persona(string persona) =>
            context.Organisation.User(persona) is { } demo ? users.SingleOrDefault(u => u.Email == context.Organisation.UsernameOf(demo)) : null;
        var requester = Persona("requester");
        var admin = Persona("admin");
        var executive = Persona("executive");
        if (requester is null)
        {
            return;
        }

        var groups = await db.SupportGroups.AsNoTracking().Include(g => g.Members).Where(g => g.Members.Count > 0).OrderBy(g => g.Name.En).ToListAsync(cancellationToken).ConfigureAwait(false);
        var locations = await db.Locations.AsNoTracking().Where(l => l.Active).OrderBy(l => l.Depth).ThenBy(l => l.Position).Select(l => l.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
        var categories = await db.Categories.AsNoTracking().Where(c => c.Active).OrderBy(c => c.Depth).ThenBy(c => c.Position).Select(c => c.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
        var policy = await db.SlaPolicies.AsNoTracking().Include(p => p.Targets).Where(p => p.CustomerGroupId == null).OrderBy(p => p.Position).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        var prefixes = await settings.GetAsync(Incidents.IncidentSettings.NumberPrefixes, cancellationToken).ConfigureAwait(false);
        var matrix = await settings.GetAsync(Incidents.IncidentSettings.PriorityMatrix, cancellationToken).ConfigureAwait(false);
        var government = context.Organisation.Kind == DemoOrganisationKind.Government;

        var now = DateTimeOffset.UtcNow;
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        // Deterministic, so every reset shows the same trends.
        var random = new Random(government ? 2026 : 1125);
        const int days = 182;
        for (var day = days; day >= 1; day--)
        {
            var date = now.AddDays(-day);
            if (date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
            {
                continue;
            }

            // SLA results get better over the six months; volumes vary a little.
            var progress = 1 - (day / (double)days);
            var perDay = 1 + random.Next(3);
            for (var n = 0; n < perDay; n++)
            {
                var type = random.Next(4) == 0 ? TicketType.ServiceRequest : TicketType.Incident;
                var group = groups.Count == 0 ? null : groups[random.Next(groups.Count)];
                var assignee = group?.Members[random.Next(group.Members.Count)].UserId;
                var impact = 1 + random.Next(3);
                var urgency = 1 + random.Next(3);
                var priority = type == TicketType.Incident ? matrix.PriorityOf(impact, urgency) : 3;
                var created = date.Date.AddHours(8 + random.Next(7)).AddMinutes(random.Next(60));
                var createdAt = new DateTimeOffset(created, TimeSpan.Zero);
                var open = day <= 3 && random.Next(2) == 0;
                var target = policy?.Targets.FirstOrDefault(t => t.Priority == priority);
                var responseTarget = target?.ResponseMinutes ?? 120;
                var resolutionTarget = target?.ResolutionMinutes ?? 1440;
                var responseMet = random.NextDouble() < 0.80 + (0.17 * progress);
                var resolutionMet = random.NextDouble() < 0.72 + (0.20 * progress);
                var respondedAt = createdAt.AddMinutes(responseMet ? responseTarget * (0.2 + (0.6 * random.NextDouble())) : responseTarget * (1.2 + random.NextDouble()));
                var resolvedAt = createdAt.AddMinutes(resolutionMet ? resolutionTarget * (0.3 + (0.6 * random.NextDouble())) : resolutionTarget * (1.1 + random.NextDouble()));
                if (resolvedAt > now)
                {
                    open = true;
                }

                var (titleEn, titleAr) = IncidentTitles[random.Next(IncidentTitles.Length)];
                var sequence = await TicketNumbers.NextAsync(db, context.Tenant.Id, type, cancellationToken).ConfigureAwait(false);
                var ticket = new Ticket
                {
                    Type = type,
                    Number = TicketNumbers.Format(TicketNumbers.PrefixOf(prefixes, type), sequence),
                    Sequence = sequence,
                    Title = government || random.Next(3) == 0 ? titleAr : titleEn,
                    Channel = (random.Next(10)) switch { < 5 => TicketChannel.Portal, < 8 => TicketChannel.Email, 8 => TicketChannel.Chat, _ => TicketChannel.Phone },
                    RequesterId = requester.Id,
                    // History has no creator: it stands for tickets from before the installation, and
                    // has no notifications of its own.
                    CreatedById = null,
                    LocationId = locations.Count == 0 ? null : locations[random.Next(locations.Count)],
                    GroupId = group?.Id,
                    AssigneeId = assignee,
                    CategoryId = categories.Count == 0 ? null : categories[random.Next(categories.Count)],
                    State = open ? TicketState.InProgress : TicketState.Closed,
                    CreatedAt = createdAt,
                    UpdatedAt = open ? createdAt : resolvedAt.AddDays(2),
                    RespondedAt = open && respondedAt > now ? null : respondedAt,
                    ResolvedAt = open ? null : resolvedAt,
                    ClosedAt = open ? null : resolvedAt.AddDays(2),
                    ReopenCount = random.Next(20) == 0 ? 1 : 0,
                    ResolutionNotes = open ? null : government ? "تمت المعالجة وإبلاغ المتعامل." : "Fixed and confirmed with the requester.",
                };
                if (type == TicketType.Incident)
                {
                    ticket.Incident = new Incident { TicketId = ticket.Id, Impact = impact, Urgency = urgency, Priority = priority };
                }

                db.Tickets.Add(ticket);
                db.TicketSlas.Add(Clock(ticket, SlaMetric.Response, policy?.Id, responseTarget, createdAt, ticket.RespondedAt, now));
                db.TicketSlas.Add(Clock(ticket, SlaMetric.Resolution, policy?.Id, resolutionTarget, createdAt, ticket.ResolvedAt, now));

                // About half of the resolved tickets are rated; happiness rises with the SLA results.
                if (!open && random.Next(2) == 0)
                {
                    var happy = random.NextDouble() < 0.62 + (0.25 * progress) - (resolutionMet ? 0 : 0.25);
                    db.HappinessRatings.Add(new HappinessRating
                    {
                        TicketId = ticket.Id, RecordType = type, Channel = ticket.Channel, UserId = requester.Id,
                        Score = happy ? 3 : random.Next(3) == 0 ? 1 : 2, Points = 3, CreatedAt = resolvedAt.AddHours(4),
                    });
                }
            }

            if (day % 50 == 0)
            {
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        // The sample organisations are set up: the wizard is finished.
        await settings.SetAsync(SetupEndpoints.Progress, new SetupProgress(
            SetupEndpoints.Steps.ToDictionary(s => s, s => s is "ai" or "channels" ? SetupStepStatus.Skipped : SetupStepStatus.Done), now, []), cancellationToken).ConfigureAwait(false);

        // A weekly overview for the executive, on Monday mornings.
        if (admin is not null && executive is not null)
        {
            var zone = await new ReportBuilder(db).TimeZoneAsync(cancellationToken).ConfigureAwait(false);
            var schedule = new ReportSchedule
            {
                Name = government ? "التقرير الأسبوعي لمكتب الخدمة" : "Weekly service desk overview",
                Report = nameof(ReportKind.Overview),
                Filters = ScheduleFilters.None.ToJson(),
                Frequency = ReportFrequency.Weekly,
                DayOfWeek = 1,
                TimeOfDay = 7 * 60 + 30,
                OwnerId = admin.Id,
                NextRunAt = ReportScheduleTimes.NextRun(ReportFrequency.Weekly, 1, null, 7 * 60 + 30, zone, now),
            };
            schedule.Recipients.Add(new ReportScheduleRecipient { ScheduleId = schedule.Id, UserId = executive.Id });
            db.ReportSchedules.Add(schedule);
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    // A clock as the B3 engine would have left it: stopped at the response or resolution, breached
    // when that came after the target (approximated with elapsed time for the sample data).
    private static TicketSla Clock(Ticket ticket, SlaMetric metric, Guid? policy, int target, DateTimeOffset created, DateTimeOffset? stopped, DateTimeOffset now)
    {
        var until = stopped ?? now;
        var elapsed = (long)(until - created).TotalSeconds;
        var breached = elapsed > target * 60L ? created.AddMinutes(target) : (DateTimeOffset?)null;
        return new TicketSla
        {
            TicketId = ticket.Id, Metric = metric, PolicyId = policy, TargetMinutes = target, State = stopped is null ? SlaClockState.Running : SlaClockState.Stopped,
            ElapsedSeconds = elapsed, CountedUntil = until, StoppedAt = stopped, BreachedAt = breached, DueAt = created.AddMinutes(target),
            ReachedPercent = (int)Math.Min(300, elapsed * 100 / Math.Max(1, target * 60L)),
            NextCheckAt = stopped is null ? now.AddMinutes(1) : null,
        };
    }
}
