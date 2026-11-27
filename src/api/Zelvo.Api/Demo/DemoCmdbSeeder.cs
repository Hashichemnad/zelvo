using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Zelvo.Api.Cmdb;
using Zelvo.Api.Settings;
using Zelvo.Data;
using Zelvo.Data.Cmdb;
using Zelvo.Data.Localization;
using Zelvo.Data.Tickets;
using Zelvo.Search;

namespace Zelvo.Api.Demo;

/// <summary>
/// Task G1: each sample organisation's CMDB: Zelvo's standard classes, business services, the
/// applications behind them, the servers, databases and network devices they run on, who uses the
/// services (a department and the VIP customer group), and sample incidents linked to the items
/// they are about. Fictional, like everything in the demo.
/// </summary>
internal sealed class DemoCmdbSeeder : IDemoSeeder
{
    public string Key => "g1-cmdb";

    public int Order => 270;

    private sealed record Item(string Key, string Class, string Name, LocalizedText Description, string? Group = null, JsonObject? Attributes = null,
        CiStatus Status = CiStatus.Active);

    private sealed record Link(string From, string Type, string To);

    public async Task SeedAsync(DemoSeedContext context, CancellationToken cancellationToken)
    {
        var db = context.Get<ZelvoDbContext>();
        var government = context.Organisation.Kind == DemoOrganisationKind.Government;
        var tenant = await db.Tenants.SingleAsync(cancellationToken).ConfigureAwait(false);
        var prefix = await context.Get<TenantSettingsStore>().GetAsync(CmdbSettings.NumberPrefix, cancellationToken).ConfigureAwait(false);

        var classes = await db.CiClasses.ToDictionaryAsync(c => c.Key, StringComparer.Ordinal, cancellationToken).ConfigureAwait(false);
        CmdbStandardClasses.AddMissing(db, classes);
        await CmdbRelationshipDefaults.EnsureAsync(db, cancellationToken).ConfigureAwait(false);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        var types = await db.CiRelationshipTypes.ToDictionaryAsync(t => t.Key, StringComparer.Ordinal, cancellationToken).ConfigureAwait(false);

        // A type of the organisation's own, to show that administrators add them.
        var backsUp = new CiRelationshipType
        {
            Key = "backs-up", OutgoingName = new("Backs up", "ينسخ احتياطياً"), IncomingName = new("Backed up by", "منسوخ احتياطياً بواسطة"),
            Impact = ImpactDirection.None,
        };
        db.CiRelationshipTypes.Add(backsUp);
        types[backsUp.Key] = backsUp;

        var groups = await db.SupportGroups.ToListAsync(cancellationToken).ConfigureAwait(false);
        Guid? GroupId(string? en) => en is null ? null : groups.FirstOrDefault(g => g.Name.En == en)?.Id;
        var infrastructure = government ? "Infrastructure team" : "Network team";

        var items = government ? GovernmentItems(infrastructure) : CompanyItems(infrastructure);
        var links = government ? GovernmentLinks : CompanyLinks;

        var created = new Dictionary<string, ConfigurationItem>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            var sequence = await CmdbNumbers.NextAsync(db, tenant.Id, cancellationToken).ConfigureAwait(false);
            var ci = new ConfigurationItem
            {
                Number = CmdbNumbers.Format(prefix, sequence),
                Sequence = sequence,
                ClassId = classes[item.Class].Id,
                Name = item.Name,
                Description = item.Description,
                Status = item.Status,
                OwnerGroupId = GroupId(item.Group),
                Attributes = (item.Attributes ?? []).ToJsonString(),
            };
            db.ConfigurationItems.Add(ci);
            created[item.Key] = ci;
        }

        foreach (var link in links)
        {
            db.CiRelationships.Add(new CiRelationship { TypeId = types[link.Type].Id, SourceId = created[link.From].Id, TargetCiId = created[link.To].Id });
        }

        // Who uses the services (decision 6, 2026-11-26): a department and the VIP customer group.
        var happiness = await db.Departments.FirstOrDefaultAsync(d => d.Name.En == (government ? "Customer happiness" : "Sales"), cancellationToken).ConfigureAwait(false);
        var finance = await db.Departments.FirstOrDefaultAsync(d => d.Name.En == "Finance", cancellationToken).ConfigureAwait(false);
        var vip = await db.CustomerGroups.FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        var usedBy = types[CmdbRelationshipDefaults.UsedBy].Id;
        var mainService = created[government ? "portal" : "email"];
        if (happiness is not null)
        {
            db.CiRelationships.Add(new CiRelationship { TypeId = usedBy, SourceId = mainService.Id, TargetDepartmentId = happiness.Id });
        }

        if (finance is not null)
        {
            db.CiRelationships.Add(new CiRelationship { TypeId = usedBy, SourceId = created[government ? "email" : "billing"].Id, TargetDepartmentId = finance.Id });
        }

