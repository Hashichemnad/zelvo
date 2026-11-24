using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Zelvo.Api.Demo;
using Zelvo.Api.Identity;
using Zelvo.Data.Identity;
using Zelvo.Data.Tenancy;
using Zelvo.Licensing;

namespace Zelvo.Api.Tests;

/// <summary>Demo mode (task D): sample organisations, seeding, demo users and "Reset demo".</summary>
/// <remarks>The sample organisations have fixed slugs, so these tests share them and run one after another (one class).</remarks>
public sealed class DemoModeTests(DatabaseFixture database) : IAsyncDisposable
{
    private static readonly Uri DemoUri = new("/api/v1/demo", UriKind.Relative);
    private static readonly Uri ResetUri = new("/api/v1/demo/reset", UriKind.Relative);

    private readonly ApiFactory _demo = ApiFactory.ForDatabase(database).WithSetting(InstallationProfile.ConfigurationKey, InstallationProfile.Demo);
    private readonly ApiFactory _standard = ApiFactory.ForDatabase(database);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask DisposeAsync()
    {
        await _demo.DisposeAsync();
        await _standard.DisposeAsync();
    }

    [Fact]
    public async Task Installations_that_are_not_demos_have_no_demo_features()
    {
        var gov = await SeededAsync(DemoCatalog.Government);
        using var anonymous = _standard.ForTenant(gov);
        using var admin = _standard.ForTenant(gov).As(await AdminOfAsync(gov));

        var info = await anonymous.GetAsync(DemoUri, Ct);
        var reset = await admin.PostAsync(ResetUri, null, Ct);

        Assert.Equal(HttpStatusCode.NotFound, info.StatusCode);
        Assert.Equal("not-demo", (await info.Content.ReadFromJsonAsync<ProblemBody>(Ct))!.Code);
        Assert.Equal(HttpStatusCode.NotFound, reset.StatusCode);
    }

    [Fact]
    public async Task Tenants_that_are_not_sample_organisations_have_no_demo_features_even_in_a_demo()
    {
        var other = await database.CreateTenantAsync();
        using var anonymous = _demo.ForTenant(other);

        var info = await anonymous.GetAsync(DemoUri, Ct);

        Assert.Equal(HttpStatusCode.NotFound, info.StatusCode);
    }

