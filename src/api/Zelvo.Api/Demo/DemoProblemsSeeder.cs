using Microsoft.EntityFrameworkCore;
using Zelvo.Api.Problems;
using Zelvo.Data;
using Zelvo.Data.Localization;
using Zelvo.Data.Problems;
using Zelvo.Data.Tickets;
using Zelvo.Search;
using Zelvo.Tickets;

namespace Zelvo.Api.Demo;

/// <summary>
/// Task H1: each sample organisation's problems. A problem raised from the incidents of the outage
/// G1 linked to the CMDB (the e-services portal for the government entity, email for the company),
/// in root-cause analysis with its 5 Whys and fishbone, the configuration item and a known error
/// with a workaround in both languages that agents see on those incidents; and a second problem,
/// new in the queue. Fictional, like everything in the demo.
/// </summary>
internal sealed class DemoProblemsSeeder : IDemoSeeder
{
    public string Key => "h1-problems";

    // After the CMDB (g1-cmdb) that links the sample incidents to their items.
    public int Order => 290;

    public async Task SeedAsync(DemoSeedContext context, CancellationToken cancellationToken)
    {
        var db = context.Get<ZelvoDbContext>();
        var government = context.Organisation.Kind == DemoOrganisationKind.Government;
        var leadEmail = context.Organisation.User("lead") is { } l ? context.Organisation.UsernameOf(l) : null;
        var lead = leadEmail is null ? null : await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Email == leadEmail, cancellationToken).ConfigureAwait(false);
        if (lead is null)
        {
            return;
        }

        var groupId = await db.SupportGroupMembers.AsNoTracking().Where(m => m.UserId == lead.Id).Select(m => (Guid?)m.GroupId).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        var outage = government ? "انقطاع بوابة الخدمات الإلكترونية" : "Email delivery delayed for all staff";
        var incidents = await db.Tickets.AsNoTracking().Where(t => t.Type == TicketType.Incident && t.Title == outage).ToListAsync(cancellationToken).ConfigureAwait(false);
        var others = await db.Tickets.AsNoTracking().Where(t => t.Type == TicketType.Incident && t.Title != outage && t.State != TicketState.Closed)
            .OrderBy(t => t.Sequence).Take(2).ToListAsync(cancellationToken).ConfigureAwait(false);
        var items = await db.TicketConfigurationItems.AsNoTracking().Where(l => incidents.Select(i => i.Id).Contains(l.TicketId))
            .Select(l => l.ConfigurationItemId).Distinct().ToListAsync(cancellationToken).ConfigureAwait(false);

        var prefixes = await TicketSettingKeys.ReadAsync<IReadOnlyDictionary<TicketType, string>>(db, TicketSettingKeys.NumberPrefixes,
            TicketNumbers.DefaultPrefixes, TicketNumbers.AreValid, cancellationToken).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        async Task<Ticket> NewAsync(string title, string description, Guid? categoryId, DateTimeOffset at)
        {
            var sequence = await TicketNumbers.NextAsync(db, context.Tenant.Id, TicketType.Problem, cancellationToken).ConfigureAwait(false);
            var ticket = new Ticket
            {
                Type = TicketType.Problem,
                Number = TicketNumbers.Format(TicketNumbers.PrefixOf(prefixes, TicketType.Problem), sequence),
                Sequence = sequence,
                Title = title,
                Description = description,
                Channel = TicketChannel.Portal,
                RequesterId = lead.Id,
                CreatedById = lead.Id,
                CategoryId = categoryId,
                GroupId = groupId,
                CreatedAt = at,
                UpdatedAt = at,
            };
            db.Tickets.Add(ticket);
            return ticket;
        }

        // The outage, under investigation.
        var main = await NewAsync(
            government ? "انقطاعات متكررة في بوابة الخدمات الإلكترونية" : "Recurring email delivery delays",
            government
                ? "تتكرر انقطاعات بوابة الخدمات الإلكترونية عند ارتفاع عدد المتعاملين صباحًا، وقد ورد أكثر من بلاغ بالسبب نفسه."
                : "Email to and from staff is delayed by up to an hour on busy mornings; several incidents report the same cause.",
            incidents.FirstOrDefault()?.CategoryId, now.AddDays(-3)).ConfigureAwait(false);
        TicketWorkflow.Assign(main, lead.Id, now.AddDays(-3));
        TicketWorkflow.Apply(main, TicketState.InProgress, null, null, now.AddDays(-3).AddHours(1));
        main.Problem = new ProblemRecord
        {
            TicketId = main.Id,
            Phase = ProblemPhase.KnownError,
            Priority = 2,
            RootCause = government
                ? "مجمّع الاتصالات بقاعدة البيانات صغير جدًا لحجم الطلبات في ساعات الذروة."
                : "The mail gateway's queue fills up because the spam scanner runs on one thread.",
            FiveWhys = ProblemAnalysis.Write(government
                ? new FiveWhys("تتوقف البوابة عن الاستجابة صباحًا.",
                [
                    new("لماذا تتوقف البوابة؟", "لأن خادم التطبيق ينتظر اتصالات قاعدة البيانات."),
                    new("لماذا ينتظر الاتصالات؟", "لأن جميع الاتصالات المتاحة مستخدمة."),
                    new("لماذا تُستخدم جميعها؟", "لأن عدد المتعاملين في ساعات الذروة تضاعف هذا العام."),
                    new("لماذا لم يُرفع الحد؟", "لأن الإعداد لم يُراجع منذ إطلاق البوابة."),
                ], "مراجعة سعة مجمّع الاتصالات ضمن خطة السعة السنوية.")
                : new FiveWhys("Email is delayed on busy mornings.",
                [
                    new("Why is email delayed?", "Messages wait in the mail gateway's queue."),
                    new("Why do they wait?", "The spam scanner cannot keep up."),
                    new("Why can it not keep up?", "It runs on a single thread."),
                    new("Why a single thread?", "The default setting was never changed after the upgrade."),
                ], "Review gateway settings after every upgrade.")),
            Fishbone = ProblemAnalysis.Write(new Fishbone(
                government ? "انقطاع البوابة" : "Delayed email",
                government
                    ?
                    [
                        new("technology", "حجم مجمّع اتصالات قاعدة البيانات", true),
                        new("process", "لا توجد مراجعة دورية للسعة", false),
                        new("measurement", "لا يوجد تنبيه عند امتلاء المجمّع", false),
                    ]
                    :
                    [
                        new("technology", "Spam scanner on one thread", true),
                        new("process", "Settings not reviewed after upgrades", false),
                        new("measurement", "No alert on queue length", false),
                    ])),
        };
        foreach (var incident in incidents)
        {
            db.ProblemIncidents.Add(new ProblemIncident { ProblemId = main.Id, IncidentId = incident.Id, LinkedById = lead.Id, LinkedAt = now.AddDays(-3) });
        }

