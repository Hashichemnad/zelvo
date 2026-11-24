using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Zelvo.Api.Channels;
using Zelvo.Data.Live;
using Zelvo.Channels;
using Zelvo.Data.Channels;
using Zelvo.Data.Identity;
using Zelvo.Data.Tickets;

namespace Zelvo.Api.Tests;

/// <summary>Live chat (module 6, task E2; product owner decisions 2026-11-24) against PostgreSQL.</summary>
public sealed class ChatChannelTests(DatabaseFixture database) : IAsyncDisposable
{
    private readonly ApiFactory _factory = ApiFactory.ForDatabase(database);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    private static async Task<HttpResponseMessage> AsVisitorAsync(HttpClient client, HttpMethod method, string uri, string? token, object? body = null)
    {
        using var request = new HttpRequestMessage(method, uri) { Content = body is null ? null : JsonContent.Create(body) };
        if (token is not null)
        {
            request.Headers.Add(ChatEndpoints.TokenHeader, token);
        }

        return await client.SendAsync(request, Ct);
    }

    private static async Task<JsonObject> JsonAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode} {text}");
        return JsonNode.Parse(text)!.AsObject();
    }

    [Fact]
    public async Task A_visitor_chats_with_an_available_agent_who_makes_a_ticket_of_it()
    {
        var tenant = await database.CreateTenantAsync("e2chat");
        using var admin = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.TenantAdmin);
        var agentUser = await database.CreateUserAsync(tenant, BuiltInRoles.Agent);
        var colleagueUser = await database.CreateUserAsync(tenant, BuiltInRoles.Agent);
        using var agent = _factory.ForTenant(tenant).As(agentUser);
        using var colleague = _factory.ForTenant(tenant).As(colleagueUser);
        using var visitor = _factory.ForTenant(tenant);
        var group = await admin.GroupAsync("Service desk", agentUser, colleagueUser);
        var otherGroup = await admin.GroupAsync("Facilities");
        await admin.SendOkAsync(HttpMethod.Patch, $"/api/v1/groups/{group}", new { chatLimit = 1 });
        var widget = (await admin.SendOkAsync(HttpMethod.Post, "/api/v1/chat/widgets", new
        {
            name = new { en = "Chat with us", ar = "تحدّث معنا" },
            greeting = new { en = "Hello" },
            groupId = group,
            allowedOrigins = new[] { "https://www.entity.example.gov/", "HTTPS://Portal.Entity.example.gov" },
            onPortal = true,
        }, HttpStatusCode.Created)).Id();
        var stored = Assert.Single((await admin.SendOkAsync(HttpMethod.Get, "/api/v1/chat/widgets"))["items"]!.AsArray())!;
        Assert.Equal(["https://www.entity.example.gov", "https://portal.entity.example.gov"], stored["allowedOrigins"]!.AsArray().Select(o => (string)o!));
        Assert.Equal(HttpStatusCode.Forbidden, (await agent.SendAsync(HttpMethod.Get, "/api/v1/chat/widgets")).StatusCode);

        // The widget only works on the websites it lists.
        var publicWidget = await JsonAsync(await visitor.GetAsync($"/api/v1/chat/widgets/{widget}/public?origin=https://evil.example.com", Ct));
        Assert.False((bool)publicWidget["allowed"]!);
        Assert.Equal("origin-not-allowed", (await (await visitor.SendAsync(HttpMethod.Post, $"/api/v1/chat/widgets/{widget}/sessions",
            new { name = "Sara", email = "sara@visitors.example.org", origin = "https://evil.example.com" })).ProblemAsync()).Code);

        // Visitors give their name and email.
        Assert.Equal(HttpStatusCode.BadRequest, (await visitor.SendAsync(HttpMethod.Post, $"/api/v1/chat/widgets/{widget}/sessions", new { name = "Sara" })).StatusCode);

        var start = await JsonAsync(await visitor.SendAsync(HttpMethod.Post, $"/api/v1/chat/widgets/{widget}/sessions",
            new { name = "Sara", email = "Sara@Visitors.example.org", language = "ar", message = "My card payment failed.", origin = "https://www.entity.example.gov" }));
        var session = Guid.Parse((string)start["sessionId"]!);
        var token = (string)start["token"]!;
        await using (var db = TestData.AppContext(database, tenant))
        {
            // Only a hash of the token is kept.
            Assert.DoesNotContain(token, (await db.ChatSessions.SingleAsync(Ct)).VisitorTokenHash, StringComparison.Ordinal);
        }

        // Nobody else can read the chat.
        Assert.Equal(HttpStatusCode.NotFound, (await AsVisitorAsync(visitor, HttpMethod.Get, $"/api/v1/chat/sessions/{session}", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await AsVisitorAsync(visitor, HttpMethod.Get, $"/api/v1/chat/sessions/{session}", token[..^1] + "0")).StatusCode);

        // The agent sees it waiting, accepts it, and the colleague cannot take it too.
        var queue = await agent.SendOkAsync(HttpMethod.Get, "/api/v1/chat/queue");
        var waiting = Assert.Single(queue["waiting"]!.AsArray())!;
        Assert.Equal(("Sara", "My card payment failed."), ((string?)waiting["visitorName"], (string?)waiting["lastMessage"]));
        await agent.SendOkAsync(HttpMethod.Post, $"/api/v1/chat/sessions/{session}/accept");
        Assert.Equal("chat-taken", (await (await colleague.SendAsync(HttpMethod.Post, $"/api/v1/chat/sessions/{session}/accept")).ProblemAsync()).Code);
        Assert.Equal("chat-not-yours", (await (await colleague.SendAsync(HttpMethod.Post, $"/api/v1/chat/sessions/{session}/agent-messages", new { body = "Hi" })).ProblemAsync()).Code);

        await agent.SendOkAsync(HttpMethod.Post, $"/api/v1/chat/sessions/{session}/agent-messages", new { body = "Which card did you use?" }, HttpStatusCode.Created);
        var seen = await JsonAsync(await AsVisitorAsync(visitor, HttpMethod.Get, $"/api/v1/chat/sessions/{session}", token));
        Assert.Equal("Active", (string?)seen["state"]);
        var last = seen["messages"]!.AsArray()[^1]!;
        Assert.Equal(("Agent", "Which card did you use?"), ((string?)last["sender"], (string?)last["body"]));
        var after = (long)last["sequence"]!;
        await JsonAsync(await AsVisitorAsync(visitor, HttpMethod.Post, $"/api/v1/chat/sessions/{session}/messages", token, new { body = "A debit card." }));
        var newer = await JsonAsync(await AsVisitorAsync(visitor, HttpMethod.Get, $"/api/v1/chat/sessions/{session}?after={after}", token));
        Assert.Equal("A debit card.", (string?)Assert.Single(newer["messages"]!.AsArray())!["body"]);

        // The group's limit of one chat: a second visitor waits; the agent cannot take it, the colleague can.
        var second = await JsonAsync(await visitor.SendAsync(HttpMethod.Post, $"/api/v1/chat/widgets/{widget}/sessions",
            new { name = "Ali", email = "ali@visitors.example.org", origin = "https://www.entity.example.gov" }));
        var secondId = Guid.Parse((string)second["sessionId"]!);
        Assert.Equal("chat-limit-reached", (await (await agent.SendAsync(HttpMethod.Post, $"/api/v1/chat/sessions/{secondId}/accept")).ProblemAsync()).Code);
        await colleague.SendOkAsync(HttpMethod.Post, $"/api/v1/chat/sessions/{secondId}/accept");
        // Transferred to another group, it waits there and leaves the colleague's list.
        await colleague.SendOkAsync(HttpMethod.Post, $"/api/v1/chat/sessions/{secondId}/transfer", new { groupId = otherGroup });
        Assert.Empty((await colleague.SendOkAsync(HttpMethod.Get, "/api/v1/chat/queue"))["mine"]!.AsArray());
        Assert.Equal(HttpStatusCode.NotFound, (await colleague.SendAsync(HttpMethod.Get, $"/api/v1/chat/sessions/{secondId}/agent")).StatusCode);

        // The agent decides this one needs a ticket: an incident for the visitor's email with the transcript.
        var withTicket = await agent.SendOkAsync(HttpMethod.Post, $"/api/v1/chat/sessions/{session}/ticket", new { title = "Card payment failed" });
        var ticketId = Guid.Parse((string)withTicket["ticketId"]!);
        var incident = await agent.GetIncidentAsync(ticketId);
        Assert.Equal(("Chat", "Card payment failed"), ((string?)incident["channel"], (string?)incident["title"]));
        Assert.Equal(agentUser.Id.ToString(), (string?)incident["assignee"]!["id"]);
        var transcript = (await agent.SendOkAsync(HttpMethod.Get, $"/api/v1/incidents/{ticketId}/comments"))["items"]!.AsArray();
        Assert.Equal(["My card payment failed.", "Which card did you use?", "A debit card."], transcript.Select(m => (string)m!["body"]!));
        Assert.Equal("chat-has-ticket", (await (await agent.SendAsync(HttpMethod.Post, $"/api/v1/chat/sessions/{session}/ticket", new { })).ProblemAsync()).Code);
        await using (var db = TestData.AppContext(database, tenant))
        {
            var requester = await db.Users.SingleAsync(u => u.Email == "sara@visitors.example.org", Ct);
            Assert.Equal(("ar", requester.Id), (requester.PreferredLanguage, (await db.Tickets.SingleAsync(t => t.Id == ticketId, Ct)).RequesterId));
            // The transcript's copy raises no reply notifications: both sides saw it live.
            Assert.False(await db.Notifications.AnyAsync(n => n.EventType == "incident.replied", Ct));
            Assert.All(await db.TicketComments.Where(c => c.TicketId == ticketId).ToListAsync(Ct), c => Assert.Equal((CommentSource.Chat, session, "ar"), (c.Source, c.ChatSessionId, c.Language)));
            Assert.NotNull((await db.Tickets.SingleAsync(t => t.Id == ticketId, Ct)).RespondedAt);
        }

        // The visitor ends the chat; nothing more can be written.
        var ended = await JsonAsync(await AsVisitorAsync(visitor, HttpMethod.Post, $"/api/v1/chat/sessions/{session}/end", token));
        Assert.Equal(("Ended", "Visitor"), ((string?)ended["state"], (string?)ended["endedBy"]));
        Assert.Equal(HttpStatusCode.Conflict, (await AsVisitorAsync(visitor, HttpMethod.Post, $"/api/v1/chat/sessions/{session}/messages", token, new { body = "?" })).StatusCode);

        // Viewing a transcript is audited as a view of personal data.
        await agent.SendOkAsync(HttpMethod.Get, $"/api/v1/chat/sessions/{session}/agent");
        await using (var db = TestData.AppContext(database, tenant))
        {
            Assert.True(await db.AuditEvents.AnyAsync(e => e.Action == "channels.chat.viewed" && e.EntityId == session.ToString(), Ct));
        }
    }

    [Fact]
    public async Task With_nobody_available_visitors_leave_a_message_that_becomes_a_ticket()
    {
        var tenant = await database.CreateTenantAsync("e2off");
        using var admin = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.TenantAdmin);
        var agentUser = await database.CreateUserAsync(tenant, BuiltInRoles.Agent);
        using var agent = _factory.ForTenant(tenant).As(agentUser);
        using var visitor = _factory.ForTenant(tenant);
        var group = await admin.GroupAsync("Service desk", agentUser);
        var widget = (await admin.SendOkAsync(HttpMethod.Post, "/api/v1/chat/widgets", new { name = new { en = "Help" }, groupId = group, onPortal = true }, HttpStatusCode.Created)).Id();
        await agent.SendOkAsync(HttpMethod.Put, "/api/v1/me/availability", new { available = false });

        Assert.False((bool)(await JsonAsync(await visitor.GetAsync("/api/v1/chat/portal-widget", Ct)))["online"]!);
        Assert.Equal("chat-offline", (await (await visitor.SendAsync(HttpMethod.Post, $"/api/v1/chat/widgets/{widget}/sessions",
            new { name = "Mona", email = "mona@visitors.example.org" })).ProblemAsync()).Code);

        var left = await JsonAsync(await visitor.SendAsync(HttpMethod.Post, $"/api/v1/chat/widgets/{widget}/offline-messages",
            new { name = "Mona", email = "mona@visitors.example.org", message = "Please call me about my permit.\nThank you." }));
        await using (var db = TestData.AppContext(database, tenant))
        {
            var ticket = await db.Tickets.Include(t => t.Requester).SingleAsync(t => t.Number == (string)left["number"]!, Ct);
            Assert.Equal((TicketChannel.Chat, "Please call me about my permit.", group, "mona@visitors.example.org"),
                (ticket.Channel, ticket.Title, ticket.GroupId, ticket.Requester!.Email));
        }

        // An available agent at their limit is not online either; a signed-in requester chats without giving details.
        await agent.SendOkAsync(HttpMethod.Put, "/api/v1/me/availability", new { available = true });
        using var requester = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.Requester);
        var start = await JsonAsync(await requester.SendAsync(HttpMethod.Post, $"/api/v1/chat/widgets/{widget}/sessions", new { message = "Hi" }));
        Assert.Null(start["token"]);
        var mine = await JsonAsync(await requester.GetAsync($"/api/v1/chat/sessions/{(string)start["sessionId"]!}", Ct));
        Assert.Equal("Waiting", (string?)mine["state"]);
    }

    [Fact]
    public async Task Chat_changes_are_announced_to_agents_on_the_live_channel_with_ids_only()
    {
        var tenant = await database.CreateTenantAsync("e2live");
        using var admin = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.TenantAdmin);
        var agentUser = await database.CreateUserAsync(tenant, BuiltInRoles.Agent);
        using var visitor = _factory.ForTenant(tenant);
        var group = await admin.GroupAsync("Desk", agentUser);
        var widget = (await admin.SendOkAsync(HttpMethod.Post, "/api/v1/chat/widgets", new { name = new { en = "Help" }, groupId = group, onPortal = true }, HttpStatusCode.Created)).Id();

        await using var listener = new NpgsqlConnection(database.AppConnectionString);
        await listener.OpenAsync(Ct);
        var received = new List<string>();
        listener.Notification += (_, e) => { lock (received) received.Add(e.Payload); };
        await using (var listen = new NpgsqlCommand($"LISTEN {LiveUpdateInterceptor.Channel}", listener))
        {
            await listen.ExecuteNonQueryAsync(Ct);
        }

        var start = await JsonAsync(await visitor.SendAsync(HttpMethod.Post, $"/api/v1/chat/widgets/{widget}/sessions",
            new { name = "Visitor name", email = "v@visitors.example.org", message = "Private question text" }));
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        wait.CancelAfter(TimeSpan.FromSeconds(5));
        while (!received.Any(p => p.Contains("\"chat\"", StringComparison.Ordinal)))
        {
            await listener.WaitAsync(wait.Token);
        }

        var live = received.Select(LiveEvent.Parse).First(e => e?.Kind == LiveEvent.Chat)!;
        Assert.Equal((tenant.Id, Guid.Parse((string)start["sessionId"]!)), (live.Tenant, live.Id));
        Assert.DoesNotContain(received, p => p.Contains("Private", StringComparison.Ordinal) || p.Contains("visitors.example.org", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Chats_whose_visitor_left_end_on_their_own()
    {
        var tenant = await database.CreateTenantAsync("e2idle");
        using var admin = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.TenantAdmin);
        var agentUser = await database.CreateUserAsync(tenant, BuiltInRoles.Agent);
        using var visitor = _factory.ForTenant(tenant);
        var group = await admin.GroupAsync("Desk", agentUser);
        var widget = (await admin.SendOkAsync(HttpMethod.Post, "/api/v1/chat/widgets", new { name = new { en = "Help" }, groupId = group, onPortal = true }, HttpStatusCode.Created)).Id();
        var start = await JsonAsync(await visitor.SendAsync(HttpMethod.Post, $"/api/v1/chat/widgets/{widget}/sessions", new { name = "V", email = "v@visitors.example.org" }));
        var session = Guid.Parse((string)start["sessionId"]!);
        await using (var db = TestData.AppContext(database, tenant))
        {
            await db.ChatSessions.Where(c => c.Id == session).ExecuteUpdateAsync(s => s.SetProperty(c => c.LastVisitorActivityAt, DateTimeOffset.UtcNow.AddHours(-1)), Ct);
        }

        var job = new ChatIdleJob(_factory.Services.GetRequiredService<IServiceScopeFactory>(), _factory.Services.GetRequiredService<ChannelOptions>(),
            TimeProvider.System, NullLogger<ChatIdleJob>.Instance);
        Assert.True(await job.RunOnceAsync(Ct) >= 1);
        var ended = await JsonAsync(await AsVisitorAsync(visitor, HttpMethod.Get, $"/api/v1/chat/sessions/{session}", (string)start["token"]!));
        Assert.Equal(("Ended", "System"), ((string?)ended["state"], (string?)ended["endedBy"]));
    }

    [Theory]
    [InlineData("https://www.entity.example.gov", "https://www.entity.example.gov")]
    [InlineData("https://WWW.Entity.example.gov:8443/", "https://www.entity.example.gov:8443")]
    [InlineData("http://localhost:3000", "http://localhost:3000")]
    [InlineData("http://www.entity.example.gov", null)]
    [InlineData("https://www.entity.example.gov/page", null)]
    [InlineData("https://user:pw@www.entity.example.gov", null)]
    [InlineData("javascript:alert(1)", null)]
    public void Widget_origins_are_https_addresses_without_paths(string value, string? expected) =>
        Assert.Equal(expected, ChatEndpoints.NormalizeOrigin(value));
}
