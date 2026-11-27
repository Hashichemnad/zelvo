using System.Net;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Zelvo.Ai;
using Zelvo.Data.Channels;
using Zelvo.Data.Identity;
using Zelvo.Data.Tenancy;

namespace Zelvo.Api.Tests;

/// <summary>Plain-language rules (module 20, task K1) against PostgreSQL, a fake AI provider and a fake WhatsApp.</summary>
public sealed class PlainLanguageRuleTests(DatabaseFixture database) : IAsyncDisposable
{
    private readonly FakeMessagingClients _providers = new();

    private ApiFactory? _factory;

    private ApiFactory Factory => _factory ??= ApiFactory.ForDatabase(database).WithLocalAi().WithSetting("Ai:AllowInsecureHttp", "true")
        .WithMessaging(_providers).WithEmailSender(new CapturingEmailSender());

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask DisposeAsync() => _factory?.DisposeAsync() ?? ValueTask.CompletedTask;

    private const string Story = "When a VIP raises a critical incident, notify the IT manager on WhatsApp";

    private static string Answer(string vipRef, string groupRef) => """
        {
          "name": {"en": "VIP critical incidents to the IT manager", "ar": "البلاغات الحرجة لكبار العملاء إلى مدير تقنية المعلومات"},
          "trigger": "TicketCreated",
          "recordType": "Incident",
          "match": "All",
          "conditions": [
            {"field": "requesterCustomerGroup", "op": "in", "value": ["VIPREF"]},
            {"field": "priority", "op": "eq", "value": 1},
            {"field": "group", "op": "in", "value": ["g99"]},
            {"field": "requesterMood", "op": "in", "value": ["angry"]}
          ],
          "actions": [
            {"type": "notify", "recipients": ["groupLeads:GROUPREF", "user:4f1c6e0a-0000-4000-8000-000000000001"], "channels": ["WhatsApp", "Pager"], "email": false,
             "subject": {"en": "VIP critical incident {{ticket.number}}", "ar": "بلاغ حرج لكبار العملاء {{ticket.number}}"},
             "body": {"en": "{{requester.name}} raised {{ticket.title}}.", "ar": "قدّم {{requester.name}} البلاغ {{ticket.title}}."}},
            {"type": "approval", "levels": []}
          ],
          "unmapped": ["Only during working hours"]
        }
        """.Replace("VIPREF", vipRef, StringComparison.Ordinal).Replace("GROUPREF", groupRef, StringComparison.Ordinal);

    [Fact]
    public async Task A_described_rule_becomes_a_draft_that_runs_only_after_approval_and_notifies_on_WhatsApp()
    {
        var tenant = await database.CreateTenantAsync("k1pl");
        await using var fake = await FakeAiProvider.StartAsync();
        using var admin = await Factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.TenantAdmin);

        // Off until set up: the screen says why.
        var off = await admin.SendOkAsync(HttpMethod.Get, "/api/v1/workflow-rules/plain-language");
        Assert.Equal((false, "ai-off"), ((bool)off["available"]!, (string?)off["reason"]));
        using (var refused = await admin.SendAsync(HttpMethod.Post, "/api/v1/workflow-rules/plain-language", new { text = Story }))
        {
            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        }

        var provider = (await admin.SendOkAsync(HttpMethod.Post, "/api/v1/ai/providers",
            new { name = "Local model", kind = "OpenAICompatible", endpoint = new Uri(fake.Url, "v1").ToString() }, HttpStatusCode.Created)).Id();
        await admin.SendOkAsync(HttpMethod.Put, $"/api/v1/ai/features/{AiFeatures.RuleBuilder}", new { enabled = true, providerId = provider, model = "test-model" });
        Assert.True((bool)(await admin.SendOkAsync(HttpMethod.Get, "/api/v1/workflow-rules/plain-language"))["available"]!);

