using System.Net;
using System.Text.Json.Nodes;
using Zelvo.Data.Identity;

namespace Zelvo.Api.Tests;

/// <summary>Change management (task H3, module 17), against PostgreSQL.</summary>
public sealed class ChangeApiTests(DatabaseFixture database) : IAsyncDisposable
{
    private readonly ApiFactory _factory = ApiFactory.ForDatabase(database);

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    private static readonly DateTimeOffset Start = new DateTimeOffset(DateTime.UtcNow.Date, TimeSpan.Zero).AddDays(5).AddHours(20);

    private static object Medium => new Dictionary<string, string>
    {
        ["reach"] = "department", ["experience"] = "sometimes", ["downtime"] = "window", ["backout"] = "easy", ["testing"] = "partial",
    };

    private static object High => new Dictionary<string, string>
    {
        ["reach"] = "organisation", ["experience"] = "never", ["downtime"] = "business", ["backout"] = "hard", ["testing"] = "none",
    };

    private static async Task<Guid> PendingFor(HttpClient approver)
    {
        var page = await approver.SendOkAsync(HttpMethod.Get, "/api/v1/approvals?state=Pending");
        return Guid.Parse((string)page["items"]!.AsArray().Single()!["id"]!);
    }

    private static Task<JsonObject> NormalAsync(HttpClient client, string title, DateTimeOffset start, params Guid[] items) =>
        client.SendOkAsync(HttpMethod.Post, "/api/v1/changes", new
        {
            kind = "Normal",
            title,
            plannedStart = start,
            plannedEnd = start.AddHours(3),
            implementationPlan = "Do it.",
            backoutPlan = "Undo it.",
            configurationItemIds = items,
        }, HttpStatusCode.Created);

