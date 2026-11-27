using Zelvo.Data;
using Zelvo.Data.Cmdb;
using Zelvo.Data.Localization;
using Zelvo.Tickets.Forms;

namespace Zelvo.Api.Cmdb;

/// <summary>A class Zelvo offers ready-made, with its attributes.</summary>
internal sealed record StandardClass(
    string Key, string? ParentKey, CiFamily Family, LocalizedText Name, LocalizedText Description, IReadOnlyList<FormField> Attributes, bool RequesterVisible = false);

/// <summary>
/// Zelvo's standard classes in English and Arabic (Settings → CMDB → "Add the standard classes").
/// The attribute keys used to identify items (hostName, serialNumber, macAddress, resourceId) are
/// the ones discovery and reconciliation (G2) match on.
/// </summary>
internal static class CmdbStandardClasses
{
    private static FormField Text(string key, string en, string ar, int maxLength = 200) =>
        new() { Key = key, Type = FieldType.Text, Label = new(en, ar), Validation = new FieldValidation { MaxLength = maxLength } };

    private static FormField Number(string key, string en, string ar, decimal min = 0, decimal max = 1_000_000) =>
        new() { Key = key, Type = FieldType.Number, Label = new(en, ar), Validation = new FieldValidation { Min = min, Max = max } };

    private static FormField Date(string key, string en, string ar) => new() { Key = key, Type = FieldType.Date, Label = new(en, ar) };

    private static FormField Choice(string key, string en, string ar, params (string Value, string En, string Ar)[] options) =>
        new() { Key = key, Type = FieldType.Dropdown, Label = new(en, ar), Options = [.. options.Select(o => new FieldOption(o.Value, new(o.En, o.Ar)))] };

    private static readonly FormField Environment = Choice("environment", "Environment", "البيئة",
        ("production", "Production", "الإنتاج"), ("test", "Test", "الاختبار"), ("development", "Development", "التطوير"));

    private static readonly FormField Criticality = Choice("criticality", "Criticality", "درجة الأهمية",
        ("high", "High", "عالية"), ("medium", "Medium", "متوسطة"), ("low", "Low", "منخفضة"));

