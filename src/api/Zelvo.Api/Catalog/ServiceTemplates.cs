using Microsoft.EntityFrameworkCore;
using Zelvo.Api.Conventions;
using Zelvo.Api.Forms;
using Zelvo.Api.Identity;
using Zelvo.Api.Tenancy;
using Zelvo.Data;
using Zelvo.Data.Auditing;
using Zelvo.Data.Catalog;
using Zelvo.Data.Localization;

namespace Zelvo.Api.Catalog;

/// <summary>
/// Templates for new services (module 2: "ready-made templates for common services"). Zelvo ships
/// ready-made bilingual templates (in code, ids <c>builtin:&lt;key&gt;</c>) and tenants save their
/// own from any service (product owner decision 2026-11-24). A template only fills a new draft;
/// changing a template never changes services made from it.
/// </summary>
internal static class ServiceTemplates
{
    /// <summary>Prefix of Zelvo's own template ids.</summary>
    public const string BuiltInPrefix = "builtin:";

    private static LocalizedText T(string en, string ar) => new(en, ar);

    private static ServiceStep Step(string en, string ar, ServiceStepStage stage) => new() { Name = T(en, ar), Stage = stage };

    private static ServiceDocument Doc(string key, string en, string ar, bool required, params string[] types) =>
        new() { Key = key, Name = T(en, ar), Required = required, FileTypes = types };

    private static ServiceFaq Faq(string qEn, string qAr, string aEn, string aAr) => new() { Question = T(qEn, qAr), Answer = T(aEn, aAr) };

    private static readonly ServiceStep[] StandardSteps =
    [
        Step("Request received", "استلام الطلب", ServiceStepStage.Submitted),
        Step("Under review", "قيد الدراسة", ServiceStepStage.Assigned),
        Step("Being fulfilled", "قيد التنفيذ", ServiceStepStage.InProgress),
        Step("Completed", "اكتمل الطلب", ServiceStepStage.Resolved),
    ];

