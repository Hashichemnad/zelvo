using Microsoft.EntityFrameworkCore.Migrations;

namespace Zelvo.Data.Migrations;

/// <summary>
/// Row-level security for tenant-owned tables. Every migration that creates a table with a
/// <c>tenant_id</c> column calls <see cref="EnableTenantIsolation"/> for it; a test checks that
/// no such table is left without it.
/// </summary>
public static class TenantIsolationMigrationExtensions
{
    /// <summary>
    /// Enables and forces row-level security on <paramref name="table"/> and adds the policy that
    /// limits reads and writes to rows whose <c>tenant_id</c> is the connection's current tenant.
    /// </summary>
    public static void EnableTenantIsolation(this MigrationBuilder migrationBuilder, string table)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        var name = QuoteIdentifier(table);
        migrationBuilder.Sql($"""
            ALTER TABLE {name} ENABLE ROW LEVEL SECURITY;
            ALTER TABLE {name} FORCE ROW LEVEL SECURITY;
            CREATE POLICY tenant_isolation ON {name}
                USING (tenant_id = zelvo_current_tenant_id())
                WITH CHECK (tenant_id = zelvo_current_tenant_id());
            """);
    }

    /// <summary>Removes what <see cref="EnableTenantIsolation"/> added.</summary>
    public static void DisableTenantIsolation(this MigrationBuilder migrationBuilder, string table)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        var name = QuoteIdentifier(table);
        migrationBuilder.Sql($"""
            DROP POLICY IF EXISTS tenant_isolation ON {name};
            ALTER TABLE {name} NO FORCE ROW LEVEL SECURITY;
            ALTER TABLE {name} DISABLE ROW LEVEL SECURITY;
            """);
    }

    private static string QuoteIdentifier(string identifier)
    {
        if (string.IsNullOrEmpty(identifier) || !identifier.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_'))
        {
            throw new ArgumentException("Table names are lower-case letters, digits and underscores.", nameof(identifier));
        }

        return $"\"{identifier}\"";
    }
}
