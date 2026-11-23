using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Zelvo.Api.Integrations;
using Zelvo.Api.Security.RateLimiting;

namespace Zelvo.Api.Tests;

/// <summary>API keys and OAuth clients (module 14): scopes, roles, revocation, per-key limits, logging.</summary>
public sealed class ApiClientApiTests(DatabaseFixture database) : IAsyncDisposable
{
    private static readonly Uri Clients = new("/api/v1/integrations/clients", UriKind.Relative);
    private static readonly Uri Users = new("/api/v1/users", UriKind.Relative);
    private readonly ApiFactory _factory = ApiFactory.ForDatabase(database);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    [Fact]
    public async Task An_api_key_is_shown_once_and_acts_with_its_role_narrowed_by_its_scopes()
    {
        var tenant = await database.CreateTenantAsync("ak");
        using var admin = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, "tenant-admin");

        var (key, id) = await admin.CreateApiKeyAsync("agent", "users.read");

        Assert.StartsWith("zvk_", key, StringComparison.Ordinal);
        var listed = await (await admin.GetAsync(Clients, Ct)).JsonAsync();
        var item = listed["items"]!.AsArray().Single()!;
        Assert.Equal(id.ToString(), (string?)item["id"]);
        Assert.Equal("agent", (string?)item["role"]);
        Assert.Equal(600, (int?)item["rateLimitPerMinute"]);
        Assert.EndsWith("_…", (string?)item["keyHint"], StringComparison.Ordinal);
        Assert.DoesNotContain(key, listed.ToJsonString(), StringComparison.Ordinal);
        var stored = await database.ScalarAsync<byte[]>("SELECT key_hash FROM api_clients WHERE id = @id", ("id", id));
        Assert.Equal(ApiKeys.Hash(key), stored);

        using var integration = _factory.ForTenant(tenant).WithApiKey(key);
        using var users = await integration.GetAsync(Users, Ct);
        Assert.Equal(HttpStatusCode.OK, users.StatusCode);
        // The agent role may read email addresses; field rules apply to integrations too.
        Assert.NotNull((await users.JsonAsync())["items"]![0]!["email"]);

