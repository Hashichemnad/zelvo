using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Zelvo.Data.Identity;
using Zelvo.Data.Tenancy;
using Zelvo.Search;

namespace Zelvo.Api.Tests;

/// <summary>Search API (LANG-9) and Settings → Search (module 10).</summary>
[Collection(SearchTestGroup.Name)]
public sealed class SearchApiTests : IAsyncDisposable
{
    private const string InternalType = "test-internal";
    private static readonly Uri StatusUri = new("/api/v1/search/status", UriKind.Relative);
    private static readonly Uri RebuildUri = new("/api/v1/search/rebuild", UriKind.Relative);

    private readonly DatabaseFixture _database;
    private readonly SearchFixture _search;
    private readonly ApiFactory _factory;

    public SearchApiTests(DatabaseFixture database, SearchFixture search)
    {
        _database = database;
        _search = search;
        _factory = search.Api().WithServices(AddTestTypes);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    [Fact]
    public async Task Callers_find_only_documents_whose_readers_include_them()
    {
        var tenant = await _database.CreateTenantAsync("readers");
        var requester = await _database.CreateUserAsync(tenant, BuiltInRoles.Requester);
        var agent = await _database.CreateUserAsync(tenant, BuiltInRoles.Agent);
        await _search.IndexAsync(tenant,
            TestDocuments.Article("public", "Holiday opening hours", "ساعات العمل في العطلات"),
            TestDocuments.Article("staff", "Holiday rota for agents", "جدول العطلات للموظفين", readers: SearchReaders.Role(BuiltInRoles.Agent)),
            TestDocuments.Article("mine", "My holiday request", "طلب العطلة الخاص بي", readers: SearchReaders.User(requester.Id)),
            TestDocuments.Article("signed-in", "Holiday policy", "سياسة العطلات", readers: SearchReaders.SignedIn));
        await _search.WaitUntilIndexedAsync(tenant);

        Assert.Equal(["public"], await IdsAsync(_factory.ForTenant(tenant), "holiday"));
        Assert.Equal(["mine", "public", "signed-in"], await IdsAsync(_factory.ForTenant(tenant).As(requester), "العطلات"));
        Assert.Equal(["public", "signed-in", "staff"], await IdsAsync(_factory.ForTenant(tenant).As(agent), "holiday"));

        // Without the second factor an agent's role does not apply yet.
        Assert.Equal(["public", "signed-in"], await IdsAsync(_factory.ForTenant(tenant).As(agent, mfa: false), "holiday"));
    }

    [Fact]
    public async Task Types_that_need_a_permission_are_hidden_from_callers_without_it()
    {
        var tenant = await _database.CreateTenantAsync("types");
        await _search.IndexAsync(tenant,
            TestDocuments.Article("article", "Printer guide", "دليل الطابعة"),
            TestDocuments.Article("internal", "Printer supplier contract", "عقد مورد الطابعات", readers: SearchReaders.SignedIn) with { Type = InternalType });
        await _search.WaitUntilIndexedAsync(tenant);
        using var requester = await _factory.ForTenant(tenant).SignedInAsync(_database, tenant, BuiltInRoles.Requester);
        using var agent = await _factory.ForTenant(tenant).SignedInAsync(_database, tenant, BuiltInRoles.Agent);

        Assert.Equal(["article"], await IdsAsync(requester, "printer"));
        Assert.Empty(await IdsAsync(requester, "printer", InternalType));
        Assert.Equal(["article", "internal"], await IdsAsync(agent, "printer"));
        Assert.Equal(["internal"], await IdsAsync(agent, "printer", InternalType));
    }

    [Fact]
    public async Task Results_carry_both_languages_and_paging()
    {
        var tenant = await _database.CreateTenantAsync("page");
        await _search.IndexAsync(tenant, [.. Enumerable.Range(1, 3).Select(i => TestDocuments.Article($"vpn-{i}", $"VPN guide {i}", $"دليل الشبكة الخاصة {i}"))]);
        await _search.WaitUntilIndexedAsync(tenant);
        using var client = _factory.ForTenant(tenant);

        var page = await client.GetFromJsonAsync<SearchPage>("/api/v1/search?q=vpn&pageSize=2&page=2", Ct);

        Assert.Equal((2, 2, 3L), (page!.Page, page.PageSize, page.TotalCount));
        var hit = Assert.Single(page.Items);
        Assert.Equal(TestDocuments.Type, hit.Type);
        Assert.StartsWith("VPN guide", hit.Title.En, StringComparison.Ordinal);
        Assert.StartsWith("دليل الشبكة", hit.Title.Ar, StringComparison.Ordinal);
        Assert.StartsWith("/knowledge/vpn-", hit.Url, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/api/v1/search")]
    [InlineData("/api/v1/search?q=")]
    [InlineData("/api/v1/search?q=a&types=Not_A_Type")]
    [InlineData("/api/v1/search?q=a&page=500&pageSize=100")]
    public async Task Invalid_searches_are_refused(string uri)
    {
        var tenant = await _database.CreateTenantAsync("invalid");
        using var client = _factory.ForTenant(tenant);

        var response = await client.GetAsync(new Uri(uri, UriKind.Relative), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("validation-failed", (await response.Content.ReadFromJsonAsync<ProblemBody>(Ct))!.Code);
    }

    [Fact]
    public async Task Search_says_so_when_OpenSearch_is_unavailable()
    {
        var tenant = await _database.CreateTenantAsync("down");
        await using var factory = ApiFactory.ForDatabase(_database).WithServices(AddTestTypes);
        using var client = factory.ForTenant(tenant);

        var response = await client.GetAsync(new Uri("/api/v1/search?q=printer", UriKind.Relative), Ct);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("search-unavailable", (await response.Content.ReadFromJsonAsync<ProblemBody>(Ct))!.Code);
    }

    [Fact]
    public async Task Only_administrators_see_the_status_and_rebuild_the_index()
    {
        var tenant = await _database.CreateTenantAsync("admin-only");
        using var anonymous = _factory.ForTenant(tenant);
        using var agent = await _factory.ForTenant(tenant).SignedInAsync(_database, tenant, BuiltInRoles.Agent);
        using var executive = await _factory.ForTenant(tenant).SignedInAsync(_database, tenant, BuiltInRoles.ExecutiveAuditor);

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(StatusUri, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await agent.GetAsync(StatusUri, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await executive.GetAsync(StatusUri, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await agent.PostAsync(RebuildUri, null, Ct)).StatusCode);
    }

    [Fact]
    public async Task The_status_shows_documents_pending_updates_and_the_last_error()
    {
        var tenant = await _database.CreateTenantAsync("status");
        using var admin = await _factory.ForTenant(tenant).SignedInAsync(_database, tenant, BuiltInRoles.TenantAdmin);

        var empty = await admin.GetFromJsonAsync<StatusBody>(StatusUri, Ct);
        Assert.Equal(new StatusBody(true, 0, 0, 0, null, null, SearchIndexDefinition.SchemaVersion, false), empty! with { LastIndexedAt = null });

        await _search.AddOutboxEntryAsync(tenant, new Data.Search.SearchOutboxEntry
        {
            Operation = Data.Search.SearchOperation.Upsert,
            DocumentType = TestDocuments.Type,
            DocumentId = "broken",
            Document = "{}",
        });
        await _search.IndexAsync(tenant, TestDocuments.Article("one", "One", "واحد"), TestDocuments.Article("two", "Two", "اثنان"));
        await _search.WaitUntilIndexedAsync(tenant);

        var status = await admin.GetFromJsonAsync<StatusBody>(StatusUri, Ct);
        Assert.True(status!.Available);
        Assert.Equal(2, status.Documents);
        Assert.Equal(0, status.PendingUpdates);
        Assert.Equal(1, status.FailedUpdates);
        Assert.Contains("test-article/broken", status.LastError, StringComparison.Ordinal);
        Assert.NotNull(status.LastIndexedAt);
    }

    [Fact]
    public async Task A_rebuild_sends_every_record_again_removes_stale_documents_and_is_audited()
    {
        var tenant = await _database.CreateTenantAsync("rebuild");
        using var admin = await _factory.ForTenant(tenant).SignedInAsync(_database, tenant, BuiltInRoles.TenantAdmin);
        await _search.IndexAsync(tenant,
            TestDocuments.Article("kept", "Old title of a kept article", null),
            TestDocuments.Article("orphan", "Orphan whose record was deleted", null));
        await _search.WaitUntilIndexedAsync(tenant);
        await _search.AddOutboxEntryAsync(tenant, new Data.Search.SearchOutboxEntry
        {
            Operation = Data.Search.SearchOperation.Upsert,
            DocumentType = TestDocuments.Type,
            DocumentId = "kept",
            Document = "{}",
        });
        await _search.WaitUntilIndexedAsync(tenant);
        TestSource.Documents[tenant.Id] = [TestDocuments.Article("kept", "Current title of a kept article", "العنوان الحالي")];

        var response = await admin.PostAsync(RebuildUri, null, Ct);
        await _search.WaitUntilIndexedAsync(tenant);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal(1, (await response.Content.ReadFromJsonAsync<RebuildBody>(Ct))!.DocumentsQueued);
        Assert.Equal(["kept"], await IdsAsync(admin, "article"));
        Assert.Empty(await IdsAsync(admin, "orphan"));
        Assert.Equal("Current title of a kept article", (await _search.SearchAsync(tenant, "current")).Items.Single().Title.En);

        var status = await admin.GetFromJsonAsync<StatusBody>(StatusUri, Ct);
        Assert.Equal((1L, 0, 0, false), (status!.Documents, status.PendingUpdates, status.FailedUpdates, status.RebuildInProgress));

        await using var db = TestData.AppContext(_database, tenant);
        var audit = await db.AuditEvents.SingleAsync(e => e.Action == "search.index.rebuild-requested", Ct);
        Assert.Equal(Data.Auditing.AuditCategory.Admin, audit.Category);
        Assert.Contains("\"documentsQueued\": 1", audit.Metadata, StringComparison.Ordinal);
    }

    private static void AddTestTypes(IServiceCollection services)
    {
        services.AddSearchType(TestDocuments.Type, requiredPermission: null);
        services.AddSearchType(InternalType, requiredPermission: "users.read");
        services.AddScoped<ISearchDocumentSource, TestSource>();
    }

    private static async Task<List<string>> IdsAsync(HttpClient client, string query, string? types = null)
    {
        var uri = $"/api/v1/search?q={Uri.EscapeDataString(query)}" + (types is null ? string.Empty : $"&types={types}");
        var page = await client.GetFromJsonAsync<SearchPage>(new Uri(uri, UriKind.Relative), Ct);
        return [.. page!.Items.Select(i => i.Id).Order(StringComparer.Ordinal)];
    }

    private sealed record SearchPage(List<HitBody> Items, int Page, int PageSize, long TotalCount);

    private sealed record HitBody(string Type, string Id, TextBody Title, string? Url);

    private sealed record TextBody(string? En, string? Ar);

    private sealed record StatusBody(
        bool Available, long? Documents, int PendingUpdates, int FailedUpdates, string? LastError,
        DateTimeOffset? LastIndexedAt, int CurrentSchemaVersion, bool RebuildInProgress);

    private sealed record RebuildBody(int DocumentsQueued);

    /// <summary>A module's records, per tenant, for rebuilds.</summary>
    private sealed class TestSource(ITenantContext tenantContext) : ISearchDocumentSource
    {
        public static ConcurrentDictionary<Guid, List<SearchDocument>> Documents { get; } = new();

        public string Type => TestDocuments.Type;

        public async IAsyncEnumerable<SearchDocument> ReadAllAsync([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            foreach (var document in Documents.GetValueOrDefault(tenantContext.TenantId!.Value) ?? [])
            {
                yield return document;
            }
        }
    }
}
