using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Zelvo.Data.Identity;
using Zelvo.Tickets;

namespace Zelvo.Api.Tests;

/// <summary>Custom record types on the ticket core (task K2, module 20), against PostgreSQL.</summary>
public sealed class RecordTypeApiTests(DatabaseFixture database) : IAsyncDisposable
{
    private readonly ApiFactory _factory = ApiFactory.ForDatabase(database);

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    private static object FacilitiesType(string key = "facilities", string prefix = "FAC", bool portal = true) => new
    {
        key,
        name = new { en = "Facilities request", ar = "طلب مرافق" },
        numberPrefix = prefix,
        portalEntry = portal,
        states = new object[]
        {
            new { key = "new", name = new { en = "New", ar = "جديد" }, category = "New" },
            new { key = "scheduled", name = new { en = "Scheduled", ar = "مجدول" }, category = "InProgress" },
            new { key = "on-site", name = new { en = "On site", ar = "في الموقع" }, category = "InProgress" },
            new { key = "waiting-parts", name = new { en = "Waiting for parts" }, category = "OnHold" },
            new { key = "done", name = new { en = "Done", ar = "منجز" }, category = "Resolved" },
            new { key = "closed", name = new { en = "Closed", ar = "مغلق" }, category = "Closed" },
        },
    };

