using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Zelvo.Data.Identity;
using Zelvo.Data.Tenancy;

namespace Zelvo.Api.Tests;

/// <summary>Service request fulfilment (task C4, module 2) against PostgreSQL: initial group and overrides, service SLAs, tasks, the journey, notifications and the MCP catalog tools.</summary>
public sealed class FulfilmentApiTests(DatabaseFixture database) : IAsyncDisposable
{
    private readonly ApiFactory _factory = ApiFactory.ForDatabase(database).WithSetting("AttachmentScanning:Enabled", "false");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    private static JsonObject Text(string en) => new() { ["en"] = en, ["ar"] = en + " (ar)" };

    private static async Task<Guid> ServiceAsync(HttpClient author, HttpClient reviewer, string code, JsonObject card)
    {
        var id = (await author.SendOkAsync(HttpMethod.Post, "/api/v1/catalog/services", new { code }, HttpStatusCode.Created)).Id();
        var draft = await author.SendOkAsync(HttpMethod.Put, $"/api/v1/catalog/services/{id}/draft", new { card });
        Assert.Empty(draft["problems"]!.AsArray());
        await author.SendOkAsync(HttpMethod.Post, $"/api/v1/catalog/services/{id}/submit");
        await reviewer.SendOkAsync(HttpMethod.Post, $"/api/v1/catalog/services/{id}/approve");
        return id;
    }

    private static async Task<Guid> GroupAsync(HttpClient admin, string name, params TestUser[] members)
    {
        var id = (await admin.SendOkAsync(HttpMethod.Post, "/api/v1/groups", new { name = Text(name) }, HttpStatusCode.Created)).Id();
        foreach (var member in members)
        {
            await admin.SendOkAsync(HttpMethod.Put, $"/api/v1/groups/{id}/members/{member.Id}", new { isLead = false });
        }

        return id;
    }

    private static JsonObject Clock(JsonObject request, string metric) =>
        request["sla"]!.AsArray().Select(c => c!.AsObject()).Single(c => (string?)c["metric"] == metric);

