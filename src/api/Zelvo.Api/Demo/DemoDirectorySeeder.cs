using Microsoft.EntityFrameworkCore;
using Zelvo.Api.ServiceDesk;
using Zelvo.Api.Settings;
using Zelvo.Data;
using Zelvo.Data.Directory;
using Zelvo.Data.Localization;
using Zelvo.Tickets;

namespace Zelvo.Api.Demo;

/// <summary>
/// Task B2: categories, locations, skills, groups, assignment rules and a default for each sample
/// organisation, with the demo agent and team lead as group members; the B1 sample incidents get a
/// category, a location and a group. Fictional, like everything in the demo.
/// </summary>
internal sealed class DemoDirectorySeeder : IDemoSeeder
{
    public string Key => "b2-directory";

    public int Order => 110;

    private sealed record Node(string En, string Ar, Node[] Children);

    private static Node N(string en, string ar, params Node[] children) => new(en, ar, children);

    private static readonly Node[] GovernmentCategories =
    [
        N("E-services", "الخدمات الإلكترونية", N("Portal access", "الدخول إلى البوابة"), N("Online payments", "الدفع الإلكتروني")),
        N("Hardware", "الأجهزة", N("Printers", "الطابعات"), N("Computers", "أجهزة الحاسوب", N("Laptops", "الحواسيب المحمولة"))),
        N("Accounts and email", "الحسابات والبريد", N("Passwords", "كلمات المرور")),
        N("Business applications", "التطبيقات المؤسسية", N("Transaction management", "إدارة المعاملات")),
    ];

    private static readonly Node[] CompanyCategories =
    [
        N("Email and collaboration", "البريد والتعاون", N("Email delivery", "تسليم البريد")),
        N("Hardware", "الأجهزة", N("Docking stations", "محطات الإرساء"), N("Laptops", "الحواسيب المحمولة")),
        N("Network", "الشبكة", N("VPN", "الشبكة الافتراضية الخاصة"), N("Wi-Fi", "الشبكة اللاسلكية")),
        N("Access and permissions", "الوصول والصلاحيات", N("Shared folders", "المجلدات المشتركة")),
        N("Finance systems", "الأنظمة المالية", N("Invoicing", "الفوترة")),
    ];

    private static readonly Node[] GovernmentLocations =
    [
        N("Ajman", "عجمان", N("Main building", "المبنى الرئيسي", N("Ground floor", "الطابق الأرضي"), N("First floor", "الطابق الأول")), N("Customer happiness centre", "مركز إسعاد المتعاملين")),
        N("Al Manama", "المنامة", N("Branch office", "المكتب الفرعي")),
    ];

    private static readonly Node[] CompanyLocations =
    [
        N("Dubai", "دبي", N("Head office", "المقر الرئيسي", N("Floor 12", "الطابق 12"), N("Floor 14", "الطابق 14"))),
        N("Abu Dhabi", "أبوظبي", N("Sales office", "مكتب المبيعات")),
        N("Remote", "العمل عن بُعد"),
    ];