    [Fact]
    public async Task An_administrator_makes_a_type_whose_records_move_through_its_own_states_on_the_core()
    {
        var tenant = await database.CreateTenantAsync("rec");
        using var admin = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.TenantAdmin);
        var agentUser = await database.CreateUserAsync(tenant, BuiltInRoles.Agent);
        using var requester = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.Requester);

        // Only administrators make types; prefixes are unique and the built-in ones are taken.
        using var agentBefore = _factory.ForTenant(tenant).As(agentUser);
        Assert.Equal(HttpStatusCode.Forbidden, (await agentBefore.SendAsync(HttpMethod.Post, "/api/v1/record-types", FacilitiesType())).StatusCode);
        Assert.Equal("prefix-taken", (await (await admin.SendAsync(HttpMethod.Post, "/api/v1/record-types", FacilitiesType("other", "INC"))).ProblemAsync()).Code);
        using var noClosed = await admin.SendAsync(HttpMethod.Post, "/api/v1/record-types", new
        {
            key = "broken", name = new { en = "Broken" }, numberPrefix = "BRK",
            states = new object[] { new { key = "new", name = new { en = "New" }, category = "New" }, new { key = "done", name = new { en = "Done" }, category = "Resolved" } },
        });
        Assert.Equal(HttpStatusCode.BadRequest, noClosed.StatusCode);
        var type = await admin.SendOkAsync(HttpMethod.Post, "/api/v1/record-types", FacilitiesType(), HttpStatusCode.Created);
        Assert.Equal(6, type["states"]!.AsArray().Count);
        Assert.Equal("prefix-taken", (await (await admin.SendAsync(HttpMethod.Post, "/api/v1/record-types", FacilitiesType("again", "FAC"))).ProblemAsync()).Code);

        // The type's permissions went to agents (and are in the role catalog with the type's name).
        var catalog = await admin.SendOkAsync(HttpMethod.Get, "/api/v1/permissions");
        Assert.Contains("records.facilities.assign", catalog["permissions"]!.AsArray().Select(p => (string)p!));
        Assert.Contains(catalog["recordPermissions"]!.AsArray(), p => (string)p!["recordType"]! == "facilities" && (string)p!["recordTypeName"]!["ar"]! == "طلب مرافق");
        using var agent = _factory.ForTenant(tenant).As(agentUser);

        // A requester raises one from the portal; it starts in the first New state with the type's number.
        var created = await requester.SendOkAsync(HttpMethod.Post, "/api/v1/records/facilities", new { title = "AC broken in room 2" }, HttpStatusCode.Created);
        var id = created.Id();
        Assert.Equal("FAC-000001", (string)created["number"]!);
        Assert.Equal("new", (string)created["stateOfType"]!["key"]!);
        Assert.Null(created["assignee"]);
        Assert.Empty(created["sla"]!.AsArray());
        Assert.Equal(HttpStatusCode.Forbidden, (await requester.SendAsync(HttpMethod.Post, "/api/v1/records/facilities", new { title = "x", priority = 1 })).StatusCode);

        // Staff see it with its clocks; others' records stay hidden from requesters.
        var seen = await agent.SendOkAsync(HttpMethod.Get, $"/api/v1/records/facilities/{id}");
        Assert.NotEmpty(seen["sla"]!.AsArray());
        using var stranger = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.Requester);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.SendAsync(HttpMethod.Get, $"/api/v1/records/facilities/{id}")).StatusCode);

        // Moving to an InProgress state needs an assignee; between states of a category the core does not change.
        Assert.Equal("assignee-required", (await (await agent.SendAsync(HttpMethod.Post, $"/api/v1/records/facilities/{id}/transitions", new { stateKey = "scheduled" })).ProblemAsync()).Code);
        await agent.SendOkAsync(HttpMethod.Put, $"/api/v1/records/facilities/{id}/assignment", new { assigneeId = agentUser.Id });
        var scheduled = await agent.SendOkAsync(HttpMethod.Post, $"/api/v1/records/facilities/{id}/transitions", new { stateKey = "scheduled" });
        Assert.Equal("InProgress", (string)scheduled["state"]!);
        var onSite = await agent.SendOkAsync(HttpMethod.Post, $"/api/v1/records/facilities/{id}/transitions", new { stateKey = "on-site" });
        Assert.Equal("InProgress", (string)onSite["state"]!);
        Assert.Equal("on-site", (string)onSite["stateOfType"]!["key"]!);
        Assert.Equal(HttpStatusCode.BadRequest, (await agent.SendAsync(HttpMethod.Post, $"/api/v1/records/facilities/{id}/transitions", new { stateKey = "done" })).StatusCode);
        var done = await agent.SendOkAsync(HttpMethod.Post, $"/api/v1/records/facilities/{id}/transitions", new { stateKey = "done", resolutionNotes = "Filter replaced." });
        Assert.Equal("Resolved", (string)done["state"]!);

        // The requester can reopen it, back to work in the first InProgress state.
        var reopened = await requester.SendOkAsync(HttpMethod.Post, $"/api/v1/records/facilities/{id}/transitions", new { stateKey = "scheduled" });
        Assert.Equal("Reopened", (string)reopened["state"]!);
        Assert.Equal("scheduled", (string)reopened["stateOfType"]!["key"]!);

        // Replies, notifications to the requester and their own list; the type's states in use cannot be removed.
        await agent.SendOkAsync(HttpMethod.Post, $"/api/v1/records/facilities/{id}/comments", new { body = "We will come at 10." }, HttpStatusCode.Created);
        await _factory.DeliverNotificationsAsync(tenant);
        var inbox = await requester.InboxAsync();
        Assert.Contains("record.received", inbox.Events());
        Assert.Contains("record.replied", inbox.Events());
        var mine = (await requester.SendOkAsync(HttpMethod.Get, "/api/v1/me/tickets"))["items"]!.AsArray().Single()!;
        Assert.Equal($"/records/facilities/{id}", (string)mine["path"]!);
        Assert.Equal("طلب مرافق", (string)mine["recordType"]!["name"]!["ar"]!);
        Assert.Single((await requester.SendOkAsync(HttpMethod.Get, "/api/v1/records/facilities"))["items"]!.AsArray());
        using var removing = await admin.SendAsync(HttpMethod.Patch, "/api/v1/record-types/facilities", new
        {
            states = new object[]
            {
                new { key = "new", name = new { en = "New" }, category = "New" },
                new { key = "done", name = new { en = "Done" }, category = "Resolved" },
                new { key = "closed", name = new { en = "Closed" }, category = "Closed" },
            },
        });
        Assert.Equal(HttpStatusCode.BadRequest, removing.StatusCode);

        // The missing Arabic name of a state is reported (LANG-5).
        Assert.Contains("states.waiting-parts", (await admin.SendOkAsync(HttpMethod.Get, "/api/v1/translations/missing")).ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Types_have_their_own_sla_policies_rules_and_approvals_and_auto_close_follows_the_states()
    {
        var tenant = await database.CreateTenantAsync("rec");
        using var admin = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.TenantAdmin);
        var approverUser = await database.CreateUserAsync(tenant, BuiltInRoles.Approver);
        using var approver = _factory.ForTenant(tenant).As(approverUser);
        using var requester = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.Requester);
        var type = await admin.SendOkAsync(HttpMethod.Post, "/api/v1/record-types", FacilitiesType("moves", "MOV"), HttpStatusCode.Created);
        var typeId = type.Id();

        // An SLA policy for this type only.
        await admin.SendOkAsync(HttpMethod.Post, "/api/v1/sla/policies", new
        {
            name = new { en = "Moves" }, recordType = "Custom", customRecordTypeId = typeId,
            targets = new[] { new { priority = 3, responseMinutes = 30, resolutionMinutes = 120 } },
        }, HttpStatusCode.Created);

        // A rule asks a named approver before the record reaches the service desk.
        await admin.SendOkAsync(HttpMethod.Post, "/api/v1/workflow-rules", new
        {
            name = new { en = "Moves need approval" }, trigger = "TicketCreated", recordType = "Custom", customRecordTypeId = typeId,
            actions = new object[]
            {
                new { type = "approval", stage = "BeforeAssignment", levels = new[] { new { name = new { en = "Facilities manager" }, mode = "Any", approvers = new[] { new { kind = "User", id = approverUser.Id } } } } },
            },
        }, HttpStatusCode.Created);

        var created = await requester.SendOkAsync(HttpMethod.Post, "/api/v1/records/moves", new { title = "Move my desk" }, HttpStatusCode.Created);
        Assert.True((bool)created["awaitingApproval"]!);
        Assert.Empty(created["nextStates"]!.AsArray());
        var pending = (await approver.SendOkAsync(HttpMethod.Get, "/api/v1/approvals?state=Pending"))["items"]!.AsArray();
        var decision = pending.Single()!;
        await approver.SendOkAsync(HttpMethod.Post, $"/api/v1/approvals/{(string)decision["id"]!}/decision", new { decision = "Approve" });
        var released = await admin.SendOkAsync(HttpMethod.Get, $"/api/v1/records/moves/{created.Id()}");
        Assert.False((bool)released["awaitingApproval"]!);
        Assert.Equal("Moves", (string)released["sla"]!.AsArray()[0]!["policy"]!["name"]!["en"]!);

        // Auto-close: a resolved record closes and its state of the type follows (first Closed state).
        await admin.SendOkAsync(HttpMethod.Put, $"/api/v1/records/moves/{created.Id()}/assignment", new { assigneeId = (string)(await admin.SendOkAsync(HttpMethod.Get, "/api/v1/me"))["id"]! });
        await admin.SendOkAsync(HttpMethod.Post, $"/api/v1/records/moves/{created.Id()}/transitions", new { stateKey = "done", resolutionNotes = "Moved." });
        await database.ExecuteAsOwnerAsync("UPDATE tickets SET resolved_at = now() - interval '30 days' WHERE id = @id", ("id", created.Id()));
        await using (var worker = IncidentApi.Worker(database))
        {
            Assert.Equal(1, await worker.GetRequiredService<TicketAutoCloseJob>().CloseTenantAsync(tenant.Id, tenant.Slug, TestContext.Current.CancellationToken));
        }

        var closed = await admin.SendOkAsync(HttpMethod.Get, $"/api/v1/records/moves/{created.Id()}");
        Assert.Equal("Closed", (string)closed["state"]!);
        Assert.Equal("closed", (string)closed["stateOfType"]!["key"]!);
    }

    [Fact]
    public async Task Per_type_permissions_can_be_granted_to_roles_and_integrations_only_for_types_that_exist()
    {
        var tenant = await database.CreateTenantAsync("rec");
        using var admin = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.TenantAdmin);
        await admin.SendOkAsync(HttpMethod.Post, "/api/v1/record-types", FacilitiesType("hr", "HRQ", portal: false) , HttpStatusCode.Created);

        using var unknown = await admin.SendAsync(HttpMethod.Put, "/api/v1/roles/executive-auditor/permissions", new { permissions = new[] { "records.nothing.read" } });
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        var role = await admin.SendOkAsync(HttpMethod.Put, "/api/v1/roles/executive-auditor/permissions", new { permissions = new[] { "records.hr.read", "incidents.read" } });
        Assert.Contains("records.hr.read", role["permissions"]!.AsArray().Select(p => (string)p!));

        // Without a portal entry, requesters cannot raise one; staff with the type's edit can, for others.
        using var requester = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.Requester);
        Assert.Equal(HttpStatusCode.Forbidden, (await requester.SendAsync(HttpMethod.Post, "/api/v1/records/hr", new { title = "Leave" })).StatusCode);
        var me = (string)(await requester.SendOkAsync(HttpMethod.Get, "/api/v1/me"))["id"]!;
        var logged = await admin.SendOkAsync(HttpMethod.Post, "/api/v1/records/hr", new { title = "Leave balance", requesterId = me, priority = 2 }, HttpStatusCode.Created);
        Assert.Equal(2, (int)logged["priority"]!);
        JsonArray types = await requester.ArrayAsync("/api/v1/record-types");
        Assert.Empty(types);
        Assert.Single(await admin.ArrayAsync("/api/v1/record-types"));
    }
}
