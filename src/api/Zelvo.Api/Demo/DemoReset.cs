using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.Options;
using Zelvo.Api.Identity;
using Zelvo.Api.Tenancy;
using Zelvo.Data;
using Zelvo.Data.Auditing;
using Zelvo.Data.Tenancy;
using Zelvo.Search;

namespace Zelvo.Api.Demo;

/// <summary>What one sample organisation looked like after a reset.</summary>
internal sealed record DemoOrganisationReset(string Slug, IReadOnlyDictionary<string, int> RowsDeleted, int KeycloakUsersDeleted, IReadOnlyList<string> Seeders);

/// <summary>
/// "Reset demo": puts every sample organisation back to the clean sample state (product owner
/// decision 2026-11-18). For each sample tenant it deletes the rows of every tenant-owned table
/// except the audit log, which is append-only and keeps growing across demos; deletes Keycloak
/// users that are not demo users and restores the demo users; gives the tenant its built-in roles
/// again; runs every seeder; rebuilds the search index; and records <c>demo.reset</c> in the
/// tenant's audit log. Only one reset runs at a time.
/// </summary>
internal sealed class DemoReset(
    IServiceScopeFactory scopes,
    DemoSeeding seeding,
    IKeycloakAdmin keycloak,
    IOptions<IdentityOptions> identity,
    IConfiguration configuration)
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>Resets the demo; <see langword="null"/> when another reset is running.</summary>
    public async Task<IReadOnlyList<DemoOrganisationReset>?> ResetAsync(string requestedBy, string requestedIn, CancellationToken cancellationToken)
    {
        if (!await Gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        try
        {
            var results = new List<DemoOrganisationReset>();
            foreach (var organisation in DemoCatalog.Organisations)
            {
                results.Add(await ResetOrganisationAsync(organisation, requestedBy, requestedIn, cancellationToken).ConfigureAwait(false));
            }

            return results;
        }
        finally
        {
            Gate.Release();
        }
    }

    private async Task<DemoOrganisationReset> ResetOrganisationAsync(
        DemoOrganisation organisation, string requestedBy, string requestedIn, CancellationToken cancellationToken)
    {
        var tenant = await FindTenantAsync(organisation.Slug, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Sample tenant {organisation.Slug} does not exist; run migrate first.");

        var deleted = new SortedDictionary<string, int>(StringComparer.Ordinal);
        await using (var scope = DemoSeeding.CreateTenantScope(scopes, tenant, "demo-reset", "Reset demo"))
        {
            var db = scope.ServiceProvider.GetRequiredService<ZelvoDbContext>();
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            foreach (var table in TablesToEmpty(db.Model))
            {
                // Table names come from the model, never from input (and are checked); row-level
                // security limits the delete to this tenant, and the condition says so as well.
                var sql = DeleteStatement(table);
                var count = await db.Database.ExecuteSqlRawAsync(sql, [tenant.Id], cancellationToken).ConfigureAwait(false);
                if (count > 0)
                {
                    deleted[table] = count;
                }
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        var realm = identity.Value.RealmFor(tenant.Slug);
        var keep = organisation.Users.Select(organisation.UsernameOf).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var keycloakDeleted = await keycloak.DeleteUsersExceptAsync(realm, keep, cancellationToken).ConfigureAwait(false);

        var appConnection = configuration.GetConnectionString("Postgres")
            ?? throw new InvalidOperationException("ConnectionStrings:Postgres is required to reset the demo.");
        await IdentityProvisioning.EnsureTenantIdentityAsync(appConnection, tenant, cancellationToken).ConfigureAwait(false);
        var seeders = await seeding.SeedPendingAsync(organisation, tenant, cancellationToken).ConfigureAwait(false);

        await using (var scope = DemoSeeding.CreateTenantScope(scopes, tenant, "demo-reset", "Reset demo"))
        {
            var settings = scope.ServiceProvider.GetRequiredService<Settings.TenantSettingsStore>();
            await settings.SetAsync(DemoSettings.LastResetAt, DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);
            scope.ServiceProvider.GetRequiredService<IAuditLog>().Add(AuditCategory.Admin, "demo.reset", "tenant", tenant.Id.ToString(),
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["requestedBy"] = requestedBy,
                    ["requestedIn"] = requestedIn,
                    ["rowsDeleted"] = deleted,
                    ["keycloakUsersDeleted"] = keycloakDeleted,
                    ["seeders"] = seeders,
                });
            await scope.ServiceProvider.GetRequiredService<ZelvoDbContext>().SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await scope.ServiceProvider.GetRequiredService<SearchRebuild>().RequestAsync(cancellationToken).ConfigureAwait(false);
        }

        return new DemoOrganisationReset(organisation.Slug, deleted, keycloakDeleted, seeders);
    }

    internal static string DeleteStatement(string table) =>
        System.Text.RegularExpressions.Regex.IsMatch(table, "^[a-z_][a-z0-9_]*$", System.Text.RegularExpressions.RegexOptions.CultureInvariant)
            ? "DELETE FROM \"" + table + "\" WHERE tenant_id = {0}"
            : throw new InvalidOperationException($"Unexpected table name '{table}'.");

    private async Task<Tenant?> FindTenantAsync(string slug, CancellationToken cancellationToken)
    {
        await using var lookup = scopes.CreateAsyncScope();
        var resolved = await lookup.ServiceProvider.GetRequiredService<TenantResolver>().ResolveAsync(slug, cancellationToken).ConfigureAwait(false);
        if (resolved is null)
        {
            return null;
        }

        lookup.ServiceProvider.GetRequiredService<TenantContext>().SetTenant(resolved.Id, resolved.Slug);
        return await lookup.ServiceProvider.GetRequiredService<ZelvoDbContext>().Tenants.AsNoTracking()
            .SingleOrDefaultAsync(t => t.Id == resolved.Id, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Every tenant-owned table except the audit log, children before the tables they refer to.
    /// Tables later modules add are included automatically (they implement <see cref="ITenantOwned"/>).
    /// </summary>
    internal static IReadOnlyList<string> TablesToEmpty(IModel model)
    {
        var remaining = model.GetEntityTypes()
            .Where(e => typeof(ITenantOwned).IsAssignableFrom(e.ClrType) && e.ClrType != typeof(AuditEvent) && e.GetTableName() is not null)
            .ToList();
        var ordered = new List<string>();
        while (remaining.Count > 0)
        {
            // A table can go once no other remaining table refers to it.
            // A reference that cascades from a table this table itself refers to is removed with it
            // (task C1: a form's versions go with the form that points at its published version).
            var leaves = remaining.Where(e => !remaining.Any(other => other != e
                && other.GetForeignKeys().Any(fk => fk.PrincipalEntityType == e
                    && !(fk.DeleteBehavior == DeleteBehavior.Cascade && e.GetForeignKeys().Any(back => back.PrincipalEntityType == other && back.DeleteBehavior == DeleteBehavior.NoAction))))).ToList();
            if (leaves.Count == 0)
            {
                throw new InvalidOperationException("Tenant-owned tables refer to each other in a cycle; reset cannot order them.");
            }

            foreach (var leaf in leaves.OrderBy(e => e.GetTableName(), StringComparer.Ordinal))
            {
                ordered.Add(leaf.GetTableName()!);
                remaining.Remove(leaf);
            }
        }

        return ordered.Distinct(StringComparer.Ordinal).ToList();
    }
}
