using Microsoft.EntityFrameworkCore;
using Zelvo.Api.Identity;
using Zelvo.Api.Settings;
using Zelvo.Data;
using Zelvo.Data.Identity;

namespace Zelvo.Api.Demo;

/// <summary>Task D: the organisation's name and its language, calendar and digit defaults (LANG-1, LANG-8).</summary>
internal sealed class DemoOrganisationSeeder : IDemoSeeder
{
    public string Key => "d-organisation";

    public int Order => 0;

    public async Task SeedAsync(DemoSeedContext context, CancellationToken cancellationToken)
    {
        var db = context.Get<ZelvoDbContext>();
        var tenant = await db.Tenants.SingleAsync(cancellationToken).ConfigureAwait(false);
        if (tenant.Name.En != context.Organisation.Name.En || tenant.Name.Ar != context.Organisation.Name.Ar)
        {
            tenant.Name = context.Organisation.Name;
            tenant.UpdatedAt = DateTimeOffset.UtcNow;
        }

        var settings = context.Get<TenantSettingsStore>();
        await settings.SetAsync(LocalizationSettings.DefaultLanguage, context.Organisation.DefaultLanguage, cancellationToken).ConfigureAwait(false);
        await settings.SetAsync(LocalizationSettings.DefaultCalendar, context.Organisation.DefaultCalendar, cancellationToken).ConfigureAwait(false);
        await settings.SetAsync(LocalizationSettings.DefaultDigits, context.Organisation.DefaultDigits, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// Task D: one demo user per persona, in Keycloak (no password: they sign in through the demo
/// sign-in step) and in Zelvo with exactly their persona's role.
/// </summary>
internal sealed class DemoUsersSeeder : IDemoSeeder
{
    public string Key => "d-users";

    public int Order => 10;

    public async Task SeedAsync(DemoSeedContext context, CancellationToken cancellationToken)
    {
        var db = context.Get<ZelvoDbContext>();
        var keycloak = context.Get<IKeycloakAdmin>();
        var roles = await db.Roles.ToDictionaryAsync(r => r.Key, StringComparer.Ordinal, cancellationToken).ConfigureAwait(false);

        foreach (var demoUser in context.Organisation.Users)
        {
            var username = context.Organisation.UsernameOf(demoUser);
            var subject = await keycloak.EnsureDemoUserAsync(context.Realm, username, demoUser.FirstName, demoUser.LastName, cancellationToken)
                .ConfigureAwait(false);
            var user = await db.Users.Include(u => u.Roles).SingleOrDefaultAsync(u => u.Subject == subject, cancellationToken).ConfigureAwait(false);
            if (user is null)
            {
                user = new UserAccount { Subject = subject };
                db.Users.Add(user);
            }

            user.Email = username;
            user.DisplayName = demoUser.DisplayName;
            user.Status = UserStatus.Active;
            user.EmiratesId = null;
            user.PreferredLanguage = null;
            user.Calendar = null;
            user.Digits = null;
            user.UpdatedAt = DateTimeOffset.UtcNow;

            var role = roles.GetValueOrDefault(demoUser.RoleKey)
                ?? throw new InvalidOperationException($"Role {demoUser.RoleKey} is missing in tenant {context.Tenant.Slug}.");
            user.Roles.RemoveAll(r => r.RoleId != role.Id);
            if (user.Roles.Count == 0)
            {
                user.Roles.Add(new UserRole { UserId = user.Id, RoleId = role.Id });
            }
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
