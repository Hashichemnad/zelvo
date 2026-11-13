using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;

namespace Zelvo.Data.Tenancy;

/// <summary>
/// Sets the PostgreSQL setting <c>zelvo.tenant_id</c> every time EF Core opens a connection.
/// Row-level security policies compare each row's <c>tenant_id</c> with this setting, so the
/// application role only ever sees and writes rows of the current tenant. When no tenant is set
/// the setting is cleared and tenant-owned tables return no rows (fail closed).
/// </summary>
public sealed class TenantConnectionInterceptor(ITenantContext tenantContext) : DbConnectionInterceptor
{
    /// <summary>Name of the PostgreSQL setting read by the row-level security policies.</summary>
    public const string SettingName = "zelvo.tenant_id";

    private const string Sql = "SELECT set_config('" + SettingName + "', @tenant, false)";

    /// <inheritdoc />
    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        using var command = CreateCommand(connection);
        command.ExecuteNonQuery();
    }

    /// <inheritdoc />
    public override async Task ConnectionOpenedAsync(
        DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        await using var command = CreateCommand(connection);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private NpgsqlCommand CreateCommand(DbConnection connection)
    {
        var command = new NpgsqlCommand(Sql, (NpgsqlConnection)connection);
        command.Parameters.AddWithValue("tenant", tenantContext.TenantId?.ToString() ?? string.Empty);
        return command;
    }
}