    /// <summary>Zelvo's ready-made templates by key.</summary>
    public static IReadOnlyList<(string Key, LocalizedText Name, LocalizedText Description, ServiceCard Card)> BuiltIn { get; } =
    [
        ("new-laptop", T("New laptop", "حاسوب محمول جديد"), T("Employees ask for a laptop, with their manager's approval.", "يطلب الموظف حاسوبًا محمولًا بعد اعتماد مديره."), new ServiceCard
        {
            Name = T("New laptop", "حاسوب محمول جديد"),
            Summary = T("Request a laptop for your work.", "اطلب حاسوبًا محمولًا لأداء عملك."),
            Description = T("Request a standard laptop for a new joiner or to replace a device that is too old. Your manager approves the request before it is fulfilled.",
                "تقديم طلب للحصول على حاسوب محمول قياسي لموظف جديد أو بدلًا من جهاز قديم. يعتمد المدير الطلب قبل تنفيذه."),
            Steps = StandardSteps, Days = 5, Icon = "laptop",
            Channels = [DeliveryChannel.Portal, DeliveryChannel.Email], Audiences = [TargetAudience.Employees],
            Faq = [Faq("How long does it take?", "كم تستغرق الخدمة؟", "Usually five working days after approval.", "عادةً خمسة أيام عمل بعد الاعتماد.")],
        }),
        ("software-access", T("Software access", "صلاحية برنامج"), T("Access to an application or system.", "منح صلاحية الدخول إلى تطبيق أو نظام."), new ServiceCard
        {
            Name = T("Software access", "صلاحية الدخول إلى برنامج"),
            Summary = T("Get access to an application or system.", "احصل على صلاحية الدخول إلى تطبيق أو نظام."),
            Description = T("Request access to a business application. Say which system and which role you need.", "تقديم طلب صلاحية الدخول إلى أحد أنظمة العمل، مع تحديد النظام والدور المطلوب."),
            Steps = StandardSteps, Days = 2, Icon = "access",
            Channels = [DeliveryChannel.Portal], Audiences = [TargetAudience.Employees],
        }),
        ("password-reset", T("Password reset", "إعادة تعيين كلمة المرور"), T("Reset a forgotten or locked password.", "إعادة تعيين كلمة مرور منسية أو حساب مقفل."), new ServiceCard
        {
            Name = T("Password reset", "إعادة تعيين كلمة المرور"),
            Summary = T("Unlock your account or reset your password.", "افتح حسابك المقفل أو أعد تعيين كلمة المرور."),
            Description = T("We reset the password of your work account and tell you how to choose a new one.", "نعيد تعيين كلمة مرور حساب العمل ونرشدك إلى كيفية اختيار كلمة مرور جديدة."),
            Steps = [Step("Request received", "استلام الطلب", ServiceStepStage.Submitted), Step("Completed", "اكتمل الطلب", ServiceStepStage.Resolved)],
            Days = 1, Icon = "key", Channels = [DeliveryChannel.Portal, DeliveryChannel.CallCentre], Audiences = [TargetAudience.Employees],
        }),
        ("salary-certificate", T("Salary certificate", "شهادة راتب"), T("An official letter stating the employee's salary.", "خطاب رسمي يبيّن راتب الموظف."), new ServiceCard
        {
            Name = T("Salary certificate", "شهادة راتب"),
            Summary = T("An official letter stating your salary, addressed as you choose.", "خطاب رسمي يبيّن راتبك، موجّه إلى الجهة التي تحددها."),
            Description = T("Human resources issue a signed salary certificate addressed to a bank, an embassy or any entity you name.",
                "تصدر الموارد البشرية شهادة راتب موقّعة موجهة إلى بنك أو سفارة أو أي جهة تحددها."),
            Steps = StandardSteps, Days = 3, Icon = "certificate",
            Channels = [DeliveryChannel.Portal], Audiences = [TargetAudience.Employees],
        }),
        ("permit-renewal", T("Permit renewal", "تجديد تصريح"), T("Renew a permit or licence before it expires.", "تجديد تصريح أو رخصة قبل انتهاء صلاحيتها."), new ServiceCard
        {
            Name = T("Permit renewal", "تجديد تصريح"),
            Summary = T("Renew your permit before it expires.", "جدّد تصريحك قبل انتهاء صلاحيته."),
            Description = T("Submit your renewal with the required documents. You are told when the renewed permit is ready.",
                "قدّم طلب التجديد مع المستندات المطلوبة، وسنبلغك عند جاهزية التصريح المجدد."),
            Documents =
            [
                Doc("currentPermit", "Current permit", "التصريح الحالي", true, "pdf", "png", "jpg"),
                Doc("emiratesIdCopy", "Copy of Emirates ID", "صورة بطاقة الهوية الإماراتية", true, "pdf", "png", "jpg"),
            ],
            Steps = StandardSteps, Days = 5, Icon = "permit",
            Fee = new ServiceFee { Paid = true, AmountAed = 100m, Notes = T("Urgent renewal costs extra.", "تُستوفى رسوم إضافية للتجديد العاجل.") },
            Channels = [DeliveryChannel.Portal, DeliveryChannel.ServiceCentre], Audiences = [TargetAudience.Individuals, TargetAudience.Businesses],
            Access = new ServiceAccess { Mode = ServiceAccessMode.Public },
            Faq = [Faq("When can I renew?", "متى يمكنني التجديد؟", "Up to 30 days before the permit expires.", "خلال الثلاثين يومًا السابقة لانتهاء صلاحية التصريح.")],
        }),
        ("trade-licence", T("Trade licence issuance", "إصدار رخصة تجارية"), T("Issue a new trade licence for a business.", "إصدار رخصة تجارية جديدة لمنشأة."), new ServiceCard
        {
            Name = T("Trade licence issuance", "إصدار رخصة تجارية"),
            Summary = T("Start a new business with a trade licence.", "ابدأ نشاطك التجاري برخصة تجارية."),
            Description = T("Apply for a new trade licence. Approval of the trade name and the initial approval are prerequisites.",
                "تقديم طلب إصدار رخصة تجارية جديدة. يشترط حجز الاسم التجاري والحصول على الموافقة المبدئية مسبقًا."),
            Documents =
            [
                Doc("tradeNameCertificate", "Trade name reservation certificate", "شهادة حجز الاسم التجاري", true, "pdf"),
                Doc("tenancyContract", "Tenancy contract", "عقد الإيجار", true, "pdf"),
                Doc("passportCopy", "Copy of the owner's passport", "صورة جواز سفر المالك", true, "pdf", "png", "jpg"),
            ],
            Steps = StandardSteps, Days = 10, Icon = "license",
            Fee = new ServiceFee { Paid = true, AmountAed = 1000m },
            Channels = [DeliveryChannel.Portal, DeliveryChannel.ServiceCentre], Audiences = [TargetAudience.Businesses],
            Access = new ServiceAccess { Mode = ServiceAccessMode.Public },
        }),
        ("facility-booking", T("Meeting room booking", "حجز قاعة اجتماعات"), T("Book a meeting room or venue.", "حجز قاعة اجتماعات أو مرفق."), new ServiceCard
        {
            Name = T("Meeting room booking", "حجز قاعة اجتماعات"),
            Summary = T("Book a room for a meeting or event.", "احجز قاعة لاجتماع أو فعالية."),
            Description = T("Tell us the date, time and number of people; facilities confirm the room.", "حدّد التاريخ والوقت وعدد الحضور، وتؤكد إدارة المرافق الحجز."),
            Steps = StandardSteps, Days = 1, Icon = "building",
            Channels = [DeliveryChannel.Portal], Audiences = [TargetAudience.Employees],
        }),
        ("general-inquiry", T("General service request", "طلب خدمة عام"), T("A request that fits no other service.", "طلب لا يندرج تحت أي خدمة أخرى."), new ServiceCard
        {
            Name = T("General service request", "طلب خدمة عام"),
            Summary = T("Ask for something that is not in the catalog.", "اطلب خدمة غير مدرجة في الدليل."),
            Description = T("Describe what you need and the right team will contact you.", "صف ما تحتاج إليه، وسيتواصل معك الفريق المختص."),
            Steps = StandardSteps, Days = 3, Icon = "support",
            Channels = [DeliveryChannel.Portal, DeliveryChannel.Email, DeliveryChannel.CallCentre],
            Audiences = [TargetAudience.Employees, TargetAudience.Individuals],
        }),
    ];