    [Fact]
    public async Task A_normal_change_is_assessed_approved_by_its_risk_path_implemented_with_tasks_and_reviewed()
    {
        var tenant = await database.CreateTenantAsync("chg");
        using var admin = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.TenantAdmin);
        var leadUser = await database.CreateUserAsync(tenant, BuiltInRoles.TeamLead);
        using var lead = _factory.ForTenant(tenant).As(leadUser);
        var agentUser = await database.CreateUserAsync(tenant, BuiltInRoles.Agent);
        using var agent = _factory.ForTenant(tenant).As(agentUser);
        using var requester = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.Requester);

        var group = (await admin.SendOkAsync(HttpMethod.Post, "/api/v1/groups", new { name = new { en = "Change team", ar = "فريق التغيير" } }, HttpStatusCode.Created)).Id();
        await admin.SendOkAsync(HttpMethod.Put, $"/api/v1/groups/{group}/members/{leadUser.Id}", new { isLead = true });
        await admin.SendOkAsync(HttpMethod.Put, $"/api/v1/groups/{group}/members/{agentUser.Id}", new { isLead = false });
        var classes = await admin.StandardClassesAsync();
        var server = await admin.ItemAsync(classes["server"], "db-01");
        var problem = (await agent.SendOkAsync(HttpMethod.Post, "/api/v1/problems", new { title = "Database too slow" }, HttpStatusCode.Created));

        // Requesters never see changes.
        Assert.Equal(HttpStatusCode.Forbidden, (await requester.SendAsync(HttpMethod.Get, "/api/v1/changes")).StatusCode);

        var created = await NormalAsync(agent, "Enlarge the connection pool", Start, server);
        var id = created.Id();
        Assert.StartsWith("CHG-", (string)created["number"]!, StringComparison.Ordinal);
        Assert.Equal("New", (string)created["stage"]!);
        Assert.Equal(1, (int)created["itemCount"]!);

        // A normal change needs every question answered before it is submitted.
        Assert.Equal(HttpStatusCode.BadRequest, (await agent.SendAsync(HttpMethod.Post, $"/api/v1/changes/{id}/submit")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await agent.SendAsync(HttpMethod.Put, $"/api/v1/changes/{id}/risk", new { answers = new { reach = "galaxy" } })).StatusCode);
        var assessed = await agent.SendOkAsync(HttpMethod.Put, $"/api/v1/changes/{id}/risk", new { answers = Medium });
        Assert.Equal(7, (int)assessed["riskScore"]!);
        Assert.Equal("Medium", (string)assessed["riskLevel"]!);

        // A task for the team, waiting until implementation starts; the problem it fixes.
        var tasks = await agent.SendOkAsync(HttpMethod.Post, $"/api/v1/changes/{id}/tasks", new { name = new { en = "Change the pool size", ar = "تعديل حجم المجمّع" }, groupId = group });
        Assert.Equal("Waiting", (string)(await agent.ArrayAsync($"/api/v1/changes/{id}/tasks")).Single()!["state"]!);
        _ = tasks;
        await agent.SendOkAsync(HttpMethod.Post, $"/api/v1/changes/{id}/links", new { number = (string)problem["number"]! });
        Assert.Single(await agent.ArrayAsync($"/api/v1/problems/{problem.Id()}/changes"));
        Assert.Contains(await agent.ArrayAsync($"/api/v1/problems/{problem.Id()}/timeline"), e => (string)e!["kind"]! == "change.linked");

        // Medium risk goes to the board (holders of changes.approve), not to the person who raised it.
        var submitted = await agent.SendOkAsync(HttpMethod.Post, $"/api/v1/changes/{id}/submit");
        Assert.Equal("Authorisation", (string)submitted["stage"]!);
        Assert.Equal("Change advisory board", (string)submitted["approval"]!["levelName"]!["en"]!);
        Assert.Equal(HttpStatusCode.Conflict, (await agent.SendAsync(HttpMethod.Patch, $"/api/v1/changes/{id}", new { implementationPlan = "Something else" })).StatusCode);
        await _factory.DeliverNotificationsAsync(tenant);
        Assert.Contains("change.approval-requested", (await lead.InboxAsync()).Events());
        await lead.SendOkAsync(HttpMethod.Post, $"/api/v1/approvals/{await PendingFor(lead)}/decision", new { decision = "Approve", comment = "Fine." });
        var scheduled = await agent.SendOkAsync(HttpMethod.Get, $"/api/v1/changes/{id}");
        Assert.Equal("Scheduled", (string)scheduled["stage"]!);
        Assert.Equal("Approved", (string)scheduled["approval"]!["state"]!);
        await _factory.DeliverNotificationsAsync(tenant);
        Assert.Contains("change.approved", (await agent.InboxAsync()).Events());

        // Implementation needs an assignee; its tasks open; it is implemented once they are done.
        Assert.Equal("assignee-required", (await (await agent.SendAsync(HttpMethod.Post, $"/api/v1/changes/{id}/transitions", new { to = "StartImplementation" })).ProblemAsync()).Code);
        Assert.Equal(HttpStatusCode.Forbidden, (await agent.SendAsync(HttpMethod.Put, $"/api/v1/changes/{id}/assignment", new { groupId = group, assigneeId = agentUser.Id })).StatusCode);
        await lead.SendOkAsync(HttpMethod.Put, $"/api/v1/changes/{id}/assignment", new { groupId = group, assigneeId = agentUser.Id });
        Assert.Equal("Implementation", (string)(await agent.SendOkAsync(HttpMethod.Post, $"/api/v1/changes/{id}/transitions", new { to = "StartImplementation" }))["stage"]!);
        var task = (await agent.ArrayAsync($"/api/v1/changes/{id}/tasks")).Single()!.AsObject();
        Assert.Equal("Open", (string)task["state"]!);
        Assert.Equal("Change", (string)task["requestType"]!);
        Assert.Equal("tasks-open", (await (await agent.SendAsync(HttpMethod.Post, $"/api/v1/changes/{id}/transitions", new { to = "Implemented", notes = "Done." })).ProblemAsync()).Code);
        await agent.SendOkAsync(HttpMethod.Post, $"/api/v1/request-tasks/{task.Id()}/complete", new { notes = "Pool now 300." });
        // The last task does not resolve the change by itself: a person marks it implemented.
        Assert.Equal("Implementation", (string)(await agent.SendOkAsync(HttpMethod.Get, $"/api/v1/changes/{id}"))["stage"]!);
        Assert.Equal("Review", (string)(await agent.SendOkAsync(HttpMethod.Post, $"/api/v1/changes/{id}/transitions", new { to = "Implemented", notes = "Done in an hour." }))["stage"]!);

        // The post-implementation review closes it (the change manager).
        Assert.Equal(HttpStatusCode.Forbidden, (await agent.SendAsync(HttpMethod.Post, $"/api/v1/changes/{id}/review", new { outcome = "Successful" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await lead.SendAsync(HttpMethod.Post, $"/api/v1/changes/{id}/review", new { outcome = "Failed" })).StatusCode);
        var closed = await lead.SendOkAsync(HttpMethod.Post, $"/api/v1/changes/{id}/review", new { outcome = "SuccessfulWithProblems", notes = "Took longer than planned." });
        Assert.Equal("Closed", (string)closed["stage"]!);
        Assert.Equal("Completed", (string)closed["closure"]!);
        Assert.Equal("SuccessfulWithProblems", (string)closed["outcome"]!);
    }

    [Fact]
    public async Task Standard_changes_are_pre_approved_except_in_a_blackout_and_conflicts_reach_the_calendar()
    {
        var tenant = await database.CreateTenantAsync("chg");
        using var admin = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.TenantAdmin);
        using var agent = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.Agent);
        var classes = await admin.StandardClassesAsync();
        var types = await admin.TypesAsync();
        var server = await admin.ItemAsync(classes["server"], "web-01");
        var service = await admin.ItemAsync(classes["business-service"], "E-services");
        await admin.RelateAsync(service, types["runs-on"], server);

        // The catalog of standard changes is the change manager's.
        Assert.Equal(HttpStatusCode.Forbidden, (await agent.SendAsync(HttpMethod.Post, "/api/v1/change-templates", new { name = new { en = "Restart" } })).StatusCode);
        var template = await admin.SendOkAsync(HttpMethod.Post, "/api/v1/change-templates", new
        {
            name = new { en = "Restart a web server", ar = "إعادة تشغيل خادم ويب" },
            implementationPlan = "Drain and restart.",
            backoutPlan = "Keep it drained.",
            tasks = new[] { new { name = new { en = "Restart" } } },
        }, HttpStatusCode.Created);

        // A blackout on the business service covers the server it runs on.
        await admin.SendOkAsync(HttpMethod.Post, "/api/v1/change-blackouts", new
        {
            name = new { en = "Peak week", ar = "أسبوع الذروة" }, startsAt = Start.AddDays(-1), endsAt = Start.AddDays(1), configurationItemId = service,
        }, HttpStatusCode.Created);
        var inBlackout = await agent.SendOkAsync(HttpMethod.Post, "/api/v1/changes", new
        {
            kind = "Standard", templateId = template.Id(), plannedStart = Start, plannedEnd = Start.AddHours(1), configurationItemIds = new[] { server },
        }, HttpStatusCode.Created);
        Assert.Equal("Restart a web server", (string)inBlackout["title"]!);
        Assert.Equal("Low", (string)inBlackout["riskLevel"]!);
        Assert.Equal(1, (int)inBlackout["taskCount"]!);
        Assert.Contains(inBlackout["conflicts"]!.AsArray(), c => (string)c!["kind"]! == "blackout");
        Assert.Equal("blackout", (await (await agent.SendAsync(HttpMethod.Post, $"/api/v1/changes/{inBlackout.Id()}/submit")).ProblemAsync()).Code);

        // Outside it, a standard change is scheduled at once.
        await agent.SendOkAsync(HttpMethod.Patch, $"/api/v1/changes/{inBlackout.Id()}", new { plannedStart = Start.AddDays(2), plannedEnd = Start.AddDays(2).AddHours(1) });
        Assert.Equal("Scheduled", (string)(await agent.SendOkAsync(HttpMethod.Post, $"/api/v1/changes/{inBlackout.Id()}/submit"))["stage"]!);

        // Another change on the same server in an overlapping window is a conflict, shown before submission and on the calendar.
        var other = await NormalAsync(agent, "Patch web-01", Start.AddDays(2).AddMinutes(30), server);
        var conflict = other["conflicts"]!.AsArray().Single()!;
        Assert.Equal("change", (string)conflict["kind"]!);
        Assert.Equal((string)inBlackout["number"]!, (string)conflict["changeNumber"]!);
        var check = await agent.SendAsync(HttpMethod.Post, "/api/v1/changes/conflicts", new { plannedStart = Start.AddDays(2), plannedEnd = Start.AddDays(2).AddHours(2), configurationItemIds = new[] { server } });
        Assert.Equal(2, JsonNode.Parse(await check.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!.AsArray().Count);
        var calendar = await agent.SendOkAsync(HttpMethod.Get, $"/api/v1/changes/calendar?from={Uri.EscapeDataString(Start.AddDays(-2).ToString("O"))}&to={Uri.EscapeDataString(Start.AddDays(5).ToString("O"))}");
        Assert.Equal(2, calendar["changes"]!.AsArray().Count);
        Assert.All(calendar["changes"]!.AsArray(), c => Assert.True((bool)c!["conflict"]!));
        Assert.Single(calendar["blackouts"]!.AsArray());

        // High risk: the board and an executive; a rejection closes the change with the reason.
        await agent.SendOkAsync(HttpMethod.Put, $"/api/v1/changes/{other.Id()}/risk", new { answers = High });
        var submitted = await agent.SendOkAsync(HttpMethod.Post, $"/api/v1/changes/{other.Id()}/submit");
        Assert.Equal("High", (string)submitted["riskLevel"]!);
        Assert.Equal(2, (int)submitted["approval"]!["levels"]!);
        await admin.SendOkAsync(HttpMethod.Post, $"/api/v1/approvals/{await PendingFor(admin)}/decision", new { decision = "Reject", comment = "Not during the peak." });
        var rejected = await agent.SendOkAsync(HttpMethod.Get, $"/api/v1/changes/{other.Id()}");
        Assert.Equal("Closed", (string)rejected["stage"]!);
        Assert.Equal("Rejected", (string)rejected["closure"]!);
        Assert.Equal("Not during the peak.", (string)rejected["resolutionNotes"]!);
    }

    [Fact]
    public async Task Settings_change_the_questionnaire_and_paths_and_a_cancelled_change_closes()
    {
        var tenant = await database.CreateTenantAsync("chg");
        using var admin = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.TenantAdmin);
        using var agent = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.Agent);

        var settings = await admin.SendOkAsync(HttpMethod.Get, "/api/v1/settings/changes");
        Assert.Equal(5, settings["questions"]!.AsArray().Count);
        Assert.Equal("Permission", (string)settings["paths"]!["low"]![0]!["approvers"]![0]!["kind"]!);

        // One question and no approval for low risk.
        var changed = await admin.SendOkAsync(HttpMethod.Put, "/api/v1/settings/changes", new
        {
            questions = new[]
            {
                new { key = "size", text = new { en = "How big?", ar = "ما حجمه؟" }, active = true, options = new[] { new { key = "small", text = new { en = "Small", ar = "صغير" }, weight = 0 }, new { key = "big", text = new { en = "Big", ar = "كبير" }, weight = 5 } } },
            },
            mediumFrom = 3,
            highFrom = 5,
            paths = new { low = Array.Empty<object>(), medium = settings["paths"]!["medium"], high = settings["paths"]!["high"], emergency = settings["paths"]!["emergency"] },
        });
        Assert.Single(changed["questions"]!.AsArray());
        using var bad = await admin.SendAsync(HttpMethod.Put, "/api/v1/settings/changes", new { questions = Array.Empty<object>(), mediumFrom = 5, highFrom = 2, paths = changed["paths"] });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await agent.SendAsync(HttpMethod.Put, "/api/v1/settings/changes", changed)).StatusCode);

        var change = await NormalAsync(agent, "Small tweak", Start);
        await agent.SendOkAsync(HttpMethod.Put, $"/api/v1/changes/{change.Id()}/risk", new { answers = new { size = "small" } });
        Assert.Equal("Scheduled", (string)(await agent.SendOkAsync(HttpMethod.Post, $"/api/v1/changes/{change.Id()}/submit"))["stage"]!);

        Assert.Equal(HttpStatusCode.BadRequest, (await agent.SendAsync(HttpMethod.Post, $"/api/v1/changes/{change.Id()}/transitions", new { to = "Cancel" })).StatusCode);
        var cancelled = await agent.SendOkAsync(HttpMethod.Post, $"/api/v1/changes/{change.Id()}/transitions", new { to = "Cancel", notes = "No longer needed." });
        Assert.Equal("Cancelled", (string)cancelled["closure"]!);
        Assert.Equal(HttpStatusCode.Conflict, (await agent.SendAsync(HttpMethod.Patch, $"/api/v1/changes/{change.Id()}", new { title = "Again" })).StatusCode);
    }
}
