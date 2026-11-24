using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Zelvo.Data.Auditing;
using Zelvo.Data.Identity;
using Zelvo.Data.Live;
using Zelvo.Data.Tickets;
using Zelvo.Notifications;

namespace Zelvo.Api.Tests;

/// <summary>
/// The agent workspace (module 5, task B4): public replies and internal notes, @mentions, the
/// response clock, canned responses, saved views, the customer beside the ticket, bulk actions and
/// live updates.
/// </summary>
public sealed class WorkspaceApiTests(DatabaseFixture database) : IAsyncDisposable
{
    private readonly ApiFactory _factory = ApiFactory.ForDatabase(database);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    private static string Comments(Guid id) => $"/api/v1/incidents/{id}/comments";

    [Fact]
    public async Task Requesters_see_public_replies_only_and_staff_see_internal_notes_too()
    {
        var tenant = await database.CreateTenantAsync("ws");
        var requester = await database.CreateUserAsync(tenant, BuiltInRoles.Requester);
        var agent = await database.CreateUserAsync(tenant, BuiltInRoles.Agent);
        using var asRequester = _factory.ForTenant(tenant).As(requester);
        using var asAgent = _factory.ForTenant(tenant).As(agent);
        using var stranger = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.Requester);
        var id = IncidentApi.IdOf(await asRequester.CreateAsync(new { title = "VPN keeps dropping" }));

        var reply = await asAgent.SendOkAsync(HttpMethod.Post, Comments(id), new { body = "Which VPN client version do you use?", language = "en" }, HttpStatusCode.Created);
        await asAgent.SendOkAsync(HttpMethod.Post, Comments(id), new { body = "Probably the 6.2 bug.", visibility = "Internal" }, HttpStatusCode.Created);
        await asRequester.SendOkAsync(HttpMethod.Post, Comments(id), new { body = "الإصدار 6.2", language = "ar" }, HttpStatusCode.Created);

        Assert.Equal("Public", (string?)reply["visibility"]);
        Assert.True((bool)reply["byStaff"]!);
        var staffView = await asAgent.ArrayAsync(Comments(id));
        Assert.Equal(["Which VPN client version do you use?", "Probably the 6.2 bug.", "الإصدار 6.2"], staffView.Select(c => (string)c!["body"]!));
        Assert.Equal("ar", (string?)staffView[2]!["language"]);
        Assert.False((bool)staffView[2]!["byStaff"]!);
        var requesterView = await asRequester.ArrayAsync(Comments(id));
        Assert.Equal(2, requesterView.Count);
        Assert.DoesNotContain(requesterView, c => (string)c!["visibility"]! == "Internal");

