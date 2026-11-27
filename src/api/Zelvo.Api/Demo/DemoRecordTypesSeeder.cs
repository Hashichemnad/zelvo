using Microsoft.EntityFrameworkCore;
using Zelvo.Data;
using Zelvo.Data.Identity;
using Zelvo.Data.Localization;
using Zelvo.Data.Records;
using Zelvo.Data.Sla;
using Zelvo.Data.Tickets;
using Zelvo.Search;
using Zelvo.Tickets;

namespace Zelvo.Api.Demo;

/// <summary>
/// Task K2: each sample organisation's custom record type "Facilities request" (<c>FAC-</c>), with
/// its own states (new, scheduled, on site, waiting for parts, done, closed) mapped onto the ticket
/// core, a portal entry, its permissions for agents, team leads and administrators, an SLA policy
/// of its own and three sample records: new from the requester, on site with the agent, done.
/// Fictional, like everything in the demo.
/// </summary>
internal sealed class DemoRecordTypesSeeder : IDemoSeeder
{
    public string Key => "k2-record-types";

    public int Order => 295;

    public async Task SeedAsync(DemoSeedContext context, CancellationToken cancellationToken)
    {
        var db = context.Get<ZelvoDbContext>();
        var organisation = context.Organisation;
        var arabic = organisation.DefaultLanguage == LocalizedText.Arabic;
        Task<UserAccount?> UserAsync(string persona) =>
            organisation.User(persona) is { } u
                ? db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Email == organisation.UsernameOf(u), cancellationToken)
                : Task.FromResult<UserAccount?>(null);
        var requester = await UserAsync("requester").ConfigureAwait(false);
        var agent = await UserAsync("agent").ConfigureAwait(false);
        if (requester is null || agent is null)
        {
            return;
        }

        var group = await db.SupportGroupMembers.AsNoTracking().Where(m => m.UserId == agent.Id).Select(m => (Guid?)m.GroupId).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        List<RecordState> states =
        [
            new("new", new("New", "جديد"), RecordStateCategory.New),
            new("scheduled", new("Scheduled", "مجدول"), RecordStateCategory.InProgress),
            new("on-site", new("On site", "في الموقع"), RecordStateCategory.InProgress),
            new("waiting-parts", new("Waiting for parts", "بانتظار قطع الغيار"), RecordStateCategory.OnHold),
            new("done", new("Done", "منجز"), RecordStateCategory.Resolved),
            new("closed", new("Closed", "مغلق"), RecordStateCategory.Closed),
        ];
        var type = new RecordType
        {
            Key = "facilities",
            Name = new LocalizedText("Facilities request", "طلب مرافق"),
            Description = new LocalizedText("Repairs and requests for buildings, offices, air conditioning, lighting and furniture.",
                "طلبات الصيانة والخدمات للمباني والمكاتب والتكييف والإنارة والأثاث."),
            NumberPrefix = "FAC",
            States = RecordStates.Write(states),
            PortalEntry = true,
            DefaultPriority = 3,
        };
        db.RecordTypes.Add(type);

        // The type's permissions for the roles that handle it, as Settings → Record types grants them.
        var roleKeys = new[] { BuiltInRoles.Agent, BuiltInRoles.TeamLead, BuiltInRoles.TenantAdmin };
        foreach (var role in await db.Roles.Include(r => r.Permissions).Where(r => roleKeys.Contains(r.Key)).ToListAsync(cancellationToken).ConfigureAwait(false))
        {
            foreach (var permission in RecordPermissions.Actions.Select(a => $"records.{type.Key}.{a}"))
            {
                if (role.Permissions.All(p => p.Permission != permission))
                {
                    role.Permissions.Add(new RoleGrant { Permission = permission });
                }
            }
        }

        // Its own SLA policy, first in the list: P3 answered within 4 working hours, done within 3 days.
        var policy = new SlaPolicy
        {
            Name = new LocalizedText("Facilities requests", "طلبات المرافق"),
            Position = -1,
            RecordType = TicketType.Custom,
            CustomRecordTypeId = type.Id,
        };
        foreach (var (priority, response, resolution) in new[] { (1, 60, 480), (2, 120, 960), (3, 240, 1440), (4, 480, 2880), (5, 960, 4320) })
        {
            policy.Targets.Add(new SlaPolicyTarget { PolicyId = policy.Id, Priority = priority, ResponseMinutes = response, ResolutionMinutes = resolution });
        }

        db.SlaPolicies.Add(policy);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        var now = DateTimeOffset.UtcNow;
        var samples = arabic
            ? new (string Title, string Description, string State, bool Assigned, int Days)[]
            {
                ("تعطل التكييف في قاعة الاجتماعات الكبرى", "درجة الحرارة مرتفعة في القاعة منذ الصباح.", "new", false, 0),
                ("إنارة الممر في الطابق الثاني لا تعمل", "ثلاثة مصابيح متعطلة قرب المصعد.", "on-site", true, 1),
                ("استبدال كرسي مكتب مكسور", "الكرسي في مكتب خدمة المتعاملين رقم ٤.", "done", true, 4),
            }
            :
            [
                ("Air conditioning not working in the large meeting room", "The room has been too warm since the morning.", "new", false, 0),
                ("Corridor lights out on the second floor", "Three lights near the lift are out.", "on-site", true, 1),
                ("Replace a broken office chair", "The chair at desk 4 of the sales floor.", "done", true, 4),
            ];
        var created = new List<Ticket>();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        foreach (var (title, description, state, assigned, days) in samples)
        {
            var at = now.AddDays(-days).AddHours(-2);
            var sequence = (await db.Database.SqlQuery<long>($"""
                UPDATE record_types SET last_number = last_number + 1 WHERE id = {type.Id} RETURNING last_number AS "Value"
                """).ToListAsync(cancellationToken).ConfigureAwait(false)).Single();
            var ticket = new Ticket
            {
                Type = TicketType.Custom,
                RecordTypeId = type.Id,
                Number = TicketNumbers.Format(type.NumberPrefix, sequence),
                Sequence = sequence,
                Title = title,
                Description = description,
                Channel = TicketChannel.Portal,
                RequesterId = requester.Id,
                CreatedById = requester.Id,
                GroupId = group,
                LocationId = requester.LocationId,
                CreatedAt = at,
                UpdatedAt = at,
            };
            ticket.Custom = new CustomRecord { TicketId = ticket.Id, RecordTypeId = type.Id, StateKey = "new", Priority = 3 };
            if (assigned)
            {
                TicketWorkflow.Assign(ticket, agent.Id, at.AddMinutes(10));
                TicketWorkflow.Apply(ticket, TicketState.InProgress, null, null, at.AddMinutes(30));
                ticket.Custom.StateKey = "on-site";
            }

            if (state == "done")
            {
                TicketWorkflow.Apply(ticket, TicketState.Resolved, null, arabic ? "تم استبدال الكرسي." : "The chair was replaced.", at.AddDays(1));
                ticket.Custom.StateKey = "done";
            }

            db.Tickets.Add(ticket);
            created.Add(ticket);
        }

        await new Tickets.Sla.SlaTracker(db, context.Get<Data.Auditing.IAuditLog>()).UpdateAsync(created, now, cancellationToken).ConfigureAwait(false);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        await RecordSearch.IndexAsync(db, context.Get<ISearchIndexer>(), created, cancellationToken).ConfigureAwait(false);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
