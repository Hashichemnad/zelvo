using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Zelvo.Api.Identity;
using Zelvo.Data.Auditing;
using Zelvo.Data.Identity;

namespace Zelvo.Api.Tests;

/// <summary>
/// GOV-1 rules (product owner decisions 2026-11-18): UAE PASS as the second factor for staff,
/// minimum account levels, automatic requester accounts, and Emirates IDs for matching staff.
/// </summary>
public sealed class UaePassRulesApiTests(DatabaseFixture database) : IAsyncDisposable
{
    private static readonly Uri SettingsUri = new("/api/v1/identity/uae-pass", UriKind.Relative);
    private static readonly Uri MeUri = new("/api/v1/me", UriKind.Relative);
    // A made-up client secret, built at run time so that secret scanning sees no literal.
    private static readonly string Secret = new('x', 32);

    private readonly ApiFactory _factory = ApiFactory.ForDatabase(database);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    private async Task SetRulesAsync(Zelvo.Data.Tenancy.Tenant tenant, object rules)
    {
        using var admin = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.TenantAdmin);
        (await admin.PutAsJsonAsync(SettingsUri, rules, Ct)).EnsureSuccessStatusCode();
    }

    private async Task<HttpResponseMessage> MeAsUaePassAsync(TestUser user, string? level, bool mfa = false)
    {
        using var client = _factory.ForTenant(user.Tenant).AsUaePass(user, level, mfa);
        return await client.GetAsync(MeUri, Ct);
    }

    [Fact]
    public async Task The_rules_start_at_the_approved_defaults()
    {
        var tenant = await database.CreateTenantAsync();
        using var admin = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.TenantAdmin);

        var settings = await admin.GetFromJsonAsync<RulesBody>(SettingsUri, Ct);

        Assert.Equal(new RulesBody(false, "SOP1", "SOP2", true), settings);
    }

    [Fact]
    public async Task Saved_rules_reach_the_keycloak_first_sign_in_step_and_are_audited()
    {
        var tenant = await database.CreateTenantAsync();
        await SetRulesAsync(tenant, new
        {
            enabled = true, clientId = "entity", clientSecret = Secret,
            acceptAsSecondFactor = true, requesterMinimumLevel = "SOP2", createRequesters = false,
        });

        var provider = Assert.Single(_factory.Keycloak.UaePassCalls).Provider;
        Assert.False(provider.CreateRequesters);
        Assert.Equal(UaePassLevel.SOP2, provider.RequesterMinimumLevel);
        Assert.Equal(new Dictionary<string, string> { ["createRequesters"] = "false", ["requesterMinimumLevel"] = "SOP2" },
            KeycloakAdminClient.FirstLoginConfig(provider));

        await using var db = TestData.AppContext(database, tenant);
        var audited = await db.AuditEvents.Where(e => e.EntityType == "tenant_setting").Select(e => e.EntityId!).ToListAsync(Ct);
        Assert.Contains($"{tenant.Id},identity.uaePass.acceptAsSecondFactor", audited);
        Assert.Contains($"{tenant.Id},identity.uaePass.requesterMinimumLevel", audited);
        Assert.Contains($"{tenant.Id},identity.uaePass.createRequesters", audited);
    }

    [Fact]
    public async Task Leaving_rules_out_keeps_them()
    {
        var tenant = await database.CreateTenantAsync();
        await SetRulesAsync(tenant, new { enabled = false, acceptAsSecondFactor = true, requesterMinimumLevel = "SOP3" });
        await SetRulesAsync(tenant, new { enabled = false, clientId = "entity" });
        using var admin = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.TenantAdmin);

        var settings = await admin.GetFromJsonAsync<RulesBody>(SettingsUri, Ct);

        Assert.Equal(new RulesBody(true, "SOP3", "SOP2", true), settings);
    }

    [Theory]
    [InlineData("SOP1", false)]
    [InlineData(null, false)]
    [InlineData("SOP2", true)]
    [InlineData("SOP3", true)]
    public async Task Staff_always_need_a_verified_uae_pass_account(string? level, bool allowed)
    {
        var tenant = await database.CreateTenantAsync();
        var agent = await database.CreateUserAsync(tenant, BuiltInRoles.Agent);

        var response = await MeAsUaePassAsync(agent, level, mfa: true);

        if (allowed)
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        else
        {
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Equal("uae-pass-level-too-low", (await response.Content.ReadFromJsonAsync<ProblemBody>(Ct))!.Code);
        }
    }

    [Fact]
    public async Task By_default_staff_still_need_their_authenticator_app_after_uae_pass()
    {
        var tenant = await database.CreateTenantAsync();
        var agent = await database.CreateUserAsync(tenant, BuiltInRoles.Agent);

        var me = await (await MeAsUaePassAsync(agent, "SOP3")).Content.ReadFromJsonAsync<MeBody>(Ct);

        Assert.Equal(new MfaBody(false, true), me!.Mfa);
        Assert.DoesNotContain(Permissions.UsersRead, me.Permissions);
    }

    [Fact]
    public async Task When_the_tenant_accepts_it_a_verified_uae_pass_sign_in_is_the_second_factor()
    {
        var tenant = await database.CreateTenantAsync();
        await SetRulesAsync(tenant, new { enabled = false, acceptAsSecondFactor = true });
        var agent = await database.CreateUserAsync(tenant, BuiltInRoles.Agent);

        var me = await (await MeAsUaePassAsync(agent, "SOP2")).Content.ReadFromJsonAsync<MeBody>(Ct);

        Assert.Equal(new MfaBody(true, false), me!.Mfa);
        Assert.Contains(Permissions.UsersRead, me.Permissions);
    }

    [Fact]
    public async Task Accepting_uae_pass_needs_the_uae_pass_claim_not_just_a_level()
    {
        var tenant = await database.CreateTenantAsync();
        await SetRulesAsync(tenant, new { enabled = false, acceptAsSecondFactor = true });
        var agent = await database.CreateUserAsync(tenant, BuiltInRoles.Agent);
        using var client = _factory.ForTenant(tenant);
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer",
            TestTokens.Issue(tenant, agent.Subject, mfa: false, extraClaims: new Dictionary<string, object> { [ZelvoClaimTypes.UaePassLevel] = "SOP3" }));

        var me = await client.GetFromJsonAsync<MeBody>(MeUri, Ct);

        Assert.Equal(new MfaBody(false, true), me!.Mfa);
    }

    [Fact]
    public async Task Requesters_need_the_tenants_minimum_level()
    {
        var tenant = await database.CreateTenantAsync();
        var requester = await database.CreateUserAsync(tenant, BuiltInRoles.Requester);
        var allowedByDefault = await MeAsUaePassAsync(requester, "SOP1");
        await SetRulesAsync(tenant, new { enabled = false, requesterMinimumLevel = "SOP2" });

        var basic = await MeAsUaePassAsync(requester, "SOP1");
        var verified = await MeAsUaePassAsync(requester, "SOP2");

        Assert.Equal(HttpStatusCode.OK, allowedByDefault.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, basic.StatusCode);
        Assert.Equal(HttpStatusCode.OK, verified.StatusCode);
    }

    [Fact]
    public async Task Other_sign_ins_are_not_affected_by_the_uae_pass_rules()
    {
        var tenant = await database.CreateTenantAsync();
        await SetRulesAsync(tenant, new { enabled = false, requesterMinimumLevel = "SOP3" });
        var requester = await database.CreateUserAsync(tenant, BuiltInRoles.Requester);
        using var client = _factory.ForTenant(tenant).As(requester, mfa: false);

        var response = await client.GetAsync(MeUri, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task An_admin_records_an_emirates_id_which_goes_to_keycloak_and_stays_hidden_from_others()
    {
        var tenant = await database.CreateTenantAsync();
        var lead = await database.CreateUserAsync(tenant, BuiltInRoles.TeamLead);
        using var admin = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.TenantAdmin);
        using var agent = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.Agent);

        var response = await admin.PatchAsJsonAsync(new Uri($"/api/v1/users/{lead.Id}", UriKind.Relative), new { emiratesId = "784-1990-1234567-1" }, Ct);
        var body = await response.Content.ReadFromJsonAsync<UserBody>(Ct);
        var seenByAgent = await agent.GetFromJsonAsync<UserBody>(new Uri($"/api/v1/users/{lead.Id}", UriKind.Relative), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("784199012345671", body!.EmiratesId);
        Assert.Equal(($"zelvo-{tenant.Slug}", lead.Subject, (string?)"784199012345671"), Assert.Single(_factory.Keycloak.EmiratesIdCalls));
        Assert.Null(seenByAgent!.EmiratesId);
        await using var db = TestData.AppContext(database, tenant);
        var changes = await db.AuditEvents.Where(e => e.EntityType == "user_account" && e.Category == AuditCategory.Change)
            .Select(e => e.Changes).ToListAsync(Ct);
        Assert.Contains(changes, c => c != null && c.Contains("emirates", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(changes, c => c != null && c.Contains("784199012345671", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_emirates_id_belongs_to_one_user_is_well_formed_and_can_be_removed()
    {
        var tenant = await database.CreateTenantAsync();
        var first = await database.CreateUserAsync(tenant, BuiltInRoles.Agent);
        var second = await database.CreateUserAsync(tenant, BuiltInRoles.Agent);
        using var admin = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.TenantAdmin);
        Uri U(TestUser u) => new($"/api/v1/users/{u.Id}", UriKind.Relative);
        (await admin.PatchAsJsonAsync(U(first), new { emiratesId = "784199012345671" }, Ct)).EnsureSuccessStatusCode();

        var duplicate = await admin.PatchAsJsonAsync(U(second), new { emiratesId = "784199012345671" }, Ct);
        var malformed = await admin.PatchAsJsonAsync(U(second), new { emiratesId = "123456789012345" }, Ct);
        var removed = await admin.PatchAsJsonAsync(U(first), new { emiratesId = "" }, Ct);

        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Equal("emirates-id-in-use", (await duplicate.Content.ReadFromJsonAsync<ProblemBody>(Ct))!.Code);
        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
        Assert.Null((await removed.Content.ReadFromJsonAsync<UserBody>(Ct))!.EmiratesId);
        Assert.Equal((string?)null, _factory.Keycloak.EmiratesIdCalls[^1].EmiratesId);
    }

    [Fact]
    public async Task Only_roles_with_edit_access_may_record_an_emirates_id()
    {
        var tenant = await database.CreateTenantAsync();
        var lead = await database.CreateUserAsync(tenant, BuiltInRoles.TeamLead);
        using var agent = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.Agent);

        var response = await agent.PatchAsJsonAsync(new Uri($"/api/v1/users/{lead.Id}", UriKind.Relative), new { emiratesId = "784199012345671" }, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(_factory.Keycloak.EmiratesIdCalls);
    }

    [Fact]
    public async Task When_keycloak_refuses_the_emirates_id_is_not_saved()
    {
        var tenant = await database.CreateTenantAsync();
        var lead = await database.CreateUserAsync(tenant, BuiltInRoles.TeamLead);
        using var admin = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.TenantAdmin);
        _factory.Keycloak.Fail = true;

        var response = await admin.PatchAsJsonAsync(new Uri($"/api/v1/users/{lead.Id}", UriKind.Relative), new { emiratesId = "784199012345671" }, Ct);

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        await using var db = TestData.AppContext(database, tenant);
        Assert.Null((await db.Users.SingleAsync(u => u.Id == lead.Id, Ct)).EmiratesId);
    }

    private sealed record RulesBody(bool AcceptAsSecondFactor, string RequesterMinimumLevel, string StaffMinimumLevel, bool CreateRequesters);

    private sealed record MfaBody(bool Satisfied, bool Required);

    private sealed record MeBody(List<string> Permissions, MfaBody Mfa);

    private sealed record UserBody(Guid Id, string? EmiratesId);
}
