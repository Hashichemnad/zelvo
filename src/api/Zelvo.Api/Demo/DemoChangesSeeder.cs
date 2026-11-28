using Microsoft.EntityFrameworkCore;
using Zelvo.Api.Catalog;
using Zelvo.Api.Changes;
using Zelvo.Data;
using Zelvo.Data.Changes;
using Zelvo.Data.Localization;
using Zelvo.Data.Tickets;
using Zelvo.Search;
using Zelvo.Tickets;

namespace Zelvo.Api.Demo;

/// <summary>
/// Task H3: each sample organisation's changes. Two standard change templates; a year-end blackout
/// for the whole organisation and one on the main business service; a normal change that fixes the
/// H1 problem, scheduled on the outage's items with tasks for two teams; a high-risk normal change
/// ready to submit (so the approval path can be shown live); an emergency change implemented and
/// waiting for its review; and past changes with their review outcomes (successful and backed out)
/// that the what-if simulation (H5) learns from. Fictional, like everything in the demo.
/// </summary>
internal sealed class DemoChangesSeeder : IDemoSeeder
{
    public string Key => "h3-changes";

    // After the problems (h1-problems) the first change fixes.
    public int Order => 300;

    public async Task SeedAsync(DemoSeedContext context, CancellationToken cancellationToken)
    {
        var db = context.Get<ZelvoDbContext>();
        var g = context.Organisation.Kind == DemoOrganisationKind.Government;
        var leadEmail = context.Organisation.User("lead") is { } l ? context.Organisation.UsernameOf(l) : null;
        var agentEmail = context.Organisation.User("agent") is { } a ? context.Organisation.UsernameOf(a) : null;
        var lead = leadEmail is null ? null : await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Email == leadEmail, cancellationToken).ConfigureAwait(false);
        var agent = agentEmail is null ? null : await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Email == agentEmail, cancellationToken).ConfigureAwait(false);
        if (lead is null)
        {
            return;
        }

        var groupId = await db.SupportGroupMembers.AsNoTracking().Where(m => m.UserId == lead.Id).Select(m => (Guid?)m.GroupId).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        var otherGroup = await db.SupportGroups.AsNoTracking().Where(x => x.Active && x.Id != groupId).OrderBy(x => x.Name.En).Select(x => (Guid?)x.Id).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        var problem = await db.Tickets.AsNoTracking().Where(t => t.Type == TicketType.Problem).OrderBy(t => t.Sequence).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        var problemItems = problem is null ? [] : await db.TicketConfigurationItems.AsNoTracking().Where(x => x.TicketId == problem.Id)
            .Select(x => x.ConfigurationItemId).ToListAsync(cancellationToken).ConfigureAwait(false);
        // The items the outage's services run on (servers, databases): the change touches those too.
        var below = await db.CiRelationships.AsNoTracking().Where(r => problemItems.Contains(r.SourceId) && r.TargetCiId != null)
            .Select(r => r.TargetCiId!.Value).Distinct().Take(3).ToListAsync(cancellationToken).ConfigureAwait(false);
        var items = problemItems.Concat(below).Distinct().ToList();
        var mainService = problemItems.FirstOrDefault();

        var settings = ChangeSettingsValue.Default;
        var prefixes = await TicketSettingKeys.ReadAsync<IReadOnlyDictionary<TicketType, string>>(db, TicketSettingKeys.NumberPrefixes,
            TicketNumbers.DefaultPrefixes, TicketNumbers.AreValid, cancellationToken).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        var tonight = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero).AddHours(18);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        static LocalizedText T(string en, string ar) => new(en, ar);

        // The catalog of standard changes.
        var restart = new ChangeTemplate
        {
            Name = T("Scheduled restart of an application server", "إعادة تشغيل مجدولة لخادم تطبيقات"),
            Description = T("Restart one application server of a cluster outside working hours, one node at a time.", "إعادة تشغيل خادم تطبيقات واحد من المجموعة خارج ساعات العمل، خادمًا تلو الآخر."),
            GroupId = groupId,
            ImplementationPlan = g ? "إخراج الخادم من موازن الأحمال، ثم إعادة تشغيله، ثم التحقق من السجلات وإعادته إلى الموازن." : "Drain the node from the load balancer, restart it, check the logs and put it back.",
            TestPlan = g ? "فتح صفحة الفحص الصحي للبوابة والتأكد من استجابتها." : "Open the health page and send a test email.",
            BackoutPlan = g ? "إبقاء الخادم خارج الموازن وإبلاغ فريق البنية التحتية." : "Keep the node out of the load balancer and call the infrastructure team.",
            Tasks = ChangeCore.WriteTasks([new FulfilmentTask { Key = "task1", Name = T("Restart and check the node", "إعادة التشغيل والتحقق من الخادم"), GroupId = groupId, TargetMinutes = 60 }]),
        };
        var patches = new ChangeTemplate
        {
            Name = T("Monthly security patches for desktops", "التحديثات الأمنية الشهرية لأجهزة المكتب"),
            Description = T("Roll out the month's approved operating system patches to desktops and laptops in waves.", "نشر تحديثات نظام التشغيل المعتمدة لهذا الشهر على أجهزة المكتب والحواسيب المحمولة على دفعات."),
            GroupId = groupId,
            ImplementationPlan = g ? "نشر التحديثات على مجموعة تجريبية، ثم على بقية الأجهزة في اليوم التالي." : "Deploy to the pilot ring, then to everyone the next day.",
            TestPlan = g ? "التحقق من تقرير الامتثال في أداة إدارة الأجهزة." : "Check the compliance report in the device management tool.",
            BackoutPlan = g ? "إلغاء نشر التحديث المتسبب في المشكلة من أداة إدارة الأجهزة." : "Withdraw the faulty patch from the device management tool.",
            Tasks = ChangeCore.WriteTasks(
            [
                new FulfilmentTask { Key = "task1", Name = T("Pilot ring", "المجموعة التجريبية"), GroupId = groupId, TargetMinutes = 240 },
                new FulfilmentTask { Key = "task2", Name = T("All devices", "جميع الأجهزة"), GroupId = groupId, TargetMinutes = 480 },
            ]),
        };
        db.ChangeTemplates.AddRange(restart, patches);

        // Blackout periods: year-end for everyone, and the main service in its busiest week.
        var yearEnd = new DateTimeOffset(now.Year, 12, 28, 0, 0, 0, TimeSpan.Zero);
        db.ChangeBlackouts.Add(new ChangeBlackout
        {
            Name = g ? T("Fiscal year-end closing", "إقفال نهاية السنة المالية") : T("Year-end closing", "إقفال نهاية السنة"),
            StartsAt = yearEnd,
            EndsAt = yearEnd.AddDays(6),
        });
        if (mainService != Guid.Empty)
        {
            db.ChangeBlackouts.Add(new ChangeBlackout
            {
                Name = g ? T("Peak week of the e-services portal", "أسبوع الذروة لبوابة الخدمات الإلكترونية") : T("Quarter-end for email", "نهاية الربع للبريد الإلكتروني"),
                StartsAt = tonight.AddDays(10),
                EndsAt = tonight.AddDays(17),
                ConfigurationItemId = mainService,
            });
        }

        async Task<Ticket> NewAsync(string title, string description, ChangeKind kind, DateTimeOffset at, ChangeTemplate? template = null, Guid? raisedBy = null)
        {
            var sequence = await TicketNumbers.NextAsync(db, context.Tenant.Id, TicketType.Change, cancellationToken).ConfigureAwait(false);
            var ticket = new Ticket
            {
                Type = TicketType.Change,
                Number = TicketNumbers.Format(TicketNumbers.PrefixOf(prefixes, TicketType.Change), sequence),
                Sequence = sequence,
                Title = title,
                Description = description,
                Channel = TicketChannel.Portal,
                RequesterId = raisedBy ?? lead.Id,
                CreatedById = raisedBy ?? lead.Id,
                GroupId = groupId,
                CreatedAt = at,
                UpdatedAt = at,
            };
            ticket.Change = new ChangeRecord { TicketId = ticket.Id, Kind = kind, TemplateId = template?.Id };
            db.Tickets.Add(ticket);
            return ticket;
        }

        void Assess(ChangeRecord change, params string[] options)
        {
            var answers = settings.Questions.Zip(options).ToDictionary(x => x.First.Key, x => x.Second, StringComparer.Ordinal);
            var (score, level, _) = settings.Assess(answers);
            change.RiskAnswers = ChangeCore.WriteAnswers(answers);
            change.RiskScore = score;
            change.RiskLevel = level;
        }

        void Items(Ticket ticket, IEnumerable<Guid> ids)
        {
            foreach (var id in ids.Distinct())
            {
                db.TicketConfigurationItems.Add(new Data.Cmdb.TicketConfigurationItem { TicketId = ticket.Id, ConfigurationItemId = id, AddedById = lead.Id });
            }
        }

        // 1. The fix of the H1 problem: normal, medium risk, approved and scheduled, with tasks for two teams.
        var fix = await NewAsync(
            g ? "توسيع مجمّع اتصالات قاعدة بيانات البوابة" : "Run the mail gateway's spam scanner on four threads",
            g ? "رفع الحد الأقصى لاتصالات قاعدة البيانات لمعالجة انقطاعات البوابة في ساعات الذروة." : "Raise the scanner's threads so the gateway queue keeps up on busy mornings.",
            ChangeKind.Normal, now.AddDays(-1)).ConfigureAwait(false);
        Assess(fix.Change!, "department", "sometimes", "window", "easy", "partial");
        fix.Change!.ImplementationPlan = g ? "١. أخذ نسخة من الإعدادات.\n٢. رفع الحد من ١٠٠ إلى ٣٠٠ اتصال.\n٣. إعادة تشغيل خوادم التطبيق واحدًا تلو الآخر." : "1. Back up the gateway settings.\n2. Set the scanner threads from 1 to 4.\n3. Restart the gateway service.";
        fix.Change.TestPlan = g ? "اختبار حمل على بيئة الاختبار ومتابعة لوحة المراقبة صباح اليوم التالي." : "Send test mail and watch the queue length the next morning.";
        fix.Change.BackoutPlan = g ? "استعادة الإعدادات المحفوظة وإعادة تشغيل الخوادم." : "Restore the saved settings and restart the service.";
        fix.Change.PlannedStart = tonight.AddDays(3).AddHours(4);
        fix.Change.PlannedEnd = tonight.AddDays(3).AddHours(7);
        fix.Change.SubmittedAt = now.AddHours(-20);
        fix.Change.ApprovedAt = now.AddHours(-4);
        fix.Change.Phase = ChangePhase.Scheduled;
        TicketWorkflow.Assign(fix, agent?.Id ?? lead.Id, now.AddHours(-20));
        Items(fix, items);
        if (problem is not null)
        {
            db.ChangeLinks.Add(new ChangeLink { ChangeId = fix.Id, TicketId = problem.Id, Kind = ChangeLinkKind.Fixes, LinkedById = lead.Id, LinkedAt = now.AddDays(-1) });
        }

        await RequestTasks.AddWaitingAsync(db, fix,
        [
            new FulfilmentTask { Key = "task1", Name = T("Change the database settings", "تعديل إعدادات قاعدة البيانات"), GroupId = otherGroup ?? groupId, TargetMinutes = 60 },
            new FulfilmentTask { Key = "task2", Name = T("Restart and check the application", "إعادة تشغيل التطبيق والتحقق منه"), GroupId = groupId, TargetMinutes = 60 },
        ], now, cancellationToken).ConfigureAwait(false);

        // 2. A high-risk normal change, planned and assessed, ready to submit to the board.
        var migration = await NewAsync(
            g ? "نقل نظام إدارة المعاملات إلى مركز البيانات الجديد" : "Move billing to the new data centre",
            g ? "نقل خوادم نظام إدارة المعاملات وقاعدة بياناته إلى مركز البيانات الجديد." : "Move the billing servers and database to the new data centre.",
            ChangeKind.Normal, now.AddHours(-6), raisedBy: agent?.Id).ConfigureAwait(false);
        Assess(migration.Change!, "organisation", "never", "window", "hard", "partial");
        migration.Change!.ImplementationPlan = g ? "نسخ قاعدة البيانات، ثم نقل الخوادم، ثم تحويل أسماء النطاقات." : "Copy the database, move the servers, then switch DNS.";
        migration.Change.TestPlan = g ? "اختبار المعاملات الرئيسية مع فريق العمليات." : "Run the month-end billing test with finance.";
        migration.Change.BackoutPlan = g ? "إعادة أسماء النطاقات إلى المركز الحالي." : "Point DNS back to the current data centre.";
        migration.Change.PlannedStart = tonight.AddDays(12);
        migration.Change.PlannedEnd = tonight.AddDays(12).AddHours(10);
        Items(migration, below.Take(1));

        // 3. An emergency change, implemented last night; its review is due.
        var emergency = await NewAsync(
            g ? "تطبيق تحديث أمني عاجل على خادم البوابة" : "Urgent security patch on the VPN gateway",
            g ? "ثغرة معلنة تتطلب تحديثًا فوريًا." : "A published vulnerability needed patching straight away.",
            ChangeKind.Emergency, now.AddDays(-1).AddHours(-3)).ConfigureAwait(false);
        Assess(emergency.Change!, "organisation", "often", "business", "easy", "partial");
        emergency.Change!.ImplementationPlan = g ? "تطبيق التحديث وإعادة التشغيل." : "Apply the vendor's patch and restart.";
        emergency.Change.BackoutPlan = g ? "إزالة التحديث من لوحة التحكم." : "Uninstall the patch from the console.";
        emergency.Change.PlannedStart = now.AddHours(-20);
        emergency.Change.PlannedEnd = now.AddHours(-18);
        emergency.Change.SubmittedAt = now.AddDays(-1).AddHours(-2);
        emergency.Change.ApprovedAt = now.AddDays(-1).AddHours(-1);
        emergency.Change.ActualStart = now.AddHours(-20);
        emergency.Change.ActualEnd = now.AddHours(-19);
        TicketWorkflow.Assign(emergency, lead.Id, now.AddDays(-1));
        TicketWorkflow.Apply(emergency, TicketState.InProgress, null, null, now.AddHours(-20));
        emergency.Change.Phase = ChangePhase.Implementation;
        TicketWorkflow.Apply(emergency, TicketState.Resolved, null, g ? "طُبّق التحديث دون انقطاع يُذكر." : "Patched with five minutes of downtime.", now.AddHours(-19));
        Items(emergency, items.Take(1));

        // 4. Past changes with their reviews: what H5's predicted risk learns from.
        async Task PastAsync(string title, ChangeOutcome outcome, string review, int daysAgo, ChangeTemplate? template = null)
        {
            var past = await NewAsync(title, title, template is null ? ChangeKind.Normal : ChangeKind.Standard, now.AddDays(-daysAgo - 2), template).ConfigureAwait(false);
            var c = past.Change!;
            if (template is null)
            {
                Assess(c, "department", "sometimes", "window", "easy", "full");
            }
            else
            {
                c.RiskScore = 0;
                c.RiskLevel = ChangeRiskLevel.Low;
            }

            c.ImplementationPlan = template?.ImplementationPlan ?? (g ? "وفق الإجراء المعتاد." : "As per the usual procedure.");
            c.BackoutPlan = template?.BackoutPlan ?? (g ? "استعادة النسخة السابقة." : "Restore the previous version.");
            c.PlannedStart = now.AddDays(-daysAgo).AddHours(-3);
            c.PlannedEnd = now.AddDays(-daysAgo);
            c.SubmittedAt = now.AddDays(-daysAgo - 2);
            c.ApprovedAt = now.AddDays(-daysAgo - 1);
            c.ActualStart = now.AddDays(-daysAgo).AddHours(-3);
            c.ActualEnd = now.AddDays(-daysAgo).AddHours(-1);
            c.Outcome = outcome;
            c.ReviewNotes = review;
            c.ReviewedById = lead.Id;
            c.ReviewedAt = now.AddDays(-daysAgo + 1);
            c.Closure = ChangeClosure.Completed;
            TicketWorkflow.Assign(past, lead.Id, now.AddDays(-daysAgo - 2));
            TicketWorkflow.Apply(past, TicketState.InProgress, null, null, now.AddDays(-daysAgo).AddHours(-3));
            c.Phase = ChangePhase.Implementation;
            TicketWorkflow.Apply(past, TicketState.Resolved, null, review, now.AddDays(-daysAgo).AddHours(-1));
            TicketWorkflow.Apply(past, TicketState.Closed, null, null, now.AddDays(-daysAgo + 1));
            Items(past, items.Take(2));
        }

        await PastAsync(g ? "تحديث إصدار خادم تطبيقات البوابة" : "Upgrade the mail gateway's firmware", ChangeOutcome.BackedOut,
            g ? "تسبب الإصدار الجديد في أخطاء بالجلسات، فتمت استعادة الإصدار السابق." : "The new firmware dropped connections; we restored the previous one.", 40).ConfigureAwait(false);
        await PastAsync(g ? "زيادة ذاكرة خادم قاعدة البيانات" : "Add memory to the mail server", ChangeOutcome.Successful,
            g ? "نُفذ وفق الخطة." : "Done as planned.", 25).ConfigureAwait(false);
        await PastAsync(restart.Name.Get(context.Organisation.DefaultLanguage) ?? "Restart", ChangeOutcome.Successful,
            g ? "نُفذ وفق الخطة." : "Done as planned.", 12, restart).ConfigureAwait(false);

        // Task H4: the boards (the team lead chairs, the administrator deputises) and next week's meeting.
        var adminEmail = context.Organisation.User("admin") is { } ad ? context.Organisation.UsernameOf(ad) : null;
        var admin = adminEmail is null ? null : await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Email == adminEmail, cancellationToken).ConfigureAwait(false);
        var boardMembers = new[] { (Guid?)lead.Id, admin?.Id }.OfType<Guid>().ToList();
        var advisory = new ChangeBoard
        {
            Kind = ChangeBoardKind.Advisory,
            Name = T("Change advisory board", "المجلس الاستشاري للتغيير"),
            ChairId = lead.Id,
            DeputyChairId = admin?.Id,
        };
        var emergencyBoard = new ChangeBoard { Kind = ChangeBoardKind.Emergency, Name = T("Emergency change board", "مجلس التغييرات الطارئة"), DecideWithinHours = 4 };
        foreach (var member in boardMembers)
        {
            advisory.Members.Add(new ChangeBoardMember { BoardId = advisory.Id, UserId = member });
            emergencyBoard.Members.Add(new ChangeBoardMember { BoardId = emergencyBoard.Id, UserId = member });
        }

        db.ChangeBoards.AddRange(advisory, emergencyBoard);
        var meeting = new CabMeeting
        {
            Title = T("Weekly change advisory board", "الاجتماع الأسبوعي للمجلس الاستشاري للتغيير"),
            StartsAt = tonight.AddDays(2).AddHours(-8),
            EndsAt = tonight.AddDays(2).AddHours(-7),
            Location = g ? "قاعة الاجتماعات ٣" : "Meeting room 3",
            ChairId = lead.Id,
            CreatedById = lead.Id,
        };
        db.CabMeetings.Add(meeting);
        foreach (var member in boardMembers)
        {
            db.CabMeetingMembers.Add(new CabMeetingMember { MeetingId = meeting.Id, UserId = member });
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        // Task H5: the approved fix keeps the what-if simulation it was approved with.
        await ChangeSimulator.KeepAsync(db, fix, cancellationToken).ConfigureAwait(false);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        var indexer = context.Get<ISearchIndexer>();
        var roles = await ChangeSearch.StaffRolesAsync(db, cancellationToken).ConfigureAwait(false);
        foreach (var ticket in await ChangeCore.Changes(db).AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false))
        {
            indexer.Upsert(ChangeSearch.Document(ticket, roles));
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
