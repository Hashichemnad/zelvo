using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Zelvo.Data.Auditing;
using Zelvo.Data.Identity;

namespace Zelvo.Api.Tests;

/// <summary>Sign-in with tenant realm tokens, first-sign-in provisioning and the second factor.</summary>
public sealed class IdentityApiTests(DatabaseFixture database) : IAsyncDisposable
{
    private static readonly Uri MeUri = new("/api/v1/me", UriKind.Relative);
    private static readonly Uri UsersUri = new("/api/v1/users", UriKind.Relative);
    private readonly ApiFactory _factory = ApiFactory.ForDatabase(database);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    [Fact]
    public async Task Me_needs_sign_in()
    {
        var tenant = await database.CreateTenantAsync();
        using var client = _factory.ForTenant(tenant);

        var response = await client.GetAsync(MeUri, Ct);
        var problem = await response.Content.ReadFromJsonAsync<ProblemBody>(Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("unauthenticated", problem!.Code);
        Assert.Equal("Bearer", response.Headers.WwwAuthenticate.ToString());
    }

    public static TheoryData<string> BadTokens => ["other-realm", "wrong-audience", "expired", "wrong-key", "wrong-tenant-claim", "garbage"];

    [Theory]
    [MemberData(nameof(BadTokens))]
    public async Task Tokens_not_issued_for_this_tenant_are_refused(string kind)
    {
        var tenant = await database.CreateTenantAsync();
        var other = await database.CreateTenantAsync("other");
        var subject = Guid.NewGuid().ToString();
        using var otherKey = RSA.Create(2048);
        var token = kind switch
        {
            "other-realm" => TestTokens.Issue(tenant, subject, issuer: TestTokens.IssuerFor(other)),
            "wrong-audience" => TestTokens.Issue(tenant, subject, audience: "account"),
            "expired" => TestTokens.Issue(tenant, subject, expires: DateTime.UtcNow.AddMinutes(-5)),
            "wrong-key" => TestTokens.Issue(tenant, subject, key: new RsaSecurityKey(otherKey) { KeyId = "test-key" }),
            "wrong-tenant-claim" => TestTokens.Issue(tenant, subject, tenantClaim: other.Id),
            _ => "not.a.token",
        };
        using var client = _factory.ForTenant(tenant);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.GetAsync(MeUri, Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        await using var db = TestData.AppContext(database, tenant);
        Assert.False(await db.Users.AnyAsync(u => u.Subject == subject, Ct));
    }

    [Fact]
    public async Task The_first_sign_in_creates_a_requester_and_it_is_audited()
    {
        var tenant = await database.CreateTenantAsync();
        await database.EnsureIdentityAsync(tenant);
        var subject = Guid.NewGuid().ToString();
        using var client = _factory.ForTenant(tenant);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", TestTokens.Issue(tenant, subject, mfa: false, email: "citizen@example.test", name: "Mariam Saeed"));

        var me = await client.GetFromJsonAsync<MeBody>(MeUri, Ct);
        var again = await client.GetFromJsonAsync<MeBody>(MeUri, Ct);

        Assert.Equal("citizen@example.test", me!.Email);
        Assert.Equal("Mariam Saeed", me.DisplayName);
        Assert.Equal([BuiltInRoles.Requester], me.Roles.Select(r => r.Key));
        Assert.Equal("مقدّم الطلب", me.Roles[0].Name.Ar);
        Assert.Empty(me.Permissions);
        Assert.False(me.Mfa.Required);
        Assert.Equal(me.Id, again!.Id);

        await using var db = TestData.AppContext(database, tenant);
        var created = await db.AuditEvents.SingleAsync(e => e.Category == AuditCategory.Create && e.EntityType == "user_account" && e.EntityId == me.Id.ToString(), Ct);
        Assert.Equal(me.Id.ToString(), created.ActorId);
        Assert.Contains("[masked]", created.Changes, StringComparison.Ordinal);
        Assert.DoesNotContain("citizen@example.test", created.Changes, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_changed_email_in_the_identity_provider_is_copied()
    {
        var tenant = await database.CreateTenantAsync();
        var user = await database.CreateUserAsync(tenant, BuiltInRoles.Requester);
        using var client = _factory.ForTenant(tenant);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", TestTokens.Issue(tenant, user.Subject, email: "new.address@example.test"));

        var me = await client.GetFromJsonAsync<MeBody>(MeUri, Ct);

        Assert.Equal("new.address@example.test", me!.Email);
    }

    [Fact]
    public async Task Disabled_users_are_refused_even_with_a_valid_token()
    {
        var tenant = await database.CreateTenantAsync();
        var user = await database.CreateUserAsync(tenant, BuiltInRoles.Agent);
        await database.ExecuteAsOwnerAsync("UPDATE users SET status = 'Disabled' WHERE id = @id", ("id", user.Id));
        using var client = _factory.ForTenant(tenant).As(user);

        var response = await client.GetAsync(MeUri, Ct);
        var problem = await response.Content.ReadFromJsonAsync<ProblemBody>(Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("user-disabled", problem!.Code);
    }

    [Fact]
    public async Task An_agent_without_a_second_factor_is_asked_to_step_up()
    {
        var tenant = await database.CreateTenantAsync();
        var agent = await database.CreateUserAsync(tenant, BuiltInRoles.Agent);
        using var client = _factory.ForTenant(tenant).As(agent, mfa: false);

        var me = await client.GetFromJsonAsync<MeBody>(MeUri, Ct);
        var response = await client.GetAsync(UsersUri, Ct);
        var problem = await response.Content.ReadFromJsonAsync<ProblemBody>(Ct);

        Assert.True(me!.Mfa.Required);
        Assert.False(me.Mfa.Satisfied);
        Assert.Empty(me.Permissions);
        Assert.Contains("users.read", me.PendingPermissions);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("mfa-required", problem!.Code);
        Assert.Contains("insufficient_user_authentication", response.Headers.WwwAuthenticate.ToString(), StringComparison.Ordinal);
        Assert.Contains("acr_values=\"mfa\"", response.Headers.WwwAuthenticate.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_agent_with_a_second_factor_gets_the_agent_permissions()
    {
        var tenant = await database.CreateTenantAsync();
        var agent = await database.CreateUserAsync(tenant, BuiltInRoles.Agent);
        using var client = _factory.ForTenant(tenant).As(agent);

        var me = await client.GetFromJsonAsync<MeBody>(MeUri, Ct);
        var response = await client.GetAsync(UsersUri, Ct);

        Assert.True(me!.Mfa.Satisfied);
        Assert.False(me.Mfa.Required);
        Assert.Equal(["assets.read", "changes.edit", "changes.read", "chat.handle", "cmdb.read", "directory.read", "incidents.assign", "incidents.create", "incidents.edit", "incidents.read", "knowledge.read", "knowledge.write", "problems.edit", "problems.read", "requests.assign", "requests.edit", "requests.read", "users.read", "voice.assign", "voice.edit", "voice.read"], me.Permissions.Order(StringComparer.Ordinal));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Approvers_and_requesters_do_not_need_a_second_factor()
    {
        var tenant = await database.CreateTenantAsync();
        var approver = await database.CreateUserAsync(tenant, BuiltInRoles.Approver, BuiltInRoles.Requester);
        using var client = _factory.ForTenant(tenant).As(approver, mfa: false);

        var me = await client.GetFromJsonAsync<MeBody>(MeUri, Ct);

        Assert.False(me!.Mfa.Required);
        Assert.Equal([BuiltInRoles.Approver, BuiltInRoles.Requester], me.Roles.Select(r => r.Key));
    }

    [Fact]
    public async Task A_requester_cannot_list_users()
    {
        var tenant = await database.CreateTenantAsync();
        using var client = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.Requester);

        var response = await client.GetAsync(UsersUri, Ct);
        var problem = await response.Content.ReadFromJsonAsync<ProblemBody>(Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("forbidden", problem!.Code);
    }

    internal sealed record MeBody(
        Guid Id, string? Email, string? DisplayName, List<RoleBody> Roles, List<string> Permissions, List<string> PendingPermissions, MfaBody Mfa);

    internal sealed record RoleBody(string Key, NameBody Name, bool RequiresMfa);

    internal sealed record NameBody(string? En, string? Ar);

    internal sealed record MfaBody(bool Satisfied, bool Required);

    [Fact]
    public async Task Disabling_a_person_turns_them_off_in_keycloak_and_ends_their_sessions()
    {
        var tenant = await database.CreateTenantAsync("disable");
        using var admin = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.TenantAdmin);
        var agent = await database.CreateUserAsync(tenant, BuiltInRoles.Agent);
        var realm = $"zelvo-{tenant.Slug}";

        using var disabled = await admin.PatchAsJsonAsync($"/api/v1/users/{agent.Id}", new { status = "Disabled" }, Ct);
        Assert.Equal(HttpStatusCode.OK, disabled.StatusCode);
        Assert.Contains(_factory.Keycloak.EnabledCalls, c => c == (realm, agent.Subject, false));

        // The API refuses the disabled person's token at once.
        using var refused = await _factory.ForTenant(tenant).As(agent).GetAsync(MeUri, Ct);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);

        // Another change that leaves the status alone does not touch Keycloak again.
        var before = _factory.Keycloak.EnabledCalls.Count;
        using var renamed = await admin.PatchAsJsonAsync($"/api/v1/users/{agent.Id}", new { displayName = "Renamed agent" }, Ct);
        Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);
        Assert.Equal(before, _factory.Keycloak.EnabledCalls.Count);

        using var enabled = await admin.PatchAsJsonAsync($"/api/v1/users/{agent.Id}", new { status = "Active" }, Ct);
        Assert.Equal(HttpStatusCode.OK, enabled.StatusCode);
        Assert.Contains(_factory.Keycloak.EnabledCalls, c => c == (realm, agent.Subject, true));

        // When Keycloak refuses, the status is not changed either.
        _factory.Keycloak.Fail = true;
        try
        {
            using var failed = await admin.PatchAsJsonAsync($"/api/v1/users/{agent.Id}", new { status = "Disabled" }, Ct);
            Assert.Equal(HttpStatusCode.BadGateway, failed.StatusCode);
        }
        finally
        {
            _factory.Keycloak.Fail = false;
        }

        await using var db = TestData.AppContext(database, tenant);
        Assert.Equal(UserStatus.Active, (await db.Users.SingleAsync(u => u.Id == agent.Id, Ct)).Status);
    }

    [Fact]
    public async Task Administrators_end_a_persons_sessions_or_everyones_and_it_is_audited()
    {
        var tenant = await database.CreateTenantAsync("signout");
        using var admin = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.TenantAdmin);
        var agent = await database.CreateUserAsync(tenant, BuiltInRoles.Agent);
        var realm = $"zelvo-{tenant.Slug}";

        using var one = await admin.PostAsync($"/api/v1/users/{agent.Id}/sign-out", null, Ct);
        Assert.Equal(HttpStatusCode.NoContent, one.StatusCode);
        Assert.Contains(_factory.Keycloak.SignOuts, c => c == (realm, agent.Subject));

        using var missing = await admin.PostAsync($"/api/v1/users/{Guid.NewGuid()}/sign-out", null, Ct);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

        using var everyone = await admin.PostAsync("/api/v1/identity/sign-out-everyone", null, Ct);
        Assert.Equal(HttpStatusCode.NoContent, everyone.StatusCode);
        Assert.Contains(_factory.Keycloak.SignOuts, c => c == (realm, (string?)null));

        // Agents may do neither.
        using var asAgent = _factory.ForTenant(tenant).As(agent);
        using var refused = await asAgent.PostAsync("/api/v1/identity/sign-out-everyone", null, Ct);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);

        await using var db = TestData.AppContext(database, tenant);
        Assert.True(await db.AuditEvents.AnyAsync(e => e.Category == AuditCategory.Security && e.Action == "users.signed-out" && e.EntityId == agent.Id.ToString(), Ct));
        Assert.True(await db.AuditEvents.AnyAsync(e => e.Category == AuditCategory.Security && e.Action == "identity.everyone-signed-out", Ct));
    }

    [Fact]
    public async Task Refused_calls_are_security_events_in_the_audit_log()
    {
        var tenant = await database.CreateTenantAsync("refused");
        var agent = await database.CreateUserAsync(tenant, BuiltInRoles.Agent);
        using var asAgent = _factory.ForTenant(tenant).As(agent);
        using var forbidden = await asAgent.GetAsync("/api/v1/roles", Ct);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        Assert.Equal("no-store", forbidden.Headers.CacheControl?.ToString());

        using var badToken = _factory.ForTenant(tenant);
        badToken.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "not.a.token");
        using var unauthenticated = await badToken.GetAsync(MeUri, Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);

        // Without any credential, a visitor's 401 is only logged and counted.
        using var visitor = _factory.ForTenant(tenant);
        using var anonymous = await visitor.GetAsync(MeUri, Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        await using var db = TestData.AppContext(database, tenant);
        var denied = await db.AuditEvents.SingleAsync(e => e.Action == "access.denied", Ct);
        Assert.Equal((AuditCategory.Security, AuditActorType.User, agent.Id.ToString()), (denied.Category, denied.ActorType, denied.ActorId));
        Assert.Contains("/roles", denied.Metadata, StringComparison.Ordinal);
        var unauthenticatedEntry = await db.AuditEvents.SingleAsync(e => e.Action == "access.unauthenticated", Ct);
        Assert.Equal(AuditActorType.Anonymous, unauthenticatedEntry.ActorType);
        Assert.DoesNotContain("not.a.token", unauthenticatedEntry.Metadata, StringComparison.Ordinal);
    }
}
