using Microsoft.EntityFrameworkCore;
using Zelvo.Api.Settings;
using Zelvo.Api.Voice;
using Zelvo.Data;
using Zelvo.Data.Identity;
using Zelvo.Data.Tickets;
using Zelvo.Data.Voice;
using Zelvo.Tickets;

namespace Zelvo.Api.Demo;

/// <summary>
/// Task C6: six months of complaints, inquiries and suggestions for each sample organisation, in
/// English and Arabic, in every closure state (confirmed, rejected and unanswered complaints;
/// answered inquiries; suggestions under review, accepted, implemented and declined), with
/// happiness ratings on resolved tickets and in the portal that improve over the months, so the
/// customer voice report shows trends. The government entity allows anonymous submissions.
/// </summary>
internal sealed class DemoCustomerVoiceSeeder : IDemoSeeder
{
    public string Key => "c6-customer-voice";

    public int Order => 160;

    private sealed record Sample(TicketType Type, string En, string Ar, string? DetailEn, string? DetailAr);

    private static readonly Sample[] Samples =
    [
        new(TicketType.Complaint, "Long waiting time at the service centre", "طول فترة الانتظار في مركز الخدمة",
            "I waited more than an hour although I had an appointment.", "انتظرت أكثر من ساعة رغم وجود موعد مسبق."),
        new(TicketType.Inquiry, "Which documents do I need to renew my permit?", "ما المستندات المطلوبة لتجديد التصريح؟", null, null),
        new(TicketType.Suggestion, "Add evening hours at the service centre", "إضافة فترة عمل مسائية في مركز الخدمة",
            "Many people work during the day and cannot visit before 3 pm.", "كثير من المتعاملين يعملون نهارًا ولا يستطيعون الحضور قبل الساعة الثالثة."),
        new(TicketType.Complaint, "The online payment failed twice", "فشل الدفع الإلكتروني مرتين",
            "The amount was taken from my card but the application shows unpaid.", "خُصم المبلغ من بطاقتي لكن الطلب يظهر غير مدفوع."),
        new(TicketType.Inquiry, "How long does a building permit take?", "كم تستغرق مدة إصدار رخصة البناء؟", null, null),
        new(TicketType.Suggestion, "Send reminders before documents expire", "إرسال تذكير قبل انتهاء صلاحية المستندات", null, null),
        new(TicketType.Complaint, "Unclear reason for rejecting my application", "سبب رفض الطلب غير واضح",
            "The rejection message did not say what to correct.", "لم توضح رسالة الرفض ما يجب تصحيحه."),
        new(TicketType.Inquiry, "Can I submit the request on behalf of my father?", "هل يمكنني تقديم الطلب نيابة عن والدي؟", null, null),
        new(TicketType.Suggestion, "Offer the service in the mobile app", "توفير الخدمة عبر التطبيق الذكي", null, null),
    ];

    private static readonly string[] RootCauses = ["service-delay", "system", "information", "process", "staff-conduct"];

