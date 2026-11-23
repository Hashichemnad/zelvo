using System.Net;
using System.Text.Json.Nodes;
using Zelvo.Data.Identity;

namespace Zelvo.Api.Tests;

/// <summary>Assignment rules, round-robin and incident groups (task B2; product owner decisions 2026-11-23).</summary>
public sealed class AssignmentApiTests(DatabaseFixture database) : IAsyncDisposable
{
    private readonly ApiFactory _factory = ApiFactory.ForDatabase(database);

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    private static Guid? GroupOf(JsonObject incident) => incident["group"] is JsonObject g ? Guid.Parse((string)g["id"]!) : null;

    private static Guid? AssigneeOf(JsonObject incident) => incident["assignee"] is JsonObject a ? Guid.Parse((string)a["id"]!) : null;

    private async Task<TestUser> AgentAsync(Data.Tenancy.Tenant tenant, HttpClient admin, params string[] languages)
    {
        var agent = await database.CreateUserAsync(tenant, BuiltInRoles.Agent);
        if (languages.Length > 0)
        {
            await admin.SendOkAsync(HttpMethod.Patch, $"/api/v1/users/{agent.Id}", new { languages });
        }

        return agent;
    }

    [Fact]
    public async Task A_report_in_a_category_or_below_it_goes_to_the_rule_group_queue_and_others_to_the_default()
    {
        var tenant = await database.CreateTenantAsync("route");
        using var admin = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.TenantAdmin);
        var requester = await database.CreateUserAsync(tenant, BuiltInRoles.Requester);
        using var asRequester = _factory.ForTenant(tenant).As(requester);
        var network = await admin.CategoryAsync("Network");
        var wifi = await admin.CategoryAsync("Wi-Fi", parentId: network);
        var printers = await admin.CategoryAsync("Printers");
        var networkTeam = await admin.GroupAsync("Network team");
        var desk = await admin.GroupAsync("Service desk");
        var rule = await admin.RuleAsync("Network problems", networkTeam, categoryId: network);
        await admin.SendOkAsync(HttpMethod.Put, "/api/v1/assignment-rules/default", new { groupId = desk, method = "GroupQueue" });

        var routed = await asRequester.CreateAsync(new { title = "No Wi-Fi on floor 2", categoryId = wifi });
        Assert.Equal("Wi-Fi", (string)routed["category"]!["name"]!["en"]!);
        Assert.Null(routed["group"]); // Requesters do not see the group.

        var staffView = await admin.GetIncidentAsync(IncidentApi.IdOf(routed));
        Assert.Equal(networkTeam, GroupOf(staffView));
        Assert.Equal(rule, Guid.Parse((string)staffView["routingRule"]!["id"]!));
        Assert.Null(AssigneeOf(staffView));
        Assert.Equal("New", (string)staffView["state"]!);

        var fallback = await admin.GetIncidentAsync(IncidentApi.IdOf(await asRequester.CreateAsync(new { title = "Printer jam", categoryId = printers })));
        Assert.Equal(desk, GroupOf(fallback));
        Assert.Null(fallback["routingRule"]);