    [Fact]
    public async Task Seeding_gives_each_sample_organisation_its_name_defaults_and_one_demo_user_per_persona()
    {
        var gov = await SeededAsync(DemoCatalog.Government);
        var company = await SeededAsync(DemoCatalog.Company);
        using var visitor = _demo.ForTenant(gov);

        var demo = await visitor.GetFromJsonAsync<DemoBody>(DemoUri, Ct);

        Assert.Equal("demo-municipality", demo!.Organisation);
        Assert.Equal(["demo-municipality", "demo-company"], demo.Organisations.Select(o => o.Slug));
        Assert.Equal(["Government", "Company"], demo.Organisations.Select(o => o.Kind));
        Assert.Equal("دائرة الخدمات البلدية", demo.Organisations[0].Name.Ar);
        Assert.Equal(DemoCatalog.Personas.Select(p => p.Persona), demo.Users.Select(u => u.Persona));
        Assert.Equal("agent@municipality.example", demo.Users.Single(u => u.Persona == "agent").Username);
        Assert.Equal("موظف الدعم", demo.Users.Single(u => u.Persona == "agent").RoleName.Ar);
        Assert.NotNull(demo.LastResetAt);

        foreach (var (tenant, organisation) in new[] { (gov, DemoCatalog.Government), (company, DemoCatalog.Company) })
        {
            await using var db = TestData.AppContext(database, tenant);
            var users = await db.Users.Include(u => u.Roles).ThenInclude(r => r.Role).ToListAsync(Ct);
            Assert.Equal(6, users.Count);
            foreach (var demoUser in organisation.Users)
            {
                var user = users.Single(u => u.Email == organisation.UsernameOf(demoUser));
                Assert.Equal(demoUser.DisplayName, user.DisplayName);
                Assert.Equal([demoUser.RoleKey], user.Roles.Select(r => r.Role!.Key));
                Assert.Equal(FakeKeycloakAdmin.IdFor($"zelvo-{tenant.Slug}", user.Email!), user.Subject);
            }

            Assert.Equal(organisation.Name.En, (await db.Tenants.SingleAsync(Ct)).Name.En);
            var language = await db.TenantSettings.SingleAsync(s => s.Key == "localization.defaultLanguage", Ct);
            Assert.Contains(organisation.DefaultLanguage, language.Value, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Sample_organisations_get_groups_rules_and_classified_incidents()
    {
        foreach (var organisation in new[] { DemoCatalog.Government, DemoCatalog.Company })
        {
            var tenant = await SeededAsync(organisation);
            await using var db = TestData.AppContext(database, tenant);
            Assert.Equal(3, await db.SupportGroups.CountAsync(Ct));
            Assert.Equal(3, await db.AssignmentRules.CountAsync(Ct));
            Assert.True(await db.Categories.AnyAsync(c => c.Depth == 2, Ct));
            Assert.True(await db.Locations.AnyAsync(c => c.Depth == 3, Ct));
            Assert.All(await db.Tickets.ToListAsync(Ct), t => Assert.True(t.CategoryId is not null && t.LocationId is not null && t.GroupId is not null));
            var agent = await db.Users.Include(u => u.Skills).SingleAsync(u => u.Email == organisation.UsernameOf(organisation.User("agent")!), Ct);
            Assert.Equal(["en", "ar"], agent.Languages);
            Assert.Equal(2, agent.Skills.Count);
        }
    }

    [Fact]
    public async Task Sample_organisations_get_calendars_sla_policies_and_clocks()
    {
        foreach (var organisation in new[] { DemoCatalog.Government, DemoCatalog.Company })
        {
            var tenant = await SeededAsync(organisation);
            await using var db = TestData.AppContext(database, tenant);
            var calendars = await db.BusinessCalendars.Include(c => c.Holidays).ToListAsync(Ct);
            Assert.Equal(2, calendars.Count);
            Assert.Contains(calendars, c => c.IsDefault && c.Holidays.Any(h => h.StartDate == new DateOnly(2026, 12, 2)));
            Assert.Equal(2, await db.SlaPolicies.CountAsync(Ct));
            Assert.Single(await db.CustomerGroupMembers.ToListAsync(Ct));
            Assert.True(await db.SupportGroups.AnyAsync(g => g.CalendarId != null, Ct));
            Assert.Equal(await db.Tickets.CountAsync(Ct) * 2, await db.TicketSlas.CountAsync(Ct));
            // Task B5: in the API, saving the sample incidents queued their notifications once.
            Assert.Equal(await db.Tickets.CountAsync(Ct),
                await db.Notifications.CountAsync(n => n.EventType == Zelvo.Notifications.NotificationEvents.IncidentReceived, Ct));
            Assert.True(await db.TicketSlas.AnyAsync(c => c.TargetMinutes != null && c.State == Data.Sla.SlaClockState.Running, Ct));
            // Task B4: canned responses at every scope, shared and personal views, and conversations with a mention.
            Assert.Equal(3, await db.CannedResponses.Select(r => r.Scope).Distinct().CountAsync(Ct));
            Assert.Equal(2, await db.SavedViews.CountAsync(Ct));
            Assert.True(await db.TicketComments.AnyAsync(c => c.Visibility == Data.Tickets.CommentVisibility.Internal, Ct));
            Assert.True(await db.TicketCommentMentions.AnyAsync(Ct));
        }
    }

    [Fact]
    public async Task Every_seeder_runs_with_the_services_of_the_migrate_command()
    {
        // migrate seeds demo installations without the web host's services (task B3 needed one more).
        var tenant = await database.CreateTenantAsync("seedmig");
        await database.EnsureIdentityAsync(tenant);
        var configuration = _demo.Services.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>();
        var identity = _demo.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<IdentityOptions>>().Value;
        await using var services = Commands.MigrateCommand.DemoServices(configuration, database.AppConnectionString, identity, _demo.Keycloak);

        var ran = await services.GetRequiredService<DemoSeeding>().SeedPendingAsync(DemoCatalog.Government, tenant, Ct);

        Assert.Contains("b3-sla", ran);
        Assert.Contains("b5-notifications", ran);
        Assert.Contains("b4-workspace", ran);
        Assert.Contains("c1-forms", ran);
        await using var db = TestData.AppContext(database, tenant);
        Assert.True(await db.TicketSlas.AnyAsync(Ct));
        // Task B5: migrate has no notification interceptor; the seeder queues one "received" per sample incident.
        Assert.Equal(await db.Tickets.CountAsync(Ct),
            await db.Notifications.CountAsync(n => n.EventType == Zelvo.Notifications.NotificationEvents.IncidentReceived, Ct));

        // Task C1: published forms with a field set, bound to the default and to Laptops, and profile fields.
        Assert.Equal(2, await db.Forms.CountAsync(f => f.State == Data.Forms.FormState.Published, Ct));
        Assert.Equal(2, await db.FormBindings.CountAsync(Ct));
        Assert.True(await db.FieldSets.AnyAsync(s => s.State == Data.Forms.FormState.Published, Ct));
        Assert.True(await db.Users.AnyAsync(u => u.DepartmentId != null && u.ManagerId != null && u.Phone != null, Ct));
        foreach (var form in await db.Forms.ToListAsync(Ct))
        {
            var resolved = await Zelvo.Tickets.Forms.FormResolver.PublishedAsync(db, form.Id, Ct);
            Assert.Empty(Zelvo.Tickets.Forms.FormDefinitionCheck.Check([.. resolved!.PlainFields]));
        }
    }

    [Fact]
    public async Task Seeders_run_once_per_tenant()
    {
        var gov = await SeededAsync(DemoCatalog.Government);

        var again = await _demo.Services.GetRequiredService<DemoSeeding>().SeedPendingAsync(DemoCatalog.Government, gov, Ct);

        Assert.Empty(again);
    }

    [Fact]
    public async Task Reset_restores_the_sample_state_of_every_organisation_and_keeps_the_audit_log()
    {
        var gov = await SeededAsync(DemoCatalog.Government);
        var company = await SeededAsync(DemoCatalog.Company);
        var admin = await DemoUserAsync(gov, "admin");
        using var client = _demo.ForTenant(gov).As(admin);

        // A demo meeting: settings changed, a visitor's account, a demo user given another role.
        (await client.PutAsJsonAsync(SettingsApiTests.LocalizationUri, new { defaultLanguage = "en", defaultCalendar = "Hijri", defaultDigits = "ArabicIndic" }, Ct))
            .EnsureSuccessStatusCode();
        var visitor = await database.CreateUserAsync(company, BuiltInRoles.Requester);
        var agentOfCompany = await DemoUserAsync(company, "agent");
        await IdentityProvisioning.EnsureUserAsync(database.OwnerConnectionString, company, agentOfCompany.Subject, agentOfCompany.Email,
            "Renamed", [BuiltInRoles.TenantAdmin], Ct);
        var auditBefore = await AuditCountAsync(gov);

        var response = await client.PostAsync(ResetUri, null, Ct);
        var body = await response.Content.ReadFromJsonAsync<ResetBody>(Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(["demo-municipality", "demo-company"], body!.Organisations);

        await using (var db = TestData.AppContext(database, gov))
        {
            var settings = await db.TenantSettings.Where(s => s.Key.StartsWith("localization.")).ToDictionaryAsync(s => s.Key, s => s.Value, Ct);
            Assert.Contains("ar", settings["localization.defaultLanguage"], StringComparison.Ordinal);
            Assert.Contains("Gregorian", settings["localization.defaultCalendar"], StringComparison.Ordinal);
            Assert.Equal(6, await db.Users.CountAsync(Ct));
            var actions = await db.AuditEvents.Select(e => e.Action).ToListAsync(Ct);
            Assert.True(actions.Count > auditBefore, "The audit log keeps its earlier entries and records the reset.");
            Assert.Contains("demo.reset", actions);
            Assert.Contains("demo.reset-requested", actions);
        }

        await using (var db = TestData.AppContext(database, company))
        {
            var users = await db.Users.Include(u => u.Roles).ThenInclude(r => r.Role).ToListAsync(Ct);
            Assert.DoesNotContain(users, u => u.Subject == visitor.Subject);
            var agent = users.Single(u => u.Subject == agentOfCompany.Subject);
            Assert.Equal(DemoCatalog.Company.User("agent")!.DisplayName, agent.DisplayName);
            Assert.Equal([BuiltInRoles.Agent], agent.Roles.Select(r => r.Role!.Key));
        }

        Assert.Contains(_demo.Keycloak.DeleteCalls, c => c.Realm == "zelvo-demo-company" && c.Keep.Contains("agent@gulfhorizon.example"));

        // The admin who pressed the button is still signed in: same Keycloak account, new Zelvo record.
        var me = await client.GetAsync(new Uri("/api/v1/me", UriKind.Relative), Ct);
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);

        var chain = await client.GetFromJsonAsync<VerificationBody>(new Uri("/api/v1/audit/verification", UriKind.Relative), Ct);
        Assert.True(chain!.Valid);
    }

    [Theory]
    [InlineData("agent", true)]
    [InlineData("requester", true)]
    [InlineData("admin", false)]
    public async Task Only_demo_administrators_with_a_second_factor_may_reset(string persona, bool mfa)
    {
        var gov = await SeededAsync(DemoCatalog.Government);
        using var client = _demo.ForTenant(gov).As(await DemoUserAsync(gov, persona), mfa);

        var response = await client.PostAsync(ResetUri, null, Ct);

        Assert.True(response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized, $"{persona}: {response.StatusCode}");
    }

    [Fact]
    public async Task Reset_covers_every_tenant_owned_table_except_the_audit_log()
    {
        await using var db = TestData.AppContext(database, null);
        var tables = DemoReset.TablesToEmpty(db.Model);

        await using var connection = new NpgsqlConnection(database.OwnerConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(
            "SELECT table_name FROM information_schema.columns WHERE table_schema = 'public' AND column_name = 'tenant_id'", connection);
        var withTenant = new List<string>();
        await using (var reader = await command.ExecuteReaderAsync(Ct))
        {
            while (await reader.ReadAsync(Ct))
            {
                withTenant.Add(reader.GetString(0));
            }
        }

        Assert.DoesNotContain("audit_events", tables);
        Assert.Equal(withTenant.Where(t => t != "audit_events").Order(StringComparer.Ordinal), tables.Order(StringComparer.Ordinal));
        // Children before parents: role grants go before roles, user roles before users.
        Assert.True(tables.ToList().IndexOf("user_roles") < tables.ToList().IndexOf("users"));
        Assert.True(tables.ToList().IndexOf("user_roles") < tables.ToList().IndexOf("roles"));
    }

    private async Task<Tenant> SeededAsync(DemoOrganisation organisation)
    {
        var (tenant, _) = await TenantProvisioning.EnsureTenantAsync(database.OwnerConnectionString, organisation.Slug, organisation.Name, Ct);
        await database.EnsureIdentityAsync(tenant);
        await _demo.Services.GetRequiredService<DemoSeeding>().SeedPendingAsync(organisation, tenant, Ct);
        return tenant;
    }

    private async Task<TestUser> DemoUserAsync(Tenant tenant, string persona)
    {
        var organisation = DemoCatalog.Find(tenant.Slug)!;
        var username = organisation.UsernameOf(organisation.User(persona)!);
        await using var db = TestData.AppContext(database, tenant);
        var user = await db.Users.SingleAsync(u => u.Email == username, Ct);
        return new TestUser(tenant, user.Subject, user.Id, username);
    }

    private Task<TestUser> AdminOfAsync(Tenant tenant) => DemoUserAsync(tenant, "admin");

    private async Task<int> AuditCountAsync(Tenant tenant)
    {
        await using var db = TestData.AppContext(database, tenant);
        return await db.AuditEvents.CountAsync(Ct);
    }

    private sealed record LocalizedBody(string? En, string? Ar);

    private sealed record OrganisationBody(string Slug, string Kind, LocalizedBody Name);

    private sealed record UserBody(string Persona, string RoleKey, LocalizedBody RoleName, string DisplayName, string Username);

    private sealed record DemoBody(string Organisation, List<OrganisationBody> Organisations, List<UserBody> Users, DateTimeOffset? LastResetAt);

    private sealed record ResetBody(DateTimeOffset ResetAt, List<string> Organisations);

    private sealed record VerificationBody(bool Valid);
}
