using Microsoft.EntityFrameworkCore;
using Zelvo.Api.Catalog;
using Zelvo.Data;
using Zelvo.Data.Catalog;
using Zelvo.Data.Directory;
using Zelvo.Data.Localization;

namespace Zelvo.Api.Demo;

/// <summary>
/// Task C4: a "New employee onboarding" service for each sample organisation, fulfilled by its
/// own group with a service SLA (4 working hours to take ownership, 3 working days to complete)
/// and a task template: the laptop (applications team) and the network account (infrastructure or
/// network team) in parallel, then the hand-over by the service desk. Its steps follow the tasks,
/// so the requester's journey moves as the work does.
/// </summary>
internal sealed class DemoFulfilmentSeeder : IDemoSeeder
{
    public string Key => "c4-fulfilment";

    public int Order => 170;

    private static LocalizedText T(string en, string ar) => new(en, ar);

    public async Task SeedAsync(DemoSeedContext context, CancellationToken cancellationToken)
    {
        var db = context.Get<ZelvoDbContext>();
        var groups = await db.SupportGroups.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
        Guid? Group(params string[] names) => groups.FirstOrDefault(g => names.Contains(g.Name.En))?.Id;
        var desk = Group("Service desk");
        var apps = Group("Applications team");
        var network = Group("Infrastructure team", "Network team");
        var catalog = await db.ServiceCatalogs.AsNoTracking().Where(c => c.Key == "employee-services").Select(c => (Guid?)c.Id).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        var admin = context.Organisation.User("admin") is { } demo
            ? await db.Users.AsNoTracking().Where(u => u.Email == context.Organisation.UsernameOf(demo)).Select(u => (Guid?)u.Id).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false)
            : null;
        if (desk is null)
        {
            return;
        }

        var card = ServiceCards.Normalise(new ServiceCard
        {
            Name = T("New employee onboarding", "تهيئة موظف جديد"),
            Summary = T("Laptop, network account and a hand-over for someone joining.", "حاسوب وحساب شبكة وتسليم للموظف المنضم حديثاً."),
            Description = T("Everything a new colleague needs on their first day.", "كل ما يحتاجه الزميل الجديد في يومه الأول."),
            CatalogIds = catalog is { } c ? [c] : [],
            OwnerId = admin,
            Icon = "people",
            Days = 3,
            Channels = [DeliveryChannel.Portal],
            Audiences = [TargetAudience.Employees],
            Steps =
            [
                new ServiceStep { Name = T("Request received", "استلام الطلب"), Stage = ServiceStepStage.Submitted },
                new ServiceStep { Name = T("Preparing the laptop and accounts", "تجهيز الحاسوب والحسابات"), Stage = ServiceStepStage.InProgress, TaskKey = "laptop" },
                new ServiceStep { Name = T("Hand-over", "التسليم"), Stage = ServiceStepStage.InProgress, TaskKey = "handover" },
                new ServiceStep { Name = T("Completed", "اكتمل الطلب"), Stage = ServiceStepStage.Resolved },
            ],
            Fulfilment = new ServiceFulfilment
            {
                GroupId = desk,
                OwnershipMinutes = 240,
                Escalations = [new ServiceEscalation { Percent = 80, NotifyGroupLeads = true }],
                Tasks =
                [
                    new FulfilmentTask { Key = "laptop", Name = T("Prepare the laptop", "تجهيز الحاسوب"), GroupId = apps ?? desk, TargetMinutes = 480 },
                    new FulfilmentTask { Key = "account", Name = T("Create the network account", "إنشاء حساب الشبكة"), GroupId = network ?? desk, ParallelWithPrevious = true, TargetMinutes = 240 },
                    new FulfilmentTask { Key = "handover", Name = T("Hand over on the first day", "التسليم في اليوم الأول"), GroupId = desk, TargetMinutes = 120 },
                ],
            },
        });
        var service = new CatalogService { Code = "HR-ONBOARD", Name = card.Name, State = ServiceState.Active, CreatedById = admin };
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
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