        Assert.Equal(HttpStatusCode.Forbidden, (await asRequester.SendAsync(HttpMethod.Post, Comments(id), new { body = "secret", visibility = "Internal" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.SendAsync(HttpMethod.Get, Comments(id))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.SendAsync(HttpMethod.Post, Comments(id), new { body = "hello" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await asAgent.SendAsync(HttpMethod.Post, Comments(id), new { body = "   " })).StatusCode);

        await using var db = TestData.AppContext(database, tenant);
        var stored = await db.TicketComments.Where(c => c.TicketId == id).ToListAsync(Ct);
        Assert.Equal(3, stored.Count);
        // The audit log keeps the length of a comment, not its text.
        var audited = await db.AuditEvents.Where(e => e.EntityType == "ticket_comment" && e.Category == AuditCategory.Create).ToListAsync(Ct);
        Assert.Equal(3, audited.Count);
        Assert.DoesNotContain(audited, e => e.Changes!.Contains("6.2", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Comments_cannot_be_changed_and_closed_incidents_take_none()
    {
        var tenant = await database.CreateTenantAsync("ws");
        var agent = await database.CreateUserAsync(tenant, BuiltInRoles.Agent);
        using var asAgent = _factory.ForTenant(tenant).As(agent);
        var id = IncidentApi.IdOf(await asAgent.CreateAsync(new { title = "Disk full", assigneeId = agent.Id }));
        var comment = (await asAgent.SendOkAsync(HttpMethod.Post, Comments(id), new { body = "Cleared temp files.", visibility = "Internal" }, HttpStatusCode.Created)).Id();

        await using (var db = TestData.AppContext(database, tenant))
        {
            var error = await Assert.ThrowsAsync<PostgresException>(() =>
                db.Database.ExecuteSqlRawAsync("UPDATE ticket_comments SET body = 'changed' WHERE id = {0}", [comment], Ct));
            Assert.Contains("cannot be changed", error.MessageText, StringComparison.Ordinal);
        }

        Assert.True((await asAgent.MoveAsync(id, "Resolved", notes: "Done")).IsSuccessStatusCode);
        Assert.True((await asAgent.MoveAsync(id, "Closed")).IsSuccessStatusCode);
        var closed = await asAgent.SendAsync(HttpMethod.Post, Comments(id), new { body = "One more thing" });
        Assert.Equal(HttpStatusCode.Conflict, closed.StatusCode);
        Assert.Equal("ticket-closed", (await closed.ProblemAsync()).Code);
    }

    [Fact]
    public async Task A_staff_public_reply_is_the_response_but_internal_notes_are_not()
    {
        var tenant = await database.CreateTenantAsync("ws");
        var requester = await database.CreateUserAsync(tenant, BuiltInRoles.Requester);
        var agent = await database.CreateUserAsync(tenant, BuiltInRoles.Agent);
        using var asRequester = _factory.ForTenant(tenant).As(requester);
        using var asAgent = _factory.ForTenant(tenant).As(agent);
        var id = IncidentApi.IdOf(await asRequester.CreateAsync(new { title = "Cannot print" }));

        await asAgent.SendOkAsync(HttpMethod.Post, Comments(id), new { body = "Looking.", visibility = "Internal" }, HttpStatusCode.Created);
        Assert.Null((await asAgent.GetIncidentAsync(id))["respondedAt"]);
        await asRequester.SendOkAsync(HttpMethod.Post, Comments(id), new { body = "Any news?" }, HttpStatusCode.Created);
        Assert.Null((await asAgent.GetIncidentAsync(id))["respondedAt"]);

        await asAgent.SendOkAsync(HttpMethod.Post, Comments(id), new { body = "We are on it." }, HttpStatusCode.Created);
        var first = (string?)(await asAgent.GetIncidentAsync(id))["respondedAt"];
        Assert.NotNull(first);
        await asAgent.SendOkAsync(HttpMethod.Post, Comments(id), new { body = "Still on it." }, HttpStatusCode.Created);
        Assert.Equal(first, (string?)(await asAgent.GetIncidentAsync(id))["respondedAt"]);
    }

    [Fact]
    public async Task Replies_notify_the_requester_and_mentions_notify_staff_but_never_the_author()
    {
        var tenant = await database.CreateTenantAsync("ws");
        var requester = await database.CreateUserAsync(tenant, BuiltInRoles.Requester);
        var agent = await database.CreateUserAsync(tenant, BuiltInRoles.Agent);
        var lead = await database.CreateUserAsync(tenant, BuiltInRoles.TeamLead);
        using var asRequester = _factory.ForTenant(tenant).As(requester);
        using var asAgent = _factory.ForTenant(tenant).As(agent);
        var id = IncidentApi.IdOf(await asRequester.CreateAsync(new { title = "Laptop overheating" }));

        await asAgent.SendOkAsync(HttpMethod.Post, Comments(id), new { body = "Please send a photo." }, HttpStatusCode.Created);
        var note = await asAgent.SendOkAsync(HttpMethod.Post, Comments(id),
            new { body = "@Lead can you approve a replacement?", visibility = "Internal", mentionIds = new[] { lead.Id, agent.Id } }, HttpStatusCode.Created);
        Assert.Equal(2, note["mentions"]!.AsArray().Count);

        var badMention = await asAgent.SendAsync(HttpMethod.Post, Comments(id), new { body = "@someone", visibility = "Internal", mentionIds = new[] { requester.Id } });
        Assert.Equal(HttpStatusCode.BadRequest, badMention.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await asRequester.SendAsync(HttpMethod.Post, Comments(id), new { body = "hi", mentionIds = new[] { agent.Id } })).StatusCode);

        await using var db = TestData.AppContext(database, tenant);
        var notifications = await db.Notifications.Where(n => n.TicketId == id).ToListAsync(Ct);
        Assert.Single(notifications, n => n.EventType == NotificationEvents.IncidentReplied && n.RecipientId == requester.Id);
        Assert.Single(notifications, n => n.EventType == NotificationEvents.IncidentMentioned && n.RecipientId == lead.Id);
        Assert.DoesNotContain(notifications, n => n.RecipientId == agent.Id && n.EventType == NotificationEvents.IncidentMentioned);
        // Internal notes never reach the requester.
        Assert.Single(notifications, n => n.RecipientId == requester.Id && n.EventType == NotificationEvents.IncidentReplied);
        Assert.Contains("photo", notifications.Single(n => n.EventType == NotificationEvents.IncidentReplied).Data, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Canned_responses_are_personal_for_the_group_or_for_everyone_and_are_filled_in_per_ticket()
    {
        var tenant = await database.CreateTenantAsync("ws");
        var admin = await database.CreateUserAsync(tenant, BuiltInRoles.TenantAdmin);
        var lead = await database.CreateUserAsync(tenant, BuiltInRoles.TeamLead);
        var member = await database.CreateUserAsync(tenant, BuiltInRoles.Agent);
        var outsider = await database.CreateUserAsync(tenant, BuiltInRoles.Agent);
        var requester = await database.CreateUserAsync(tenant, BuiltInRoles.Requester);
        using var asAdmin = _factory.ForTenant(tenant).As(admin);
        using var asLead = _factory.ForTenant(tenant).As(lead);
        using var asMember = _factory.ForTenant(tenant).As(member);
        using var asOutsider = _factory.ForTenant(tenant).As(outsider);
        var group = await asAdmin.GroupAsync("Desk", member);
        await asAdmin.SendOkAsync(HttpMethod.Put, $"/api/v1/groups/{group}/members/{lead.Id}", new { isLead = true });

        var everyone = new { scope = "Tenant", title = new { en = "Greeting", ar = "تحية" }, body = new { en = "Hello {{requester.name}}, about {{ticket.number}}:", ar = "السيد/السيدة {{requester.name}}، بخصوص {{ticket.number}}:" } };
        Assert.Equal(HttpStatusCode.BadRequest, (await asMember.SendAsync(HttpMethod.Post, "/api/v1/canned-responses", everyone)).StatusCode);
        await asAdmin.SendOkAsync(HttpMethod.Post, "/api/v1/canned-responses", everyone, HttpStatusCode.Created);

        var forGroup = new { scope = "Group", groupId = group, title = new { en = "Restart" }, body = new { en = "Please restart, {{agent.name}}" } };
        Assert.Equal(HttpStatusCode.BadRequest, (await asOutsider.SendAsync(HttpMethod.Post, "/api/v1/canned-responses", forGroup)).StatusCode);
        var groupResponse = (await asLead.SendOkAsync(HttpMethod.Post, "/api/v1/canned-responses", forGroup, HttpStatusCode.Created)).Id();
        await asOutsider.SendOkAsync(HttpMethod.Post, "/api/v1/canned-responses",
            new { scope = "Personal", title = new { ar = "شكراً" }, body = new { ar = "شكراً لكم" } }, HttpStatusCode.Created);

        var unknown = await asMember.SendAsync(HttpMethod.Post, "/api/v1/canned-responses",
            new { scope = "Personal", title = new { en = "Bad" }, body = new { en = "Hi {{requester.email}}" } });
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);

        Assert.Equal(2, (await asMember.ArrayAsync("/api/v1/canned-responses")).Count);
        Assert.Equal(2, (await asOutsider.ArrayAsync("/api/v1/canned-responses")).Count);
        Assert.Equal(HttpStatusCode.NotFound, (await asMember.SendAsync(HttpMethod.Put, $"/api/v1/canned-responses/{groupResponse}", forGroup)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _factory.ForTenant(tenant).As(requester).SendAsync(HttpMethod.Get, "/api/v1/canned-responses")).StatusCode);

        using var asRequester = _factory.ForTenant(tenant).As(requester);
        var incident = IncidentApi.IdOf(await asRequester.CreateAsync(new { title = "Screen flickers" }));
        var tenantResponse = (await asMember.ArrayAsync("/api/v1/canned-responses")).Single(r => (string)r!["scope"]! == "Tenant")!["id"]!.GetValue<string>();
        var arabic = await asMember.SendOkAsync(HttpMethod.Post, $"/api/v1/incidents/{incident}/canned-responses/{tenantResponse}/render", new { language = "ar" });
        Assert.Equal("ar", (string?)arabic["language"]);
        Assert.Contains("INC-000001", (string)arabic["text"]!, StringComparison.Ordinal);
        Assert.StartsWith("السيد/السيدة Test user", (string)arabic["text"]!, StringComparison.Ordinal);
        var fallback = await asMember.SendOkAsync(HttpMethod.Post, $"/api/v1/incidents/{incident}/canned-responses/{groupResponse}/render", new { language = "ar" });
        Assert.True((bool)fallback["fallback"]!);
        Assert.Equal("en", (string?)fallback["language"]);
        Assert.Equal(HttpStatusCode.NotFound,
            (await asOutsider.SendAsync(HttpMethod.Post, $"/api/v1/incidents/{incident}/canned-responses/{groupResponse}/render", new { language = "en" })).StatusCode);

        // A response that comments used can still be removed; the comments keep their text.
        var used = await asMember.SendOkAsync(HttpMethod.Post, $"/api/v1/incidents/{incident}/comments",
            new { body = "Please restart", cannedResponseId = groupResponse }, HttpStatusCode.Created);
        await asLead.SendOkAsync(HttpMethod.Delete, $"/api/v1/canned-responses/{groupResponse}", expected: HttpStatusCode.NoContent);
        var kept = (await asMember.ArrayAsync($"/api/v1/incidents/{incident}/comments")).Single(c => (string)c!["id"]! == (string)used["id"]!)!;
        Assert.Equal("Please restart", (string?)kept["body"]);
        Assert.Null(kept["cannedResponseId"]);

        // The admin's manage list holds the organisation's and every group's, but not others' personal ones.
        var managed = await asAdmin.ArrayAsync("/api/v1/canned-responses?manage=true");
        Assert.Equal(["Tenant"], managed.Select(r => (string)r!["scope"]!));
    }

    [Fact]
    public async Task Saved_views_are_personal_unless_a_lead_or_a_sharer_shares_them()
    {
        var tenant = await database.CreateTenantAsync("ws");
        var admin = await database.CreateUserAsync(tenant, BuiltInRoles.TenantAdmin);
        var lead = await database.CreateUserAsync(tenant, BuiltInRoles.TeamLead);
        var member = await database.CreateUserAsync(tenant, BuiltInRoles.Agent);
        var outsider = await database.CreateUserAsync(tenant, BuiltInRoles.Agent);
        using var asAdmin = _factory.ForTenant(tenant).As(admin);
        using var asLead = _factory.ForTenant(tenant).As(lead);
        using var asMember = _factory.ForTenant(tenant).As(member);
        using var asOutsider = _factory.ForTenant(tenant).As(outsider);
        var group = await asAdmin.GroupAsync("Network", member);
        await asAdmin.SendOkAsync(HttpMethod.Put, $"/api/v1/groups/{group}/members/{lead.Id}", new { isLead = true });

        var mine = await asMember.SendOkAsync(HttpMethod.Post, "/api/v1/workspace/views",
            new { name = new { en = "My urgent" }, scope = "Personal", filters = new { open = true, priority = 1, assignee = "me", sort = "due" } }, HttpStatusCode.Created);
        Assert.Equal("me", (string?)mine["filters"]!["assignee"]);
        Assert.Equal(HttpStatusCode.BadRequest, (await asMember.SendAsync(HttpMethod.Post, "/api/v1/workspace/views",
            new { name = new { en = "Bad" }, scope = "Personal", filters = new { drop = "table" } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await asMember.SendAsync(HttpMethod.Post, "/api/v1/workspace/views",
            new { name = new { en = "Bad" }, scope = "Personal", filters = new { priority = 42 } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await asLead.SendAsync(HttpMethod.Post, "/api/v1/workspace/views",
            new { name = new { en = "All" }, scope = "Tenant", filters = new { open = true } })).StatusCode);

        await asLead.SendOkAsync(HttpMethod.Post, "/api/v1/workspace/views",
            new { name = new { en = "Network queue", ar = "طابور الشبكة" }, scope = "Group", groupId = group, filters = new { group = group.ToString(), assignee = "none" } }, HttpStatusCode.Created);
        await asAdmin.SendOkAsync(HttpMethod.Post, "/api/v1/workspace/views",
            new { name = new { en = "Breached" }, scope = "Tenant", filters = new { sla = "breached" } }, HttpStatusCode.Created);

        Assert.Equal(3, (await asMember.ArrayAsync("/api/v1/workspace/views")).Count);
        var outsiderViews = await asOutsider.ArrayAsync("/api/v1/workspace/views");
        Assert.Equal(["Breached"], outsiderViews.Select(v => (string)v!["name"]!["en"]!));
        Assert.False((bool)outsiderViews[0]!["canEdit"]!);
        Assert.Equal(HttpStatusCode.NotFound, (await asOutsider.SendAsync(HttpMethod.Delete, $"/api/v1/workspace/views/{mine.Id()}")).StatusCode);
        await asMember.SendOkAsync(HttpMethod.Delete, $"/api/v1/workspace/views/{mine.Id()}", expected: HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task The_customer_panel_shows_the_requesters_other_incidents_and_audits_their_email()
    {
        var tenant = await database.CreateTenantAsync("ws");
        var requester = await database.CreateUserAsync(tenant, BuiltInRoles.Requester);
        var agent = await database.CreateUserAsync(tenant, BuiltInRoles.Agent);
        using var asRequester = _factory.ForTenant(tenant).As(requester);
        using var asAgent = _factory.ForTenant(tenant).As(agent);
        var older = IncidentApi.IdOf(await asRequester.CreateAsync(new { title = "Printer jam" }));
        var current = IncidentApi.IdOf(await asRequester.CreateAsync(new { title = "Printer jam again" }));
        using var other = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.Requester);
        await other.CreateAsync(new { title = "Unrelated" });

        var customer = await asAgent.SendOkAsync(HttpMethod.Get, $"/api/v1/incidents/{current}/customer");

        Assert.Equal(requester.Id.ToString(), (string?)customer["person"]!["id"]);
        Assert.Equal(requester.Email, (string?)customer["email"]);
        Assert.Equal((2, 2), ((int)customer["openCount"]!, (int)customer["totalCount"]!));
        Assert.Equal([older.ToString()], customer["recent"]!.AsArray().Select(t => (string)t!["id"]!));
        Assert.Equal(HttpStatusCode.Forbidden, (await asRequester.SendAsync(HttpMethod.Get, $"/api/v1/incidents/{current}/customer")).StatusCode);
        Assert.Equal(2, (int)(await asAgent.SendOkAsync(HttpMethod.Get, $"/api/v1/incidents?requester={requester.Id}&open=true"))["totalCount"]!);

        await using var db = TestData.AppContext(database, tenant);
        Assert.True(await db.AuditEvents.AnyAsync(e => e.Action == "users.personal-data.viewed" && e.EntityId == requester.Id.ToString(), Ct));
    }

    [Fact]
    public async Task Bulk_actions_change_each_incident_like_a_single_change_and_report_each_result()
    {
        var tenant = await database.CreateTenantAsync("ws");
        var agent = await database.CreateUserAsync(tenant, BuiltInRoles.Agent);
        var requester = await database.CreateUserAsync(tenant, BuiltInRoles.Requester);
        using var asAgent = _factory.ForTenant(tenant).As(agent);
        using var asRequester = _factory.ForTenant(tenant).As(requester);
        var ids = new List<Guid>();
        for (var i = 0; i < 3; i++)
        {
            ids.Add(IncidentApi.IdOf(await asRequester.CreateAsync(new { title = $"Email bounce {i}" })));
        }

        var closed = IncidentApi.IdOf(await asAgent.CreateAsync(new { title = "Old", assigneeId = agent.Id }));
        Assert.True((await asAgent.MoveAsync(closed, "Resolved", notes: "x")).IsSuccessStatusCode);
        Assert.True((await asAgent.MoveAsync(closed, "Closed")).IsSuccessStatusCode);
        var missing = Guid.NewGuid();

        var assigned = await asAgent.SendOkAsync(HttpMethod.Post, "/api/v1/incidents/bulk", new { ids = ids.Append(closed).Append(missing), action = "Assign", assigneeId = agent.Id });
        Assert.Equal((3, 2), ((int)assigned["succeeded"]!, (int)assigned["failed"]!));
        var results = assigned["results"]!.AsArray();
        Assert.Equal("ticket-closed", (string?)results[3]!["code"]);
        Assert.Equal(404, (int)results[4]!["status"]!);

        var started = await asAgent.SendOkAsync(HttpMethod.Post, "/api/v1/incidents/bulk", new { ids, action = "Transition", state = "InProgress" });
        Assert.Equal(3, (int)started["succeeded"]!);
        var priority = await asAgent.SendOkAsync(HttpMethod.Post, "/api/v1/incidents/bulk", new { ids, action = "Priority", impact = 1, urgency = 1 });
        Assert.Equal(3, (int)priority["succeeded"]!);
        var replied = await asAgent.SendOkAsync(HttpMethod.Post, "/api/v1/incidents/bulk", new { ids, action = "Reply", body = "We are looking into the mail server." });
        Assert.Equal(3, (int)replied["succeeded"]!);
        var onHold = await asAgent.SendOkAsync(HttpMethod.Post, "/api/v1/incidents/bulk", new { ids, action = "Transition", state = "OnHold", onHoldReason = "no-such-reason" });
        Assert.Equal(3, (int)onHold["failed"]!);
        Assert.Equal("validation-failed", (string?)onHold["results"]![0]!["code"]);

        foreach (var id in ids)
        {
            var incident = await asAgent.GetIncidentAsync(id);
            Assert.Equal(("InProgress", 1), ((string?)incident["state"], (int)incident["priority"]!));
            Assert.Single(await asRequester.ArrayAsync(Comments(id)));
        }

        Assert.Equal(HttpStatusCode.BadRequest, (await asAgent.SendAsync(HttpMethod.Post, "/api/v1/incidents/bulk",
            new { ids = Enumerable.Range(0, 101).Select(_ => Guid.NewGuid()), action = "Route" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await asRequester.SendAsync(HttpMethod.Post, "/api/v1/incidents/bulk", new { ids, action = "Route" })).StatusCode);

        await using var db = TestData.AppContext(database, tenant);
        // Each change is audited like a single change.
        Assert.Equal(3, await db.AuditEvents.CountAsync(e => e.EntityType == "ticket_comment" && e.Category == AuditCategory.Create, Ct));
        Assert.True(await db.AuditEvents.CountAsync(e => e.EntityType == "ticket" && e.Category == AuditCategory.Change, Ct) >= 9);
    }

    [Fact]
    public async Task Saved_changes_are_announced_on_commit_with_ids_only()
    {
        var tenant = await database.CreateTenantAsync("ws");
        var agent = await database.CreateUserAsync(tenant, BuiltInRoles.Agent);
        using var asAgent = _factory.ForTenant(tenant).As(agent);
        var id = IncidentApi.IdOf(await asAgent.CreateAsync(new { title = "Live" }));

        await using var listener = new NpgsqlConnection(database.AppConnectionString);
        await listener.OpenAsync(Ct);
        var received = new List<string>();
        listener.Notification += (_, e) => { lock (received) received.Add(e.Payload); };
        await using (var listen = new NpgsqlCommand($"LISTEN {LiveUpdateInterceptor.Channel}", listener))
        {
            await listen.ExecuteNonQueryAsync(Ct);
        }

        await asAgent.SendOkAsync(HttpMethod.Post, Comments(id), new { body = "Secret note text", visibility = "Internal" }, HttpStatusCode.Created);
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        wait.CancelAfter(TimeSpan.FromSeconds(5));
        while (received.Count == 0)
        {
            await listener.WaitAsync(wait.Token);
        }

        var live = LiveEvent.Parse(received[0]);
        Assert.Equal((tenant.Id, LiveEvent.Ticket, (Guid?)id), (live!.Tenant, live.Kind, live.Id));
        Assert.DoesNotContain(received, p => p.Contains("Secret", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_live_hub_accepts_signed_in_people_over_server_sent_events_and_long_polling_only()
    {
        var tenant = await database.CreateTenantAsync("ws");
        using var anonymous = _factory.ForTenant(tenant);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsync("/api/v1/live/negotiate?negotiateVersion=1", null, Ct)).StatusCode);

        using var asAgent = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.Agent);
        var negotiate = await asAgent.PostAsync("/api/v1/live/negotiate?negotiateVersion=1", null, Ct);
        Assert.Equal(HttpStatusCode.OK, negotiate.StatusCode);
        var transports = (await negotiate.Content.ReadFromJsonAsync<JsonObject>(Ct))!["availableTransports"]!.AsArray()
            .Select(t => (string)t!["transport"]!).ToList();
        Assert.Equal(["ServerSentEvents", "LongPolling"], transports);
    }
}