    /// <summary>The card of a template: <c>builtin:&lt;key&gt;</c> or a saved template's id; null when unknown.</summary>
    public static async Task<ServiceCard?> FindAsync(ZelvoDbContext db, string id, CancellationToken cancellationToken)
    {
        if (id.StartsWith(BuiltInPrefix, StringComparison.Ordinal))
        {
            var key = id[BuiltInPrefix.Length..];
            return BuiltIn.FirstOrDefault(t => t.Key == key).Card;
        }

        if (!Guid.TryParse(id, out var guid))
        {
            return null;
        }

        var content = await db.CatalogServiceTemplates.AsNoTracking().Where(t => t.Id == guid).Select(t => t.Content).SingleOrDefaultAsync(cancellationToken);
        return content is null ? null : ServiceCards.Read(content);
    }

    /// <summary>A card without what belongs to one service only: files, links to other services, named users and the owner.</summary>
    public static ServiceCard Generalise(ServiceCard card) => card with
    {
        ParentId = null,
        OwnerId = null,
        ImageFileId = null,
        RelatedServiceIds = [],
        PrerequisiteServiceIds = [],
        Documents = [.. card.Documents.Select(d => d with { TemplateFileId = null })],
        Access = card.Access with { UserIds = [] },
    };

    public static RouteGroupBuilder MapServiceTemplateEndpoints(this RouteGroupBuilder group)
    {
        var templates = group.MapGroup("/catalog/templates").WithTags("Catalog").RequireTenant();
        templates.MapGet("/", ListAsync).WithName("ListServiceTemplates")
            .WithSummary("Zelvo's ready-made templates and the tenant's saved ones, each with its card, for creating a service. Catalog managers and editors.")
            .RequireAuthorization().Produces<IReadOnlyList<ServiceTemplateResponse>>();
        templates.MapDelete("/{id:guid}", DeleteAsync).WithName("DeleteServiceTemplate")
            .WithSummary("Delete a saved template; services made from it are not changed. Needs catalog.manage; audited.")
            .RequirePermission(Permissions.CatalogManage).Produces(StatusCodes.Status204NoContent);
        return group;
    }

    internal static async Task<IResult> ListAsync(ZelvoDbContext db, CurrentUser user, CancellationToken cancellationToken)
    {
        if (!CatalogAccess.IsManager(user) && (await CatalogAccess.EditedCatalogsAsync(db, user, cancellationToken)).Count == 0)
        {
            return CatalogEndpoints.Forbidden();
        }

        var saved = await db.CatalogServiceTemplates.AsNoTracking().OrderBy(t => t.CreatedAt).ToListAsync(cancellationToken);
        return TypedResults.Ok(BuiltIn.Select(t => new ServiceTemplateResponse(BuiltInPrefix + t.Key, true, LocalizedTextDto.From(t.Name), LocalizedTextDto.From(t.Description), t.Card, null))
            .Concat(saved.Select(ServiceTemplateResponse.From)).ToList());
    }

    internal static async Task<IResult> DeleteAsync(Guid id, ZelvoDbContext db, IAuditLog audit, CancellationToken cancellationToken)
    {
        var template = await db.CatalogServiceTemplates.SingleOrDefaultAsync(t => t.Id == id, cancellationToken);
        if (template is null)
        {
            return FormsCommon.NotFound("template");
        }

        db.CatalogServiceTemplates.Remove(template);
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.NoContent();
    }
}

/// <summary>A template.</summary>
/// <param name="Id"><c>builtin:&lt;key&gt;</c> for Zelvo's templates, the id for saved ones.</param>
internal sealed record ServiceTemplateResponse(string Id, bool BuiltIn, LocalizedTextDto Name, LocalizedTextDto Description, ServiceCard Card, DateTimeOffset? CreatedAt)
{
    public static ServiceTemplateResponse From(CatalogServiceTemplate t) =>
        new(t.Id.ToString(), false, LocalizedTextDto.From(t.Name), LocalizedTextDto.From(t.Description), ServiceCards.Read(t.Content), t.CreatedAt);
}
