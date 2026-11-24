using Microsoft.EntityFrameworkCore;
using Zelvo.Api.Knowledge;
using Zelvo.Api.Settings;
using Zelvo.Data;
using Zelvo.Data.Knowledge;
using Zelvo.Data.Localization;
using Zelvo.Data.Tickets;
using Zelvo.Search;
using Zelvo.Tickets;

namespace Zelvo.Api.Demo;

/// <summary>
/// Task D1: sample knowledge articles of each organisation in English and Arabic: published
/// external and internal articles with views and feedback, one waiting for review, one draft, and
/// one made from a resolved sample incident. Fictional, like everything in the demo.
/// </summary>
internal sealed class DemoKnowledgeSeeder : IDemoSeeder
{
    public string Key => "d1-knowledge";

    public int Order => 200;

    private sealed record Sample(
        LocalizedText Title, LocalizedText Summary, LocalizedText Body, ArticleVisibility Visibility, ArticleState State,
        long Views = 0, int Helpful = 0, int NotHelpful = 0, string? FromIncidentNotes = null);

    private static readonly Sample[] Government =
    [
        new(new("How to apply for a building permit", "كيفية التقدم بطلب رخصة بناء"),
            new("The documents you need and the steps in the e-services portal.", "المستندات المطلوبة وخطوات التقديم عبر بوابة الخدمات الإلكترونية."),
            new("""
                ## Before you start

                - A valid Emirates ID and UAE PASS account
                - The site plan and the approved drawings (PDF)

                ## Steps

                1. Sign in to the e-services portal with UAE PASS.
                2. Choose **Building permits** and then **New permit**.
                3. Upload the documents and pay the fee.

                You can follow the request under **My requests**.
                """,
                """
                ## قبل البدء

                - هوية إماراتية سارية وحساب في الهوية الرقمية (UAE PASS)
                - مخطط الموقع والمخططات المعتمدة بصيغة PDF

                ## الخطوات

                1. سجّل الدخول إلى بوابة الخدمات الإلكترونية باستخدام الهوية الرقمية.
                2. اختر **رخص البناء** ثم **رخصة جديدة**.
                3. حمّل المستندات وادفع الرسوم.

                يمكنك متابعة الطلب من **طلباتي**.
                """),
            ArticleVisibility.External, ArticleState.Published, Views: 184, Helpful: 23, NotHelpful: 2),
        new(new("Paying service fees online", "دفع رسوم الخدمات إلكترونياً"),
            new("Accepted cards and what to do if a payment fails.", "البطاقات المقبولة وما يجب فعله عند تعذّر الدفع."),
            new("""
                Fees can be paid by debit or credit card. If a payment fails, wait ten minutes before trying again: the first payment may still be confirmed.
                """,
                """
                يمكن دفع الرسوم ببطاقة الخصم أو الائتمان. إذا تعذّر الدفع فانتظر عشر دقائق قبل المحاولة مرة أخرى، فقد يُؤكَّد الدفع الأول.
                """),
            ArticleVisibility.External, ArticleState.Published, Views: 96, Helpful: 11, NotHelpful: 4),
        new(new("Restarting the transactions system after maintenance", "إعادة تشغيل نظام إدارة المعاملات بعد الصيانة"),
            new("Checklist for the service desk after planned maintenance.", "قائمة تحقق لفريق الدعم بعد الصيانة المخطط لها."),
            new(null,
                """
                1. تأكد من تشغيل خادم قاعدة البيانات أولاً.
                2. أعد تشغيل خدمات التطبيق بالترتيب الموضح في دليل التشغيل.
                3. افتح معاملة تجريبية وتحقق من سرعة الاستجابة.
                """),
            ArticleVisibility.Internal, ArticleState.Published, Views: 31, Helpful: 6),
        new(new("Booking an appointment at a customer happiness centre", "حجز موعد في مركز سعادة المتعاملين"),
            new("Book, change or cancel an appointment online.", "احجز موعداً أو عدّله أو ألغِه إلكترونياً."),
            new("Choose the centre and the service, then pick a free time.", "اختر المركز والخدمة ثم اختر وقتاً متاحاً."),
            ArticleVisibility.External, ArticleState.InReview),
        new(new("Slow transactions system", "بطء نظام إدارة المعاملات"),
            new(null, null), new(null, null), ArticleVisibility.Internal, ArticleState.Draft, FromIncidentNotes: "تمت زيادة ذاكرة خادم قاعدة البيانات وعاد الأداء إلى طبيعته."),
    ];

