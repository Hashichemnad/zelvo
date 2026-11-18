using Zelvo.Data.Identity;
using Zelvo.Data.Localization;

namespace Zelvo.Api.Demo;

/// <summary>What kind of organisation a sample tenant shows.</summary>
public enum DemoOrganisationKind
{
    /// <summary>A UAE government entity serving citizens and residents.</summary>
    Government,

    /// <summary>A private company with internal IT and customer service.</summary>
    Company,
}

/// <summary>A ready-made demo user of a sample organisation: one per persona.</summary>
/// <param name="Persona">Short persona key used in addresses and the demo panel, for example <c>agent</c>.</param>
/// <param name="RoleKey">The built-in role the user holds.</param>
/// <param name="FirstName">Given name (fictional).</param>
/// <param name="LastName">Family name (fictional).</param>
internal sealed record DemoUser(string Persona, string RoleKey, string FirstName, string LastName)
{
    public string DisplayName => $"{FirstName} {LastName}";
}

/// <summary>A sample organisation of the demo installation: one tenant.</summary>
internal sealed record DemoOrganisation(
    string Slug,
    DemoOrganisationKind Kind,
    LocalizedText Name,
    string EmailDomain,
    string DefaultLanguage,
    CalendarSystem DefaultCalendar,
    DigitSystem DefaultDigits,
    IReadOnlyList<DemoUser> Users)
{
    /// <summary>Username (and email) of a demo user in Keycloak.</summary>
    public string UsernameOf(DemoUser user) => $"{user.Persona}@{EmailDomain}";

    public DemoUser? User(string persona) => Users.FirstOrDefault(u => string.Equals(u.Persona, persona, StringComparison.Ordinal));
}

/// <summary>
/// The sample organisations of a demo installation (demo mode, <c>docs/requirements.md</c>):
/// a government entity and a private company, with fictional names and people (product owner
/// decision 2026-11-18). Email addresses use the reserved <c>.example</c> domain, so nothing is
/// ever sent to a real mailbox. Later modules add their sample data through
/// <see cref="IDemoSeeder"/>s, not here.
/// </summary>
internal static class DemoCatalog
{
    /// <summary>Persona keys in the order the demo panel shows them.</summary>
    public static IReadOnlyList<(string Persona, string RoleKey)> Personas { get; } =
    [
        ("requester", BuiltInRoles.Requester),
        ("agent", BuiltInRoles.Agent),
        ("lead", BuiltInRoles.TeamLead),
        ("approver", BuiltInRoles.Approver),
        ("admin", BuiltInRoles.TenantAdmin),
        ("executive", BuiltInRoles.ExecutiveAuditor),
    ];

    public static DemoOrganisation Government { get; } = new(
        "demo-municipality",
        DemoOrganisationKind.Government,
        new LocalizedText("Department of Municipal Services", "دائرة الخدمات البلدية"),
        "municipality.example",
        LocalizedText.Arabic,
        CalendarSystem.Gregorian,
        DigitSystem.Western,
        [
            new("requester", BuiltInRoles.Requester, "Mariam", "Al Suwaidi"),
            new("agent", BuiltInRoles.Agent, "Hamad", "Al Ketbi"),
            new("lead", BuiltInRoles.TeamLead, "Noura", "Al Mansoori"),
            new("approver", BuiltInRoles.Approver, "Saeed", "Al Nuaimi"),
            new("admin", BuiltInRoles.TenantAdmin, "Fatima", "Al Hammadi"),
            new("executive", BuiltInRoles.ExecutiveAuditor, "Abdullah", "Al Shamsi"),
        ]);

    public static DemoOrganisation Company { get; } = new(
        "demo-company",
        DemoOrganisationKind.Company,
        new LocalizedText("Gulf Horizon Trading", "أفق الخليج للتجارة"),
        "gulfhorizon.example",
        LocalizedText.English,
        CalendarSystem.Gregorian,
        DigitSystem.Western,
        [
            new("requester", BuiltInRoles.Requester, "Priya", "Nair"),
            new("agent", BuiltInRoles.Agent, "Daniel", "Mathew"),
            new("lead", BuiltInRoles.TeamLead, "Sara", "Haddad"),
            new("approver", BuiltInRoles.Approver, "Youssef", "Karam"),
            new("admin", BuiltInRoles.TenantAdmin, "Elena", "Petrova"),
            new("executive", BuiltInRoles.ExecutiveAuditor, "Rashid", "Al Marri"),
        ]);

    public static IReadOnlyList<DemoOrganisation> Organisations { get; } = [Government, Company];

    public static DemoOrganisation? Find(string slug) =>
        Organisations.FirstOrDefault(o => string.Equals(o.Slug, slug, StringComparison.Ordinal));
}