        foreach (var item in items)
        {
            db.TicketConfigurationItems.Add(new Data.Cmdb.TicketConfigurationItem { TicketId = main.Id, ConfigurationItemId = item, AddedById = lead.Id });
        }

        var keSequence = (await db.Database.SqlQuery<long>($"""
            INSERT INTO known_error_number_sequences (tenant_id, last_value) VALUES ({context.Tenant.Id}, 1)
            ON CONFLICT (tenant_id) DO UPDATE SET last_value = known_error_number_sequences.last_value + 1
            RETURNING last_value AS "Value"
            """).ToListAsync(cancellationToken).ConfigureAwait(false)).Single();
        var error = new KnownError
        {
            Number = TicketNumbers.Format(ProblemSettingsValue.Default.KnownErrorPrefix, keSequence),
            Sequence = keSequence,
            ProblemId = main.Id,
            Title = government
                ? new LocalizedText("E-services portal stops responding at peak hours", "توقف بوابة الخدمات الإلكترونية عن الاستجابة في ساعات الذروة")
                : new LocalizedText("Email delayed on busy mornings", "تأخر البريد الإلكتروني صباح أيام الذروة"),
            Symptoms = government
                ? new LocalizedText("The portal shows a timeout page between 8 and 10 in the morning.", "تظهر صفحة انتهاء المهلة في البوابة بين الساعة الثامنة والعاشرة صباحًا.")
                : new LocalizedText("Messages arrive up to an hour late; nothing bounces.", "تصل الرسائل متأخرة حتى ساعة دون أن تُرفض."),
            Workaround = government
                ? new LocalizedText("Ask the customer to try again after 10:00 or to use the Smart app, which is not affected. Staff can restart the portal's application pool from the operations console.",
                    "يُرجى إفادة المتعامل بإعادة المحاولة بعد الساعة العاشرة صباحًا أو استخدام التطبيق الذكي غير المتأثر. ويمكن للموظفين إعادة تشغيل مجمّع تطبيقات البوابة من وحدة التحكم التشغيلية.")
                : new LocalizedText("Urgent mail can be sent through the web mail, which bypasses the gateway queue. The mail team can flush the queue from the gateway console.",
                    "يمكن إرسال الرسائل العاجلة عبر بريد الويب الذي لا يمر بطابور البوابة، ويستطيع فريق البريد تفريغ الطابور من وحدة تحكم البوابة."),
            CreatedById = lead.Id,
            CreatedAt = now.AddDays(-1),
            UpdatedAt = now.AddDays(-1),
        };
        db.KnownErrors.Add(error);
        db.TicketComments.Add(new TicketComment
        {
            TicketId = main.Id, Visibility = CommentVisibility.Internal, ByStaff = true, AuthorId = lead.Id, Language = context.Organisation.DefaultLanguage, CreatedAt = now.AddDays(-2),
            Body = government ? "تمت مقارنة سجلات الخادم في أوقات الانقطاعات، وتظهر جميعها انتظار اتصالات قاعدة البيانات." : "Compared the gateway logs at each delay: the queue is full every time.",
        });

        // A second problem, new in the group's queue, from two other incidents.
        var second = await NewAsync(
            government ? "تكرار أعطال طابعات مراكز الخدمة" : "Repeated VPN disconnects",
            government ? "عدة بلاغات عن طابعات مراكز الخدمة خلال الشهر." : "Several incidents report VPN sessions dropping every few minutes.",
            others.FirstOrDefault()?.CategoryId, now.AddHours(-5)).ConfigureAwait(false);
        second.Problem = new ProblemRecord { TicketId = second.Id, Priority = 3 };
        foreach (var incident in others)
        {
            db.ProblemIncidents.Add(new ProblemIncident { ProblemId = second.Id, IncidentId = incident.Id, LinkedById = lead.Id, LinkedAt = now.AddHours(-5) });
        }

        await new Tickets.Sla.SlaTracker(db, context.Get<Data.Auditing.IAuditLog>()).UpdateAsync([main, second], now, cancellationToken).ConfigureAwait(false);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        var indexer = context.Get<ISearchIndexer>();
        var roles = await ProblemSearch.StaffRolesAsync(db, cancellationToken).ConfigureAwait(false);
        indexer.Upsert(ProblemSearch.Document(main, roles));
        indexer.Upsert(ProblemSearch.Document(second, roles));
        indexer.Upsert(ProblemSearch.Document(error, roles));
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
