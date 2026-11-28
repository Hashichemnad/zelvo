using System.Net;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Zelvo.Api.Identity;
using Zelvo.Data.Auditing;
using Zelvo.Data.Identity;

namespace Zelvo.Api.Tests;

/// <summary>Passkeys (module 19, task J3; product owner decision 15 of 2026-11-26) against PostgreSQL with a fake Keycloak.</summary>
public sealed class PasskeyApiTests(DatabaseFixture database) : IAsyncDisposable
{
    private readonly ApiFactory _factory = ApiFactory.ForDatabase(database);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    [Fact]
    public async Task People_list_and_remove_only_their_own_passkeys_and_staff_see_it_is_their_second_factor()
    {
        var tenant = await database.CreateTenantAsync("pk");
        var realm = $"zelvo-{tenant.Slug}";
        var agentUser = await database.CreateUserAsync(tenant, BuiltInRoles.Agent);
        var requesterUser = await database.CreateUserAsync(tenant, BuiltInRoles.Requester);
        using var agent = _factory.ForTenant(tenant).As(agentUser);
        using var requester = _factory.ForTenant(tenant).As(requesterUser);
        var phone = Guid.NewGuid().ToString();
        var laptop = Guid.NewGuid().ToString();
        var requesterKey = Guid.NewGuid().ToString();
        _factory.Keycloak.Passkeys[(realm, agentUser.Subject)] = [new KeycloakPasskey(phone, "My phone", DateTimeOffset.UtcNow.AddDays(-3)), new KeycloakPasskey(laptop, null, null)];
        _factory.Keycloak.Passkeys[(realm, requesterUser.Subject)] = [new KeycloakPasskey(requesterKey, "Tablet", null)];

        var mine = await agent.SendOkAsync(HttpMethod.Get, "/api/v1/me/passkeys");
        Assert.Equal(("Allowed", true), ((string?)mine["mode"], (bool)mine["secondFactor"]!));
        Assert.Equal([phone, laptop], mine["passkeys"]!.AsArray().Select(p => (string)p!["id"]!));
        Assert.Equal("My phone", (string?)mine["passkeys"]![0]!["label"]);
        Assert.False((bool)(await requester.SendOkAsync(HttpMethod.Get, "/api/v1/me/passkeys"))["secondFactor"]!);

        // Someone else's passkey is not found; one's own is removed and audited.
        Assert.Equal(HttpStatusCode.NotFound, (await agent.SendAsync(HttpMethod.Delete, $"/api/v1/me/passkeys/{requesterKey}")).StatusCode);
        Assert.Single(_factory.Keycloak.Passkeys[(realm, requesterUser.Subject)]);
        await agent.SendOkAsync(HttpMethod.Delete, $"/api/v1/me/passkeys/{phone}", expected: HttpStatusCode.NoContent);
        Assert.Equal([laptop], _factory.Keycloak.Passkeys[(realm, agentUser.Subject)].Select(p => p.Id));
        await using (var db = TestData.AppContext(database, tenant))
        {
            var removed = await db.AuditEvents.SingleAsync(e => e.Action == "identity.passkey.removed", Ct);
            Assert.Equal((AuditCategory.Security, agentUser.Id.ToString()), (removed.Category, removed.EntityId));
        }

        // Keycloak down: a clear 502.
        _factory.Keycloak.FailPasskeys = true;
        Assert.Equal(HttpStatusCode.BadGateway, (await agent.SendAsync(HttpMethod.Get, "/api/v1/me/passkeys")).StatusCode);
    }

