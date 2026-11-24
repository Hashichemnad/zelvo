using Microsoft.EntityFrameworkCore;
using Zelvo.Api.Incidents;
using Zelvo.Api.Settings;
using Zelvo.Data;
using Zelvo.Data.Localization;
using Zelvo.Data.Tickets;
using Zelvo.Search;
using Zelvo.Tickets;

namespace Zelvo.Api.Demo;

/// <summary>
/// Task B1: sample incidents of each organisation, in its main language: open, on hold, resolved
/// and closed incidents, and a major incident with two child incidents and its communication
/// timeline. Fictional, like everything in the demo.
/// </summary>
internal sealed class DemoIncidentsSeeder : IDemoSeeder
{
    public string Key => "b1-incidents";

    public int Order => 100;

    private sealed record Sample(string Title, string Description, int Impact, int Urgency, TicketState State, string Requester, string? Assignee, int DaysAgo,
        TicketChannel Channel, string? Reason = null, string? Notes = null, string? ParentTitle = null, bool Major = false);

    private static readonly Sample[] Government =
    [
        new("انقطاع بوابة الخدمات الإلكترونية", "لا يتمكن المتعاملون من الدخول إلى بوابة الخدمات الإلكترونية منذ الساعة الثامنة صباحاً.", 1, 1, TicketState.InProgress, "lead", "agent", 0, TicketChannel.Phone, Major: true),
        new("تعذّر دفع رسوم رخصة البناء", "تظهر رسالة خطأ عند محاولة دفع رسوم رخصة البناء عبر البوابة.", 2, 1, TicketState.Assigned, "requester", "agent", 0, TicketChannel.Portal, ParentTitle: "انقطاع بوابة الخدمات الإلكترونية"),
        new("لا يمكن تتبع طلب شهادة الإنجاز", "صفحة تتبع الطلبات لا تفتح.", 3, 2, TicketState.New, "requester", null, 0, TicketChannel.Portal, ParentTitle: "انقطاع بوابة الخدمات الإلكترونية"),
        new("طابعة مكتب خدمة المتعاملين لا تعمل", "الطابعة في الطابق الأرضي تعرض رسالة انحشار الورق باستمرار.", 3, 2, TicketState.OnHold, "requester", "agent", 2, TicketChannel.Portal, Reason: "awaiting-supplier"),
        new("بطء نظام إدارة المعاملات", "يستغرق فتح المعاملة أكثر من دقيقة.", 2, 2, TicketState.Resolved, "requester", "agent", 3, TicketChannel.Email, Notes: "تمت زيادة ذاكرة خادم قاعدة البيانات وعاد الأداء إلى طبيعته."),
        new("نسيت كلمة المرور لحساب البريد", "لا أستطيع الدخول إلى بريدي الإلكتروني الحكومي.", 3, 3, TicketState.Closed, "requester", "agent", 12, TicketChannel.Phone, Notes: "تمت إعادة تعيين كلمة المرور بعد التحقق من الهوية."),
    ];

    private static readonly Sample[] Company =
    [
        new("Email delivery delayed for all staff", "Outgoing email is queued for up to an hour since this morning.", 1, 1, TicketState.InProgress, "lead", "agent", 0, TicketChannel.Phone, Major: true),
        new("Customer invoices not sent", "The billing system's invoice emails are not arriving at customers.", 2, 1, TicketState.Assigned, "requester", "agent", 0, TicketChannel.Portal, ParentTitle: "Email delivery delayed for all staff"),
        new("Password reset emails not arriving", "I asked for a password reset twice and received nothing.", 3, 2, TicketState.New, "requester", null, 0, TicketChannel.Portal, ParentTitle: "Email delivery delayed for all staff"),
        new("Laptop docking station not detected", "My second monitor stays black when I connect the dock.", 3, 3, TicketState.OnHold, "requester", "agent", 1, TicketChannel.Portal, Reason: "awaiting-requester"),
        new("VPN disconnects every few minutes", "Working from home, the VPN drops several times an hour.", 2, 2, TicketState.Resolved, "requester", "agent", 4, TicketChannel.Chat, Notes: "Updated the VPN client to the latest version; the connection is stable."),
        new("Cannot open shared finance folder", "Access denied on the finance shared drive.", 3, 2, TicketState.Closed, "requester", "agent", 15, TicketChannel.Email, Notes: "Added the user to the finance readers group."),
    ];