        // The tenant's lists: a VIP customer group (B3) and the IT desk with its team lead, who gets WhatsApp.
        var vipUser = await database.CreateUserAsync(tenant, BuiltInRoles.Requester);
        var otherUser = await database.CreateUserAsync(tenant, BuiltInRoles.Requester);
        var leadUser = await database.CreateUserAsync(tenant, BuiltInRoles.TeamLead);
        var agentUser = await database.CreateUserAsync(tenant, BuiltInRoles.Agent);
        using var agent = Factory.ForTenant(tenant).As(agentUser);
        await database.ExecuteAsOwnerAsync("UPDATE users SET phone = '+971501110000', notify_whats_app = true WHERE id = @u", ("u", leadUser.Id));
        var vip = (await admin.SendOkAsync(HttpMethod.Post, "/api/v1/sla/customer-groups", new { name = new { en = "VIP", ar = "كبار العملاء" } }, HttpStatusCode.Created)).Id();
        await admin.SendOkAsync(HttpMethod.Put, $"/api/v1/sla/customer-groups/{vip}/members/{vipUser.Id}");
        var desk = await admin.GroupAsync("IT service desk", agentUser);
        await admin.SendOkAsync(HttpMethod.Put, $"/api/v1/groups/{desk}/members/{leadUser.Id}", new { isLead = true });
        await admin.SendOkAsync(HttpMethod.Post, "/api/v1/messaging/channels", new
        {
            name = "IT on WhatsApp", kind = "WhatsApp", provider = "WhatsAppCloud", accountId = "1098765432", sender = "+97142345678",
            credential = MessagingApi.Secret("wa"), appSecret = MessagingApi.Secret("app"), groupId = desk,
        }, HttpStatusCode.Created);

        fake.Respond = body => body.Contains("Task: ruleBuilder.", StringComparison.Ordinal) ? Answer("v1", "g1") : null;
        var draft = await admin.SendOkAsync(HttpMethod.Post, "/api/v1/workflow-rules/plain-language", new { text = Story }, HttpStatusCode.Created);

