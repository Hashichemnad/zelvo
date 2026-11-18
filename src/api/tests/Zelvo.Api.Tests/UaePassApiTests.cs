using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Zelvo.Api.Identity;
using Zelvo.Data.Auditing;
using Zelvo.Data.Identity;

namespace Zelvo.Api.Tests;

/// <summary>Settings → UAE PASS and the sign-in methods visitors see (GOV-1).</summary>
public sealed class UaePassApiTests(DatabaseFixture database) : IAsyncDisposable
{
    private static readonly Uri SettingsUri = new("/api/v1/identity/uae-pass", UriKind.Relative);
    private static readonly Uri MethodsUri = new("/api/v1/identity/sign-in-methods", UriKind.Relative);
    // A made-up client secret, built at run time so that secret scanning sees no literal.
    private static readonly string Secret = new('x', 32);

    private readonly ApiFactory _factory = ApiFactory.ForDatabase(database);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    [Fact]
    public async Task Uae_pass_starts_off_with_the_redirect_uri_to_register_and_no_mock_in_installs()
    {
        var tenant = await database.CreateTenantAsync();
        using var admin = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.TenantAdmin);

        var settings = await admin.GetFromJsonAsync<UaePassBody>(SettingsUri, Ct);

        Assert.False(settings!.Enabled);
        Assert.Equal("Staging", settings.Environment);
        Assert.Equal("", settings.ClientId);
        Assert.False(settings.ClientSecretSet);
        Assert.Null(settings.ClientSecretChangedAt);
        Assert.Equal($"{TestTokens.PublicUrl}/realms/zelvo-{tenant.Slug}/broker/uaepass/endpoint", settings.RedirectUri);
        Assert.Equal(["Staging", "Production"], settings.Environments);
        Assert.Null(settings.UpdatedAt);
    }

    [Fact]
    public async Task An_admin_turns_uae_pass_on_the_secret_goes_to_keycloak_only_and_the_change_is_audited_without_it()
    {
        var tenant = await database.CreateTenantAsync();
        using var admin = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.TenantAdmin);

        var response = await admin.PutAsJsonAsync(SettingsUri,
            new { enabled = true, environment = "Staging", clientId = " entity_web_stage ", clientSecret = Secret }, Ct);
        var raw = await response.Content.ReadAsStringAsync(Ct);
        var body = await response.Content.ReadFromJsonAsync<UaePassBody>(Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain(Secret, raw, StringComparison.Ordinal);
        Assert.True(body!.Enabled);
        Assert.Equal("entity_web_stage", body.ClientId);
        Assert.True(body.ClientSecretSet);
        Assert.NotNull(body.ClientSecretChangedAt);

        var (realm, provider) = Assert.Single(_factory.Keycloak.UaePassCalls);
        Assert.Equal($"zelvo-{tenant.Slug}", realm);
        Assert.True(provider.Enabled);
        Assert.Equal("entity_web_stage", provider.ClientId);
        Assert.Equal(Secret, provider.ClientSecret);
        Assert.Equal("https://stg-id.uaepass.ae/idshub/authorize", provider.Endpoints.Authorization);
        Assert.Equal("https://stg-id.uaepass.ae/idshub/token", provider.Endpoints.Token);
        Assert.Equal("https://stg-id.uaepass.ae/idshub/userinfo", provider.Endpoints.UserInfo);

        await using var db = TestData.AppContext(database, tenant);
        var events = await db.AuditEvents.Where(e => e.EntityType == "tenant_setting").ToListAsync(Ct);
        Assert.Equal(
            [$"{tenant.Id},identity.uaePass.clientId", $"{tenant.Id},identity.uaePass.clientSecretChangedAt", $"{tenant.Id},identity.uaePass.enabled", $"{tenant.Id},identity.uaePass.environment"],
            events.Select(e => e.EntityId!).Order(StringComparer.Ordinal));
        Assert.All(events, e => Assert.DoesNotContain(Secret, (e.Changes ?? "") + (e.Metadata ?? ""), StringComparison.Ordinal));
    }

    [Fact]
    public async Task Leaving_the_secret_out_keeps_it_and_switching_to_production_uses_production_addresses()
    {
        var tenant = await database.CreateTenantAsync();
        using var admin = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.TenantAdmin);
        (await admin.PutAsJsonAsync(SettingsUri, new { enabled = true, clientId = "entity", clientSecret = Secret }, Ct)).EnsureSuccessStatusCode();
        var first = await admin.GetFromJsonAsync<UaePassBody>(SettingsUri, Ct);

        var response = await admin.PutAsJsonAsync(SettingsUri, new { enabled = true, environment = "Production", clientId = "entity" }, Ct);
        var second = await response.Content.ReadFromJsonAsync<UaePassBody>(Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Production", second!.Environment);
        Assert.True(second.ClientSecretSet);
        Assert.Equal(first!.ClientSecretChangedAt, second.ClientSecretChangedAt);
        var provider = _factory.Keycloak.UaePassCalls[^1].Provider;
        Assert.Null(provider.ClientSecret);
        Assert.Equal("https://id.uaepass.ae/idshub/authorize", provider.Endpoints.Authorization);
    }

    [Fact]
    public async Task Turning_uae_pass_off_disables_the_provider_and_visitors_no_longer_see_it()
    {
        var tenant = await database.CreateTenantAsync();
        using var admin = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.TenantAdmin);
        using var visitor = _factory.ForTenant(tenant);
        (await admin.PutAsJsonAsync(SettingsUri, new { enabled = true, clientId = "entity", clientSecret = Secret }, Ct)).EnsureSuccessStatusCode();
        var whileOn = await visitor.GetFromJsonAsync<MethodsBody>(MethodsUri, Ct);

        (await admin.PutAsJsonAsync(SettingsUri, new { enabled = false, clientId = "entity" }, Ct)).EnsureSuccessStatusCode();
        var whileOff = await visitor.GetFromJsonAsync<MethodsBody>(MethodsUri, Ct);

        Assert.Equal(new UaePassSignInBody(true, "https://stg-id.uaepass.ae/idshub/logout"), whileOn!.UaePass);
        Assert.Equal(new UaePassSignInBody(false, null), whileOff!.UaePass);
        Assert.False(_factory.Keycloak.UaePassCalls[^1].Provider.Enabled);
    }

    [Fact]
    public async Task Saving_while_never_set_up_and_off_does_not_touch_keycloak()
    {
        var tenant = await database.CreateTenantAsync();
        using var admin = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.TenantAdmin);

        var response = await admin.PutAsJsonAsync(SettingsUri, new { enabled = false, environment = "Production", clientId = "entity" }, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(_factory.Keycloak.UaePassCalls);
    }

    [Theory]
    [InlineData("""{"enabled":true,"clientId":"entity"}""", "clientSecret")]
    [InlineData("""{"enabled":true,"clientSecret":"x"}""", "clientId")]
    [InlineData("""{"enabled":true,"clientId":"has space","clientSecret":"x"}""", "clientId")]
    [InlineData("""{"enabled":false,"environment":"Mock"}""", "environment")]
    [InlineData("""{"environment":"Staging"}""", "Enabled")]
    public async Task Incomplete_or_invalid_settings_are_refused(string json, string field)
    {
        var tenant = await database.CreateTenantAsync();
        using var admin = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.TenantAdmin);

        using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        var response = await admin.PutAsync(SettingsUri, content, Ct);
        var problem = await response.Content.ReadFromJsonAsync<ProblemBody>(Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(problem!.Errors!.Keys, k => k.Equals(field, StringComparison.OrdinalIgnoreCase));
        Assert.Empty(_factory.Keycloak.UaePassCalls);
    }

    [Fact]
    public async Task Development_installations_offer_the_mock_whose_server_calls_use_the_internal_address()
    {
        await using var factory = ApiFactory.ForDatabase(database)
            .WithSetting("Identity:UaePass:Mock:PublicUrl", "http://localhost:8095/")
            .WithSetting("Identity:UaePass:Mock:InternalUrl", "http://uaepass-mock:8095");
        var tenant = await database.CreateTenantAsync();
        using var admin = await factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.TenantAdmin);

        var response = await admin.PutAsJsonAsync(SettingsUri, new { enabled = true, environment = "Mock", clientId = "zelvo-mock", clientSecret = Secret }, Ct);
        var body = await response.Content.ReadFromJsonAsync<UaePassBody>(Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(["Staging", "Production", "Mock"], body!.Environments);
        var endpoints = Assert.Single(factory.Keycloak.UaePassCalls).Provider.Endpoints;
        Assert.Equal("http://localhost:8095/idshub/authorize", endpoints.Authorization);
        Assert.Equal("http://uaepass-mock:8095/idshub/token", endpoints.Token);
        Assert.Equal("http://uaepass-mock:8095/idshub/userinfo", endpoints.UserInfo);
        Assert.Equal("http://localhost:8095/idshub/logout", endpoints.Logout);
    }

    [Fact]
    public async Task When_keycloak_refuses_nothing_changes()
    {
        var tenant = await database.CreateTenantAsync();
        using var admin = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.TenantAdmin);
        _factory.Keycloak.Fail = true;

        var response = await admin.PutAsJsonAsync(SettingsUri, new { enabled = true, clientId = "entity", clientSecret = Secret }, Ct);
        _factory.Keycloak.Fail = false;
        var settings = await admin.GetFromJsonAsync<UaePassBody>(SettingsUri, Ct);

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal("identity-provider-unavailable", (await response.Content.ReadFromJsonAsync<ProblemBody>(Ct))!.Code);
        Assert.False(settings!.Enabled);
        Assert.False(settings.ClientSecretSet);
        Assert.Equal("", settings.ClientId);
    }

    [Theory]
    [InlineData(BuiltInRoles.TeamLead)]
    [InlineData(BuiltInRoles.Agent)]
    [InlineData(BuiltInRoles.Requester)]
    public async Task Only_identity_managers_see_or_change_uae_pass_settings(string role)
    {
        var tenant = await database.CreateTenantAsync();
        using var user = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, role);
        using var visitor = _factory.ForTenant(tenant);

        var read = await user.GetAsync(SettingsUri, Ct);
        var change = await user.PutAsJsonAsync(SettingsUri, new { enabled = true, clientId = "entity", clientSecret = Secret }, Ct);
        var anonymous = await visitor.GetAsync(SettingsUri, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, read.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, change.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Empty(_factory.Keycloak.UaePassCalls);
    }

    [Fact]
    public async Task Settings_of_one_tenant_are_not_seen_by_another()
    {
        var tenant = await database.CreateTenantAsync();
        var other = await database.CreateTenantAsync();
        using var admin = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.TenantAdmin);
        using var otherVisitor = _factory.ForTenant(other);
        (await admin.PutAsJsonAsync(SettingsUri, new { enabled = true, clientId = "entity", clientSecret = Secret }, Ct)).EnsureSuccessStatusCode();

        var methods = await otherVisitor.GetFromJsonAsync<MethodsBody>(MethodsUri, Ct);

        Assert.False(methods!.UaePass.Enabled);
    }

    [Fact]
    public void The_keycloak_provider_uses_the_oauth2_broker_basic_auth_uuid_and_the_zelvo_flows()
    {
        var endpoints = UaePassEndpoints.At("https://stg-id.uaepass.ae", "https://stg-id.uaepass.ae");

        var kept = KeycloakAdminClient.UaePassRepresentation(new UaePassProvider(true, endpoints, "entity", null));
        var given = KeycloakAdminClient.UaePassRepresentation(new UaePassProvider(false, endpoints, "entity", Secret));

        Assert.Equal("oauth2", (string?)kept["providerId"]);
        Assert.Equal("uaepass", (string?)kept["alias"]);
        Assert.False((bool)kept["trustEmail"]!);
        Assert.Equal(UaePassProvider.FirstLoginFlow, (string?)kept["firstBrokerLoginFlowAlias"]);
        Assert.Equal(UaePassProvider.AfterLoginFlow, (string?)kept["postBrokerLoginFlowAlias"]);
        var config = kept["config"]!;
        Assert.Equal(
            "https://stg-id.uaepass.ae/idshub/authorize?acr_values=urn%3Asafelayer%3Atws%3Apolicies%3Aauthentication%3Alevel%3Alow",
            (string?)config["authorizationUrl"]);
        Assert.Equal("client_secret_basic", (string?)config["clientAuthMethod"]);
        Assert.Equal("urn:uae:digitalid:profile:general", (string?)config["defaultScope"]);
        Assert.Equal("uuid", (string?)config["userIDClaim"]);
        Assert.Equal("true", (string?)config["uiLocales"]);
        Assert.Equal("**********", (string?)config["clientSecret"]);
        Assert.Equal(Secret, (string?)given["config"]!["clientSecret"]);
        Assert.False((bool)given["enabled"]!);
    }

    private sealed record UaePassBody(
        bool Enabled, string Environment, string ClientId, bool ClientSecretSet, DateTimeOffset? ClientSecretChangedAt,
        string RedirectUri, List<string> Environments, DateTimeOffset? UpdatedAt);

    private sealed record MethodsBody(UaePassSignInBody UaePass);

    private sealed record UaePassSignInBody(bool Enabled, string? SignOutUrl);
}
