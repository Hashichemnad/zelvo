using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Zelvo.Data.Auditing;
using Zelvo.Data.Identity;

namespace Zelvo.Api.Tests;

/// <summary>A user's own language, calendar and digits over the tenant defaults (LANG-2, LANG-8).</summary>
public sealed class PreferencesApiTests(DatabaseFixture database) : IAsyncDisposable
{
    private static readonly Uri PreferencesUri = new("/api/v1/me/preferences", UriKind.Relative);
    private static readonly Uri MeUri = new("/api/v1/me", UriKind.Relative);
    private readonly ApiFactory _factory = ApiFactory.ForDatabase(database);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    [Fact]
    public async Task Without_own_choices_the_tenant_defaults_apply()
    {
        var tenant = await database.CreateTenantAsync();
        await SetTenantDefaultsAsync(tenant, "ar", "Hijri", "Western");
        using var user = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.Requester);

        var preferences = await user.GetFromJsonAsync<PreferencesBody>(PreferencesUri, Ct);
        var me = await user.GetFromJsonAsync<MeWithPreferences>(MeUri, Ct);

        Assert.Equal(new PreferencesBody(null, null, null, new EffectiveBody("ar", "Hijri", "Western")), preferences);
        Assert.Equal(preferences, me!.Preferences);
    }

    [Fact]
    public async Task Own_choices_win_over_the_tenant_defaults_and_are_audited()
    {
        var tenant = await database.CreateTenantAsync();
        await SetTenantDefaultsAsync(tenant, "ar", "Hijri", "ArabicIndic");
        var account = await database.CreateUserAsync(tenant, BuiltInRoles.Agent);
        using var user = _factory.ForTenant(tenant).As(account);

        var response = await user.PutAsJsonAsync(PreferencesUri, new { language = "en", calendar = "Gregorian", digits = (string?)null }, Ct);
        var saved = await response.Content.ReadFromJsonAsync<PreferencesBody>(Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(new PreferencesBody("en", "Gregorian", null, new EffectiveBody("en", "Gregorian", "ArabicIndic")), saved);

        await using var db = TestData.AppContext(database, tenant);
        var changes = await db.AuditEvents
            .Where(e => e.EntityType == "user_account" && e.EntityId == account.Id.ToString() && e.Category == AuditCategory.Change)
            .ToListAsync(Ct);
        var change = Assert.Single(changes, e => e.Changes!.Contains("preferredLanguage", StringComparison.Ordinal));
        Assert.Contains("calendar", change.Changes, StringComparison.Ordinal);
        Assert.Equal(account.Id.ToString(), change.ActorId);
    }

    [Fact]
    public async Task Null_returns_to_the_tenant_default()
    {
        var tenant = await database.CreateTenantAsync();
        using var user = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.Requester);

        (await user.PutAsJsonAsync(PreferencesUri, new { language = "ar", calendar = "Hijri", digits = "ArabicIndic" }, Ct)).EnsureSuccessStatusCode();
        var response = await user.PutAsJsonAsync(PreferencesUri, new { language = (string?)null, calendar = (string?)null, digits = (string?)null }, Ct);

        Assert.Equal(new PreferencesBody(null, null, null, new EffectiveBody("en", "Gregorian", "Western")),
            await response.Content.ReadFromJsonAsync<PreferencesBody>(Ct));
    }

    [Fact]
    public async Task Unsupported_languages_are_refused()
    {
        var tenant = await database.CreateTenantAsync();
        using var user = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.Requester);

        var response = await user.PutAsJsonAsync(PreferencesUri, new { language = "fr" }, Ct);
        var problem = await response.Content.ReadFromJsonAsync<ProblemBody>(Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("validation-failed", problem!.Code);
        Assert.True(problem.Errors!.ContainsKey("language"));
    }

    [Fact]
    public async Task Preferences_need_sign_in()
    {
        var tenant = await database.CreateTenantAsync();
        using var anonymous = _factory.ForTenant(tenant);

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(PreferencesUri, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PutAsJsonAsync(PreferencesUri, new { language = "ar" }, Ct)).StatusCode);
    }

    private async Task SetTenantDefaultsAsync(Data.Tenancy.Tenant tenant, string language, string calendar, string digits)
    {
        using var admin = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.TenantAdmin);
        (await admin.PutAsJsonAsync(SettingsApiTests.LocalizationUri, new { defaultLanguage = language, defaultCalendar = calendar, defaultDigits = digits }, Ct))
            .EnsureSuccessStatusCode();
    }

    private sealed record PreferencesBody(string? Language, string? Calendar, string? Digits, EffectiveBody Effective);

    private sealed record EffectiveBody(string Language, string Calendar, string Digits);

    private sealed record MeWithPreferences(Guid Id, PreferencesBody Preferences);
}