    private static readonly Sample[] Company =
    [
        new(new("Connecting to the VPN from home", "الاتصال بالشبكة الافتراضية الخاصة من المنزل"),
            new("Install the VPN client and sign in with your company account.", "ثبّت برنامج الشبكة الافتراضية وسجّل الدخول بحساب الشركة."),
            new("""
                ## Install

                1. Download the VPN client from the **Software centre**.
                2. Sign in with your company email and approve the request on your phone.

                ## If it disconnects

                Update the client to the latest version and restart your laptop.
                """,
                """
                ## التثبيت

                1. نزّل برنامج الشبكة الافتراضية من **مركز البرامج**.
                2. سجّل الدخول ببريد الشركة ووافق على الطلب من هاتفك.

                ## عند انقطاع الاتصال

                حدّث البرنامج إلى أحدث إصدار ثم أعد تشغيل جهازك.
                """),
            ArticleVisibility.External, ArticleState.Published, Views: 241, Helpful: 37, NotHelpful: 3),
        new(new("Resetting your password", "إعادة تعيين كلمة المرور"),
            new("Reset your company password yourself in two minutes.", "أعد تعيين كلمة مرور الشركة بنفسك خلال دقيقتين."),
            new("Open the password page, confirm with your phone and choose a new password of at least 12 characters.",
                "افتح صفحة كلمة المرور، وأكّد عبر هاتفك، ثم اختر كلمة مرور جديدة من 12 حرفاً على الأقل."),
            ArticleVisibility.External, ArticleState.Published, Views: 312, Helpful: 44, NotHelpful: 5),
        new(new("Granting access to shared finance folders", "منح صلاحية الوصول إلى مجلدات المالية المشتركة"),
            new("Which group to add the user to, and who approves.", "المجموعة التي يُضاف إليها المستخدم ومن يعتمد الطلب."),
            new("Add the user to **Finance readers** after the finance manager approves. Never add users to **Finance owners**.", null),
            ArticleVisibility.Internal, ArticleState.Published, Views: 18, Helpful: 4),
        new(new("Setting up a docking station", "إعداد محطة الإرساء"),
            new("Connect a second monitor through the docking station.", "توصيل شاشة ثانية عبر محطة الإرساء."),
            new("Connect the dock with the USB-C cable marked with a lightning symbol.", "صِل المحطة بكابل USB-C الذي يحمل رمز البرق."),
            ArticleVisibility.External, ArticleState.InReview),
        new(new("VPN disconnects every few minutes", "انقطاع الشبكة الافتراضية كل بضع دقائق"),
            new(null, null), new(null, null), ArticleVisibility.Internal, ArticleState.Draft, FromIncidentNotes: "Updated the VPN client to the latest version; the connection is stable."),
    ];

    public async Task SeedAsync(DemoSeedContext context, CancellationToken cancellationToken)
    {
        var db = context.Get<ZelvoDbContext>();
        var settings = context.Get<TenantSettingsStore>();
        var indexer = context.Get<ISearchIndexer>();
        var users = (await db.Users.AsNoTracking().Where(u => u.Email != null).Select(u => new { u.Email, u.Id }).ToListAsync(cancellationToken).ConfigureAwait(false))
            .GroupBy(u => u.Email!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Id, StringComparer.OrdinalIgnoreCase);
        Guid UserId(string persona) => users[context.Organisation.UsernameOf(context.Organisation.User(persona)!)];
        var agent = UserId("agent");
        var lead = UserId("lead");
        var prefix = await settings.GetAsync(KnowledgeSettings.NumberPrefix, cancellationToken).ConfigureAwait(false);
        var samples = context.Organisation.Kind == DemoOrganisationKind.Government ? Government : Company;
        var now = DateTimeOffset.UtcNow;
        var sequence = await db.KbArticles.MaxAsync(a => (long?)a.Sequence, cancellationToken).ConfigureAwait(false) ?? 0;
        var articles = new List<KbArticle>();
        foreach (var (sample, index) in samples.Select((s, i) => (s, i)))
        {
            var at = now.AddDays(-(samples.Length - index) * 3);
            Ticket? source = null;
            var sampleTitle = sample.Title;
            var summary = sample.Summary;
            var body = sample.Body;
            if (sample.FromIncidentNotes is { } notes)
            {
                // "Turn this ticket into an article" on the resolved sample incident.
                source = await db.Tickets.AsNoTracking().Include(t => t.Requester).Include(t => t.Assignee)
                    .FirstOrDefaultAsync(t => t.ResolutionNotes == notes, cancellationToken).ConfigureAwait(false);
                if (source is not null)
                {
                    var draft = KnowledgeEndpoints.FromTicket(source);
                    sampleTitle = draft.Title!.ToValue();
                    body = draft.Body!.ToValue();
                }
            }

            sequence++;
            var article = new KbArticle
            {
                Number = TicketNumbers.Format(prefix, sequence),
                Sequence = sequence,
                State = sample.State,
                Visibility = sample.Visibility,
                CreatedById = agent,
                SourceTicketId = source?.Id,
                ViewCount = sample.Views,
                HelpfulCount = sample.Helpful,
                NotHelpfulCount = sample.NotHelpful,
                CreatedAt = at,
                UpdatedAt = at.AddDays(1),
            };
            var version = new KbArticleVersion
            {
                ArticleId = article.Id,
                Number = 1,
                Title = sampleTitle,
                Summary = summary,
                Body = body,
                Visibility = sample.Visibility,
                CreatedById = agent,
                LastEditedById = agent,
                CreatedAt = at,
                UpdatedAt = at.AddDays(1),
            };
            switch (sample.State)
            {
                case ArticleState.Published:
                    version.State = ArticleVersionState.Published;
                    version.SubmittedById = agent;
                    version.SubmittedAt = at.AddHours(2);
                    version.ReviewedById = lead;
                    version.ReviewedAt = at.AddDays(1);
                    version.PublishedAt = at.AddDays(1);
                    article.PublishedVersionId = version.Id;
                    article.PublishedAt = version.PublishedAt;
                    break;
                case ArticleState.InReview:
                    version.State = ArticleVersionState.InReview;
                    version.SubmittedById = agent;
                    version.SubmittedAt = at.AddHours(2);
                    article.WorkingVersionId = version.Id;
                    break;
                default:
                    article.WorkingVersionId = version.Id;
                    break;
            }

            db.KbArticles.Add(article);
            db.KbArticleVersions.Add(version);
            articles.Add(article);
        }

        var audience = await KnowledgeSearch.AudienceAsync(db, settings, cancellationToken).ConfigureAwait(false);
        foreach (var article in articles)
        {
            await KnowledgeSearch.IndexAsync(db, indexer, audience, article, cancellationToken).ConfigureAwait(false);
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