    public async Task SeedAsync(DemoSeedContext context, CancellationToken cancellationToken)
    {
        var db = context.Get<ZelvoDbContext>();
        var settings = context.Get<TenantSettingsStore>();
        var government = context.Organisation.Kind == DemoOrganisationKind.Government;
        var users = (await db.Users.Where(u => u.Email != null).ToListAsync(cancellationToken).ConfigureAwait(false))
            .GroupBy(u => u.Email!, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        Data.Identity.UserAccount Persona(string persona) => users[context.Organisation.UsernameOf(context.Organisation.User(persona)!)];

        var categories = Add(db.Categories, government ? GovernmentCategories : CompanyCategories, (name, parent, depth, position) =>
            new Category { Name = name, ParentId = parent, Depth = depth, Position = position });
        var locations = Add(db.Locations, government ? GovernmentLocations : CompanyLocations, (name, parent, depth, position) =>
            new Location { Name = name, ParentId = parent, Depth = depth, Position = position });

        var arabicSkill = new Skill { Name = new LocalizedText("Arabic correspondence", "المراسلات العربية") };
        var networkSkill = new Skill { Name = new LocalizedText("Networks", "الشبكات") };
        var appsSkill = new Skill { Name = new LocalizedText(government ? "Government systems" : "Finance systems", government ? "الأنظمة الحكومية" : "الأنظمة المالية") };
        db.Skills.AddRange(arabicSkill, networkSkill, appsSkill);

        var agent = Persona("agent");
        var lead = Persona("lead");
        agent.Languages = ["en", "ar"];
        lead.Languages = government ? ["ar", "en"] : ["en"];
        agent.Skills.AddRange([new UserSkill { UserId = agent.Id, SkillId = arabicSkill.Id }, new UserSkill { UserId = agent.Id, SkillId = appsSkill.Id }]);
        lead.Skills.Add(new UserSkill { UserId = lead.Id, SkillId = networkSkill.Id });

        var desk = Group(db, "Service desk", "مكتب الخدمة", "First contact for every request.", "نقطة الاتصال الأولى لجميع الطلبات.",
            (lead, true), (agent, false));
        var network = Group(db, government ? "Infrastructure team" : "Network team", government ? "فريق البنية التحتية" : "فريق الشبكات",
            "Networks, servers and connectivity.", "الشبكات والخوادم والاتصال.", (lead, true), (agent, false));
        var apps = Group(db, "Applications team", "فريق التطبيقات", "Business applications and online services.", "التطبيقات المؤسسية والخدمات الإلكترونية.",
            (agent, false));

        var position = 0;
        AssignmentRule Rule(string en, string ar, SupportGroup group, AssignmentMethod method, string? category = null, string? location = null, string? language = null, Skill? skill = null)
        {
            var rule = new AssignmentRule
            {
                Name = new LocalizedText(en, ar), Position = position++, GroupId = group.Id, Method = method,
                CategoryId = category is null ? null : categories[category].Id, LocationId = location is null ? null : locations[location].Id,
                Language = language, SkillId = skill?.Id,
            };
            db.AssignmentRules.Add(rule);
            return rule;
        }

        if (government)
        {
            Rule("E-services to the applications team", "الخدمات الإلكترونية إلى فريق التطبيقات", apps, AssignmentMethod.RoundRobin, category: "E-services", skill: appsSkill);
            Rule("Main building hardware", "أجهزة المبنى الرئيسي", network, AssignmentMethod.RoundRobin, category: "Hardware", location: "Main building");
            Rule("Arabic requesters to the service desk", "المتعاملون بالعربية إلى مكتب الخدمة", desk, AssignmentMethod.RoundRobin, language: "ar", skill: arabicSkill);
        }
        else
        {
            Rule("Network problems", "مشكلات الشبكة", network, AssignmentMethod.RoundRobin, category: "Network", skill: networkSkill);
            Rule("Finance systems", "الأنظمة المالية", apps, AssignmentMethod.GroupQueue, category: "Finance systems");
            Rule("Head office hardware", "أجهزة المقر الرئيسي", desk, AssignmentMethod.RoundRobin, category: "Hardware", location: "Head office");
        }

        await settings.SetAsync(AssignmentSettings.Default, new AssignmentDefault(desk.Id, AssignmentMethod.GroupQueue), cancellationToken).ConfigureAwait(false);

        // The B1 sample incidents, by title: category, location and group.
        var placed = government
            ? new (string Title, string Category, string Location, SupportGroup Group)[]
            {
                ("انقطاع بوابة الخدمات الإلكترونية", "Portal access", "Main building", apps),
                ("تعذّر دفع رسوم رخصة البناء", "Online payments", "Customer happiness centre", apps),
                ("لا يمكن تتبع طلب شهادة الإنجاز", "Portal access", "Customer happiness centre", apps),
                ("طابعة مكتب خدمة المتعاملين لا تعمل", "Printers", "Ground floor", network),
                ("بطء نظام إدارة المعاملات", "Transaction management", "First floor", apps),
                ("نسيت كلمة المرور لحساب البريد", "Passwords", "Branch office", desk),
            }
            : [
                ("Email delivery delayed for all staff", "Email delivery", "Head office", network),
                ("Customer invoices not sent", "Invoicing", "Floor 14", apps),
                ("Password reset emails not arriving", "Email delivery", "Remote", desk),
                ("Laptop docking station not detected", "Docking stations", "Floor 12", desk),
                ("VPN disconnects every few minutes", "VPN", "Remote", network),
                ("Cannot open shared finance folder", "Shared folders", "Sales office", desk),
            ];
        var titles = placed.Select(p => p.Title).ToList();
        var tickets = await db.Tickets.Where(t => titles.Contains(t.Title)).ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var (title, category, location, group) in placed)
        {
            foreach (var ticket in tickets.Where(t => t.Title == title))
            {
                ticket.CategoryId = categories[category].Id;
                ticket.LocationId = locations[location].Id;
                ticket.GroupId = group.Id;
            }
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Adds a tree; returns its nodes by English name.</summary>
    private static Dictionary<string, TNode> Add<TNode>(DbSet<TNode> set, Node[] roots, Func<LocalizedText, Guid?, int, int, TNode> create)
        where TNode : TreeNode
    {
        var byName = new Dictionary<string, TNode>(StringComparer.Ordinal);
        void AddLevel(IEnumerable<Node> nodes, TNode? parent, int depth)
        {
            var position = 0;
            foreach (var node in nodes)
            {
                var entity = create(new LocalizedText(node.En, node.Ar), parent?.Id, depth, position++);
                set.Add(entity);
                byName.TryAdd(node.En, entity);
                AddLevel(node.Children, entity, depth + 1);
            }
        }

        AddLevel(roots, null, 1);
        return byName;
    }

    private static SupportGroup Group(ZelvoDbContext db, string en, string ar, string descriptionEn, string descriptionAr,
        params (Data.Identity.UserAccount User, bool IsLead)[] members)
    {
        var group = new SupportGroup { Name = new LocalizedText(en, ar), Description = new LocalizedText(descriptionEn, descriptionAr) };
        foreach (var (user, isLead) in members)
        {
            group.Members.Add(new SupportGroupMember { GroupId = group.Id, UserId = user.Id, IsLead = isLead });
        }

        db.SupportGroups.Add(group);
        return group;
    }
}
