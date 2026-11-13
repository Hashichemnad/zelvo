using System.Net.Http.Json;
using Zelvo.Data.Identity;

namespace Zelvo.Api.Tests;

public sealed class TranslationApiTests(DatabaseFixture database) : IAsyncDisposable
{
    private static readonly Uri MissingUri = new("/api/v1/translations/missing", UriKind.Relative);
    private readonly ApiFactory _factory = ApiFactory.ForDatabase(database);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    [Fact]
    public async Task Admins_see_fields_missing_a_language()
    {
        var tenant = await database.CreateTenantAsync(nameEn: "English only", nameAr: null);
        using var client = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.TenantAdmin);

        var page = await client.GetFromJsonAsync<PageBody>(MissingUri, Ct);

        var item = Assert.Single(page!.Items);
        Assert.Equal("tenant", item.EntityType);
        Assert.Equal(tenant.Id.ToString(), item.EntityId);
        Assert.Equal("name", item.Field);
        Assert.Equal(["ar"], item.MissingLanguages);
    }

    [Fact]
    public async Task A_complete_tenant_has_nothing_missing()
    {
        var tenant = await database.CreateTenantAsync();
        using var client = await _factory.ForTenant(tenant).SignedInAsync(database, tenant, BuiltInRoles.TenantAdmin);

        var page = await client.GetFromJsonAsync<PageBody>(MissingUri, Ct);

        Assert.Empty(page!.Items);
        Assert.Equal(0, page.TotalCount);
    }

    private sealed record PageBody(List<ItemBody> Items, long TotalCount);

    private sealed record ItemBody(string EntityType, string EntityId, string Field, List<string> MissingLanguages);
}
