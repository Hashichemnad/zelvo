using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Zelvo.Data.Identity;

namespace Zelvo.Api.Tests;

/// <summary>The low-code app builder (task K3, module 20): data lists, apps, export and approved imports, against PostgreSQL.</summary>
public sealed class AppBuilderApiTests(DatabaseFixture database) : IAsyncDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly ApiFactory _factory = ApiFactory.ForDatabase(database);

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    private static object RoomsList(string key = "rooms") => new
    {
        key,
        name = new { en = "Meeting rooms", ar = "قاعات الاجتماعات" },
        columns = new[] { new { key = "seats", label = new { en = "Seats", ar = "المقاعد" } } },
        items = new object[]
        {
            new { key = "r1", label = new { en = "Room 1", ar = "القاعة ١" }, values = new Dictionary<string, string> { ["seats"] = "8" } },
            new { key = "r2", label = new { en = "Room 2", ar = "القاعة ٢" }, active = false },
        },
    };

    private static object BookingType(string key = "booking", string prefix = "BKG") => new
    {
        key,
        name = new { en = "Room booking", ar = "حجز قاعة" },
        numberPrefix = prefix,
        portalEntry = true,
        states = new object[]
        {
            new { key = "new", name = new { en = "New", ar = "جديد" }, category = "New" },
            new { key = "booked", name = new { en = "Booked", ar = "محجوز" }, category = "InProgress" },
            new { key = "done", name = new { en = "Done", ar = "منجز" }, category = "Resolved" },
            new { key = "closed", name = new { en = "Closed", ar = "مغلق" }, category = "Closed" },
        },
    };

    private static async Task<Guid> BoundFormAsync(HttpClient admin, Guid typeId, string key, string list = "rooms")
    {
        var field = new { key = "room", type = "Dropdown", label = new { en = "Room", ar = "القاعة" }, required = true, dataList = list };
        var id = (await admin.SendOkAsync(HttpMethod.Post, "/api/v1/forms",
            new { key, name = new { en = "Booking", ar = "حجز" }, schema = new { elements = new[] { new { type = "Field", field } } } }, HttpStatusCode.Created)).Id();
        await admin.SendOkAsync(HttpMethod.Post, $"/api/v1/forms/{id}/publish");
        await admin.SendOkAsync(HttpMethod.Put, "/api/v1/form-bindings", new { recordType = "Custom", customRecordTypeId = typeId, formId = id });
        return id;
    }

    [Fact]
    public async Task Data_lists_give_dropdown_fields_their_options_and_answers_are_checked_against_them()
    {
        var tenant = await database.CreateTenantAsync("dl");
        using var admin = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.TenantAdmin);
        using var agent = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.Agent);
        using var requester = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.Requester);

        Assert.Equal(HttpStatusCode.Forbidden, (await agent.SendAsync(HttpMethod.Post, "/api/v1/data-lists", RoomsList())).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await agent.SendAsync(HttpMethod.Get, "/api/v1/data-lists")).StatusCode);
        using var broken = await admin.SendAsync(HttpMethod.Post, "/api/v1/data-lists", new { key = "bad", name = new { en = "Bad" }, items = new[] { new { key = "x", label = new { en = "X" } }, new { key = "x", label = new { en = "Y" } } } });
        Assert.Equal(HttpStatusCode.BadRequest, broken.StatusCode);
        var list = await admin.SendOkAsync(HttpMethod.Post, "/api/v1/data-lists", RoomsList(), HttpStatusCode.Created);
        Assert.Equal(2, list["items"]!.AsArray().Count);
        Assert.Equal("key-taken", (await (await admin.SendAsync(HttpMethod.Post, "/api/v1/data-lists", RoomsList())).ProblemAsync()).Code);

        var type = await admin.SendOkAsync(HttpMethod.Post, "/api/v1/record-types", BookingType(), HttpStatusCode.Created);

        // A form naming a list that does not exist cannot be published.
        var orphan = (await admin.SendOkAsync(HttpMethod.Post, "/api/v1/forms", new
        {
            key = "orphan", name = new { en = "Orphan" },
            schema = new { elements = new[] { new { type = "Field", field = new { key = "room", type = "Dropdown", label = new { en = "Room" }, dataList = "nowhere" } } } },
        }, HttpStatusCode.Created)).Id();
        Assert.NotEqual(HttpStatusCode.OK, (await admin.SendAsync(HttpMethod.Post, $"/api/v1/forms/{orphan}/publish")).StatusCode);

        await BoundFormAsync(admin, type.Id(), "booking");
        var resolved = await requester.SendOkAsync(HttpMethod.Get, $"/api/v1/forms/resolve?recordType=Custom&customRecordTypeId={type.Id()}");
        var options = resolved["fields"]!.AsArray()[0]!["field"]!["options"]!.AsArray();
        Assert.Equal("r1", (string)Assert.Single(options)!["value"]!);
        var versionId = (string)resolved["form"]!["versionId"]!;

        // Inactive items are not offered; an active one is accepted.
        using var refused = await requester.SendAsync(HttpMethod.Post, "/api/v1/records/booking", new { title = "Board meeting", formVersionId = versionId, formAnswers = new { room = "r2" } });
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        await requester.SendOkAsync(HttpMethod.Post, "/api/v1/records/booking", new { title = "Board meeting", formVersionId = versionId, formAnswers = new { room = "r1" } }, HttpStatusCode.Created);

        // Items left out of an update are no longer offered (kept for answers that chose them).
        var updated = await admin.SendOkAsync(HttpMethod.Put, "/api/v1/data-lists/rooms", new
        {
            name = new { en = "Meeting rooms", ar = "قاعات الاجتماعات" },
            items = new[] { new { key = "r3", label = new { en = "Room 3", ar = "القاعة ٣" } } },
        });
        Assert.Contains(updated["items"]!.AsArray(), i => (string)i!["key"]! == "r1" && !(bool)i["active"]!);
        var again = await requester.SendOkAsync(HttpMethod.Get, $"/api/v1/forms/resolve?recordType=Custom&customRecordTypeId={type.Id()}");
        Assert.Equal("r3", (string)Assert.Single(again["fields"]!.AsArray()[0]!["field"]!["options"]!.AsArray())!["value"]!);
    }

    /// <summary>Builds the booking app in a tenant: data list, record type, form, a rule assigning to a group, a role, entries.</summary>
    private async Task<(HttpClient Admin, string Package)> BuildAndExportAsync(Data.Tenancy.Tenant tenant, string groupName)
    {
        var admin = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.TenantAdmin);
        await admin.SendOkAsync(HttpMethod.Post, "/api/v1/data-lists", RoomsList(), HttpStatusCode.Created);
        var type = await admin.SendOkAsync(HttpMethod.Post, "/api/v1/record-types", BookingType(), HttpStatusCode.Created);
        await BoundFormAsync(admin, type.Id(), "booking");
        var group = await admin.GroupAsync(groupName);
        await admin.SendOkAsync(HttpMethod.Post, "/api/v1/workflow-rules", new
        {
            name = new { en = "Bookings to the front desk", ar = "الحجوزات إلى الاستقبال" },
            trigger = "TicketCreated", recordType = "Custom", customRecordTypeId = type.Id(),
            actions = new object[] { new { type = "assign", groupId = group, method = "GroupQueue" } },
        }, HttpStatusCode.Created);
        await admin.SendOkAsync(HttpMethod.Post, "/api/v1/roles", new { key = "booking-desk", name = new { en = "Booking desk", ar = "مكتب الحجوزات" } }, HttpStatusCode.Created);
        await admin.SendOkAsync(HttpMethod.Put, "/api/v1/roles/booking-desk/permissions", new { permissions = new[] { "records.booking.read", "records.booking.edit" } });

        // Only administrators build apps.
        using var agent = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.Agent);
        Assert.Equal(HttpStatusCode.Forbidden, (await agent.SendAsync(HttpMethod.Post, "/api/v1/apps", new { key = "rooms", name = new { en = "Rooms" } })).StatusCode);
        await admin.SendOkAsync(HttpMethod.Post, "/api/v1/apps", new { key = "rooms", name = new { en = "Rooms", ar = "القاعات" }, icon = "calendar" }, HttpStatusCode.Created);
        using var badEntry = await admin.SendAsync(HttpMethod.Patch, "/api/v1/apps/rooms", new
        {
            recordTypes = new[] { "booking" },
            definition = new { listViews = new[] { new { key = "open", name = new { en = "Open" }, recordType = "booking", state = "nowhere" } } },
        });
        Assert.Equal(HttpStatusCode.BadRequest, badEntry.StatusCode);
        var app = await admin.SendOkAsync(HttpMethod.Patch, "/api/v1/apps/rooms", new
        {
            recordTypes = new[] { "booking" }, dataLists = new[] { "rooms" }, roles = new[] { "booking-desk" },
            definition = new
            {
                listViews = new[] { new { key = "open", name = new { en = "Open bookings", ar = "الحجوزات المفتوحة" }, recordType = "booking", open = true } },
                menuEntries = new object[] { new { key = "open", label = new { en = "Open bookings", ar = "الحجوزات المفتوحة" }, target = "records", recordType = "booking", view = "open" } },
                portalEntries = new[] { new { key = "book", label = new { en = "Book a room", ar = "احجز قاعة" }, description = new { en = "Meeting rooms" }, recordType = "booking" } },
            },
        });
        Assert.Single(app["members"]!["forms"]!.AsArray());
        Assert.Single(app["members"]!["rules"]!.AsArray());

        using var export = await admin.SendAsync(HttpMethod.Get, "/api/v1/apps/rooms/export");
        Assert.Equal(HttpStatusCode.OK, export.StatusCode);
        return (admin, await export.Content.ReadAsStringAsync(Ct));
    }

    private static async Task<JsonObject> ImportAsync(HttpClient admin, string package)
    {
        using var content = new StringContent(package, Encoding.UTF8, "application/json");
        using var response = await admin.PostAsync("/api/v1/app-imports?fileName=rooms.zelvo-app.json", content, Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.StatusCode == HttpStatusCode.Created, text);
        return JsonNode.Parse(text)!.AsObject();
    }

    [Fact]
    public async Task An_exported_app_is_checked_previewed_and_installed_in_another_tenant_only_after_approval()
    {
        var source = await database.CreateTenantAsync("app-src");
        var (sourceAdmin, json) = await BuildAndExportAsync(source, "Front desk");
        sourceAdmin.Dispose();
        var package = JsonNode.Parse(json)!.AsObject();
        Assert.Equal("zelvo-app/1", (string)package["format"]!);
        Assert.Contains(package["references"]!.AsArray(), r => (string)r!["kind"]! == "group" && (string)r["name"]!["en"]! == "Front desk");
        Assert.Null(package["forms"]![0]!["fields"]![0]!["options"]);

        var target = await database.CreateTenantAsync("app-dst");
        using var admin = await _factory.ForTenant(target).SignedInAsync(database, target, BuiltInRoles.TenantAdmin);
        using var requester = await _factory.ForTenant(target).SignedInAsync(database, target, BuiltInRoles.Requester);

        // Not a package: refused without being kept.
        using var junk = new StringContent("{\"format\":\"other\"}", Encoding.UTF8, "application/json");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await admin.PostAsync("/api/v1/app-imports", junk, Ct)).StatusCode);

        // The group the rule assigns to is missing here: the preview says so and approval is refused.
        var import = await ImportAsync(admin, json);
        var plan = import["plan"]!;
        Assert.False((bool)plan["valid"]!);
        Assert.Contains(plan["problems"]!.AsArray(), p => (string)p!["code"]! == "reference-not-found");
        Assert.Contains("records.booking.read", plan["permissionsCreated"]!.AsArray().Select(p => (string)p!));
        Assert.Contains(plan["grants"]!.AsArray(), g => (string)g!["role"]! == "booking-desk" && !(bool)g["roleExists"]!);
        Assert.Empty(plan["conflicts"]!.AsArray());
        var id = (string)import["id"]!;
        Assert.Equal("package-invalid", (await (await admin.SendAsync(HttpMethod.Post, $"/api/v1/app-imports/{id}/approve", new { })).ProblemAsync()).Code);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.SendAsync(HttpMethod.Get, "/api/v1/record-types/booking")).StatusCode);

        // Once the group exists here the same package is valid; approving installs and activates it.
        var group = await admin.GroupAsync("Front desk");
        var checkedAgain = await admin.SendOkAsync(HttpMethod.Get, $"/api/v1/app-imports/{id}");
        Assert.True((bool)checkedAgain["plan"]!["valid"]!, checkedAgain["plan"]!["problems"]!.ToJsonString());
        var approved = await admin.SendOkAsync(HttpMethod.Post, $"/api/v1/app-imports/{id}/approve", new { });
        Assert.Equal("Approved", (string)approved["status"]!);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.SendAsync(HttpMethod.Post, $"/api/v1/app-imports/{id}/approve", new { })).StatusCode);

        var app = await admin.SendOkAsync(HttpMethod.Get, "/api/v1/apps/rooms");
        Assert.True((bool)app["active"]!);
        Assert.Equal("booking", (string)Assert.Single(app["members"]!["recordTypes"]!.AsArray())!["key"]!);
        var rules = await admin.ArrayAsync("/api/v1/workflow-rules");
        var rule = Assert.Single(rules, r => (string)r!["name"]!["en"]! == "Bookings to the front desk");
        Assert.Equal(group.ToString(), (string)rule!["actions"]![0]!["groupId"]!);
        var role = await admin.SendOkAsync(HttpMethod.Get, "/api/v1/roles/booking-desk");
        Assert.Contains("records.booking.edit", role["permissions"]!.AsArray().Select(p => (string)p!));

        // The requester sees the portal entry and the form takes the imported list's items.
        var apps = await requester.ArrayAsync("/api/v1/apps");
        Assert.Equal("book", (string)Assert.Single(Assert.Single(apps)!["portalEntries"]!.AsArray())!["key"]!);
        var type = await admin.SendOkAsync(HttpMethod.Get, "/api/v1/record-types/booking");
        var resolved = await requester.SendOkAsync(HttpMethod.Get, $"/api/v1/forms/resolve?recordType=Custom&customRecordTypeId={type.Id()}");
        Assert.Equal("r1", (string)Assert.Single(resolved["fields"]!.AsArray()[0]!["field"]!["options"]!.AsArray())!["value"]!);
        var record = await requester.SendOkAsync(HttpMethod.Post, "/api/v1/records/booking",
            new { title = "Workshop", formVersionId = (string)resolved["form"]!["versionId"]!, formAnswers = new { room = "r1" } }, HttpStatusCode.Created);
        Assert.Equal("BKG-000001", (string)record["number"]!);

        // Importing it again: everything exists, so nothing is changed without a choice.
        var second = await ImportAsync(admin, json);
        var conflicts = second["plan"]!["conflicts"]!.AsArray().Select(c => (string)c!["id"]!).ToList();
        Assert.Contains("app:rooms", conflicts);
        Assert.Contains("recordType:booking", conflicts);
        Assert.Contains("dataList:rooms", conflicts);
        Assert.Contains("role:booking-desk", conflicts);
        var secondId = (string)second["id"]!;
        Assert.Equal("choices-required", (await (await admin.SendAsync(HttpMethod.Post, $"/api/v1/app-imports/{secondId}/approve", new { })).ProblemAsync()).Code);
        var keepAll = conflicts.ToDictionary(c => c, _ => "keep");
        await admin.SendOkAsync(HttpMethod.Post, $"/api/v1/app-imports/{secondId}/approve", new { choices = keepAll });
        Assert.Single(await admin.ArrayAsync("/api/v1/workflow-rules"), r => (string)r!["name"]!["en"]! == "Bookings to the front desk");

        // Replacing the record type switches its rules off and adds the package's.
        var third = await ImportAsync(admin, json);
        var replaceType = keepAll.ToDictionary(c => c.Key, c => c.Key == "recordType:booking" ? "replace" : "keep");
        await admin.SendOkAsync(HttpMethod.Post, $"/api/v1/app-imports/{(string)third["id"]!}/approve", new { choices = replaceType });
        var after = (await admin.ArrayAsync("/api/v1/workflow-rules")).Where(r => (string)r!["name"]!["en"]! == "Bookings to the front desk").ToList();
        Assert.Equal(2, after.Count);
        Assert.Single(after, r => (bool)r!["active"]!);

        // A discarded package changes nothing.
        var fourth = await ImportAsync(admin, json);
        Assert.Equal("Discarded", (string)(await admin.SendOkAsync(HttpMethod.Post, $"/api/v1/app-imports/{(string)fourth["id"]!}/discard"))["status"]!);
        Assert.Equal(4, (await admin.ArrayAsync("/api/v1/app-imports")).Count);
    }

    [Fact]
    public async Task A_package_role_may_hold_only_its_own_record_types_permissions_and_requesters_get_none()
    {
        var source = await database.CreateTenantAsync("app-perm");
        var (sourceAdmin, json) = await BuildAndExportAsync(source, "Desk");
        sourceAdmin.Dispose();
        var package = JsonNode.Parse(json)!.AsObject();
        package["roles"]![0]!["permissions"]!.AsArray().Add("users.manage");
        package["recordTypes"]![0]!["grantRoles"]!.AsArray().Add("requester");
        package["references"] = new JsonArray();
        package["rules"] = new JsonArray();

        var target = await database.CreateTenantAsync("app-perm2");
        using var admin = await _factory.ForTenant(target).SignedInAsync(database, target, BuiltInRoles.TenantAdmin);
        var import = await ImportAsync(admin, package.ToJsonString());
        var codes = import["plan"]!["problems"]!.AsArray().Select(p => (string)p!["code"]!).ToList();
        Assert.Contains("permission-not-allowed", codes);
        Assert.Contains("grant-requester", codes);
    }

    [Fact]
    public async Task Switching_an_app_off_stops_its_record_types_and_rules_and_switching_it_on_restores_them()
    {
        var tenant = await database.CreateTenantAsync("app-off");
        var (admin, _) = await BuildAndExportAsync(tenant, "Desk");
        using var _admin = admin;
        using var requester = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.Requester);
        Assert.Single(await requester.ArrayAsync("/api/v1/apps"));

        var off = await admin.SendOkAsync(HttpMethod.Post, "/api/v1/apps/rooms/deactivate");
        Assert.False((bool)off["active"]!);
        Assert.False((bool)(await admin.SendOkAsync(HttpMethod.Get, "/api/v1/record-types/booking"))["active"]!);
        Assert.All((await admin.ArrayAsync("/api/v1/workflow-rules")).Where(r => (string)r!["name"]!["en"]! == "Bookings to the front desk"), r => Assert.False((bool)r!["active"]!));
        Assert.Empty(await requester.ArrayAsync("/api/v1/apps"));

        await admin.SendOkAsync(HttpMethod.Post, "/api/v1/apps/rooms/activate");
        Assert.True((bool)(await admin.SendOkAsync(HttpMethod.Get, "/api/v1/record-types/booking"))["active"]!);
        Assert.Single(await requester.ArrayAsync("/api/v1/apps"));
    }

    [Fact]
    public void The_demo_hr_app_package_is_well_formed()
    {
        var package = Demo.DemoAppBuilderSeeder.HrPackage();
        Assert.Equal("hr", package.App!.Key);
        Assert.Equal("leave-types", Assert.Single(package.DataLists!).Key);
        Assert.All(package.Roles!.SelectMany(r => r.Permissions!), p => Assert.StartsWith("records.leave.", p, StringComparison.Ordinal));
    }
}
