using Microsoft.EntityFrameworkCore;
using Npgsql;
using Zelvo.Data.Auditing;

namespace Zelvo.Api.Tests;

/// <summary>
/// Tenant isolation is enforced by PostgreSQL row-level security for the application role, so
/// these tests use that role directly, below the application code.
/// </summary>
public sealed class TenantIsolationTests(DatabaseFixture database)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_tenant_sees_only_its_own_tenant_row_and_audit_entries()
    {
        var a = await database.CreateTenantAsync("a");
        var b = await database.CreateTenantAsync("b");

        await using var db = TestData.AppContext(database, a);

        var tenants = await db.Tenants.Select(t => t.Id).ToListAsync(Ct);
        var auditTenants = await db.AuditEvents.Select(e => e.TenantId).Distinct().ToListAsync(Ct);
        var bById = await db.Tenants.Where(t => t.Id == b.Id).CountAsync(Ct);
        var bAudit = await db.AuditEvents.Where(e => e.TenantId == b.Id).CountAsync(Ct);

        Assert.Equal([a.Id], tenants);
        Assert.Equal([a.Id], auditTenants);
        Assert.Equal(0, bById);
        Assert.Equal(0, bAudit);
    }

    [Fact]
    public async Task Without_a_tenant_nothing_is_visible()
    {
        await database.CreateTenantAsync();

        await using var db = TestData.AppContext(database, tenant: null);

        Assert.Equal(0, await db.Tenants.CountAsync(Ct));
        Assert.Equal(0, await db.AuditEvents.CountAsync(Ct));
    }

    [Fact]
    public async Task Raw_sql_cannot_cross_tenants_either()
    {
        var a = await database.CreateTenantAsync("a");
        var b = await database.CreateTenantAsync("b");
        await using var connection = await database.OpenAppConnectionAsync(a.Id);

        await using var read = new NpgsqlCommand("SELECT count(*) FROM tenants WHERE id = @b", connection);
        read.Parameters.AddWithValue("b", b.Id);
        Assert.Equal(0L, await read.ExecuteScalarAsync(Ct));

        await using var update = new NpgsqlCommand("UPDATE tenants SET name_en = 'taken over' WHERE id = @b", connection);
        update.Parameters.AddWithValue("b", b.Id);
        Assert.Equal(0, await update.ExecuteNonQueryAsync(Ct));

        await using var insert = new NpgsqlCommand(
            "INSERT INTO audit_events (tenant_id, category, action, actor_type) VALUES (@b, 'Admin', 'forged', 'System')", connection);
        insert.Parameters.AddWithValue("b", b.Id);
        var error = await Assert.ThrowsAsync<PostgresException>(() => insert.ExecuteNonQueryAsync(Ct));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, error.SqlState);
    }

    [Fact]
    public async Task The_application_role_cannot_create_or_delete_tenants()
    {
        var a = await database.CreateTenantAsync("a");
        await using var connection = await database.OpenAppConnectionAsync(a.Id);

        await using var insert = new NpgsqlCommand(
            "INSERT INTO tenants (id, slug, status, created_at, updated_at) VALUES (gen_random_uuid(), 'rogue', 'Active', now(), now())", connection);
        var error = await Assert.ThrowsAsync<PostgresException>(() => insert.ExecuteNonQueryAsync(Ct));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, error.SqlState);

        await using var delete = new NpgsqlCommand("DELETE FROM tenants WHERE id = @a", connection);
        delete.Parameters.AddWithValue("a", a.Id);
        Assert.Equal(0, await delete.ExecuteNonQueryAsync(Ct));
    }

    [Fact]
    public async Task The_tenant_setting_does_not_leak_through_the_connection_pool()
    {
        var a = await database.CreateTenantAsync("a");
        await using (var first = TestData.AppContext(database, a))
        {
            Assert.Equal(1, await first.Tenants.CountAsync(Ct));
        }

        // Same pool; the next context has no tenant and must see nothing.
        for (var i = 0; i < 5; i++)
        {
            await using var next = TestData.AppContext(database, tenant: null);
            Assert.Equal(0, await next.Tenants.CountAsync(Ct));
        }
    }

    [Fact]
    public async Task Saving_an_audit_entry_for_another_tenant_fails()
    {
        var a = await database.CreateTenantAsync("a");
        var b = await database.CreateTenantAsync("b");
        await using var db = TestData.AppContext(database, a);
        db.AuditEvents.Add(new AuditEvent { TenantId = b.Id, Category = AuditCategory.Admin, Action = "forged", ActorType = AuditActorType.System });

        await Assert.ThrowsAnyAsync<Exception>(() => db.SaveChangesAsync(Ct));
    }

    [Fact]
    public async Task The_application_role_cannot_bypass_row_level_security()
    {
        await using var connection = await database.OpenAppConnectionAsync(null);
        await using var command = new NpgsqlCommand(
            "SELECT rolsuper OR rolbypassrls FROM pg_roles WHERE rolname = current_user", connection);

        Assert.Equal(false, await command.ExecuteScalarAsync(Ct));
    }

    /// <summary>
    /// Guard for every later module: a table with a tenant_id column must have row-level security
    /// enabled and forced, with at least one policy.
    /// </summary>
    [Fact]
    public async Task Every_tenant_owned_table_has_forced_row_level_security_and_a_policy()
    {
        await using var connection = new NpgsqlConnection(database.OwnerConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand("""
            SELECT c.relname, c.relrowsecurity, c.relforcerowsecurity,
                   (SELECT count(*) FROM pg_policy p WHERE p.polrelid = c.oid) AS policies
            FROM pg_class c
            JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = 'public' AND c.relkind = 'r'
              AND EXISTS (SELECT 1 FROM pg_attribute a WHERE a.attrelid = c.oid AND a.attname = 'tenant_id' AND NOT a.attisdropped)
            ORDER BY c.relname
            """, connection);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        var tables = new List<string>();
        while (await reader.ReadAsync(Ct))
        {
            var table = reader.GetString(0);
            tables.Add(table);
            Assert.True(reader.GetBoolean(1), $"{table}: row-level security is not enabled.");
            Assert.True(reader.GetBoolean(2), $"{table}: row-level security is not forced.");
            Assert.True(reader.GetInt64(3) > 0, $"{table}: no row-level security policy.");
        }

        Assert.Contains("audit_events", tables);
        Assert.Contains("users", tables);
        Assert.Contains("role_permissions", tables);
        Assert.Contains("identity_settings", tables);
    }
}
