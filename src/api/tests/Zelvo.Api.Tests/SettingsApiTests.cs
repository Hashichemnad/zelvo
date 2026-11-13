using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Zelvo.Data.Auditing;
using Zelvo.Data.Identity;

namespace Zelvo.Api.Tests;

/// <summary>Tenant settings store and the language and region defaults (LANG-1, LANG-8).</summary>
public sealed class SettingsApiTests(DatabaseFixture database) : IAsyncDisposable
{
    internal static readonly Uri LocalizationUri = new("/api/v1/settings/localization", UriKind.Relative);
    private readonly ApiFactory _factory = ApiFactory.ForDatabase(database);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    [Fact]
    public async Task Anonymous_visitors_get_the_platform_defaults_until_an_admin_changes_them()
    {
        var tenant = await database.CreateTenantAsync();
        using var anonymous = _factory.ForTenant(tenant);

        var settings = await anonymous.GetFromJsonAsync<LocalizationBody>(LocalizationUri, Ct);

        Assert.Equal(new LocalizationBody("en", "Gregorian", "Western", null), settings);
    }

    [Fact]
    public async Task An_admin_changes_the_defaults_everyone_sees_and_each_change_is_audited()
    {
        var tenant = await database.CreateTenantAsync();
        using var admin = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.TenantAdmin);
        using var anonymous = _factory.ForTenant(tenant);

        var response = await admin.PutAsJsonAsync(LocalizationUri, new { defaultLanguage = "ar", defaultCalendar = "Hijri", defaultDigits = "ArabicIndic" }, Ct);
        var saved = await response.Content.ReadFromJsonAsync<LocalizationBody>(Ct);
        var seen = await anonymous.GetFromJsonAsync<LocalizationBody>(LocalizationUri, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(("ar", "Hijri", "ArabicIndic"), (saved!.DefaultLanguage, saved.DefaultCalendar, saved.DefaultDigits));
        Assert.NotNull(saved.UpdatedAt);
        Assert.Equal(saved, seen);

        await using var db = TestData.AppContext(database, tenant);
        var audited = await db.AuditEvents.Where(e => e.EntityType == "tenant_setting").Select(e => e.EntityId).ToListAsync(Ct);
        Assert.Equal(
            [$"{tenant.Id},localization.defaultCalendar", $"{tenant.Id},localization.defaultDigits", $"{tenant.Id},localization.defaultLanguage"],
            audited.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Saving_the_same_values_again_changes_and_audits_nothing()
    {
        var tenant = await database.CreateTenantAsync();
        using var admin = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.TenantAdmin);
        var body = new { defaultLanguage = "ar", defaultCalendar = "Gregorian", defaultDigits = "Western" };

        (await admin.PutAsJsonAsync(LocalizationUri, body, Ct)).EnsureSuccessStatusCode();
        await using var db = TestData.AppContext(database, tenant);
        var before = await db.AuditEvents.CountAsync(e => e.EntityType == "tenant_setting", Ct);
        (await admin.PutAsJsonAsync(LocalizationUri, body, Ct)).EnsureSuccessStatusCode();
        var after = await db.AuditEvents.CountAsync(e => e.EntityType == "tenant_setting", Ct);

        Assert.Equal(3, before);
        Assert.Equal(before, after);
    }

    [Fact]
    public async Task Only_holders_of_settings_manage_may_change_the_defaults()
    {
        var tenant = await database.CreateTenantAsync();
        using var agent = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.Agent);
        using var anonymous = _factory.ForTenant(tenant);
        var body = new { defaultLanguage = "ar", defaultCalendar = "Hijri", defaultDigits = "Western" };

        var byAgent = await agent.PutAsJsonAsync(LocalizationUri, body, Ct);
        var byAnonymous = await anonymous.PutAsJsonAsync(LocalizationUri, body, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, byAgent.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, byAnonymous.StatusCode);
        Assert.Equal("en", (await anonymous.GetFromJsonAsync<LocalizationBody>(LocalizationUri, Ct))!.DefaultLanguage);
    }

    public static TheoryData<string, string> InvalidBodies => new()
    {
        { """{"defaultLanguage":"fr","defaultCalendar":"Hijri","defaultDigits":"Western"}""", "defaultLanguage" },
        { """{"defaultLanguage":"ar","defaultDigits":"Western"}""", "defaultCalendar" },
    };

    [Theory]
    [MemberData(nameof(InvalidBodies))]
    public async Task Invalid_values_are_refused(string json, string field)
    {
        var tenant = await database.CreateTenantAsync();
        using var admin = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.TenantAdmin);

        using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        var response = await admin.PutAsync(LocalizationUri, content, Ct);
        var problem = await response.Content.ReadFromJsonAsync<ProblemBody>(Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("validation-failed", problem!.Code);
        Assert.Contains(problem.Errors!.Keys, k => k.Equals(field, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Unknown_enum_values_are_refused()
    {
        var tenant = await database.CreateTenantAsync();
        using var admin = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.TenantAdmin);

        var response = await admin.PutAsJsonAsync(LocalizationUri, new { defaultLanguage = "ar", defaultCalendar = "Julian", defaultDigits = "Western" }, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Settings_belong_to_one_tenant()
    {
        var a = await database.CreateTenantAsync("a");
        var b = await database.CreateTenantAsync("b");
        using var adminOfA = await _factory.ForTenant(a).SignedInAsync(database, a, BuiltInRoles.TenantAdmin);
        using var visitorOfB = _factory.ForTenant(b);

        (await adminOfA.PutAsJsonAsync(LocalizationUri, new { defaultLanguage = "ar", defaultCalendar = "Hijri", defaultDigits = "ArabicIndic" }, Ct))
            .EnsureSuccessStatusCode();

        Assert.Equal(new LocalizationBody("en", "Gregorian", "Western", null), await visitorOfB.GetFromJsonAsync<LocalizationBody>(LocalizationUri, Ct));
    }

    [Fact]
    public async Task A_stored_value_that_is_no_longer_valid_falls_back_to_the_default()
    {
        var tenant = await database.CreateTenantAsync();
        await database.ExecuteAsOwnerAsync(
            "INSERT INTO tenant_settings (tenant_id, key, value, updated_at) VALUES (@t, 'localization.defaultLanguage', '\"fr\"', now())",
            ("t", tenant.Id));
        using var anonymous = _factory.ForTenant(tenant);

        var settings = await anonymous.GetFromJsonAsync<LocalizationBody>(LocalizationUri, Ct);

        Assert.Equal("en", settings!.DefaultLanguage);
    }

    internal sealed record LocalizationBody(string DefaultLanguage, string DefaultCalendar, string DefaultDigits, DateTimeOffset? UpdatedAt);
}
