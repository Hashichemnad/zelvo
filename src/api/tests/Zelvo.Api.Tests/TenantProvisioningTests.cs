using Microsoft.EntityFrameworkCore;
using Zelvo.Data.Localization;
using Zelvo.Data.Tenancy;

namespace Zelvo.Api.Tests;

public sealed class TenantProvisioningTests(DatabaseFixture database)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Provisioning_again_returns_the_existing_tenant_without_a_new_audit_entry()
    {
        var tenant = await database.CreateTenantAsync();

        // Repeated migrate runs may omit the names: the existing tenant is kept as it is.
        var (again, created) = await TenantProvisioning.EnsureTenantAsync(
            database.OwnerConnectionString, tenant.Slug, new LocalizedText(null, null), Ct);

        Assert.False(created);
        Assert.Equal(tenant.Id, again.Id);
        await using var db = TestData.AppContext(database, tenant);
        Assert.Equal(1, await db.AuditEvents.CountAsync(Ct));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Upper")]
    [InlineData("-leading")]
    [InlineData("trailing-")]
    [InlineData("has space")]
    public async Task Invalid_slugs_are_rejected(string slug)
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            TenantProvisioning.EnsureTenantAsync(database.OwnerConnectionString, slug, new LocalizedText("Name", null), Ct));
    }

    [Fact]
    public async Task A_new_tenant_needs_a_name()
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            TenantProvisioning.EnsureTenantAsync(database.OwnerConnectionString, $"n-{Guid.NewGuid():N}", new LocalizedText(" ", null), Ct));
    }
}
