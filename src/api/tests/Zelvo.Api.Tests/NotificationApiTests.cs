using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Zelvo.Data.Auditing;
using Zelvo.Data.Identity;
using Zelvo.Data.Notifications;
using Zelvo.Data.Tenancy;
using Zelvo.Notifications;

namespace Zelvo.Api.Tests;

/// <summary>Captures emails instead of sending them; answers with <see cref="Answer"/>.</summary>
internal sealed class CapturingEmailSender : IEmailSender
{
    public ConcurrentQueue<EmailMessage> Sent { get; } = new();

    public Func<EmailMessage, EmailResult> Answer { get; set; } = _ => EmailResult.Ok;

    public Task<EmailResult> SendAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        var result = Answer(message);
        if (result.Sent)
        {
            Sent.Enqueue(message);
        }

        return Task.FromResult(result);
    }

    public EmailMessage[] To(TestUser user) => [.. Sent.Where(m => m.To == user.Email)];
}

/// <summary>Requests to the notification API and the worker's delivery.</summary>
internal static class NotificationApi
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static ApiFactory WithEmailSender(this ApiFactory factory, CapturingEmailSender sender) =>
        factory.WithServices(s =>
        {
            s.RemoveAll<IEmailSender>();
            s.AddSingleton<IEmailSender>(sender);
        });

    /// <summary>Runs the worker's notification job once for <paramref name="tenant"/>, against the API's services.</summary>
    public static Task<int> DeliverNotificationsAsync(this ApiFactory factory, Tenant tenant) =>
        new NotificationDispatcher(factory.Services.GetRequiredService<IServiceScopeFactory>(), factory.Services.GetRequiredService<NotificationOptions>(),
                NullLogger<NotificationDispatcher>.Instance)
            .RunTenantAsync(tenant.Id, tenant.Slug, Ct);

    public static async Task<JsonArray> InboxAsync(this HttpClient client, bool unreadOnly = false) =>
        (await client.SendOkAsync(HttpMethod.Get, $"/api/v1/me/notifications?unreadOnly={(unreadOnly ? "true" : "false")}"))["items"]!.AsArray();

    public static async Task<int> UnreadAsync(this HttpClient client) =>
        (int)(await client.SendOkAsync(HttpMethod.Get, "/api/v1/me/notifications/unread-count"))["count"]!;

    public static IEnumerable<string> Events(this JsonArray inbox) => inbox.Select(n => (string)n!["eventType"]!);

    public static JsonNode Of(this JsonArray inbox, string eventType) => inbox.First(n => (string)n!["eventType"]! == eventType)!;
}

/// <summary>Notification engine (module 10, LANG-6, GOV-8; task B5; product owner decisions 2026-11-24) against PostgreSQL.</summary>
public sealed class NotificationApiTests : IAsyncDisposable
{
    private readonly DatabaseFixture _database;
    private readonly CapturingEmailSender _mail = new();
    private readonly ApiFactory _factory;

