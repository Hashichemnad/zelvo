using Npgsql;
using Testcontainers.PostgreSql;
using Zelvo.Api.Identity;
using Zelvo.Data;
using Zelvo.Data.Localization;
using Zelvo.Data.Tenancy;

[assembly: AssemblyFixture(typeof(Zelvo.Api.Tests.DatabaseFixture))]

namespace Zelvo.Api.Tests;

/// <summary>
/// One real PostgreSQL (with pgvector) for the test run, set up like the Compose database: a
/// superuser that runs migrations and an application role without BYPASSRLS. Tests isolate
/// themselves by creating their own tenants.
/// </summary>
public sealed class DatabaseFixture : IAsyncLifetime
{
    private const string AppRole = "zelvo_app";
    private const string AppPassword = "test-only-app-password";

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("pgvector/pgvector:pg17").Build();

    /// <summary>Superuser connection: migrations, provisioning and tamper simulations.</summary>
    public string OwnerConnectionString { get; private set; } = string.Empty;

    /// <summary>Application role connection: row-level security applies.</summary>
    public string AppConnectionString { get; private set; } = string.Empty;

    public async ValueTask InitializeAsync()
    {
        await _postgres.StartAsync();
        OwnerConnectionString = _postgres.GetConnectionString();
        var builder = new NpgsqlConnectionStringBuilder(OwnerConnectionString) { Username = AppRole, Password = AppPassword };
        AppConnectionString = builder.ConnectionString;

        // Same grants as deploy/compose/postgres/init/01-roles-and-databases.sh.
        await ExecuteAsOwnerAsync($"""
            CREATE ROLE {AppRole} LOGIN PASSWORD '{AppPassword}' NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS;
            GRANT USAGE ON SCHEMA public TO {AppRole};
            ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO {AppRole};
            ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT USAGE, SELECT ON SEQUENCES TO {AppRole};
            """);

        await DatabaseMigrator.MigrateAsync(OwnerConnectionString, TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync() => await _postgres.DisposeAsync();

    /// <summary>Creates a tenant with a unique slug. Its roles are added by <see cref="EnsureIdentityAsync"/>.</summary>
    public async Task<Tenant> CreateTenantAsync(string prefix = "t", string? nameEn = "Test tenant", string? nameAr = "مستأجر تجريبي")
    {
        var slug = $"{prefix}-{Guid.NewGuid():N}"[..Math.Min(40, prefix.Length + 33)];
        var (tenant, _) = await TenantProvisioning.EnsureTenantAsync(
            OwnerConnectionString, slug, new LocalizedText(nameEn, nameAr), TestContext.Current.CancellationToken);
        return tenant;
    }

    /// <summary>Gives a tenant its built-in roles and sign-in settings, as <c>migrate</c> does.</summary>
    public Task EnsureIdentityAsync(Tenant tenant) =>
        IdentityProvisioning.EnsureTenantIdentityAsync(OwnerConnectionString, tenant, TestContext.Current.CancellationToken);

    public async Task<int> ExecuteAsOwnerAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(OwnerConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>Opens an application-role connection with <c>zelvo.tenant_id</c> set (or not).</summary>
    public async Task<NpgsqlConnection> OpenAppConnectionAsync(Guid? tenantId)
    {
        var connection = new NpgsqlConnection(AppConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        if (tenantId is { } id)
        {
            await using var command = new NpgsqlCommand("SELECT set_config('zelvo.tenant_id', @t, false)", connection);
            command.Parameters.AddWithValue("t", id.ToString());
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        return connection;
    }
}
