using System.Net;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Zelvo.Ai;
using Zelvo.Ai.Features;
using Zelvo.Api.Problems;
using Zelvo.Data.Identity;
using Zelvo.Data.Problems;
using Zelvo.Data.Tenancy;

namespace Zelvo.Api.Tests;

/// <summary>Problem detection (task H2, module 16): similar incidents judged by a fake AI and trends, against PostgreSQL.</summary>
public sealed class ProblemDetectionTests(DatabaseFixture database) : IAsyncDisposable
{
    private readonly ApiFactory _factory = ApiFactory.ForDatabase(database).WithLocalAi().WithSetting("Ai:AllowInsecureHttp", "true");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    private ProblemDetectionJobs Jobs => _factory.Services.GetRequiredService<ProblemDetectionJobs>();

    private async Task<HttpClient> AdminWithAiAsync(Tenant tenant, FakeAiProvider fake)
    {
        var admin = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.TenantAdmin);
        var provider = (await admin.SendOkAsync(HttpMethod.Post, "/api/v1/ai/providers",
            new { name = "Local model", kind = "OpenAICompatible", endpoint = new Uri(fake.Url, "v1").ToString() }, HttpStatusCode.Created)).Id();
        await admin.SendOkAsync(HttpMethod.Put, $"/api/v1/ai/features/{AiFeatures.ProblemDetection}", new { enabled = true, providerId = provider, model = "test-model" });
        return admin;
    }

    private async Task<Guid> IncidentAsync(HttpClient requester, string title, string? description = null, Guid? categoryId = null, int daysAgo = 0)
    {
        var id = IncidentApi.IdOf(await requester.CreateAsync(new { title, description, categoryId }));
        if (daysAgo > 0)
        {
            await database.ExecuteAsOwnerAsync("UPDATE tickets SET created_at = now() - make_interval(days => @d) WHERE id = @id", ("d", daysAgo), ("id", id));
        }

        return id;
    }

    private static string Related(string exclude = "") =>
        $$"""{"related": true, "confidence": 85, "title": "VPN drops connections every hour", "reason": "All report the VPN dropping at the hour.", "exclude": [{{exclude}}]}""";

    [Fact]
    public async Task Similar_incidents_are_judged_by_the_AI_and_offered_to_problem_managers_who_dismiss_with_a_reason()
    {
        var tenant = await database.CreateTenantAsync("h2sim");
        await using var fake = await FakeAiProvider.StartAsync();
        using var admin = await AdminWithAiAsync(tenant, fake);
        using var requester = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.Requester);
        using var agent = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.Agent);
        await admin.SendOkAsync(HttpMethod.Put, "/api/v1/settings/problems/detection", new { minimumIncidents = 3, trendsEnabled = false });

        var vpn = new List<Guid>();
        foreach (var (title, days) in new[] { ("VPN disconnects every hour", 20), ("The VPN disconnects every hour again", 12), ("VPN keeps disconnecting every hour", 5), ("Every hour the VPN disconnects", 1) })
        {
            vpn.Add(await IncidentAsync(requester, title, "Remote access VPN connection drops", daysAgo: days));
        }

        var printer = await IncidentAsync(requester, "Printer on the third floor jams");
        var old = await IncidentAsync(requester, "VPN disconnects every hour", "Remote access VPN connection drops", daysAgo: 80);

        // Not for agents: problem managers (team leads and administrators) see the queue.
        Assert.Equal(HttpStatusCode.Forbidden, (await agent.SendAsync(HttpMethod.Get, "/api/v1/problem-suggestions")).StatusCode);

        fake.Respond = body => body.Contains("Task: problemDetection.", StringComparison.Ordinal) ? Related() : null;
        var run = await Jobs.RunTenantAsync(tenant.Id, tenant.Slug, DateTimeOffset.UtcNow, Ct);
        Assert.Equal(1, run!.Similar);
        Assert.Null(run.SimilarSkipped);

        // Only Zelvo's shortlist went to the AI, by reference, and never the incident outside the 60 days.
        var prompt = fake.Received.Single().Body;
        Assert.Contains("i1 (", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("Printer", prompt, StringComparison.Ordinal);

        var list = await admin.SendOkAsync(HttpMethod.Get, "/api/v1/problem-suggestions");
        var suggestion = list["items"]!.AsArray().Single()!;
        Assert.Equal("SimilarIncidents", (string)suggestion["kind"]!);
        Assert.Equal("VPN drops connections every hour", (string)suggestion["title"]!);
        Assert.Equal(85, (int)suggestion["confidence"]!);
        Assert.Equal(4, (int)suggestion["incidentCount"]!);
        var named = suggestion["incidents"]!.AsArray().Select(i => Guid.Parse((string)i!["id"]!)).ToHashSet();
        Assert.True(named.SetEquals(vpn));
        Assert.DoesNotContain(printer, named);
        Assert.DoesNotContain(old, named);

        // Dismissing needs a reason, which is kept; the group is not suggested again.
        var id = Guid.Parse((string)suggestion["id"]!);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.SendAsync(HttpMethod.Post, $"/api/v1/problem-suggestions/{id}/dismiss", new { reason = " " })).StatusCode);
        var dismissed = await admin.SendOkAsync(HttpMethod.Post, $"/api/v1/problem-suggestions/{id}/dismiss", new { reason = "A known ISP outage, already handled." });
        Assert.Equal("Dismissed", (string)dismissed["status"]!);
        Assert.Equal("A known ISP outage, already handled.", (string)dismissed["dismissReason"]!);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.SendAsync(HttpMethod.Post, $"/api/v1/problem-suggestions/{id}/accept", new { })).StatusCode);

        await IncidentAsync(requester, "VPN disconnects every hour today", "Remote access VPN connection drops");
        var again = await Jobs.RunTenantAsync(tenant.Id, tenant.Slug, DateTimeOffset.UtcNow, Ct);
        Assert.Equal(0, again!.Similar);
        Assert.Single(fake.Received);
        Assert.Empty((await admin.SendOkAsync(HttpMethod.Get, "/api/v1/problem-suggestions"))["items"]!.AsArray());
        Assert.Single((await admin.SendOkAsync(HttpMethod.Get, "/api/v1/problem-suggestions?status=Dismissed"))["items"]!.AsArray());

        await using var db = TestData.AppContext(database, tenant);
        Assert.True(await db.AuditEvents.AnyAsync(e => e.Action == "problems.suggestion.made", Ct));
        Assert.True(await db.AuditEvents.AnyAsync(e => e.Action == "problems.suggestion.dismissed", Ct));
    }

    [Fact]
    public async Task Accepting_raises_the_problem_with_the_incidents_linked_and_a_group_the_AI_rejects_is_never_shown()
    {
        var tenant = await database.CreateTenantAsync("h2acc");
        await using var fake = await FakeAiProvider.StartAsync();
        using var admin = await AdminWithAiAsync(tenant, fake);
        using var requester = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.Requester);
        using var lead = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.TeamLead);
        await admin.SendOkAsync(HttpMethod.Put, "/api/v1/settings/problems/detection", new { minimumIncidents = 3, trendsEnabled = false });
        var email = await admin.CategoryAsync("Email", "البريد");

        var outlook = new List<Guid>();
        for (var i = 0; i < 4; i++)
        {
            outlook.Add(await IncidentAsync(requester, $"Outlook cannot send email to external addresses {i}", "Messages stay in the outbox", email, daysAgo: 10 - i));
        }

        var wifi = new List<Guid>();
        for (var i = 0; i < 3; i++)
        {
            wifi.Add(await IncidentAsync(requester, $"Guest wireless network password rejected {i}", "Visitors cannot join the guest wireless", daysAgo: 3));
        }

        // The AI confirms the email group without its first incident and does not see a shared cause in the other.
        fake.Respond = body => !body.Contains("Task: problemDetection.", StringComparison.Ordinal) ? null
            : body.Contains("Outlook", StringComparison.Ordinal) ? Related("\"i1\"")
            : """{"related": false, "confidence": 30, "title": "Guest wireless", "reason": "Different visitors, different days."}""";
        var run = await Jobs.RunTenantAsync(tenant.Id, tenant.Slug, DateTimeOffset.UtcNow, Ct);
        Assert.Equal(1, run!.Similar);
        Assert.Equal(2, fake.Received.Count);

        var suggestion = (await lead.SendOkAsync(HttpMethod.Get, "/api/v1/problem-suggestions"))["items"]!.AsArray().Single()!;
        Assert.Equal(3, (int)suggestion["incidentCount"]!);
        Assert.Equal(email.ToString(), (string)suggestion["category"]!["id"]!);
        Assert.DoesNotContain(outlook[0].ToString(), suggestion["incidents"]!.ToJsonString(), StringComparison.Ordinal);

        // Not confirmed: never listed, and not judged again until the group has doubled.
        await Jobs.RunTenantAsync(tenant.Id, tenant.Slug, DateTimeOffset.UtcNow, Ct);
        Assert.Equal(2, fake.Received.Count);

        var id = Guid.Parse((string)suggestion["id"]!);
        var problem = await lead.SendOkAsync(HttpMethod.Post, $"/api/v1/problem-suggestions/{id}/accept", new { title = "Outbound email stuck in the outbox" }, HttpStatusCode.Created);
        Assert.StartsWith("PRB-", (string)problem["number"]!, StringComparison.Ordinal);
        Assert.Equal("Outbound email stuck in the outbox", (string)problem["title"]!);
        Assert.Equal(3, (int)problem["incidentCount"]!);
        Assert.Equal(email.ToString(), (string)problem["category"]!["id"]!);
        var linked = (await lead.ArrayAsync($"/api/v1/problems/{problem.Id()}/incidents")).Select(i => Guid.Parse((string)i!["id"]!)).ToHashSet();
        Assert.True(linked.SetEquals(outlook.Skip(1)));

        var accepted = await lead.SendOkAsync(HttpMethod.Get, $"/api/v1/problem-suggestions/{id}");
        Assert.Equal("Accepted", (string)accepted["status"]!);
        Assert.Equal((string)problem["number"]!, (string)accepted["problem"]!["number"]!);
        Assert.Equal(HttpStatusCode.Conflict, (await lead.SendAsync(HttpMethod.Post, $"/api/v1/problem-suggestions/{id}/accept", new { })).StatusCode);

        // Incidents of a problem are not grouped again.
        await IncidentAsync(requester, "Outlook cannot send email to external addresses 9", "Messages stay in the outbox", email);
        Assert.Equal(0, (await Jobs.RunTenantAsync(tenant.Id, tenant.Slug, DateTimeOffset.UtcNow, Ct))!.Similar);
    }

    [Fact]
    public async Task Rising_trends_per_category_and_business_service_are_suggested_without_AI()
    {
        var tenant = await database.CreateTenantAsync("h2trd");
        using var admin = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.TenantAdmin);
        using var requester = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.Requester);
        var detection = await admin.SendOkAsync(HttpMethod.Put, "/api/v1/settings/problems/detection", new { trendMinimum = 5, trendFactor = 2.0 });
        Assert.False((bool)detection["aiAvailable"]!);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.SendAsync(HttpMethod.Put, "/api/v1/settings/problems/detection", new { windowDays = 120 })).StatusCode);

        // Payroll: 5 in the last 30 days against 1 a month before (rising). Badges: 5 against 3 a month (not twice).
        var payroll = await admin.CategoryAsync("Payroll", "الرواتب");
        var badges = await admin.CategoryAsync("Badges", "البطاقات");
        foreach (var days in new[] { 1, 3, 6, 9, 12 })
        {
            await IncidentAsync(requester, $"Payslip missing {days}", categoryId: payroll, daysAgo: days);
            await IncidentAsync(requester, $"Badge reader {days}", categoryId: badges, daysAgo: days);
        }

        foreach (var days in new[] { 40, 70, 100 })
        {
            await IncidentAsync(requester, $"Payslip late {days}", categoryId: payroll, daysAgo: days);
        }

        foreach (var days in new[] { 35, 40, 45, 65, 70, 75, 95, 100, 105 })
        {
            await IncidentAsync(requester, $"Badge reader old {days}", categoryId: badges, daysAgo: days);
        }

        // A server the Email service depends on: its incidents also count for the service.
        var classes = await admin.StandardClassesAsync();
        var types = await admin.TypesAsync();
        var server = await admin.ItemAsync(classes["server"], "MAIL-01");
        var service = await admin.ItemAsync(classes["business-service"], "Email");
        await admin.RelateAsync(service, types["depends-on"], server);
        foreach (var days in new[] { 2, 4, 5, 8, 10, 11 })
        {
            var id = await IncidentAsync(requester, $"Mail server slow {days}", daysAgo: days);
            await database.ExecuteAsOwnerAsync("INSERT INTO ticket_configuration_items (tenant_id, ticket_id, configuration_item_id, added_at) VALUES (@t, @i, @c, now())",
                ("t", tenant.Id), ("i", id), ("c", server));
        }

        var run = await Jobs.RunTenantAsync(tenant.Id, tenant.Slug, DateTimeOffset.UtcNow, Ct);
        Assert.Equal("ai-off", run!.SimilarSkipped);
        Assert.Equal(3, run.Trends);

        var items = (await admin.SendOkAsync(HttpMethod.Get, "/api/v1/problem-suggestions?kind=Trend"))["items"]!.AsArray();
        var byDimension = items.ToDictionary(i => (string)i!["dimension"]!, i => i!.AsObject(), StringComparer.Ordinal);
        Assert.Equal(["Category", "ConfigurationItem", "Service"], byDimension.Keys.Order(StringComparer.Ordinal));
        var category = byDimension["Category"];
        Assert.Equal(payroll.ToString(), (string)category["category"]!["id"]!);
        Assert.Equal(5, (int)category["incidentCount"]!);
        Assert.Equal(1.0, (double)category["baseline"]!);
        Assert.Equal("Rising incidents: Payroll", (string)category["suggestedTitle"]!["en"]!);
        Assert.Equal("ارتفاع في البلاغات: الرواتب", (string)category["suggestedTitle"]!["ar"]!);
        Assert.Equal(service.ToString(), (string)byDimension["Service"]["configurationItem"]!["id"]!);
        Assert.Equal(6, (int)byDimension["Service"]["incidentCount"]!);

        // The same trends are not suggested twice; accepting the service trend links its item.
        Assert.Equal(0, (await Jobs.RunTenantAsync(tenant.Id, tenant.Slug, DateTimeOffset.UtcNow, Ct))!.Trends);
        var problem = await admin.SendOkAsync(HttpMethod.Post, $"/api/v1/problem-suggestions/{byDimension["Service"]["id"]}/accept", new { }, HttpStatusCode.Created);
        Assert.StartsWith("Rising incidents: ", (string)problem["title"]!, StringComparison.Ordinal);
        Assert.Equal(6, (int)problem["incidentCount"]!);
        await using var db = TestData.AppContext(database, tenant);
        Assert.True(await db.TicketConfigurationItems.AnyAsync(l => l.TicketId == problem.Id() && l.ConfigurationItemId == service, Ct));
        Assert.Equal(ProblemSuggestionStatus.Accepted, (await db.ProblemSuggestions.SingleAsync(s => s.Dimension == ProblemTrendDimension.Service, Ct)).Status);

        // "Look now" runs the same for the problem manager.
        var now = await admin.SendOkAsync(HttpMethod.Post, "/api/v1/problem-suggestions/detect");
        Assert.Equal(0, (int)now["trends"]!);
    }

    [Fact]
    public void Similar_groups_use_English_and_Arabic_word_forms_and_take_each_incident_once()
    {
        static GroupedIncident I(string title, int minutes) => new(Guid.NewGuid(), title, null, null, DateTimeOffset.UnixEpoch.AddMinutes(minutes));
        var incidents = new[]
        {
            I("البوابة الإلكترونية لا تفتح", 1), I("البوابه الالكترونيه لا تفتح", 2), I("بوابة إلكترونية لا تفتح اليوم", 3),
            I("Printer jammed", 4), I("Payroll export failed", 5), I("Printer jammed again", 6),
        };
        var groups = SimilarIncidentGroups.Find(incidents, minimum: 3);
        var group = Assert.Single(groups);
        Assert.Equal(incidents.Take(3).Select(i => i.Id), group.Select(i => i.Id));
        Assert.Empty(SimilarIncidentGroups.Find(incidents, minimum: 4));
    }
}
