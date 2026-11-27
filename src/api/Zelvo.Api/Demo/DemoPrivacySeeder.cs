using Microsoft.EntityFrameworkCore;
using Zelvo.Data;
using Zelvo.Data.Identity;
using Zelvo.Data.Localization;
using Zelvo.Data.Privacy;

namespace Zelvo.Api.Demo;

/// <summary>
/// Task F5: privacy for each sample organisation: a published bilingual privacy notice (accepted by
/// the demo users, so switching users is not interrupted), two optional purposes (satisfaction
/// surveys, granted by the requester; AI assistance), and an open data subject request from the
/// requester for a copy of their data. Retention periods stay off, as for any new tenant.
/// </summary>
internal sealed class DemoPrivacySeeder : IDemoSeeder
{
    public string Key => "f5-privacy";

    public int Order => 250;

    public async Task SeedAsync(DemoSeedContext context, CancellationToken cancellationToken)
    {
        var db = context.Get<ZelvoDbContext>();
        var name = context.Organisation.Name;
        var notice = new PrivacyNotice
        {
            Version = 1,
            Title = new LocalizedText("Privacy notice", "إشعار الخصوصية"),
            Body = new LocalizedText(
                $"""
                ## Who we are

                {name.En} provides this service portal. We process your personal data to handle your requests, incidents, complaints, inquiries and suggestions, in line with the UAE Personal Data Protection Law.

                ## What we keep

                Your name, email address, phone number and Emirates ID when you give them, what you write to us, the files you send and your satisfaction ratings.

                ## How long

                Closed records are kept for the periods our records policy sets, then their personal data is removed.

                ## For our staff: workload balancing

                When workload balancing is on, Zelvo records once a day, for each member of a support team, the open tickets assigned to them (weighted by priority), new assignments, their changes in Zelvo outside the team's working hours and the share of angry or escalated tickets they handle, so that team leads can rebalance work before anyone is overloaded. Nothing about presence or typing is recorded, and the figures are never used to rank or score people. Team leads see their own groups' members; executives and administrators see group totals only; each person sees their own figures under **Account → My workload**. The figures are kept for 90 days, every view of them is recorded, and they are part of the copy of your data.

                ## Your rights

                Under **Account → Privacy** you can download a copy of your data, ask for it to be corrected or erased, or ask us to restrict or stop processing it. We answer within 30 days.
                """,
                $"""
                ## من نحن

                تقدّم {name.Ar} بوابة الخدمات هذه، ونعالج بياناتكم الشخصية لمتابعة طلباتكم وبلاغاتكم وشكاواكم واستفساراتكم واقتراحاتكم، وفقاً لقانون حماية البيانات الشخصية في دولة الإمارات العربية المتحدة.

                ## البيانات التي نحتفظ بها

                الاسم والبريد الإلكتروني ورقم الهاتف ورقم الهوية الإماراتية عند تقديمها، وما تكتبونه إلينا، والملفات التي ترسلونها، وتقييماتكم لمستوى الرضا.

                ## مدة الاحتفاظ

                نحتفظ بالسجلات المغلقة للمدد التي تحددها سياسة السجلات لدينا، ثم تُحذف بياناتها الشخصية.

                ## لموظفينا: موازنة أعباء العمل

                عند تفعيل موازنة أعباء العمل، يسجّل زيلفو مرة واحدة يومياً لكل عضو في فرق الدعم التذاكرَ المفتوحة المسندة إليه (مرجّحة بحسب الأولوية)، والإسنادات الجديدة، وتغييراته في زيلفو خارج ساعات عمل الفريق، ونسبة التذاكر التي فيها استياء أو تصعيد مما يعالجه، ليتمكن قادة الفرق من إعادة توزيع العمل قبل أن تتجاوز الأعباء طاقة أي موظف. ولا يُسجَّل شيء عن الحضور أو الكتابة، ولا تُستخدم الأرقام لترتيب الموظفين أو تقييمهم. ويطّلع قادة الفرق على أعضاء مجموعاتهم، بينما لا يطّلع التنفيذيون والمسؤولون إلا على إجماليات المجموعات، ويطّلع كل شخص على أرقامه من صفحة **الحساب ← أعباء عملي**. وتُحفظ الأرقام لمدة 90 يوماً، ويُسجَّل كل اطلاع عليها، وتُدرج في نسخة بياناتكم.

                ## حقوقكم

                من صفحة **الحساب ← الخصوصية** يمكنكم تنزيل نسخة من بياناتكم، أو طلب تصحيحها أو محوها، أو طلب تقييد معالجتها أو إيقافها. ونلتزم بالرد خلال 30 يوماً.
                """),
            State = PrivacyNoticeState.Published,
            PublishedAt = DateTimeOffset.UtcNow,
        };
        db.PrivacyNotices.Add(notice);
        var surveys = new ConsentPurpose
        {
            Key = "surveys",
            Name = new LocalizedText("Satisfaction surveys", "استبيانات الرضا"),
            Description = new LocalizedText(
                "We may email you a short survey after your request is completed.",
                "قد نرسل إليكم عبر البريد الإلكتروني استبياناً قصيراً بعد إنجاز طلبكم."),
            Position = 1,
        };
        var ai = new ConsentPurpose
        {
            Key = "ai-assistance",
            Name = new LocalizedText("AI assistance", "المساعدة بالذكاء الاصطناعي"),
            Description = new LocalizedText(
                "Our AI assistant may help staff summarise and translate your messages; personal details are masked first.",
                "قد يساعد المساعد الذكي الموظفين في تلخيص رسائلكم وترجمتها، مع إخفاء البيانات الشخصية أولاً."),
            Position = 2,
        };
        db.ConsentPurposes.AddRange(surveys, ai);

        // The demo users accepted the notice, so one-click switching shows the screens straight away.
        var users = await db.Users.AsNoTracking().Where(u => u.ErasedAt == null && u.Email != null).ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var user in users)
        {
            db.ConsentRecords.Add(new ConsentRecord { UserId = user.Id, Action = ConsentAction.NoticeAccepted, NoticeId = notice.Id, NoticeVersion = 1, Channel = ConsentChannel.Web });
        }

        UserAccount? Persona(string persona) =>
            context.Organisation.User(persona) is { } demo ? users.SingleOrDefault(u => u.Email == context.Organisation.UsernameOf(demo)) : null;
        if (Persona("requester") is { } requester)
        {
            db.ConsentRecords.Add(new ConsentRecord
            {
                UserId = requester.Id, Action = ConsentAction.Granted, PurposeId = surveys.Id, NoticeId = notice.Id, NoticeVersion = 1, Channel = ConsentChannel.Web,
            });
            var received = DateTimeOffset.UtcNow.AddDays(-3);
            db.DataSubjectRequests.Add(new DataSubjectRequest
            {
                Number = "DSR-000001",
                Sequence = 1,
                Type = DataSubjectRequestType.Access,
                SubjectId = requester.Id,
                Channel = DataSubjectRequestChannel.Portal,
                Details = requester.PreferredLanguage == LocalizedText.Arabic
                    ? "أرجو تزويدي بنسخة من جميع بياناتي الشخصية المحفوظة لديكم."
                    : "Please send me a copy of all the personal data you hold about me.",
                SubmittedById = requester.Id,
                ReceivedAt = received,
                DueAt = received.AddDays(30),
            });
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