    [Fact]
    public async Task Requests_go_to_the_services_group_or_the_one_their_answers_choose_with_the_services_own_targets()
    {
        var tenant = await database.CreateTenantAsync("fg");
        using var admin = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.TenantAdmin);
        using var publisher = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.TenantAdmin);
        using var requester = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.Requester);
        var desk = await GroupAsync(admin, "Dubai desk");
        var abuDhabi = await GroupAsync(admin, "Abu Dhabi desk");
        var formId = (await admin.SendOkAsync(HttpMethod.Post, "/api/v1/forms", new
        {
            key = "office", name = new { en = "Office" },
            schema = new { elements = new[] { new { type = "Field", field = new { key = "city", type = "Text", label = new { en = "City", ar = "المدينة" }, required = true } } } },
        }, HttpStatusCode.Created)).Id();
        await admin.SendOkAsync(HttpMethod.Post, $"/api/v1/forms/{formId}/publish");

        // Problems: an override without a group and a step naming a task that does not exist.
        var badId = (await admin.SendOkAsync(HttpMethod.Post, "/api/v1/catalog/services", new { code = "BAD-1" }, HttpStatusCode.Created)).Id();
        var bad = await admin.SendOkAsync(HttpMethod.Put, $"/api/v1/catalog/services/{badId}/draft", new
        {
            card = new JsonObject
            {
                ["name"] = Text("Bad"), ["channels"] = new JsonArray("Portal"),
                ["steps"] = new JsonArray(new JsonObject { ["name"] = Text("Prepare"), ["stage"] = "InProgress", ["taskKey"] = "nope" }),
                ["fulfilment"] = new JsonObject { ["overrides"] = new JsonArray(new JsonObject { ["conditions"] = new JsonArray() }) },
            },
        });
        var codes = bad["problems"]!.AsArray().Select(p => (string)p!["code"]!).ToList();
        Assert.Contains("group-required", codes);
        Assert.Contains("conditions-required", codes);
        Assert.Contains("task-unknown", codes);

        var service = await ServiceAsync(admin, publisher, "FAC-DESK", new JsonObject
        {
            ["name"] = Text("Desk move"), ["channels"] = new JsonArray("Portal"), ["formId"] = formId.ToString(), ["days"] = 2,
            ["fulfilment"] = new JsonObject
            {
                ["groupId"] = desk.ToString(),
                ["ownershipMinutes"] = 60,
                ["overrides"] = new JsonArray(new JsonObject
                {
                    ["groupId"] = abuDhabi.ToString(),
                    ["conditions"] = new JsonArray(new JsonObject { ["field"] = "answer.city", ["op"] = "eq", ["value"] = "Abu Dhabi" }),
                }),
                ["escalations"] = new JsonArray(new JsonObject { ["percent"] = 50, ["notifyGroupLeads"] = true }),
            },
        });
        var versionId = (string?)(await requester.SendOkAsync(HttpMethod.Get, $"/api/v1/catalog/published/{service}/request-form"))["form"]!["versionId"];

        var dubai = (await requester.SendOkAsync(HttpMethod.Post, "/api/v1/service-requests",
            new { serviceId = service, formVersionId = versionId, formAnswers = new { city = "Dubai" } }, HttpStatusCode.Created)).Id();
        var abu = (await requester.SendOkAsync(HttpMethod.Post, "/api/v1/service-requests",
            new { serviceId = service, formVersionId = versionId, formAnswers = new { city = "abu dhabi" } }, HttpStatusCode.Created)).Id();
        var dubaiView = await admin.SendOkAsync(HttpMethod.Get, $"/api/v1/service-requests/{dubai}");
        var abuView = await admin.SendOkAsync(HttpMethod.Get, $"/api/v1/service-requests/{abu}");
        Assert.Equal(desk, Guid.Parse((string)dubaiView["group"]!["id"]!));
        Assert.Equal(abuDhabi, Guid.Parse((string)abuView["group"]!["id"]!));

        // Ownership: the service's 60 minutes. Completion: to the end of the second working day
        // after submission (no calendar: around the clock, days end at midnight UTC).
        Assert.Equal(60, (int?)Clock(dubaiView, "Response")["targetMinutes"]);
        var created = DateTimeOffset.Parse((string)dubaiView["createdAt"]!, System.Globalization.CultureInfo.InvariantCulture);
        var due = new DateTimeOffset(created.UtcDateTime.Date.AddDays(3), TimeSpan.Zero);
        Assert.Equal((int)Math.Ceiling((due - created).TotalSeconds / 60), (int?)Clock(dubaiView, "Resolution")["targetMinutes"]);
        Assert.Null(Clock(dubaiView, "Resolution")["policy"]);

        await using var db = TestData.AppContext(database, tenant);
        var details = await db.ServiceRequests.AsNoTracking().SingleAsync(r => r.TicketId == dubai, Ct);
        Assert.Equal(50, (int?)JsonNode.Parse(details.Escalations)![0]!["percent"]);
        Assert.Equal(1, await db.Notifications.CountAsync(n => n.EventType == "request.received" && n.TicketId == dubai, Ct));
    }

    [Fact]
    public async Task Tasks_run_in_sequence_and_in_parallel_move_the_journey_and_the_last_one_completes_the_request()
    {
        var tenant = await database.CreateTenantAsync("ft");
        using var admin = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.TenantAdmin);
        using var publisher = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.TenantAdmin);
        var requesterUser = await database.CreateUserAsync(tenant, BuiltInRoles.Requester);
        var itUser = await database.CreateUserAsync(tenant, BuiltInRoles.Agent);
        var facilitiesUser = await database.CreateUserAsync(tenant, BuiltInRoles.Agent);
        using var requester = _factory.ForTenant(tenant).As(requesterUser);
        using var it = _factory.ForTenant(tenant).As(itUser);
        using var facilities = _factory.ForTenant(tenant).As(facilitiesUser);
        var itGroup = await GroupAsync(admin, "IT", itUser);
        var facilitiesGroup = await GroupAsync(admin, "Facilities", facilitiesUser);

        var service = await ServiceAsync(admin, publisher, "HR-JOINER", new JsonObject
        {
            ["name"] = Text("New joiner"), ["channels"] = new JsonArray("Portal"),
            ["steps"] = new JsonArray(
                new JsonObject { ["name"] = Text("Received"), ["stage"] = "Submitted" },
                new JsonObject { ["name"] = Text("Preparing"), ["stage"] = "InProgress", ["taskKey"] = "laptop" },
                new JsonObject { ["name"] = Text("Handing over"), ["stage"] = "InProgress", ["taskKey"] = "handover" },
                new JsonObject { ["name"] = Text("Done"), ["stage"] = "Resolved" }),
            ["fulfilment"] = new JsonObject
            {
                ["groupId"] = itGroup.ToString(),
                ["tasks"] = new JsonArray(
                    new JsonObject { ["key"] = "laptop", ["name"] = Text("Prepare the laptop"), ["groupId"] = itGroup.ToString(), ["targetMinutes"] = 240 },
                    new JsonObject { ["key"] = "desk", ["name"] = Text("Set up the desk"), ["groupId"] = facilitiesGroup.ToString(), ["parallelWithPrevious"] = true },
                    new JsonObject { ["key"] = "handover", ["name"] = Text("Hand over"), ["groupId"] = itGroup.ToString() }),
            },
        });

        var id = (await requester.SendOkAsync(HttpMethod.Post, "/api/v1/service-requests", new { serviceId = service }, HttpStatusCode.Created)).Id();
        async Task<JsonArray> TasksAsync() => await admin.ArrayAsync($"/api/v1/service-requests/{id}/tasks");
        var tasks = await TasksAsync();
        Assert.Equal(["Open", "Open", "Waiting"], tasks.Select(t => (string?)t!["state"]));
        Assert.Equal([0, 0, 1], tasks.Select(t => (int)t!["stage"]!));
        Assert.NotNull(tasks[0]!["dueAt"]);
        Assert.Equal(1, (int?)(await requester.SendOkAsync(HttpMethod.Get, $"/api/v1/service-requests/{id}"))["currentStep"]);

        // Each group sees its own; others cannot work on them.
        var mine = await facilities.SendOkAsync(HttpMethod.Get, "/api/v1/request-tasks?group=mine");
        var deskTask = Guid.Parse((string)mine["items"]!.AsArray().Single()!["id"]!);
        var laptopTask = Guid.Parse((string)tasks[0]!["id"]!);
        using (var notMember = await facilities.SendAsync(HttpMethod.Post, $"/api/v1/request-tasks/{laptopTask}/start"))
        {
            Assert.Equal(HttpStatusCode.Forbidden, notMember.StatusCode);
        }

        // Starting a task starts work on the request (ownership clock).
        await it.SendOkAsync(HttpMethod.Post, $"/api/v1/request-tasks/{laptopTask}/start");
        var started = await admin.SendOkAsync(HttpMethod.Get, $"/api/v1/service-requests/{id}");
        Assert.Equal(("InProgress", itUser.Id), ((string?)started["state"], Guid.Parse((string)started["assignee"]!["id"]!)));
        Assert.Equal("Stopped", (string?)Clock(started, "Response")["state"]);

        await it.SendOkAsync(HttpMethod.Post, $"/api/v1/request-tasks/{laptopTask}/complete", new { notes = "Laptop imaged." });
        Assert.Equal(["Done", "Open", "Waiting"], (await TasksAsync()).Select(t => (string?)t!["state"]));
        await facilities.SendOkAsync(HttpMethod.Post, $"/api/v1/request-tasks/{deskTask}/complete");
        tasks = await TasksAsync();
        Assert.Equal(["Done", "Done", "Open"], tasks.Select(t => (string?)t!["state"]));
        Assert.Equal(2, (int?)(await requester.SendOkAsync(HttpMethod.Get, $"/api/v1/service-requests/{id}"))["currentStep"]);

        await it.SendOkAsync(HttpMethod.Post, $"/api/v1/request-tasks/{(string)tasks[2]!["id"]!}/complete");
        var done = await requester.SendOkAsync(HttpMethod.Get, $"/api/v1/service-requests/{id}");
        Assert.Equal(("Resolved", 3), ((string?)done["state"], (int?)done["currentStep"]));
        Assert.Contains("Prepare the laptop", (string?)done["resolutionNotes"], StringComparison.Ordinal);

        await using var db = TestData.AppContext(database, tenant);
        Assert.True(await db.Notifications.AnyAsync(n => n.EventType == "task.queued" && n.RecipientId == facilitiesUser.Id, Ct));
        Assert.Equal(1, await db.Notifications.CountAsync(n => n.EventType == "request.resolved" && n.RecipientId == requesterUser.Id, Ct));
    }

    [Fact]
    public async Task Resolving_by_hand_cancels_the_tasks_still_to_do()
    {
        var tenant = await database.CreateTenantAsync("fc");
        using var admin = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.TenantAdmin);
        using var publisher = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.TenantAdmin);
        var agentUser = await database.CreateUserAsync(tenant, BuiltInRoles.Agent);
        using var agent = _factory.ForTenant(tenant).As(agentUser);
        using var requester = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.Requester);
        var group = await GroupAsync(admin, "IT", agentUser);
        var service = await ServiceAsync(admin, publisher, "IT-TWO", new JsonObject
        {
            ["name"] = Text("Two steps"), ["channels"] = new JsonArray("Portal"),
            ["fulfilment"] = new JsonObject
            {
                ["groupId"] = group.ToString(), ["method"] = "RoundRobin",
                ["tasks"] = new JsonArray(new JsonObject { ["key"] = "one", ["name"] = Text("One") }, new JsonObject { ["key"] = "two", ["name"] = Text("Two") }),
            },
        });
        var id = (await requester.SendOkAsync(HttpMethod.Post, "/api/v1/service-requests", new { serviceId = service }, HttpStatusCode.Created)).Id();
        var view = await agent.SendOkAsync(HttpMethod.Get, $"/api/v1/service-requests/{id}");
        Assert.Equal(agentUser.Id, Guid.Parse((string)view["assignee"]!["id"]!));
        await agent.SendOkAsync(HttpMethod.Post, $"/api/v1/service-requests/{id}/transitions", new { state = "Resolved", resolutionNotes = "Done another way." });
        Assert.All(await admin.ArrayAsync($"/api/v1/service-requests/{id}/tasks"), t => Assert.Equal("Cancelled", (string?)t!["state"]));
    }

    [Fact]
    public async Task An_ai_agent_finds_reads_and_requests_a_service_for_a_person_who_may_see_it()
    {
        var tenant = await database.CreateTenantAsync("fm");
        using var admin = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.TenantAdmin);
        using var publisher = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.TenantAdmin);
        var person = await database.CreateUserAsync(tenant, BuiltInRoles.Requester);
        var open = await ServiceAsync(admin, publisher, "IT-MOUSE", new JsonObject { ["name"] = Text("Mouse"), ["channels"] = new JsonArray("Portal"), ["keywords"] = new JsonArray("mouse") });
        await ServiceAsync(admin, publisher, "FIN-SECRET", new JsonObject
        {
            ["name"] = Text("Secret mouse"), ["channels"] = new JsonArray("Portal"), ["keywords"] = new JsonArray("mouse"),
            ["access"] = new JsonObject { ["mode"] = "Restricted", ["roleKeys"] = new JsonArray(BuiltInRoles.TenantAdmin) },
        });

        using (var on = await admin.PutAsJsonAsync(new Uri("/api/v1/ai/mcp/settings", UriKind.Relative), new { enabled = true }, Ct))
        {
            Assert.Equal(HttpStatusCode.OK, on.StatusCode);
        }

        var (key, clientId) = await admin.CreateApiKeyAsync("agent", "requests.read", "requests.edit");
        using (var set = await admin.PutAsJsonAsync(new Uri($"/api/v1/ai/mcp/agents/{clientId}", UriKind.Relative), new
        {
            tools = new[] { "search_services", "get_service", "request_service", "get_service_request" },
            toolsWithoutApproval = new[] { "request_service" },
        }, Ct))
        {
            Assert.Equal(HttpStatusCode.OK, set.StatusCode);
        }

        await using var client = await ConnectAsync(tenant, key);
        var found = Content(await client.CallToolAsync("search_services", new Dictionary<string, object?> { ["requesterEmail"] = person.Email, ["text"] = "mouse" }, cancellationToken: Ct));
        Assert.Equal(["IT-MOUSE"], found["items"]!.AsArray().Select(i => (string?)i!["code"]));
        var hidden = await client.CallToolAsync("get_service", new Dictionary<string, object?> { ["service"] = "FIN-SECRET", ["requesterEmail"] = person.Email }, cancellationToken: Ct);
        Assert.True(hidden.IsError);

        var card = Content(await client.CallToolAsync("get_service", new Dictionary<string, object?> { ["service"] = "it-mouse", ["requesterEmail"] = person.Email }, cancellationToken: Ct));
        Assert.Equal(open.ToString(), (string?)card["id"]);
        var made = Content(await client.CallToolAsync("request_service", new Dictionary<string, object?> { ["service"] = "IT-MOUSE", ["requesterEmail"] = person.Email, ["description"] = "Left-handed." }, cancellationToken: Ct));
        var number = (string?)made["number"];
        Assert.StartsWith("REQ-", number, StringComparison.Ordinal);
        var status = Content(await client.CallToolAsync("get_service_request", new Dictionary<string, object?> { ["request"] = number }, cancellationToken: Ct));
        Assert.Equal(("AiAgent", person.Id.ToString()), ((string?)status["channel"], (string?)status["requester"]!["id"]));
    }

    private async Task<McpClient> ConnectAsync(Tenant tenant, string key)
    {
        var http = _factory.CreateClient();
        http.DefaultRequestHeaders.Add("X-Zelvo-Tenant", tenant.Slug);
        http.DefaultRequestHeaders.Add("X-Api-Key", key);
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions { Endpoint = new Uri(http.BaseAddress!, "/api/v1/mcp"), TransportMode = HttpTransportMode.StreamableHttp },
            http, NullLoggerFactory.Instance, ownsHttpClient: true);
        return await McpClient.CreateAsync(transport, cancellationToken: Ct);
    }

    private static JsonObject Content(CallToolResult result) =>
        JsonNode.Parse(((TextContentBlock)result.Content.Single()).Text)!.AsObject();
}