    [Fact]
    public async Task Administrators_turn_passkeys_off_allow_or_require_them_and_remove_a_persons_passkeys()
    {
        var tenant = await database.CreateTenantAsync("pk2");
        var realm = $"zelvo-{tenant.Slug}";
        using var admin = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.TenantAdmin);
        using var lead = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.TeamLead);
        var agentUser = await database.CreateUserAsync(tenant, BuiltInRoles.Agent);

        Assert.Equal("Allowed", (string?)(await admin.SendOkAsync(HttpMethod.Get, "/api/v1/identity/settings"))["passkeys"]);
        Assert.Equal(HttpStatusCode.Forbidden, (await lead.SendAsync(HttpMethod.Put, "/api/v1/identity/settings/passkeys", new { mode = "Off" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.SendAsync(HttpMethod.Put, "/api/v1/identity/settings/passkeys", new { })).StatusCode);

        var saved = await admin.SendOkAsync(HttpMethod.Put, "/api/v1/identity/settings/passkeys", new { mode = "RequiredForStaff" });
        Assert.Equal("RequiredForStaff", (string?)saved["passkeys"]);
        Assert.Contains((realm, PasskeySignIn.RequiredForStaff), _factory.Keycloak.PasskeyCalls);
        // The email code setting is kept.
        Assert.Equal("KnownUsers", (string?)saved["emailCodeSignIn"]);

        // Keycloak refuses: nothing changes.
        _factory.Keycloak.FailPasskeys = true;
        Assert.Equal(HttpStatusCode.BadGateway, (await admin.SendAsync(HttpMethod.Put, "/api/v1/identity/settings/passkeys", new { mode = "Off" })).StatusCode);
        _factory.Keycloak.FailPasskeys = false;
        Assert.Equal("RequiredForStaff", (string?)(await admin.SendOkAsync(HttpMethod.Get, "/api/v1/identity/settings"))["passkeys"]);

        await using (var db = TestData.AppContext(database, tenant))
        {
            // The setting is audited as a change of the identity settings.
            Assert.Contains(await db.AuditEvents.Where(e => e.EntityType == "identity_settings").ToListAsync(Ct),
                e => e.Changes != null && e.Changes.Contains("RequiredForStaff", StringComparison.Ordinal));
        }

        // A lost phone: the administrator removes all of a person's passkeys.
        _factory.Keycloak.Passkeys[(realm, agentUser.Subject)] = [new KeycloakPasskey(Guid.NewGuid().ToString(), "Old phone", null), new KeycloakPasskey(Guid.NewGuid().ToString(), null, null)];
        Assert.Equal(HttpStatusCode.Forbidden, (await lead.SendAsync(HttpMethod.Delete, $"/api/v1/users/{agentUser.Id}/passkeys")).StatusCode);
        Assert.Equal(2, (int)(await admin.SendOkAsync(HttpMethod.Delete, $"/api/v1/users/{agentUser.Id}/passkeys"))["removed"]!);
        Assert.Empty(_factory.Keycloak.Passkeys[(realm, agentUser.Subject)]);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.SendAsync(HttpMethod.Delete, $"/api/v1/users/{Guid.NewGuid()}/passkeys")).StatusCode);
    }

    [Fact]
    public void The_realm_policy_needs_user_verification_and_discoverable_passkeys()
    {
        var policy = KeycloakAdminClient.PasskeyPolicy("zelvo-x", "Service Desk", PasskeySignIn.Allowed);
        Assert.Equal("required", (string?)policy["webAuthnPolicyPasswordlessUserVerificationRequirement"]);
        Assert.Equal("required", (string?)policy["webAuthnPolicyPasswordlessResidentKey"]); // Lower case: the browser ignores "Required" (checked against Keycloak 26.8.2).
        Assert.True((bool)policy["webAuthnPolicyPasswordlessPasskeysEnabled"]!);
        Assert.Equal("Service Desk", (string?)policy["webAuthnPolicyPasswordlessRpEntityName"]);
        Assert.Equal("", (string?)policy["webAuthnPolicyPasswordlessRpId"]);
        Assert.False((bool)KeycloakAdminClient.PasskeyPolicy("zelvo-x", null, PasskeySignIn.Off)["webAuthnPolicyPasswordlessPasskeysEnabled"]!);
        Assert.Equal("Zelvo", (string?)KeycloakAdminClient.PasskeyPolicy("zelvo-x", " ", PasskeySignIn.Off)["webAuthnPolicyPasswordlessRpEntityName"]);
    }
}
