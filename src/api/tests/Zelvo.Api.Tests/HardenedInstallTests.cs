using System.Net;
using Microsoft.EntityFrameworkCore;
using Zelvo.Api.Identity;
using Zelvo.Data.Identity;

namespace Zelvo.Api.Tests;

/// <summary>
/// Task M2: what a hardened installation needs from <c>migrate</c>: the first tenant administrator
/// with a one-time setup link, and further tenants served on their own hosts.
/// </summary>
public sealed class HardenedInstallTests(DatabaseFixture database) : IAsyncDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private ApiFactory Factory => _factoryLazy ??= ApiFactory.ForDatabase(database).WithMasterKey();

    private ApiFactory? _factoryLazy;

    public ValueTask DisposeAsync() => _factoryLazy?.DisposeAsync() ?? ValueTask.CompletedTask;

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    // A password made at run time (never a literal in tests).
    private static string NewPassword() => $"Pw-{Guid.NewGuid():N}";

    private static Func<CancellationToken, Task<string>> KeycloakUser(string subject) => _ => Task.FromResult(subject);

    [Fact]
    public void Emails_are_checked_and_tokens_are_random_and_hashed()
    {
        Assert.Equal("it.admin@entity.gov.ae", FirstAdministrator.NormalizeEmail(" IT.Admin@Entity.gov.ae "));
        Assert.Null(FirstAdministrator.NormalizeEmail("not an email"));
        Assert.Null(FirstAdministrator.NormalizeEmail("admin@localhost"));
        var token = FirstAdministrator.NewToken();
        Assert.Equal(43, token.Length);
        Assert.NotEqual(token, FirstAdministrator.NewToken());
        Assert.Equal(64, FirstAdministrator.Hash(token).Length);
        Assert.DoesNotContain(token, FirstAdministrator.Hash(token), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_first_administrator_gets_a_one_time_link_until_they_use_it()
    {
        var tenant = await database.CreateTenantAsync("m2adm");
        await IdentityProvisioning.EnsureTenantIdentityAsync(database.OwnerConnectionString, tenant, Ct);
        var email = $"admin-{Guid.NewGuid():N}@entity.gov.ae";
        var subject = Guid.NewGuid().ToString();
        var time = new FixedTime(DateTimeOffset.UtcNow);

        var first = await FirstAdministrator.EnsureAsync(database.OwnerConnectionString, tenant, email, KeycloakUser(subject), time, Ct);
        Assert.NotNull(first.Token);
        // migrate runs again before the link is used: a new link, the old one stops working.
        var second = await FirstAdministrator.EnsureAsync(database.OwnerConnectionString, tenant, email, KeycloakUser(subject), time, Ct);
        Assert.NotNull(second.Token);
        Assert.NotEqual(first.Token, second.Token);

        await using (var db = TestData.AppContext(database, tenant))
        {
            var user = await db.Users.Include(u => u.Roles).ThenInclude(r => r.Role).SingleAsync(u => u.Subject == subject, Ct);
            Assert.Contains(user.Roles, r => r.Role!.Key == BuiltInRoles.TenantAdmin);
            Assert.Equal(2, await db.AccountSetupLinks.CountAsync(l => l.UserId == user.Id, Ct));
            Assert.DoesNotContain(await db.AccountSetupLinks.Select(l => l.TokenHash).ToListAsync(Ct), h => h == second.Token);
        }

        using var visitor = Factory.ForTenant(tenant);
        Assert.Equal("setup-link-invalid", (await (await visitor.SendAsync(HttpMethod.Post, "/api/v1/identity/account-setup/check", new { token = first.Token })).ProblemAsync()).Code);
        var check = await visitor.SendOkAsync(HttpMethod.Post, "/api/v1/identity/account-setup/check", new { token = second.Token });
        Assert.Equal(email, (string?)check["email"]);

        // Keycloak's password policy decides.
        var refused = await visitor.SendAsync(HttpMethod.Post, "/api/v1/identity/account-setup", new { token = second.Token, password = "short" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        var problem = (await refused.JsonAsync()).AsObject();
        Assert.Equal("password-refused", (string?)problem["code"]);
        Assert.Equal("invalidPasswordMinLengthMessage", (string?)problem["policy"]);

        var password = NewPassword();
        Assert.Equal(HttpStatusCode.NoContent, (await visitor.SendAsync(HttpMethod.Post, "/api/v1/identity/account-setup", new { token = second.Token, password })).StatusCode);
        Assert.Contains(Factory.Keycloak.PasswordCalls, c => c.Realm == $"zelvo-{tenant.Slug}" && c.Subject == subject && c.Password == password);
        Assert.Contains(await database.AuditAsync(tenant), a => a.Action == "identity.account-setup.completed");

        // Used once only, and migrate makes no new link once the tenant has its administrator.
        Assert.Equal(HttpStatusCode.NotFound, (await visitor.SendAsync(HttpMethod.Post, "/api/v1/identity/account-setup", new { token = second.Token, password = NewPassword() })).StatusCode);
        var third = await FirstAdministrator.EnsureAsync(database.OwnerConnectionString, tenant, email, KeycloakUser(subject), time, Ct);
        Assert.Null(third.Token);
    }

    [Fact]
    public async Task Links_expire_and_tenants_with_an_administrator_get_none()
    {
        var tenant = await database.CreateTenantAsync("m2exp");
        await IdentityProvisioning.EnsureTenantIdentityAsync(database.OwnerConnectionString, tenant, Ct);
        var time = new FixedTime(DateTimeOffset.UtcNow.AddDays(-4));
        var issued = await FirstAdministrator.EnsureAsync(database.OwnerConnectionString, tenant, $"a-{Guid.NewGuid():N}@entity.gov.ae",
            KeycloakUser(Guid.NewGuid().ToString()), time, Ct);
        using var visitor = Factory.ForTenant(tenant);
        // Made four days ago: past its 72 hours.
        Assert.Equal(HttpStatusCode.NotFound, (await visitor.SendAsync(HttpMethod.Post, "/api/v1/identity/account-setup", new { token = issued.Token, password = NewPassword() })).StatusCode);

        // Another tenant whose administrator exists already.
        var other = await database.CreateTenantAsync("m2has");
        await database.CreateUserAsync(other, BuiltInRoles.TenantAdmin);
        var none = await FirstAdministrator.EnsureAsync(database.OwnerConnectionString, other, $"b-{Guid.NewGuid():N}@entity.gov.ae",
            _ => throw new InvalidOperationException("Keycloak must not be asked"), TimeProvider.System, Ct);
        Assert.Null(none.Token);

        // A mistyped address, corrected before anyone used the link: the earlier person loses the
        // administrator role and is disabled, and the corrected one gets the link.
        var corrected = await FirstAdministrator.EnsureAsync(database.OwnerConnectionString, tenant, $"a2-{Guid.NewGuid():N}@entity.gov.ae",
            KeycloakUser(Guid.NewGuid().ToString()), TimeProvider.System, Ct);
        Assert.NotNull(corrected.Token);
        await using var db = TestData.AppContext(database, tenant);
        var admins = await db.Users.Where(u => u.Roles.Any(r => r.Role!.Key == BuiltInRoles.TenantAdmin)).ToListAsync(Ct);
        Assert.Single(admins);
        Assert.Equal(1, await db.Users.CountAsync(u => u.Status == UserStatus.Disabled, Ct));
    }

    [Fact]
    public async Task A_further_tenant_is_served_on_its_own_host()
    {
        var tenant = await database.CreateTenantAsync("m2host");
        var host = $"support-{Guid.NewGuid():N}"[..20] + ".entity.gov.ae";
        Assert.True(await FirstAdministrator.EnsureHostAsync(database.OwnerConnectionString, tenant, host, TimeProvider.System, Ct));
        Assert.False(await FirstAdministrator.EnsureHostAsync(database.OwnerConnectionString, tenant, host, TimeProvider.System, Ct));

        var other = await database.CreateTenantAsync("m2oth");
        using var anyone = Factory.ForTenant(other);
        var answer = await anyone.SendOkAsync(HttpMethod.Get, $"/api/v1/platform/hosts/{host}");
        Assert.Equal(tenant.Slug, (string?)answer["tenant"]);

        // A host belongs to one tenant of the installation.
        await Assert.ThrowsAsync<DbUpdateException>(() => FirstAdministrator.EnsureHostAsync(database.OwnerConnectionString, other, host, TimeProvider.System, Ct));
    }
}
