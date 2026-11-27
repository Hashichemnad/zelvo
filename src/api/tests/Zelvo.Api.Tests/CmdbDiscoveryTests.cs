using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Zelvo.Api.Cmdb;
using Zelvo.Connectors;
using Zelvo.Data;
using Zelvo.Data.Cmdb;
using Zelvo.Data.Connectors;
using Zelvo.Data.Identity;
using Zelvo.Data.Tenancy;

namespace Zelvo.Api.Tests;

/// <summary>Discovery sources in memory (task G2): what each pull source reports now.</summary>
internal sealed class FakeDiscoveryClients : IDiscoveryClients
{
    public Dictionary<DiscoverySourceKind, List<DiscoveredRecord>> Reports { get; } = [];

    public List<(DiscoverySourceKind Kind, string? ConnectorCredential, string? Credential)> Reads { get; } = [];

    public Task<IReadOnlyList<DiscoveredRecord>> DiscoverAsync(
        DiscoverySourceKind kind, DiscoverySettings settings, Connector? connector, ConnectorSettings? connectorSettings, string? connectorCredential, string? credential,
        ExchangeLog log, CancellationToken cancellationToken)
    {
        Reads.Add((kind, connectorCredential, credential));
        log.Exchanges.Add(new HttpExchange("GET", "https://graph.microsoft.com/v1.0/deviceManagement/managedDevices", 200, 3, null, null,
            """{"value":[{"userPrincipalName":"someone@example.com"}]}""", "application/json", null));
        return Task.FromResult<IReadOnlyList<DiscoveredRecord>>([.. Reports.GetValueOrDefault(kind) ?? []]);
    }
}

/// <summary>CMDB import, the discovery API, pull sources, reconciliation, duplicates and merging (task G2), against PostgreSQL.</summary>
public sealed class CmdbDiscoveryTests(DatabaseFixture database) : IAsyncDisposable
{
    private readonly FakeDiscoveryClients _sources = new();
    private ApiFactory? _factoryLazy;

    private ApiFactory Factory => _factoryLazy ??= ApiFactory.ForDatabase(database).WithConnectors(new FakeConnectorClients()).WithServices(s =>
    {
        s.RemoveAll<IDiscoveryClients>();
        s.AddSingleton<IDiscoveryClients>(_sources);
    });

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask DisposeAsync()
    {
        if (_factoryLazy is not null)
        {
            await _factoryLazy.DisposeAsync();
        }
    }

    private Task<int> RunDiscoveryAsync(Tenant tenant) =>
        Factory.Services.GetRequiredService<CmdbDiscoveryJobs>().RunTenantAsync(tenant.Id, tenant.Slug, Ct);

