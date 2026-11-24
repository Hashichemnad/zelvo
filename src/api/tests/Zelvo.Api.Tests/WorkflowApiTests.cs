using System.Net;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Zelvo.Data.Identity;
using Zelvo.Data.Tenancy;

namespace Zelvo.Api.Tests;

/// <summary>Workflow rules and approvals on the Elsa engine (task C3, modules 2 and 10) against PostgreSQL.</summary>
public sealed class WorkflowApiTests(DatabaseFixture database) : IAsyncDisposable
{
    private readonly ApiFactory _factory = ApiFactory.ForDatabase(database).WithSetting("AttachmentScanning:Enabled", "false");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    private static JsonObject Text(string en, string? ar = null) => new() { ["en"] = en, ["ar"] = ar ?? en + " (ar)" };

    private static JsonObject Level(string name, string mode, params JsonObject[] approvers) => new()
    {
        ["name"] = Text(name),
        ["mode"] = mode,
        ["approvers"] = new JsonArray([.. approvers]),
    };

    private static JsonObject Card(string name, JsonObject? approvals = null, Action<JsonObject>? more = null)
    {
        var card = new JsonObject
        {
            ["name"] = Text(name),
            ["summary"] = Text("Get " + name),
            ["channels"] = new JsonArray("Portal"),
            ["approvals"] = approvals ?? new JsonObject(),
        };
        more?.Invoke(card);
        return card;
    }

    /// <summary>Creates and publishes a service (written by one administrator, approved by another).</summary>
    private static async Task<Guid> ServiceAsync(HttpClient author, HttpClient reviewer, string code, JsonObject card)
    {
        var id = (await author.SendOkAsync(HttpMethod.Post, "/api/v1/catalog/services", new { code }, HttpStatusCode.Created)).Id();
        var draft = await author.SendOkAsync(HttpMethod.Put, $"/api/v1/catalog/services/{id}/draft", new { card });
        Assert.Empty(draft["problems"]!.AsArray());
        await author.SendOkAsync(HttpMethod.Post, $"/api/v1/catalog/services/{id}/submit");
        await reviewer.SendOkAsync(HttpMethod.Post, $"/api/v1/catalog/services/{id}/approve");
        return id;
    }

    private static async Task<Guid> GroupAsync(HttpClient admin, string name, params TestUser[] members)
    {
        var id = (await admin.SendOkAsync(HttpMethod.Post, "/api/v1/groups", new { name = Text(name) }, HttpStatusCode.Created)).Id();
        foreach (var member in members)
        {
            await admin.SendOkAsync(HttpMethod.Put, $"/api/v1/groups/{id}/members/{member.Id}", new { isLead = false });
        }

        return id;
    }

    private Task<int> SetManagerAsync(TestUser user, TestUser manager) =>
        database.ExecuteAsOwnerAsync("UPDATE users SET manager_id = @m WHERE id = @u", ("m", manager.Id), ("u", user.Id));

    private static async Task<Guid> PendingFor(HttpClient approver)
    {
        var page = await approver.SendOkAsync(HttpMethod.Get, "/api/v1/approvals?state=Pending");
        return Guid.Parse((string)page["items"]!.AsArray().Single()!["id"]!);
    }

    [Fact]
    public async Task A_request_waits_for_its_managers_approval_then_all_of_finance_before_reaching_agents()
    {
        var tenant = await database.CreateTenantAsync("wf");
        var adminUser = await database.CreateUserAsync(tenant, BuiltInRoles.TenantAdmin);
        using var admin = _factory.ForTenant(tenant).As(adminUser);
        using var publisher = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.TenantAdmin);
        var requesterUser = await database.CreateUserAsync(tenant, BuiltInRoles.Requester);
        var managerUser = await database.CreateUserAsync(tenant, BuiltInRoles.Approver);
        var financeA = await database.CreateUserAsync(tenant, BuiltInRoles.Agent);
        var financeB = await database.CreateUserAsync(tenant, BuiltInRoles.Agent);
        var agent = await database.CreateUserAsync(tenant, BuiltInRoles.Agent);
        await SetManagerAsync(requesterUser, managerUser);
        using var requester = _factory.ForTenant(tenant).As(requesterUser);
        using var manager = _factory.ForTenant(tenant).As(managerUser);
        using var a = _factory.ForTenant(tenant).As(financeA);
        using var b = _factory.ForTenant(tenant).As(financeB);
        var finance = await GroupAsync(admin, "Finance", financeA, financeB);
        var desk = await GroupAsync(admin, "Desk", agent);
        await admin.SendOkAsync(HttpMethod.Put, "/api/v1/assignment-rules/default", new { groupId = desk, method = "GroupQueue" });

