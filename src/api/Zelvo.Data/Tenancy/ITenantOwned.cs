namespace Zelvo.Data.Tenancy;

/// <summary>
/// A row that belongs to one tenant. Its table must have a <c>tenant_id</c> column with row-level
/// security enabled and forced (see <see cref="Migrations.TenantIsolationMigrationExtensions"/>);
/// a test fails the build when one does not. The tenant id is filled in from
/// <see cref="ITenantContext"/> when the row is added.
/// </summary>
public interface ITenantOwned
{
    /// <summary>Owning tenant.</summary>
    Guid TenantId { get; set; }
}