        // Roles that need a second factor apply: a key is a strong credential of its own.
        using var forbidden = await integration.GetAsync(new Uri("/api/v1/audit/events", UriKind.Relative), Ct);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
    }

    [Fact]
    public async Task Scopes_must_be_held_by_the_role_and_admin_rights_are_never_given_to_integrations()
    {
        var tenant = await database.CreateTenantAsync("sc");
        using var admin = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, "tenant-admin");

        foreach (var (role, scope) in new[] { ("tenant-admin", "users.manage"), ("tenant-admin", "integrations.manage"), ("tenant-admin", "roles.manage"),
                     ("tenant-admin", "identity.manage"), ("requester", "users.read"), ("agent", "no.such.permission") })
        {
            using var response = await admin.PostAsJsonAsync(Clients, new { kind = "ApiKey", name = "x", role, scopes = new[] { scope } }, Ct);
            var problem = await response.Content.ReadFromJsonAsync<ProblemBody>(Ct);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.True(problem!.Errors!.ContainsKey("scopes"), $"{role}/{scope}");
        }

        using var unknownRole = await admin.PostAsJsonAsync(Clients, new { kind = "ApiKey", name = "x", role = "nobody", scopes = Array.Empty<string>() }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, unknownRole.StatusCode);
        using var badLimit = await admin.PostAsJsonAsync(Clients, new { kind = "ApiKey", name = "x", role = "agent", scopes = Array.Empty<string>(), rateLimitPerMinute = 0 }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, badLimit.StatusCode);

        var info = await (await admin.GetAsync(new Uri("/api/v1/integrations/info", UriKind.Relative), Ct)).JsonAsync();
        var scopes = info["scopes"]!.AsArray().Select(s => (string)s!).ToList();
        Assert.Contains("users.read", scopes);
        Assert.DoesNotContain("users.manage", scopes);
        Assert.DoesNotContain("integrations.manage", scopes);
        Assert.Equal("development", (string?)info["environment"]);
        Assert.EndsWith($"/realms/zelvo-{tenant.Slug}/protocol/openid-connect/token", (string?)info["tokenUrl"], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Only_administrators_manage_integrations_and_integrations_never_manage_themselves()
    {
        var tenant = await database.CreateTenantAsync("im");
        using var agent = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, "agent");
        Assert.Equal(HttpStatusCode.Forbidden, (await agent.GetAsync(Clients, Ct)).StatusCode);

        using var admin = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, "tenant-admin");
        var (key, _) = await admin.CreateApiKeyAsync("tenant-admin", "users.read", "settings.manage");
        using var integration = _factory.ForTenant(tenant).WithApiKey(key);

        Assert.Equal(HttpStatusCode.Forbidden, (await integration.GetAsync(Clients, Ct)).StatusCode);
        using var patch = await integration.PatchAsJsonAsync(new Uri($"/api/v1/users/{Guid.NewGuid()}", UriKind.Relative), new { status = "Disabled" }, Ct);
        Assert.Equal(HttpStatusCode.Forbidden, patch.StatusCode);

        using var me = await integration.GetAsync(new Uri("/api/v1/me", UriKind.Relative), Ct);
        Assert.Equal(HttpStatusCode.Forbidden, me.StatusCode);
        Assert.Equal("people-only", (await me.Content.ReadFromJsonAsync<ProblemBody>(Ct))!.Code);
    }

    [Fact]
    public async Task Wrong_revoked_expired_or_other_tenants_keys_are_refused()
    {
        var tenant = await database.CreateTenantAsync("rk");
        var other = await database.CreateTenantAsync("ro");
        using var admin = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, "tenant-admin");
        var (key, id) = await admin.CreateApiKeyAsync("agent", "users.read");

        using var otherTenant = _factory.ForTenant(other).WithApiKey(key);
        Assert.Equal(HttpStatusCode.Unauthorized, (await otherTenant.GetAsync(Users, Ct)).StatusCode);
        using var tampered = _factory.ForTenant(tenant).WithApiKey(key[..^2] + (key[^1] == 'a' ? "bb" : "aa"));
        Assert.Equal(HttpStatusCode.Unauthorized, (await tampered.GetAsync(Users, Ct)).StatusCode);
        using var garbage = _factory.ForTenant(tenant).WithApiKey("not-a-key");
        Assert.Equal(HttpStatusCode.Unauthorized, (await garbage.GetAsync(Users, Ct)).StatusCode);

        using var integration = _factory.ForTenant(tenant).WithApiKey(key);
        Assert.Equal(HttpStatusCode.OK, (await integration.GetAsync(Users, Ct)).StatusCode);

        await database.ExecuteAsOwnerAsync("UPDATE api_clients SET expires_at = now() - interval '1 minute' WHERE id = @id", ("id", id));
        Assert.Equal(HttpStatusCode.Unauthorized, (await integration.GetAsync(Users, Ct)).StatusCode);
        await database.ExecuteAsOwnerAsync("UPDATE api_clients SET expires_at = NULL WHERE id = @id", ("id", id));
        Assert.Equal(HttpStatusCode.OK, (await integration.GetAsync(Users, Ct)).StatusCode);

        using var revoke = await admin.PostAsync(new Uri($"/api/v1/integrations/clients/{id}/revoke", UriKind.Relative), null, Ct);
        Assert.Equal("Revoked", (string?)(await revoke.JsonAsync())["status"]);
        Assert.Equal(HttpStatusCode.Unauthorized, (await integration.GetAsync(Users, Ct)).StatusCode);

        using var change = await admin.PatchAsJsonAsync(new Uri($"/api/v1/integrations/clients/{id}", UriKind.Relative), new { name = "again" }, Ct);
        Assert.Equal(HttpStatusCode.Conflict, change.StatusCode);
        Assert.Contains(await database.AuditAsync(tenant), a => a.Action == "integrations.client.revoked");
    }

    [Fact]
    public async Task Rotating_a_key_stops_the_old_one_at_once()
    {
        var tenant = await database.CreateTenantAsync("rt");
        using var admin = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, "tenant-admin");
        var (oldKey, id) = await admin.CreateApiKeyAsync("agent", "users.read");

        using var rotate = await admin.PostAsync(new Uri($"/api/v1/integrations/clients/{id}/rotate", UriKind.Relative), null, Ct);
        var newKey = (string)(await rotate.JsonAsync())["apiKey"]!;

        Assert.NotEqual(oldKey, newKey);
        using var withOld = _factory.ForTenant(tenant).WithApiKey(oldKey);
        Assert.Equal(HttpStatusCode.Unauthorized, (await withOld.GetAsync(Users, Ct)).StatusCode);
        using var withNew = _factory.ForTenant(tenant).WithApiKey(newKey);
        Assert.Equal(HttpStatusCode.OK, (await withNew.GetAsync(Users, Ct)).StatusCode);
    }

    [Fact]
    public async Task Every_call_is_in_the_integration_log_with_personal_data_masked_and_in_the_audit_log()
    {
        var tenant = await database.CreateTenantAsync("lg");
        using var admin = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, "tenant-admin");
        var (key, id) = await admin.CreateApiKeyAsync("agent", "users.read");
        using var integration = _factory.ForTenant(tenant).WithApiKey(key);

        Assert.Equal(HttpStatusCode.OK, (await integration.GetAsync(new Uri("/api/v1/users?page=1&email=someone%40example.ae", UriKind.Relative), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await integration.GetAsync(new Uri("/api/v1/audit/events", UriKind.Relative), Ct)).StatusCode);

        var log = await (await admin.GetAsync(new Uri($"/api/v1/integrations/log?apiClientId={id}", UriKind.Relative), Ct)).JsonAsync();
        var entries = log["items"]!.AsArray();
        Assert.Equal(2, entries.Count);
        Assert.Equal(403, (int?)entries[0]!["statusCode"]);
        Assert.Equal("CRM sync", (string?)entries[1]!["name"]);
        Assert.Equal("Incoming", (string?)entries[1]!["direction"]);
        Assert.DoesNotContain("someone", (string?)entries[1]!["target"], StringComparison.Ordinal);

        var detail = await (await admin.GetAsync(new Uri($"/api/v1/integrations/log/{(long)entries[1]!["id"]!}", UriKind.Relative), Ct)).JsonAsync();
        var response = (string?)detail["responseBody"];
        Assert.NotNull(response);
        Assert.Contains("[masked]", response, StringComparison.Ordinal);
        Assert.DoesNotContain("@example.test", response, StringComparison.Ordinal);

        var failedOnly = await (await admin.GetAsync(new Uri("/api/v1/integrations/log?failed=true", UriKind.Relative), Ct)).JsonAsync();
        Assert.Equal(1, (int?)failedOnly["totalCount"]);

        var audit = await database.AuditAsync(tenant);
        var calls = audit.Where(a => a.Action == "integrations.api.called").ToList();
        Assert.Equal(2, calls.Count);
        Assert.All(calls, c => Assert.Equal("ApiClient", c.ActorType));
        Assert.All(calls, c => Assert.Equal(id.ToString(), c.ActorId));
        Assert.NotNull(await database.ScalarAsync<DateTime?>("SELECT last_used_at FROM api_clients WHERE id = @id", ("id", id)));
    }

    [Fact]
    public async Task Oauth_clients_are_created_in_the_tenant_realm_and_their_tokens_act_with_the_client_role()
    {
        var tenant = await database.CreateTenantAsync("oa");
        using var admin = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, "tenant-admin");

        using var created = await admin.PostAsJsonAsync(Clients, new { kind = "OAuthClient", name = "ERP", role = "agent", scopes = new[] { "users.read" }, rateLimitPerMinute = 120 }, Ct);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var body = await created.JsonAsync();
        var clientId = (string)body["client"]!["clientId"]!;
        var id = Guid.Parse((string)body["client"]!["id"]!);
        Assert.StartsWith("zelvo-int-", clientId, StringComparison.Ordinal);
        Assert.NotNull((string?)body["clientSecret"]);
        Assert.Null((string?)body["apiKey"]);
        Assert.EndsWith("/protocol/openid-connect/token", (string?)body["client"]!["tokenUrl"], StringComparison.Ordinal);

        var realm = $"zelvo-{tenant.Slug}";
        var (spec, enabled, _) = _factory.Keycloak.IntegrationClients[(realm, clientId)];
        Assert.True(enabled);
        Assert.Equal(tenant.Id, spec.TenantId);
        Assert.Equal(id, spec.ApiClientId);
        Assert.Equal("zelvo-api", spec.Audience);

        using var integration = _factory.ForTenant(tenant);
        integration.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ClientToken(tenant, id, clientId));
        Assert.Equal(HttpStatusCode.OK, (await integration.GetAsync(Users, Ct)).StatusCode);
        Assert.Contains(await database.AuditAsync(tenant), a => a.Action == "integrations.api.called" && a.ActorId == id.ToString());

        // A token of another Keycloak client that claims this integration is refused.
        using var forged = _factory.ForTenant(tenant);
        forged.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ClientToken(tenant, id, "zelvo-web"));
        using var refused = await forged.GetAsync(Users, Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        Assert.Equal("credential-revoked", (await refused.Content.ReadFromJsonAsync<ProblemBody>(Ct))!.Code);

        // Revoked: Keycloak stops issuing tokens, and tokens issued before are refused at once.
        await admin.PostAsync(new Uri($"/api/v1/integrations/clients/{id}/revoke", UriKind.Relative), null, Ct);
        Assert.False(_factory.Keycloak.IntegrationClients[(realm, clientId)].Enabled);
        Assert.Equal(HttpStatusCode.Unauthorized, (await integration.GetAsync(Users, Ct)).StatusCode);
    }

    [Fact]
    public async Task Oauth_client_secrets_are_rotated_in_keycloak_and_nothing_is_created_when_keycloak_refuses()
    {
        var tenant = await database.CreateTenantAsync("os");
        using var admin = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, "tenant-admin");
        using var created = await admin.PostAsJsonAsync(Clients, new { kind = "OAuthClient", name = "ERP", role = "agent", scopes = Array.Empty<string>() }, Ct);
        var body = await created.JsonAsync();
        var id = (string)body["client"]!["id"]!;

        using var rotated = await admin.PostAsync(new Uri($"/api/v1/integrations/clients/{id}/rotate", UriKind.Relative), null, Ct);
        var secret = (string?)(await rotated.JsonAsync())["clientSecret"];
        Assert.NotEqual((string?)body["clientSecret"], secret);
        Assert.Equal(secret, _factory.Keycloak.IntegrationClients[($"zelvo-{tenant.Slug}", (string)body["client"]!["clientId"]!)].Secret);

        _factory.Keycloak.Fail = true;
        try
        {
            using var refused = await admin.PostAsJsonAsync(Clients, new { kind = "OAuthClient", name = "Other", role = "agent", scopes = Array.Empty<string>() }, Ct);
            Assert.Equal(HttpStatusCode.BadGateway, refused.StatusCode);
        }
        finally
        {
            _factory.Keycloak.Fail = false;
        }

        Assert.Equal(1, (int?)(await (await admin.GetAsync(Clients, Ct)).JsonAsync())["totalCount"]);
    }

    [Fact]
    public async Task Each_integration_has_its_own_rate_limit()
    {
        var tenant = await database.CreateTenantAsync("lim");
        await using var factory = ApiFactory.ForDatabase(database).WithServices(services =>
        {
            services.RemoveAll<IRateLimitStore>();
            services.AddSingleton<IRateLimitStore, CountingStore>();
        });
        using var admin = await factory.ForTenant(tenant).SignedInAsync(database, tenant, "tenant-admin");
        using var created = await admin.PostAsJsonAsync(Clients, new { kind = "ApiKey", name = "Slow", role = "agent", scopes = new[] { "users.read" }, rateLimitPerMinute = 2 }, Ct);
        var key = (string)(await created.JsonAsync())["apiKey"]!;
        var (otherKey, _) = await admin.CreateApiKeyAsync("agent", "users.read");

        using var integration = factory.ForTenant(tenant).WithApiKey(key);
        using var first = await integration.GetAsync(Users, Ct);
        Assert.Equal("2", first.Headers.GetValues("RateLimit-Limit").Single());
        Assert.Equal(HttpStatusCode.OK, (await integration.GetAsync(Users, Ct)).StatusCode);
        using var refused = await integration.GetAsync(Users, Ct);
        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
        Assert.Equal("rate-limited", (await refused.Content.ReadFromJsonAsync<ProblemBody>(Ct))!.Code);

        using var other = factory.ForTenant(tenant).WithApiKey(otherKey);
        using var allowed = await other.GetAsync(Users, Ct);
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        Assert.Equal("600", allowed.Headers.GetValues("RateLimit-Limit").Single());
    }

    [Fact]
    public async Task Tenants_see_only_their_own_integrations()
    {
        var tenant = await database.CreateTenantAsync("ta");
        var other = await database.CreateTenantAsync("tb");
        using var admin = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, "tenant-admin");
        var (_, id) = await admin.CreateApiKeyAsync("agent");

        using var otherAdmin = await _factory.ForTenant(other).SignedInAsync(database, other, "tenant-admin");
        Assert.Equal(0, (int?)(await (await otherAdmin.GetAsync(Clients, Ct)).JsonAsync())["totalCount"]);
        Assert.Equal(HttpStatusCode.NotFound, (await otherAdmin.GetAsync(new Uri($"/api/v1/integrations/clients/{id}", UriKind.Relative), Ct)).StatusCode);
    }

    [Fact]
    public async Task Integrations_read_incidents_with_their_scopes_but_changes_that_record_a_person_are_for_people()
    {
        var tenant = await database.CreateTenantAsync("ii");
        using var admin = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, "tenant-admin");
        var (reader, _) = await admin.CreateApiKeyAsync("tenant-admin", "incidents.read", "incidents.create");
        var (other, _) = await admin.CreateApiKeyAsync("tenant-admin", "users.read");
        var incidents = new Uri("/api/v1/incidents", UriKind.Relative);

        using var withScope = _factory.ForTenant(tenant).WithApiKey(reader);
        Assert.Equal(HttpStatusCode.OK, (await withScope.GetAsync(incidents, Ct)).StatusCode);
        using var create = await withScope.PostAsJsonAsync(incidents, new { title = "From the CRM", description = "Raised by an integration" }, Ct);
        Assert.Equal(HttpStatusCode.Forbidden, create.StatusCode);
        Assert.Equal("people-only", (await create.Content.ReadFromJsonAsync<ProblemBody>(Ct))!.Code);

        // Without incidents.read an integration is not a requester either: it sees none.
        using var withoutScope = _factory.ForTenant(tenant).WithApiKey(other);
        using var none = await withoutScope.GetAsync(incidents, Ct);
        Assert.Equal(HttpStatusCode.OK, none.StatusCode);
        Assert.Equal(0, (int?)(await none.JsonAsync())["totalCount"]);
    }

    [Fact]
    public async Task Integration_records_past_their_retention_are_removed()
    {
        var tenant = await database.CreateTenantAsync("pr");
        await database.ExecuteAsOwnerAsync("""
            INSERT INTO integration_log (tenant_id, occurred_at, direction, method, target, duration_ms)
            VALUES (@t, now() - interval '40 days', 'Incoming', 'GET', '/old', 1), (@t, now(), 'Incoming', 'GET', '/new', 1)
            """, ("t", tenant.Id));

        var purge = new Zelvo.Integrations.IntegrationRecordsPurge(
            _factory.Services.GetRequiredService<IServiceScopeFactory>(),
            _factory.Services.GetRequiredService<Zelvo.Integrations.IntegrationOptions>(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<Zelvo.Integrations.IntegrationRecordsPurge>.Instance);
        Assert.True(await purge.RunOnceAsync(Ct) >= 1);

        Assert.Equal("/new", await database.ScalarAsync<string>("SELECT string_agg(target, ',') FROM integration_log WHERE tenant_id = @t", ("t", tenant.Id)));
    }

    private static string ClientToken(Zelvo.Data.Tenancy.Tenant tenant, Guid apiClientId, string azp) =>
        TestTokens.Issue(tenant, FakeKeycloakAdmin.ServiceAccountFor(azp), mfa: false, extraClaims: new Dictionary<string, object>
        {
            [ApiClientClaims.ClientId] = apiClientId.ToString(),
            [ApiClientClaims.AuthorizedParty] = azp,
        });

    private sealed class CountingStore : IRateLimitStore
    {
        private readonly ConcurrentDictionary<string, long> _counts = new(StringComparer.Ordinal);

        public Task<RateLimitCount?> HitAsync(string key, TimeSpan window, CancellationToken cancellationToken) =>
            Task.FromResult<RateLimitCount?>(new RateLimitCount(_counts.AddOrUpdate(key, 1, (_, c) => c + 1), window));
    }
}
