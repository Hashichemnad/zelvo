using Microsoft.EntityFrameworkCore;
using Zelvo.Api.Catalog;
using Zelvo.Data;
using Zelvo.Data.Catalog;
using Zelvo.Data.Localization;

namespace Zelvo.Api.Demo;

/// <summary>
/// Task C5: a bundle and proactive services for each sample organisation.
/// <list type="bullet">
/// <item>"New joiner package" (<c>HR-JOINER</c>): onboarding and system access together, then an
/// optional monitor; offered proactively when the HR system sends a <c>newJoiner</c> event.</item>
/// <item>"Yearly access review" (<c>IT-REVIEW</c>): started automatically each year for the service desk.</item>
/// <item>An open offer of the package for the sample requester, so the portal shows it.</item>
/// </list>
/// </summary>
internal sealed class DemoBundlesSeeder : IDemoSeeder
{
    public string Key => "c5-bundles-proactive";

    public int Order => 180;

    private static LocalizedText T(string en, string ar) => new(en, ar);

    public async Task SeedAsync(DemoSeedContext context, CancellationToken cancellationToken)
    {
        var db = context.Get<ZelvoDbContext>();
        var services = await db.CatalogServices.AsNoTracking().Where(s => s.State == ServiceState.Active).ToDictionaryAsync(s => s.Code, s => s.Id, cancellationToken).ConfigureAwait(false);
        var desk = await db.SupportGroups.AsNoTracking().Where(g => g.Name.En == "Service desk").Select(g => (Guid?)g.Id).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        var catalog = await db.ServiceCatalogs.AsNoTracking().Where(c => c.Key == "employee-services").Select(c => (Guid?)c.Id).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        async Task<Guid?> PersonAsync(string persona) => context.Organisation.User(persona) is { } demo
            ? await db.Users.AsNoTracking().Where(u => u.Email == context.Organisation.UsernameOf(demo)).Select(u => (Guid?)u.Id).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false)
            : null;
        var admin = await PersonAsync("admin");
        var requester = await PersonAsync("requester");
        if (!services.TryGetValue("HR-ONBOARD", out var onboard) || desk is null)
        {
            return;
        }

        var items = new List<BundleItem> { new() { ServiceId = onboard } };
        if (services.TryGetValue("IT-ACCESS", out var access))
        {
            items.Add(new BundleItem { ServiceId = access, ParallelWithPrevious = true });
        }

        if (services.TryGetValue("IT-MONITOR", out var monitor))
        {
            items.Add(new BundleItem { ServiceId = monitor, Required = false });
        }

        var bundle = Add(db, "HR-JOINER", admin, new ServiceCard
        {
            Kind = ServiceKind.Bundle,
            Name = T("New joiner package", "باقة الموظف الجديد"),
            Summary = T("Onboarding and system access together, with an optional monitor.", "التهيئة وصلاحيات الأنظمة معاً، مع شاشة إضافية اختيارية."),
            Description = T("One request for everything a new colleague needs. Each part is handled by its own team; follow them all on one page.",
                "طلب واحد لكل ما يحتاجه الزميل الجديد، يتولى كل فريق الجزء الخاص به، ويمكنكم متابعة جميع الأجزاء في صفحة واحدة."),
            CatalogIds = catalog is { } c ? [c] : [],
            OwnerId = admin,
            Icon = "people",
            Channels = [DeliveryChannel.Portal],
            Audiences = [TargetAudience.Employees],
            Steps =
            [
                new ServiceStep { Name = T("Request received", "استلام الطلب"), Stage = ServiceStepStage.Submitted },
                new ServiceStep { Name = T("Parts in progress", "تنفيذ الأجزاء"), Stage = ServiceStepStage.InProgress },
                new ServiceStep { Name = T("All parts complete", "اكتمال جميع الأجزاء"), Stage = ServiceStepStage.Resolved },
            ],
            Bundle = new ServiceBundle { Items = items },
            Proactive = new ServiceProactive
            {
                Enabled = true,
                Mode = ProactiveMode.Offer,
                OfferDays = 14,
                Triggers = [new ProactiveTrigger { Key = "newJoiner", Kind = ProactiveTriggerKind.Event }],
            },
        });

        Add(db, "IT-REVIEW", admin, new ServiceCard
        {
            Name = T("Yearly access review", "المراجعة السنوية للصلاحيات"),
            Summary = T("Check who can use which system, once a year.", "التحقق سنوياً من صلاحيات استخدام الأنظمة."),
            CatalogIds = catalog is { } c2 ? [c2] : [],
            OwnerId = admin,
            Icon = "shield",
            Channels = [DeliveryChannel.Portal],
            Audiences = [TargetAudience.Employees],
            Fulfilment = new ServiceFulfilment { GroupId = desk },
            Proactive = new ServiceProactive
            {
                Enabled = true,
                Mode = ProactiveMode.AutoStart,
                Triggers =
                [
                    new ProactiveTrigger
                    {
                        Key = "yearly", Kind = ProactiveTriggerKind.Schedule, StartOn = new DateOnly(DateTime.UtcNow.Year + 1, 1, 15), Every = 1, Unit = ScheduleUnit.Year,
                        People = new ProactivePeople { GroupIds = [desk.Value] },
                    },
                ],
            },
        });

        if (requester is { } person)
        {
            db.ProactiveRuns.Add(new ProactiveRun
            {
                ServiceId = bundle,
                TriggerKey = "newJoiner",
                Kind = ProactiveTriggerKind.Event,
                PersonId = person,
                Occurrence = "api:demo-hr-1001",
                DueOn = DateOnly.FromDateTime(DateTime.UtcNow),
                State = ProactiveRunState.Offered,
                OfferedAt = DateTimeOffset.UtcNow,
                ExpiresAt = DateTimeOffset.UtcNow.AddDays(14),
            });
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private static Guid Add(ZelvoDbContext db, string code, Guid? admin, ServiceCard card)
    {
        card = ServiceCards.Normalise(card);
        var service = new CatalogService { Code = code, Name = card.Name, State = ServiceState.Active, CreatedById = admin };
        ServiceEndpoints.CopyPublished(service, card);
        var version = new CatalogServiceVersion
        {
            ServiceId = service.Id,
            Number = 1,
            Content = ServiceCards.Write(card),
            CreatedById = admin,
            LastEditedById = admin,
            State = ServiceVersionState.Published,
            PublishedAt = DateTimeOffset.UtcNow,
        };
        service.PublishedVersionId = version.Id;
        service.PublishedAt = version.PublishedAt;
        db.CatalogServices.Add(service);
        db.CatalogServiceVersions.Add(version);
        return service.Id;
    }
}
