using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Zelvo.Api.Identity;
using Zelvo.Connectors;
using Zelvo.Data.Connectors;
using Zelvo.Data.Identity;
using Zelvo.Data.Tickets;

namespace Zelvo.Api.Tests;

/// <summary>Identity, CRM and REST connectors and monitoring alerts (module 14, task E7) against PostgreSQL, with the connected systems in memory.</summary>
public sealed class ConnectorTests : IAsyncDisposable
{
    private readonly DatabaseFixture _database;
    private readonly FakeConnectorClients _systems = new();
    private readonly ApiFactory _factory;

    public ConnectorTests(DatabaseFixture database)
    {
        _database = database;
        _factory = ApiFactory.ForDatabase(database).WithConnectors(_systems).WithEmailSender(new CapturingEmailSender());
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    private static JsonObject Text(string en, string? ar = null) => new() { ["en"] = en, ["ar"] = ar };

    [Fact]
    public async Task An_Entra_directory_syncs_people_departments_managers_and_groups_one_way_and_offers_sign_in()
    {
        var tenant = await _database.CreateTenantAsync("e7entra");
        using var admin = await _factory.ForTenant(tenant).SignedInAsync(_database, tenant, BuiltInRoles.TenantAdmin);
        var existing = await _database.CreateUserAsync(tenant, BuiltInRoles.Agent);
        var group = await admin.GroupAsync("IT operations");
        var secret = MessagingApi.Secret("entra");
        var directory = Guid.NewGuid().ToString();
        var itGroup = Guid.NewGuid().ToString();
        var created = await admin.SendOkAsync(HttpMethod.Post, "/api/v1/connectors", new
        {
            name = "Entity directory",
            kind = "EntraId",
            entra = new { directoryId = directory, clientId = Guid.NewGuid().ToString(), groupMappings = new[] { new { externalId = itGroup, groupId = group } }, signIn = true },
            credential = secret,
        }, HttpStatusCode.Created);
        var id = created.Id();
        Assert.True((bool)created["credentialSet"]!);
        Assert.DoesNotContain(secret, created.ToJsonString(), StringComparison.Ordinal);
        Assert.EndsWith($"/realms/zelvo-{tenant.Slug}/broker/entra/endpoint", (string)created["signInRedirectUri"]!, StringComparison.Ordinal);
        var signIn = Assert.Single(_factory.Keycloak.DirectorySignInCalls);
        Assert.Equal((DirectorySignInKind.Entra, directory, secret), (signIn.Kind, signIn.SignIn!.DirectoryId, signIn.SignIn.Secret));

        // The directory: a manager, someone already in Zelvo (by email), and someone new in IT.
        _systems.Directory.AddRange([
            new DirectoryPerson("m-1", "fatima@entity.example.ae", "Fatima Al Mansoori", "Information Technology", null, "050 123 4567", "1001", true, [itGroup]),
            new DirectoryPerson("u-2", existing.Email, "Omar Saeed", "Information Technology", "m-1", null, "1002", true, [itGroup]),
            new DirectoryPerson("u-3", "khalid@entity.example.ae", "Khalid", "الموارد البشرية", "m-1", null, null, false, []),
        ]);
        await _factory.RunConnectorsAsync(tenant);
        await using (var db = TestData.AppContext(_database, tenant))
        {
            var fatima = await db.Users.SingleAsync(u => u.Email == "fatima@entity.example.ae", Ct);
            var omar = await db.Users.SingleAsync(u => u.Id == existing.Id, Ct);
            var khalid = await db.Users.SingleAsync(u => u.Email == "khalid@entity.example.ae", Ct);
            Assert.Equal(("Fatima Al Mansoori", "+971501234567", "1001", UserStatus.Active), (fatima.DisplayName, fatima.Phone, fatima.EmployeeId, fatima.Status));
            Assert.StartsWith(Zelvo.Tickets.ContactAccounts.SubjectPrefix, fatima.Subject, StringComparison.Ordinal);
            Assert.Equal((fatima.Id, "Omar Saeed"), (omar.ManagerId, omar.DisplayName));
            Assert.Equal(UserStatus.Disabled, khalid.Status);
            Assert.Equal(["الموارد البشرية", "Information Technology"],
                (await db.Departments.ToListAsync(Ct)).Select(d => d.Name.En ?? d.Name.Ar).Order(StringComparer.Ordinal).Reverse());
            Assert.Equal(new[] { fatima.Id, omar.Id }.Order(), (await db.SupportGroupMembers.Where(m => m.GroupId == group).Select(m => m.UserId).ToListAsync(Ct)).Order());
            var connector = await db.Connectors.SingleAsync(Ct);
            Assert.True(connector.LastSyncSucceeded);
            Assert.True(connector.NextSyncAt > DateTimeOffset.UtcNow.AddMinutes(50));
        }

        // Omar leaves the directory: disabled (never deleted) and out of the group; a person added by hand stays.
        _systems.Directory.RemoveAt(1);
        await admin.SendOkAsync(HttpMethod.Post, $"/api/v1/connectors/{id}/sync");
        await _factory.RunConnectorsAsync(tenant);
        await using (var db = TestData.AppContext(_database, tenant))
        {
            Assert.Equal(UserStatus.Disabled, (await db.Users.SingleAsync(u => u.Id == existing.Id, Ct)).Status);
            Assert.Single(await db.SupportGroupMembers.Where(m => m.GroupId == group).ToListAsync(Ct));
            Assert.Contains(await db.AuditEvents.Select(e => e.Action).ToListAsync(Ct), a => a == "connectors.synced");
        }

        // A new app needs the secret again; turning sign-in off removes it from Keycloak.
        using (var noSecret = await admin.SendAsync(HttpMethod.Patch, $"/api/v1/connectors/{id}", new { entra = new { directoryId = directory, clientId = Guid.NewGuid().ToString() } }))
        {
            Assert.Equal(HttpStatusCode.BadRequest, noSecret.StatusCode);
        }

        await admin.SendOkAsync(HttpMethod.Patch, $"/api/v1/connectors/{id}", new
        {
            entra = new { directoryId = directory, clientId = (string)created["settings"]!["entra"]!["clientId"]!, groupMappings = new[] { new { externalId = itGroup, groupId = group } }, signIn = false },
        });
        Assert.Null(_factory.Keycloak.DirectorySignInCalls[^1].SignIn);
    }

    [Fact]
    public async Task CRM_contacts_become_requesters_and_their_tickets_become_cases_with_notes_of_every_change()
    {
        var tenant = await _database.CreateTenantAsync("e7crm");
        using var admin = await _factory.ForTenant(tenant).SignedInAsync(_database, tenant, BuiltInRoles.TenantAdmin);
        var agentUser = await _database.CreateUserAsync(tenant, BuiltInRoles.Agent);
        using var agent = _factory.ForTenant(tenant).As(agentUser);
        var contactUser = await _database.CreateUserAsync(tenant, BuiltInRoles.Requester);
        using var contact = _factory.ForTenant(tenant).As(contactUser);
        var crm = (await admin.SendOkAsync(HttpMethod.Post, "/api/v1/connectors", new
        {
            name = "Salesforce",
            kind = "Salesforce",
            crm = new { url = "https://entity.my.salesforce.com", clientId = "3MVG9-zelvo" },
            credential = MessagingApi.Secret("sf"),
        }, HttpStatusCode.Created)).Id();
        _systems.Contacts.AddRange([
            new CrmContact("003A", contactUser.Email, "Mariam", "+971509998877", "001X", "Gulf Trading LLC"),
            new CrmContact("003B", "new.contact@gulftrading.example.ae", "Hamad", null, "001X", "Gulf Trading LLC"),
        ]);
        await _factory.RunConnectorsAsync(tenant);
        await using (var db = TestData.AppContext(_database, tenant))
        {
            var customers = await db.CustomerGroups.Include(g => g.Members).SingleAsync(Ct);
            Assert.Equal("Gulf Trading LLC", customers.Name.En);
            Assert.Equal(2, customers.Members.Count);
            Assert.True(await db.Users.AnyAsync(u => u.Email == "new.contact@gulftrading.example.ae", Ct));
            Assert.NotNull((await db.Connectors.SingleAsync(Ct)).SyncCursor);
        }

        // Her new incident becomes a case; resolving it updates the case with a note.
        var incident = (await contact.SendOkAsync(HttpMethod.Post, "/api/v1/incidents", new { title = "Invoice portal down", description = "Error 500." }, HttpStatusCode.Created)).Id();
        await _factory.RunConnectorsAsync(tenant);
        var opened = Assert.Single(_systems.Cases);
        Assert.Equal((null, "003A", "New", (string?)null), (opened.CaseId, opened.ContactId, opened.Status, opened.Note));
        Assert.Contains($"/incidents/{incident}", opened.Url, StringComparison.Ordinal);
        await agent.PostAsJsonAsync(new Uri($"/api/v1/incidents/{incident}/transitions", UriKind.Relative), new { state = "InProgress" }, Ct);
        await agent.PostAsJsonAsync(new Uri($"/api/v1/incidents/{incident}/transitions", UriKind.Relative), new { state = "Resolved", resolutionNotes = "Restarted the portal." }, Ct);
        await _factory.RunConnectorsAsync(tenant);
        var resolved = _systems.Cases.Last();
        Assert.Equal(("CASE-1", "Closed"), (resolved.CaseId, resolved.Status));
        Assert.Contains("Restarted the portal.", resolved.Note, StringComparison.Ordinal);

        // Every call is logged; bodies are masked in the integration log.
        var calls = (await admin.SendOkAsync(HttpMethod.Get, $"/api/v1/connectors/{crm}/calls"))["items"]!.AsArray();
        Assert.All(calls, c => Assert.Equal("Succeeded", (string?)c!["status"]));
        await using (var db = TestData.AppContext(_database, tenant))
        {
            var logged = await db.IntegrationLog.Where(l => l.EventType!.StartsWith("connector.salesforce")).ToListAsync(Ct);
            Assert.NotEmpty(logged);
            Assert.DoesNotContain(logged, l => l.RequestBody!.Contains("someone@example.com", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task A_REST_connector_is_tested_before_saving_and_called_by_a_workflow_rule_with_its_answer_as_a_note()
    {
        var tenant = await _database.CreateTenantAsync("e7rest");
        using var admin = await _factory.ForTenant(tenant).SignedInAsync(_database, tenant, BuiltInRoles.TenantAdmin);
        var agentUser = await _database.CreateUserAsync(tenant, BuiltInRoles.Agent);
        using var agent = _factory.ForTenant(tenant).As(agentUser);
        using var requester = await _factory.ForTenant(tenant).SignedInAsync(_database, tenant, BuiltInRoles.Requester);
        var token = MessagingApi.Secret("rest");
        var settings = new JsonObject
        {
            ["baseUrl"] = "https://crm.entity.example.ae/api",
            ["auth"] = "Bearer",
            ["headers"] = new JsonObject { ["X-Source"] = "zelvo" },
            ["operations"] = new JsonArray(new JsonObject
            {
                ["key"] = "update-case",
                ["name"] = Text("Update the CRM case", "تحديث الحالة في نظام إدارة علاقات المتعاملين"),
                ["method"] = "POST",
                ["path"] = "/cases/{{ticket.number}}?state={{ticket.state}}",
                ["body"] = """{"subject":"{{ticket.title}}","notes":"{{ticket.resolutionNotes}}","email":"{{requester.email}}"}""",
                ["results"] = new JsonArray(new JsonObject { ["key"] = "status", ["name"] = Text("Case status", "حالة الحالة"), ["path"] = "case.status" }),
                ["externalRefPath"] = "case.id",
            }),
        };

        // The test button, before saving: sample values, the request, the answer and what was read.
        var outcome = await admin.SendOkAsync(HttpMethod.Post, "/api/v1/connectors/rest/test", new { settings, credential = token, operation = "update-case" });
        Assert.Equal((true, 200, "Open", "C-1001"), ((bool)outcome["ok"]!, (int)outcome["status"]!, (string?)outcome["results"]!["status"], (string?)outcome["externalRef"]));
        var sent = _systems.RestCalls.Single();
        Assert.Equal("https://crm.entity.example.ae/api/cases/INC-000123?state=InProgress", sent.Url.AbsoluteUri);
        Assert.Equal("Sample ticket", (string?)JsonNode.Parse(sent.Body!)!["subject"]);
        Assert.DoesNotContain("requester@example.com", (string)outcome["request"]!, StringComparison.Ordinal);

        // Placeholders and paths cannot leave the base address; unknown placeholders are refused.
        settings["operations"]![0]!["path"] = "/{{ticket.nope}}";
        using (var bad = await admin.SendAsync(HttpMethod.Post, "/api/v1/connectors/rest/test", new { settings, credential = token, operation = "update-case" }))
        {
            Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        }

        settings["operations"]![0]!["path"] = "/cases/{{ticket.number}}?state={{ticket.state}}";
        var connector = (await admin.SendOkAsync(HttpMethod.Post, "/api/v1/connectors", new { name = "Case system", kind = "Rest", rest = settings, credential = token }, HttpStatusCode.Created)).Id();
        var operations = await admin.ArrayAsync("/api/v1/connectors/rest/operations");
        Assert.Equal("update-case", (string?)operations.Single()!["operations"]![0]!["key"]);

        // A rule: when an incident is resolved, update the case.
        await admin.SendOkAsync(HttpMethod.Post, "/api/v1/workflow-rules", new
        {
            name = Text("Resolved incidents update the CRM", "الحوادث المحلولة تحدّث نظام إدارة علاقات المتعاملين"),
            trigger = "StateChanged",
            recordType = "Incident",
            conditions = new JsonArray(new JsonObject { ["field"] = "state", ["op"] = "in", ["value"] = new JsonArray("Resolved") }),
            actions = new JsonArray(new JsonObject { ["type"] = "connector", ["connectorId"] = connector.ToString(), ["operation"] = "update-case" }),
        }, HttpStatusCode.Created);
        using (var unknown = await admin.SendAsync(HttpMethod.Post, "/api/v1/workflow-rules", new
        {
            name = Text("Broken"),
            trigger = "StateChanged",
            recordType = "Incident",
            actions = new JsonArray(new JsonObject { ["type"] = "connector", ["connectorId"] = connector.ToString(), ["operation"] = "nope" }),
        }))
        {
            Assert.Contains("operation-unknown", (await unknown.JsonAsync())["problems"]!.ToJsonString(), StringComparison.Ordinal);
        }

        var incident = (await requester.SendOkAsync(HttpMethod.Post, "/api/v1/incidents", new { title = "Card reader broken", description = "Gate 2." }, HttpStatusCode.Created)).Id();
        await agent.PostAsJsonAsync(new Uri($"/api/v1/incidents/{incident}/transitions", UriKind.Relative), new { state = "InProgress" }, Ct);
        _systems.RestCalls.Clear();
        await agent.PostAsJsonAsync(new Uri($"/api/v1/incidents/{incident}/transitions", UriKind.Relative), new { state = "Resolved", resolutionNotes = "Replaced the reader." }, Ct);
        // Nothing is sent inside the save; the worker sends it.
        Assert.Empty(_systems.RestCalls);
        await _factory.RunConnectorsAsync(tenant);
        var call = Assert.Single(_systems.RestCalls);
        Assert.Contains("state=Resolved", call.Url.Query, StringComparison.Ordinal);
        Assert.Equal("Replaced the reader.", (string?)JsonNode.Parse(call.Body!)!["notes"]);
        var notes = (await agent.SendOkAsync(HttpMethod.Get, $"/api/v1/incidents/{incident}/comments"))["items"]!.AsArray()
            .Where(c => (string?)c!["visibility"] == "Internal").Select(c => (string)c!["body"]!).ToList();
        Assert.Equal("Update the CRM case\nCase status: Open", Assert.Single(notes));
        await using (var db = TestData.AppContext(_database, tenant))
        {
            Assert.Equal("C-1001", (await db.ConnectorLinks.SingleAsync(l => l.Entity == ConnectorEntity.Ticket, Ct)).ExternalId);
        }

        // A refusal that will not improve fails at once and can be sent again.
        _systems.RestStatus = 422;
        await agent.PostAsJsonAsync(new Uri($"/api/v1/incidents/{incident}/transitions", UriKind.Relative), new { state = "Reopened" }, Ct);
        await agent.PostAsJsonAsync(new Uri($"/api/v1/incidents/{incident}/transitions", UriKind.Relative), new { state = "Resolved", resolutionNotes = "Again." }, Ct);
        await _factory.RunConnectorsAsync(tenant);
        var failed = (await admin.SendOkAsync(HttpMethod.Get, $"/api/v1/connectors/{connector}/calls?status=Failed"))["items"]!.AsArray().Single()!;
        Assert.StartsWith("refused", (string?)failed["error"], StringComparison.Ordinal);
        _systems.RestStatus = 200;
        await admin.SendOkAsync(HttpMethod.Post, $"/api/v1/connectors/{connector}/calls/{(long)failed["id"]!}/resend");
        await _factory.RunConnectorsAsync(tenant);
        Assert.Empty((await admin.SendOkAsync(HttpMethod.Get, $"/api/v1/connectors/{connector}/calls?status=Failed"))["items"]!.AsArray());
    }

    [Fact]
    public async Task Monitoring_alerts_raise_incidents_add_to_them_when_they_fire_again_and_resolve_them_when_they_clear()
    {
        var tenant = await _database.CreateTenantAsync("e7mon");
        using var admin = await _factory.ForTenant(tenant).SignedInAsync(_database, tenant, BuiltInRoles.TenantAdmin);
        var agentUser = await _database.CreateUserAsync(tenant, BuiltInRoles.Agent);
        var group = await admin.GroupAsync("Infrastructure", agentUser);
        using var prometheus = _factory.ForTenant(tenant);
        var created = await admin.SendOkAsync(HttpMethod.Post, "/api/v1/monitoring/sources", new { name = "Prometheus", format = "Alertmanager", groupId = group, resolveOnClear = true },
            HttpStatusCode.Created);
        var source = Guid.Parse((string)created["source"]!["id"]!);
        var token = (string)created["token"]!;
        Assert.EndsWith($"/hooks/{tenant.Slug}/monitoring/{source}", (string)created["source"]!["webhookUrl"]!, StringComparison.Ordinal);
        Assert.DoesNotContain(token, (await admin.ArrayAsync("/api/v1/monitoring/sources")).ToJsonString(), StringComparison.Ordinal);

        static JsonObject Alertmanager(string status, string fingerprint) => new()
        {
            ["version"] = "4",
            ["status"] = status,
            ["alerts"] = new JsonArray(new JsonObject
            {
                ["status"] = status,
                ["fingerprint"] = fingerprint,
                ["labels"] = new JsonObject { ["alertname"] = "DiskFull", ["severity"] = "critical", ["instance"] = "db-01" },
                ["annotations"] = new JsonObject { ["summary"] = "Disk almost full on db-01", ["description"] = "Free space below 5 %." },
            }),
        };

        async Task<HttpResponseMessage> Post(JsonObject body, string? bearer)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/hooks/monitoring/{source}") { Content = JsonContent.Create(body) };
            if (bearer is not null)
            {
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bearer);
            }

            return await prometheus.SendAsync(request, Ct);
        }

        Assert.Equal(HttpStatusCode.Unauthorized, (await Post(Alertmanager("firing", "abc"), MessagingApi.Secret("wrong"))).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await Post(Alertmanager("firing", "abc"), token)).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await Post(Alertmanager("firing", "abc"), token)).StatusCode);
        Ticket ticket;
        await using (var db = TestData.AppContext(_database, tenant))
        {
            ticket = await db.Tickets.Include(t => t.Incident).Include(t => t.Requester).SingleAsync(Ct);
            Assert.Equal(("Disk almost full on db-01", TicketChannel.Monitoring, group, 1, 1), (ticket.Title, ticket.Channel, ticket.GroupId, ticket.Incident!.Impact, ticket.Incident.Urgency));
            Assert.Equal(("Prometheus", UserStatus.Disabled), (ticket.Requester!.DisplayName, ticket.Requester.Status));
            Assert.Equal(2, (await db.MonitoringAlerts.SingleAsync(Ct)).Count);
            Assert.Single(await db.TicketComments.Where(c => c.Visibility == CommentVisibility.Internal).ToListAsync(Ct));
        }

        Assert.Equal(HttpStatusCode.Accepted, (await Post(Alertmanager("resolved", "abc"), token)).StatusCode);
        await using (var db = TestData.AppContext(_database, tenant))
        {
            Assert.Equal(TicketState.Resolved, (await db.Tickets.SingleAsync(Ct)).State);
            Assert.Equal(2, await db.TicketComments.CountAsync(c => c.Visibility == CommentVisibility.Internal, Ct));
        }

        // A renewed token replaces the old one at once. Zabbix's preset maps its own fields.
        var renewed = (string)(await admin.SendOkAsync(HttpMethod.Post, $"/api/v1/monitoring/sources/{source}/token"))["token"]!;
        Assert.Equal(HttpStatusCode.Unauthorized, (await Post(Alertmanager("firing", "def"), token)).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await Post(Alertmanager("firing", "def"), renewed)).StatusCode);
        var zabbix = await admin.SendOkAsync(HttpMethod.Post, "/api/v1/monitoring/sources", new { name = "Zabbix", format = "Generic", preset = "zabbix" }, HttpStatusCode.Created);
        using var zabbixPost = await prometheus.PostAsJsonAsync(new Uri($"/api/v1/hooks/monitoring/{(string)zabbix["source"]!["id"]!}?token={Uri.EscapeDataString((string)zabbix["token"]!)}", UriKind.Relative),
            new { eventId = "777", status = "PROBLEM", severity = "Average", subject = "Problem: high CPU on app-02", message = "CPU above 90 % for 5 minutes" }, Ct);
        var outcome = await zabbixPost.JsonAsync();
        Assert.Equal(1, (int)outcome["created"]!);
        await using (var db = TestData.AppContext(_database, tenant))
        {
            var cpu = await db.Tickets.Include(t => t.Incident).SingleAsync(t => t.Title == "Problem: high CPU on app-02", Ct);
            Assert.Equal((2, 2), (cpu.Incident!.Impact, cpu.Incident.Urgency));
        }

        var alerts = (await admin.SendOkAsync(HttpMethod.Get, $"/api/v1/monitoring/sources/{source}/alerts"))["items"]!.AsArray();
        Assert.Equal(["Firing", "Resolved"], alerts.Select(a => (string?)a!["state"]));
    }
}
