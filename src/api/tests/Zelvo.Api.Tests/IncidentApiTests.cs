using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Zelvo.Data;
using Zelvo.Data.Auditing;
using Zelvo.Data.Identity;
using Zelvo.Data.Tenancy;
using Zelvo.Data.Tickets;
using Zelvo.Search;
using Zelvo.Tickets;

namespace Zelvo.Api.Tests;

/// <summary>Requests to the incident API.</summary>
internal static class IncidentApi
{
    public static readonly Uri Incidents = new("/api/v1/incidents", UriKind.Relative);

    public static Uri Of(Guid id, string? path = null) => new($"/api/v1/incidents/{id}{(path is null ? "" : "/" + path)}", UriKind.Relative);

    public static async Task<JsonObject> CreateAsync(this HttpClient client, object body)
    {
        var response = await client.PostAsJsonAsync(Incidents, body, TestContext.Current.CancellationToken);
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return (await response.Content.ReadFromJsonAsync<JsonObject>(TestContext.Current.CancellationToken))!;
    }

    public static Guid IdOf(JsonObject incident) => Guid.Parse((string)incident["id"]!);

    public static async Task<JsonObject> GetIncidentAsync(this HttpClient client, Guid id) =>
        (await client.GetFromJsonAsync<JsonObject>(Of(id), TestContext.Current.CancellationToken))!;

    public static Task<HttpResponseMessage> MoveAsync(this HttpClient client, Guid id, string state, string? reason = null, string? notes = null) =>
        client.PostAsJsonAsync(Of(id, "transitions"), new { state, onHoldReason = reason, resolutionNotes = notes }, TestContext.Current.CancellationToken);

    public static async Task<ProblemBody> ProblemAsync(this HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<ProblemBody>(TestContext.Current.CancellationToken))!;

    /// <summary>A worker host for ticket jobs against the test database.</summary>
    public static ServiceProvider Worker(DatabaseFixture database, IMalwareScanner? scanner = null)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection().Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddZelvoData(database.AppConnectionString);
        services.AddScoped<ISearchIndexer, SearchIndexer>();
        if (scanner is not null)
        {
            services.AddSingleton(scanner);
        }

        services.AddZelvoTickets(configuration);
        services.AddSingleton<TicketAutoCloseJob>();
        services.AddSingleton<AttachmentScanJob>();
        return services.BuildServiceProvider();
    }
}

/// <summary>Incidents (module 1): reporting, numbering, priority, life cycle, parent and child, major incidents, history.</summary>
public sealed class IncidentApiTests(DatabaseFixture database) : IAsyncDisposable
{
    private readonly ApiFactory _factory = ApiFactory.ForDatabase(database);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    [Fact]
    public async Task A_requester_reports_an_incident_and_sees_only_their_own_through_a_smaller_view()
    {
        var tenant = await database.CreateTenantAsync("inc");
        var requester = await database.CreateUserAsync(tenant, BuiltInRoles.Requester);
        using var client = _factory.ForTenant(tenant).As(requester);
        using var other = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.Requester);

        var incident = await client.CreateAsync(new { title = "الطابعة لا تعمل", description = "رسالة انحشار الورق" });

        Assert.Equal("INC-000001", (string?)incident["number"]);
        Assert.Equal("New", (string?)incident["state"]);
        Assert.Equal(3, (int?)incident["priority"]); // medium impact × medium urgency
        Assert.Null(incident["assignee"]);
        Assert.False(incident.ContainsKey("impact"));
        Assert.False(incident.ContainsKey("channel"));