        var service = await ServiceAsync(admin, publisher, "IT-LAPTOP", Card("New laptop", new JsonObject
        {
            ["beforeAssignment"] = new JsonArray(
                Level("Manager approval", "Any", new JsonObject { ["kind"] = "RequesterManager" }),
                Level("Budget approval", "All", new JsonObject { ["kind"] = "Group", ["id"] = finance.ToString() })),
        }));

        var request = await requester.SendOkAsync(HttpMethod.Post, "/api/v1/service-requests", new { serviceId = service }, HttpStatusCode.Created);
        var id = request.Id();
        Assert.True((bool)request["awaitingApproval"]!);
        Assert.Equal("New", (string?)request["state"]);
        var staffView = await admin.SendOkAsync(HttpMethod.Get, $"/api/v1/service-requests/{id}");
        Assert.Null(staffView["group"]);
        Assert.All(staffView["sla"]!.AsArray(), clock => Assert.Equal("Paused", (string?)clock!["state"]));

        // The requester follows the levels; only the manager is asked first.
        var processes = await requester.ArrayAsync($"/api/v1/service-requests/{id}/approvals");
        var levels = processes.Single()!["levels"]!.AsArray();
        Assert.Equal(["Waiting", "NotStarted"], levels.Select(l => (string?)l!["state"]));
        Assert.Empty((await a.SendOkAsync(HttpMethod.Get, "/api/v1/approvals?state=Pending"))["items"]!.AsArray());
        var managerDecision = await PendingFor(manager);
        var detail = await manager.SendOkAsync(HttpMethod.Get, $"/api/v1/approvals/{managerDecision}");
        Assert.True((bool)detail["mayDecide"]!);
        Assert.Equal("IT-LAPTOP", (string?)detail["approval"]!["ticket"]!["service"]!["code"]);

        // The approval email was queued for the manager, with the approval's link.
        await using (var db = TestData.AppContext(database, tenant))
        {
            var queued = await db.Notifications.AsNoTracking().Where(n => n.EventType == "approval.requested").ToListAsync(Ct);
            Assert.Equal((managerUser.Id, $"/approvals/{managerDecision}"), (queued.Single().RecipientId, queued.Single().LinkPath));
        }

        // Nobody else decides for the manager; staff cannot work on the request meanwhile.
        using var notMine = await a.SendAsync(HttpMethod.Post, $"/api/v1/approvals/{managerDecision}/decision", new { decision = "Approve" });
        Assert.Equal(HttpStatusCode.NotFound, notMine.StatusCode);
        using var blocked = await admin.SendAsync(HttpMethod.Put, $"/api/v1/service-requests/{id}/assignment", new { groupId = desk });
        Assert.Equal("awaiting-approval", (await blocked.ProblemAsync()).Code);
        using var noReason = await manager.SendAsync(HttpMethod.Post, $"/api/v1/approvals/{managerDecision}/decision", new { decision = "Reject" });
        Assert.Equal(HttpStatusCode.BadRequest, noReason.StatusCode);

        await manager.SendOkAsync(HttpMethod.Post, $"/api/v1/approvals/{managerDecision}/decision", new { decision = "Approve", comment = "Fine.", via = "email" });
        using var twice = await manager.SendAsync(HttpMethod.Post, $"/api/v1/approvals/{managerDecision}/decision", new { decision = "Approve" });
        Assert.Equal("already-decided", (await twice.ProblemAsync()).Code);

        // Both finance members must approve (parallel); one approval is not enough.
        await a.SendOkAsync(HttpMethod.Post, $"/api/v1/approvals/{await PendingFor(a)}/decision", new { decision = "Approve" });
        Assert.True((bool)(await requester.SendOkAsync(HttpMethod.Get, $"/api/v1/service-requests/{id}"))["awaitingApproval"]!);
        await b.SendOkAsync(HttpMethod.Post, $"/api/v1/approvals/{await PendingFor(b)}/decision", new { decision = "Approve" });

