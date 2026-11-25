using Zelvo.Data.Localization;
using Zelvo.Data.Tickets;

namespace Zelvo.Api.Setup;

/// <summary>A support group a template creates.</summary>
/// <param name="Key">Stable key within the template.</param>
/// <param name="Name">Name in English and Arabic.</param>
internal sealed record TemplateGroup(string Key, LocalizedText Name);

/// <summary>A category under the department's own root category, routed to one of the template's groups.</summary>
/// <param name="Name">Name.</param>
/// <param name="Group">Key of the group its tickets go to.</param>
/// <param name="Children">Categories below it (third level).</param>
internal sealed record TemplateCategory(LocalizedText Name, string Group, IReadOnlyList<LocalizedText> Children);

/// <summary>An SLA policy for one record type, with response and resolution targets per priority in business minutes.</summary>
internal sealed record TemplatePolicy(TicketType RecordType, IReadOnlyList<(int Priority, int Response, int Resolution)> Targets);

/// <summary>A draft catalog service from one of Zelvo's built-in service templates, optionally renamed.</summary>
/// <param name="Template">Built-in service template key (task C2).</param>
/// <param name="Code">Code suffix after the department prefix.</param>
/// <param name="Name">New name, or null to keep the template's.</param>
internal sealed record TemplateService(string Template, string Code, LocalizedText? Name = null);

/// <summary>A canned response for the department's first group.</summary>
internal sealed record TemplateResponse(LocalizedText Title, LocalizedText Body);

/// <summary>
/// A department template (module 10, task F2): what a new department's service desk needs to be
/// live the same day. Applying one creates, in one audited transaction, a department, its support
/// groups, a category tree under a root category named after the department, assignment rules that
/// route each category to its group, SLA policies for the department's categories, a catalog with
/// draft services (reviewed and published by the four-eyes rule of task C2) and canned responses.
/// It never changes anything that already exists. Approval rules come with task C3.
/// </summary>
internal sealed record DepartmentTemplate(
    string Key, LocalizedText Name, LocalizedText Description, string CodePrefix, IReadOnlyList<TemplateGroup> Groups, IReadOnlyList<TemplateCategory> Categories,
    IReadOnlyList<TemplatePolicy> Policies, IReadOnlyList<TemplateService> Services, IReadOnlyList<TemplateResponse> Responses);

/// <summary>The four department templates Zelvo ships (product owner decision 2026-11-25), in English and Arabic.</summary>
internal static class DepartmentTemplates
{
    private static LocalizedText T(string en, string ar) => new(en, ar);

    // Business minutes: an 8-hour working day is 480 minutes.
    private static readonly (int, int, int)[] Standard = [(1, 30, 240), (2, 60, 480), (3, 240, 1440), (4, 480, 2400), (5, 960, 4800)];
    private static readonly (int, int, int)[] Requests = [(1, 60, 960), (2, 120, 1440), (3, 480, 2400), (4, 960, 4800), (5, 1440, 7200)];
    private static readonly (int, int, int)[] Voice = [(1, 60, 960), (2, 240, 1920), (3, 480, 2400), (4, 960, 4800), (5, 1440, 9600)];