        // The lists went to the AI by reference.
        var prompt = fake.Received.Last().Body;
        Assert.Contains("v1: VIP / ", prompt, StringComparison.Ordinal);
        Assert.Contains("g1: IT service desk", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain(vip.ToString(), prompt, StringComparison.Ordinal);

        // Only what fits the builder was taken; the rest is listed.
        Assert.True((bool)draft["draft"]!);
        Assert.Equal(Story, (string?)draft["sourceText"]);
        Assert.Equal("en", (string?)draft["sourceLanguage"]);
        var conditions = draft["conditions"]!.AsArray();
        Assert.Equal(["requesterCustomerGroup", "priority"], conditions.Select(c => (string)c!["field"]!));
        Assert.Equal(vip.ToString(), (string)conditions[0]!["value"]![0]!);
        var notify = draft["actions"]!.AsArray().Single()!;
        Assert.Equal($"groupLeads:{desk}", (string)notify["recipients"]!.AsArray().Single()!);
        Assert.Equal("WhatsApp", (string)notify["channels"]!.AsArray().Single()!);
        var unmapped = draft["unmapped"]!.AsArray().Select(u => (string)u!).ToList();
        Assert.Contains("Only during working hours", unmapped);
        Assert.Contains(unmapped, u => u.Contains("g99", StringComparison.Ordinal));
        Assert.Contains(unmapped, u => u.Contains("requesterMood", StringComparison.Ordinal));
        Assert.Contains(unmapped, u => u.Contains("Pager", StringComparison.Ordinal));
        Assert.Contains(unmapped, u => u.Contains("approval", StringComparison.Ordinal));
        Assert.Contains(unmapped, u => u.Contains("user:", StringComparison.Ordinal));

        // A draft never runs.
        var first = IncidentApi.IdOf(await agent.CreateAsync(new { title = "Minister's laptop will not start", requesterId = vipUser.Id, impact = 1, urgency = 1 }));
        await using (var db = TestData.AppContext(database, tenant))
        {
            Assert.False(await db.Notifications.AnyAsync(n => n.EventType == "workflow.rule" && n.TicketId == first, Ct));
        }

        // "Try the rules" can include drafts.
        var number = (string?)(await agent.GetIncidentAsync(first))["number"];
        var tried = (await admin.SendOkAsync(HttpMethod.Post, "/api/v1/workflow-rules/test", new { number, trigger = "TicketCreated", includeDrafts = true }))["items"]!.AsArray();
        Assert.True((bool)tried.Single()!["holds"]! && (bool)tried.Single()!["draft"]!);
        Assert.Empty((await admin.SendOkAsync(HttpMethod.Post, "/api/v1/workflow-rules/test", new { number, trigger = "TicketCreated" }))["items"]!.AsArray());

        // The administrator approves; the original text is audited.
        var approved = await admin.SendOkAsync(HttpMethod.Post, $"/api/v1/workflow-rules/{draft.Id()}/approve");
        Assert.False((bool)approved["draft"]!);
        Assert.NotNull(approved["approvedAt"]);
        using (var again = await admin.SendAsync(HttpMethod.Post, $"/api/v1/workflow-rules/{draft.Id()}/approve"))
        {
            Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        }

        var audit = await database.AuditAsync(tenant);
        Assert.Contains(audit, a => a.Action == "workflows.rule.drafted" && a.Metadata!.Contains(Story, StringComparison.Ordinal));
        Assert.Contains(audit, a => a.Action == "workflows.rule.approved" && a.Metadata!.Contains(Story, StringComparison.Ordinal));

        // Live: a VIP's P1 tells the IT desk's team lead in the app and on WhatsApp; others' P1s do not.
        var vipP1 = IncidentApi.IdOf(await agent.CreateAsync(new { title = "Email down for the minister's office", requesterId = vipUser.Id, impact = 1, urgency = 1 }));
        var otherP1 = IncidentApi.IdOf(await agent.CreateAsync(new { title = "Email down", requesterId = otherUser.Id, impact = 1, urgency = 1 }));
        var vipP3 = IncidentApi.IdOf(await agent.CreateAsync(new { title = "Mouse is slow", requesterId = vipUser.Id, impact = 3, urgency = 3 }));
        await Factory.DeliverNotificationsAsync(tenant);
        await using (var db = TestData.AppContext(database, tenant))
        {
            var sent = await db.Notifications.AsNoTracking().Where(n => n.EventType == "workflow.rule").ToListAsync(Ct);
            Assert.Equal((vipP1, leadUser.Id), (sent.Single().TicketId, sent.Single().RecipientId));
            Assert.DoesNotContain(sent, n => n.TicketId == otherP1 || n.TicketId == vipP3);
            var whatsApp = await db.ChannelMessages.AsNoTracking().Where(m => m.Direction == MessageDirection.Out && m.TicketId == vipP1).SingleAsync(Ct);
            Assert.Equal("+971501110000", whatsApp.Address);
            Assert.Contains("VIP critical incident", whatsApp.Text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task A_description_without_anything_the_builder_can_do_is_refused_with_the_reasons()
    {
        var tenant = await database.CreateTenantAsync("k1no");
        await using var fake = await FakeAiProvider.StartAsync();
        fake.Answer = """{"trigger": "TicketCreated", "conditions": [], "actions": [{"type": "sendFlowers"}], "unmapped": ["Send flowers to the requester"]}""";
        using var admin = await Factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.TenantAdmin);
        var provider = (await admin.SendOkAsync(HttpMethod.Post, "/api/v1/ai/providers",
            new { name = "Local model", kind = "OpenAICompatible", endpoint = new Uri(fake.Url, "v1").ToString() }, HttpStatusCode.Created)).Id();
        await admin.SendOkAsync(HttpMethod.Put, $"/api/v1/ai/features/{AiFeatures.RuleBuilder}", new { enabled = true, providerId = provider, model = "test-model" });

        using var refused = await admin.SendAsync(HttpMethod.Post, "/api/v1/workflow-rules/plain-language", new { text = "أرسل الورود إلى مقدّم الطلب" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        var problem = await refused.JsonAsync();
        Assert.Equal("rule-not-understood", (string?)problem["code"]);
        Assert.Contains(problem["unmapped"]!.AsArray(), u => ((string)u!).Contains("sendFlowers", StringComparison.Ordinal));
        Assert.Empty(await admin.ArrayAsync("/api/v1/workflow-rules"));
        Assert.Contains(await database.AuditAsync(tenant), a => a.Action == "workflows.rule.not-understood");

        // Requesters and agents never reach it.
        var agentUser = await database.CreateUserAsync(tenant, BuiltInRoles.Agent);
        using var agent = Factory.ForTenant(tenant).As(agentUser);
        using var forbidden = await agent.SendAsync(HttpMethod.Post, "/api/v1/workflow-rules/plain-language", new { text = "anything" });
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
    }
}
