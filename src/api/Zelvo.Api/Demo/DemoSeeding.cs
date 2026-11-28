using Microsoft.Extensions.Options;
using Zelvo.Api.Identity;
using Zelvo.Api.Settings;
using Zelvo.Data;
using Zelvo.Data.Auditing;
using Zelvo.Data.Tenancy;

namespace Zelvo.Api.Demo;

/// <summary>
/// One step of the demo sample data, for example the demo users or (later) the service catalog.
/// Each module adds its own seeder and registers it with <c>AddDemoSeeder&lt;T&gt;()</c>.
/// </summary>
/// <remarks>
/// A seeder runs once per sample organisation: at the first start of a demo installation, at the
/// first start after a release adds it, and after every "Reset demo". It runs in a scope whose
/// tenant is the organisation's (row-level security applies), and writes are audited as the
/// system actor <c>demo-seed</c>. It must be safe to run on a tenant whose earlier seeders ran.
/// </remarks>
internal interface IDemoSeeder
{
    /// <summary>Stable key, recorded per tenant once the seeder ran, for example <c>d-users</c>.</summary>
    string Key { get; }

    /// <summary>Seeders run in ascending order; later modules use higher numbers (B1 from 100).</summary>
    int Order { get; }

    Task SeedAsync(DemoSeedContext context, CancellationToken cancellationToken);
}

/// <summary>What a seeder works with.</summary>
/// <param name="Organisation">The sample organisation being seeded.</param>
/// <param name="Tenant">Its tenant.</param>
/// <param name="Services">The tenant's scope: <see cref="ZelvoDbContext"/>, <see cref="TenantSettingsStore"/>, <see cref="IKeycloakAdmin"/> and so on.</param>
internal sealed record DemoSeedContext(DemoOrganisation Organisation, Tenant Tenant, IServiceProvider Services)
{
    public T Get<T>() where T : notnull => Services.GetRequiredService<T>();

    /// <summary>The organisation's Keycloak realm.</summary>
    public string Realm => Get<IOptions<IdentityOptions>>().Value.RealmFor(Tenant.Slug);
}

/// <summary>Demo bookkeeping in the tenant settings store.</summary>
internal static class DemoSettings
{
    /// <summary>Keys of the seeders that ran for this tenant since the last reset.</summary>
    public static SettingDefinition<string[]> AppliedSeeders { get; } = new("demo.seeders", [], Permissions.DemoReset);

    /// <summary>When the demo was last reset (or first seeded).</summary>
    public static SettingDefinition<DateTimeOffset> LastResetAt { get; } = new("demo.lastResetAt", DateTimeOffset.MinValue, Permissions.DemoReset);
}

/// <summary>How far seeding of one organisation has got.</summary>
/// <param name="Seeder">The seeder starting now; <see langword="null"/> once all ran.</param>
/// <param name="Done">Seeders finished.</param>
/// <param name="Total">Seeders to run.</param>
internal sealed record DemoSeedProgress(string? Seeder, int Done, int Total);

/// <summary>Runs the demo seeders for sample organisations.</summary>
internal sealed class DemoSeeding(IServiceScopeFactory scopes, IEnumerable<IDemoSeeder> seeders)
{
    /// <summary>The registered seeders in run order.</summary>
    public IReadOnlyList<IDemoSeeder> Seeders { get; } = seeders.OrderBy(s => s.Order).ThenBy(s => s.Key, StringComparer.Ordinal).ToList();

    /// <summary>
    /// Runs the seeders that have not run for this tenant yet (all of them after a reset) and
    /// returns their keys.
    /// </summary>
    public async Task<IReadOnlyList<string>> SeedPendingAsync(
        DemoOrganisation organisation, Tenant tenant, CancellationToken cancellationToken, IProgress<DemoSeedProgress>? progress = null)
    {
        string[] applied;
        await using (var lookup = CreateTenantScope(scopes, tenant, "demo-seed", "Demo sample data"))
        {
            applied = await lookup.ServiceProvider.GetRequiredService<TenantSettingsStore>()
                .GetAsync(DemoSettings.AppliedSeeders, cancellationToken).ConfigureAwait(false);
        }

        var pending = Seeders.Where(s => !applied.Contains(s.Key, StringComparer.Ordinal)).ToList();
        var done = applied.ToList();
        var ran = new List<string>();
        foreach (var seeder in pending)
        {
            progress?.Report(new DemoSeedProgress(seeder.Key, ran.Count, pending.Count));

            // Each seeder gets its own scope and so its own database context: one shared context
            // kept every earlier seeder's rows in its change tracker, and every save (with the
            // audit, notification, webhook and other interceptors each looking at all tracked
            // entries) grew slower with each seeder until a reset took minutes.
            await using var scope = CreateTenantScope(scopes, tenant, "demo-seed", "Demo sample data");
            var settings = scope.ServiceProvider.GetRequiredService<TenantSettingsStore>();
            var db = scope.ServiceProvider.GetRequiredService<ZelvoDbContext>();
            await seeder.SeedAsync(new DemoSeedContext(organisation, tenant, scope.ServiceProvider), cancellationToken).ConfigureAwait(false);
            done.Add(seeder.Key);
            await settings.SetAsync(DemoSettings.AppliedSeeders, [.. done], cancellationToken).ConfigureAwait(false);
            if (await settings.GetAsync(DemoSettings.LastResetAt, cancellationToken).ConfigureAwait(false) == DateTimeOffset.MinValue)
            {
                await settings.SetAsync(DemoSettings.LastResetAt, DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);
            }

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            ran.Add(seeder.Key);
        }

        progress?.Report(new DemoSeedProgress(null, ran.Count, pending.Count));
        return ran;
    }

    /// <summary>A scope that acts for <paramref name="tenant"/> as a system actor.</summary>
    internal static AsyncServiceScope CreateTenantScope(IServiceScopeFactory scopes, Tenant tenant, string actorId, string actorName)
    {
        var scope = scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().SetTenant(tenant.Id, tenant.Slug);
        scope.ServiceProvider.GetRequiredService<AuditContext>().SetActor(AuditActorType.System, actorId, actorName);
        return scope;
    }
}