    public async Task SeedAsync(DemoSeedContext context, CancellationToken cancellationToken)
    {
        var db = context.Get<ZelvoDbContext>();
        var settings = context.Get<TenantSettingsStore>();
        var indexer = context.Get<ISearchIndexer>();
        var matrix = await settings.GetAsync(IncidentSettings.PriorityMatrix, cancellationToken).ConfigureAwait(false);
        var prefixes = await settings.GetAsync(IncidentSettings.NumberPrefixes, cancellationToken).ConfigureAwait(false);
        var users = (await db.Users.AsNoTracking().Where(u => u.Email != null).Select(u => new { u.Email, u.Id }).ToListAsync(cancellationToken).ConfigureAwait(false))
            .GroupBy(u => u.Email!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Id, StringComparer.OrdinalIgnoreCase);
        Guid UserId(string persona) => users[context.Organisation.UsernameOf(context.Organisation.User(persona)!)];

        var government = context.Organisation.Kind == DemoOrganisationKind.Government;
        var samples = government ? Government : Company;
        var now = DateTimeOffset.UtcNow;
        var created = new Dictionary<string, Ticket>(StringComparer.Ordinal);
        foreach (var sample in samples)
        {
            var sequence = await TicketNumbers.NextAsync(db, context.Tenant.Id, TicketType.Incident, cancellationToken).ConfigureAwait(false);
            var at = now.AddDays(-sample.DaysAgo).AddHours(-1 - created.Count);
            var ticket = new Ticket
            {
                Type = TicketType.Incident,
                Number = TicketNumbers.Format(TicketNumbers.PrefixOf(prefixes, TicketType.Incident), sequence),
                Sequence = sequence,
                Title = sample.Title,
                Description = sample.Description,
                Channel = sample.Channel,
                RequesterId = UserId(sample.Requester),
                CreatedById = UserId(sample.Channel == TicketChannel.Portal ? sample.Requester : "agent"),
                AssigneeId = sample.Assignee is null ? null : UserId(sample.Assignee),
                ParentId = sample.ParentTitle is null ? null : created[sample.ParentTitle].Id,
                State = sample.State,
                OnHoldReason = sample.Reason,
                ResolutionNotes = sample.Notes,
                CreatedAt = at,
                UpdatedAt = at,
                ResolvedAt = sample.State is TicketState.Resolved or TicketState.Closed ? at.AddHours(4) : null,
                ClosedAt = sample.State == TicketState.Closed ? at.AddDays(5) : null,
            };
            ticket.Incident = new Incident
            {
                TicketId = ticket.Id,
                Impact = sample.Impact,
                Urgency = sample.Urgency,
                Priority = matrix.PriorityOf(sample.Impact, sample.Urgency),
                IsMajor = sample.Major,
                MajorDeclaredAt = sample.Major ? at.AddMinutes(20) : null,
                MajorDeclaredById = sample.Major ? UserId("lead") : null,
            };
            db.Tickets.Add(ticket);
            created[sample.Title] = ticket;
        }

        var major = created.Values.Single(t => t.Incident!.IsMajor);
        var lead = UserId("lead");
        db.MajorIncidentUpdates.AddRange(
            new MajorIncidentUpdate
            {
                TicketId = major.Id,
                Body = government
                    ? new LocalizedText("We are aware that the e-services portal is unavailable and are working to restore it.", "نعلم بتعذّر الوصول إلى بوابة الخدمات الإلكترونية، ونعمل على استعادتها في أقرب وقت.")
                    : new LocalizedText("We are aware of delayed email and are working with our provider to restore normal delivery.", "نعلم بتأخر وصول البريد الإلكتروني، ونعمل مع مزوّد الخدمة على إعادته إلى وضعه الطبيعي."),
                Audience = UpdateAudience.Requesters,
                PostedById = lead,
                PostedAt = major.CreatedAt.AddMinutes(30),
            },
            new MajorIncidentUpdate
            {
                TicketId = major.Id,
                Body = new LocalizedText("Cause found: a failed load balancer node. Failover in progress.", "تم تحديد السبب: تعطل إحدى عُقد موازنة الأحمال، وجارٍ التحويل إلى العقدة الاحتياطية."),
                Audience = UpdateAudience.Internal,
                PostedById = lead,
                PostedAt = major.CreatedAt.AddMinutes(50),
            });

        await IncidentEndpoints.IndexAsync(db, indexer, created.Values, cancellationToken).ConfigureAwait(false);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
