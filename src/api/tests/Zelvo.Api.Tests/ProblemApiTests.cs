using System.Net;
using Zelvo.Data.Identity;

namespace Zelvo.Api.Tests;

/// <summary>Problem management and the known error database (task H1, module 16), against PostgreSQL.</summary>
public sealed class ProblemApiTests(DatabaseFixture database) : IAsyncDisposable
{
    private readonly ApiFactory _factory = ApiFactory.ForDatabase(database);

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    [Fact]
    public async Task A_problem_raised_from_incidents_moves_through_its_stages_and_resolves_its_incidents()
    {
        var tenant = await database.CreateTenantAsync("prb");
        using var admin = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.TenantAdmin);
        var leadUser = await database.CreateUserAsync(tenant, BuiltInRoles.TeamLead);
        using var lead = _factory.ForTenant(tenant).As(leadUser);
        var agentUser = await database.CreateUserAsync(tenant, BuiltInRoles.Agent);
        using var agent = _factory.ForTenant(tenant).As(agentUser);
        using var requester = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.Requester);

        var group = (await admin.SendOkAsync(HttpMethod.Post, "/api/v1/groups", new { name = new { en = "Problem team", ar = "فريق المشكلات" } }, HttpStatusCode.Created)).Id();
        await admin.SendOkAsync(HttpMethod.Put, $"/api/v1/groups/{group}/members/{leadUser.Id}", new { isLead = true });
        await admin.SendOkAsync(HttpMethod.Put, $"/api/v1/groups/{group}/members/{agentUser.Id}", new { isLead = false });

        var first = IncidentApi.IdOf(await requester.CreateAsync(new { title = "Portal down" }));
        var second = IncidentApi.IdOf(await requester.CreateAsync(new { title = "Portal down again" }));
        await agent.SendOkAsync(HttpMethod.Put, $"/api/v1/incidents/{first}/assignment", new { groupId = group, assigneeId = agentUser.Id });

        // Requesters never see problems; agents raise them from incidents.
        Assert.Equal(HttpStatusCode.Forbidden, (await requester.SendAsync(HttpMethod.Get, "/api/v1/problems")).StatusCode);
        var created = await agent.SendOkAsync(HttpMethod.Post, "/api/v1/problems", new { title = "Portal keeps going down", incidentIds = new[] { first, second } }, HttpStatusCode.Created);
        var id = created.Id();
        Assert.StartsWith("PRB-", (string)created["number"]!, StringComparison.Ordinal);
        Assert.Equal("New", (string)created["stage"]!);
        Assert.Equal((int)(await agent.GetIncidentAsync(first))["priority"]!, (int)created["priority"]!);
        Assert.Equal(2, (int)created["incidentCount"]!);
        Assert.Contains((string)created["number"]!, (await agent.ArrayAsync($"/api/v1/incidents/{first}/problems")).ToJsonString(), StringComparison.Ordinal);

        // Assignment needs the problem manager (team leads by default).
        Assert.Equal(HttpStatusCode.Forbidden, (await agent.SendAsync(HttpMethod.Put, $"/api/v1/problems/{id}/assignment", new { groupId = group, assigneeId = agentUser.Id })).StatusCode);
        await lead.SendOkAsync(HttpMethod.Put, $"/api/v1/problems/{id}/assignment", new { groupId = group, assigneeId = agentUser.Id });

        // Assessment → root-cause analysis with the 5 Whys and fishbone.
        Assert.Equal("Assessment", (string)(await agent.SendOkAsync(HttpMethod.Post, $"/api/v1/problems/{id}/transitions", new { to = "Assessment" }))["stage"]!);
        var analysed = await agent.SendOkAsync(HttpMethod.Put, $"/api/v1/problems/{id}/analysis", new
        {
            fiveWhys = new { statement = "Portal down", steps = new[] { new { why = "Why?", because = "Pool exhausted" } }, conclusion = "Resize the pool" },
            fishbone = new { effect = "Outage", causes = new[] { new { category = "technology", text = "Pool too small", root = true } } },
        });
        Assert.Equal("Pool exhausted", (string)analysed["fiveWhys"]!["steps"]![0]!["because"]!);
        using var wrongCategory = await agent.SendAsync(HttpMethod.Put, $"/api/v1/problems/{id}/analysis", new
        {
            fishbone = new { causes = new[] { new { category = "astrology", text = "Stars", root = false } } },
        });
        Assert.Equal(HttpStatusCode.BadRequest, wrongCategory.StatusCode);
        Assert.Equal("RootCauseAnalysis", (string)(await agent.SendOkAsync(HttpMethod.Post, $"/api/v1/problems/{id}/transitions", new { to = "RootCauseAnalysis" }))["stage"]!);

        // Known error: needs a workaround before the stage; agents see it on matching incidents.
        Assert.Equal("known-error-required", (await (await lead.SendAsync(HttpMethod.Post, $"/api/v1/problems/{id}/transitions", new { to = "KnownError" })).ProblemAsync()).Code);
        var error = await lead.SendOkAsync(HttpMethod.Put, $"/api/v1/problems/{id}/known-error", new
        {
            title = new { en = "Portal times out at peak", ar = "انتهاء مهلة البوابة في الذروة" },
            workaround = new { en = "Restart the application pool.", ar = "أعد تشغيل مجمّع التطبيقات." },
        });
        Assert.StartsWith("KE-", (string)error["number"]!, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Forbidden, (await agent.SendAsync(HttpMethod.Post, $"/api/v1/problems/{id}/transitions", new { to = "KnownError" })).StatusCode);
        Assert.Equal("KnownError", (string)(await lead.SendOkAsync(HttpMethod.Post, $"/api/v1/problems/{id}/transitions", new { to = "KnownError" }))["stage"]!);
        var matching = await agent.ArrayAsync($"/api/v1/incidents/{second}/known-errors");
        Assert.Equal("Restart the application pool.", (string)matching.Single()!["knownError"]!["workaround"]!["en"]!);
        Assert.Contains("problem", matching.Single()!["reasons"]!.AsArray().Select(r => (string)r!));

        // The assignee of the open incident heard about the workaround.
        await _factory.DeliverNotificationsAsync(tenant);
        var inbox = await agent.InboxAsync();
        Assert.Contains("problem.known-error", inbox.Events());

        // Timeline of the problem and its incidents.
        var timeline = await agent.ArrayAsync($"/api/v1/problems/{id}/timeline");
        Assert.Contains(timeline, e => (string)e!["kind"]! == "incident.linked");
        Assert.Contains(timeline, e => (string)e!["kind"]! == "problem.known-error");

        // Resolving resolves the open incidents too.
        Assert.Equal(HttpStatusCode.BadRequest, (await lead.SendAsync(HttpMethod.Post, $"/api/v1/problems/{id}/transitions", new { to = "Resolved" })).StatusCode);
        var resolved = await lead.SendOkAsync(HttpMethod.Post, $"/api/v1/problems/{id}/transitions", new { to = "Resolved", resolutionNotes = "Pool enlarged.", resolveIncidents = true });
        Assert.Equal("Resolved", (string)resolved["stage"]!);
        Assert.Equal(0, (int)resolved["openIncidentCount"]!);
        var incident = await agent.GetIncidentAsync(second);
        Assert.Equal("Resolved", (string)incident["state"]!);
        Assert.Contains("Pool enlarged.", (string)incident["resolutionNotes"]!, StringComparison.Ordinal);
        Assert.Equal("Closed", (string)(await lead.SendOkAsync(HttpMethod.Post, $"/api/v1/problems/{id}/transitions", new { to = "Closed" }))["stage"]!);
        Assert.Equal(HttpStatusCode.Conflict, (await agent.SendAsync(HttpMethod.Put, $"/api/v1/problems/{id}/analysis", new { fiveWhys = new { steps = Array.Empty<object>() } })).StatusCode);
    }

    [Fact]
    public async Task Known_errors_match_incidents_by_category_and_become_external_article_drafts()
    {
        var tenant = await database.CreateTenantAsync("prb");
        using var admin = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.TenantAdmin);
        using var agent = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.Agent);
        using var requester = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.Requester);

        var parent = (await admin.SendOkAsync(HttpMethod.Post, "/api/v1/categories", new { name = new { en = "Network", ar = "الشبكة" } }, HttpStatusCode.Created)).Id();
        var child = (await admin.SendOkAsync(HttpMethod.Post, "/api/v1/categories", new { name = new { en = "VPN", ar = "الشبكة الخاصة" }, parentId = parent }, HttpStatusCode.Created)).Id();
        var problem = (await agent.SendOkAsync(HttpMethod.Post, "/api/v1/problems", new { title = "VPN drops", categoryId = parent, priority = 2 }, HttpStatusCode.Created)).Id();
        var error = await agent.SendOkAsync(HttpMethod.Put, $"/api/v1/problems/{problem}/known-error", new
        {
            title = new { en = "VPN drops every few minutes" },
            symptoms = new { en = "Sessions end after 5 minutes." },
            workaround = new { en = "Switch to the backup gateway." },
        });

        // An incident in the category below matches; one elsewhere does not.
        var vpn = IncidentApi.IdOf(await requester.CreateAsync(new { title = "VPN keeps dropping", categoryId = child }));
        var other = IncidentApi.IdOf(await requester.CreateAsync(new { title = "Printer jam" }));
        Assert.Equal("category", (string)(await agent.ArrayAsync($"/api/v1/incidents/{vpn}/known-errors")).Single()!["reasons"]![0]!);
        Assert.Empty(await agent.ArrayAsync($"/api/v1/incidents/{other}/known-errors"));

        // Missing Arabic is reported; the database is searchable by its text.
        Assert.Contains(error.Id().ToString(), (await admin.SendOkAsync(HttpMethod.Get, "/api/v1/translations/missing")).ToJsonString(), StringComparison.Ordinal);
        var list = await agent.SendOkAsync(HttpMethod.Get, "/api/v1/known-errors?q=backup%20gateway");
        Assert.Equal(1, (int)list["totalCount"]!);

        // Publishing goes through the knowledge base as an external draft; agents may not do it.
        Assert.Equal(HttpStatusCode.Forbidden, (await agent.SendAsync(HttpMethod.Post, $"/api/v1/known-errors/{error.Id()}/article")).StatusCode);
        var article = await admin.SendOkAsync(HttpMethod.Post, $"/api/v1/known-errors/{error.Id()}/article", expected: HttpStatusCode.Created);
        Assert.Equal("Draft", (string)article["article"]!["state"]!);
        Assert.Equal("External", (string)article["article"]!["visibility"]!);
        Assert.Equal("article-exists", (await (await admin.SendAsync(HttpMethod.Post, $"/api/v1/known-errors/{error.Id()}/article")).ProblemAsync()).Code);
        Assert.Single(await agent.ArrayAsync($"/api/v1/problems/{problem}/articles"));

        // Retired known errors are no longer offered.
        await admin.SendOkAsync(HttpMethod.Post, $"/api/v1/known-errors/{error.Id()}/retire");
        Assert.Empty(await agent.ArrayAsync($"/api/v1/incidents/{vpn}/known-errors"));
    }

    [Fact]
    public async Task Fishbone_categories_are_settings_and_used_ones_cannot_be_removed()
    {
        var tenant = await database.CreateTenantAsync("prb");
        using var admin = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.TenantAdmin);
        using var agent = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.Agent);

        var settings = await agent.SendOkAsync(HttpMethod.Get, "/api/v1/settings/problems");
        Assert.Equal("KE", (string)settings["knownErrorPrefix"]!);
        Assert.Equal(6, settings["fishboneCategories"]!.AsArray().Count);
        Assert.Equal(HttpStatusCode.Forbidden, (await agent.SendAsync(HttpMethod.Put, "/api/v1/settings/problems", new { knownErrorPrefix = "KED" })).StatusCode);

        var problem = (await agent.SendOkAsync(HttpMethod.Post, "/api/v1/problems", new { title = "Slow app" }, HttpStatusCode.Created)).Id();
        await agent.SendOkAsync(HttpMethod.Put, $"/api/v1/problems/{problem}/analysis", new
        {
            fishbone = new { causes = new[] { new { category = "people", text = "Untrained users", root = false } } },
        });
        using var removed = await admin.SendAsync(HttpMethod.Put, "/api/v1/settings/problems", new
        {
            fishboneCategories = new[] { new { key = "technology", name = new { en = "Technology", ar = "التقنية" }, active = true } },
        });
        Assert.Equal(HttpStatusCode.BadRequest, removed.StatusCode);
        var changed = await admin.SendOkAsync(HttpMethod.Put, "/api/v1/settings/problems", new
        {
            knownErrorPrefix = "KED",
            fishboneCategories = new[]
            {
                new { key = "people", name = new { en = "People", ar = "الأفراد" }, active = false },
                new { key = "technology", name = new { en = "Technology", ar = "التقنية" }, active = true },
            },
        });
        Assert.Equal("KED", (string)changed["knownErrorPrefix"]!);
        var error = await agent.SendOkAsync(HttpMethod.Put, $"/api/v1/problems/{problem}/known-error", new { title = new { en = "Slow" }, workaround = new { en = "Wait" } });
        Assert.StartsWith("KED-", (string)error["number"]!, StringComparison.Ordinal);

        // Notes are internal; nobody but staff with problems.read reads them.
        await agent.SendOkAsync(HttpMethod.Post, $"/api/v1/problems/{problem}/comments", new { body = "Checked the logs." }, HttpStatusCode.Created);
        Assert.Single(await agent.ArrayAsync($"/api/v1/problems/{problem}/comments"));
        Assert.Single((await agent.SendOkAsync(HttpMethod.Get, "/api/v1/problems?stage=New"))["items"]!.AsArray());
    }
}
