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
        Assert.Equal(["users.read"], me.Permissions);
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
}
