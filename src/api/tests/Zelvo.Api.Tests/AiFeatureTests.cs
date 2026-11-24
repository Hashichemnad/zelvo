using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Zelvo.Ai;
using Zelvo.Ai.Features;
using Zelvo.Api.Channels;
using Zelvo.Data.Ai;
using Zelvo.Data.Identity;
using Zelvo.Data.Knowledge;
using Zelvo.Data.Localization;
using Zelvo.Data.Tenancy;
using Zelvo.Data.Tickets;

namespace Zelvo.Api.Tests;

/// <summary>The AI features of task E5 (module 9, LANG-7, LANG-10) against PostgreSQL and a fake provider.</summary>
public sealed class AiFeatureTests(DatabaseFixture database) : IAsyncDisposable
{
    private readonly ApiFactory _factory = ApiFactory.ForDatabase(database).WithLocalAi().WithSetting("Ai:AllowInsecureHttp", "true");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    // The feature's "Task: …" line picks the answer.
    private static Func<string, string?> ByTask(Dictionary<string, string> answers) =>
        body => answers.FirstOrDefault(a => body.Contains($"Task: {a.Key}.", StringComparison.Ordinal)).Value;

    private async Task<HttpClient> AdminWithAiAsync(Tenant tenant, FakeAiProvider fake, params string[] features)
    {
        var admin = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.TenantAdmin);
        var provider = (await admin.SendOkAsync(HttpMethod.Post, "/api/v1/ai/providers",
            new { name = "Local model", kind = "OpenAICompatible", endpoint = new Uri(fake.Url, "v1").ToString() }, HttpStatusCode.Created)).Id();
        foreach (var feature in features)
        {
            await admin.SendOkAsync(HttpMethod.Put, $"/api/v1/ai/features/{feature}", new { enabled = true, providerId = provider, model = "test-model" });
        }

