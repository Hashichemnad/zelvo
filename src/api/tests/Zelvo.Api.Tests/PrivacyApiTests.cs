using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Zelvo.Api.Privacy;
using Zelvo.Data.Identity;
using Zelvo.Data.Tenancy;

namespace Zelvo.Api.Tests;

/// <summary>Privacy and the UAE PDPL (GOV-5, task F5) against PostgreSQL.</summary>
public sealed class PrivacyApiTests(DatabaseFixture database) : IAsyncDisposable
{
    private readonly ApiFactory _factory = ApiFactory.ForDatabase(database).WithMasterKey().WithSetting("AttachmentScanning:Enabled", "false");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    private static object Text(string en, string? ar = null) => new { en, ar = ar ?? en + " (ar)" };

    private static async Task<JsonObject> PublishNoticeAsync(HttpClient admin, string en = "We keep your data safely.")
    {
        await admin.SendOkAsync(HttpMethod.Put, "/api/v1/privacy/notices/draft", new { title = Text("Privacy notice", "إشعار الخصوصية"), body = Text(en, "نحفظ بياناتكم بأمان.") });
        return await admin.SendOkAsync(HttpMethod.Post, "/api/v1/privacy/notices/draft/publish");
    }

    private static async Task<Guid> IncidentAsync(HttpClient requester, string title) =>
        (await requester.SendOkAsync(HttpMethod.Post, "/api/v1/incidents", new { title, description = $"{title} — details" }, HttpStatusCode.Created)).Id();

