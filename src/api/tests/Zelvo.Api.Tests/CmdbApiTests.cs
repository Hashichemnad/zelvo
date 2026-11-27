using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Zelvo.Data.Directory;
using Zelvo.Data.Identity;
using Zelvo.Data.Localization;
using Zelvo.Data.Sla;

namespace Zelvo.Api.Tests;

/// <summary>Requests to the CMDB API (task G1).</summary>
internal static class CmdbApi
{
    public static async Task<Dictionary<string, Guid>> StandardClassesAsync(this HttpClient admin)
    {
        var classes = await admin.SendOkAsync(HttpMethod.Post, "/api/v1/cmdb/classes/standard");
        return classes["items"]!.AsArray().ToDictionary(c => (string)c!["key"]!, c => Guid.Parse((string)c!["id"]!), StringComparer.Ordinal);
    }

    public static async Task<Dictionary<string, Guid>> TypesAsync(this HttpClient client)
    {
        var types = await client.ArrayAsync("/api/v1/cmdb/relationship-types");
        return types.ToDictionary(t => (string)t!["key"]!, t => Guid.Parse((string)t!["id"]!), StringComparer.Ordinal);
    }

    public static async Task<Guid> ItemAsync(this HttpClient admin, Guid classId, string name, object? attributes = null, string? status = null) =>
        (await admin.SendOkAsync(HttpMethod.Post, "/api/v1/cmdb/items", new { classId, name, attributes, status }, HttpStatusCode.Created)).Id();

    public static Task<JsonObject> RelateAsync(this HttpClient admin, Guid from, Guid typeId, Guid? targetId = null, Guid? departmentId = null, Guid? groupId = null, Guid? userId = null) =>
        admin.SendOkAsync(HttpMethod.Post, $"/api/v1/cmdb/items/{from}/relationships",
            new { typeId, targetId, targetDepartmentId = departmentId, targetCustomerGroupId = groupId, targetUserId = userId }, HttpStatusCode.Created);
}