        if (vip is not null)
        {
            db.CiRelationships.Add(new CiRelationship { TypeId = usedBy, SourceId = mainService.Id, TargetCustomerGroupId = vip.Id });
        }

        // Sample incidents on the items they are about.
        var incidents = await db.Tickets.Where(t => t.Type == TicketType.Incident).ToListAsync(cancellationToken).ConfigureAwait(false);
        var ticketLinks = government
            ? new (string Title, string Item)[]
            {
                ("انقطاع بوابة الخدمات الإلكترونية", "portal"), ("انقطاع بوابة الخدمات الإلكترونية", "website"),
                ("تعذّر دفع رسوم رخصة البناء", "permits"), ("طابعة مكتب خدمة المتعاملين لا تعمل", "printer"), ("بطء نظام إدارة المعاملات", "transactions"),
            }
            : [("Email delivery delayed for all staff", "email"), ("Email delivery delayed for all staff", "mail-gateway"), ("Customer invoices not sent", "billing"),
                ("VPN disconnects every few minutes", "vpn"), ("Laptop docking station not detected", "laptop")];
        foreach (var (title, key) in ticketLinks)
        {
            if (incidents.FirstOrDefault(t => t.Title == title) is { } ticket)
            {
                db.TicketConfigurationItems.Add(new TicketConfigurationItem { TicketId = ticket.Id, ConfigurationItemId = created[key].Id });
            }
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        var indexer = context.Get<ISearchIndexer>();
        var index = await CiClassIndex.LoadAsync(db, cancellationToken).ConfigureAwait(false);
        var roles = await CmdbSearch.ReaderRolesAsync(db, cancellationToken).ConfigureAwait(false);
        foreach (var ci in created.Values)
        {
            indexer.Upsert(CmdbSearch.Document(ci, index, roles));
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private static JsonObject A(params (string Key, JsonNode? Value)[] values)
    {
        var o = new JsonObject();
        foreach (var (key, value) in values)
        {
            o[key] = value;
        }

        return o;
    }

    private static Item[] GovernmentItems(string infrastructure) =>
    [
        new("portal", "business-service", "E-services portal", new("Online services for customers of the municipality.", "الخدمات الإلكترونية لمتعاملي البلدية."),
            "Applications team", A(("criticality", "high"), ("serviceHours", "24/7"))),
        new("permits", "business-service", "Building permits", new("Applying for and renewing building permits.", "التقديم على رخص البناء وتجديدها."),
            "Applications team", A(("criticality", "high"), ("serviceHours", "Mon–Fri 07:30–15:30"))),
        new("email", "business-service", "Staff email", new("Email for every employee.", "البريد الإلكتروني لجميع الموظفين."), infrastructure, A(("criticality", "medium"))),
        new("website", "web-application", "E-services website", new("The public website of the e-services.", "الموقع العام للخدمات الإلكترونية."),
            "Applications team", A(("environment", "production"), ("version", "4.2"))),
        new("permits-app", "application", "Permits system", new("Back office for building permits.", "النظام الخلفي لرخص البناء."), "Applications team",
            A(("vendor", "Fictional Systems LLC"), ("version", "2025.3"), ("environment", "production"))),
        new("transactions", "application", "Transaction management system", new("Incoming and outgoing correspondence.", "المراسلات الواردة والصادرة."),
            "Applications team", A(("environment", "production"))),
        new("permits-db", "database", "permits-db", new(null, null), infrastructure, A(("engine", "postgresql"))),
        new("web-01", "server", "srv-web-01", new(null, null), infrastructure,
            A(("hostName", "srv-web-01"), ("ipAddress", "10.10.1.11"), ("operatingSystem", "Ubuntu 24.04"), ("cpuCores", 8), ("memoryGb", 32), ("environment", "production"), ("serialNumber", "DEMO-SRV-1011"))),
        new("web-02", "server", "srv-web-02", new(null, null), infrastructure,
            A(("hostName", "srv-web-02"), ("ipAddress", "10.10.1.12"), ("operatingSystem", "Ubuntu 24.04"), ("cpuCores", 8), ("memoryGb", 32), ("environment", "production"), ("serialNumber", "DEMO-SRV-1012"))),
        new("app-01", "server", "srv-app-01", new(null, null), infrastructure,
            A(("hostName", "srv-app-01"), ("ipAddress", "10.10.2.21"), ("operatingSystem", "Windows Server 2025"), ("cpuCores", 16), ("memoryGb", 64), ("environment", "production"))),
        new("db-01", "server", "srv-db-01", new(null, null), infrastructure,
            A(("hostName", "srv-db-01"), ("ipAddress", "10.10.3.31"), ("operatingSystem", "Ubuntu 24.04"), ("cpuCores", 16), ("memoryGb", 128), ("environment", "production"))),
        new("backup-01", "server", "srv-backup-01", new(null, null), infrastructure, A(("hostName", "srv-backup-01"), ("environment", "production"))),
        new("mail-01", "virtual-machine", "vm-mail-01", new(null, null), infrastructure, A(("hostName", "vm-mail-01"), ("environment", "production"))),
        new("switch", "switch", "sw-core-01", new(null, null), infrastructure, A(("hostName", "sw-core-01"), ("ipAddress", "10.10.0.2"))),
        new("firewall", "firewall", "fw-edge-01", new(null, null), infrastructure, A(("hostName", "fw-edge-01"), ("ipAddress", "10.10.0.1"))),
        new("printer", "printer", "Customer service printer", new("Ground floor, customer service hall.", "الطابق الأرضي، صالة خدمة المتعاملين."), "Service desk",
            A(("ipAddress", "10.20.0.50"), ("manufacturer", "Fictional Print Co."))),
        new("old-web", "server", "srv-web-legacy", new("Replaced by srv-web-01 and srv-web-02.", "استُبدل بالخادمين srv-web-01 وsrv-web-02."), infrastructure,
            A(("hostName", "srv-web-legacy")), CiStatus.Retired),
    ];

    private static readonly Link[] GovernmentLinks =
    [
        new("portal", "depends-on", "website"), new("permits", "depends-on", "permits-app"), new("website", "depends-on", "permits-app"),
        new("website", "runs-on", "web-01"), new("website", "runs-on", "web-02"), new("permits-app", "runs-on", "app-01"),
        new("permits-app", "depends-on", "permits-db"), new("permits-db", "runs-on", "db-01"), new("transactions", "runs-on", "app-01"),
        new("email", "runs-on", "mail-01"), new("web-01", "connects-to", "switch"), new("web-02", "connects-to", "switch"),
        new("app-01", "connects-to", "switch"), new("db-01", "connects-to", "switch"), new("switch", "connects-to", "firewall"),
        new("backup-01", "backs-up", "db-01"),
    ];

    private static Item[] CompanyItems(string infrastructure) =>
    [
        new("email", "business-service", "Email", new("Email and calendars for all staff.", "البريد الإلكتروني والتقويم لجميع الموظفين."), infrastructure,
            A(("criticality", "high"), ("serviceHours", "24/7"))),
        new("billing", "business-service", "Customer billing", new("Invoices and payments of customers.", "فواتير المتعاملين ومدفوعاتهم."), "Applications team",
            A(("criticality", "high"))),
        new("vpn", "business-service", "Remote access (VPN)", new("Working from home and on the road.", "العمل من المنزل وأثناء التنقل."), infrastructure,
            A(("criticality", "medium"))),
        new("billing-app", "application", "Billing system", new(null, null), "Applications team", A(("vendor", "Fictional Billing Inc."), ("environment", "production"))),
        new("mail-gateway", "application", "Mail gateway", new("Filters and relays outgoing email.", "يصفّي البريد الصادر ويمرّره."), infrastructure, A(("environment", "production"))),
        new("billing-db", "cloud-database", "billing-db", new(null, null), infrastructure, A(("provider", "azure"), ("region", "UAE North"))),
        new("billing-vm", "virtual-machine", "vm-billing-01", new(null, null), infrastructure,
            A(("hostName", "vm-billing-01"), ("operatingSystem", "Ubuntu 24.04"), ("cpuCores", 4), ("memoryGb", 16), ("environment", "production"))),
        new("mail-vm", "virtual-machine", "vm-mailgw-01", new(null, null), infrastructure, A(("hostName", "vm-mailgw-01"), ("environment", "production"))),
        new("vpn-gw", "firewall", "fw-vpn-01", new("VPN concentrator.", "مركّز الشبكة الافتراضية الخاصة."), infrastructure, A(("hostName", "fw-vpn-01"), ("ipAddress", "172.16.0.1"))),
        new("switch", "switch", "sw-hq-12", new("Floor 12 switch.", "محوّل الطابق 12."), infrastructure, A(("hostName", "sw-hq-12"))),
        new("laptop", "laptop", "LT-0042", new(null, null), "Service desk", A(("serialNumber", "DEMO-LT-0042"), ("model", "Fictional Book 14"), ("operatingSystem", "Windows 11"))),
        new("backup", "storage", "backup-vault", new(null, null), infrastructure, A(("provider", "azure"), ("capacityGb", 10240))),
    ];

    private static readonly Link[] CompanyLinks =
    [
        new("email", "depends-on", "mail-gateway"), new("mail-gateway", "runs-on", "mail-vm"), new("billing", "depends-on", "billing-app"),
        new("billing-app", "runs-on", "billing-vm"), new("billing-app", "depends-on", "billing-db"), new("billing-app", "depends-on", "mail-gateway"),
        new("vpn", "depends-on", "vpn-gw"), new("vpn-gw", "connects-to", "switch"), new("laptop", "connects-to", "switch"),
        new("backup", "backs-up", "billing-db"),
    ];
}