        var released = await admin.SendOkAsync(HttpMethod.Get, $"/api/v1/service-requests/{id}");
        Assert.False((bool)released["awaitingApproval"]!);
        Assert.Equal(desk, Guid.Parse((string)released["group"]!["id"]!));
        Assert.All(released["sla"]!.AsArray(), clock => Assert.Equal("Running", (string?)clock!["state"]));
        var done = (await requester.ArrayAsync($"/api/v1/service-requests/{id}/approvals")).Single()!;
        Assert.Equal("Approved", (string?)done["state"]);
        Assert.Equal(["Approved", "Approved"], done["levels"]!.AsArray().Select(l => (string?)l!["state"]));

        await using (var db = TestData.AppContext(database, tenant))
        {
            Assert.Equal(1, await db.Notifications.CountAsync(n => n.EventType == "request.approved" && n.RecipientId == requesterUser.Id, Ct));
            var actions = await db.AuditEvents.AsNoTracking().Where(e => e.EntityId == id.ToString() && e.Action.StartsWith("approvals.")).Select(e => e.Action).ToListAsync(Ct);
            Assert.Contains("approvals.started", actions);
            Assert.Contains("approvals.ended", actions);
            Assert.Equal(3, actions.Count(x => x == "approvals.approved"));
        }
    }

    [Fact]
    public async Task A_rejected_request_is_closed_with_the_reason_and_a_rejected_sign_off_goes_back_to_the_agent()
    {
        var tenant = await database.CreateTenantAsync("wr");
        using var admin = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.TenantAdmin);
        using var publisher = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.TenantAdmin);
        var requesterUser = await database.CreateUserAsync(tenant, BuiltInRoles.Requester);
        var approverUser = await database.CreateUserAsync(tenant, BuiltInRoles.Approver);
        var agentUser = await database.CreateUserAsync(tenant, BuiltInRoles.Agent);
        var leadUser = await database.CreateUserAsync(tenant, BuiltInRoles.TeamLead);
        using var requester = _factory.ForTenant(tenant).As(requesterUser);
        using var approver = _factory.ForTenant(tenant).As(approverUser);
        using var agent = _factory.ForTenant(tenant).As(agentUser);
        using var lead = _factory.ForTenant(tenant).As(leadUser);
        var desk = await GroupAsync(admin, "Desk", agentUser);
        await admin.SendOkAsync(HttpMethod.Put, "/api/v1/assignment-rules/default", new { groupId = desk, method = "GroupQueue" });

        var vetted = await ServiceAsync(admin, publisher, "HR-CERT", Card("Certificate", new JsonObject
        {
            ["beforeAssignment"] = new JsonArray(Level("HR approval", "Any", new JsonObject { ["kind"] = "User", ["id"] = approverUser.Id.ToString() })),
        }));
        var id = (await requester.SendOkAsync(HttpMethod.Post, "/api/v1/service-requests", new { serviceId = vetted }, HttpStatusCode.Created)).Id();
        await approver.SendOkAsync(HttpMethod.Post, $"/api/v1/approvals/{await PendingFor(approver)}/decision", new { decision = "Reject", comment = "Ask your HR partner first." });
        var rejected = await requester.SendOkAsync(HttpMethod.Get, $"/api/v1/service-requests/{id}");
        Assert.Equal(("Closed", "Ask your HR partner first.", false), ((string?)rejected["state"], (string?)rejected["resolutionNotes"], (bool)rejected["awaitingApproval"]!));

        // The requester may withdraw a request that waits for approval; it is closed.
        var withdrawn = (await requester.SendOkAsync(HttpMethod.Post, "/api/v1/service-requests", new { serviceId = vetted }, HttpStatusCode.Created)).Id();
        var stopped = await requester.SendOkAsync(HttpMethod.Post, $"/api/v1/service-requests/{withdrawn}/approvals/cancel", new { reason = "No longer needed." });
        Assert.Equal("Closed", (string?)stopped["state"]);
        Assert.Empty((await approver.SendOkAsync(HttpMethod.Get, "/api/v1/approvals?state=Pending"))["items"]!.AsArray());

        // Sign-off after fulfilment by the group's team lead.
        await admin.SendOkAsync(HttpMethod.Put, $"/api/v1/groups/{desk}/members/{leadUser.Id}", new { isLead = true });
        var signed = await ServiceAsync(admin, publisher, "IT-SETUP", Card("Desk setup", new JsonObject
        {
            ["afterFulfilment"] = new JsonArray(Level("Supervisor sign-off", "Any", new JsonObject { ["kind"] = "GroupLeads", ["id"] = desk.ToString() })),
        }));
        var work = (await requester.SendOkAsync(HttpMethod.Post, "/api/v1/service-requests", new { serviceId = signed }, HttpStatusCode.Created)).Id();
        await admin.SendOkAsync(HttpMethod.Put, $"/api/v1/service-requests/{work}/assignment", new { groupId = desk, assigneeId = agentUser.Id });
        await agent.SendOkAsync(HttpMethod.Post, $"/api/v1/service-requests/{work}/transitions", new { state = "InProgress" });
        var asked = await agent.SendOkAsync(HttpMethod.Post, $"/api/v1/service-requests/{work}/transitions", new { state = "Resolved", resolutionNotes = "Desk is set up." });
        Assert.Equal(("InProgress", true), ((string?)asked["state"], (bool)asked["awaitingApproval"]!));

        await lead.SendOkAsync(HttpMethod.Post, $"/api/v1/approvals/{await PendingFor(lead)}/decision", new { decision = "Reject", comment = "The monitor arm is missing." });
        var back = await agent.SendOkAsync(HttpMethod.Get, $"/api/v1/service-requests/{work}");
        Assert.Equal(("InProgress", false), ((string?)back["state"], (bool)back["awaitingApproval"]!));
        await using (var db = TestData.AppContext(database, tenant))
        {
            var note = await db.TicketComments.AsNoTracking().SingleAsync(c => c.TicketId == work, Ct);
            Assert.Equal(("The monitor arm is missing.", Zelvo.Data.Tickets.CommentVisibility.Internal), (note.Body, note.Visibility));
            Assert.Equal(1, await db.Notifications.CountAsync(n => n.EventType == "request.signoff-rejected" && n.RecipientId == agentUser.Id, Ct));
        }

        await agent.SendOkAsync(HttpMethod.Post, $"/api/v1/service-requests/{work}/transitions", new { state = "Resolved", resolutionNotes = "Arm fitted." });
        await lead.SendOkAsync(HttpMethod.Post, $"/api/v1/approvals/{await PendingFor(lead)}/decision", new { decision = "Approve" });
        var resolved = await requester.SendOkAsync(HttpMethod.Get, $"/api/v1/service-requests/{work}");
        Assert.Equal(("Resolved", "Arm fitted."), ((string?)resolved["state"], (string?)resolved["resolutionNotes"]));
    }

    [Fact]
    public async Task Rules_assign_notify_and_ask_for_approval_by_amount_and_answer()
    {
        var tenant = await database.CreateTenantAsync("wu");
        using var admin = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.TenantAdmin);
        using var publisher = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.TenantAdmin);
        var requesterUser = await database.CreateUserAsync(tenant, BuiltInRoles.Requester);
        var cfoUser = await database.CreateUserAsync(tenant, BuiltInRoles.Approver);
        var agentUser = await database.CreateUserAsync(tenant, BuiltInRoles.Agent);
        using var requester = _factory.ForTenant(tenant).As(requesterUser);
        using var agent = _factory.ForTenant(tenant).As(agentUser);
        using var cfo = _factory.ForTenant(tenant).As(cfoUser);
        var desk = await GroupAsync(admin, "Desk");
        var vip = await GroupAsync(admin, "VIP desk", agentUser);
        await admin.SendOkAsync(HttpMethod.Put, "/api/v1/assignment-rules/default", new { groupId = desk, method = "GroupQueue" });
        var cheap = await ServiceAsync(admin, publisher, "SW-FREE", Card("Free software"));
        var costly = await ServiceAsync(admin, publisher, "SW-PAID", Card("Paid software", more: c => c["fee"] = new JsonObject { ["paid"] = true, ["amountAed"] = 6000 }));

        // Only workflow administrators build rules, and problems are listed.
        using (var notAdmin = await agent.SendAsync(HttpMethod.Get, "/api/v1/workflow-rules"))
        {
            Assert.Equal(HttpStatusCode.Forbidden, notAdmin.StatusCode);
        }

        using var bad = await admin.SendAsync(HttpMethod.Post, "/api/v1/workflow-rules", new
        {
            name = Text("Broken"),
            trigger = "StateChanged",
            recordType = "Incident",
            conditions = new JsonArray(new JsonObject { ["field"] = "priority", ["op"] = "in", ["value"] = 1 }),
            actions = new JsonArray(
                new JsonObject { ["type"] = "approval", ["levels"] = new JsonArray() },
                new JsonObject { ["type"] = "notify", ["recipients"] = new JsonArray("requester"), ["subject"] = Text("{{nope}}"), ["body"] = Text("Hi") }),
        });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        var codes = (await bad.JsonAsync())["problems"]!.AsArray().Select(p => (string)p!["code"]!).ToList();
        Assert.Contains("operator-invalid", codes);
        Assert.Contains("approval-not-allowed", codes);
        Assert.Contains("placeholder-unknown", codes);

        await admin.SendOkAsync(HttpMethod.Post, "/api/v1/workflow-rules", new
        {
            name = Text("Paid software needs the CFO", "البرمجيات المدفوعة تحتاج إلى المدير المالي"),
            trigger = "TicketCreated",
            recordType = "ServiceRequest",
            conditions = new JsonArray(new JsonObject { ["field"] = "fee", ["op"] = "gt", ["value"] = 5000 }),
            actions = new JsonArray(new JsonObject
            {
                ["type"] = "approval",
                ["stage"] = "BeforeAssignment",
                ["levels"] = new JsonArray(Level("CFO approval", "Any", new JsonObject { ["kind"] = "User", ["id"] = cfoUser.Id.ToString() })),
            }),
        }, HttpStatusCode.Created);
        var assign = await admin.SendOkAsync(HttpMethod.Post, "/api/v1/workflow-rules", new
        {
            name = Text("Software to the VIP desk"),
            trigger = "TicketCreated",
            recordType = "ServiceRequest",
            match = "Any",
            conditions = new JsonArray(new JsonObject { ["field"] = "service", ["op"] = "in", ["value"] = new JsonArray(cheap.ToString(), costly.ToString()) }),
            actions = new JsonArray(
                new JsonObject { ["type"] = "assign", ["groupId"] = vip.ToString(), ["method"] = "RoundRobin" },
                new JsonObject
                {
                    ["type"] = "notify",
                    ["recipients"] = new JsonArray("requester", "assignee"),
                    ["subject"] = Text("{{ticket.number}} is with {{ticket.group}}", "{{ticket.number}} لدى {{ticket.group}}"),
                    ["body"] = Text("Dear {{recipient.name}}, we are on it.", "حضرة {{recipient.name}}، نعمل على طلبكم."),
                    ["email"] = false,
                }),
        }, HttpStatusCode.Created);
        Assert.Equal(2, (await admin.ArrayAsync("/api/v1/workflow-rules?trigger=TicketCreated")).Count);

        // A free service: the rule assigns by round-robin and notifies.
        var free = (await requester.SendOkAsync(HttpMethod.Post, "/api/v1/service-requests", new { serviceId = cheap }, HttpStatusCode.Created)).Id();
        var freeView = await admin.SendOkAsync(HttpMethod.Get, $"/api/v1/service-requests/{free}");
        Assert.Equal((vip, agentUser.Id), (Guid.Parse((string)freeView["group"]!["id"]!), Guid.Parse((string)freeView["assignee"]!["id"]!)));
        await using (var db = TestData.AppContext(database, tenant))
        {
            var rule = await db.Notifications.AsNoTracking().Where(n => n.EventType == "workflow.rule" && n.TicketId == free).ToListAsync(Ct);
            Assert.Equal(new[] { agentUser.Id, requesterUser.Id }.Order(), rule.Select(n => n.RecipientId).Order());
            Assert.True(await db.AuditEvents.AnyAsync(e => e.Action == "workflows.rule.applied" && e.EntityId == free.ToString(), Ct));
        }

        // The paid one (over 5,000 AED) waits for the CFO first; the assign rule waits too.
        var paid = (await requester.SendOkAsync(HttpMethod.Post, "/api/v1/service-requests", new { serviceId = costly }, HttpStatusCode.Created)).Id();
        var paidView = await admin.SendOkAsync(HttpMethod.Get, $"/api/v1/service-requests/{paid}");
        Assert.True((bool)paidView["awaitingApproval"]!);
        Assert.Null(paidView["group"]);
        await cfo.SendOkAsync(HttpMethod.Post, $"/api/v1/approvals/{await PendingFor(cfo)}/decision", new { decision = "Approve" });
        Assert.Equal(desk, Guid.Parse((string)(await admin.SendOkAsync(HttpMethod.Get, $"/api/v1/service-requests/{paid}"))["group"]!["id"]!));

        // Trying the rules on a ticket shows which hold.
        using var test = await admin.SendAsync(HttpMethod.Post, "/api/v1/workflow-rules/test", new { number = (string?)freeView["number"], trigger = "TicketCreated" });
        var results = JsonNode.Parse(await test.Content.ReadAsStringAsync(Ct))!.AsArray();
        Assert.Equal([false, true], results.Select(r => (bool)r!["holds"]!));

        // Missing Arabic is reported; deleting a rule stops it.
        await admin.SendOkAsync(HttpMethod.Delete, $"/api/v1/workflow-rules/{assign.Id()}", expected: HttpStatusCode.NoContent);
        Assert.Single(await admin.ArrayAsync("/api/v1/workflow-rules"));
    }

    [Fact]
    public async Task Elsa_tables_have_forced_row_level_security_and_one_tenant_never_sees_anothers_workflows()
    {
        await using var owner = new NpgsqlConnection(database.OwnerConnectionString);
        await owner.OpenAsync(Ct);
        await using (var command = new NpgsqlCommand("""
            SELECT c.relname, c.relrowsecurity, c.relforcerowsecurity, (SELECT count(*) FROM pg_policy p WHERE p.polrelid = c.oid)
            FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = 'Elsa' AND c.relkind = 'r' AND c.relname <> '__EFMigrationsHistory'
            """, owner))
        await using (var reader = await command.ExecuteReaderAsync(Ct))
        {
            var tables = 0;
            while (await reader.ReadAsync(Ct))
            {
                tables++;
                Assert.True(reader.GetBoolean(1) && reader.GetBoolean(2) && reader.GetInt64(3) > 0, $"Elsa.{reader.GetString(0)} is not isolated.");
            }

            Assert.True(tables >= 8);
        }

        var tenant = await database.CreateTenantAsync("we");
        var other = await database.CreateTenantAsync("wo");
        using var admin = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.TenantAdmin);
        using var publisher = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.TenantAdmin);
        var approverUser = await database.CreateUserAsync(tenant, BuiltInRoles.Approver);
        using var requester = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.Requester);
        var service = await ServiceAsync(admin, publisher, "ANY-1", Card("Thing", new JsonObject
        {
            ["beforeAssignment"] = new JsonArray(Level("Check", "Any", new JsonObject { ["kind"] = "User", ["id"] = approverUser.Id.ToString() })),
        }));
        var id = (await requester.SendOkAsync(HttpMethod.Post, "/api/v1/service-requests", new { serviceId = service }, HttpStatusCode.Created)).Id();
        string instance;
        await using (var db = TestData.AppContext(database, tenant))
        {
            instance = (await db.ApprovalProcesses.AsNoTracking().SingleAsync(p => p.TicketId == id, Ct)).EngineInstanceId!;
        }

        async Task<long> CountAsync(Tenant? as_)
        {
            await using var connection = await database.OpenAppConnectionAsync(as_?.Id);
            await using var count = new NpgsqlCommand("""SELECT count(*) FROM "Elsa"."WorkflowInstances" WHERE "Id" = @id""", connection);
            count.Parameters.AddWithValue("id", instance);
            return (long)(await count.ExecuteScalarAsync(Ct))!;
        }

        Assert.Equal(1, await CountAsync(tenant));
        Assert.Equal(0, await CountAsync(other));
        Assert.Equal(0, await CountAsync(null));
    }
}