    public async Task SeedAsync(DemoSeedContext context, CancellationToken cancellationToken)
    {
        var db = context.Get<ZelvoDbContext>();
        var settings = context.Get<TenantSettingsStore>();
        var government = context.Organisation.Kind == DemoOrganisationKind.Government;
        var users = await db.Users.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
        UserAccount? Persona(string persona) =>
            context.Organisation.User(persona) is { } demo ? users.SingleOrDefault(u => u.Email == context.Organisation.UsernameOf(demo)) : null;
        var requester = Persona("requester");
        var agent = Persona("agent");
        if (requester is null)
        {
            return;
        }

        if (government)
        {
            var voiceSettings = await settings.GetAsync(VoiceSettings.Voice, cancellationToken).ConfigureAwait(false);
            await settings.SetAsync(VoiceSettings.Voice, voiceSettings with { AnonymousEnabled = true }, cancellationToken).ConfigureAwait(false);
        }

        var voice = await settings.GetAsync(VoiceSettings.Voice, cancellationToken).ConfigureAwait(false);
        var prefixes = await settings.GetAsync(Incidents.IncidentSettings.NumberPrefixes, cancellationToken).ConfigureAwait(false);
        var services = await db.CatalogServices.AsNoTracking().Where(s => s.PublishedVersionId != null).OrderBy(s => s.Code).Select(s => s.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
        var group = agent is null ? null : await db.SupportGroupMembers.AsNoTracking().Where(m => m.UserId == agent.Id).Select(m => (Guid?)m.GroupId).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        var anonymousId = government ? await AnonymousAccounts.GetOrCreateAsync(db, cancellationToken).ConfigureAwait(false) : (Guid?)null;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        var now = DateTimeOffset.UtcNow;
        var arabic = requester.PreferredLanguage == "ar";
        var open = new List<Ticket>();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        // 27 records over about six months, newest last; the last few are still open.
        const int count = 27;
        for (var i = 0; i < count; i++)
        {
            var sample = Samples[i % Samples.Length];
            var created = now.AddDays(-180 + (i * 6.5)).AddHours(9 + (i % 5));
            var isOpen = i >= count - 5;
            var anonymous = anonymousId is not null && i % 8 == 3;
            var sequence = await TicketNumbers.NextAsync(db, context.Tenant.Id, sample.Type, cancellationToken).ConfigureAwait(false);
            var useArabic = government ? i % 2 == 0 : arabic && i % 3 == 0;
            var ticket = new Ticket
            {
                Type = sample.Type,
                Number = TicketNumbers.Format(TicketNumbers.PrefixOf(prefixes, sample.Type), sequence),
                Sequence = sequence,
                Title = useArabic ? sample.Ar : sample.En,
                Description = useArabic ? sample.DetailAr : sample.DetailEn,
                Channel = i % 4 == 1 ? TicketChannel.Phone : TicketChannel.Portal,
                RequesterId = anonymous ? anonymousId!.Value : requester.Id,
                CreatedById = anonymous ? null : requester.Id,
                GroupId = group,
                CreatedAt = created,
                UpdatedAt = created,
            };
            var severity = sample.Type == TicketType.Complaint ? 1 + (i % 3) : (int?)null;
            ticket.Voice = new CustomerVoiceRecord
            {
                TicketId = ticket.Id,
                Priority = voice.PriorityOf(sample.Type, severity),
                Severity = severity,
                ServiceId = services.Count > 0 && i % 3 != 2 ? services[i % services.Count] : null,
                Review = sample.Type == TicketType.Suggestion ? SuggestionReview.UnderReview : null,
                Anonymous = anonymous,
                TrackingCodeHash = anonymous ? VoiceCore.HashCode(VoiceCore.NewTrackingCode()) : null,
            };
            if (agent is not null && i % 6 != 5)
            {
                ticket.AssigneeId = agent.Id;
                ticket.State = TicketState.Assigned;
            }

            if (!isOpen)
            {
                var resolved = created.AddHours(20 + (i % 4 * 18));
                ticket.RespondedAt = created.AddHours(2);
                ticket.ResolvedAt = resolved;
                ticket.ClosedAt = resolved.AddDays(3);
                ticket.State = TicketState.Closed;
                ticket.UpdatedAt = resolved.AddDays(3);
                switch (sample.Type)
                {
                    case TicketType.Complaint:
                        ticket.ResolutionNotes = useArabic ? "تمت مراجعة الإجراء مع الفريق المختص وتصحيح الخطأ." : "The case was reviewed with the team and the error corrected.";
                        ticket.Voice.RootCauseKey = RootCauses[i % RootCauses.Length];
                        ticket.Voice.Confirmation = i % 5 == 2 ? ComplaintConfirmation.NotConfirmed : ComplaintConfirmation.Confirmed;
                        ticket.Voice.ConfirmationAt = ticket.ClosedAt;
                        break;
                    case TicketType.Inquiry:
                        ticket.ResolutionNotes = useArabic
                            ? "يلزم إرفاق صورة الهوية والتصريح السابق وإثبات العنوان، ويمكن التقديم عبر البوابة."
                            : "You need your Emirates ID, the previous permit and proof of address; you can apply through the portal.";
                        break;
                    default:
                        var review = i % 4 == 2 ? SuggestionReview.Declined : SuggestionReview.Implemented;
                        ticket.Voice.Review = review;
                        ticket.Voice.ReviewFeedback = review == SuggestionReview.Implemented
                            ? (useArabic ? "شكرًا لكم، تم تنفيذ الاقتراح." : "Thank you; we have put your suggestion in place.")
                            : (useArabic ? "شكرًا لكم، لا يمكن تنفيذ الاقتراح حاليًا لأسباب تنظيمية." : "Thank you; we cannot do this now for regulatory reasons.");
                        ticket.ResolutionNotes = ticket.Voice.ReviewFeedback;
                        break;
                }
            }
            else if (sample.Type == TicketType.Suggestion && i % 2 == 0)
            {
                ticket.Voice.Review = SuggestionReview.Accepted;
                ticket.Voice.ReviewFeedback = useArabic ? "اقتراح مقبول، وسيُدرج في خطة التحسين." : "Accepted; it is in our improvement plan.";
            }

            db.Tickets.Add(ticket);
            if (isOpen)
            {
                open.Add(ticket);
            }
            else if (i % 3 != 1)
            {
                // Happiness improves over the months (GOV-2 trends): 3-face scale.
                var score = i < 9 ? 1 + (i % 3) : i < 18 ? 2 + (i % 2) : 3 - (i % 5 == 0 ? 1 : 0);
                db.HappinessRatings.Add(new HappinessRating
                {
                    TicketId = ticket.Id,
                    RecordType = ticket.Type,
                    Channel = ticket.Channel,
                    ServiceId = ticket.Voice.ServiceId,
                    UserId = anonymous ? null : requester.Id,
                    Score = score,
                    Points = 3,
                    CreatedAt = ticket.ResolvedAt!.Value.AddHours(5),
                });
            }

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        // Ratings of resolved incidents and requests, and general portal ratings over the months.
        var resolvedOthers = await db.Tickets.AsNoTracking()
            .Where(t => (t.Type == TicketType.Incident || t.Type == TicketType.ServiceRequest) && (t.State == TicketState.Resolved || t.State == TicketState.Closed)
                && !db.HappinessRatings.Any(r => r.TicketId == t.Id))
            .Take(6).ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var (ticket, index) in resolvedOthers.Select((t, i) => (t, i)))
        {
            db.HappinessRatings.Add(new HappinessRating
            {
                TicketId = ticket.Id, RecordType = ticket.Type, Channel = ticket.Channel, UserId = ticket.RequesterId,
                Score = index % 4 == 0 ? 2 : 3, Points = 3, CreatedAt = now.AddDays(-index * 9),
            });
        }

        for (var day = 0; day < 36; day++)
        {
            db.HappinessRatings.Add(new HappinessRating
            {
                DeviceHash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Guid.NewGuid().ToByteArray())),
                Score = day < 12 ? 1 + (day % 3) : day < 24 ? 2 + (day % 2) : 3 - (day % 6 == 0 ? 1 : 0),
                Points = 3,
                CreatedAt = now.AddDays(-175 + (day * 5)),
            });
        }

        await new Tickets.Sla.SlaTracker(db, context.Get<Data.Auditing.IAuditLog>()).UpdateAsync(open, now, cancellationToken).ConfigureAwait(false);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        var indexer = context.Get<Zelvo.Search.ISearchIndexer>();
        var staff = await VoiceSearch.StaffRolesAsync(db, cancellationToken).ConfigureAwait(false);
        foreach (var ticket in await VoiceCore.Records(db).AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false))
        {
            indexer.Upsert(VoiceSearch.Document(ticket, staff));
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