    public NotificationApiTests(DatabaseFixture database)
    {
        _database = database;
        _factory = ApiFactory.ForDatabase(database).WithEmailSender(_mail);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    [Fact]
    public async Task A_requester_hears_about_their_incident_in_Arabic_with_their_digits_and_agents_hear_about_assignments_in_English()
    {
        var tenant = await _database.CreateTenantAsync("ntf");
        var requester = await _database.CreateUserAsync(tenant, BuiltInRoles.Requester);
        var agentUser = await _database.CreateUserAsync(tenant, BuiltInRoles.Agent);
        using var asRequester = _factory.ForTenant(tenant).As(requester);
        using var agent = _factory.ForTenant(tenant).As(agentUser);
        using var lead = await _factory.ForTenant(tenant).SignedInAsync(_database, tenant, BuiltInRoles.TeamLead);
        await asRequester.SendOkAsync(HttpMethod.Put, "/api/v1/me/preferences", new { language = "ar", digits = "ArabicIndic" });

        // A team lead logs an incident for the requester and assigns it to the agent.
        var id = IncidentApi.IdOf(await lead.CreateAsync(new { title = "VPN-123 drops", requesterId = requester.Id, channel = "Phone", assigneeId = agentUser.Id }));
        var number = (string)(await lead.GetIncidentAsync(id))["number"]!;
        Assert.True(await _factory.DeliverNotificationsAsync(tenant) >= 2);

        // LANG-6: the requester reads Arabic, whoever wrote the incident.
        var received = (await asRequester.InboxAsync()).Of(NotificationEvents.IncidentReceived);
        Assert.Equal(("ar", $"بلاغ رقم {number}: تم استلام بلاغكم"), ((string?)received["language"], (string?)received["title"]));
        Assert.Equal($"/my-incidents/{id}", (string?)received["linkPath"]);
        var email = Assert.Single(_mail.To(requester));
        Assert.Equal(("ar", $"بلاغ رقم {number}: تم استلام بلاغكم"), (email.Language, email.Subject));
        Assert.Contains("<html lang=\"ar\" dir=\"rtl\">", email.Html, StringComparison.Ordinal);
        Assert.Contains($"<bdi>{number}</bdi>", email.Html, StringComparison.Ordinal); // Latin references keep their direction.
        Assert.Contains("بتقنية زيلفو", email.Html, StringComparison.Ordinal);
        Assert.Contains($"http://localhost:3000/ar/my-incidents/{id}", email.Html, StringComparison.Ordinal);
        Assert.Contains("بتاريخ ", email.Text, StringComparison.Ordinal);
        Assert.Matches("[٠-٩]{4}م،", email.Text); // LANG-8: the date in Arabic-Indic digits, naming its calendar.
        Assert.DoesNotContain("<script", email.Html, StringComparison.OrdinalIgnoreCase);

        // The agent reads English; the lead who assigned it hears nothing.
        var assigned = (await agent.InboxAsync()).Of(NotificationEvents.IncidentAssigned);
        Assert.Equal($"{number} is assigned to you", (string?)assigned["title"]);
        Assert.Equal($"/incidents/{id}", (string?)assigned["linkPath"]);
        Assert.Empty(await lead.InboxAsync());

        // On hold waiting for the requester, then resolved with notes: the requester hears both; the agent, who acted, nothing new.
        Assert.Equal(HttpStatusCode.OK, (await agent.MoveAsync(id, "InProgress")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await agent.MoveAsync(id, "OnHold", reason: "awaiting-requester")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await agent.MoveAsync(id, "InProgress")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await agent.MoveAsync(id, "Resolved", notes: "Router firmware updated")).StatusCode);
        await _factory.DeliverNotificationsAsync(tenant);
        var inbox = await asRequester.InboxAsync();
        Assert.Equal([NotificationEvents.IncidentResolved, NotificationEvents.IncidentAwaitingRequester, NotificationEvents.IncidentReceived], inbox.Events());
        Assert.Contains(_mail.To(requester), m => m.Text.Contains("Router firmware updated", StringComparison.Ordinal)); // Emails go out in parallel.
        Assert.Single(await agent.InboxAsync());

        // Unread count, mark read, and nobody can mark someone else's notification.
        Assert.Equal(3, await asRequester.UnreadAsync());
        var first = (string)inbox[0]!["id"]!;
        Assert.Equal(HttpStatusCode.NotFound, (await agent.SendAsync(HttpMethod.Post, $"/api/v1/me/notifications/{first}/read")).StatusCode);
        Assert.Equal(2, (int)(await asRequester.SendOkAsync(HttpMethod.Post, $"/api/v1/me/notifications/{first}/read"))["count"]!);
        Assert.Equal(2, await asRequester.UnreadAsync());
        Assert.Equal(2, (int)(await asRequester.SendOkAsync(HttpMethod.Post, "/api/v1/me/notifications/read-all"))["count"]!);
        Assert.Empty(await asRequester.InboxAsync(unreadOnly: true));
    }

    [Fact]
    public async Task New_incidents_in_a_group_queue_notify_its_members_and_major_incident_updates_reach_requesters_of_child_incidents()
    {
        var tenant = await _database.CreateTenantAsync("ntq");
        using var admin = await _factory.ForTenant(tenant).SignedInAsync(_database, tenant, BuiltInRoles.TenantAdmin);
        var member = await _database.CreateUserAsync(tenant, BuiltInRoles.Agent);
        var outsider = await _database.CreateUserAsync(tenant, BuiltInRoles.Agent);
        var desk = await admin.GroupAsync("Service desk", member);
        await admin.SendOkAsync(HttpMethod.Put, "/api/v1/assignment-rules/default", new { groupId = desk, method = "GroupQueue" });
        var requester = await _database.CreateUserAsync(tenant, BuiltInRoles.Requester);
        using var asRequester = _factory.ForTenant(tenant).As(requester);
        using var asMember = _factory.ForTenant(tenant).As(member);
        using var lead = await _factory.ForTenant(tenant).SignedInAsync(_database, tenant, BuiltInRoles.TeamLead);

        var child = IncidentApi.IdOf(await asRequester.CreateAsync(new { title = "Cannot open shared drive" }));
        await _factory.DeliverNotificationsAsync(tenant);
        var queued = (await asMember.InboxAsync()).Of(NotificationEvents.IncidentQueued);
        Assert.StartsWith("New incident in the Service desk queue", (string?)queued["title"], StringComparison.Ordinal);
        Assert.Empty(await _factory.ForTenant(tenant).As(outsider).InboxAsync());

        var major = IncidentApi.IdOf(await lead.CreateAsync(new { title = "File server outage", impact = 1, urgency = 1 }));
        Assert.Equal(HttpStatusCode.OK, (await lead.PutAsJsonAsync(IncidentApi.Of(child, "parent"), new { parentId = major }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await lead.PutAsJsonAsync(IncidentApi.Of(major, "major"), new { isMajor = true }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await lead.PostAsJsonAsync(IncidentApi.Of(major, "updates"),
            new { en = "The file server is being restarted.", ar = "تجري إعادة تشغيل خادم الملفات.", audience = "Requesters" }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await lead.PostAsJsonAsync(IncidentApi.Of(major, "updates"), new { en = "Disk array replaced.", audience = "Internal" }, Ct)).StatusCode);
        await _factory.DeliverNotificationsAsync(tenant);

        var update = (await asRequester.InboxAsync()).Of(NotificationEvents.IncidentMajorUpdate);
        Assert.Equal("The file server is being restarted.", (string?)update["text"]);
        Assert.Equal($"/my-incidents/{child}", (string?)update["linkPath"]); // Linked to their own incident.
        Assert.Single((await asRequester.InboxAsync()).Events(), e => e == NotificationEvents.IncidentMajorUpdate); // Internal updates stay internal.
    }

    [Fact]
    public async Task Administrators_switch_events_off_and_word_them_in_both_languages_with_only_their_placeholders()
    {
        var tenant = await _database.CreateTenantAsync("ntt");
        using var admin = await _factory.ForTenant(tenant).SignedInAsync(_database, tenant, BuiltInRoles.TenantAdmin);
        using var agent = await _factory.ForTenant(tenant).SignedInAsync(_database, tenant, BuiltInRoles.Agent);
        var requester = await _database.CreateUserAsync(tenant, BuiltInRoles.Requester);
        using var asRequester = _factory.ForTenant(tenant).As(requester);
        const string Settings = "/api/v1/notifications/settings";
        const string Template = "/api/v1/notifications/templates/incident.received";

        Assert.Equal(HttpStatusCode.Forbidden, (await agent.SendAsync(HttpMethod.Get, Settings)).StatusCode);
        var settings = await admin.SendOkAsync(HttpMethod.Get, Settings);
        Assert.False((bool)settings["mailServerConfigured"]!);
        var received = settings["events"]!.AsArray().First(e => (string)e!["eventType"]! == NotificationEvents.IncidentReceived)!;
        Assert.Contains("ticket.number", received["placeholders"]!.AsArray().Select(p => (string)p!));
        Assert.Equal("عرض البلاغ", (string?)received["default"]!["action"]!["ar"]);
        Assert.False((bool)received["customised"]!);

        // Unknown placeholders and stray braces are refused.
        var bad = await admin.SendAsync(HttpMethod.Put, Template, new
        {
            subject = new { en = "{{ticket.number}} {{ticket.password}}" },
            body = new { en = "Hello {{ recipient.name }" },
            inApp = new { en = "Received" },
            action = new { en = "Open" },
        });
        var problem = await bad.ProblemAsync();
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Contains("Unknown placeholder {{ticket.password}}.", problem.Errors!["subject"]);
        Assert.NotNull(problem.Errors["body"]);

        // English only: the Arabic falls back to Zelvo's wording and the gap is reported (LANG-5).
        var saved = await admin.SendOkAsync(HttpMethod.Put, Template, new
        {
            subject = new { en = "Ticket {{ ticket.number }} logged" },
            body = new { en = "Hello {{recipient.name}},\n\nWe logged “{{ticket.title}}”.\n\n<b>Thanks</b>" },
            inApp = new { en = "Logged: {{ticket.title}}" },
            action = new { en = "Track it" },
        });
        Assert.True((bool)saved["customised"]!);
        Assert.Equal(["ar"], saved["template"]!["subject"]!["missing"]!.AsArray().Select(m => (string)m!));
        var missing = await admin.SendOkAsync(HttpMethod.Get, "/api/v1/translations/missing");
        Assert.Contains(missing["items"]!.AsArray(), m => (string?)m!["entityType"] == "notification_template" && (string?)m["entityId"] == NotificationEvents.IncidentReceived);

        var preview = await admin.SendOkAsync(HttpMethod.Post, $"{Template}/preview", new { language = "en" });
        Assert.Equal("Ticket INC-000123 logged", (string?)preview["subject"]);
        Assert.Contains("&lt;b&gt;Thanks&lt;/b&gt;", (string?)preview["html"], StringComparison.Ordinal); // Templates cannot add HTML.
        var arabic = await admin.SendOkAsync(HttpMethod.Post, $"{Template}/preview", new { language = "ar" });
        Assert.Equal("بلاغ رقم INC-000123: تم استلام بلاغكم", (string?)arabic["subject"]);
        var draft = await admin.SendOkAsync(HttpMethod.Post, $"{Template}/preview", new { language = "ar", template = new { subject = new { ar = "بلاغ {{ticket.number}}" } } });
        Assert.Equal("بلاغ INC-000123", (string?)draft["subject"]);

        await asRequester.CreateAsync(new { title = "Monitor flickers" });
        await _factory.DeliverNotificationsAsync(tenant);
        Assert.StartsWith("Ticket INC-", (string?)(await asRequester.InboxAsync())[0]!["title"], StringComparison.Ordinal);

        // Email off for the event: in-app only. Event off: nothing.
        await admin.SendOkAsync(HttpMethod.Put, "/api/v1/notifications/settings/events/incident.received", new { enabled = true, email = false });
        await asRequester.CreateAsync(new { title = "Keyboard missing keys" });
        await _factory.DeliverNotificationsAsync(tenant);
        Assert.Equal(2, (await asRequester.InboxAsync()).Count);
        Assert.Single(_mail.To(requester));
        await admin.SendOkAsync(HttpMethod.Put, "/api/v1/notifications/settings/events/incident.received", new { enabled = false, email = false });
        await asRequester.CreateAsync(new { title = "Mouse broken" });
        await _factory.DeliverNotificationsAsync(tenant);
        Assert.Equal(2, (await asRequester.InboxAsync()).Count);

        var reset = await admin.SendOkAsync(HttpMethod.Delete, Template);
        Assert.False((bool)reset["customised"]!);
        var actions = (await _database.AuditAsync(tenant)).Select(a => a.Action).ToList();
        Assert.Contains(actions, a => a.StartsWith("notification_template", StringComparison.Ordinal) && a.EndsWith(".deleted", StringComparison.Ordinal));
    }

    [Fact]
    public async Task People_turn_email_off_per_event_but_SLA_alerts_always_email()
    {
        var tenant = await _database.CreateTenantAsync("ntp");
        var requester = await _database.CreateUserAsync(tenant, BuiltInRoles.Requester);
        using var asRequester = _factory.ForTenant(tenant).As(requester);
        using var agent = await _factory.ForTenant(tenant).SignedInAsync(_database, tenant, BuiltInRoles.Agent);
        const string Preferences = "/api/v1/me/notification-preferences";

        var mine = (await asRequester.SendOkAsync(HttpMethod.Get, Preferences))["items"]!.AsArray();
        Assert.DoesNotContain(mine, p => (string)p!["eventType"]! == NotificationEvents.IncidentAssigned); // Staff events are not offered to requesters.
        var staff = (await agent.SendOkAsync(HttpMethod.Get, Preferences))["items"]!.AsArray();
        var sla = staff.First(p => (string)p!["eventType"]! == NotificationEvents.SlaThreshold)!;
        Assert.Equal((true, false), ((bool)sla["email"]!, (bool)sla["canChange"]!));
        Assert.Equal(HttpStatusCode.BadRequest,
            (await agent.SendAsync(HttpMethod.Put, Preferences, new { events = new[] { new { eventType = NotificationEvents.SlaThreshold, email = false } } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await asRequester.SendAsync(HttpMethod.Put, Preferences, new { events = new[] { new { eventType = NotificationEvents.IncidentAssigned, email = false } } })).StatusCode);

        var changed = (await asRequester.SendOkAsync(HttpMethod.Put, Preferences,
            new { events = new[] { new { eventType = NotificationEvents.IncidentReceived, email = false } } }))["items"]!.AsArray();
        Assert.False((bool)changed.First(p => (string)p!["eventType"]! == NotificationEvents.IncidentReceived)!["email"]!);
        await asRequester.CreateAsync(new { title = "Projector not found" });
        await _factory.DeliverNotificationsAsync(tenant);
        Assert.Single(await asRequester.InboxAsync()); // In-app always stays on.
        Assert.Empty(_mail.To(requester));
        Assert.Contains((await _database.AuditAsync(tenant)).Select(a => a.Action), a => a.StartsWith("notification_preference", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SLA_thresholds_are_delivered_to_their_recipients_and_marked_delivered()
    {
        var tenant = await _database.CreateTenantAsync("nts");
        using var admin = await _factory.ForTenant(tenant).SignedInAsync(_database, tenant, BuiltInRoles.TenantAdmin);
        var agentUser = await _database.CreateUserAsync(tenant, BuiltInRoles.Agent);
        using var agent = _factory.ForTenant(tenant).As(agentUser);
        var desk = await admin.GroupAsync("Desk", agentUser);
        await admin.StandardPolicyAsync(response: 60, resolution: 240);
        var id = IncidentApi.IdOf(await admin.CreateAsync(new { title = "Payroll export fails", requesterId = agentUser.Id }));
        await admin.SendOkAsync(HttpMethod.Put, $"/api/v1/incidents/{id}/assignment", new { groupId = desk, assigneeId = agentUser.Id });
        await _factory.DeliverNotificationsAsync(tenant);

        await _factory.RunSlaTimersAsync(tenant, DateTimeOffset.UtcNow.AddMinutes(50));
        await _factory.DeliverNotificationsAsync(tenant);
        var alert = (await agent.InboxAsync()).Of(NotificationEvents.SlaThreshold);
        Assert.Contains("75%", (string?)alert["title"], StringComparison.Ordinal);
        Assert.Contains("response", (string?)alert["title"], StringComparison.Ordinal);
        Assert.Contains(_mail.To(agentUser), m => m.Subject == (string?)alert["title"]);
        await using var db = TestData.AppContext(_database, tenant);
        Assert.All(await db.SlaAlerts.Where(a => a.TicketId == id).ToListAsync(Ct), a => Assert.NotNull(a.DeliveredAt));
    }

    [Fact]
    public async Task Publishers_other_than_the_author_hear_about_articles_waiting_for_review()
    {
        var tenant = await _database.CreateTenantAsync("ntk");
        using var agent = await _factory.ForTenant(tenant).SignedInAsync(_database, tenant, BuiltInRoles.Agent);
        using var lead = await _factory.ForTenant(tenant).SignedInAsync(_database, tenant, BuiltInRoles.TeamLead);
        using var otherAgent = await _factory.ForTenant(tenant).SignedInAsync(_database, tenant, BuiltInRoles.Agent);
        var created = await agent.SendOkAsync(HttpMethod.Post, "/api/v1/knowledge/articles", new
        {
            title = new { en = "Reset your password", ar = "إعادة تعيين كلمة المرور" },
            body = new { en = "Open the portal." },
            visibility = "Internal",
        }, HttpStatusCode.Created);
        var article = Guid.Parse((string)created["article"]!["id"]!);
        await agent.SendOkAsync(HttpMethod.Post, $"/api/v1/knowledge/articles/{article}/submit");
        await _factory.DeliverNotificationsAsync(tenant);

        var review = (await lead.InboxAsync()).Of(NotificationEvents.KnowledgeReviewRequested);
        Assert.Equal($"/knowledge/{article}", (string?)review["linkPath"]);
        Assert.Contains("Reset your password", (string?)review["text"], StringComparison.Ordinal);
        Assert.Empty(await agent.InboxAsync()); // The author, and agents who cannot publish, hear nothing.
        Assert.Empty(await otherAgent.InboxAsync());
    }

    [Fact]
    public async Task Emails_are_retried_given_up_logged_with_masked_addresses_and_sent_again_on_request()
    {
        var tenant = await _database.CreateTenantAsync("ntd");
        var adminUser = await _database.CreateUserAsync(tenant, BuiltInRoles.TenantAdmin);
        using var admin = _factory.ForTenant(tenant).As(adminUser);
        var requester = await _database.CreateUserAsync(tenant, BuiltInRoles.Requester);
        using var asRequester = _factory.ForTenant(tenant).As(requester);

        _mail.Answer = _ => new EmailResult(false, false, "The mail server answered 451: try later");
        await asRequester.CreateAsync(new { title = "Badge reader offline" });
        await _factory.DeliverNotificationsAsync(tenant);
        var log = (await admin.SendOkAsync(HttpMethod.Get, "/api/v1/notifications/deliveries"))["items"]!.AsArray();
        var entry = Assert.Single(log)!;
        Assert.Equal(("Pending", 1), ((string?)entry["status"], (int)entry["attempts"]!));
        Assert.Equal($"{requester.Email[0]}***@example.test", (string?)entry["address"]);
        Assert.Contains("451", (string?)entry["lastError"], StringComparison.Ordinal);
        Assert.Single(await asRequester.InboxAsync()); // In-app does not wait for email.

        // A refused address is not retried.
        await using (var db = TestData.AppContext(_database, tenant))
        {
            await db.NotificationDeliveries.ExecuteUpdateAsync(set => set.SetProperty(d => d.NextAttemptAt, DateTimeOffset.UtcNow.AddMinutes(-1)), Ct);
        }

        _mail.Answer = _ => new EmailResult(false, true, "The mail server answered 550: no such user");
        await _factory.DeliverNotificationsAsync(tenant);
        var deliveryId = (long)entry["id"]!;
        var detail = await admin.SendOkAsync(HttpMethod.Get, $"/api/v1/notifications/deliveries/{deliveryId}");
        Assert.Equal(("Failed", requester.Email), ((string?)detail["delivery"]!["status"], (string?)detail["address"]));
        Assert.Contains((await _database.AuditAsync(tenant)).Select(a => a.Action), a => a == "notifications.delivery.viewed");

        _mail.Answer = _ => EmailResult.Ok;
        await admin.SendOkAsync(HttpMethod.Post, $"/api/v1/notifications/deliveries/{deliveryId}/resend", expected: HttpStatusCode.Accepted);
        var test = await admin.SendOkAsync(HttpMethod.Post, "/api/v1/notifications/test-email", expected: HttpStatusCode.Accepted);
        Assert.Equal(NotificationEvents.TestEmail, (string?)test["eventType"]);
        await _factory.DeliverNotificationsAsync(tenant);
        Assert.Single(_mail.To(requester));
        Assert.StartsWith("Test email from ", Assert.Single(_mail.To(adminUser)).Subject, StringComparison.Ordinal);
        var sent = (await admin.SendOkAsync(HttpMethod.Get, "/api/v1/notifications/deliveries?status=Sent"))["items"]!.AsArray();
        Assert.Equal(2, sent.Count);
        var audit = (await _database.AuditAsync(tenant)).Select(a => a.Action).ToList();
        Assert.Contains("notifications.delivery.resent", audit);
        Assert.Contains("notifications.test-email.queued", audit);
    }
}