    public static IReadOnlyList<StandardClass> All { get; } =
    [
        // Hardware
        new("hardware", null, CiFamily.Hardware, new("Hardware", "الأجهزة"), new("Physical devices.", "الأجهزة المادية."),
            [Text("serialNumber", "Serial number", "الرقم التسلسلي", 100), Text("manufacturer", "Manufacturer", "الشركة المصنّعة", 100),
                Text("model", "Model", "الطراز", 100), Date("purchaseDate", "Purchase date", "تاريخ الشراء")]),
        new("server", "hardware", CiFamily.Hardware, new("Server", "خادم"), new("Physical servers.", "الخوادم المادية."),
            [Text("hostName", "Host name", "اسم المضيف", 253), Text("ipAddress", "IP address", "عنوان IP", 45), Text("macAddress", "MAC address", "عنوان MAC", 17),
                Text("operatingSystem", "Operating system", "نظام التشغيل", 100), Number("cpuCores", "CPU cores", "أنوية المعالج", 1, 1024),
                Number("memoryGb", "Memory (GB)", "الذاكرة (غيغابايت)", 1, 65536), Environment]),
        new("computer", "hardware", CiFamily.Hardware, new("Computer", "حاسوب"), new("Laptops and desktops of staff.", "الحواسيب المحمولة والمكتبية للموظفين."),
            [Text("hostName", "Host name", "اسم المضيف", 253), Text("macAddress", "MAC address", "عنوان MAC", 17), Text("operatingSystem", "Operating system", "نظام التشغيل", 100)]),
        new("laptop", "computer", CiFamily.Hardware, new("Laptop", "حاسوب محمول"), new(null, null), []),
        new("desktop", "computer", CiFamily.Hardware, new("Desktop", "حاسوب مكتبي"), new(null, null), []),
        new("printer", "hardware", CiFamily.Hardware, new("Printer", "طابعة"), new(null, null),
            [Text("ipAddress", "IP address", "عنوان IP", 45)], RequesterVisible: true),
        new("mobile-device", "hardware", CiFamily.Hardware, new("Mobile device", "جهاز محمول"), new("Phones and tablets.", "الهواتف والأجهزة اللوحية."),
            [Text("imei", "IMEI", "رقم IMEI", 20)]),

        // Software
        new("software", null, CiFamily.Software, new("Software", "البرمجيات"), new("Installed software and its versions.", "البرمجيات المثبتة وإصداراتها."),
            [Text("vendor", "Vendor", "المورّد", 100), Text("version", "Version", "الإصدار", 50)]),
        new("operating-system", "software", CiFamily.Software, new("Operating system", "نظام تشغيل"), new(null, null), []),
        new("installed-software", "software", CiFamily.Software, new("Installed software", "برنامج مثبت"), new(null, null), []),

        // Business services
        new("business-service", null, CiFamily.BusinessService, new("Business service", "خدمة أعمال"),
            new("What the organisation offers its customers or staff, for example email or payroll.", "ما تقدّمه الجهة لمتعامليها أو موظفيها، مثل البريد الإلكتروني أو الرواتب."),
            [Criticality, Text("serviceHours", "Service hours", "ساعات الخدمة", 100)], RequesterVisible: true),

        // Applications
        new("application", null, CiFamily.Application, new("Application", "تطبيق"), new("Applications and their components.", "التطبيقات ومكوّناتها."),
            [Text("vendor", "Vendor", "المورّد", 100), Text("version", "Version", "الإصدار", 50), Environment, Text("url", "Address", "العنوان", 500)],
            RequesterVisible: true),
        new("web-application", "application", CiFamily.Application, new("Web application", "تطبيق ويب"), new(null, null), [], RequesterVisible: true),
        new("database", "application", CiFamily.Application, new("Database", "قاعدة بيانات"), new(null, null),
            [Choice("engine", "Engine", "المحرّك", ("postgresql", "PostgreSQL", "PostgreSQL"), ("sql-server", "SQL Server", "SQL Server"),
                ("oracle", "Oracle", "Oracle"), ("mysql", "MySQL", "MySQL"), ("other", "Other", "أخرى"))]),

        // Network
        new("network-device", null, CiFamily.Network, new("Network device", "جهاز شبكة"), new("Routers, switches, firewalls and access points.", "الموجّهات والمحوّلات والجدران النارية ونقاط الوصول."),
            [Text("hostName", "Host name", "اسم المضيف", 253), Text("ipAddress", "IP address", "عنوان IP", 45), Text("macAddress", "MAC address", "عنوان MAC", 17),
                Text("serialNumber", "Serial number", "الرقم التسلسلي", 100)]),
        new("router", "network-device", CiFamily.Network, new("Router", "موجّه"), new(null, null), []),
        new("switch", "network-device", CiFamily.Network, new("Switch", "محوّل"), new(null, null), []),
        new("firewall", "network-device", CiFamily.Network, new("Firewall", "جدار ناري"), new(null, null), []),

        // Cloud resources
        new("cloud-resource", null, CiFamily.CloudResource, new("Cloud resource", "مورد سحابي"), new("Resources in a public or private cloud.", "الموارد في سحابة عامة أو خاصة."),
            [Text("resourceId", "Resource id", "معرّف المورد", 500), Choice("provider", "Provider", "المزوّد", ("azure", "Microsoft Azure", "Microsoft Azure"),
                ("vmware", "VMware", "VMware"), ("aws", "Amazon Web Services", "Amazon Web Services"), ("other", "Other", "أخرى")), Text("region", "Region", "المنطقة", 100)]),
        new("virtual-machine", "cloud-resource", CiFamily.CloudResource, new("Virtual machine", "جهاز افتراضي"), new(null, null),
            [Text("hostName", "Host name", "اسم المضيف", 253), Text("operatingSystem", "Operating system", "نظام التشغيل", 100),
                Number("cpuCores", "CPU cores", "أنوية المعالج", 1, 1024), Number("memoryGb", "Memory (GB)", "الذاكرة (غيغابايت)", 1, 65536), Environment]),
        new("cloud-database", "cloud-resource", CiFamily.CloudResource, new("Cloud database", "قاعدة بيانات سحابية"), new(null, null), []),
        new("storage", "cloud-resource", CiFamily.CloudResource, new("Storage", "التخزين"), new(null, null), [Number("capacityGb", "Capacity (GB)", "السعة (غيغابايت)", 1, 100_000_000)]),
    ];

    /// <summary>Adds the standard classes missing from <paramref name="existing"/> (by key) to <paramref name="db"/>; returns those added.</summary>
    public static List<CiClass> AddMissing(ZelvoDbContext db, Dictionary<string, CiClass> existing)
    {
        var added = new List<CiClass>();
        foreach (var standard in All)
        {
            if (existing.ContainsKey(standard.Key))
            {
                continue;
            }

            CiClass? parent = null;
            if (standard.ParentKey is { } parentKey && !existing.TryGetValue(parentKey, out parent))
            {
                continue;
            }

            var family = parent?.Family ?? standard.Family;
            var ciClass = new CiClass
            {
                Key = standard.Key,
                ParentId = parent?.Id,
                Depth = (parent?.Depth ?? 0) + 1,
                Family = family,
                Name = standard.Name,
                Description = standard.Description,
                Attributes = CmdbAttributes.Write(standard.Attributes),
                RequesterVisible = standard.RequesterVisible,
                Position = existing.Values.Count(c => c.ParentId == parent?.Id && c.Family == family),
            };
            db.CiClasses.Add(ciClass);
            existing[ciClass.Key] = ciClass;
            added.Add(ciClass);
        }

        return added;
    }
}
