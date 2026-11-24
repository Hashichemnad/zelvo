using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Zelvo.Data.Identity;
using Zelvo.Data.Tenancy;

namespace Zelvo.Api.Tests;

/// <summary>Requests to the directory API (task B2).</summary>
internal static class DirectoryApi
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static async Task<JsonObject> SendOkAsync(this HttpClient client, HttpMethod method, string uri, object? body = null, HttpStatusCode expected = HttpStatusCode.OK)
    {
        using var request = new HttpRequestMessage(method, uri) { Content = body is null ? null : JsonContent.Create(body) };
        var response = await client.SendAsync(request, Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.StatusCode == expected, $"{method} {uri}: {(int)response.StatusCode} {text}");
        return text.Length == 0 ? [] : JsonNode.Parse(text) is JsonObject o ? o : new JsonObject { ["items"] = JsonNode.Parse(text) };
    }

    public static async Task<JsonArray> ArrayAsync(this HttpClient client, string uri) =>
        (await client.GetFromJsonAsync<JsonArray>(uri, Ct))!;

    public static async Task<HttpResponseMessage> SendAsync(this HttpClient client, HttpMethod method, string uri, object? body = null)
    {
        using var request = new HttpRequestMessage(method, uri) { Content = body is null ? null : JsonContent.Create(body) };
        return await client.SendAsync(request, Ct);
    }

    public static Guid Id(this JsonObject json) => Guid.Parse((string)json["id"]!);

    public static async Task<Guid> CategoryAsync(this HttpClient admin, string en, string? ar = null, Guid? parentId = null) =>
        (await admin.SendOkAsync(HttpMethod.Post, "/api/v1/categories", new { name = new { en, ar }, parentId }, HttpStatusCode.Created)).Id();

    public static async Task<Guid> LocationAsync(this HttpClient admin, string en, Guid? parentId = null) =>
        (await admin.SendOkAsync(HttpMethod.Post, "/api/v1/locations", new { name = new { en }, parentId }, HttpStatusCode.Created)).Id();

    public static async Task<Guid> GroupAsync(this HttpClient admin, string en, params TestUser[] members)
    {
        var id = (await admin.SendOkAsync(HttpMethod.Post, "/api/v1/groups", new { name = new { en, ar = en + " (ar)" } }, HttpStatusCode.Created)).Id();
        foreach (var member in members)
        {
            await admin.SendOkAsync(HttpMethod.Put, $"/api/v1/groups/{id}/members/{member.Id}", new { isLead = false });
        }

        return id;
    }

    public static async Task<Guid> RuleAsync(this HttpClient admin, string en, Guid groupId, string method = "GroupQueue",
        Guid? categoryId = null, Guid? locationId = null, string? language = null, Guid? skillId = null) =>
        (await admin.SendOkAsync(HttpMethod.Post, "/api/v1/assignment-rules",
            new { name = new { en }, groupId, method, categoryId, locationId, language, skillId }, HttpStatusCode.Created)).Id();
}

