using Zelvo.Data;
using Zelvo.Data.Auditing;
using Zelvo.Data.Tenancy;

namespace Zelvo.Api.Tests;

/// <summary>Creates application-role database contexts the way the API does.</summary>
internal static class TestData
{
    public static ZelvoDbContext AppContext(DatabaseFixture database, Tenant? tenant, AuditContext? audit = null)
    {
        var tenantContext = new TenantContext();
        if (tenant is not null)
        {
            tenantContext.SetTenant(tenant.Id, tenant.Slug);
        }

        return new ZelvoDbContext(DataServiceCollectionExtensions.CreateOptions(
            database.AppConnectionString,
            new TenantConnectionInterceptor(tenantContext),
            new AuditSaveChangesInterceptor(tenantContext, audit ?? new AuditContext())));
    }
}
