using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Zelvo.Data.Catalog;
using Zelvo.Data.Identity;
using Zelvo.Data.Tickets;
using Zelvo.Data.Workspace;

namespace Zelvo.Api.Tests;

/// <summary>Setup wizard and department templates (module 10, task F2) against PostgreSQL.</summary>
public sealed class SetupApiTests(DatabaseFixture database) : IAsyncDisposable
{
    private readonly ApiFactory _factory = ApiFactory.ForDatabase(database);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    [Fact]
    public async Task The_wizard_lists_its_steps_with_checks_and_remembers_progress()
    {
        var tenant = await database.CreateTenantAsync("setup");
        using var admin = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.TenantAdmin);
        using var lead = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.TeamLead);

        var setup = await admin.SendOkAsync(HttpMethod.Get, "/api/v1/setup");
        var steps = setup["steps"]!.AsArray();
        Assert.Equal(["license", "organisation", "language", "calendar", "branding", "signIn", "departments", "users", "channels", "ai"], steps.Select(s => (string)s!["key"]!));
        Assert.All(steps, s => Assert.Equal("Pending", (string?)s!["status"]));
        // The test tenant has an English and an Arabic name; no calendar or AI provider yet.
        Assert.True((bool)steps[0]!["configured"]!);
        Assert.True((bool)steps[1]!["configured"]!);
        Assert.False((bool)steps[3]!["configured"]!);
        Assert.Null(steps[5]!["configured"]);
        Assert.Null(setup["completedAt"]);

        var done = await admin.SendOkAsync(HttpMethod.Put, "/api/v1/setup/steps/branding", new { status = "Skipped" });
        Assert.Equal(("Skipped", 1), ((string?)done["steps"]![4]!["status"], (int)done["finished"]!));
        Assert.Equal(HttpStatusCode.NotFound, (await admin.SendAsync(HttpMethod.Put, "/api/v1/setup/steps/nothing", new { status = "Done" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.SendAsync(HttpMethod.Put, "/api/v1/setup/steps/ai", new { })).StatusCode);
        Assert.NotNull((await admin.SendOkAsync(HttpMethod.Post, "/api/v1/setup/finish"))["completedAt"]);
        Assert.Null((await admin.SendOkAsync(HttpMethod.Post, "/api/v1/setup/reopen"))["completedAt"]);
        Assert.Equal(HttpStatusCode.Forbidden, (await lead.SendAsync(HttpMethod.Get, "/api/v1/setup")).StatusCode);

        var audit = await database.AuditAsync(tenant);
        Assert.Contains(audit, e => e.Action == "setup.step.updated");
        Assert.Contains(audit, e => e.Action == "setup.finished");
    }

    [Fact]
    public async Task A_department_template_creates_a_service_desk_and_never_changes_what_exists()
    {
        var tenant = await database.CreateTenantAsync("tpl");
        using var admin = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.TenantAdmin);
        var templates = (await admin.GetFromJsonAsync<JsonArray>("/api/v1/setup/templates", Ct))!;
        Assert.Equal(["it-service-desk", "hr", "facilities", "customer-happiness"], templates.Select(t => (string)t!["key"]!));
        Assert.All(templates, t => Assert.False(string.IsNullOrEmpty((string?)t!["name"]!["ar"])));
        var it = templates[0]!;
        Assert.Equal("IT-LAPTOP", (string?)it["services"]![0]!["code"]);

        // An existing rule and policy keep their order after the department's new ones.
        var existingGroup = (await admin.SendOkAsync(HttpMethod.Post, "/api/v1/groups", new { name = new { en = "Existing", ar = "قائم" } }, HttpStatusCode.Created)).Id();
        await admin.SendOkAsync(HttpMethod.Post, "/api/v1/assignment-rules", new { name = new { en = "Everything", ar = "الكل" }, groupId = existingGroup, method = "GroupQueue" }, HttpStatusCode.Created);

        var applied = await admin.SendOkAsync(HttpMethod.Post, "/api/v1/setup/templates/it-service-desk/apply",
            new { departmentName = new { en = "Information technology", ar = "تقنية المعلومات" }, groupNames = new Dictionary<string, object> { ["service-desk"] = new { en = "Help desk", ar = "مكتب المساعدة" } } },
            HttpStatusCode.Created);
        Assert.Equal((3, 5, 2, 3, 3), (applied["groupIds"]!.AsArray().Count, (int)applied["categories"]!, (int)applied["policies"]!, applied["serviceIds"]!.AsArray().Count, (int)applied["responses"]!));

        await using (var db = TestData.AppContext(database, tenant))
        {
            var department = await db.Departments.SingleAsync(d => d.Id == Guid.Parse((string)applied["departmentId"]!), Ct);
            Assert.Equal("تقنية المعلومات", department.Name.Ar);
            Assert.Contains(await db.SupportGroups.Select(g => g.Name.En).ToListAsync(Ct), n => n == "Help desk");
            var root = await db.Categories.SingleAsync(c => c.Id == Guid.Parse((string)applied["rootCategoryId"]!), Ct);
            Assert.Equal(("Information technology", 1), (root.Name.En, root.Depth));
            Assert.Equal(10, await db.Categories.CountAsync(c => c.Depth > 1, Ct));
            var rules = await db.AssignmentRules.OrderBy(r => r.Position).ToListAsync(Ct);
            Assert.Equal(6, rules.Count);
            Assert.Equal("Everything", rules[^1].Name.En);
            Assert.Equal("Information technology: Hardware", rules[0].Name.En);
            var policies = await db.SlaPolicies.Include(p => p.Targets).OrderBy(p => p.Position).ToListAsync(Ct);
            Assert.Equal([TicketType.Incident, TicketType.ServiceRequest], policies.Select(p => p.RecordType!.Value));
            Assert.All(policies, p => Assert.Equal((root.Id, 5), (p.CategoryId!.Value, p.Targets.Count)));
            var services = await db.CatalogServices.OrderBy(s => s.Code).ToListAsync(Ct);
            Assert.Equal(["IT-ACCESS", "IT-LAPTOP", "IT-PASSWORD"], services.Select(s => s.Code));
            Assert.All(services, s => Assert.Equal(ServiceState.Draft, s.State));
            Assert.Equal(3, await db.CannedResponses.CountAsync(r => r.Scope == WorkspaceScope.Group, Ct));
        }

        // Applied again for another department: new groups, a second catalog key and new codes.
        var again = await admin.SendOkAsync(HttpMethod.Post, "/api/v1/setup/templates/it-service-desk/apply",
            new { departmentName = new { en = "Campus IT", ar = "تقنية المعلومات في الحرم" }, codePrefix = "IT", includeResponses = false }, HttpStatusCode.Created);
        Assert.Equal(0, (int)again["responses"]!);
        await using (var db = TestData.AppContext(database, tenant))
        {
            Assert.Equal(["it-service-desk", "it-service-desk-2"], await db.ServiceCatalogs.OrderBy(c => c.Key).Select(c => c.Key).ToListAsync(Ct));
            Assert.Contains("IT-LAPTOP-2", await db.CatalogServices.Select(s => s.Code).ToListAsync(Ct));
            Assert.Equal(2, await db.Departments.CountAsync(Ct));
        }

        var progress = await admin.SendOkAsync(HttpMethod.Get, "/api/v1/setup");
        Assert.Equal(2, progress["applied"]!.AsArray().Count);
        Assert.Equal("Done", (string?)progress["steps"]![6]!["status"]);
        Assert.True((bool)progress["steps"]![6]!["configured"]!);
        Assert.Contains(await database.AuditAsync(tenant), e => e.Action == "setup.template.applied");
    }

    [Fact]
    public async Task The_customer_happiness_template_sets_slas_for_complaints_inquiries_and_suggestions_and_checks_input()
    {
        var tenant = await database.CreateTenantAsync("chc");
        using var admin = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.TenantAdmin);
        using var agent = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.Agent);
        Assert.Equal(HttpStatusCode.Forbidden, (await agent.SendAsync(HttpMethod.Post, "/api/v1/setup/templates/hr/apply", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.SendAsync(HttpMethod.Post, "/api/v1/setup/templates/nothing/apply", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.SendAsync(HttpMethod.Post, "/api/v1/setup/templates/hr/apply", new { codePrefix = "a-b" })).StatusCode);

        await admin.SendOkAsync(HttpMethod.Post, "/api/v1/setup/templates/customer-happiness/apply", new { includeServices = false }, HttpStatusCode.Created);
        await using var db = TestData.AppContext(database, tenant);
        Assert.Equal([TicketType.Complaint, TicketType.Inquiry, TicketType.Suggestion],
            await db.SlaPolicies.OrderBy(p => p.Position).Select(p => p.RecordType!.Value).ToListAsync(Ct));
        Assert.False(await db.ServiceCatalogs.AnyAsync(Ct));
        Assert.Equal("مركز سعادة المتعاملين", (await db.Departments.SingleAsync(Ct)).Name.Ar);
    }
}