        // Inactive categories cannot be chosen.
        await admin.SendOkAsync(HttpMethod.Patch, $"/api/v1/categories/{network}", new { active = false });
        var refused = await asRequester.SendAsync(HttpMethod.Post, "/api/v1/incidents", new { title = "Still no Wi-Fi", categoryId = wifi });
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
    }

    [Fact]
    public async Task Round_robin_takes_turns_among_available_members_with_the_skill_and_language()
    {
        var tenant = await database.CreateTenantAsync("rr");
        using var admin = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.TenantAdmin);
        var arabicRequester = await database.CreateUserAsync(tenant, BuiltInRoles.Requester);
        await database.ExecuteAsOwnerAsync("UPDATE users SET preferred_language = 'ar' WHERE id = @id", ("id", arabicRequester.Id));
        using var asRequester = _factory.ForTenant(tenant).As(arabicRequester);

        var a = await AgentAsync(tenant, admin, "en", "ar");
        var b = await AgentAsync(tenant, admin, "ar");
        var englishOnly = await AgentAsync(tenant, admin, "en");
        var away = await AgentAsync(tenant, admin, "ar");
        var noSkill = await AgentAsync(tenant, admin, "ar");
        var skill = (await admin.SendOkAsync(HttpMethod.Post, "/api/v1/skills", new { name = new { en = "Arabic desk" } }, HttpStatusCode.Created)).Id();
        foreach (var agent in new[] { a, b, englishOnly, away })
        {
            await admin.SendOkAsync(HttpMethod.Put, $"/api/v1/users/{agent.Id}/skills", new { skillIds = new[] { skill } });
        }

        await admin.SendOkAsync(HttpMethod.Put, $"/api/v1/users/{away.Id}/availability", new { available = false });
        var group = await admin.GroupAsync("Arabic desk", a, b, englishOnly, away, noSkill);
        await admin.RuleAsync("Arabic requesters", group, "RoundRobin", language: "ar", skillId: skill);

        var assignees = new List<Guid?>();
        for (var i = 0; i < 4; i++)
        {
            var incident = await admin.GetIncidentAsync(IncidentApi.IdOf(await asRequester.CreateAsync(new { title = $"طلب رقم {i}" })));
            Assert.Equal(group, GroupOf(incident));
            Assert.Equal("Assigned", (string)incident["state"]!);
            assignees.Add(AssigneeOf(incident));
        }

        Assert.All(assignees, x => Assert.Contains(x, new Guid?[] { a.Id, b.Id }));
        Assert.Equal(2, assignees.Count(x => x == a.Id));
        Assert.NotEqual(assignees[0], assignees[1]);
        Assert.Equal(assignees[0], assignees[2]);

        // The preview shows the next person without moving the rotation.
        var preview1 = await admin.SendOkAsync(HttpMethod.Post, "/api/v1/assignment-rules/test", new { language = "ar" });
        var preview2 = await admin.SendOkAsync(HttpMethod.Post, "/api/v1/assignment-rules/test", new { language = "ar" });
        Assert.Equal((string)preview1["assignee"]!["id"]!, (string)preview2["assignee"]!["id"]!);
        Assert.Equal("Arabic requesters", (string)preview1["rule"]!["name"]!["en"]!);

        // Nobody available: the ticket waits in the group's queue.
        await admin.SendOkAsync(HttpMethod.Put, $"/api/v1/users/{a.Id}/availability", new { available = false });
        await admin.SendOkAsync(HttpMethod.Put, $"/api/v1/users/{b.Id}/availability", new { available = false });
        var queued = await admin.GetIncidentAsync(IncidentApi.IdOf(await asRequester.CreateAsync(new { title = "لا أحد متاح" })));
        Assert.Equal(group, GroupOf(queued));
        Assert.Null(AssigneeOf(queued));
        Assert.Equal("New", (string)queued["state"]!);
    }

    [Fact]
    public async Task Concurrent_reports_are_spread_evenly_over_the_members()
    {
        var tenant = await database.CreateTenantAsync("rrpar");
        using var admin = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.TenantAdmin);
        var agents = new[] { await AgentAsync(tenant, admin), await AgentAsync(tenant, admin), await AgentAsync(tenant, admin) };
        var group = await admin.GroupAsync("Desk", agents);
        await admin.SendOkAsync(HttpMethod.Put, "/api/v1/assignment-rules/default", new { groupId = group, method = "RoundRobin" });
        var requesters = new List<HttpClient>();
        for (var i = 0; i < 21; i++)
        {
            requesters.Add(await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.Requester));
        }

        var created = await Task.WhenAll(requesters.Select((c, i) => c.CreateAsync(new { title = $"Concurrent report {i}" })));
        var details = await Task.WhenAll(created.Select(c => admin.GetIncidentAsync(IncidentApi.IdOf(c))));
        var counts = details.GroupBy(AssigneeOf).ToDictionary(g => g.Key!.Value, g => g.Count());
        Assert.Equal(agents.Select(a => a.Id).Order(), counts.Keys.Order());
        Assert.All(counts.Values, n => Assert.Equal(7, n));
        foreach (var client in requesters)
        {
            client.Dispose();
        }
    }

    [Fact]
    public async Task The_assignee_belongs_to_the_group_and_route_again_follows_the_rules()
    {
        var tenant = await database.CreateTenantAsync("asg");
        using var admin = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.TenantAdmin);
        var member = await AgentAsync(tenant, admin);
        var outsider = await AgentAsync(tenant, admin);
        using var asMember = _factory.ForTenant(tenant).As(member);
        var hardware = await admin.CategoryAsync("Hardware");
        var main = await admin.LocationAsync("Main building");
        var floor = await admin.LocationAsync("Floor 2", main);
        var desk = await admin.GroupAsync("Desk", member);
        var field = await admin.GroupAsync("Field team", outsider);

        // Staff log an incident straight into a group: no routing.
        var incident = await admin.CreateAsync(new { title = "Screen flickers", groupId = desk, categoryId = hardware });
        var id = IncidentApi.IdOf(incident);
        Assert.Equal(desk, GroupOf(incident));
        var notMember = await admin.SendAsync(HttpMethod.Put, $"/api/v1/incidents/{id}/assignee", new { assigneeId = outsider.Id });
        Assert.Equal(HttpStatusCode.BadRequest, notMember.StatusCode);
        Assert.Contains("assigneeId", (await notMember.ProblemAsync()).Errors!.Keys);
        var assigned = await admin.SendOkAsync(HttpMethod.Put, $"/api/v1/incidents/{id}/assignment", new { groupId = field, assigneeId = outsider.Id });
        Assert.Equal((field, outsider.Id), (GroupOf(assigned)!.Value, AssigneeOf(assigned)!.Value));

        // Category and location change without routing again.
        var classified = await admin.SendOkAsync(HttpMethod.Put, $"/api/v1/incidents/{id}/classification", new { categoryId = hardware, locationId = floor });
        Assert.Equal("Main building › Floor 2", (string)classified["location"]!["path"]!["en"]!);
        Assert.Equal(field, GroupOf(classified));

        // Route again: the location rule sends it to the desk, round-robin picks the member.
        var rule = await admin.RuleAsync("Main building", desk, "RoundRobin", locationId: main);
        var rerouted = await admin.SendOkAsync(HttpMethod.Post, $"/api/v1/incidents/{id}/route");
        Assert.Equal((desk, member.Id), (GroupOf(rerouted)!.Value, AssigneeOf(rerouted)!.Value));
        Assert.Equal(rule, Guid.Parse((string)rerouted["routingRule"]!["id"]!));

        // Filters: my groups, category (with what is below it), location.
        Assert.Equal(1, (int)(await asMember.SendOkAsync(HttpMethod.Get, "/api/v1/incidents?group=mine"))["totalCount"]!);
        Assert.Equal(1, (int)(await admin.SendOkAsync(HttpMethod.Get, $"/api/v1/incidents?location={main}"))["totalCount"]!);
        Assert.Equal(0, (int)(await admin.SendOkAsync(HttpMethod.Get, $"/api/v1/incidents?group={field}"))["totalCount"]!);
        var members = await admin.ArrayAsync($"/api/v1/incidents/assignees?groupId={desk}");
        Assert.Equal([member.Id], members.Select(m => Guid.Parse((string)m!["id"]!)));

        // History names the group and category.
        var history = await admin.ArrayAsync($"/api/v1/incidents/{id}/history");
        var values = history.SelectMany(e => e!["changes"] is JsonObject c ? c.Select(x => x.Value?["new"]?.ToString()) : []).ToList();
        Assert.Contains("Field team", values);
        Assert.Contains("Main building › Floor 2", values);

        // Resolved incidents are not routed again.
        await admin.MoveAsync(id, "InProgress");
        await admin.MoveAsync(id, "Resolved", notes: "Replaced the cable.");
        var late = await admin.SendAsync(HttpMethod.Post, $"/api/v1/incidents/{id}/route");
        Assert.Equal("ticket-not-open", (await late.ProblemAsync()).Code);
    }

    [Fact]
    public async Task Rules_are_ordered_validated_and_only_administrators_change_them()
    {
        var tenant = await database.CreateTenantAsync("rules");
        using var admin = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.TenantAdmin);
        using var agent = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.Agent);
        var desk = await admin.GroupAsync("Desk");
        var network = await admin.GroupAsync("Network");
        var first = await admin.RuleAsync("Everything", desk);
        var second = await admin.RuleAsync("Arabic", network, language: "ar");

        // The first match wins until the order changes.
        Assert.Equal(desk, Guid.Parse((string)(await admin.SendOkAsync(HttpMethod.Post, "/api/v1/assignment-rules/test", new { language = "ar" }))["group"]!["id"]!));
        await admin.SendOkAsync(HttpMethod.Put, "/api/v1/assignment-rules/order", new { ids = new[] { second, first } });
        Assert.Equal(network, Guid.Parse((string)(await admin.SendOkAsync(HttpMethod.Post, "/api/v1/assignment-rules/test", new { language = "ar" }))["group"]!["id"]!));
        Assert.Equal(["Arabic", "Everything"], (await agent.ArrayAsync("/api/v1/assignment-rules")).Select(r => (string)r!["name"]!["en"]!));

        Assert.Equal(HttpStatusCode.BadRequest, (await admin.SendAsync(HttpMethod.Put, "/api/v1/assignment-rules/order", new { ids = new[] { first } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.SendAsync(HttpMethod.Post, "/api/v1/assignment-rules",
            new { name = new { en = "Bad" }, groupId = desk, method = "GroupQueue", skillId = Guid.NewGuid() })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.SendAsync(HttpMethod.Post, "/api/v1/assignment-rules",
            new { name = new { en = "Bad" }, groupId = desk, method = "GroupQueue", language = "fr" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.SendAsync(HttpMethod.Post, "/api/v1/assignment-rules",
            new { name = new { en = "Bad" }, groupId = Guid.NewGuid(), method = "GroupQueue" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await agent.SendAsync(HttpMethod.Post, "/api/v1/assignment-rules",
            new { name = new { en = "Mine" }, groupId = desk, method = "GroupQueue" })).StatusCode);

        // A rule whose group became inactive is skipped.
        await admin.SendOkAsync(HttpMethod.Put, $"/api/v1/assignment-rules/{second}",
            new { name = new { en = "Arabic" }, groupId = network, method = "GroupQueue", language = "ar", active = false });
        await admin.SendOkAsync(HttpMethod.Patch, $"/api/v1/groups/{network}", new { active = false });
        Assert.Equal(desk, Guid.Parse((string)(await admin.SendOkAsync(HttpMethod.Post, "/api/v1/assignment-rules/test", new { language = "ar" }))["group"]!["id"]!));
        Assert.Equal(HttpStatusCode.NoContent, (await admin.SendAsync(HttpMethod.Delete, $"/api/v1/assignment-rules/{second}")).StatusCode);
    }
}