/// <summary>Configuration items, classes, relationships, impact, the map and CIs on tickets (task G1, module 18), against PostgreSQL.</summary>
public sealed class CmdbApiTests(DatabaseFixture database) : IAsyncDisposable
{
    private readonly ApiFactory _factory = ApiFactory.ForDatabase(database);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    [Fact]
    public async Task Classes_form_a_tree_whose_attributes_are_C1_fields_inherited_by_the_classes_below()
    {
        var tenant = await database.CreateTenantAsync("cmc");
        using var admin = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.TenantAdmin);
        using var agent = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.Agent);
        using var requester = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.Requester);

        var standard = await admin.StandardClassesAsync();
        Assert.Contains("server", standard.Keys);
        Assert.Contains("business-service", standard.Keys);
        // Adding them again changes nothing.
        Assert.Equal(standard.Count, (await admin.StandardClassesAsync()).Count);

        var classes = await agent.ArrayAsync("/api/v1/cmdb/classes");
        var server = classes.Single(c => (string)c!["key"]! == "server")!;
        Assert.Equal("Hardware", (string)server["family"]!);
        Assert.Equal("Hardware › Server", (string)server["path"]!["en"]!);
        Assert.Equal("الأجهزة › خادم", (string)server["path"]!["ar"]!);
        Assert.Contains(server["inheritedAttributes"]!.AsArray(), a => (string)a!["field"]!["key"]! == "serialNumber");
        Assert.Equal(HttpStatusCode.Forbidden, (await requester.SendAsync(HttpMethod.Get, "/api/v1/cmdb/classes")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await agent.SendAsync(HttpMethod.Post, "/api/v1/cmdb/classes/standard")).StatusCode);

        // A sub-class of its own with an attribute; it may not reuse a key from a class above.
        var dbServer = (await admin.SendOkAsync(HttpMethod.Post, "/api/v1/cmdb/classes", new
        {
            key = "database-server", parentId = standard["server"], name = new { en = "Database server" },
            attributes = new object[] { new { key = "engineVersion", type = "Text", label = new { en = "Engine version", ar = "إصدار المحرّك" } } },
        }, HttpStatusCode.Created));
        Assert.Equal(3, (int)dbServer["depth"]!);
        Assert.Equal("Hardware", (string)dbServer["family"]!);

        using var inherited = await admin.SendAsync(HttpMethod.Post, "/api/v1/cmdb/classes", new
        {
            key = "web-server", parentId = standard["server"], name = new { en = "Web server" },
            attributes = new object[] { new { key = "hostName", type = "Text", label = new { en = "Host" } } },
        });
        Assert.Equal(HttpStatusCode.Conflict, inherited.StatusCode);
        Assert.Contains("attribute-inherited", await inherited.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);

        using var fileType = await admin.SendAsync(HttpMethod.Post, "/api/v1/cmdb/classes", new
        {
            key = "racks", family = "Hardware", name = new { en = "Rack" },
            attributes = new object[] { new { key = "photo", type = "File", label = new { en = "Photo" } } },
        });
        Assert.Contains("attribute-type", await fileType.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);

        using var personal = await admin.SendAsync(HttpMethod.Post, "/api/v1/cmdb/classes", new
        {
            key = "racks", family = "Hardware", name = new { en = "Rack" },
            attributes = new object[] { new { key = "ownerId", type = "Text", label = new { en = "Owner" }, validation = new { format = "EmiratesId" } } },
        });
        Assert.Contains("attribute-personal-data", await personal.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);

        // A class above may not take a key a class below already has.
        using var below = await admin.SendAsync(HttpMethod.Patch, $"/api/v1/cmdb/classes/{standard["server"]}", new
        {
            attributes = new object[] { new { key = "engineVersion", type = "Text", label = new { en = "x" } } },
        });
        Assert.Contains("attribute-below", await below.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);

        // Stale ETag.
        using var request = new HttpRequestMessage(HttpMethod.Patch, $"/api/v1/cmdb/classes/{dbServer.Id()}") { Content = JsonContent.Create(new { requesterVisible = true }) };
        request.Headers.TryAddWithoutValidation("If-Match", "\"1\"");
        Assert.Equal(HttpStatusCode.PreconditionFailed, (await admin.SendAsync(request, Ct)).StatusCode);

        // Missing Arabic names are reported (LANG-5); a class with items cannot be deleted.
        var report = await admin.SendOkAsync(HttpMethod.Get, "/api/v1/translations/missing");
        Assert.Contains("database-server", report.ToJsonString(), StringComparison.Ordinal);
        await admin.ItemAsync(dbServer.Id(), "db-01");
        Assert.Equal("in-use", (await (await admin.SendAsync(HttpMethod.Delete, $"/api/v1/cmdb/classes/{dbServer.Id()}")).ProblemAsync()).Code);
        Assert.Equal("in-use", (await (await admin.SendAsync(HttpMethod.Delete, $"/api/v1/cmdb/classes/{standard["server"]}")).ProblemAsync()).Code);
    }

    [Fact]
    public async Task Items_are_numbered_checked_against_their_class_audited_and_searchable()
    {
        var tenant = await database.CreateTenantAsync("cmi");
        using var admin = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.TenantAdmin);
        using var agent = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.Agent);
        var classes = await admin.StandardClassesAsync();
        var group = await admin.GroupAsync("Infrastructure");
        var ajman = await admin.LocationAsync("Ajman");
        var building = await admin.LocationAsync("Main building", ajman);

        // Values are checked with C1's checks.
        using var bad = await admin.SendAsync(HttpMethod.Post, "/api/v1/cmdb/items", new
        {
            classId = classes["server"], name = "srv-01", attributes = new { cpuCores = 0, environment = "staging", unknown = "x" },
        });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        var errors = (await bad.Content.ReadFromJsonAsync<JsonObject>(Ct))!["attributeErrors"]!.AsArray()
            .Select(e => $"{(string)e!["field"]!}:{(string)e["code"]!}").Order(StringComparer.Ordinal).ToList();
        Assert.Equal(["cpuCores:min", "environment:option-unknown", "unknown:field-unknown"], errors);

        var created = await admin.SendOkAsync(HttpMethod.Post, "/api/v1/cmdb/items", new
        {
            classId = classes["server"], name = "  srv-app-01 ", ownerGroupId = group, locationId = building,
            attributes = new { hostName = "srv-app-01", serialNumber = "SN-1", cpuCores = 8, environment = "production" },
        }, HttpStatusCode.Created);
        Assert.Equal("CI-000001", (string)created["number"]!);
        Assert.Equal("srv-app-01", (string)created["name"]!);
        Assert.Equal("Ajman › Main building", (string)created["location"]!["path"]!["en"]!);
        Assert.Equal("SN-1", (string)created["attributes"]!.AsArray().Single(a => (string)a!["field"]!["key"]! == "serialNumber")!["value"]!);
        var id = created.Id();
        Assert.Equal(HttpStatusCode.Forbidden, (await agent.SendAsync(HttpMethod.Post, "/api/v1/cmdb/items", new { classId = classes["server"], name = "x" })).StatusCode);

        // The prefix is a tenant setting; numbers keep counting.
        await admin.SendOkAsync(HttpMethod.Put, "/api/v1/cmdb/settings", new { numberPrefix = "CMDB" });
        var second = await admin.SendOkAsync(HttpMethod.Post, "/api/v1/cmdb/items", new { classId = classes["application"], name = "Payroll app" }, HttpStatusCode.Created);
        Assert.Equal("CMDB-000002", (string)second["number"]!);

        // Moving to another class drops values it does not have; a stale ETag is refused.
        var etag = (string)created["etag"]!;
        using (var change = new HttpRequestMessage(HttpMethod.Patch, $"/api/v1/cmdb/items/{id}") { Content = JsonContent.Create(new { classId = classes["virtual-machine"] }) })
        {
            change.Headers.TryAddWithoutValidation("If-Match", etag);
            var response = await admin.SendAsync(change, Ct);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var moved = (await response.Content.ReadFromJsonAsync<JsonObject>(Ct))!;
            Assert.Equal("virtual-machine", (string)moved["class"]!["key"]!);
            Assert.DoesNotContain("SN-1", moved["attributes"]!.ToJsonString(), StringComparison.Ordinal);
            Assert.Contains("srv-app-01", moved["attributes"]!.ToJsonString(), StringComparison.Ordinal);
        }

        using (var stale = new HttpRequestMessage(HttpMethod.Patch, $"/api/v1/cmdb/items/{id}") { Content = JsonContent.Create(new { status = "Maintenance" }) })
        {
            stale.Headers.TryAddWithoutValidation("If-Match", etag);
            Assert.Equal(HttpStatusCode.PreconditionFailed, (await admin.SendAsync(stale, Ct)).StatusCode);
        }

        await admin.SendOkAsync(HttpMethod.Patch, $"/api/v1/cmdb/items/{id}", new { status = "Maintenance", ownerGroupId = Guid.Empty });

        // History from the audit log, with names instead of ids.
        var history = await agent.ArrayAsync($"/api/v1/cmdb/items/{id}/history");
        Assert.Contains(history, h => h!["changes"]?["classId"]?["new"]?.ToString() == "Cloud resource › Virtual machine");
        Assert.Contains(history, h => h!["changes"]?["status"]?["new"]?.ToString() == "Maintenance");
        Assert.Contains(history, h => h!["changes"]?["ownerGroupId"]?["old"]?.ToString() == "Infrastructure");

        // Lists filter by class (and the classes below it), status and words.
        Assert.Equal(1, (int)(await agent.SendOkAsync(HttpMethod.Get, $"/api/v1/cmdb/items?classId={classes["cloud-resource"]}"))["totalCount"]!);
        Assert.Equal(1, (int)(await agent.SendOkAsync(HttpMethod.Get, "/api/v1/cmdb/items?status=Maintenance"))["totalCount"]!);
        Assert.Equal(1, (int)(await agent.SendOkAsync(HttpMethod.Get, "/api/v1/cmdb/items?q=payroll"))["totalCount"]!);
        Assert.Equal(1, (int)(await agent.SendOkAsync(HttpMethod.Get, $"/api/v1/cmdb/items?locationId={ajman}"))["totalCount"]!);
        await admin.SendOkAsync(HttpMethod.Patch, $"/api/v1/cmdb/items/{second.Id()}", new { status = "Retired" });
        Assert.Equal(1, (int)(await agent.SendOkAsync(HttpMethod.Get, "/api/v1/cmdb/items"))["totalCount"]!);
        Assert.Equal(2, (int)(await agent.SendOkAsync(HttpMethod.Get, "/api/v1/cmdb/items?includeRetired=true"))["totalCount"]!);

        // Search documents for cmdb.read holders.
        await using var db = TestData.AppContext(database, tenant);
        var entry = await db.SearchOutbox.Where(e => e.DocumentId == id.ToString()).OrderBy(e => e.Id).LastAsync(Ct);
        Assert.Equal("ci", entry.DocumentType);
        Assert.Contains("role:agent", entry.Document ?? string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain("role:requester", entry.Document ?? string.Empty, StringComparison.Ordinal);

        // An item without tickets can be deleted.
        Assert.Equal(HttpStatusCode.NoContent, (await admin.SendAsync(HttpMethod.Delete, $"/api/v1/cmdb/items/{second.Id()}")).StatusCode);
    }

    [Fact]
    public async Task Impact_follows_each_relationship_type_and_reaches_the_people_who_use_a_service()
    {
        var tenant = await database.CreateTenantAsync("cmr");
        using var admin = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.TenantAdmin);
        using var agent = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.Agent);
        var classes = await admin.StandardClassesAsync();
        var types = await admin.TypesAsync();
        Assert.Equal(["connects-to", "depends-on", "runs-on", "used-by"], types.Keys.Order(StringComparer.Ordinal));

        var staff = await database.CreateUserAsync(tenant, BuiltInRoles.Requester);
        Guid finance;
        Guid vip;
        await using (var db = TestData.AppContext(database, tenant))
        {
            var department = new Department { Name = new LocalizedText("Finance", "المالية") };
            var group = new CustomerGroup { Name = new LocalizedText("VIP", "كبار المتعاملين") };
            db.Departments.Add(department);
            db.CustomerGroups.Add(group);
            await db.SaveChangesAsync(Ct);
            (await db.Users.SingleAsync(u => u.Id == staff.Id, Ct)).DepartmentId = department.Id;
            await db.SaveChangesAsync(Ct);
            (finance, vip) = (department.Id, group.Id);
        }

        var payroll = await admin.ItemAsync(classes["business-service"], "Payroll");
        var app = await admin.ItemAsync(classes["application"], "Payroll app");
        var server = await admin.ItemAsync(classes["server"], "srv-app-01");
        var sw = await admin.ItemAsync(classes["switch"], "sw-core-01");
        var retired = await admin.ItemAsync(classes["application"], "Old payroll", status: "Retired");
        await admin.RelateAsync(payroll, types["depends-on"], app);
        await admin.RelateAsync(app, types["runs-on"], server);
        await admin.RelateAsync(retired, types["runs-on"], server);
        await admin.RelateAsync(server, types["connects-to"], sw);
        await admin.RelateAsync(payroll, types["used-by"], departmentId: finance);
        await admin.RelateAsync(payroll, types["used-by"], groupId: vip);

        // Refusals: the same relationship twice, an item to itself, an item as the target of used by.
        using var twice = await admin.SendAsync(HttpMethod.Post, $"/api/v1/cmdb/items/{app}/relationships", new { typeId = types["runs-on"], targetId = server });
        Assert.Equal("relationship-exists", (await twice.ProblemAsync()).Code);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.SendAsync(HttpMethod.Post, $"/api/v1/cmdb/items/{app}/relationships", new { typeId = types["runs-on"], targetId = app })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.SendAsync(HttpMethod.Post, $"/api/v1/cmdb/items/{app}/relationships", new { typeId = types["used-by"], targetId = server })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await agent.SendAsync(HttpMethod.Post, $"/api/v1/cmdb/items/{app}/relationships", new { typeId = types["runs-on"], targetId = sw })).StatusCode);

        // Seen from the server: the application runs on it (incoming), it connects to the switch.
        var relationships = await agent.ArrayAsync($"/api/v1/cmdb/items/{server}/relationships");
        Assert.Contains(relationships, r => (string)r!["direction"]! == "Incoming" && (string)r["name"]!["en"]! == "Hosts" && (string)r["other"]!["name"]! == "Payroll app");
        Assert.Contains(relationships, r => (string)r!["direction"]! == "Outgoing" && (string)r["name"]!["en"]! == "Connects to");

        // A fault of the switch reaches the server (connects to goes both ways), the application
        // that runs on it, the service that depends on it and the people who use the service; not
        // the retired application.
        var impact = await agent.SendOkAsync(HttpMethod.Get, $"/api/v1/cmdb/items/{sw}/impact?depth=5");
        var reached = impact["items"]!.AsArray().ToDictionary(i => (string)i!["node"]!["name"]!, i => (int)i!["depth"]!);
        Assert.Equal(new Dictionary<string, int> { ["srv-app-01"] = 1, ["Payroll app"] = 2, ["Payroll"] = 3 }, reached);
        Assert.Equal(["Payroll"], impact["businessServices"]!.AsArray().Select(i => (string)i!["node"]!["name"]!));
        Assert.Equal(["CustomerGroup", "Department"], impact["consumers"]!.AsArray().Select(c => (string)c!["node"]!["kind"]!).Order(StringComparer.Ordinal));
        Assert.Equal(1, (int)impact["userCount"]!);

        // Depth limits the steps; a fault of the service itself reaches its users only.
        Assert.Single((await agent.SendOkAsync(HttpMethod.Get, $"/api/v1/cmdb/items/{sw}/impact?depth=1"))["items"]!.AsArray());
        var service = await agent.SendOkAsync(HttpMethod.Get, $"/api/v1/cmdb/items/{payroll}/impact");
        Assert.Empty(service["items"]!.AsArray());
        Assert.Equal(2, service["consumers"]!.AsArray().Count);
        var many = await agent.SendOkAsync(HttpMethod.Post, "/api/v1/cmdb/impact", new { itemIds = new[] { server, sw }, depth = 1 });
        Assert.Contains(many["items"]!.AsArray(), i => (string)i!["node"]!["name"]! == "Payroll app");

        // The map goes both ways, filtered by type and class; retired items only on request.
        var map = await agent.SendOkAsync(HttpMethod.Get, $"/api/v1/cmdb/items/{app}/map?depth=1");
        Assert.Equal(["Payroll", "Payroll app", "srv-app-01"], map["nodes"]!.AsArray().Select(n => (string)n!["node"]!["name"]!).Order(StringComparer.Ordinal));
        Assert.Equal(2, map["edges"]!.AsArray().Count);
        var deep = await agent.SendOkAsync(HttpMethod.Get, $"/api/v1/cmdb/items/{app}/map?depth=2&retired=true");
        Assert.Contains(deep["nodes"]!.AsArray(), n => (string?)n!["node"]!["name"] == "Old payroll");
        Assert.Contains(deep["nodes"]!.AsArray(), n => (string)n!["node"]!["kind"]! == "Department");
        var runsOnly = await agent.SendOkAsync(HttpMethod.Get, $"/api/v1/cmdb/items/{app}/map?depth=3&typeIds={types["runs-on"]}");
        Assert.Equal(["Payroll app", "srv-app-01"], runsOnly["nodes"]!.AsArray().Select(n => (string)n!["node"]!["name"]!).Order(StringComparer.Ordinal));
        var servers = await agent.SendOkAsync(HttpMethod.Get, $"/api/v1/cmdb/items/{app}/map?depth=3&classIds={classes["hardware"]}&consumers=false");
        Assert.Equal(["Payroll app", "srv-app-01"], servers["nodes"]!.AsArray().Select(n => (string)n!["node"]!["name"]!).Order(StringComparer.Ordinal));

        // Relationships are in both items' history; removing one is too.
        var relationship = relationships.Single(r => (string)r!["direction"]! == "Outgoing")!;
        Assert.Equal(HttpStatusCode.NoContent, (await admin.SendAsync(HttpMethod.Delete, $"/api/v1/cmdb/relationships/{(string)relationship["id"]!}")).StatusCode);
        var swHistory = await agent.ArrayAsync($"/api/v1/cmdb/items/{sw}/history");
        Assert.Contains(swHistory, h => (string)h!["action"]! == "cmdb.relationship.added");
        Assert.Contains(swHistory, h => (string)h!["action"]! == "cmdb.relationship.removed");

        // Custom types: built-in ones keep their impact and cannot be deleted.
        var backsUp = (await admin.SendOkAsync(HttpMethod.Post, "/api/v1/cmdb/relationship-types",
            new { key = "backs-up", outgoingName = new { en = "Backs up", ar = "ينسخ احتياطياً" }, incomingName = new { en = "Backed up by" }, impact = "None" }, HttpStatusCode.Created)).Id();
        Assert.Equal("built-in", (await (await admin.SendAsync(HttpMethod.Patch, $"/api/v1/cmdb/relationship-types/{types["runs-on"]}", new { impact = "None" })).ProblemAsync()).Code);
        Assert.Equal("built-in", (await (await admin.SendAsync(HttpMethod.Delete, $"/api/v1/cmdb/relationship-types/{types["used-by"]}")).ProblemAsync()).Code);
        await admin.RelateAsync(server, backsUp, app);
        Assert.Equal("in-use", (await (await admin.SendAsync(HttpMethod.Delete, $"/api/v1/cmdb/relationship-types/{backsUp}")).ProblemAsync()).Code);
    }

    [Fact]
    public async Task Configuration_items_are_linked_to_tickets_by_staff_and_by_the_picker_field_of_forms()
    {
        var tenant = await database.CreateTenantAsync("cmt");
        using var admin = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.TenantAdmin);
        using var agent = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.Agent);
        using var requester = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.Requester);
        var classes = await admin.StandardClassesAsync();
        var email = await admin.ItemAsync(classes["business-service"], "Email");
        var server = await admin.ItemAsync(classes["server"], "srv-mail-01");
        var planned = await admin.ItemAsync(classes["business-service"], "Chat", status: "Planned");

        // Staff link items to an incident; the incident's history shows it.
        var incident = IncidentApi.IdOf(await agent.CreateAsync(new { title = "Mail is slow", requesterId = (Guid?)null }));
        var links = await agent.SendOkAsync(HttpMethod.Put, $"/api/v1/incidents/{incident}/configuration-items/{server}");
        Assert.Equal("srv-mail-01", (string)links["items"]![0]!["item"]!["name"]!);
        await agent.SendOkAsync(HttpMethod.Put, $"/api/v1/incidents/{incident}/configuration-items/{server}");
        Assert.Single(await agent.ArrayAsync($"/api/v1/incidents/{incident}/configuration-items"));
        Assert.Equal(HttpStatusCode.Forbidden, (await requester.SendAsync(HttpMethod.Get, $"/api/v1/incidents/{incident}/configuration-items")).StatusCode);
        var tickets = await agent.ArrayAsync($"/api/v1/cmdb/items/{server}/tickets");
        Assert.Equal(incident, Guid.Parse((string)tickets.Single()!["id"]!));
        var incidentHistory = await agent.ArrayAsync($"/api/v1/incidents/{incident}/history");
        Assert.Contains(incidentHistory, h => (string)h!["action"]! == "cmdb.ticket-link.added");

        // An item on a ticket cannot be deleted, only retired; retired items cannot be linked.
        Assert.Equal("in-use", (await (await admin.SendAsync(HttpMethod.Delete, $"/api/v1/cmdb/items/{server}")).ProblemAsync()).Code);
        Assert.Equal(HttpStatusCode.NoContent, (await agent.SendAsync(HttpMethod.Delete, $"/api/v1/incidents/{incident}/configuration-items/{server}")).StatusCode);
        await admin.SendOkAsync(HttpMethod.Patch, $"/api/v1/cmdb/items/{server}", new { status = "Retired" });
        Assert.Equal(HttpStatusCode.NotFound, (await agent.SendAsync(HttpMethod.Put, $"/api/v1/incidents/{incident}/configuration-items/{server}")).StatusCode);

        // Requesters find only active items of classes marked for them.
        var found = await requester.ArrayAsync("/api/v1/cmdb/lookup");
        Assert.Equal(["Email"], found.Select(f => (string)f!["name"]!));
        Assert.Equal(2, (await agent.ArrayAsync("/api/v1/cmdb/lookup")).Count);

        // A form with the configuration item picker (the type C1 deferred to the CMDB).
        var formId = (await admin.SendOkAsync(HttpMethod.Post, "/api/v1/forms", new
        {
            key = "service-problem", name = new { en = "Service problem" },
            schema = new
            {
                elements = new object[]
                {
                    new { type = "Field", field = new { key = "service", type = "ConfigurationItem", label = new { en = "Service", ar = "الخدمة" }, required = true,
                        configurationItemClasses = new[] { classes["business-service"] } } },
                },
            },
        }, HttpStatusCode.Created)).Id();
        await admin.SendOkAsync(HttpMethod.Post, $"/api/v1/forms/{formId}/publish");
        await admin.SendOkAsync(HttpMethod.Put, "/api/v1/form-bindings", new { recordType = "Incident", formId });

        using var hidden = await requester.SendAsync(HttpMethod.Post, "/api/v1/incidents", new { title = "Mail down", formAnswers = new { service = server } });
        Assert.Contains("configuration-item-unknown", await hidden.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        using var notLive = await requester.SendAsync(HttpMethod.Post, "/api/v1/incidents", new { title = "Chat down", formAnswers = new { service = planned } });
        Assert.Contains("configuration-item-unknown", await notLive.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);

        var reported = IncidentApi.IdOf(await requester.CreateAsync(new { title = "Mail down", formAnswers = new { service = email } }));
        var linked = await agent.ArrayAsync($"/api/v1/incidents/{reported}/configuration-items");
        Assert.Equal("Email", (string)linked.Single()!["item"]!["name"]!);
        var form = await agent.SendOkAsync(HttpMethod.Get, $"/api/v1/incidents/{reported}/form");
        Assert.Contains("Email", form["references"]!["configurationItems"]!.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Items_of_one_tenant_are_not_seen_by_another()
    {
        var a = await database.CreateTenantAsync("cma");
        var b = await database.CreateTenantAsync("cmb");
        using var adminA = await _factory.ForTenant(a).SignedInAsync(database, a, BuiltInRoles.TenantAdmin);
        using var adminB = await _factory.ForTenant(b).SignedInAsync(database, b, BuiltInRoles.TenantAdmin);
        var classes = await adminA.StandardClassesAsync();
        var item = await adminA.ItemAsync(classes["server"], "srv-a");

        Assert.Equal(HttpStatusCode.NotFound, (await adminB.SendAsync(HttpMethod.Get, $"/api/v1/cmdb/items/{item}")).StatusCode);
        Assert.Equal(0, (int)(await adminB.SendOkAsync(HttpMethod.Get, "/api/v1/cmdb/items"))["totalCount"]!);
        Assert.Empty(await adminB.ArrayAsync("/api/v1/cmdb/classes"));
        var typeB = (await adminB.TypesAsync())["runs-on"];
        var itemB = await adminB.ItemAsync((await adminB.StandardClassesAsync())["server"], "srv-b");
        Assert.Equal(HttpStatusCode.BadRequest, (await adminB.SendAsync(HttpMethod.Post, $"/api/v1/cmdb/items/{itemB}/relationships", new { typeId = typeB, targetId = item })).StatusCode);

        // Numbers count per tenant.
        await using var db = TestData.AppContext(database, b);
        Assert.Equal("CI-000001", (await db.ConfigurationItems.SingleAsync(Ct)).Number);
    }
}
