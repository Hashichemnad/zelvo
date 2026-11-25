using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Zelvo.Channels;
using Zelvo.Channels.Messaging;
using Zelvo.Data.Channels;
using Zelvo.Data.Identity;
using Zelvo.Data.Tickets;
using Zelvo.Tickets;

namespace Zelvo.Api.Tests;

/// <summary>WhatsApp, SMS and Microsoft Teams (module 6, task E3) against PostgreSQL, with the providers in memory.</summary>
public sealed class MessagingChannelTests : IAsyncDisposable
{
    private readonly DatabaseFixture _database;
    private readonly FakeMessagingClients _providers = new();
    private readonly ApiFactory _factory;

    public MessagingChannelTests(DatabaseFixture database)
    {
        _database = database;
        _factory = ApiFactory.ForDatabase(database).WithMessaging(_providers).WithEmailSender(new CapturingEmailSender());
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    private async Task<int> DeliverAsync(Zelvo.Data.Tenancy.Tenant tenant)
    {
        await _factory.DeliverNotificationsAsync(tenant);
        return await _factory.RunMessagingAsync(tenant);
    }

    [Fact]
    public async Task A_WhatsApp_message_becomes_an_incident_and_the_conversation_and_updates_stay_on_WhatsApp()
    {
        var tenant = await _database.CreateTenantAsync("e3wa");
        using var admin = await _factory.ForTenant(tenant).SignedInAsync(_database, tenant, BuiltInRoles.TenantAdmin);
        var agentUser = await _database.CreateUserAsync(tenant, BuiltInRoles.Agent);
        using var agent = _factory.ForTenant(tenant).As(agentUser);
        using var meta = _factory.ForTenant(tenant);
        var group = await admin.GroupAsync("Service desk", agentUser);
        var token = MessagingApi.Secret("wa");
        var appSecret = MessagingApi.Secret("app");
        var created = await admin.SendOkAsync(HttpMethod.Post, "/api/v1/messaging/channels", new
        {
            name = "Customer care",
            kind = "WhatsApp",
            provider = "WhatsAppCloud",
            accountId = "1098765432",
            sender = "+97142345678",
            credential = token,
            appSecret,
            groupId = group,
        }, HttpStatusCode.Created);
        var channel = created.Id();

        // Credentials never come back; the webhook address names the tenant and channel.
        Assert.True((bool)created["credentialSet"]! && (bool)created["appSecretSet"]!);
        Assert.DoesNotContain(token, created.ToJsonString(), StringComparison.Ordinal);
        Assert.DoesNotContain(appSecret, created.ToJsonString(), StringComparison.Ordinal);
        Assert.EndsWith($"/hooks/{tenant.Slug}/whatsapp/{channel}", (string)created["webhookUrl"]!, StringComparison.Ordinal);
        var verify = (string)created["verifyToken"]!;

        // Meta registers the webhook with the verify token; anything unsigned is refused.
        using (var echo = await meta.GetAsync(new Uri($"/api/v1/hooks/whatsapp/{channel}?hub.mode=subscribe&hub.verify_token={verify}&hub.challenge=1158201444", UriKind.Relative), Ct))
        {
            Assert.Equal("1158201444", await echo.Content.ReadAsStringAsync(Ct));
        }

        Assert.Equal(HttpStatusCode.Forbidden,
            await meta.GetAsync(new Uri($"/api/v1/hooks/whatsapp/{channel}?hub.mode=subscribe&hub.verify_token=wrong&hub.challenge=1", UriKind.Relative), Ct).StatusAsync());
        var first = MessagingApi.WhatsAppMessage("1098765432", "971501234567", "wamid.1", MessagingApi.TextContent("The printer on floor 2 is jammed"));
        Assert.Equal(HttpStatusCode.Unauthorized, await meta.WhatsAppAsync(channel, first, null).StatusAsync());
        Assert.Equal(HttpStatusCode.Unauthorized, await meta.WhatsAppAsync(channel, first, MessagingApi.Secret("other")).StatusAsync());

        // 1. A stranger writes: a requester account for the number and an incident in the channel's group.
        Assert.Equal(HttpStatusCode.OK, await meta.WhatsAppAsync(channel, first, appSecret).StatusAsync());
        Assert.Equal(HttpStatusCode.OK, await meta.WhatsAppAsync(channel, first, appSecret).StatusAsync());
        Ticket ticket;
        await using (var db = TestData.AppContext(_database, tenant))
        {
            ticket = await db.Tickets.Include(t => t.Requester).SingleAsync(t => t.Channel == TicketChannel.WhatsApp, Ct);
            Assert.Equal(("The printer on floor 2 is jammed", group), (ticket.Title, ticket.GroupId));
            Assert.Equal(("+971501234567", "Layla Ahmed", true), (ticket.Requester!.Phone, ticket.Requester.DisplayName, ticket.Requester.NotifyWhatsApp));
            Assert.StartsWith(ContactAccounts.SubjectPrefix, ticket.Requester.Subject, StringComparison.Ordinal);
            Assert.Single(await db.ChannelMessages.Where(m => m.Direction == MessageDirection.In).ToListAsync(Ct));
        }

        // 2. "Received" goes back on WhatsApp as text (she wrote within 24 hours), with the number.
        await DeliverAsync(tenant);
        var received = Assert.Single(_providers.SentTo("+971501234567"));
        Assert.Equal(ChannelMessageKind.Text, received.Message.Kind);
        Assert.Contains(ticket.Number, received.Message.Text, StringComparison.Ordinal);

        // 3. Her next message is added to the incident; a voice note is kept (transcription is off).
        await meta.WhatsAppAsync(channel, MessagingApi.WhatsAppMessage("1098765432", "971501234567", "wamid.2", MessagingApi.TextContent("It is the HP printer near the lift")), appSecret);
        _providers.Media["media-1"] = new MessagingMedia([.. "OggS"u8.ToArray(), .. new byte[64]], "audio/ogg; codecs=opus");
        await meta.WhatsAppAsync(channel, MessagingApi.WhatsAppMessage("1098765432", "971501234567", "wamid.3",
            new JsonObject { ["type"] = "audio", ["audio"] = new JsonObject { ["id"] = "media-1", ["mime_type"] = "audio/ogg; codecs=opus", ["voice"] = true } }), appSecret);
        var comments = (await agent.SendOkAsync(HttpMethod.Get, $"/api/v1/incidents/{ticket.Id}/comments"))["items"]!.AsArray();
        Assert.Equal(2, comments.Count);
        Assert.Equal(("WhatsApp", "It is the HP printer near the lift", false), ((string?)comments[0]!["source"], (string?)comments[0]!["body"], (bool)comments[0]!["byStaff"]!));
        Assert.Equal(("Off", 1), ((string?)comments[1]!["voiceNote"]!["status"], comments[1]!["attachments"]!.AsArray().Count));
        Assert.Equal("audio/ogg", await _database.ScalarAsync<string>("SELECT content_type FROM ticket_attachments WHERE ticket_id = @t", ("t", ticket.Id)));

        // 4. The agent's public reply goes out on WhatsApp as the reply itself; delivery reports follow it.
        _providers.Sent.Clear();
        await agent.SendOkAsync(HttpMethod.Post, $"/api/v1/incidents/{ticket.Id}/comments", new { body = "An engineer is on the way.", visibility = "Public" }, HttpStatusCode.Created);
        await DeliverAsync(tenant);
        var reply = Assert.Single(_providers.SentTo("+971501234567"));
        Assert.Equal($"{ticket.Number}: An engineer is on the way.", reply.Message.Text);
        await meta.WhatsAppAsync(channel, MessagingApi.WhatsAppStatus("1098765432", "out-2", "read"), appSecret);
        comments = (await agent.SendOkAsync(HttpMethod.Get, $"/api/v1/incidents/{ticket.Id}/comments"))["items"]!.AsArray();
        Assert.Equal("Read", (string?)comments[^1]!["messageDelivery"]);

        // 5. More than 24 hours later WhatsApp needs a template: none named → not sent and logged; with one → the template.
        await _database.ExecuteAsOwnerAsync("UPDATE messaging_contacts SET last_inbound_at = now() - interval '2 days' WHERE channel_id = @c", ("c", channel));
        _providers.Sent.Clear();
        await agent.PostAsJsonAsync(new Uri($"/api/v1/incidents/{ticket.Id}/transitions", UriKind.Relative), new { state = "InProgress" }, Ct);
        await agent.PostAsJsonAsync(new Uri($"/api/v1/incidents/{ticket.Id}/transitions", UriKind.Relative), new { state = "OnHold", onHoldReason = "awaiting-requester" }, Ct);
        await DeliverAsync(tenant);
        Assert.Empty(_providers.Sent);
        var log = (await admin.SendOkAsync(HttpMethod.Get, $"/api/v1/messaging/channels/{channel}/log?direction=Out&status=Failed"))["items"]!.AsArray();
        Assert.Equal(("no-template", "+9715•••••567"), ((string?)log[0]!["reason"], (string?)log[0]!["address"]));
        await admin.SendOkAsync(HttpMethod.Patch, $"/api/v1/messaging/channels/{channel}", new { whatsApp = new { defaultTemplate = "ticket_update", englishCode = "en_US", arabicCode = "ar" } });
        await agent.PostAsJsonAsync(new Uri($"/api/v1/incidents/{ticket.Id}/transitions", UriKind.Relative), new { state = "Resolved", resolutionNotes = "Paper removed." }, Ct);
        await DeliverAsync(tenant);
        var template = Assert.Single(_providers.Sent);
        Assert.Equal(ChannelMessageKind.Template, template.Message.Kind);
        Assert.Equal(("ticket_update", "en_US"), ((string?)JsonNode.Parse(template.Message.Payload!)!["name"], (string?)JsonNode.Parse(template.Message.Payload!)!["language"]));

        // Settings changes are audited without the secrets.
        Assert.Contains((await _database.AuditAsync(tenant)).Select(a => a.Action), a => a == "channels.messaging.credential-changed");
        Assert.DoesNotContain(await _database.ScalarAsync<string>("SELECT string_agg(coalesce(changes::text, ''), ' ') FROM audit_events WHERE tenant_id = @t", ("t", tenant.Id)),
            token, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Text_messages_come_in_through_Twilio_and_a_generic_gateway_only_with_their_proof()
    {
        var tenant = await _database.CreateTenantAsync("e3sms");
        using var admin = await _factory.ForTenant(tenant).SignedInAsync(_database, tenant, BuiltInRoles.TenantAdmin);
        using var outside = _factory.ForTenant(tenant);
        var authToken = MessagingApi.Secret("tw");
        var twilio = (await admin.SendOkAsync(HttpMethod.Post, "/api/v1/messaging/channels", new
        {
            name = "SMS",
            kind = "Sms",
            provider = "Twilio",
            accountId = "AC" + Guid.NewGuid().ToString("N"),
            sender = "+97142345678",
            credential = authToken,
        }, HttpStatusCode.Created)).Id();
        var fields = new Dictionary<string, string>(StringComparer.Ordinal) { ["From"] = "+971509876543", ["Body"] = "No water in building 3", ["MessageSid"] = "SM1" };
        var url = new Uri(_factory.Services.GetRequiredService<ChannelOptions>().PublicUrl, $"hooks/{tenant.Slug}/sms/{twilio}");

        async Task<HttpStatusCode> PostTwilio(string signature)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/hooks/sms/{twilio}") { Content = new FormUrlEncodedContent(fields) };
            request.Headers.Add("X-Twilio-Signature", signature);
            using var response = await outside.SendAsync(request, Ct);
            return response.StatusCode;
        }

        Assert.Equal(HttpStatusCode.Unauthorized, await PostTwilio(WebhookSignatures.TwilioSign(url, fields, MessagingApi.Secret("x"))));
        Assert.Equal(HttpStatusCode.OK, await PostTwilio(WebhookSignatures.TwilioSign(url, fields, authToken)));

        // A gateway set up on screen: its token is in the address it posts to.
        var gateway = await admin.SendOkAsync(HttpMethod.Post, "/api/v1/messaging/channels", new
        {
            name = "Operator gateway",
            kind = "Sms",
            provider = "GenericHttp",
            sender = "ENTITY",
            credential = MessagingApi.Secret("gw"),
            gateway = new
            {
                url = "https://sms.operator.example.ae/api/send",
                body = "Json",
                bodyTemplate = """{"to":"{{to}}","sender":"{{from}}","message":"{{text}}"}""",
                auth = "Header",
                authHeader = "X-API-Key",
                inboundFrom = "msisdn",
                inboundText = "content",
                inboundId = "ref",
            },
        }, HttpStatusCode.Created);
        var hook = new Uri((string)gateway["webhookUrl"]!);
        using (var refused = await outside.PostAsJsonAsync(new Uri($"/api/v1/hooks/sms/{gateway.Id()}?token=wrong", UriKind.Relative), new { msisdn = "0501112222", content = "Hi" }, Ct))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        }

        using (var accepted = await outside.PostAsJsonAsync(new Uri($"/api/v1/hooks/sms/{gateway.Id()}{hook.Query}", UriKind.Relative),
            new { msisdn = "0501112222", content = "Street light broken on Al Wasl road", @ref = "g-1" }, Ct))
        {
            Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        }

        await using var db = TestData.AppContext(_database, tenant);
        var tickets = await db.Tickets.Include(t => t.Requester).Where(t => t.Channel == TicketChannel.Sms).OrderBy(t => t.CreatedAt).ToListAsync(Ct);
        Assert.Equal(["No water in building 3", "Street light broken on Al Wasl road"], tickets.Select(t => t.Title));
        // UAE local numbers are stored in international form.
        Assert.Equal(["+971509876543", "+971501112222"], tickets.Select(t => t.Requester!.Phone));

        // Gateways must be reachable and safe.
        using var internalHost = await admin.SendAsync(HttpMethod.Post, "/api/v1/messaging/channels", new
        {
            name = "Bad",
            kind = "Sms",
            provider = "GenericHttp",
            credential = "x",
            gateway = new { url = "https://127.0.0.1/send", bodyTemplate = "{\"to\":\"{{to}}\",\"text\":\"{{text}}\"}" },
        });
        Assert.Equal(HttpStatusCode.BadRequest, internalHost.StatusCode);
    }

    [Fact]
    public async Task Teams_raises_tickets_for_linked_people_and_approvers_decide_on_the_card()
    {
        var tenant = await _database.CreateTenantAsync("e3teams");
        var adminUser = await _database.CreateUserAsync(tenant, BuiltInRoles.TenantAdmin);
        using var admin = _factory.ForTenant(tenant).As(adminUser);
        using var publisher = await _factory.ForTenant(tenant).SignedInAsync(_database, tenant, BuiltInRoles.TenantAdmin);
        var requesterUser = await _database.CreateUserAsync(tenant, BuiltInRoles.Requester);
        var managerUser = await _database.CreateUserAsync(tenant, BuiltInRoles.Approver);
        using var requester = _factory.ForTenant(tenant).As(requesterUser);
        using var microsoft = _factory.ForTenant(tenant);
        var directory = Guid.NewGuid().ToString();
        var appId = Guid.NewGuid().ToString();
        var channel = (await admin.SendOkAsync(HttpMethod.Post, "/api/v1/messaging/channels", new
        {
            name = "Teams",
            kind = "Teams",
            provider = "TeamsBot",
            accountId = appId,
            directoryId = directory,
            credential = MessagingApi.Secret("teams"),
            newSenders = "KnownUsers",
        }, HttpStatusCode.Created)).Id();
        _providers.TeamsEmails["29:requester"] = requesterUser.Email;
        _providers.TeamsEmails["29:manager"] = managerUser.Email;

        // Only tokens Bot Framework signed for this app, about this service address.
        var hello = MessagingApi.TeamsActivity(directory, Guid.NewGuid().ToString(), "29:requester", "My monitor flickers");
        Assert.Equal(HttpStatusCode.Unauthorized, await microsoft.TeamsAsync(channel, appId, hello, TestTeamsKeys.Token(Guid.NewGuid().ToString(), MessagingApi.TeamsServiceUrl)).StatusAsync());
        Assert.Equal(HttpStatusCode.Unauthorized, await microsoft.TeamsAsync(channel, appId, hello,
            TestTeamsKeys.Token(appId, MessagingApi.TeamsServiceUrl, new Microsoft.IdentityModel.Tokens.RsaSecurityKey(System.Security.Cryptography.RSA.Create(2048)))).StatusAsync());

        // A message from a person of the directory raises an incident for their Zelvo account.
        Assert.Equal(HttpStatusCode.OK, await microsoft.TeamsAsync(channel, appId, hello).StatusAsync());
        var mine = (await requester.SendOkAsync(HttpMethod.Get, "/api/v1/me/tickets"))["items"]!.AsArray();
        Assert.Equal("My monitor flickers", (string?)Assert.Single(mine)!["title"]);
        await DeliverAsync(tenant);
        Assert.Contains(_providers.Sent, s => s.Message.Contact?.TeamsConversationId == "a:29:requester" && s.Message.Text!.Contains((string)mine[0]!["number"]!, StringComparison.Ordinal));

        // The approver installs the app and is linked; the approval reaches them as a card.
        var managerObject = Guid.NewGuid().ToString();
        await microsoft.TeamsAsync(channel, appId, MessagingApi.TeamsActivity(directory, managerObject, "29:manager", null, type: "conversationUpdate"));
        await _database.ExecuteAsOwnerAsync("UPDATE users SET manager_id = @m WHERE id = @u", ("m", managerUser.Id), ("u", requesterUser.Id));
        var service = (await admin.SendOkAsync(HttpMethod.Post, "/api/v1/catalog/services", new { code = "IT-TEAMS" }, HttpStatusCode.Created)).Id();
        await admin.SendOkAsync(HttpMethod.Put, $"/api/v1/catalog/services/{service}/draft", new
        {
            card = new JsonObject
            {
                ["name"] = new JsonObject { ["en"] = "Software", ["ar"] = "برمجيات" },
                ["summary"] = new JsonObject { ["en"] = "Get software", ["ar"] = "الحصول على برمجيات" },
                ["channels"] = new JsonArray("Portal"),
                ["approvals"] = new JsonObject
                {
                    ["beforeAssignment"] = new JsonArray(new JsonObject
                    {
                        ["name"] = new JsonObject { ["en"] = "Manager", ["ar"] = "المدير" },
                        ["mode"] = "Any",
                        ["approvers"] = new JsonArray(new JsonObject { ["kind"] = "RequesterManager" }),
                    }),
                },
            },
        });
        await admin.SendOkAsync(HttpMethod.Post, $"/api/v1/catalog/services/{service}/submit");
        await publisher.SendOkAsync(HttpMethod.Post, $"/api/v1/catalog/services/{service}/approve");
        var request = (await requester.SendOkAsync(HttpMethod.Post, "/api/v1/service-requests", new { serviceId = service }, HttpStatusCode.Created)).Id();
        _providers.Sent.Clear();
        await DeliverAsync(tenant);
        var card = Assert.Single(_providers.Sent, s => s.Message.Kind == ChannelMessageKind.Card).Message;
        var actions = JsonNode.Parse(card.Payload!)!["actions"]!.AsArray();
        var decisionId = (string)actions[0]!["data"]!["decisionId"]!;
        Assert.Equal(["approve", "reject"], actions.Take(2).Select(a => (string?)a!["data"]!["decision"]));

        // Rejecting needs a reason; someone else's account cannot decide; the approver approves on the card.
        _providers.Sent.Clear();
        var reject = new JsonObject { ["zelvo"] = "approval", ["decision"] = "reject", ["decisionId"] = decisionId };
        await microsoft.TeamsAsync(channel, appId, MessagingApi.TeamsActivity(directory, managerObject, "29:manager", null, reject));
        await microsoft.TeamsAsync(channel, appId, MessagingApi.TeamsActivity(directory, Guid.NewGuid().ToString(), "29:requester", null,
            new JsonObject { ["zelvo"] = "approval", ["decision"] = "approve", ["decisionId"] = decisionId }));
        await _factory.RunMessagingAsync(tenant);
        Assert.Equal(2, _providers.Sent.Count);
        await using (var db = TestData.AppContext(_database, tenant))
        {
            Assert.Equal("Pending", (await db.ApprovalDecisions.SingleAsync(Ct)).State.ToString());
        }

        await microsoft.TeamsAsync(channel, appId, MessagingApi.TeamsActivity(directory, managerObject, "29:manager", null,
            new JsonObject { ["zelvo"] = "approval", ["decision"] = "approve", ["decisionId"] = decisionId }));
        await using (var db = TestData.AppContext(_database, tenant))
        {
            var decision = await db.ApprovalDecisions.SingleAsync(Ct);
            Assert.Equal(("Approved", "teams"), (decision.State.ToString(), decision.DecidedVia));
            Assert.False((await db.Tickets.SingleAsync(t => t.Id == request, Ct)).AwaitingApproval);
        }

        var approved = (await _database.AuditAsync(tenant)).Single(a => a.Action == "approvals.approved");
        Assert.Equal(("User", managerUser.Id.ToString()), (approved.ActorType, approved.ActorId));

        // Other directories and group chats are left alone.
        var foreign = MessagingApi.TeamsActivity(Guid.NewGuid().ToString(), Guid.NewGuid().ToString(), "29:requester", "From elsewhere");
        Assert.Equal(HttpStatusCode.OK, await microsoft.TeamsAsync(channel, appId, foreign).StatusAsync());
        Assert.Single((await requester.SendOkAsync(HttpMethod.Get, "/api/v1/me/tickets?type=Incident"))["items"]!.AsArray());

        // The Teams app package for the admin centre.
        using var package = await admin.GetAsync(new Uri($"/api/v1/messaging/channels/{channel}/teams-app", UriKind.Relative), Ct);
        using var zip = new System.IO.Compression.ZipArchive(await package.Content.ReadAsStreamAsync(Ct));
        Assert.Equal(["manifest.json", "ar.json", "color.png", "outline.png"], zip.Entries.Select(e => e.Name));
        await using var manifestStream = zip.GetEntry("manifest.json")!.Open();
        Assert.Equal(appId, (string?)JsonNode.Parse(manifestStream)!["bots"]![0]!["botId"]);
    }

    [Fact]
    public async Task People_choose_WhatsApp_and_SMS_for_their_notifications_and_approvers_get_both_links()
    {
        var tenant = await _database.CreateTenantAsync("e3pref");
        using var admin = await _factory.ForTenant(tenant).SignedInAsync(_database, tenant, BuiltInRoles.TenantAdmin);
        var agentUser = await _database.CreateUserAsync(tenant, BuiltInRoles.Agent);
        using var agent = _factory.ForTenant(tenant).As(agentUser);
        await admin.SendOkAsync(HttpMethod.Post, "/api/v1/messaging/channels", new
        {
            name = "SMS",
            kind = "Sms",
            provider = "Twilio",
            accountId = "AC" + Guid.NewGuid().ToString("N"),
            sender = "ENTITY",
            credential = MessagingApi.Secret("tw"),
        }, HttpStatusCode.Created);

        // Without a phone on the profile, WhatsApp and SMS cannot be chosen.
        var choices = await agent.SendOkAsync(HttpMethod.Get, "/api/v1/me/notification-channels");
        Assert.Equal((true, false, false), ((bool)choices["smsOffered"]!, (bool)choices["whatsAppOffered"]!, (bool)choices["sms"]!));
        using (var noPhone = await agent.SendAsync(HttpMethod.Put, "/api/v1/me/notification-channels", new { sms = true }))
        {
            Assert.Equal(HttpStatusCode.BadRequest, noPhone.StatusCode);
        }

        await _database.ExecuteAsOwnerAsync("UPDATE users SET phone = '+971551234567' WHERE id = @u", ("u", agentUser.Id));
        choices = await agent.SendOkAsync(HttpMethod.Put, "/api/v1/me/notification-channels", new { sms = true });
        Assert.Equal((true, "+9715•••••567"), ((bool)choices["sms"]!, (string?)choices["phone"]));

        // An incident assigned to the agent also reaches them by SMS, with the link.
        var group = await admin.GroupAsync("Desk", agentUser);
        await admin.SendOkAsync(HttpMethod.Put, "/api/v1/assignment-rules/default", new { groupId = group, method = "RoundRobin" });
        using var requester = await _factory.ForTenant(tenant).SignedInAsync(_database, tenant, BuiltInRoles.Requester);
        await requester.SendOkAsync(HttpMethod.Post, "/api/v1/incidents", new { title = "Laptop does not start", description = "Black screen." }, HttpStatusCode.Created);
        await DeliverAsync(tenant);
        var sms = Assert.Single(_providers.SentTo("+971551234567")).Message;
        Assert.Equal(ChannelMessageKind.Text, sms.Kind);
        Assert.Contains("/en/", sms.Text, StringComparison.Ordinal);

        // An event switched off for WhatsApp, SMS and Teams stays in-app and email only.
        _providers.Sent.Clear();
        await admin.SendOkAsync(HttpMethod.Put, "/api/v1/notifications/settings/events/incident.assigned", new { enabled = true, email = true, messaging = false });
        await requester.SendOkAsync(HttpMethod.Post, "/api/v1/incidents", new { title = "Second laptop", description = "Also black." }, HttpStatusCode.Created);
        await DeliverAsync(tenant);
        Assert.Empty(_providers.SentTo("+971551234567"));

        // Approvals by SMS carry the Approve and Reject links of the email (they open Zelvo).
        var links = MessagingNotificationsText("approval.requested");
        Assert.Contains("?decision=approve", links, StringComparison.Ordinal);
        Assert.Contains("?decision=reject", links, StringComparison.Ordinal);
    }

    private static string MessagingNotificationsText(string eventType)
    {
        var options = new Zelvo.Notifications.NotificationOptions();
        var approve = new Uri(options.WebUrl, "en/approvals/1?decision=approve");
        return Zelvo.Notifications.MessagingNotifications.Text(
            new Zelvo.Notifications.RenderedNotification("Approval needed", "Please decide.", string.Empty, string.Empty),
            new Zelvo.Notifications.NotificationLinks(approve, null, new Uri(options.WebUrl, "en/approvals/1?decision=reject"), "Reject"), "en", eventType);
    }
}