/// <summary>Categories, locations, skills, groups, users and custom roles (task B2).</summary>
public sealed class DirectoryApiTests(DatabaseFixture database) : IAsyncDisposable
{
    private readonly ApiFactory _factory = ApiFactory.ForDatabase(database);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    [Fact]
    public async Task Categories_form_trees_of_three_levels_that_requesters_see_only_while_active()
    {
        var tenant = await database.CreateTenantAsync("dir");
        using var admin = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.TenantAdmin);
        using var agent = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.Agent);
        using var requester = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.Requester);

        var hardware = await admin.CategoryAsync("Hardware", "الأجهزة");
        var laptop = await admin.CategoryAsync("Laptop", "حاسوب محمول", hardware);
        var battery = await admin.CategoryAsync("Battery", "البطارية", laptop);
        var tooDeep = await admin.SendAsync(HttpMethod.Post, "/api/v1/categories", new { name = new { en = "Cell" }, parentId = battery });
        Assert.Equal(HttpStatusCode.Conflict, tooDeep.StatusCode);
        Assert.Equal("too-deep", (await tooDeep.ProblemAsync()).Code);

        var duplicate = await admin.SendAsync(HttpMethod.Post, "/api/v1/categories", new { name = new { en = "laptop" }, parentId = hardware });
        Assert.Equal("name-taken", (await duplicate.ProblemAsync()).Code);
        Assert.Equal(HttpStatusCode.Forbidden, (await agent.SendAsync(HttpMethod.Post, "/api/v1/categories", new { name = new { en = "Other" } })).StatusCode);

        var list = await requester.ArrayAsync("/api/v1/categories");
        Assert.Equal(["Hardware", "Laptop", "Battery"], list.Select(n => (string)n!["name"]!["en"]!));
        Assert.Equal("Hardware › Laptop › Battery", (string)list[2]!["path"]!["en"]!);
        Assert.Equal("الأجهزة › حاسوب محمول › البطارية", (string)list[2]!["path"]!["ar"]!);
        Assert.Equal(3, (int)list[2]!["depth"]!);

        await admin.SendOkAsync(HttpMethod.Patch, $"/api/v1/categories/{laptop}", new { active = false });
        Assert.Equal(["Hardware"], (await requester.ArrayAsync("/api/v1/categories")).Select(n => (string)n!["name"]!["en"]!));
        Assert.Equal(HttpStatusCode.Forbidden, (await requester.SendAsync(HttpMethod.Get, "/api/v1/categories?includeInactive=true")).StatusCode);
        var all = await agent.ArrayAsync("/api/v1/categories?includeInactive=true");
        Assert.Equal(3, all.Count);
        Assert.False((bool)all[2]!["selectable"]!);

        // A node with children is in use; a leaf can go.
        Assert.Equal("in-use", (await (await admin.SendAsync(HttpMethod.Delete, $"/api/v1/categories/{hardware}")).ProblemAsync()).Code);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.SendAsync(HttpMethod.Delete, $"/api/v1/categories/{battery}")).StatusCode);

        // Moving among siblings renumbers them.
        var network = await admin.CategoryAsync("Network");
        await admin.SendOkAsync(HttpMethod.Patch, $"/api/v1/categories/{network}", new { position = 0 });
        Assert.Equal(["Network", "Hardware"], (await requester.ArrayAsync("/api/v1/categories")).Select(n => (string)n!["name"]!["en"]!));

        // Changes are audited.
        await using var db = TestData.AppContext(database, tenant);
        var changes = await db.AuditEvents.Where(e => e.EntityType == "category" && e.EntityId == laptop.ToString()).Select(e => e.Changes).ToListAsync(Ct);
        Assert.Contains(changes, c => c != null && c.Contains("\"active\"", StringComparison.Ordinal));

        // Missing Arabic names show in the missing-translation report (LANG-5).
        var report = await admin.SendOkAsync(HttpMethod.Get, "/api/v1/translations/missing");
        Assert.Contains(network.ToString(), report.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Locations_belong_to_their_tenant_only()
    {
        var tenant = await database.CreateTenantAsync("loca");
        var other = await database.CreateTenantAsync("locb");
        using var admin = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.TenantAdmin);
        using var otherAdmin = await _factory.ForTenant(other).SignedInAsync(database, other, BuiltInRoles.TenantAdmin);
        var ajman = await admin.LocationAsync("Ajman");

        Assert.Empty(await otherAdmin.ArrayAsync("/api/v1/locations"));
        Assert.Equal(HttpStatusCode.NotFound, (await otherAdmin.SendAsync(HttpMethod.Patch, $"/api/v1/locations/{ajman}", new { active = false })).StatusCode);
        var child = await otherAdmin.SendAsync(HttpMethod.Post, "/api/v1/locations", new { name = new { en = "Floor 1" }, parentId = ajman });
        Assert.Equal(HttpStatusCode.BadRequest, child.StatusCode);
    }

    [Fact]
    public async Task Groups_hold_staff_and_their_leads_manage_members_who_are_not_leads()
    {
        var tenant = await database.CreateTenantAsync("grp");
        using var admin = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.TenantAdmin);
        var lead = await database.CreateUserAsync(tenant, BuiltInRoles.TeamLead);
        var agent = await database.CreateUserAsync(tenant, BuiltInRoles.Agent);
        var other = await database.CreateUserAsync(tenant, BuiltInRoles.Agent);
        var requester = await database.CreateUserAsync(tenant, BuiltInRoles.Requester);
        using var asLead = _factory.ForTenant(tenant).As(lead);
        using var asAgent = _factory.ForTenant(tenant).As(agent);
        using var asLeadWithoutMfa = _factory.ForTenant(tenant).As(lead, mfa: false);

        var group = await admin.GroupAsync("Network team");
        var notStaff = await admin.SendAsync(HttpMethod.Put, $"/api/v1/groups/{group}/members/{requester.Id}", new { });
        Assert.Equal(HttpStatusCode.BadRequest, notStaff.StatusCode);
        await admin.SendOkAsync(HttpMethod.Put, $"/api/v1/groups/{group}/members/{lead.Id}", new { isLead = true });

        // The lead adds and removes ordinary members; only administrators touch leads.
        await asLead.SendOkAsync(HttpMethod.Put, $"/api/v1/groups/{group}/members/{agent.Id}", new { });
        Assert.Equal(HttpStatusCode.Forbidden, (await asLead.SendAsync(HttpMethod.Put, $"/api/v1/groups/{group}/members/{other.Id}", new { isLead = true })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await asLead.SendAsync(HttpMethod.Delete, $"/api/v1/groups/{group}/members/{lead.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await asAgent.SendAsync(HttpMethod.Put, $"/api/v1/groups/{group}/members/{other.Id}", new { })).StatusCode);
        // Lead rights need the second factor, like the lead's other staff rights.
        Assert.NotEqual(HttpStatusCode.OK, (await asLeadWithoutMfa.SendAsync(HttpMethod.Put, $"/api/v1/groups/{group}/members/{other.Id}", new { })).StatusCode);

        // The lead changes the availability of the group's members.
        var user = await asLead.SendOkAsync(HttpMethod.Put, $"/api/v1/users/{agent.Id}/availability", new { available = false });
        Assert.False((bool)user["availableForAssignment"]!);
        Assert.Equal(HttpStatusCode.Forbidden, (await asLead.SendAsync(HttpMethod.Put, $"/api/v1/users/{other.Id}/availability", new { available = false })).StatusCode);

        var detail = await asAgent.SendOkAsync(HttpMethod.Get, $"/api/v1/groups/{group}");
        Assert.Equal(2, (int)detail["memberCount"]!);
        Assert.Equal(lead.Id, Guid.Parse((string)detail["members"]![0]!["userId"]!));
        Assert.True((bool)detail["members"]![0]!["isLead"]!);
        using var asRequester = _factory.ForTenant(tenant).As(requester);
        Assert.Equal(HttpStatusCode.Forbidden, (await asRequester.SendAsync(HttpMethod.Get, $"/api/v1/groups/{group}")).StatusCode);

        await asLead.SendOkAsync(HttpMethod.Delete, $"/api/v1/groups/{group}/members/{agent.Id}");
        Assert.Equal(1, (int)(await admin.SendOkAsync(HttpMethod.Get, $"/api/v1/groups/{group}"))["memberCount"]!);
    }

    [Fact]
    public async Task Groups_the_default_or_a_rule_uses_cannot_be_deactivated_or_deleted()
    {
        var tenant = await database.CreateTenantAsync("grpuse");
        using var admin = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.TenantAdmin);
        var desk = await admin.GroupAsync("Service desk");
        var network = await admin.GroupAsync("Network");
        var spare = await admin.GroupAsync("Spare");
        await admin.SendOkAsync(HttpMethod.Put, "/api/v1/assignment-rules/default", new { groupId = desk, method = "GroupQueue" });
        await admin.RuleAsync("Network", network);

        Assert.Equal("in-use", (await (await admin.SendAsync(HttpMethod.Patch, $"/api/v1/groups/{desk}", new { active = false })).ProblemAsync()).Code);
        Assert.Equal("in-use", (await (await admin.SendAsync(HttpMethod.Delete, $"/api/v1/groups/{network}")).ProblemAsync()).Code);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.SendAsync(HttpMethod.Delete, $"/api/v1/groups/{spare}")).StatusCode);
        Assert.Equal("name-taken", (await (await admin.SendAsync(HttpMethod.Post, "/api/v1/groups", new { name = new { en = "service DESK" } })).ProblemAsync()).Code);
    }

    [Fact]
    public async Task Administrators_add_users_and_give_them_skills_languages_and_availability()
    {
        var tenant = await database.CreateTenantAsync("usr");
        using var admin = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.TenantAdmin);
        var agent = await database.CreateUserAsync(tenant, BuiltInRoles.Agent);
        using var asAgent = _factory.ForTenant(tenant).As(agent);

        var created = await admin.SendOkAsync(HttpMethod.Post, "/api/v1/users",
            new { email = "Mariam.Salem@Example.test", displayName = "Mariam Salem", roles = new[] { BuiltInRoles.Agent } }, HttpStatusCode.Created);
        Assert.Equal("mariam.salem@example.test", (string)created["email"]!);
        Assert.Equal([BuiltInRoles.Agent], created["roles"]!.AsArray().Select(r => (string)r!));
        Assert.Equal("user-exists", (await (await admin.SendAsync(HttpMethod.Post, "/api/v1/users", new { email = "mariam.salem@example.test" })).ProblemAsync()).Code);
        Assert.Equal(HttpStatusCode.Forbidden, (await asAgent.SendAsync(HttpMethod.Post, "/api/v1/users", new { email = "x@example.test" })).StatusCode);

        var skill = (await admin.SendOkAsync(HttpMethod.Post, "/api/v1/skills", new { name = new { en = "Networks", ar = "الشبكات" } }, HttpStatusCode.Created)).Id();
        var user = created.Id();
        var withSkill = await admin.SendOkAsync(HttpMethod.Put, $"/api/v1/users/{user}/skills", new { skillIds = new[] { skill } });
        Assert.Equal("Networks", (string)withSkill["skills"]![0]!["name"]!["en"]!);
        var withLanguages = await admin.SendOkAsync(HttpMethod.Patch, $"/api/v1/users/{user}", new { languages = new[] { "ar", "en" }, availableForAssignment = false });
        Assert.Equal(["en", "ar"], withLanguages["languages"]!.AsArray().Select(l => (string)l!));
        Assert.False((bool)withLanguages["availableForAssignment"]!);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.SendAsync(HttpMethod.Patch, $"/api/v1/users/{user}", new { languages = new[] { "fr" } })).StatusCode);
        Assert.Equal(1, (int)(await admin.ArrayAsync("/api/v1/skills"))[0]!["userCount"]!);

        // A skill a rule needs stays; otherwise it can go and people lose it.
        var group = await admin.GroupAsync("Desk");
        var rule = await admin.RuleAsync("Network", group, "RoundRobin", skillId: skill);
        Assert.Equal("in-use", (await (await admin.SendAsync(HttpMethod.Delete, $"/api/v1/skills/{skill}")).ProblemAsync()).Code);
        await admin.SendAsync(HttpMethod.Delete, $"/api/v1/assignment-rules/{rule}");
        Assert.Equal(HttpStatusCode.NoContent, (await admin.SendAsync(HttpMethod.Delete, $"/api/v1/skills/{skill}")).StatusCode);
        Assert.Empty((await admin.SendOkAsync(HttpMethod.Get, $"/api/v1/users/{user}"))["skills"]!.AsArray());

        // Search by name for everyone with users.read; by email only for callers who may see emails.
        Assert.Equal(1, (int)(await admin.SendOkAsync(HttpMethod.Get, "/api/v1/users?q=salem"))["totalCount"]!);
        Assert.Equal(1, (int)(await admin.SendOkAsync(HttpMethod.Get, "/api/v1/users?q=mariam.salem@"))["totalCount"]!);
        Assert.Equal(1, (int)(await admin.SendOkAsync(HttpMethod.Get, $"/api/v1/users?role={BuiltInRoles.TenantAdmin}"))["totalCount"]!);
        Assert.Equal(0, (int)(await admin.SendOkAsync(HttpMethod.Get, "/api/v1/users?q=%25"))["totalCount"]!);

        // Staff switch their own availability.
        var me = await asAgent.SendOkAsync(HttpMethod.Put, "/api/v1/me/availability", new { available = false });
        Assert.False((bool)me["availableForAssignment"]!);
    }

    [Fact]
    public async Task Custom_roles_can_be_added_and_deleted_while_unused_and_built_in_roles_stay()
    {
        var tenant = await database.CreateTenantAsync("role");
        using var admin = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.TenantAdmin);

        var role = await admin.SendOkAsync(HttpMethod.Post, "/api/v1/roles",
            new { key = "finance-desk", name = new { en = "Finance desk", ar = "مكتب المالية" } }, HttpStatusCode.Created);
        Assert.False((bool)role["isSystem"]!);
        Assert.Empty(role["permissions"]!.AsArray());
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.SendAsync(HttpMethod.Post, "/api/v1/roles", new { key = "agent", name = new { en = "Agent 2" } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.SendAsync(HttpMethod.Post, "/api/v1/roles", new { key = "Bad Key", name = new { en = "Bad" } })).StatusCode);
        Assert.Equal("role-exists", (await (await admin.SendAsync(HttpMethod.Post, "/api/v1/roles", new { key = "finance-desk", name = new { en = "Again" } })).ProblemAsync()).Code);

        var user = await database.CreateUserAsync(tenant, "finance-desk");
        Assert.Equal("in-use", (await (await admin.SendAsync(HttpMethod.Delete, "/api/v1/roles/finance-desk")).ProblemAsync()).Code);
        await admin.SendOkAsync(HttpMethod.Put, $"/api/v1/users/{user.Id}/roles", new { roles = new[] { BuiltInRoles.Requester } });
        Assert.Equal(HttpStatusCode.NoContent, (await admin.SendAsync(HttpMethod.Delete, "/api/v1/roles/finance-desk")).StatusCode);
        Assert.Equal("role-system", (await (await admin.SendAsync(HttpMethod.Delete, $"/api/v1/roles/{BuiltInRoles.Approver}")).ProblemAsync()).Code);
    }

    [Fact]
    public async Task The_built_in_roles_get_the_directory_permissions_once()
    {
        var tenant = await database.CreateTenantAsync("dirperm");
        await database.EnsureIdentityAsync(tenant);
        await using var db = TestData.AppContext(database, tenant);
        var roles = await db.Roles.Include(r => r.Permissions).ToDictionaryAsync(r => r.Key, r => r.Permissions.Select(p => p.Permission).ToList(), Ct);
        Assert.Contains("directory.manage", roles[BuiltInRoles.TenantAdmin]);
        Assert.Contains("directory.read", roles[BuiltInRoles.Agent]);
        Assert.Contains("directory.read", roles[BuiltInRoles.TeamLead]);
        Assert.Contains("directory.read", roles[BuiltInRoles.ExecutiveAuditor]);
        Assert.DoesNotContain("directory.read", roles[BuiltInRoles.Requester]);
        Assert.DoesNotContain("directory.manage", roles[BuiltInRoles.Agent]);
    }
}
