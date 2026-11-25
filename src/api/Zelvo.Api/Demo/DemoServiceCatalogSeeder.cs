using Microsoft.EntityFrameworkCore;
using Zelvo.Api.Catalog;
using Zelvo.Api.Settings;
using Zelvo.Data;
using Zelvo.Data.Catalog;
using Zelvo.Data.Forms;
using Zelvo.Data.Identity;
using Zelvo.Data.Localization;
using Zelvo.Data.Tickets;
using Zelvo.Tickets;
using Zelvo.Tickets.Forms;

namespace Zelvo.Api.Demo;

/// <summary>
/// Task C2: a service catalog for each sample organisation, in English and Arabic. Two catalogs
/// (the second one edited by the team lead), a main service with sub-services, services with
/// fees, required documents, steps and FAQs, a public service visitors can read, a service only
/// the finance department sees, a prerequisite, an external service, a suspended one, a draft and
/// one waiting for review, a saved template, and service requests by the sample requester
/// (one made on their behalf).
/// </summary>
internal sealed class DemoServiceCatalogSeeder : IDemoSeeder
{
    public string Key => "c2-catalog";

    public int Order => 150;

    private static LocalizedText T(string en, string ar) => new(en, ar);

    private static ServiceStep Step(string en, string ar, ServiceStepStage stage) => new() { Name = T(en, ar), Stage = stage };

    private static ServiceFaq Faq(string qEn, string qAr, string aEn, string aAr) => new() { Question = T(qEn, qAr), Answer = T(aEn, aAr) };

    private static readonly ServiceStep[] Journey =
    [
        Step("Request received", "استلام الطلب", ServiceStepStage.Submitted),
        Step("Under review", "قيد الدراسة", ServiceStepStage.Assigned),
        Step("Being fulfilled", "قيد التنفيذ", ServiceStepStage.InProgress),
        Step("Completed", "اكتمل الطلب", ServiceStepStage.Resolved),
    ];