    [Fact]
    public async Task The_privacy_notice_is_versioned_published_in_both_languages_and_accepted_again_after_a_new_version()
    {
        var tenant = await database.CreateTenantAsync("pn");
        using var admin = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.TenantAdmin);
        using var visitor = _factory.ForTenant(tenant);
        using var requester = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.Requester);

        Assert.Equal(HttpStatusCode.NotFound, (await visitor.SendAsync(HttpMethod.Get, "/api/v1/privacy/notice")).StatusCode);
        Assert.Null((await requester.SendOkAsync(HttpMethod.Get, "/api/v1/me/privacy"))["notice"]);
        Assert.Equal(HttpStatusCode.Forbidden, (await requester.SendAsync(HttpMethod.Put, "/api/v1/privacy/notices/draft", new { title = Text("x") })).StatusCode);

        // A draft in English only cannot be published.
        await admin.SendOkAsync(HttpMethod.Put, "/api/v1/privacy/notices/draft", new { title = Text("Privacy notice"), body = new { en = "English only." } });
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.SendAsync(HttpMethod.Post, "/api/v1/privacy/notices/draft/publish")).StatusCode);

        var v1 = await PublishNoticeAsync(admin);
        Assert.Equal(1, (int)v1["version"]!);
        var shown = await visitor.SendOkAsync(HttpMethod.Get, "/api/v1/privacy/notice");
        Assert.Equal("نحفظ بياناتكم بأمان.", (string?)shown["body"]!["ar"]);

        var mine = await requester.SendOkAsync(HttpMethod.Get, "/api/v1/me/privacy");
        Assert.False((bool)mine["noticeAccepted"]!);
        Assert.Equal(HttpStatusCode.BadRequest, (await requester.SendAsync(HttpMethod.Post, "/api/v1/me/privacy/notice-acceptance", new { noticeId = Guid.NewGuid() })).StatusCode);
        mine = await requester.SendOkAsync(HttpMethod.Post, "/api/v1/me/privacy/notice-acceptance", new { noticeId = v1.Id() });
        Assert.True((bool)mine["noticeAccepted"]!);
        Assert.Equal("NoticeAccepted", (string?)mine["history"]![0]!["action"]);

        // Version 2 starts from version 1; once published, people accept again.
        var draft = await admin.SendOkAsync(HttpMethod.Put, "/api/v1/privacy/notices/draft", new { });
        Assert.Equal(2, (int)draft["version"]!);
        Assert.Equal("We keep your data safely.", (string?)draft["body"]!["en"]);
        await PublishNoticeAsync(admin, "Version two.");
        Assert.False((bool)(await requester.SendOkAsync(HttpMethod.Get, "/api/v1/me/privacy"))["noticeAccepted"]!);
        var versions = await admin.ArrayAsync("/api/v1/privacy/notices");
        Assert.Equal(["Published", "Superseded"], versions.Select(v => (string?)v!["state"]));

        // The consent history is never changed, not even by the database owner.
        var changed = await Assert.ThrowsAsync<PostgresException>(() => database.ExecuteAsOwnerAsync("UPDATE consent_records SET channel = 'Staff'"));
        Assert.Contains("cannot be changed", changed.MessageText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task People_grant_and_withdraw_optional_purposes_and_integrations_read_who_agrees()
    {
        var tenant = await database.CreateTenantAsync("pc");
        using var admin = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.TenantAdmin);
        var person = await database.CreateUserAsync(tenant, BuiltInRoles.Requester);
        using var requester = _factory.ForTenant(tenant).As(person);
        using var agent = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.Agent);

        Assert.Equal(HttpStatusCode.BadRequest, (await admin.SendAsync(HttpMethod.Post, "/api/v1/privacy/purposes",
            new { key = "Bad Key", name = Text("Surveys"), description = Text("Short surveys") })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.SendAsync(HttpMethod.Post, "/api/v1/privacy/purposes",
            new { key = "surveys", name = new { en = "Surveys" }, description = Text("Short surveys") })).StatusCode);
        var surveys = (await admin.SendOkAsync(HttpMethod.Post, "/api/v1/privacy/purposes",
            new { key = "surveys", name = Text("Surveys"), description = Text("Short surveys") }, HttpStatusCode.Created)).Id();

        var mine = await requester.SendOkAsync(HttpMethod.Get, "/api/v1/me/privacy");
        Assert.False((bool)mine["purposes"]![0]!["granted"]!);
        await requester.SendOkAsync(HttpMethod.Put, $"/api/v1/me/privacy/purposes/{surveys}", new { granted = true });
        var state = await admin.SendOkAsync(HttpMethod.Get, $"/api/v1/privacy/consents/{person.Id}");
        Assert.True((bool)state["purposes"]![0]!["granted"]!);
        var granted = await admin.SendOkAsync(HttpMethod.Get, "/api/v1/privacy/purposes/surveys/granted");
        Assert.Equal(person.Id.ToString(), (string?)granted["items"]![0]!["userId"]);

        mine = await requester.SendOkAsync(HttpMethod.Put, $"/api/v1/me/privacy/purposes/{surveys}", new { granted = false });
        Assert.False((bool)mine["purposes"]![0]!["granted"]!);
        Assert.Equal(["Withdrawn", "Granted"], mine["history"]!.AsArray().Select(h => (string?)h!["action"]));
        Assert.Equal(0, (int)(await admin.SendOkAsync(HttpMethod.Get, "/api/v1/privacy/purposes/surveys/granted"))["totalCount"]!);

        // Agents cannot read other people's consent; the key of a purpose never changes.
        Assert.Equal(HttpStatusCode.Forbidden, (await agent.SendAsync(HttpMethod.Get, $"/api/v1/privacy/consents/{person.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.SendAsync(HttpMethod.Put, $"/api/v1/privacy/purposes/{surveys}", new { key = "other" })).StatusCode);
    }

    [Fact]
    public async Task A_person_asks_for_a_copy_of_their_data_and_the_privacy_officer_sends_it_and_completes_the_request()
    {
        var tenant = await database.CreateTenantAsync("pa");
        var officerUser = await database.CreateUserAsync(tenant, BuiltInRoles.TenantAdmin);
        using var officer = _factory.ForTenant(tenant).As(officerUser);
        var person = await database.CreateUserAsync(tenant, BuiltInRoles.Requester);
        using var requester = _factory.ForTenant(tenant).As(person);
        using var agent = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.Agent);
        var incident = await IncidentAsync(requester, "Printer on floor 3 jams");
        await requester.SendOkAsync(HttpMethod.Post, $"/api/v1/incidents/{incident}/comments", new { body = "It jams on every page." }, HttpStatusCode.Created);
        await agent.SendOkAsync(HttpMethod.Post, $"/api/v1/incidents/{incident}/comments", new { body = "Internal: toner order", visibility = "Internal" }, HttpStatusCode.Created);

        Assert.Equal(HttpStatusCode.BadRequest, (await requester.SendAsync(HttpMethod.Post, "/api/v1/me/privacy/requests", new { details = "no type" })).StatusCode);
        var request = await requester.SendOkAsync(HttpMethod.Post, "/api/v1/me/privacy/requests", new { type = "Access", details = "Everything you hold." }, HttpStatusCode.Created);
        Assert.Equal("DSR-000001", (string?)request["number"]);
        var id = request.Id();
        Assert.True(DateTimeOffset.Parse((string)request["dueAt"]!, System.Globalization.CultureInfo.InvariantCulture) > DateTimeOffset.UtcNow.AddDays(29));

        await using (var db = TestData.AppContext(database, tenant))
        {
            var events = await db.Notifications.AsNoTracking().Select(n => new { n.EventType, n.RecipientId }).ToListAsync(Ct);
            Assert.Contains(events, n => n.EventType == "privacy.request.received" && n.RecipientId == person.Id);
            Assert.Contains(events, n => n.EventType == "privacy.request.new" && n.RecipientId == officerUser.Id);
        }

        Assert.Equal(HttpStatusCode.Forbidden, (await agent.SendAsync(HttpMethod.Get, "/api/v1/privacy/requests")).StatusCode);
        var list = await officer.SendOkAsync(HttpMethod.Get, "/api/v1/privacy/requests?open=true");
        Assert.Equal(id.ToString(), (string?)list["items"]![0]!["id"]);
        var detail = await officer.SendOkAsync(HttpMethod.Get, $"/api/v1/privacy/requests/{id}");
        Assert.Equal(person.Email, (string?)detail["subject"]!["email"]);
        Assert.Equal(1, (int)detail["holdings"]!["tickets"]!);
        Assert.Equal("OpenTickets", (string?)detail["holdings"]!["erasureRefusal"]);
        await officer.SendOkAsync(HttpMethod.Post, $"/api/v1/privacy/requests/{id}/start");

        // The copy: machine-readable JSON with the person's own data, not staff's internal notes.
        using (var response = await officer.GetAsync(new Uri($"/api/v1/privacy/requests/{id}/export", UriKind.Relative), Ct))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("application/zip", response.Content.Headers.ContentType?.MediaType);
            await using var stream = await response.Content.ReadAsStreamAsync(Ct);
            using var zip = new ZipArchive(stream);
            await using var json = await zip.GetEntry("personal-data.json")!.OpenAsync(Ct);
            var data = (await JsonNode.ParseAsync(json, cancellationToken: Ct))!;
            Assert.Equal(person.Email, (string?)data["profile"]!["email"]);
            var ticket = data["tickets"]![0]!;
            Assert.Equal("Printer on floor 3 jams", (string?)ticket["title"]);
            var texts = ticket["messages"]!.AsArray().Select(m => (string?)m!["text"]).ToList();
            Assert.Contains("It jams on every page.", texts);
            Assert.DoesNotContain("Internal: toner order", texts);
        }

        // The person downloads the same copy themselves.
        using (var own = await requester.GetAsync(new Uri("/api/v1/me/privacy/export", UriKind.Relative), Ct))
        {
            Assert.Equal(HttpStatusCode.OK, own.StatusCode);
        }

        Assert.Equal(HttpStatusCode.BadRequest, (await officer.SendAsync(HttpMethod.Post, $"/api/v1/privacy/requests/{id}/complete", new { })).StatusCode);
        var done = await officer.SendOkAsync(HttpMethod.Post, $"/api/v1/privacy/requests/{id}/complete", new { resolution = "Sent by email on request." });
        Assert.Equal("Completed", (string?)done["state"]);
        Assert.NotNull(done["exportedAt"]);
        var myRequests = await requester.ArrayAsync("/api/v1/me/privacy/requests");
        Assert.Equal("Completed", (string?)myRequests[0]!["state"]);
        Assert.Equal(HttpStatusCode.Conflict, (await requester.SendAsync(HttpMethod.Post, $"/api/v1/me/privacy/requests/{id}/withdraw")).StatusCode);

        await using (var db = TestData.AppContext(database, tenant))
        {
            var actions = await db.AuditEvents.AsNoTracking().Select(e => e.Action).ToListAsync(Ct);
            Assert.Contains("privacy.request.created", actions);
            Assert.Contains("privacy.export.downloaded", actions);
            Assert.Contains("privacy.request.completed", actions);
            Assert.Contains(await db.Notifications.AsNoTracking().Select(n => n.EventType).ToListAsync(Ct), e => e == "privacy.request.completed");
        }
    }

    [Fact]
    public async Task Erasure_anonymises_the_persons_tickets_empties_their_account_and_keeps_the_audit_log()
    {
        var tenant = await database.CreateTenantAsync("pe");
        var officerUser = await database.CreateUserAsync(tenant, BuiltInRoles.TenantAdmin);
        using var officer = _factory.ForTenant(tenant).As(officerUser);
        var person = await database.CreateUserAsync(tenant, BuiltInRoles.Requester);
        using var requester = _factory.ForTenant(tenant).As(person);
        using var agent = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.Agent);
        var incident = await IncidentAsync(requester, "My Emirates ID 784-1990-1234567-1 is wrong");
        await requester.SendOkAsync(HttpMethod.Post, $"/api/v1/incidents/{incident}/comments", new { body = "Call me on my mobile." }, HttpStatusCode.Created);
        await agent.SendOkAsync(HttpMethod.Post, $"/api/v1/incidents/{incident}/comments", new { body = "We will check the record." }, HttpStatusCode.Created);

        var request = (await officer.SendOkAsync(HttpMethod.Post, "/api/v1/privacy/requests",
            new { subjectId = person.Id, type = "Erasure", identityCheck = "Emirates ID seen at the counter" }, HttpStatusCode.Created)).Id();
        Assert.Equal(HttpStatusCode.Conflict, (await officer.SendAsync(HttpMethod.Post, $"/api/v1/privacy/requests/{request}/complete", new { resolution = "x" })).StatusCode);

        // Open tickets first; the number must be typed again; nobody erases themselves.
        using (var open = await officer.SendAsync(HttpMethod.Post, $"/api/v1/privacy/requests/{request}/erase", new { confirmNumber = "DSR-000001" }))
        {
            Assert.Equal(HttpStatusCode.Conflict, open.StatusCode);
            Assert.Equal("open-tickets", (await open.Content.ReadFromJsonAsync<ProblemBody>(Ct))!.Code);
        }

        await agent.SendOkAsync(HttpMethod.Post, $"/api/v1/incidents/{incident}/transitions", new { state = "Resolved", resolutionNotes = "Record corrected." });
        Assert.Equal(HttpStatusCode.BadRequest, (await officer.SendAsync(HttpMethod.Post, $"/api/v1/privacy/requests/{request}/erase", new { confirmNumber = "DSR-9" })).StatusCode);
        var self = (await officer.SendOkAsync(HttpMethod.Post, "/api/v1/privacy/requests",
            new { subjectId = officerUser.Id, type = "Erasure", identityCheck = "Known colleague" }, HttpStatusCode.Created));
        using (var own = await officer.SendAsync(HttpMethod.Post, $"/api/v1/privacy/requests/{self.Id()}/erase", new { confirmNumber = (string)self["number"]! }))
        {
            Assert.Equal("erase-self", (await own.Content.ReadFromJsonAsync<ProblemBody>(Ct))!.Code);
        }

        var erased = await officer.SendOkAsync(HttpMethod.Post, $"/api/v1/privacy/requests/{request}/erase", new { confirmNumber = "dsr-000001", resolution = "Erased as asked." });
        Assert.Equal("Completed", (string?)erased["state"]);
        Assert.NotNull(erased["erasedAt"]);
        Assert.True((bool)erased["subject"]!["erased"]!);
        Assert.Null(erased["subject"]!["email"]);
        Assert.Contains(_factory.Keycloak.DeletedUsers, d => d.Subject == person.Subject);

        await using (var db = TestData.AppContext(database, tenant))
        {
            var account = await db.Users.AsNoTracking().Include(u => u.Roles).SingleAsync(u => u.Id == person.Id, Ct);
            Assert.Null(account.Email);
            Assert.Null(account.DisplayName);
            Assert.Equal(UserStatus.Disabled, account.Status);
            Assert.Empty(account.Roles);
            Assert.StartsWith("erased:", account.Subject, StringComparison.Ordinal);
            var ticket = await db.Tickets.AsNoTracking().SingleAsync(t => t.Id == incident, Ct);
            Assert.Equal(string.Empty, ticket.Title);
            Assert.Null(ticket.Description);
            Assert.NotNull(ticket.AnonymisedAt);
            var comments = await db.TicketComments.AsNoTracking().Where(c => c.TicketId == incident).OrderBy(c => c.CreatedAt).ToListAsync(Ct);
            Assert.Equal(string.Empty, comments[0].Body);
            Assert.NotNull(comments[0].ErasedAt);
            Assert.Equal("We will check the record.", comments[1].Body);
            Assert.Empty(await db.Notifications.AsNoTracking().Where(n => n.RecipientId == person.Id).ToListAsync(Ct));

            // The audit log keeps every entry; screens show the person as erased.
            var events = await db.AuditEvents.AsNoTracking().Where(e => e.ActorId == person.Id.ToString()).ToListAsync(Ct);
            Assert.NotEmpty(events);
            Assert.Contains(await db.AuditEvents.AsNoTracking().Select(e => e.Action).ToListAsync(Ct), a => a == "privacy.person.erased");
        }

        var audit = await officer.SendOkAsync(HttpMethod.Get, $"/api/v1/audit/events?entityId={incident}&pageSize=100");
        var byPerson = audit["items"]!.AsArray().Where(e => (string?)e!["actor"]!["id"] == person.Id.ToString()).ToList();
        Assert.NotEmpty(byPerson);
        Assert.All(byPerson, e => Assert.True((bool)e!["actor"]!["erased"]!));
        Assert.All(byPerson, e => Assert.Null(e!["actor"]!["name"]));
        var history = await officer.ArrayAsync($"/api/v1/incidents/{incident}/history");
        Assert.DoesNotContain("784-1990-1234567-1", history.ToJsonString(), StringComparison.Ordinal);
        Assert.NotNull((await officer.SendOkAsync(HttpMethod.Get, $"/api/v1/incidents/{incident}"))["anonymisedAt"]);

        var user = await officer.SendOkAsync(HttpMethod.Get, $"/api/v1/users/{person.Id}");
        Assert.NotNull(user["erasedAt"]);
        Assert.Equal(HttpStatusCode.Conflict, (await officer.SendAsync(HttpMethod.Patch, $"/api/v1/users/{person.Id}", new { status = "Active" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await officer.SendAsync(HttpMethod.Get, $"/api/v1/privacy/requests/{request}/export")).StatusCode);
    }

    [Fact]
    public async Task Retention_periods_anonymise_old_closed_tickets_and_erase_unused_requester_accounts()
    {
        var tenant = await database.CreateTenantAsync("pr");
        using var admin = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.TenantAdmin);
        var person = await database.CreateUserAsync(tenant, BuiltInRoles.Requester);
        using var requester = _factory.ForTenant(tenant).As(person);
        var sleeper = await database.CreateUserAsync(tenant, BuiltInRoles.Requester);
        var staff = await database.CreateUserAsync(tenant, BuiltInRoles.Agent);
        using var agent = _factory.ForTenant(tenant).As(staff);
        var old = await IncidentAsync(requester, "Old broken monitor");
        var recent = await IncidentAsync(requester, "Recent broken mouse");
        foreach (var id in new[] { old, recent })
        {
            await agent.SendOkAsync(HttpMethod.Post, $"/api/v1/incidents/{id}/transitions", new { state = "Resolved", resolutionNotes = "Replaced." });
            await agent.SendOkAsync(HttpMethod.Post, $"/api/v1/incidents/{id}/transitions", new { state = "Closed" });
        }

        await database.ExecuteAsOwnerAsync("UPDATE tickets SET closed_at = now() - interval '400 days' WHERE id = @id", ("id", old));
        await database.ExecuteAsOwnerAsync("UPDATE users SET last_seen_at = now() - interval '800 days', created_at = now() - interval '900 days' WHERE id = ANY(@ids)",
            ("ids", new[] { sleeper.Id, staff.Id }));

        // Nothing is set by default; a period under 30 days is refused.
        var settings = await admin.SendOkAsync(HttpMethod.Get, "/api/v1/settings/privacy");
        Assert.Equal(30, (int)settings["requestDueDays"]!);
        Assert.Empty(settings["ticketRetentionDays"]!.AsObject());
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.SendAsync(HttpMethod.Put, "/api/v1/settings/privacy", new { ticketRetentionDays = new { Incident = 7 } })).StatusCode);
        var preview = await admin.SendOkAsync(HttpMethod.Get, "/api/v1/settings/privacy/retention-preview?incident=365&inactiveAccounts=730");
        Assert.Equal(1, (int)preview["tickets"]!["Incident"]!);
        Assert.Equal(1, (int)preview["accounts"]!);
        await admin.SendOkAsync(HttpMethod.Put, "/api/v1/settings/privacy", new { ticketRetentionDays = new { Incident = 365 }, inactiveAccountDays = 730 });

        var jobs = _factory.Services.GetRequiredService<PrivacyJobs>();
        Assert.Equal(2, await jobs.RunTenantAsync(tenant.Id, tenant.Slug, DateTimeOffset.UtcNow, Ct));
        Assert.Equal(0, await jobs.RunTenantAsync(tenant.Id, tenant.Slug, DateTimeOffset.UtcNow, Ct));

        await using var db = TestData.AppContext(database, tenant);
        var tickets = await db.Tickets.AsNoTracking().Where(t => t.Id == old || t.Id == recent).ToDictionaryAsync(t => t.Id, Ct);
        Assert.NotNull(tickets[old].AnonymisedAt);
        Assert.Equal(string.Empty, tickets[old].Title);
        Assert.Null(tickets[old].ResolutionNotes);
        Assert.Null(tickets[recent].AnonymisedAt);
        Assert.Equal("Recent broken mouse", tickets[recent].Title);
        var accounts = await db.Users.AsNoTracking().Where(u => u.Id == sleeper.Id || u.Id == staff.Id || u.Id == person.Id).ToDictionaryAsync(u => u.Id, Ct);
        Assert.NotNull(accounts[sleeper.Id].ErasedAt);
        Assert.Null(accounts[staff.Id].ErasedAt);   // staff accounts are never erased by retention
        Assert.Null(accounts[person.Id].ErasedAt);
        Assert.Contains(await db.AuditEvents.AsNoTracking().Select(e => e.Action).ToListAsync(Ct), a => a == "privacy.tickets.anonymised");

        var history = await admin.ArrayAsync($"/api/v1/incidents/{old}/history");
        Assert.DoesNotContain("Old broken monitor", history.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Anonymous_senders_must_accept_the_published_privacy_notice()
    {
        var tenant = await database.CreateTenantAsync("pv");
        using var admin = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.TenantAdmin);
        using var visitor = _factory.ForTenant(tenant);
        await admin.SendOkAsync(HttpMethod.Put, "/api/v1/settings/customer-voice", new { anonymousEnabled = true });
        var submission = new { type = "Suggestion", title = "Longer opening hours" };

        // Without a notice nothing is asked.
        await visitor.SendOkAsync(HttpMethod.Post, "/api/v1/public/customer-voice", submission, HttpStatusCode.Created);
        await PublishNoticeAsync(admin);
        using (var refused = await visitor.SendAsync(HttpMethod.Post, "/api/v1/public/customer-voice", submission))
        {
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        }

        var sent = await visitor.SendOkAsync(HttpMethod.Post, "/api/v1/public/customer-voice",
            new { type = "Suggestion", title = "Longer opening hours", privacyNoticeAccepted = true }, HttpStatusCode.Created);
        await using var db = TestData.AppContext(database, tenant);
        var ticketId = await db.Tickets.AsNoTracking().Where(t => t.Number == (string)sent["number"]!).Select(t => t.Id).SingleAsync(Ct);
        var record = await db.ConsentRecords.AsNoTracking().SingleAsync(Ct);
        Assert.Equal($"ticket:{ticketId}", record.VisitorReference);
        Assert.Equal(Zelvo.Data.Privacy.ConsentChannel.Anonymous, record.Channel);
    }
}
