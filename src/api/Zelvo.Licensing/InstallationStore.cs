using Microsoft.Extensions.Configuration;
using Npgsql;

namespace Zelvo.Licensing;

/// <summary>A license or upgrade-license file kept by the installation.</summary>
internal sealed record StoredLicenseFile(Guid Id, string FileType, string LicenseId, byte[] Content, DateTimeOffset ImportedAt, string? FileName);

/// <summary>
/// Where the installation keeps its identity and license files: the installation-wide tables
/// <c>installation</c> and <c>installation_licenses</c> (not tenant data, so no row-level security),
/// made by <c>migrate</c>. Every service reads them with its own database connection.
/// </summary>
internal interface IInstallationStore
{
    /// <summary>The installation id and PostgreSQL's system identifier.</summary>
    Task<(string? InstallationId, string? DatabaseId)> GetIdentityAsync(CancellationToken cancellationToken);

    /// <summary>License files that apply (not replaced), oldest first.</summary>
    Task<IReadOnlyList<StoredLicenseFile>> ListAsync(CancellationToken cancellationToken);

    /// <summary>Keeps a checked file; a new license replaces the license and the upgrades before it.</summary>
    Task AddAsync(string fileType, string licenseId, byte[] content, string? watermark, LicenseImportContext context, CancellationToken cancellationToken);
}

/// <inheritdoc />
internal sealed class PostgresInstallationStore(IConfiguration configuration) : IInstallationStore
{
    private string ConnectionString =>
        configuration.GetConnectionString("Postgres") is { Length: > 0 } app ? app
        : configuration.GetConnectionString("PostgresOwner") ?? throw new InvalidOperationException("Configuration value 'ConnectionStrings:Postgres' is required.");

    public async Task<(string? InstallationId, string? DatabaseId)> GetIdentityAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand("SELECT id::text, zelvo_database_identifier() FROM installation", connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? (reader.IsDBNull(0) ? null : reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1))
            : (null, null);
    }

    public async Task<IReadOnlyList<StoredLicenseFile>> ListAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand("""
            SELECT id, file_type, license_id, content, imported_at, file_name
            FROM installation_licenses WHERE superseded_at IS NULL ORDER BY imported_at, id
            """, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var files = new List<StoredLicenseFile>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            files.Add(new StoredLicenseFile(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), (byte[])reader[3],
                reader.GetFieldValue<DateTimeOffset>(4), reader.IsDBNull(5) ? null : reader.GetString(5)));
        }

        return files;
    }

    public async Task AddAsync(string fileType, string licenseId, byte[] content, string? watermark, LicenseImportContext context, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        if (fileType == "License")
        {
            await using var replace = new NpgsqlCommand("UPDATE installation_licenses SET superseded_at = now() WHERE superseded_at IS NULL", connection, transaction);
            await replace.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await using var mark = new NpgsqlCommand("UPDATE installation SET watermark = @w", connection, transaction);
            mark.Parameters.AddWithValue("w", (object?)watermark ?? DBNull.Value);
            await mark.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using var insert = new NpgsqlCommand("""
            INSERT INTO installation_licenses (id, file_type, license_id, content, imported_at, imported_by_user_id, imported_by_tenant_id, file_name)
            VALUES (@id, @type, @license, @content, now(), @user, @tenant, @name)
            """, connection, transaction);
        insert.Parameters.AddWithValue("id", Guid.CreateVersion7());
        insert.Parameters.AddWithValue("type", fileType);
        insert.Parameters.AddWithValue("license", licenseId);
        insert.Parameters.AddWithValue("content", content);
        insert.Parameters.AddWithValue("user", (object?)context.UserId ?? DBNull.Value);
        insert.Parameters.AddWithValue("tenant", (object?)context.TenantId ?? DBNull.Value);
        insert.Parameters.AddWithValue("name", (object?)Truncate(context.FileName, 200) ?? DBNull.Value);
        await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<NpgsqlConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private static string? Truncate(string? value, int max) => value is null ? null : value.Length <= max ? value : value[..max];
}

/// <summary>
/// The parts of the fingerprint that come from the servers rather than the database: the host's
/// machine id (<c>Licensing:MachineIdFiles</c>, by default <c>/etc/machine-id</c>, which Compose
/// mounts from the host), the Kubernetes cluster id (<c>Licensing:ClusterId</c>, set by the Helm
/// chart) and the installation's domain (<c>Licensing:Domain</c>, else <c>Branding:PublicHost</c>).
/// </summary>
internal sealed class ServerFingerprintSource(IConfiguration configuration)
{
    private static readonly string[] DefaultMachineIdFiles = ["/etc/machine-id", "/var/lib/dbus/machine-id"];

    public Fingerprint Collect(string? installationId, string? databaseId)
    {
        var files = configuration.GetSection("Licensing:MachineIdFiles").GetChildren().Select(c => c.Value).OfType<string>().ToArray();
        string? machineId = null;
        foreach (var file in files.Length > 0 ? files : DefaultMachineIdFiles)
        {
            try
            {
                if (File.Exists(file) && File.ReadAllText(file).Trim() is { Length: >= 8 and <= 128 } value)
                {
                    machineId = value.ToLowerInvariant();
                    break;
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        var cluster = configuration["Licensing:ClusterId"] is { Length: > 0 } c ? c.Trim() : null;
        var domain = (configuration["Licensing:Domain"] is { Length: > 0 } d ? d : configuration["Branding:PublicHost"])?.Trim().ToLowerInvariant();
        return new Fingerprint(installationId, databaseId, machineId is null ? [] : [machineId], cluster, string.IsNullOrEmpty(domain) ? [] : [domain]);
    }
}