    public static IReadOnlyList<DepartmentTemplate> All { get; } =
    [
        new(
            "it-service-desk",
            T("IT service desk", "مكتب خدمة تقنية المعلومات"),
            T("Hardware, network, accounts and business applications, with a first-line service desk and two specialist teams.",
                "الأجهزة والشبكات والحسابات وتطبيقات الأعمال، مع مكتب خدمة للدعم الأولي وفريقين متخصصين."),
            "IT",
            [
                new("service-desk", T("IT service desk", "مكتب خدمة تقنية المعلومات")),
                new("infrastructure", T("IT infrastructure", "البنية التحتية لتقنية المعلومات")),
                new("applications", T("Business applications", "تطبيقات الأعمال")),
            ],
            [
                new(T("Hardware", "الأجهزة"), "service-desk", [T("Laptops and desktops", "الحواسيب المحمولة والمكتبية"), T("Printers", "الطابعات"), T("Phones", "الهواتف")]),
                new(T("Accounts and passwords", "الحسابات وكلمات المرور"), "service-desk", []),
                new(T("Network and connectivity", "الشبكة والاتصال"), "infrastructure", [T("Wi-Fi", "الشبكة اللاسلكية"), T("VPN and remote access", "الشبكة الافتراضية الخاصة والوصول عن بعد")]),
                new(T("Email and collaboration", "البريد الإلكتروني وأدوات التعاون"), "applications", []),
                new(T("Business applications", "تطبيقات الأعمال"), "applications", []),
            ],
            [new(TicketType.Incident, Standard), new(TicketType.ServiceRequest, Requests)],
            [new("new-laptop", "LAPTOP"), new("software-access", "ACCESS"), new("password-reset", "PASSWORD")],
            [
                new(T("We are looking into it", "نعمل على معالجة طلبكم"),
                    T("Thank you for contacting the IT service desk. We are looking into this and will update you shortly.",
                        "شكرًا لتواصلكم مع مكتب خدمة تقنية المعلومات. نعمل حاليًا على معالجة طلبكم وسنوافيكم بالمستجدات قريبًا.")),
                new(T("Please restart and try again", "يرجى إعادة التشغيل والمحاولة مجددًا"),
                    T("Please restart your device and try again. If the problem continues, reply to this message and we will help you further.",
                        "يرجى إعادة تشغيل جهازكم والمحاولة مجددًا. وفي حال استمرار المشكلة، يرجى الرد على هذه الرسالة لنواصل مساعدتكم.")),
                new(T("Resolved, please confirm", "تمت المعالجة، يرجى التأكيد"),
                    T("We have resolved this. Please let us know if everything works as expected.",
                        "تمت معالجة الطلب. يرجى إفادتنا إن كان كل شيء يعمل كما هو متوقع.")),
            ]),
        new(
            "hr",
            T("Human resources", "الموارد البشرية"),
            T("Letters and certificates, leave, payroll and onboarding for employees.",
                "الخطابات والشهادات والإجازات والرواتب وإجراءات انضمام الموظفين الجدد."),
            "HR",
            [
                new("hr-services", T("HR services", "خدمات الموارد البشرية")),
                new("payroll", T("Payroll", "الرواتب")),
            ],
            [
                new(T("Certificates and letters", "الشهادات والخطابات"), "hr-services", []),
                new(T("Leave and attendance", "الإجازات والحضور"), "hr-services", []),
                new(T("Payroll and benefits", "الرواتب والمزايا"), "payroll", []),
                new(T("Onboarding", "انضمام الموظفين الجدد"), "hr-services", []),
            ],
            [new(TicketType.ServiceRequest, Requests), new(TicketType.Incident, Requests)],
            [
                new("salary-certificate", "SALARY-CERT"),
                new("general-inquiry", "REQUEST", T("HR general request", "طلب عام للموارد البشرية")),
            ],
            [
                new(T("Request received", "تم استلام الطلب"),
                    T("Thank you. Human resources received your request and will process it within the published time.",
                        "شكرًا لكم. استلمت إدارة الموارد البشرية طلبكم وستتم معالجته خلال المدة المحددة.")),
                new(T("Document is ready", "المستند جاهز"),
                    T("Your document is ready. You can download it from the portal or collect it from human resources.",
                        "المستند المطلوب جاهز. يمكنكم تنزيله من البوابة أو استلامه من إدارة الموارد البشرية.")),
            ]),
        new(
            "facilities",
            T("Facilities", "المرافق"),
            T("Building maintenance, cleaning, meeting rooms and workplace services.",
                "صيانة المباني والنظافة وقاعات الاجتماعات وخدمات بيئة العمل."),
            "FAC",
            [
                new("maintenance", T("Maintenance", "الصيانة")),
                new("workplace", T("Workplace services", "خدمات بيئة العمل")),
            ],
            [
                new(T("Building maintenance", "صيانة المباني"), "maintenance", [T("Electrical", "الكهرباء"), T("Plumbing", "السباكة"), T("Air conditioning", "التكييف")]),
                new(T("Cleaning", "النظافة"), "workplace", []),
                new(T("Meeting rooms and events", "قاعات الاجتماعات والفعاليات"), "workplace", []),
                new(T("Security and access cards", "الأمن وبطاقات الدخول"), "workplace", []),
            ],
            [new(TicketType.Incident, Standard), new(TicketType.ServiceRequest, Requests)],
            [
                new("facility-booking", "ROOM"),
                new("general-inquiry", "MAINTENANCE", T("Maintenance request", "طلب صيانة")),
            ],
            [
                new(T("Technician on the way", "الفني في الطريق"),
                    T("A technician has been assigned and will visit the location shortly.",
                        "تم تكليف فني وسيقوم بزيارة الموقع قريبًا.")),
                new(T("Work completed", "تم إنجاز العمل"),
                    T("The work is complete. Please let us know if anything still needs attention.",
                        "تم إنجاز العمل. يرجى إفادتنا إن كان هناك ما يحتاج إلى متابعة.")),
            ]),
        new(
            "customer-happiness",
            T("Customer happiness centre", "مركز سعادة المتعاملين"),
            T("A government front office for complaints, inquiries and suggestions, with a complaints team and happiness reporting.",
                "مكتب أمامي حكومي للشكاوى والاستفسارات والاقتراحات، مع فريق لمعالجة الشكاوى وتقارير سعادة المتعاملين."),
            "CHC",
            [
                new("happiness-team", T("Customer happiness team", "فريق سعادة المتعاملين")),
                new("complaints", T("Complaints handling", "معالجة الشكاوى")),
            ],
            [
                new(T("General inquiries", "الاستفسارات العامة"), "happiness-team", []),
                new(T("Service complaints", "شكاوى الخدمات"), "complaints", [T("Service delivery", "تقديم الخدمة"), T("Staff conduct", "تعامل الموظفين"), T("Fees and payments", "الرسوم والمدفوعات")]),
                new(T("Digital channels", "القنوات الرقمية"), "happiness-team", []),
                new(T("Suggestions and feedback", "الاقتراحات والملاحظات"), "happiness-team", []),
            ],
            [new(TicketType.Complaint, Voice), new(TicketType.Inquiry, Voice), new(TicketType.Suggestion, Voice)],
            [new("general-inquiry", "SERVICE", T("General service request", "طلب خدمة عام"))],
            [
                new(T("Thank you for contacting us", "شكرًا لتواصلكم معنا"),
                    T("Thank you for contacting the customer happiness centre. We have received your message and will reply within the published time.",
                        "نشكركم على تواصلكم مع مركز سعادة المتعاملين. تم استلام رسالتكم وسيتم الرد عليها خلال المدة المحددة.")),
                new(T("Complaint under review", "الشكوى قيد الدراسة"),
                    T("Your complaint is being reviewed by the responsible team. We will inform you of the outcome.",
                        "شكواكم قيد الدراسة لدى الفريق المختص، وسنبلغكم بالنتيجة.")),
                new(T("Thank you for your suggestion", "شكرًا على اقتراحكم"),
                    T("Thank you for your suggestion. We value your ideas and will let you know the result of our review.",
                        "نشكركم على اقتراحكم، ونقدّر أفكاركم، وسنبلغكم بنتيجة الدراسة.")),
            ]),
    ];

    public static DepartmentTemplate? Find(string key) => All.FirstOrDefault(t => t.Key == key);
}