    public async Task SeedAsync(DemoSeedContext context, CancellationToken cancellationToken)
    {
        var db = context.Get<ZelvoDbContext>();
        var government = context.Organisation.Kind == DemoOrganisationKind.Government;
        var users = await db.Users.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
        UserAccount? Persona(string persona) =>
            context.Organisation.User(persona) is { } demo ? users.SingleOrDefault(u => u.Email == context.Organisation.UsernameOf(demo)) : null;
        var admin = Persona("admin");
        var lead = Persona("lead");
        var requester = Persona("requester");
        var departments = await db.Departments.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
        var it = departments.FirstOrDefault(d => d.Name.En == "Information technology")?.Id;
        var finance = departments.FirstOrDefault(d => d.Name.En == "Finance")?.Id;
        var categories = await db.Categories.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
        Guid[] Category(string en) => categories.Where(c => c.Name.En == en).Select(c => c.Id).Take(1).ToArray();

        // Catalogs: one for everyone the organisation serves, one for its own staff.
        var publicCatalog = new ServiceCatalog
        {
            Key = government ? "citizen-services" : "customer-services",
            Name = government ? T("Citizen services", "خدمات المتعاملين") : T("Customer services", "خدمات العملاء"),
            Description = government
                ? T("Permits, licences and certificates for individuals and businesses.", "التصاريح والرخص والشهادات للأفراد والمنشآت.")
                : T("Services for our customers and partners.", "خدمات لعملائنا وشركائنا."),
            Position = 1,
        };
        var staffCatalog = new ServiceCatalog
        {
            Key = "employee-services",
            Name = T("Employee services", "خدمات الموظفين"),
            Description = T("IT, human resources and facilities for our staff.", "خدمات تقنية المعلومات والموارد البشرية والمرافق لموظفينا."),
            DepartmentId = it,
            Position = 2,
        };
        if (lead is not null)
        {
            staffCatalog.Editors.Add(new CatalogEditor { CatalogId = staffCatalog.Id, UserId = lead.Id });
        }

        db.ServiceCatalogs.AddRange(publicCatalog, staffCatalog);

        // A form for the laptop service.
        var laptopForm = new Form
        {
            Key = "laptop-request",
            Name = T("Laptop request", "طلب حاسوب محمول"),
            Description = T("Which laptop you need and why.", "الحاسوب الذي تحتاج إليه وسبب الطلب."),
            HasUnpublishedChanges = false,
            DraftSchema = FormJson.Write(new FormSchema
            {
                Elements =
                [
                    new FormElement { Type = FormElementType.Field, Field = new FormField
                    {
                        Key = "model", Type = FieldType.Radio, Label = T("Model", "الطراز"), Required = true,
                        Options = [new FieldOption("standard", T("Standard 14-inch", "قياسي 14 بوصة")), new FieldOption("performance", T("Performance 16-inch", "عالي الأداء 16 بوصة"))],
                    } },
                    new FormElement { Type = FormElementType.Field, Field = new FormField
                    {
                        Key = "reason", Type = FieldType.Text, Label = T("Why do you need it?", "ما سبب الطلب؟"), Multiline = true, Required = true,
                    } },
                ],
            }),
        };
        var laptopFormVersion = new FormVersion { FormId = laptopForm.Id, Number = 1, Schema = laptopForm.DraftSchema };
        db.Forms.Add(laptopForm);
        db.FormVersions.Add(laptopFormVersion);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        laptopForm.PublishedVersionId = laptopFormVersion.Id;
        laptopForm.State = FormState.Published;

        var services = new List<CatalogService>();
        CatalogService Add(string code, ServiceCard card, ServiceState state = ServiceState.Active, LocalizedText? suspended = null)
        {
            card = ServiceCards.Normalise(card with { OwnerId = card.OwnerId ?? admin?.Id });
            var service = new CatalogService { Code = code, Name = card.Name, State = state, CreatedById = admin?.Id, SuspendedMessage = suspended ?? new LocalizedText(null, null) };
            ServiceEndpoints.CopyPublished(service, card);
            var version = new CatalogServiceVersion
            {
                ServiceId = service.Id,
                Number = 1,
                Content = ServiceCards.Write(card),
                CreatedById = admin?.Id,
                LastEditedById = admin?.Id,
                State = state switch
                {
                    ServiceState.Draft => ServiceVersionState.Draft,
                    ServiceState.UnderReview => ServiceVersionState.UnderReview,
                    _ => ServiceVersionState.Published,
                },
                SubmittedAt = state == ServiceState.UnderReview ? DateTimeOffset.UtcNow : null,
                PublishedAt = state is ServiceState.Active or ServiceState.Suspended ? DateTimeOffset.UtcNow : null,
            };
            if (state is ServiceState.Active or ServiceState.Suspended)
            {
                service.PublishedVersionId = version.Id;
                service.PublishedAt = version.PublishedAt;
            }
            else
            {
                service.WorkingVersionId = version.Id;
            }

            db.CatalogServices.Add(service);
            db.CatalogServiceVersions.Add(version);
            services.Add(service);
            return service;
        }

        var staff = new[] { staffCatalog.Id };
        var everyone = new[] { publicCatalog.Id };

        // IT: a main service with sub-services.
        var itMain = Add("IT", new ServiceCard
        {
            Name = T("IT services", "خدمات تقنية المعلومات"),
            Summary = T("Devices, software and accounts for your work.", "الأجهزة والبرامج والحسابات اللازمة لعملك."),
            Description = T("Everything the IT team provides. Choose a service below.", "جميع ما يقدمه فريق تقنية المعلومات. اختر إحدى الخدمات أدناه."),
            CatalogIds = staff, DepartmentId = it, Icon = "support", Audiences = [TargetAudience.Employees], Steps = Journey,
        });
        var laptop = Add("IT-LAPTOP", ServiceTemplates.BuiltIn.Single(t => t.Key == "new-laptop").Card with
        {
            ParentId = itMain.Id, CatalogIds = staff, CategoryIds = Category("Laptops"), DepartmentId = it, FormId = laptopForm.Id,
            Documents = [new ServiceDocument { Key = "managerApproval", Name = T("Manager's approval email", "بريد اعتماد المدير"), Required = false, FileTypes = ["pdf", "eml", "msg"] }],
            OnBehalfRoleKeys = [BuiltInRoles.TeamLead, BuiltInRoles.Agent],
            Partners = [new ServicePartner { Kind = PartnerKind.Vendor, Name = T("Hardware supplier", "مورّد الأجهزة") }],
        });
        var access = Add("IT-ACCESS", ServiceTemplates.BuiltIn.Single(t => t.Key == "software-access").Card with
        {
            ParentId = itMain.Id, CatalogIds = staff, DepartmentId = it,
            CategoryIds = Category(government ? "Business applications" : "Access and permissions"),
        });
        Add("IT-PASSWORD", ServiceTemplates.BuiltIn.Single(t => t.Key == "password-reset").Card with { ParentId = itMain.Id, CatalogIds = staff, DepartmentId = it });
        Add("IT-MONITOR", new ServiceCard
        {
            Name = T("Extra monitor", "شاشة إضافية"),
            Summary = T("A second screen for your desk.", "شاشة ثانية لمكتبك."),
            ParentId = itMain.Id, CatalogIds = staff, DepartmentId = it, Icon = "laptop", Days = 3, Steps = Journey, Audiences = [TargetAudience.Employees],
        }, ServiceState.Suspended, T("Monitors are out of stock until next month.", "الشاشات غير متوفرة حتى الشهر القادم."));

        // Human resources and finance.
        Add("HR-SALARY", ServiceTemplates.BuiltIn.Single(t => t.Key == "salary-certificate").Card with
        {
            CatalogIds = staff,
            Faq = [Faq("Can it be addressed to an embassy?", "هل يمكن توجيهها إلى سفارة؟", "Yes. Name the embassy in the request.", "نعم، اذكر اسم السفارة في الطلب.")],
            RelatedServiceIds = [laptop.Id],
        });
        Add("FIN-ADVANCE", new ServiceCard
        {
            Name = T("Salary advance", "سلفة على الراتب"),
            Summary = T("An advance on next month's salary, for finance staff.", "سلفة على راتب الشهر القادم لموظفي المالية."),
            Description = T("Only the finance department sees this service.", "هذه الخدمة متاحة لموظفي إدارة المالية فقط."),
            CatalogIds = staff, DepartmentId = finance, Icon = "money", Days = 5, Steps = Journey, Audiences = [TargetAudience.Employees],
            Access = new ServiceAccess { Mode = ServiceAccessMode.Restricted, DepartmentIds = finance is { } f ? [f] : [], RoleKeys = [BuiltInRoles.TenantAdmin] },
        });
        Add("FAC-ROOM", ServiceTemplates.BuiltIn.Single(t => t.Key == "facility-booking").Card with { CatalogIds = staff });

        // Public services, with fees, documents, a prerequisite and an external one.
        if (government)
        {
            var name = Add("BIZ-NAME", new ServiceCard
            {
                Name = T("Trade name reservation", "حجز اسم تجاري"),
                Summary = T("Reserve a name for your business.", "احجز اسمًا لمنشأتك التجارية."),
                Description = T("Reserve a trade name before applying for a licence.", "احجز الاسم التجاري قبل التقدم بطلب الرخصة."),
                CatalogIds = everyone, Icon = "document", Days = 1, Steps = Journey,
                Fee = new ServiceFee { Paid = true, AmountAed = 200m },
                Channels = [DeliveryChannel.Portal, DeliveryChannel.ServiceCentre], Audiences = [TargetAudience.Businesses, TargetAudience.Individuals],
                Access = new ServiceAccess { Mode = ServiceAccessMode.Public },
            });
            Add("BIZ-LICENCE", ServiceTemplates.BuiltIn.Single(t => t.Key == "trade-licence").Card with { CatalogIds = everyone, PrerequisiteServiceIds = [name.Id] });
            Add("PERMIT-RENEW", ServiceTemplates.BuiltIn.Single(t => t.Key == "permit-renewal").Card with { CatalogIds = everyone, RelatedServiceIds = [name.Id] });
            Add("VISA", new ServiceCard
            {
                Name = T("Residence visa", "تأشيرة الإقامة"),
                Summary = T("Applied for on the federal authority's website.", "يُقدَّم الطلب عبر الموقع الإلكتروني للهيئة الاتحادية."),
                Type = ServiceType.External, ExternalUrl = "https://visa.example/apply",
                CatalogIds = everyone, Icon = "globe", Audiences = [TargetAudience.Individuals], Access = new ServiceAccess { Mode = ServiceAccessMode.Public },
            });
        }
        else
        {
            Add("CUS-QUOTE", new ServiceCard
            {
                Name = T("Request a quotation", "طلب عرض سعر"),
                Summary = T("Ask our sales team for a price.", "اطلب عرض سعر من فريق المبيعات."),
                CatalogIds = everyone, Icon = "money", Days = 2, Steps = Journey, Audiences = [TargetAudience.Businesses],
                Access = new ServiceAccess { Mode = ServiceAccessMode.Public },
                Faq = [Faq("Is the quotation free?", "هل عرض السعر مجاني؟", "Yes, quotations are free.", "نعم، عروض الأسعار مجانية.")],
            });
            Add("CUS-SUPPORT", new ServiceCard
            {
                Name = T("Customer support portal", "بوابة دعم العملاء"),
                Summary = T("Track your orders on our partner's site.", "تابع طلباتك على موقع شريكنا."),
                Type = ServiceType.External, ExternalUrl = "https://support.example/orders",
                CatalogIds = everyone, Icon = "globe", Access = new ServiceAccess { Mode = ServiceAccessMode.Public },
            });
        }

        // Work in progress: a draft and one waiting for review.
        Add("IT-PHONE", new ServiceCard { Name = T("Mobile phone", "هاتف متحرك"), CatalogIds = staff, Icon = "phone", Steps = Journey }, ServiceState.Draft);
        Add("IT-PRINTER", new ServiceCard
        {
            Name = T("Printer access", "صلاحية الطابعة"),
            Summary = T("Print on the office printers.", "الطباعة على طابعات المكتب."),
            CatalogIds = staff, Icon = "printer", Steps = Journey, Days = 1,
        }, ServiceState.UnderReview);

        db.CatalogServiceTemplates.Add(new CatalogServiceTemplate
        {
            Name = T("Our standard IT service", "خدمة تقنية المعلومات القياسية لدينا"),
            Description = T("Steps and channels we use for IT services.", "الخطوات والقنوات المعتمدة لخدمات تقنية المعلومات."),
            Content = ServiceCards.Write(ServiceTemplates.Generalise(new ServiceCard { Steps = Journey, Channels = [DeliveryChannel.Portal, DeliveryChannel.Email], Icon = "support" })),
            CreatedById = admin?.Id,
        });
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        // Service requests by the sample requester: one of their own, one a team lead made for them.
        if (requester is not null)
        {
            var settings = context.Get<TenantSettingsStore>();
            var prefixes = await settings.GetAsync(Incidents.IncidentSettings.NumberPrefixes, cancellationToken).ConfigureAwait(false);
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            var samples = new (CatalogService Service, Guid? By, TicketState State)[] { (laptop, requester.Id, TicketState.New), (access, lead?.Id, TicketState.Resolved) };
            var tickets = new List<Ticket>();
            var fallbackGroup = await db.SupportGroups.AsNoTracking().Where(g => g.Active).OrderBy(g => g.Name.En).Select(g => (Guid?)g.Id).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            foreach (var (service, by, state) in samples)
            {
                var sequence = await TicketNumbers.NextAsync(db, context.Tenant.Id, TicketType.ServiceRequest, cancellationToken).ConfigureAwait(false);
                var now = DateTimeOffset.UtcNow;
                var ticket = new Ticket
                {
                    Type = TicketType.ServiceRequest,
                    Number = TicketNumbers.Format(TicketNumbers.PrefixOf(prefixes, TicketType.ServiceRequest), sequence),
                    Sequence = sequence,
                    Title = (requester.PreferredLanguage == "ar" ? service.Name.Ar : service.Name.En) ?? service.Code,
                    Channel = TicketChannel.Portal,
                    RequesterId = requester.Id,
                    CreatedById = by,
                    CategoryId = service.CategoryIds.FirstOrDefault() is var c && c != Guid.Empty ? c : null,
                    LocationId = requester.LocationId,
                    CreatedAt = now,
                    UpdatedAt = now,
                };
                // Placed by the assignment rules like a real request, else in the first group's queue.
                await Incidents.IncidentEndpoints.RouteAsync(db, settings, ticket, now, cancellationToken).ConfigureAwait(false);
                ticket.GroupId ??= fallbackGroup;
                if (state == TicketState.Resolved)
                {
                    ticket.State = TicketState.Resolved;
                    ticket.ResolutionNotes = "Access granted.";
                    ticket.ResolvedAt = now;
                    ticket.RespondedAt = now;
                }

                db.Tickets.Add(ticket);
                tickets.Add(ticket);
                db.ServiceRequests.Add(new ServiceRequestDetails { TicketId = ticket.Id, ServiceId = service.Id, ServiceVersionId = service.PublishedVersionId!.Value });
                if (service == laptop)
                {
                    db.TicketFormResponses.Add(new TicketFormResponse
                    {
                        TicketId = ticket.Id,
                        FormId = laptopForm.Id,
                        FormVersionId = laptopFormVersion.Id,
                        Fields = FormJson.Write(FormResolver.Expand(FormJson.ReadForm(laptopFormVersion.Schema)!, new Dictionary<Guid, IReadOnlyList<FormField>>())),
                        Answers = """{"model":"standard","reason":"My current laptop is five years old."}""",
                        SubmittedById = requester.Id,
                    });
                }

                // Round-robin keeps one rotation row per member; save each request before routing the next.
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }

            // Task B3: their SLA clocks start like every ticket's.
            await new Tickets.Sla.SlaTracker(db, context.Get<Data.Auditing.IAuditLog>()).UpdateAsync(tickets, DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        // Listed services, and the requests (task D2), are found in search.
        var indexer = context.Get<Zelvo.Search.ISearchIndexer>();
        await RequestSearch.IndexAsync(db, indexer, await db.Tickets.AsNoTracking().Where(t => t.Type == TicketType.ServiceRequest).ToListAsync(cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
        foreach (var service in services)
        {
            await CatalogSearch.IndexAsync(db, indexer, service, cancellationToken).ConfigureAwait(false);
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