        var id = IncidentApi.IdOf(incident);
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync(IncidentApi.Of(id), Ct)).StatusCode);
        var list = await other.GetFromJsonAsync<JsonObject>(IncidentApi.Incidents, Ct);
        Assert.Equal(0, (int?)list!["totalCount"]);

        await using var db = TestData.AppContext(database, tenant);
        var stored = await db.Tickets.Include(t => t.Incident).SingleAsync(Ct);
        Assert.Equal((TicketChannel.Portal, requester.Id, 2, 2), (stored.Channel, stored.RequesterId, stored.Incident!.Impact, stored.Incident.Urgency));
        Assert.True(await db.AuditEvents.AnyAsync(e => e.EntityType == "ticket" && e.EntityId == id.ToString() && e.Category == AuditCategory.Create, Ct));
        var document = await db.SearchOutbox.SingleAsync(e => e.DocumentId == id.ToString(), Ct);
        Assert.Contains(SearchReaders.User(requester.Id), document.Document, StringComparison.Ordinal);
        Assert.Contains(SearchReaders.Role(BuiltInRoles.Agent), document.Document, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Requesters_may_not_set_staff_fields()
    {
        var tenant = await database.CreateTenantAsync("inc");
        using var client = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.Requester);

        var response = await client.PostAsJsonAsync(IncidentApi.Incidents, new { title = "Network down", impact = 1, channel = "Phone" }, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Numbers_are_unique_and_never_skip_even_when_many_are_reported_at_once()
    {
        var tenant = await database.CreateTenantAsync("num");
        using var client = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.Requester);

        var created = await Task.WhenAll(Enumerable.Range(0, 20).Select(i => client.CreateAsync(new { title = $"Incident {i}" })));

        var numbers = created.Select(c => (string)c["number"]!).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(Enumerable.Range(1, 20).Select(i => $"INC-{i:D6}"), numbers);

        // Another tenant has its own series.
        var second = await database.CreateTenantAsync("num");
        using var secondClient = await _factory.ForTenant(second).SignedInAsync(database, second, BuiltInRoles.Requester);
        Assert.Equal("INC-000001", (string?)(await secondClient.CreateAsync(new { title = "First" }))["number"]);
    }

    [Fact]
    public async Task An_agent_logs_assigns_and_works_an_incident_through_its_life_cycle_and_the_requester_reopens_it()
    {
        var tenant = await database.CreateTenantAsync("life");
        var requester = await database.CreateUserAsync(tenant, BuiltInRoles.Requester);
        var agentUser = await database.CreateUserAsync(tenant, BuiltInRoles.Agent);
        using var agent = _factory.ForTenant(tenant).As(agentUser);
        using var owner = _factory.ForTenant(tenant).As(requester);

        var incident = await agent.CreateAsync(new { title = "Email down", requesterId = requester.Id, channel = "Phone", impact = 1, urgency = 1, assigneeId = agentUser.Id });
        var id = IncidentApi.IdOf(incident);
        Assert.Equal(("Assigned", 1, "Phone"), ((string?)incident["state"], (int?)incident["priority"], (string?)incident["channel"]));
        Assert.Equal(agentUser.Id.ToString(), (string?)incident["assignee"]!["id"]);

        Assert.Equal(HttpStatusCode.OK, (await agent.MoveAsync(id, "InProgress")).StatusCode);
        var noReason = await agent.MoveAsync(id, "OnHold");
        Assert.Equal(HttpStatusCode.BadRequest, noReason.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await agent.MoveAsync(id, "OnHold", "not-a-reason")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await agent.MoveAsync(id, "OnHold", "awaiting-supplier")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await agent.MoveAsync(id, "InProgress")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await agent.MoveAsync(id, "Resolved")).StatusCode);

        // A requester can only reopen, and only once it is resolved.
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.MoveAsync(id, "Resolved", notes: "Fixed it myself")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await agent.MoveAsync(id, "Resolved", notes: "Restarted the mail relay")).StatusCode);
        var resolved = await owner.GetIncidentAsync(id);
        Assert.Equal(("Resolved", "Restarted the mail relay", true), ((string?)resolved["state"], (string?)resolved["resolutionNotes"], (bool?)resolved["canReopen"]));

        Assert.Equal(HttpStatusCode.OK, (await owner.MoveAsync(id, "Reopened")).StatusCode);
        var reopened = await agent.GetIncidentAsync(id);
        Assert.Equal(("Reopened", 1), ((string?)reopened["state"], (int?)reopened["reopenCount"]));

        Assert.Equal(HttpStatusCode.OK, (await agent.MoveAsync(id, "Resolved", notes: "Replaced the relay")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await agent.MoveAsync(id, "Closed")).StatusCode);

        // Closed is final.
        var again = await owner.MoveAsync(id, "Reopened");
        Assert.Equal((HttpStatusCode.Conflict, "ticket-closed"), (again.StatusCode, (await again.ProblemAsync()).Code));
        var edit = await agent.PatchAsJsonAsync(IncidentApi.Of(id), new { title = "Changed" }, Ct);
        Assert.Equal(HttpStatusCode.Conflict, edit.StatusCode);

        // A follow-up is a new incident linked to the closed one.
        var followUp = await owner.CreateAsync(new { title = "Email down again", followUpOfId = id });
        Assert.Equal((string?)incident["number"], (string?)followUp["followUpOf"]!["number"]);
    }

    [Fact]
    public async Task Work_cannot_start_without_an_assignee_and_unassigning_returns_the_incident_to_new()
    {
        var tenant = await database.CreateTenantAsync("asg");
        var agentUser = await database.CreateUserAsync(tenant, BuiltInRoles.Agent);
        var requester = await database.CreateUserAsync(tenant, BuiltInRoles.Requester);
        using var agent = _factory.ForTenant(tenant).As(agentUser);
        var id = IncidentApi.IdOf(await agent.CreateAsync(new { title = "Slow laptop", requesterId = requester.Id }));

        var start = await agent.MoveAsync(id, "InProgress");
        Assert.Equal("assignee-required", (await start.ProblemAsync()).Code);

        var notStaff = await agent.PutAsJsonAsync(IncidentApi.Of(id, "assignee"), new { assigneeId = requester.Id }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, notStaff.StatusCode);

        var assigned = await agent.PutAsJsonAsync(IncidentApi.Of(id, "assignee"), new { assigneeId = agentUser.Id }, Ct);
        Assert.Equal("Assigned", (string?)(await assigned.Content.ReadFromJsonAsync<JsonObject>(Ct))!["state"]);
        var unassigned = await agent.PutAsJsonAsync(IncidentApi.Of(id, "assignee"), new { assigneeId = (Guid?)null }, Ct);
        Assert.Equal("New", (string?)(await unassigned.Content.ReadFromJsonAsync<JsonObject>(Ct))!["state"]);

        var assignees = await agent.GetFromJsonAsync<JsonArray>(new Uri("/api/v1/incidents/assignees", UriKind.Relative), Ct);
        Assert.Contains(assignees!, a => (string?)a!["id"] == agentUser.Id.ToString());
        Assert.DoesNotContain(assignees!, a => (string?)a!["id"] == requester.Id.ToString());
    }

    [Fact]
    public async Task Impact_and_urgency_give_the_priority_from_the_tenants_matrix_and_admins_can_change_the_matrix()
    {
        var tenant = await database.CreateTenantAsync("mtx");
        using var admin = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.TenantAdmin);
        var id = IncidentApi.IdOf(await admin.CreateAsync(new { title = "Phones down", impact = 3, urgency = 3 }));
        Assert.Equal(5, (int?)(await admin.GetIncidentAsync(id))["priority"]);

        var settings = await admin.GetFromJsonAsync<JsonObject>(new Uri("/api/v1/settings/incidents", UriKind.Relative), Ct);
        var matrix = settings!["priorityMatrix"]!.AsObject();
        Assert.Equal("P1 Critical", (string?)matrix["priorities"]![0]!["name"]!["en"]);
        var body = new
        {
            priorityMatrix = new
            {
                impacts = Names(matrix["impacts"]!),
                urgencies = Names(matrix["urgencies"]!),
                priorities = Names(matrix["priorities"]!),
                matrix = new[] { new[] { 1, 1, 2 }, new[] { 1, 2, 3 }, new[] { 2, 3, 4 } },
            },
            onHoldReasons = new[] { new { key = "awaiting-requester", name = new { en = "Waiting for the requester", ar = (string?)null }, active = true } },
            numberPrefixes = new Dictionary<string, string> { ["Incident"] = "HD" },
            autoCloseDays = 3,
        };
        var saved = await admin.PutAsJsonAsync(new Uri("/api/v1/settings/incidents", UriKind.Relative), body, Ct);
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);

        var changed = await admin.PatchAsJsonAsync(IncidentApi.Of(id), new { urgency = 2 }, Ct);
        Assert.Equal(3, (int?)(await changed.Content.ReadFromJsonAsync<JsonObject>(Ct))!["priority"]);
        Assert.Equal("HD-000002", (string?)(await admin.CreateAsync(new { title = "Next" }))["number"]);

        // The missing Arabic name of the reason shows in the missing-translation report (LANG-5).
        var missing = await admin.GetStringAsync(new Uri("/api/v1/translations/missing", UriKind.Relative), Ct);
        Assert.Contains("awaiting-requester", missing, StringComparison.Ordinal);

        // A removed reason that tickets use is refused.
        var onHold = IncidentApi.IdOf(await admin.CreateAsync(new { title = "Waiting", assigneeId = (await database.CreateUserAsync(tenant, BuiltInRoles.Agent)).Id }));
        Assert.Equal(HttpStatusCode.OK, (await admin.MoveAsync(onHold, "OnHold", "awaiting-requester")).StatusCode);
        var removing = await admin.PutAsJsonAsync(new Uri("/api/v1/settings/incidents", UriKind.Relative),
            body with { onHoldReasons = [new { key = "scheduled", name = new { en = "Scheduled", ar = (string?)"مجدول" }, active = true }] }, Ct);
        Assert.Equal("on-hold-reason-in-use", (await removing.ProblemAsync()).Code);

        static object[] Names(JsonNode names) => [.. names.AsArray().Select(n => new { en = (string?)n!["name"]!["en"], ar = (string?)n["name"]!["ar"] })];
    }

    [Fact]
    public async Task Only_staff_with_the_permission_change_settings_and_invalid_matrices_are_refused()
    {
        var tenant = await database.CreateTenantAsync("mtx");
        using var agent = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.Agent);
        using var admin = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.TenantAdmin);
        var body = new
        {
            priorityMatrix = new
            {
                impacts = new[] { new { en = "A" }, new { en = "B" } },
                urgencies = new[] { new { en = "A" }, new { en = "B" }, new { en = "C" } },
                priorities = new[] { new { en = "1" }, new { en = "2" }, new { en = "3" }, new { en = "4" }, new { en = "5" } },
                matrix = new[] { new[] { 1, 2, 3 }, new[] { 2, 3, 4 } },
            },
            onHoldReasons = new[] { new { key = "x", name = new { en = "X" }, active = true } },
            numberPrefixes = new Dictionary<string, string> { ["Incident"] = "INC" },
            autoCloseDays = 5,
        };
        var uri = new Uri("/api/v1/settings/incidents", UriKind.Relative);

        Assert.Equal(HttpStatusCode.Forbidden, (await agent.PutAsJsonAsync(uri, body, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync(uri, body, Ct)).StatusCode);
    }

    [Fact]
    public async Task A_stale_version_is_refused_with_412()
    {
        var tenant = await database.CreateTenantAsync("etag");
        using var agent = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.Agent);
        var id = IncidentApi.IdOf(await agent.CreateAsync(new { title = "Monitor flickers" }));
        var first = await agent.GetAsync(IncidentApi.Of(id), Ct);
        var etag = first.Headers.ETag!.Tag;

        using var change = new HttpRequestMessage(HttpMethod.Patch, IncidentApi.Of(id)) { Content = JsonContent.Create(new { impact = 1 }) };
        change.Headers.TryAddWithoutValidation("If-Match", etag);
        Assert.Equal(HttpStatusCode.OK, (await agent.SendAsync(change, Ct)).StatusCode);

        using var stale = new HttpRequestMessage(HttpMethod.Patch, IncidentApi.Of(id)) { Content = JsonContent.Create(new { impact = 2 }) };
        stale.Headers.TryAddWithoutValidation("If-Match", etag);
        var refused = await agent.SendAsync(stale, Ct);
        Assert.Equal((HttpStatusCode.PreconditionFailed, "version-mismatch"), (refused.StatusCode, (await refused.ProblemAsync()).Code));
    }

    [Fact]
    public async Task Resolving_a_parent_resolves_its_open_children_and_grouping_is_one_level_deep()
    {
        var tenant = await database.CreateTenantAsync("par");
        using var agent = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.Agent);
        var parent = IncidentApi.IdOf(await agent.CreateAsync(new { title = "Portal outage" }));
        var child = IncidentApi.IdOf(await agent.CreateAsync(new { title = "Cannot pay", parentId = parent }));
        var other = IncidentApi.IdOf(await agent.CreateAsync(new { title = "Cannot track" }));
        Assert.Equal(HttpStatusCode.OK, (await agent.PutAsJsonAsync(IncidentApi.Of(other, "parent"), new { parentId = parent }, Ct)).StatusCode);

        var grandchild = await agent.PutAsJsonAsync(IncidentApi.Of(parent, "parent"), new { parentId = other }, Ct);
        Assert.Equal("parent-has-parent", (await grandchild.ProblemAsync()).Code);
        var self = await agent.PutAsJsonAsync(IncidentApi.Of(parent, "parent"), new { parentId = parent }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, self.StatusCode);

        var detail = await agent.GetIncidentAsync(parent);
        Assert.Equal(2, detail["children"]!.AsArray().Count);

        Assert.Equal(HttpStatusCode.OK, (await agent.MoveAsync(parent, "Resolved", notes: "Restarted the load balancer")).StatusCode);
        foreach (var id in new[] { child, other })
        {
            var resolved = await agent.GetIncidentAsync(id);
            Assert.Equal(("Resolved", "Restarted the load balancer"), ((string?)resolved["state"], (string?)resolved["resolutionNotes"]));
        }
    }

    [Fact]
    public async Task A_team_lead_declares_a_major_incident_and_requesters_of_its_children_see_the_public_updates()
    {
        var tenant = await database.CreateTenantAsync("maj");
        var requester = await database.CreateUserAsync(tenant, BuiltInRoles.Requester);
        using var lead = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.TeamLead);
        using var agent = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.Agent);
        using var owner = _factory.ForTenant(tenant).As(requester);
        var major = IncidentApi.IdOf(await lead.CreateAsync(new { title = "Data centre power failure", impact = 1, urgency = 1 }));
        var child = IncidentApi.IdOf(await owner.CreateAsync(new { title = "Cannot reach my files" }));
        Assert.Equal(HttpStatusCode.OK, (await lead.PutAsJsonAsync(IncidentApi.Of(child, "parent"), new { parentId = major }, Ct)).StatusCode);

        var early = await lead.PostAsJsonAsync(IncidentApi.Of(major, "updates"), new { en = "Investigating" }, Ct);
        Assert.Equal("not-major-incident", (await early.ProblemAsync()).Code);
        Assert.Equal(HttpStatusCode.Forbidden, (await agent.PutAsJsonAsync(IncidentApi.Of(major, "major"), new { isMajor = true }, Ct)).StatusCode);
        var declared = await lead.PutAsJsonAsync(IncidentApi.Of(major, "major"), new { isMajor = true }, Ct);
        Assert.True((bool?)(await declared.Content.ReadFromJsonAsync<JsonObject>(Ct))!["isMajor"]);

        Assert.Equal(HttpStatusCode.Created, (await lead.PostAsJsonAsync(IncidentApi.Of(major, "updates"),
            new { en = "Power is being restored.", ar = "جارٍ استعادة التيار الكهربائي.", audience = "Requesters" }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await lead.PostAsJsonAsync(IncidentApi.Of(major, "updates"),
            new { en = "Generator 2 failed to start.", audience = "Internal" }, Ct)).StatusCode);

        var seenByRequester = await owner.GetFromJsonAsync<JsonArray>(IncidentApi.Of(child, "updates"), Ct);
        var update = Assert.Single(seenByRequester!);
        Assert.Equal(("جارٍ استعادة التيار الكهربائي.", "Requesters"), ((string?)update!["body"]!["ar"], (string?)update["audience"]));
        Assert.Equal(2, (await lead.GetFromJsonAsync<JsonArray>(IncidentApi.Of(major, "updates"), Ct))!.Count);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync(IncidentApi.Of(major, "updates"), Ct)).StatusCode);
    }

    [Fact]
    public async Task The_history_shows_every_field_change_with_old_and_new_values()
    {
        var tenant = await database.CreateTenantAsync("his");
        using var agent = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.Agent);
        using var requester = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.Requester);
        var id = IncidentApi.IdOf(await agent.CreateAsync(new { title = "Scanner offline", impact = 3, urgency = 3 }));
        (await agent.PatchAsJsonAsync(IncidentApi.Of(id), new { impact = 1, title = "Scanner offline on floor 2" }, Ct)).EnsureSuccessStatusCode();

        var history = (await agent.GetFromJsonAsync<JsonArray>(IncidentApi.Of(id, "history"), Ct))!;

        var impact = history.Select(h => h!["changes"]?["impact"]).Last(c => c is not null)!;
        Assert.Equal((3, 1), ((int?)impact["old"], (int?)impact["new"]));
        var priority = history.Select(h => h!["changes"]?["priority"]).Last(c => c is not null)!;
        Assert.Equal((5, 3), ((int?)priority["old"], (int?)priority["new"]));
        var title = history.Select(h => h!["changes"]?["title"]).Last(c => c is not null)!;
        Assert.Equal("Scanner offline on floor 2", (string?)title["new"]);
        Assert.Equal(HttpStatusCode.Forbidden, (await requester.GetAsync(IncidentApi.Of(id, "history"), Ct)).StatusCode);
    }

    [Fact]
    public async Task An_executive_reads_incidents_but_cannot_change_them()
    {
        var tenant = await database.CreateTenantAsync("exe");
        using var agent = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.Agent);
        using var executive = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.ExecutiveAuditor);
        var id = IncidentApi.IdOf(await agent.CreateAsync(new { title = "Badge reader broken" }));

        Assert.Equal("Badge reader broken", (string?)(await executive.GetIncidentAsync(id))["title"]);
        Assert.Empty((await executive.GetIncidentAsync(id))["nextStates"]!.AsArray());
        Assert.Equal(HttpStatusCode.Forbidden, (await executive.PatchAsJsonAsync(IncidentApi.Of(id), new { impact = 1 }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await executive.MoveAsync(id, "Resolved", notes: "x")).StatusCode);
    }

    [Fact]
    public async Task Resolved_incidents_close_after_the_tenants_auto_close_period()
    {
        var tenant = await database.CreateTenantAsync("auto");
        using var agent = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.Agent);
        var old = IncidentApi.IdOf(await agent.CreateAsync(new { title = "Old" }));
        var recent = IncidentApi.IdOf(await agent.CreateAsync(new { title = "Recent" }));
        foreach (var id in new[] { old, recent })
        {
            (await agent.MoveAsync(id, "Resolved", notes: "Done")).EnsureSuccessStatusCode();
        }

        await database.ExecuteAsOwnerAsync("UPDATE tickets SET resolved_at = now() - interval '6 days' WHERE id = @id", ("id", old));
        await using var worker = IncidentApi.Worker(database);
        var closed = await worker.GetRequiredService<TicketAutoCloseJob>().CloseTenantAsync(tenant.Id, tenant.Slug, Ct);

        Assert.Equal(1, closed);
        Assert.Equal("Closed", (string?)(await agent.GetIncidentAsync(old))["state"]);
        Assert.Equal("Resolved", (string?)(await agent.GetIncidentAsync(recent))["state"]);
        await using var db = TestData.AppContext(database, tenant);
        var audit = await db.AuditEvents.Where(e => e.EntityType == "ticket" && e.EntityId == old.ToString()).OrderBy(e => e.Sequence).LastAsync(Ct);
        Assert.Equal((AuditActorType.System, "Auto-close"), (audit.ActorType, audit.ActorName));
    }

    [Fact]
    public async Task Incident_tables_are_isolated_between_tenants()
    {
        var first = await database.CreateTenantAsync("iso");
        var second = await database.CreateTenantAsync("iso");
        using var agent = await _factory.ForTenant(first).SignedInAsync(database, first, BuiltInRoles.Agent);
        using var otherAgent = await _factory.ForTenant(second).SignedInAsync(database, second, BuiltInRoles.Agent);
        var id = IncidentApi.IdOf(await agent.CreateAsync(new { title = "Confidential outage" }));

        Assert.Equal(HttpStatusCode.NotFound, (await otherAgent.GetAsync(IncidentApi.Of(id), Ct)).StatusCode);
        await using var db = TestData.AppContext(database, second);
        Assert.False(await db.Tickets.AnyAsync(Ct));
        Assert.False(await db.Incidents.AnyAsync(Ct));
    }
}
