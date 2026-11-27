using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Zelvo.Api.Cmdb;
using Zelvo.Api.Settings;
using Zelvo.Connectors;
using Zelvo.Data;
using Zelvo.Data.Cmdb;
using Zelvo.Data.Connectors;

namespace Zelvo.Api.Demo;

/// <summary>
/// Task G2: each sample organisation's discovery: a discovery API source whose records were
/// reconciled with the G1 items (linked by serial number and host name, new items, installed
/// software, a relationship), a record that matched two items and a possible duplicate waiting in
/// the review queue, an item the source stopped reporting, and a pull source on the E7 connector
/// (Intune for the government entity, Active Directory for the company), turned off like the
/// connectors. Fictional, like everything in the demo.
/// </summary>
internal sealed class DemoDiscoverySeeder : IDemoSeeder
{
    public string Key => "g2-discovery";

    public int Order => 272;

    public async Task SeedAsync(DemoSeedContext context, CancellationToken cancellationToken)
    {
        var db = context.Get<ZelvoDbContext>();
        var reconciler = context.Get<CmdbReconciler>();
        var government = context.Organisation.Kind == DemoOrganisationKind.Government;
        var tenant = await db.Tenants.SingleAsync(cancellationToken).ConfigureAwait(false);
        var classes = await db.CiClasses.ToDictionaryAsync(c => c.Key, StringComparer.Ordinal, cancellationToken).ConfigureAwait(false);
        if (!classes.ContainsKey("server"))
        {
            return;
        }

        var scanner = new CiDiscoverySource
        {
            TenantId = tenant.Id, Name = government ? "Network scanner" : "Endpoint agent", Kind = DiscoverySourceKind.DiscoveryApi, Priority = 40,
            AttributePriorities = """{"operatingSystem":20}""",
        };
        db.CiDiscoverySources.Add(scanner);

        // The pull source on the E7 connector, off like the connector itself.
        var connectorKind = government ? ConnectorKind.EntraId : ConnectorKind.Ldap;
        if (await db.Connectors.AsNoTracking().FirstOrDefaultAsync(c => c.Kind == connectorKind, cancellationToken).ConfigureAwait(false) is { } connector)
        {
            db.CiDiscoverySources.Add(new CiDiscoverySource
            {
                TenantId = tenant.Id, Name = government ? "Microsoft Intune" : "Active Directory computers",
                Kind = government ? DiscoverySourceKind.Intune : DiscoverySourceKind.ActiveDirectory, ConnectorId = connector.Id, Active = false,
                Settings = new DiscoverySettings().Write(), SyncMinutes = 1440,
            });
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        // A record that will match two items: one entered by hand from an old asset list shares a host name.
        var prefix = await context.Get<TenantSettingsStore>().GetAsync(CmdbSettings.NumberPrefix, cancellationToken).ConfigureAwait(false);
        var sequence = await CmdbNumbers.NextAsync(db, tenant.Id, cancellationToken).ConfigureAwait(false);
        var hostName = government ? "srv-app-01" : "vm-billing-01";
        db.ConfigurationItems.Add(new ConfigurationItem
        {
            Number = CmdbNumbers.Format(prefix, sequence), Sequence = sequence, ClassId = classes[government ? "server" : "virtual-machine"].Id,
            Name = $"{hostName} (asset list 2023)", Attributes = new JsonObject { ["hostName"] = hostName }.ToJsonString(),
            AttributeSources = """{"hostName":"manual","name":"manual"}""",
        });
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        var records = government ? GovernmentRecords() : CompanyRecords();
        await reconciler.ApplyAsync(scanner, records, false, cancellationToken).ConfigureAwait(false);

        // One record the scanner reported last month and no longer sees.
        var old = new DiscoveredRecord(government ? "scan-old-printer" : "agent-old-laptop", government ? "printer" : "laptop", government ? "PRN-2ND-FLOOR" : "LT-0007",
            government ? A(("ipAddress", "10.20.0.77")) : A(("serialNumber", "DEMO-LT-0007"), ("hostName", "LT-0007")));
        await reconciler.ApplyAsync(scanner, [old], false, cancellationToken).ConfigureAwait(false);
        var oldRecord = await db.CiSourceRecords.SingleAsync(r => r.SourceId == scanner.Id && r.ExternalId == old.ExternalId, cancellationToken).ConfigureAwait(false);
        oldRecord.LastSeenAt = DateTimeOffset.UtcNow.AddDays(-30);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await reconciler.MarkMissingAsync(scanner.Id, DateTimeOffset.UtcNow.AddDays(-1), cancellationToken).ConfigureAwait(false);

        // A possible duplicate: an item someone added by hand with the serial number of one the scanner linked.
        var twinSequence = await CmdbNumbers.NextAsync(db, tenant.Id, cancellationToken).ConfigureAwait(false);
        var twin = new ConfigurationItem
        {
            Number = CmdbNumbers.Format(prefix, twinSequence), Sequence = twinSequence, ClassId = classes[government ? "server" : "laptop"].Id,
            Name = government ? "Web server 2" : "Ahmed's laptop",
            Attributes = new JsonObject { ["serialNumber"] = government ? "DEMO-SRV-1012" : "DEMO-LT-0042" }.ToJsonString(),
            AttributeSources = """{"serialNumber":"manual","name":"manual"}""",
        };
        db.ConfigurationItems.Add(twin);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await reconciler.RaiseDuplicatesAsync(twin, await CiClassIndex.LoadAsync(db, cancellationToken).ConfigureAwait(false), DateTimeOffset.UtcNow, cancellationToken)
            .ConfigureAwait(false);

        var tracked = await db.CiDiscoverySources.SingleAsync(s => s.Id == scanner.Id, cancellationToken).ConfigureAwait(false);
        tracked.LastSyncAt = DateTimeOffset.UtcNow.AddHours(-2);
        tracked.LastSyncSucceeded = true;
        tracked.LastSyncStats = new ReconcileStats { Received = records.Count, Created = 3, Updated = 2, Linked = 2, Review = 1, Software = 4, Relationships = 1 }.Write();
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private static List<DiscoveredRecord> GovernmentRecords() =>
    [
        // Linked by serial number and host name to the G1 servers.
        new("scan-10.10.1.12", "server", "srv-web-02", A(("serialNumber", "DEMO-SRV-1012"), ("hostName", "srv-web-02"), ("operatingSystem", "Ubuntu 24.04.2 LTS"))),
        new("scan-10.10.3.31", "server", "srv-db-01", A(("hostName", "srv-db-01"), ("operatingSystem", "Ubuntu 24.04.2 LTS"), ("macAddress", "02:00:5E:10:03:31"))),
        // Matches srv-app-01 and the old asset list's item: waits for review.
        new("scan-10.10.2.21", "server", "srv-app-01", A(("hostName", "srv-app-01"), ("operatingSystem", "Windows Server 2025"))),
        // New items.
        new("scan-10.10.0.3", "switch", "sw-floor-02", A(("hostName", "sw-floor-02"), ("ipAddress", "10.10.0.3"), ("serialNumber", "DEMO-SW-0302")),
            Links: [new DiscoveredLink("connects-to", "scan-10.10.0.2")]),
        new("scan-10.10.0.2", "switch", "sw-core-01", A(("hostName", "sw-core-01"), ("ipAddress", "10.10.0.2"))),
        new("scan-10.20.1.15", "desktop", "PC-CS-015", A(("hostName", "PC-CS-015"), ("serialNumber", "DEMO-PC-0015"), ("operatingSystem", "Windows 11")),
            Software: [new("Microsoft 365 Apps for enterprise", "Microsoft Corporation", "16.0"), new("Adobe Acrobat Reader", "Adobe", "24.1")]),
    ];

    private static List<DiscoveredRecord> CompanyRecords() =>
    [
        new("agent-lt-0042", "laptop", "LT-0042", A(("serialNumber", "DEMO-LT-0042"), ("hostName", "LT-0042"), ("operatingSystem", "Windows 11 23H2")),
            "sales.lead@demo-company.example",
            [new("Microsoft 365 Apps for enterprise", "Microsoft Corporation", "16.0"), new("Fictional CRM Desktop", "Fictional Software", "8.2")]),
        new("agent-vm-billing-01", "virtual-machine", "vm-billing-01", A(("hostName", "vm-billing-01"), ("operatingSystem", "Ubuntu 24.04.2 LTS"))),
        new("agent-lt-0051", "laptop", "LT-0051", A(("serialNumber", "DEMO-LT-0051"), ("hostName", "LT-0051"), ("operatingSystem", "Windows 11 23H2")), null,
            [new("Microsoft 365 Apps for enterprise", "Microsoft Corporation", "16.0")]),
        new("agent-lt-0052", "laptop", "LT-0052", A(("serialNumber", "DEMO-LT-0052"), ("hostName", "LT-0052"), ("operatingSystem", "macOS 14")), null,
            [new("Microsoft 365 Apps for enterprise", "Microsoft Corporation", "16.0")]),
    ];

    private static JsonObject A(params (string Key, object Value)[] values)
    {
        var o = new JsonObject();
        foreach (var (key, value) in values)
        {
            o[key] = JsonValue.Create(value);
        }

        return o;
    }
}