        return admin;
    }

    private async Task<int> RunJobsAsync()
    {
        var runner = new AiJobRunner(_factory.Services.GetRequiredService<IServiceScopeFactory>(), _factory.Services.GetRequiredService<AiOptions>(),
            NullLogger<AiJobRunner>.Instance, TimeProvider.System);
        return await runner.RunOnceAsync(Ct);
    }

    [Fact]
    public async Task Triage_suggests_and_agents_accept_or_correct_and_corrections_become_examples()
    {
        var tenant = await database.CreateTenantAsync("e5tri");
        await using var fake = await FakeAiProvider.StartAsync();
        fake.Respond = ByTask(new()
        {
            ["triage"] = """{"category": "c2", "group": "g1", "impact": 1, "urgency": 1, "confidence": 85, "reason": "Printer on a whole floor."}""",
        });
        using var admin = await AdminWithAiAsync(tenant, fake, AiFeatures.Triage);
        var network = await admin.CategoryAsync("Network", "الشبكة");
        var printers = await admin.CategoryAsync("Printers", "الطابعات");
        var agentUser = await database.CreateUserAsync(tenant, BuiltInRoles.Agent);
        var desk = await admin.GroupAsync("A service desk", agentUser);
        var facilities = await admin.GroupAsync("B facilities");
        using var agent = _factory.ForTenant(tenant).As(agentUser);
        using var requester = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.Requester);

        var id = IncidentApi.IdOf(await requester.CreateAsync(new { title = "Printer on floor 3 is jammed", description = "Nobody on the floor can print." }));
        Assert.True(await RunJobsAsync() >= 1);

        var insights = await agent.SendOkAsync(HttpMethod.Get, $"/api/v1/incidents/{id}/ai");
        var triage = insights["triage"]!;
        Assert.Equal("Pending", (string?)triage["status"]);
        Assert.Equal(printers, Guid.Parse((string)triage["category"]!["id"]!));
        Assert.Equal(desk, Guid.Parse((string)triage["group"]!["id"]!));
        Assert.Equal(85, (int)triage["confidence"]!);
        // Nothing changed yet: the agent decides.
        var before = await agent.GetIncidentAsync(id);
        Assert.Null(before["category"]);

        // The categories and groups were offered by reference; no ids or personal data left the installation.
        var sent = fake.Received.Last().Body;
        Assert.Contains("c1: Network", sent, StringComparison.Ordinal);
        Assert.Contains("g2: B facilities", sent, StringComparison.Ordinal);
        Assert.DoesNotContain(requester.DefaultRequestHeaders.Authorization?.Parameter ?? "-", sent, StringComparison.Ordinal);

        // A correction: another category and lower urgency; the group needs incidents.assign, which agents have.
        var decided = await agent.SendOkAsync(HttpMethod.Post, $"/api/v1/incidents/{id}/ai/triage/{(string)triage["id"]!}/accept", new { categoryId = network, urgency = 3 });
        Assert.Equal("Corrected", (string?)decided["status"]);
        var after = await agent.GetIncidentAsync(id);
        Assert.Equal(network, Guid.Parse((string)after["category"]!["id"]!));
        Assert.Equal(1, (int)after["impact"]!);
        Assert.Equal(3, (int)after["urgency"]!);
        Assert.Equal(3, (int)after["priority"]!);
        Assert.Equal(desk, Guid.Parse((string)after["group"]!["id"]!));
        Assert.Contains(await database.AuditAsync(tenant), a => a.Action == "ai.triage.corrected");
        // Decided once only.
        Assert.Equal(HttpStatusCode.NotFound, (await agent.SendAsync(HttpMethod.Post, $"/api/v1/incidents/{id}/ai/triage/{(string)triage["id"]!}/dismiss")).StatusCode);

        // The next suggestion learns from the correction.
        var second = IncidentApi.IdOf(await requester.CreateAsync(new { title = "Cannot print from my laptop" }));
        await RunJobsAsync();
        var prompt = fake.Received.Last().Body;
        Assert.Contains("Printer on floor 3 is jammed", prompt, StringComparison.Ordinal);
        Assert.Contains("category c1, group g1, impact 1, urgency 3 (corrected by an agent)", prompt, StringComparison.Ordinal);

        var accuracy = await admin.SendOkAsync(HttpMethod.Get, "/api/v1/ai/triage/accuracy");
        Assert.Equal(1, (int)accuracy["corrected"]!);
        Assert.Equal(0, (int)accuracy["categoryKeptPercent"]!);
        Assert.Equal(100, (int)accuracy["groupKeptPercent"]!);
        Assert.Equal(HttpStatusCode.Forbidden, (await agent.SendAsync(HttpMethod.Get, "/api/v1/ai/triage/accuracy")).StatusCode);

        var dismissed = await agent.SendOkAsync(HttpMethod.Post, $"/api/v1/incidents/{second}/ai/triage/{(string)(await agent.SendOkAsync(HttpMethod.Get, $"/api/v1/incidents/{second}/ai"))["triage"]!["id"]!}/dismiss");
        Assert.Equal("Dismissed", (string?)dismissed["status"]);
        Assert.Null((await agent.GetIncidentAsync(second))["category"]);
        _ = facilities;
    }

    [Fact]
    public async Task Triage_applies_itself_only_when_the_administrator_turned_the_rule_on()
    {
        var tenant = await database.CreateTenantAsync("e5auto");
        await using var fake = await FakeAiProvider.StartAsync();
        fake.Respond = ByTask(new() { ["triage"] = """{"category": "c1", "group": "g1", "impact": 2, "urgency": 1, "confidence": 90, "reason": "x"}""" });
        using var admin = await AdminWithAiAsync(tenant, fake, AiFeatures.Triage);
        var category = await admin.CategoryAsync("Email");
        var member = await database.CreateUserAsync(tenant, BuiltInRoles.Agent);
        var group = await admin.GroupAsync("Messaging", member);
        using var requester = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.Requester);

        Assert.False((bool)(await admin.SendOkAsync(HttpMethod.Get, "/api/v1/ai/triage/settings"))["autoApply"]!);
        await admin.SendOkAsync(HttpMethod.Put, "/api/v1/ai/triage/settings", new { autoApply = true });

        var id = IncidentApi.IdOf(await requester.CreateAsync(new { title = "Outlook will not open" }));
        await RunJobsAsync();
        var incident = await admin.GetIncidentAsync(id);
        Assert.Equal(category, Guid.Parse((string)incident["category"]!["id"]!));
        Assert.Equal(group, Guid.Parse((string)incident["group"]!["id"]!));
        Assert.Equal(2, (int)incident["priority"]!);
        var triage = (await admin.SendOkAsync(HttpMethod.Get, $"/api/v1/incidents/{id}/ai"))["triage"]!;
        Assert.Equal("AutoApplied", (string?)triage["status"]);
        var audit = await database.AuditAsync(tenant);
        Assert.Contains(audit, a => a.Action == "ai.triage.auto-applied" && a.ActorType == "System" && a.ActorId == "ai.triage");
    }

    [Fact]
    public async Task Frustrated_requesters_alert_the_groups_team_leads()
    {
        var tenant = await database.CreateTenantAsync("e5sent");
        await using var fake = await FakeAiProvider.StartAsync();
        fake.Respond = body => body.Contains("Task: sentiment.", StringComparison.Ordinal)
            ? body.Contains("unacceptable", StringComparison.Ordinal) ? """{"sentiment": "frustrated", "reason": "Third time asking."}""" : """{"sentiment": "neutral", "reason": "Plain report."}"""
            : null;
        using var admin = await AdminWithAiAsync(tenant, fake, AiFeatures.Sentiment);
        var lead = await database.CreateUserAsync(tenant, BuiltInRoles.TeamLead);
        var group = await admin.GroupAsync("Desk");
        await admin.SendOkAsync(HttpMethod.Put, $"/api/v1/groups/{group}/members/{lead.Id}", new { isLead = true });
        var requesterUser = await database.CreateUserAsync(tenant, BuiltInRoles.Requester);
        using var requester = _factory.ForTenant(tenant).As(requesterUser);
        var id = IncidentApi.IdOf(await admin.CreateAsync(new { title = "VPN keeps dropping", requesterId = requesterUser.Id, groupId = group }));
        await RunJobsAsync();
        Assert.Equal("Neutral", (string?)(await admin.SendOkAsync(HttpMethod.Get, $"/api/v1/incidents/{id}/ai"))["sentiment"]);

        await requester.SendOkAsync(HttpMethod.Post, $"/api/v1/incidents/{id}/comments", new { body = "This is unacceptable, I have asked three times." }, HttpStatusCode.Created);
        await RunJobsAsync();
        var insights = await admin.SendOkAsync(HttpMethod.Get, $"/api/v1/incidents/{id}/ai");
        Assert.Equal("Frustrated", (string?)insights["sentiment"]);
        Assert.NotNull(insights["frustratedSince"]);

        await using var db = TestData.AppContext(database, tenant);
        var alert = await db.Notifications.AsNoTracking().SingleAsync(n => n.EventType == "incident.requester-frustrated", Ct);
        Assert.Equal(lead.Id, alert.RecipientId);
        Assert.Equal($"/workspace/{id}", alert.LinkPath);
    }

    [Fact]
    public async Task Duplicates_are_shortlisted_judged_by_the_ai_and_linked_by_an_agent()
    {
        var tenant = await database.CreateTenantAsync("e5dup");
        await using var fake = await FakeAiProvider.StartAsync();
        fake.Respond = ByTask(new() { ["duplicates"] = """{"duplicates": [{"ref": "d1", "confidence": 88, "reason": "Same outage."}, {"ref": "d9", "confidence": 99}]}""" });
        using var admin = await AdminWithAiAsync(tenant, fake, AiFeatures.Duplicates);
        var first = IncidentApi.IdOf(await admin.CreateAsync(new { title = "Email server down in the head office", description = "Outlook cannot connect to the mail server." }));
        IncidentApi.IdOf(await admin.CreateAsync(new { title = "Broken chair", description = "The chair in room 4 is broken." }));
        await RunJobsAsync();
        var second = IncidentApi.IdOf(await admin.CreateAsync(new { title = "الخادم البريدي متوقف - email server down", description = "Outlook cannot connect to the mail server since 9." }));
        await RunJobsAsync();

        var prompt = fake.Received.Last().Body;
        Assert.Contains("Email server down in the head office", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("Broken chair", prompt, StringComparison.Ordinal);
        var duplicates = (await admin.SendOkAsync(HttpMethod.Get, $"/api/v1/incidents/{second}/ai"))["duplicates"]!.AsArray();
        var candidate = Assert.Single(duplicates)!;
        Assert.Equal(first, Guid.Parse((string)candidate["incident"]!["id"]!));

        var linked = await admin.SendOkAsync(HttpMethod.Post, $"/api/v1/incidents/{second}/ai/duplicates/{(string)candidate["id"]!}/link");
        Assert.Equal("Linked", (string?)linked["status"]);
        Assert.Equal(first, Guid.Parse((string)(await admin.GetIncidentAsync(second))["parent"]!["id"]!));
        Assert.Empty((await admin.SendOkAsync(HttpMethod.Get, $"/api/v1/incidents/{second}/ai"))["duplicates"]!.AsArray());
    }

    [Fact]
    public async Task Summaries_replies_and_translations_mask_personal_data_and_are_cached()
    {
        var tenant = await database.CreateTenantAsync("e5help");
        await using var fake = await FakeAiProvider.StartAsync();
        fake.Respond = body =>
            body.Contains("Task: summary.", StringComparison.Ordinal) ? $"- summary {fake.Received.Count}"
            : body.Contains("Task: reply.", StringComparison.Ordinal) ? """{"reply": "Please restart the VPN client as in the article.", "articles": ["KB-000001"]}"""
            : body.Contains("Task: translation.", StringComparison.Ordinal) ? "لا أستطيع الاتصال بالشبكة الافتراضية"
            : null;
        using var admin = await AdminWithAiAsync(tenant, fake, AiFeatures.Summary, AiFeatures.Reply, AiFeatures.Translation);
        var agentUser = await database.CreateUserAsync(tenant, BuiltInRoles.Agent);
        using var agent = _factory.ForTenant(tenant).As(agentUser);
        var requesterUser = await database.CreateUserAsync(tenant, BuiltInRoles.Requester);
        using var requester = _factory.ForTenant(tenant).As(requesterUser);
        var id = IncidentApi.IdOf(await agent.CreateAsync(new { title = "VPN cannot connect", description = $"Reported by {requesterUser.Email}, call 050 123 4567.", requesterId = requesterUser.Id }));
        await agent.SendOkAsync(HttpMethod.Post, $"/api/v1/incidents/{id}/comments", new { body = "Checked the account; it is fine.", visibility = "Internal" }, HttpStatusCode.Created);
        await PublishArticleAsync(tenant, "KB-000001", "VPN cannot connect", "Restart the VPN client.", ArticleVisibility.Internal);

        var summary = await agent.SendOkAsync(HttpMethod.Post, $"/api/v1/incidents/{id}/ai/summary", new { language = "ar" });
        Assert.Equal("ar", (string?)summary["language"]);
        var calls = fake.Received.Count;
        var sent = fake.Received.Last().Body;
        Assert.DoesNotContain(requesterUser.Email, sent, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("123 4567", sent, StringComparison.Ordinal);
        Assert.Contains("Agent (internal note)", sent, StringComparison.Ordinal);
        Assert.Contains("Arabic", sent, StringComparison.Ordinal);
        // Cached until the thread changes.
        Assert.Equal((string?)summary["text"], (string?)(await agent.SendOkAsync(HttpMethod.Post, $"/api/v1/incidents/{id}/ai/summary", new { language = "ar" }))["text"]);
        Assert.Equal(calls, fake.Received.Count);
        await requester.SendOkAsync(HttpMethod.Post, $"/api/v1/incidents/{id}/comments", new { body = "مازالت المشكلة قائمة" }, HttpStatusCode.Created);
        Assert.NotEqual((string?)summary["text"], (string?)(await agent.SendOkAsync(HttpMethod.Post, $"/api/v1/incidents/{id}/ai/summary", new { language = "ar" }))["text"]);
        Assert.Equal(HttpStatusCode.Forbidden, (await requester.SendAsync(HttpMethod.Post, $"/api/v1/incidents/{id}/ai/summary")).StatusCode);

        // The reply is grounded in a published article the agent may read.
        var reply = await agent.SendOkAsync(HttpMethod.Post, $"/api/v1/incidents/{id}/ai/reply", new { language = "en" });
        Assert.Equal("Please restart the VPN client as in the article.", (string?)reply["text"]);
        Assert.Equal("KB-000001", (string?)Assert.Single(reply["articles"]!.AsArray())!["number"]);
        Assert.Contains("Restart the VPN client.", fake.Received.Last().Body, StringComparison.Ordinal);

        // Translation: the requester translates their own incident; internal notes are not theirs to see.
        var translated = await requester.SendOkAsync(HttpMethod.Post, $"/api/v1/incidents/{id}/ai/translation", new { language = "ar" });
        Assert.False((bool)translated["cached"]!);
        Assert.True((bool)(await requester.SendOkAsync(HttpMethod.Post, $"/api/v1/incidents/{id}/ai/translation", new { language = "ar" }))["cached"]!);
        var comments = await agent.GetFromJsonAsync<JsonArray>($"/api/v1/incidents/{id}/comments", Ct);
        var note = comments!.Single(c => (string?)c!["visibility"] == "Internal")!;
        Assert.Equal(HttpStatusCode.NotFound, (await requester.SendAsync(HttpMethod.Post, $"/api/v1/incidents/{id}/ai/translation", new { language = "en", commentId = (string)note["id"]! })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await agent.SendAsync(HttpMethod.Post, $"/api/v1/incidents/{id}/ai/translation", new { language = "en", commentId = (string)note["id"]! })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await agent.SendAsync(HttpMethod.Post, $"/api/v1/incidents/{id}/ai/translation", new { language = "fr" })).StatusCode);
        var draft = await agent.SendOkAsync(HttpMethod.Post, "/api/v1/ai/translate", new { text = "I cannot reach the VPN", language = "ar", incidentId = id });
        Assert.Equal("لا أستطيع الاتصال بالشبكة الافتراضية", (string?)draft["text"]);
        Assert.Equal(HttpStatusCode.Forbidden, (await requester.SendAsync(HttpMethod.Post, "/api/v1/ai/translate", new { text = "x", language = "ar" })).StatusCode);
    }

    [Fact]
    public async Task Ai_features_that_are_off_queue_nothing_and_answer_ai_off()
    {
        var tenant = await database.CreateTenantAsync("e5off");
        using var admin = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.TenantAdmin);
        var id = IncidentApi.IdOf(await admin.CreateAsync(new { title = "Screen flickers" }));
        await using (var db = TestData.AppContext(database, tenant))
        {
            Assert.False(await db.AiJobs.AnyAsync(Ct));
        }

        foreach (var path in new[] { "summary", "reply", "triage" })
        {
            var response = await admin.SendAsync(HttpMethod.Post, $"/api/v1/incidents/{id}/ai/{path}");
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Assert.Equal("ai-off", (await response.ProblemAsync()).Code);
        }

        var insights = await admin.SendOkAsync(HttpMethod.Get, $"/api/v1/incidents/{id}/ai");
        Assert.Empty(insights["available"]!.AsArray());
    }

    [Fact]
    public async Task The_virtual_agent_answers_from_external_articles_and_hands_over_or_leaves_a_message()
    {
        var tenant = await database.CreateTenantAsync("e5va");
        await using var fake = await FakeAiProvider.StartAsync();
        fake.Respond = body => !body.Contains("Task: virtualAgent.", StringComparison.Ordinal) ? null
            : body.Contains("speak to a person", StringComparison.Ordinal) ? """{"answer": "I will pass you to a colleague.", "articles": [], "handover": true}"""
            : """{"answer": "Reset it from the self-service page, as article KB-000007 explains.", "articles": ["KB-000007", "KB-000008"], "handover": false}""";
        using var admin = await AdminWithAiAsync(tenant, fake, AiFeatures.VirtualAgent);
        await PublishArticleAsync(tenant, "KB-000007", "Reset your password", "Use the self-service page.", ArticleVisibility.External);
        await PublishArticleAsync(tenant, "KB-000008", "Internal password tool", "Use the admin console.", ArticleVisibility.Internal);
        var group = await admin.GroupAsync("Desk");
        var widget = (await admin.SendOkAsync(HttpMethod.Post, "/api/v1/chat/widgets",
            new { name = new { en = "Help" }, groupId = group, onPortal = true, virtualAgent = true }, HttpStatusCode.Created)).Id();
        using var visitor = _factory.ForTenant(tenant);

        // Nobody is online, but the virtual agent answers.
        var info = await visitor.SendOkAsync(HttpMethod.Get, $"/api/v1/chat/widgets/{widget}/public");
        Assert.False((bool)info["online"]!);
        Assert.True((bool)info["virtualAgent"]!);
        var start = await visitor.SendOkAsync(HttpMethod.Post, $"/api/v1/chat/widgets/{widget}/sessions",
            new { name = "Visitor", email = $"v-{Guid.NewGuid():N}@visitors.example.org", language = "en", message = "How do I reset my password?" }, HttpStatusCode.Created);
        var session = (string)start["sessionId"]!;
        var token = (string)start["token"]!;
        visitor.DefaultRequestHeaders.Add(ChatEndpoints.TokenHeader, token);
        var chat = await visitor.SendOkAsync(HttpMethod.Get, $"/api/v1/chat/sessions/{session}");
        Assert.Equal("Assistant", (string?)chat["state"]);
        var answer = chat["messages"]!.AsArray().Last()!;
        Assert.Equal("Assistant", (string?)answer["sender"]);
        // Visitors are answered from external articles only: the internal one was never sent or cited.
        Assert.Equal(["KB-000007"], answer["citations"]!.AsArray().Select(c => (string)c!["number"]!));
        var prompt = fake.Received.Last().Body;
        Assert.Contains("Use the self-service page.", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("admin console", prompt, StringComparison.Ordinal);

        // Asking for a person with nobody online: refused, then left as a message (an incident with the conversation).
        await visitor.SendOkAsync(HttpMethod.Post, $"/api/v1/chat/sessions/{session}/messages", new { body = "I want to speak to a person" }, HttpStatusCode.Created);
        Assert.Equal("Assistant", (string?)(await visitor.SendOkAsync(HttpMethod.Get, $"/api/v1/chat/sessions/{session}"))["state"]);
        var refused = await visitor.SendAsync(HttpMethod.Post, $"/api/v1/chat/sessions/{session}/handover");
        Assert.Equal("chat-offline", (await refused.ProblemAsync()).Code);
        var left = await visitor.SendOkAsync(HttpMethod.Post, $"/api/v1/chat/sessions/{session}/handover", new { leaveMessage = true });
        Assert.Equal("Ended", (string?)left["state"]);
        await using var db = TestData.AppContext(database, tenant);
        var ticket = await db.Tickets.AsNoTracking().SingleAsync(t => t.Channel == TicketChannel.Chat, Ct);
        Assert.Contains("Virtual agent: Reset it from the self-service page", ticket.Description, StringComparison.Ordinal);
        Assert.Equal(group, ticket.GroupId);
    }

    [Fact]
    public async Task The_virtual_agent_hands_over_to_an_available_agent_with_the_conversation()
    {
        var tenant = await database.CreateTenantAsync("e5vb");
        await using var fake = await FakeAiProvider.StartAsync();
        fake.Respond = ByTask(new() { ["virtualAgent"] = """{"answer": "Let me find a colleague.", "articles": [], "handover": true}""" });
        using var admin = await AdminWithAiAsync(tenant, fake, AiFeatures.VirtualAgent, AiFeatures.Translation);
        var agentUser = await database.CreateUserAsync(tenant, BuiltInRoles.Agent);
        using var agent = _factory.ForTenant(tenant).As(agentUser);
        var group = await admin.GroupAsync("Desk", agentUser);
        await agent.SendOkAsync(HttpMethod.Put, "/api/v1/me/availability", new { available = true });
        var widget = (await admin.SendOkAsync(HttpMethod.Post, "/api/v1/chat/widgets",
            new { name = new { en = "Help" }, groupId = group, onPortal = true, virtualAgent = true }, HttpStatusCode.Created)).Id();
        using var visitor = _factory.ForTenant(tenant);
        var start = await visitor.SendOkAsync(HttpMethod.Post, $"/api/v1/chat/widgets/{widget}/sessions",
            new { name = "Visitor", email = $"v-{Guid.NewGuid():N}@visitors.example.org", language = "ar", message = "ابي اكلم موظف لو سمحت" }, HttpStatusCode.Created);
        var session = (string)start["sessionId"]!;

        var queue = await agent.SendOkAsync(HttpMethod.Get, "/api/v1/chat/queue");
        Assert.Equal(session, (string?)Assert.Single(queue["waiting"]!.AsArray())!["id"]);
        var transcript = await agent.SendOkAsync(HttpMethod.Get, $"/api/v1/chat/sessions/{session}/agent");
        var messages = transcript["messages"]!.AsArray();
        Assert.Contains(messages, m => (string?)m!["sender"] == "Visitor");
        Assert.Contains(messages, m => (string?)m!["sender"] == "Assistant");
        Assert.Contains(messages, m => (string?)m!["event"] == "handover");

        fake.Respond = ByTask(new() { ["translation"] = "I want to talk to an employee please" });
        var visitorMessage = messages.First(m => (string?)m!["sender"] == "Visitor")!;
        var translation = await agent.SendOkAsync(HttpMethod.Post, $"/api/v1/chat/sessions/{session}/messages/{(string)visitorMessage["id"]!}/translation", new { language = "en" });
        Assert.Equal("I want to talk to an employee please", (string?)translation["text"]);
    }

    [Fact]
    public async Task The_virtual_agent_uses_read_only_mcp_tools_and_hands_over_for_tools_that_change_data()
    {
        var tenant = await database.CreateTenantAsync("e5vm");
        await using var fake = await FakeAiProvider.StartAsync();
        await using var hr = await TestMcpServer.StartAsync();
        using var admin = await AdminWithAiAsync(tenant, fake, AiFeatures.VirtualAgent);
        var server = (await admin.SendOkAsync(HttpMethod.Post, "/api/v1/ai/mcp-servers", new { name = "HR system", url = hr.Url.ToString() }, HttpStatusCode.Created)).Id();
        await admin.SendOkAsync(HttpMethod.Post, $"/api/v1/ai/mcp-servers/{server}/refresh");
        await admin.SendOkAsync(HttpMethod.Put, $"/api/v1/ai/mcp-servers/{server}/tools", new
        {
            tools = new[] { new { name = "lookup_employee", enabled = true, changesData = false }, new { name = "update_address", enabled = true, changesData = true } },
        });
        var agentUser = await database.CreateUserAsync(tenant, BuiltInRoles.Agent);
        using var agent = _factory.ForTenant(tenant).As(agentUser);
        var group = await admin.GroupAsync("Desk", agentUser);
        await agent.SendOkAsync(HttpMethod.Put, "/api/v1/me/availability", new { available = true });
        var widget = (await admin.SendOkAsync(HttpMethod.Post, "/api/v1/chat/widgets",
            new { name = new { en = "Help" }, groupId = group, onPortal = true, virtualAgent = true }, HttpStatusCode.Created)).Id();
        using var visitor = _factory.ForTenant(tenant);

        // A look-up with the read-only tool, then the answer.
        fake.Script.Enqueue("""{"tool":"t1","arguments":{"email":"[EMAIL_1]"}}""");
        fake.Script.Enqueue("""{"answer":"Mariam works in Finance.","articles":[],"handover":false}""");
        var start = await visitor.SendOkAsync(HttpMethod.Post, $"/api/v1/chat/widgets/{widget}/sessions",
            new { name = "Visitor", email = $"v-{Guid.NewGuid():N}@visitors.example.org", language = "en", message = "Which department is mariam@example.test in?" }, HttpStatusCode.Created);
        var session = (string)start["sessionId"]!;
        visitor.DefaultRequestHeaders.Add(ChatEndpoints.TokenHeader, (string)start["token"]!);
        var chat = await visitor.SendOkAsync(HttpMethod.Get, $"/api/v1/chat/sessions/{session}");
        Assert.Equal("Mariam works in Finance.", (string?)chat["messages"]!.AsArray().Last()!["body"]);
        Assert.Equal("lookup_employee", Assert.Single(hr.Calls).Tool);

        // A change in the other system is never run from the chat: a person takes over.
        fake.Script.Enqueue("""{"tool":"t2","arguments":{"employee":"E-17","address":"Al Reem Island"}}""");
        await visitor.SendOkAsync(HttpMethod.Post, $"/api/v1/chat/sessions/{session}/messages", new { body = "Change my address to Al Reem Island" }, HttpStatusCode.Created);
        Assert.Single(hr.Calls);
        chat = await visitor.SendOkAsync(HttpMethod.Get, $"/api/v1/chat/sessions/{session}");
        Assert.Equal("Waiting", (string?)chat["state"]);
        Assert.Contains(chat["messages"]!.AsArray(), m => (string?)m!["event"] == "handover");
    }

    [Fact]
    public async Task Article_drafts_from_resolved_incidents_are_bilingual_drafts_without_personal_data()
    {
        var tenant = await database.CreateTenantAsync("e5art");
        await using var fake = await FakeAiProvider.StartAsync();
        var requesterUser = await database.CreateUserAsync(tenant, BuiltInRoles.Requester);
        fake.Respond = body => body.Contains("Task: articleDraft.", StringComparison.Ordinal)
            ? """{"title": {"en": "Fix the VPN for [PERSON_1]", "ar": "إصلاح الشبكة الافتراضية"}, "summary": {"en": "Restart it.", "ar": "أعد تشغيلها."}, "body": {"en": "## Problem\n\nVPN.\n\n## Solution\n\n1. Restart.", "ar": "## المشكلة\n\nالشبكة.\n\n## الحل\n\n1. أعد التشغيل."}, "keywords": ["vpn"]}"""
            : null;
        using var admin = await AdminWithAiAsync(tenant, fake, AiFeatures.ArticleDrafts);
        var id = IncidentApi.IdOf(await admin.CreateAsync(new { title = "VPN fails", requesterId = requesterUser.Id }));
        Assert.Equal(HttpStatusCode.OK, (await admin.MoveAsync(id, "Resolved", notes: "Restarted the client.")).StatusCode);

        var article = await admin.SendOkAsync(HttpMethod.Post, $"/api/v1/incidents/{id}/knowledge-article?draftWithAi=true", expected: HttpStatusCode.Created);
        var json = article.ToJsonString(new System.Text.Json.JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        Assert.DoesNotContain(requesterUser.Email, json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(requesterUser.Email, fake.Received.Last().Body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("إصلاح الشبكة الافتراضية", json, StringComparison.Ordinal);
        Assert.Contains("Restart.", json, StringComparison.Ordinal);
        Assert.Contains("Drafted by AI from", json, StringComparison.Ordinal);
        Assert.Equal("Draft", (string?)article["article"]!["state"]);
    }

    private async Task PublishArticleAsync(Tenant tenant, string number, string title, string body, ArticleVisibility visibility)
    {
        await using var db = TestData.AppContext(database, tenant);
        var now = DateTimeOffset.UtcNow;
        var article = new KbArticle { Number = number, Sequence = int.Parse(number[3..], System.Globalization.CultureInfo.InvariantCulture), Visibility = visibility, CreatedAt = now, UpdatedAt = now };
        var version = new KbArticleVersion
        {
            ArticleId = article.Id,
            Number = 1,
            Title = new LocalizedText(title, null),
            Body = new LocalizedText(body, null),
            Visibility = visibility,
            State = ArticleVersionState.Published,
            PublishedAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.KbArticles.Add(article);
        db.KbArticleVersions.Add(version);
        await db.SaveChangesAsync(Ct);
        article.PublishedVersionId = version.Id;
        article.State = ArticleState.Published;
        article.PublishedAt = now;
        await db.SaveChangesAsync(Ct);
    }
}
