using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Zelvo.Api.Identity;
using Zelvo.Data.Auditing;
using Zelvo.Data.Identity;

namespace Zelvo.Api.Tests;

/// <summary>Users, roles, field-level access and sign-in settings through the API.</summary>
public sealed class AccessControlApiTests(DatabaseFixture database) : IAsyncDisposable
{
    private readonly ApiFactory _factory = ApiFactory.ForDatabase(database);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    [Fact]
    public async Task Every_tenant_has_the_six_persona_roles_with_least_privilege_defaults()
    {
        var tenant = await database.CreateTenantAsync();
        using var admin = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.TenantAdmin);

        var roles = await admin.GetFromJsonAsync<List<RoleBody>>(U("/roles"), Ct) ?? [];

        Assert.Equal(
            [BuiltInRoles.Agent, BuiltInRoles.Approver, BuiltInRoles.ExecutiveAuditor, BuiltInRoles.Requester, BuiltInRoles.TeamLead, BuiltInRoles.TenantAdmin],
            roles.Select(r => r.Key));
        Assert.All(roles, r => Assert.True(r.IsSystem));
        Assert.Empty(Role(roles, BuiltInRoles.Requester).Permissions);
        Assert.Empty(Role(roles, BuiltInRoles.Approver).Permissions);
        Assert.Equal(PermissionCatalog.All.Order(StringComparer.Ordinal), Role(roles, BuiltInRoles.TenantAdmin).Permissions);
        Assert.True(Role(roles, BuiltInRoles.Agent).RequiresMfa);
        Assert.True(Role(roles, BuiltInRoles.TeamLead).RequiresMfa);
        Assert.True(Role(roles, BuiltInRoles.TenantAdmin).RequiresMfa);
        Assert.False(Role(roles, BuiltInRoles.Requester).RequiresMfa);
        Assert.False(Role(roles, BuiltInRoles.Approver).RequiresMfa);
    }

    [Fact]
    public async Task Provisioning_again_does_not_undo_an_administrators_change()
    {
        var tenant = await database.CreateTenantAsync();
        using var admin = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.TenantAdmin);
        var response = await admin.PutAsJsonAsync(U("/roles/agent/permissions"), new { permissions = Array.Empty<string>() }, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await IdentityProvisioning.EnsureTenantIdentityAsync(database.OwnerConnectionString, tenant, Ct);

        var agent = await admin.GetFromJsonAsync<RoleBody>(U("/roles/agent"), Ct);
        Assert.Empty(agent!.Permissions);
        await using var db = TestData.AppContext(database, tenant);
        Assert.Equal(6, await db.Roles.CountAsync(Ct));
    }

    [Fact]
    public async Task The_email_field_follows_field_rules_and_viewing_it_is_audited()
    {
        var tenant = await database.CreateTenantAsync();
        var agent = await database.CreateUserAsync(tenant, BuiltInRoles.Agent);
        using var admin = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.TenantAdmin);
        using var agentClient = _factory.ForTenant(tenant).As(agent);

        var withEmail = await agentClient.GetFromJsonAsync<JsonObject>(U($"/users/{agent.Id}"), Ct);
        Assert.Equal(agent.Email, (string?)withEmail!["email"]);

        var hide = await admin.PutAsJsonAsync(U("/roles/agent/field-rules"),
            new { rules = new[] { new { resource = "users", field = "email", access = "Hidden" } } }, Ct);
        Assert.Equal(HttpStatusCode.OK, hide.StatusCode);

        var withoutEmail = await agentClient.GetFromJsonAsync<JsonObject>(U($"/users/{agent.Id}"), Ct);
        Assert.False(withoutEmail!.ContainsKey("email"));
        Assert.True(withoutEmail.ContainsKey("displayName"));

        await using var db = TestData.AppContext(database, tenant);
        var views = await db.AuditEvents.Where(e => e.Action == "users.personal-data.viewed").ToListAsync(Ct);
        var view = Assert.Single(views);
        Assert.Equal(AuditCategory.View, view.Category);
        Assert.Equal(agent.Id.ToString(), view.ActorId);
    }

    [Fact]
    public async Task Sensitive_fields_stay_hidden_from_a_role_that_only_has_the_read_permission()
    {
        var tenant = await database.CreateTenantAsync();
        using var admin = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.TenantAdmin);
        await admin.PutAsJsonAsync(U("/roles/approver/permissions"), new { permissions = new[] { "users.read" } }, Ct);
        using var approver = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.Approver);

        var page = await approver.GetFromJsonAsync<JsonObject>(U("/users"), Ct);

        var items = page!["items"]!.AsArray();
        Assert.NotEmpty(items);
        Assert.All(items, item =>
        {
            Assert.False(item!.AsObject().ContainsKey("email"));
            Assert.True(item.AsObject().ContainsKey("displayName"));
        });
    }

    [Fact]
    public async Task Fields_a_role_may_only_read_cannot_be_changed()
    {
        var tenant = await database.CreateTenantAsync();
        var target = await database.CreateUserAsync(tenant, BuiltInRoles.Requester);
        using var admin = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.TenantAdmin);
        await admin.PutAsJsonAsync(U("/roles/team-lead/permissions"), new { permissions = new[] { "users.read", "users.manage" } }, Ct);
        await admin.PutAsJsonAsync(U("/roles/team-lead/field-rules"),
            new { rules = new[] { new { resource = "users", field = "displayName", access = "Read" } } }, Ct);
        using var lead = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.TeamLead);

        var refused = await lead.PatchAsJsonAsync(U($"/users/{target.Id}"), new { displayName = "Renamed" }, Ct);
        var problem = await refused.Content.ReadFromJsonAsync<JsonObject>(Ct);
        var allowed = await lead.PatchAsJsonAsync(U($"/users/{target.Id}"), new { status = "Disabled" }, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal("fields-read-only", (string?)problem!["code"]);
        Assert.Equal("displayName", (string?)problem["fields"]![0]);
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
    }

    [Fact]
    public async Task An_admin_changes_a_users_roles_and_it_is_audited()
    {
        var tenant = await database.CreateTenantAsync();
        var target = await database.CreateUserAsync(tenant, BuiltInRoles.Requester);
        using var admin = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.TenantAdmin);

        var response = await admin.PutAsJsonAsync(U($"/users/{target.Id}/roles"), new { roles = new[] { "agent", "approver" } }, Ct);
        var body = await response.Content.ReadFromJsonAsync<JsonObject>(Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(["agent", "approver"], body!["roles"]!.AsArray().Select(r => (string?)r));
        await using var db = TestData.AppContext(database, tenant);
        Assert.True(await db.AuditEvents.AnyAsync(e => e.EntityType == "user_role" && e.Category == AuditCategory.Delete, Ct));
        Assert.Equal(2, await db.AuditEvents.CountAsync(e => e.EntityType == "user_role" && e.Category == AuditCategory.Create && e.ActorType == AuditActorType.User, Ct));
    }

    [Fact]
    public async Task Unknown_roles_are_a_validation_problem()
    {
        var tenant = await database.CreateTenantAsync();
        var target = await database.CreateUserAsync(tenant, BuiltInRoles.Requester);
        using var admin = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.TenantAdmin);

        var response = await admin.PutAsJsonAsync(U($"/users/{target.Id}/roles"), new { roles = new[] { "superuser" } }, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task The_tenant_always_keeps_an_active_administrator()
    {
        var tenant = await database.CreateTenantAsync();
        var admin = await database.CreateUserAsync(tenant, BuiltInRoles.TenantAdmin);
        using var client = _factory.ForTenant(tenant).As(admin);

        var dropRole = await client.PutAsJsonAsync(U($"/users/{admin.Id}/roles"), new { roles = new[] { "agent" } }, Ct);
        var disableSelf = await client.PatchAsJsonAsync(U($"/users/{admin.Id}"), new { status = "Disabled" }, Ct);

        Assert.Equal(HttpStatusCode.Conflict, dropRole.StatusCode);
        Assert.Equal("last-tenant-admin", (await dropRole.Content.ReadFromJsonAsync<ProblemBody>(Ct))!.Code);
        Assert.Equal(HttpStatusCode.Conflict, disableSelf.StatusCode);
        Assert.Equal("cannot-disable-self", (await disableSelf.Content.ReadFromJsonAsync<ProblemBody>(Ct))!.Code);
    }

    [Fact]
    public async Task Tenant_administrators_keep_the_permissions_that_prevent_a_lock_out()
    {
        var tenant = await database.CreateTenantAsync();
        using var admin = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.TenantAdmin);

        var response = await admin.PutAsJsonAsync(U("/roles/tenant-admin/permissions"), new { permissions = new[] { "users.manage" } }, Ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("role-locked", (await response.Content.ReadFromJsonAsync<ProblemBody>(Ct))!.Code);
    }

    [Theory]
    [InlineData("agent")]
    [InlineData("team-lead")]
    [InlineData("tenant-admin")]
    public async Task Staff_roles_always_need_a_second_factor(string role)
    {
        var tenant = await database.CreateTenantAsync();
        using var admin = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.TenantAdmin);

        var response = await admin.PatchAsJsonAsync(U($"/roles/{role}"), new { requiresMfa = false }, Ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("role-mfa-locked", (await response.Content.ReadFromJsonAsync<ProblemBody>(Ct))!.Code);
    }

    [Fact]
    public async Task An_admin_can_require_a_second_factor_for_approvers()
    {
        var tenant = await database.CreateTenantAsync();
        var approver = await database.CreateUserAsync(tenant, BuiltInRoles.Approver);
        using var admin = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.TenantAdmin);

        var response = await admin.PatchAsJsonAsync(U("/roles/approver"), new { requiresMfa = true, name = new { en = "Budget approver", ar = "معتمد الميزانية" } }, Ct);
        using var approverClient = _factory.ForTenant(tenant).As(approver, mfa: false);
        var me = await approverClient.GetFromJsonAsync<IdentityApiTests.MeBody>(U("/me"), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(me!.Mfa.Required);
        Assert.Equal("Budget approver", me.Roles[0].Name.En);
    }

    [Theory]
    [InlineData("""{"permissions":["users.read","everything"]}""", "/roles/agent/permissions")]
    [InlineData("""{"rules":[{"resource":"users","field":"password","access":"Read"}]}""", "/roles/agent/field-rules")]
    [InlineData("""{"rules":[{"resource":"users","field":"email","access":"Edit"}]}""", "/roles/agent/field-rules")]
    [InlineData("""{"rules":[{"resource":"tickets","field":"title","access":"Read"}]}""", "/roles/agent/field-rules")]
    public async Task Grants_must_come_from_the_catalog(string json, string path)
    {
        var tenant = await database.CreateTenantAsync();
        using var admin = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.TenantAdmin);

        using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        var response = await admin.PutAsync(U(path), content, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("validation-failed", (await response.Content.ReadFromJsonAsync<ProblemBody>(Ct))!.Code);
    }

    [Fact]
    public async Task Only_role_managers_see_roles_and_the_permission_catalog()
    {
        var tenant = await database.CreateTenantAsync();
        using var executive = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.ExecutiveAuditor);
        using var admin = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.TenantAdmin);

        Assert.Equal(HttpStatusCode.Forbidden, (await executive.GetAsync(U("/roles"), Ct)).StatusCode);
        var catalog = await admin.GetFromJsonAsync<JsonObject>(U("/permissions"), Ct);
        var email = catalog!["resources"]![0]!["fields"]!.AsArray().Single(f => (string?)f!["key"] == "email");
        Assert.True((bool)email!["sensitive"]!);
    }

    [Fact]
    public async Task Sign_in_settings_default_to_known_users_only()
    {
        var tenant = await database.CreateTenantAsync();
        using var admin = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.TenantAdmin);

        var settings = await admin.GetFromJsonAsync<SettingsBody>(U("/identity/settings"), Ct);

        Assert.Equal("KnownUsers", settings!.EmailCodeSignIn);
        Assert.Empty(settings.AllowedDomains);
    }

    [Fact]
    public async Task Allowed_domains_are_saved_and_applied_to_the_tenant_realm()
    {
        var tenant = await database.CreateTenantAsync();
        using var admin = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.TenantAdmin);

        var response = await admin.PutAsJsonAsync(U("/identity/settings"),
            new { emailCodeSignIn = "AllowedDomains", allowedDomains = new[] { " @Ajman.ae", "am.gov.ae", "ajman.ae" } }, Ct);
        var body = await response.Content.ReadFromJsonAsync<SettingsBody>(Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("AllowedDomains", body!.EmailCodeSignIn);
        Assert.Equal(["ajman.ae", "am.gov.ae"], body.AllowedDomains);
        var call = Assert.Single(_factory.Keycloak.Calls);
        Assert.Equal($"zelvo-{tenant.Slug}", call.Realm);
        Assert.Equal(EmailCodeSignIn.AllowedDomains, call.Mode);
        Assert.Equal(["ajman.ae", "am.gov.ae"], call.Domains);
        await using var db = TestData.AppContext(database, tenant);
        Assert.True(await db.AuditEvents.AnyAsync(e => e.EntityType == "identity_settings" && e.Category == AuditCategory.Change, Ct));
    }

    [Theory]
    [InlineData("""{"emailCodeSignIn":"AllowedDomains","allowedDomains":[]}""")]
    [InlineData("""{"emailCodeSignIn":"AllowedDomains","allowedDomains":["not a domain"]}""")]
    [InlineData("""{"emailCodeSignIn":"Everyone"}""")]
    [InlineData("""{}""")]
    public async Task Invalid_sign_in_settings_are_refused(string json)
    {
        var tenant = await database.CreateTenantAsync();
        using var admin = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.TenantAdmin);

        using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        var response = await admin.PutAsync(U("/identity/settings"), content, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(_factory.Keycloak.Calls);
    }

    [Fact]
    public async Task When_keycloak_refuses_the_settings_nothing_changes()
    {
        var tenant = await database.CreateTenantAsync();
        using var admin = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.TenantAdmin);
        _factory.Keycloak.Fail = true;

        var response = await admin.PutAsJsonAsync(U("/identity/settings"), new { emailCodeSignIn = "Anyone" }, Ct);

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal("identity-provider-unavailable", (await response.Content.ReadFromJsonAsync<ProblemBody>(Ct))!.Code);
        await using var db = TestData.AppContext(database, tenant);
        Assert.Equal(EmailCodeSignIn.KnownUsers, (await db.IdentitySettings.SingleAsync(Ct)).EmailCodeSignIn);
    }

    [Fact]
    public async Task Only_identity_managers_change_sign_in_settings()
    {
        var tenant = await database.CreateTenantAsync();
        using var lead = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.TeamLead);

        var response = await lead.PutAsJsonAsync(U("/identity/settings"), new { emailCodeSignIn = "Anyone" }, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    private static Uri U(string path) => new($"/api/v1{path}", UriKind.Relative);

    private static RoleBody Role(List<RoleBody> roles, string key) => roles.Single(r => r.Key == key);

    private sealed record RoleBody(string Key, bool IsSystem, bool RequiresMfa, List<string> Permissions);

    private sealed record SettingsBody(string EmailCodeSignIn, List<string> AllowedDomains);
}