    private async Task<T> InTenantAsync<T>(Tenant tenant, Func<ZelvoDbContext, Task<T>> work)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>().SetTenant(tenant.Id, tenant.Slug);
        return await work(scope.ServiceProvider.GetRequiredService<ZelvoDbContext>());
    }

    private static JsonObject Attrs(params (string Key, object Value)[] values)
    {
        var o = new JsonObject();
        foreach (var (key, value) in values)
        {
            o[key] = JsonValue.Create(value);
        }

        return o;
    }

    [Fact]
    public async Task The_discovery_API_creates_and_links_items_by_identification_rules_with_precedence_software_relationships_and_snapshots()
    {
        var tenant = await database.CreateTenantAsync("cdp");
        using var admin = await Factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.TenantAdmin);
        var classes = await admin.StandardClassesAsync();

        // An item people entered by hand, with a serial number and their own host name.
        var manual = await admin.ItemAsync(classes["server"], "SRV-APP-01", new { serialNumber = "CZ1234", hostName = "srv-app-01.entity.local", memoryGb = 32 });

        var source = await admin.SendOkAsync(HttpMethod.Post, "/api/v1/cmdb/discovery/sources", new { name = "Scanner", kind = "DiscoveryApi" }, HttpStatusCode.Created);
        Assert.Null(source["nextSyncAt"]?.GetValue<string>());
        using var badCredential = await admin.SendAsync(HttpMethod.Post, "/api/v1/cmdb/discovery/sources", new { name = "Scanner 2", kind = "DiscoveryApi", credential = "x" });
        Assert.Equal("credential-not-used", (await badCredential.ProblemAsync()).Code);

        // The tool pushes with an API key that holds cmdb.manage (an integration may not manage sources).
        var (key, _) = await admin.CreateApiKeyAsync(BuiltInRoles.TenantAdmin, "cmdb.manage", "cmdb.read");
        using var tool = Factory.ForTenant(tenant).WithApiKey(key);
        Assert.Equal(HttpStatusCode.Forbidden, (await tool.SendAsync(HttpMethod.Get, "/api/v1/cmdb/discovery/sources")).StatusCode);
        var stats = await tool.SendOkAsync(HttpMethod.Post, $"/api/v1/cmdb/discovery/sources/{source.Id()}/records", new
        {
            records = new object[]
            {
                // Same serial number: linked to the hand-made item; the scanner's memory and name lose to people's.
                new { id = "dev-1", @class = "server", name = "srv-app-01", attributes = new { serialNumber = "CZ1234", memoryGb = 64, cpuCores = 8, operatingSystem = "Windows Server 2022" } },
                new
                {
                    id = "dev-2", @class = "virtual-machine", name = "vm-web-01", attributes = new { hostName = "vm-web-01", cpuCores = "lots" },
                    software = new[] { new { name = "Microsoft 365 Apps", publisher = "Microsoft", version = "16.0" } },
                    relationships = new[] { new { type = "runs-on", target = "dev-1" } },
                },
                new { id = "dev-3", @class = "toaster", name = "toaster-01" },
            },
        });
        Assert.Equal(3, (int)stats["received"]!);
        Assert.Equal(1, (int)stats["created"]!);
        Assert.Equal(1, (int)stats["linked"]!);
        Assert.Equal(1, (int)stats["unknownClass"]!);
        Assert.Equal(1, (int)stats["valuesRefused"]!);
        Assert.Equal(1, (int)stats["relationships"]!);

        var server = await admin.SendOkAsync(HttpMethod.Get, $"/api/v1/cmdb/items/{manual}");
        Assert.Equal("SRV-APP-01", (string)server["name"]!);
        JsonNode? Value(JsonObject item, string attribute) => item["attributes"]!.AsArray().Single(a => (string)a!["field"]!["key"]! == attribute)!["value"];
        Assert.Equal(32, (int)Value(server, "memoryGb")!);
        Assert.Equal(8, (int)Value(server, "cpuCores")!);
        var discovery = await admin.SendOkAsync(HttpMethod.Get, $"/api/v1/cmdb/items/{manual}/discovery");
        Assert.True((bool)discovery["valueSetters"]!["memoryGb"]!["manual"]!);
        Assert.Equal("Scanner", (string)discovery["valueSetters"]!["cpuCores"]!["source"]!["name"]!);
        Assert.Equal("dev-1", (string)discovery["records"]![0]!["externalId"]!);

        // With the scanner ranked above people for memory, its value wins at the next push.
        await admin.SendOkAsync(HttpMethod.Patch, $"/api/v1/cmdb/discovery/sources/{source.Id()}", new { attributePriorities = new Dictionary<string, int> { ["memoryGb"] = 5 } });
        await tool.SendOkAsync(HttpMethod.Post, $"/api/v1/cmdb/discovery/sources/{source.Id()}/records", new
        {
            records = new object[] { new { id = "dev-1", @class = "server", name = "srv-app-01", attributes = new { serialNumber = "CZ1234", memoryGb = 64 } } },
        });
        Assert.Equal(64, (int)Value(await admin.SendOkAsync(HttpMethod.Get, $"/api/v1/cmdb/items/{manual}"), "memoryGb")!);

        // The VM: software, the relationship to its host.
        var vm = (await admin.SendOkAsync(HttpMethod.Get, "/api/v1/cmdb/items?q=vm-web-01"))["items"]![0]!.AsObject();
        var vmDiscovery = await admin.SendOkAsync(HttpMethod.Get, $"/api/v1/cmdb/items/{vm.Id()}/discovery");
        Assert.Equal("Microsoft 365 Apps", (string)vmDiscovery["software"]![0]!["name"]!);
        var relationships = await admin.ArrayAsync($"/api/v1/cmdb/items/{vm.Id()}/relationships");
        Assert.Contains(relationships, r => (string)r!["type"]!["key"]! == "runs-on" && (string)r["other"]!["name"]! == "SRV-APP-01");

        // A snapshot that ends without the VM marks it missing; the item stays.
        var done = await tool.SendOkAsync(HttpMethod.Post, $"/api/v1/cmdb/discovery/sources/{source.Id()}/snapshot-complete", new { startedAt = DateTimeOffset.UtcNow });
        Assert.Equal(2, (int)done["missing"]!);
        var missing = await admin.SendOkAsync(HttpMethod.Get, "/api/v1/cmdb/items?missing=true");
        Assert.Equal(2, (int)missing["totalCount"]!);
        Assert.NotNull((string?)missing["items"]![0]!["missingSince"]);

        // Reported again: no longer missing.
        await tool.SendOkAsync(HttpMethod.Post, $"/api/v1/cmdb/discovery/sources/{source.Id()}/records", new
        {
            records = new object[] { new { id = "dev-2", @class = "virtual-machine", name = "vm-web-01", attributes = new { hostName = "vm-web-01" } } },
        });
        Assert.Equal(1, (int)(await admin.SendOkAsync(HttpMethod.Get, "/api/v1/cmdb/items?missing=true"))["totalCount"]!);

        // Audited, and the source shows its last push.
        var list = await admin.ArrayAsync("/api/v1/cmdb/discovery/sources");
        Assert.Equal(2, (int)list[0]!["recordCount"]!);
        Assert.True(await InTenantAsync(tenant, db => db.AuditEvents.AnyAsync(e => e.Action == "cmdb.discovery.pushed", Ct)));
        using var tooMany = await tool.SendAsync(HttpMethod.Post, $"/api/v1/cmdb/discovery/sources/{source.Id()}/records",
            new { records = Enumerable.Range(0, 1001).Select(i => new { id = $"x{i}", @class = "server", name = $"x{i}" }) });
        Assert.Equal(HttpStatusCode.BadRequest, tooMany.StatusCode);
    }

    [Fact]
    public async Task Pull_sources_sync_on_schedule_mark_missing_items_and_raise_ambiguous_records_and_duplicates_for_review()
    {
        var tenant = await database.CreateTenantAsync("cdl");
        using var admin = await Factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.TenantAdmin);
        var classes = await admin.StandardClassesAsync();
        var appSecret = MessagingApi.Secret("entra");
        var connector = (await admin.SendOkAsync(HttpMethod.Post, "/api/v1/connectors", new
        {
            name = "Entra", kind = "EntraId", entra = new { directoryId = Guid.NewGuid().ToString(), clientId = Guid.NewGuid().ToString() }, credential = appSecret,
        }, HttpStatusCode.Created)).Id();

        using var noConnector = await admin.SendAsync(HttpMethod.Post, "/api/v1/cmdb/discovery/sources", new { name = "Intune", kind = "Intune" });
        Assert.Equal("connector-required", (await noConnector.ProblemAsync()).Code);
        var intune = await admin.SendOkAsync(HttpMethod.Post, "/api/v1/cmdb/discovery/sources", new { name = "Intune", kind = "Intune", connectorId = connector, priority = 40 },
            HttpStatusCode.Created);
        Assert.NotNull((string?)intune["nextSyncAt"]);

        // Two laptops entered by hand share a host name: a laptop Intune reports by that host name matches both.
        var laptopA = await admin.ItemAsync(classes["laptop"], "Laptop A", new { hostName = "LT-0042" });
        var laptopB = await admin.ItemAsync(classes["laptop"], "Laptop B", new { hostName = "lt-0042" });
        var incident = IncidentApi.IdOf(await admin.CreateAsync(new { title = "Laptop B is slow", requesterId = (Guid?)null }));
        await admin.SendOkAsync(HttpMethod.Put, $"/api/v1/incidents/{incident}/configuration-items/{laptopB}");
        var types = await admin.TypesAsync();
        var printer = await admin.ItemAsync(classes["printer"], "PRN-1");
        await admin.RelateAsync(laptopB, types["connects-to"], printer);

        _sources.Reports[DiscoverySourceKind.Intune] =
        [
            new DiscoveredRecord("d-1", "computer", "LT-0042", Attrs(("hostName", "LT-0042"), ("serialNumber", "SN-42"))),
            new DiscoveredRecord("d-2", "computer", "LT-0100", Attrs(("hostName", "LT-0100"), ("serialNumber", "SN-100"))),
        ];
        Assert.Equal(1, await RunDiscoveryAsync(tenant));
        Assert.Equal((DiscoverySourceKind.Intune, appSecret, (string?)null), _sources.Reads[^1]);
        // Not due again until its interval has passed.
        Assert.Equal(0, await RunDiscoveryAsync(tenant));

        var source = (await admin.ArrayAsync("/api/v1/cmdb/discovery/sources"))[0]!;
        Assert.True((bool)source["lastSyncSucceeded"]!);
        Assert.Equal(1, (int)source["lastSyncStats"]!["created"]!);
        Assert.Equal(1, (int)source["lastSyncStats"]!["review"]!);
        Assert.Equal(1, (int)source["reviewCount"]!);
        Assert.True(await InTenantAsync(tenant, db => db.IntegrationLog.AnyAsync(l => l.EventType == "cmdb.discovery.intune.sync", Ct)));
        Assert.False(await InTenantAsync(tenant, db => db.IntegrationLog.AnyAsync(l => l.ResponseBody!.Contains("someone@example.com"), Ct)));

        // The review queue: the record matched both laptops; it is linked to Laptop A.
        var queue = await admin.SendOkAsync(HttpMethod.Get, "/api/v1/cmdb/review");
        var ambiguous = queue["items"]!.AsArray().Single(r => (string)r!["kind"]! == "Ambiguous")!;
        Assert.Equal("hostName", (string)ambiguous["matchedOn"]!);
        Assert.Equal(2, ambiguous["items"]!.AsArray().Count);
        Assert.Equal("d-1", (string)ambiguous["record"]!["externalId"]!);
        using var wrong = await admin.SendAsync(HttpMethod.Post, $"/api/v1/cmdb/review/{ambiguous["id"]}/link", new { itemId = printer });
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        var linked = await admin.SendOkAsync(HttpMethod.Post, $"/api/v1/cmdb/review/{ambiguous["id"]}/link", new { itemId = laptopA });
        Assert.Equal("Linked", (string)linked["state"]!);
        Assert.Equal("Intune", (string)(await admin.SendOkAsync(HttpMethod.Get, $"/api/v1/cmdb/items/{laptopA}/discovery"))["valueSetters"]!["serialNumber"]!["source"]!["name"]!);

        // The next sync reaches Laptop A by its link; it now shares its host name with Laptop B: a possible duplicate.
        await InTenantAsync(tenant, async db =>
        {
            await db.CiDiscoverySources.ExecuteUpdateAsync(s => s.SetProperty(x => x.NextSyncAt, DateTimeOffset.UtcNow.AddMinutes(-1)), Ct);
            return 0;
        });
        _sources.Reports[DiscoverySourceKind.Intune] =
            [new DiscoveredRecord("d-1", "computer", "LT-0042", Attrs(("hostName", "LT-0042"), ("serialNumber", "SN-42"), ("operatingSystem", "Windows 11")))];
        await RunDiscoveryAsync(tenant);
        var duplicate = (await admin.SendOkAsync(HttpMethod.Get, "/api/v1/cmdb/review?kind=PossibleDuplicate"))["items"]![0]!;
        Assert.Equal("hostName", (string)duplicate["matchedOn"]!);

        // LT-0100 was not reported by this complete sync: marked missing, never deleted.
        var lt100 = (await admin.SendOkAsync(HttpMethod.Get, "/api/v1/cmdb/items?q=LT-0100"))["items"]![0]!;
        Assert.NotNull((string?)lt100["missingSince"]);
        Assert.Equal("Active", (string)lt100["status"]!);
        var records = await admin.SendOkAsync(HttpMethod.Get, $"/api/v1/cmdb/discovery/sources/{intune.Id()}/records?missing=true");
        Assert.Equal("d-2", (string)records["items"]![0]!["externalId"]!);

        // Merge Laptop B into Laptop A: the ticket and the relationship move, B is retired and points at A.
        var merged = await admin.SendOkAsync(HttpMethod.Post, $"/api/v1/cmdb/review/{duplicate["id"]}/merge", new { keepId = laptopA });
        Assert.Equal("Merged", (string)merged["state"]!);
        var b = await admin.SendOkAsync(HttpMethod.Get, $"/api/v1/cmdb/items/{laptopB}");
        Assert.Equal("Retired", (string)b["status"]!);
        Assert.Equal(laptopA, Guid.Parse((string)b["mergedIntoId"]!));
        var tickets = await admin.ArrayAsync($"/api/v1/cmdb/items/{laptopA}/tickets");
        Assert.Contains(tickets, t => Guid.Parse((string)t!["id"]!) == incident);
        Assert.Contains(await admin.ArrayAsync($"/api/v1/cmdb/items/{laptopA}/relationships"), r => (string)r!["other"]!["name"]! == "PRN-1");
        Assert.True(await InTenantAsync(tenant, db => db.AuditEvents.AnyAsync(e => e.Action == "cmdb.items.merged", Ct)));
        // Decided reviews leave the queue and are not raised again.
        Assert.Equal(0, (int)(await admin.SendOkAsync(HttpMethod.Get, "/api/v1/cmdb/review"))["totalCount"]!);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.SendAsync(HttpMethod.Post, $"/api/v1/cmdb/review/{duplicate["id"]}/keep-apart")).StatusCode);

        // Keep apart: two different servers with the same serial typo are left alone afterwards.
        var s1 = await admin.ItemAsync(classes["server"], "srv-a", new { serialNumber = "SAME" });
        var s2 = await admin.ItemAsync(classes["server"], "srv-b", new { serialNumber = "OTHER" });
        await InTenantAsync(tenant, async db =>
        {
            db.ChangeTracker.Clear();
            var item = await db.ConfigurationItems.SingleAsync(i => i.Id == s2, Ct);
            item.Attributes = """{"serialNumber":"SAME"}""";
            await db.SaveChangesAsync(Ct);
            return 0;
        });
        await InTenantAsync(tenant, async db =>
        {
            await using var scope = Factory.Services.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<ITenantContext>().SetTenant(tenant.Id, tenant.Slug);
            var reconciler = scope.ServiceProvider.GetRequiredService<CmdbReconciler>();
            var sdb = scope.ServiceProvider.GetRequiredService<ZelvoDbContext>();
            var item = await sdb.ConfigurationItems.SingleAsync(i => i.Id == s2, Ct);
            await reconciler.RaiseDuplicatesAsync(item, await CiClassIndex.LoadAsync(sdb, Ct), DateTimeOffset.UtcNow, Ct);
            await sdb.SaveChangesAsync(Ct);
            await reconciler.RaiseDuplicatesAsync(item, await CiClassIndex.LoadAsync(sdb, Ct), DateTimeOffset.UtcNow, Ct);
            await sdb.SaveChangesAsync(Ct);
            return 0;
        });
        var pair = (await admin.SendOkAsync(HttpMethod.Get, "/api/v1/cmdb/review"))["items"]!.AsArray();
        Assert.Single(pair);
        await admin.SendOkAsync(HttpMethod.Post, $"/api/v1/cmdb/review/{pair[0]!["id"]}/keep-apart");
        Assert.Equal("KeptApart", (string)(await admin.SendOkAsync(HttpMethod.Get, "/api/v1/cmdb/review?state=KeptApart"))["items"]![0]!["state"]!);
        Assert.NotEqual(s1, s2);
    }

    [Fact]
    public async Task VCenter_needs_its_password_again_for_a_new_address_and_never_returns_it_and_only_administrators_manage_sources()
    {
        var tenant = await database.CreateTenantAsync("cdv");
        using var admin = await Factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.TenantAdmin);
        using var agent = await Factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.Agent);
        var password = MessagingApi.Secret("vc");
        var settings = new { url = "https://vcenter.entity.local", username = "zelvo-ro@vsphere.local", certificateThumbprint = new string('A', 64) };
        using var noPassword = await admin.SendAsync(HttpMethod.Post, "/api/v1/cmdb/discovery/sources", new { name = "vCenter", kind = "VCenter", settings });
        Assert.Equal("credential-required", (await noPassword.ProblemAsync()).Code);
        using var httpOnly = await admin.SendAsync(HttpMethod.Post, "/api/v1/cmdb/discovery/sources",
            new { name = "vCenter", kind = "VCenter", settings = new { url = "http://vcenter.entity.local", username = "x" }, credential = password });
        Assert.Equal("url-not-https", (await httpOnly.ProblemAsync()).Code);

        var created = await admin.SendOkAsync(HttpMethod.Post, "/api/v1/cmdb/discovery/sources", new { name = "vCenter", kind = "VCenter", settings, credential = password, syncMinutes = 60 },
            HttpStatusCode.Created);
        Assert.True((bool)created["hasCredential"]!);
        Assert.DoesNotContain(password, created.ToJsonString(), StringComparison.Ordinal);
        Assert.DoesNotContain(password, (await admin.ArrayAsync("/api/v1/cmdb/discovery/sources")).ToJsonString(), StringComparison.Ordinal);

        using var moved = await admin.SendAsync(HttpMethod.Patch, $"/api/v1/cmdb/discovery/sources/{created.Id()}",
            new { settings = new { url = "https://other.entity.local", username = "zelvo-ro@vsphere.local" } });
        Assert.Equal("credential-required", (await moved.ProblemAsync()).Code);

        _sources.Reports[DiscoverySourceKind.VCenter] =
        [
            new DiscoveredRecord("host:host-1", "server", "esx-01", Attrs(("hostName", "esx-01"))),
            new DiscoveredRecord("vm:vm-1", "virtual-machine", "vm-01", Attrs(("hostName", "vm-01"), ("cpuCores", 4)), Links: [new DiscoveredLink("runs-on", "host:host-1")]),
        ];
        await admin.StandardClassesAsync();
        await RunDiscoveryAsync(tenant);
        Assert.Equal(password, _sources.Reads[^1].Credential);
        Assert.Equal(1, (int)(await admin.ArrayAsync("/api/v1/cmdb/discovery/sources"))[0]!["lastSyncStats"]!["relationships"]!);
        var test = await admin.SendOkAsync(HttpMethod.Post, $"/api/v1/cmdb/discovery/sources/{created.Id()}/test");
        Assert.Equal(2, (int)test["count"]!);
        Assert.Equal(1, (int)test["classes"]!["server"]!);

        Assert.Equal(HttpStatusCode.Forbidden, (await agent.SendAsync(HttpMethod.Get, "/api/v1/cmdb/discovery/sources")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await agent.SendAsync(HttpMethod.Get, "/api/v1/cmdb/review")).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await admin.SendAsync(HttpMethod.Post, $"/api/v1/cmdb/discovery/sources/{created.Id()}/sync")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.SendAsync(HttpMethod.Delete, $"/api/v1/cmdb/discovery/sources/{created.Id()}")).StatusCode);
        // Its items stay.
        Assert.Equal(2, (int)(await admin.SendOkAsync(HttpMethod.Get, "/api/v1/cmdb/items"))["totalCount"]!);
    }

    [Fact]
    public async Task Imports_check_every_row_first_and_match_rows_without_numbers_by_identification_rules()
    {
        var tenant = await database.CreateTenantAsync("cdi");
        using var admin = await Factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.TenantAdmin);
        using var agent = await Factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.Agent);
        var classes = await admin.StandardClassesAsync();
        var existing = await admin.ItemAsync(classes["laptop"], "LT-1", new { serialNumber = "SN-1" });
        var group = await admin.GroupAsync("Desktop support");

        // Rules of a class of its own: computers are told apart by serial number only.
        var computer = await admin.ArrayAsync("/api/v1/cmdb/classes");
        var computerClass = computer.Single(c => (string)c!["key"]! == "computer")!;
        Assert.Contains(computerClass["effectiveIdentificationRules"]!.AsArray(), r => (string)r![0]! == "hostName");
        using var unknownKey = await admin.SendAsync(HttpMethod.Patch, $"/api/v1/cmdb/classes/{computerClass["id"]}", new { identificationRules = new[] { new[] { "colour" } } });
        Assert.Equal("rule-key-unknown", (await unknownKey.ProblemAsync()).Code);
        var updated = await admin.SendOkAsync(HttpMethod.Patch, $"/api/v1/cmdb/classes/{computerClass["id"]}", new { identificationRules = new[] { new[] { "serialNumber" } } });
        Assert.Single(updated["effectiveIdentificationRules"]!.AsArray());

        var bad = "class,name,attributes,attr.memoryGb,ownerGroup\nlaptop,LT-2,\"{\"\"serialNumber\"\":\"\"SN-2\"\"}\",lots,Nobody\ntoaster,T-1,,,\nlaptop,LT-1 renamed,\"{\"\"serialNumber\"\":\"\"SN-1\"\"}\",,\n";
        using var checkedFile = await ImportAsync(admin, bad, dryRun: false);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, checkedFile.StatusCode);
        var problems = (await checkedFile.JsonAsync())["errors"]!.AsArray();
        Assert.Contains(problems, p => (string)p!["code"]! == "class-unknown");
        Assert.Contains(problems, p => (string)p!["code"]! == "group-unknown");
        Assert.Contains(problems, p => (string)p!["code"]! == "attribute-unknown");
        Assert.Equal(1, (int)(await admin.SendOkAsync(HttpMethod.Get, "/api/v1/cmdb/items"))["totalCount"]!);

        var good = "class,name,attributes,attr.hostName,ownerGroup\nlaptop,LT-2,\"{\"\"serialNumber\"\":\"\"SN-2\"\"}\",lt-2,Desktop support\nlaptop,LT-1 renamed,\"{\"\"serialNumber\"\":\"\"SN-1\"\"}\",lt-1,\nlaptop,LT-2 again,\"{\"\"serialNumber\"\":\"\"SN-2\"\"}\",,\n";
        using var sameTwice = await ImportAsync(admin, good, dryRun: true);
        Assert.Contains((await sameTwice.JsonAsync())["errors"]!.AsArray(), p => (string)p!["code"]! == "row-same-item");

        var once = string.Join('\n', good.Split('\n').Take(3)) + "\n";
        using var dry = await ImportAsync(admin, once, dryRun: true);
        var dryResult = await dry.JsonAsync();
        Assert.Equal(1, (int)dryResult["created"]!);
        Assert.Equal(1, (int)dryResult["updated"]!);
        using var applied = await ImportAsync(admin, once, dryRun: false);
        Assert.Equal(HttpStatusCode.OK, applied.StatusCode);
        var lt1 = await admin.SendOkAsync(HttpMethod.Get, $"/api/v1/cmdb/items/{existing}");
        Assert.Equal("LT-1 renamed", (string)lt1["name"]!);
        var lt2 = (await admin.SendOkAsync(HttpMethod.Get, "/api/v1/cmdb/items?q=LT-2"))["items"]![0]!;
        Assert.Equal(group, Guid.Parse((string)lt2["ownerGroup"]!["id"]!));

        // Export: CSV with the attributes column, for cmdb.read; import needs cmdb.manage.
        using var export = await agent.GetAsync(new Uri("/api/v1/bulk/configuration-items/export?format=csv", UriKind.Relative), Ct);
        Assert.Equal(HttpStatusCode.OK, export.StatusCode);
        var csv = await export.Content.ReadAsStringAsync(Ct);
        Assert.Contains("SN-2", csv, StringComparison.Ordinal);
        using var refused = await ImportAsync(agent, once, dryRun: true);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.True(await InTenantAsync(tenant, db => db.AuditEvents.AnyAsync(e => e.Action == "bulk.configuration-items.imported", Ct)));
    }

    private static async Task<HttpResponseMessage> ImportAsync(HttpClient client, string csv, bool dryRun)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(csv));
        file.Headers.ContentType = new MediaTypeHeaderValue("text/csv");
        form.Add(file, "file", "items.csv");
        return await client.PostAsync(new Uri($"/api/v1/bulk/configuration-items/import?dryRun={(dryRun ? "true" : "false")}", UriKind.Relative), form, Ct);
    }
}
