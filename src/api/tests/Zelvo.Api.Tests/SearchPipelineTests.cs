using System.Text;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using Zelvo.Data.Search;
using Zelvo.Search;

namespace Zelvo.Api.Tests;

/// <summary>The indexing pipeline: outbox → RabbitMQ → OpenSearch, per tenant.</summary>
[Collection(SearchTestGroup.Name)]
public sealed class SearchPipelineTests(DatabaseFixture database, SearchFixture search)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Changes_reach_the_index_and_deleted_records_disappear()
    {
        var tenant = await database.CreateTenantAsync("pipe");
        await search.IndexAsync(tenant, TestDocuments.Article("a1", "Network outage", "انقطاع الشبكة"));
        await search.WaitUntilIndexedAsync(tenant);
        Assert.Single((await search.SearchAsync(tenant, "outage")).Items);

        await search.IndexAsync(tenant, TestDocuments.Article("a1", "Network restored", "عودة الشبكة"));
        await search.WaitUntilIndexedAsync(tenant);
        Assert.Empty((await search.SearchAsync(tenant, "outage")).Items);
        Assert.Equal("Network restored", Assert.Single((await search.SearchAsync(tenant, "restored")).Items).Title.En);

        await search.DeleteAsync(tenant, TestDocuments.Type, "a1");
        await search.WaitUntilIndexedAsync(tenant);
        Assert.Empty((await search.SearchAsync(tenant, "network")).Items);

        await using var db = TestData.AppContext(database, tenant);
        var state = await db.SearchIndexStates.SingleAsync(Ct);
        Assert.Equal(SearchIndexDefinition.SchemaVersion, state.SchemaVersion);
        Assert.NotNull(state.LastIndexedAt);
        Assert.False(await db.SearchOutbox.AnyAsync(Ct));
    }

    [Fact]
    public async Task A_tenant_never_finds_another_tenants_documents()
    {
        var first = await database.CreateTenantAsync("iso");
        var second = await database.CreateTenantAsync("iso");
        await search.IndexAsync(first, TestDocuments.Article("same-id", "Confidential salary review", "مراجعة الرواتب السرية"));
        await search.IndexAsync(second, TestDocuments.Article("same-id", "Second tenant document", "مستند المستأجر الثاني"));
        await search.WaitUntilIndexedAsync(first);
        await search.WaitUntilIndexedAsync(second);

        Assert.Empty((await search.SearchAsync(second, "salary")).Items);
        Assert.Empty((await search.SearchAsync(second, "الرواتب")).Items);
        Assert.Single((await search.SearchAsync(first, "salary")).Items);

        // Separate indices; documents also carry their tenant, and every query filters on it.
        Assert.NotEqual(search.AliasFor(first), search.AliasFor(second));
        var count = await search.Client.CountAsync(search.AliasFor(second),
            new JsonObject { ["term"] = new JsonObject { ["tenantId"] = first.Id.ToString() } }, Ct);
        Assert.Equal(0, count);

        // The outbox is under row-level security like every tenant table.
        await using var db = TestData.AppContext(database, second);
        await search.IndexAsync(first, TestDocuments.Article("hidden", "Pending in first", null));
        Assert.False(await db.SearchOutbox.AnyAsync(e => e.DocumentId == "hidden", Ct));
        await search.WaitUntilIndexedAsync(first);
    }

    [Fact]
    public async Task An_older_change_delivered_late_never_overwrites_a_newer_one()
    {
        var tenant = await database.CreateTenantAsync("order");
        var consumer = search.Pipeline.GetRequiredService<SearchIndexConsumer>();
        const long Base = 9_000_000_000_000;

        await consumer.ProcessAsync(Message(tenant, Upsert(Base + 2, "late", "Newer title")), Ct);
        await consumer.ProcessAsync(Message(tenant, Upsert(Base + 1, "late", "Older title")), Ct);
        await consumer.ProcessAsync(Message(tenant, Upsert(Base + 2, "late", "Newer title")), Ct); // delivered twice

        await consumer.ProcessAsync(Message(tenant, new SearchIndexMessageEntry(Base + 4, SearchOperation.Delete, TestDocuments.Type, "gone", null, null)), Ct);
        await consumer.ProcessAsync(Message(tenant, Upsert(Base + 3, "gone", "Deleted before this arrived")), Ct);
        await search.Client.RefreshAsync(search.AliasFor(tenant), Ct);

        Assert.Equal("Newer title", Assert.Single((await search.SearchAsync(tenant, "title")).Items).Title.En);
        Assert.Empty((await search.SearchAsync(tenant, "deleted")).Items);
    }

    [Fact]
    public async Task Refused_documents_are_kept_as_failed_with_the_reason()
    {
        var tenant = await database.CreateTenantAsync("refused");
        var bad = await search.AddOutboxEntryAsync(tenant, new SearchOutboxEntry
        {
            Operation = SearchOperation.Upsert,
            DocumentType = TestDocuments.Type,
            DocumentId = "broken",
            Document = """{"type":"test-article","id":"broken","readers":["public"]}""",
        });
        await search.IndexAsync(tenant, TestDocuments.Article("fine", "Still indexed", null));
        await search.WaitUntilIndexedAsync(tenant);

        await using var db = TestData.AppContext(database, tenant);
        var failed = await db.SearchOutbox.SingleAsync(Ct);
        Assert.Equal(bad.Id, failed.Id);
        Assert.NotNull(failed.FailedAt);
        Assert.Contains("test-article/broken", failed.LastError, StringComparison.Ordinal);
        Assert.Contains("broken", (await db.SearchIndexStates.SingleAsync(Ct)).LastError, StringComparison.Ordinal);
        Assert.Single((await search.SearchAsync(tenant, "indexed")).Items);
    }

    [Fact]
    public async Task Messages_that_cannot_be_read_go_to_the_dead_letter_queue()
    {
        var factory = new ConnectionFactory { Uri = search.RabbitUrl };
        await using var connection = await factory.CreateConnectionAsync(Ct);
        await using var channel = await connection.CreateChannelAsync(cancellationToken: Ct);
        await SearchTopology.DeclareAsync(channel, Ct);
        var before = await channel.MessageCountAsync(SearchTopology.DeadLetterQueue, Ct);

        await channel.BasicPublishAsync(SearchTopology.Exchange, SearchTopology.RoutingKey, Encoding.UTF8.GetBytes("not json"), Ct);

        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (await channel.MessageCountAsync(SearchTopology.DeadLetterQueue, Ct) == before && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100, Ct);
        }

        Assert.Equal(before + 1, await channel.MessageCountAsync(SearchTopology.DeadLetterQueue, Ct));
    }

    [Fact]
    public async Task An_index_of_an_older_schema_version_is_upgraded_without_losing_documents()
    {
        var tenant = await database.CreateTenantAsync("upgrade");
        var alias = search.AliasFor(tenant);
        var old = SearchIndexDefinition.IndexFor(search.IndexPrefix, tenant.Id, 0);
        var oldBody = SearchIndexDefinition.CreateIndexBody(0);
        oldBody["aliases"] = new JsonObject { [alias] = new JsonObject { ["is_write_index"] = true } };
        await search.Client.CreateIndexAsync(old, oldBody, Ct);
        var document = TestDocuments.Article("kept", "Kept through the upgrade", "محفوظ بعد الترقية");
        await search.Client.BulkAsync(
        [
            new JsonObject { ["index"] = new JsonObject { ["_index"] = alias, ["_id"] = document.IndexId, ["version"] = 7, ["version_type"] = "external" } },
            SearchSource.Build(document, tenant.Id, 7),
        ], Ct);

        var manager = new SearchIndexManager(search.Client, search.Pipeline.GetRequiredService<IOptions<SearchOptions>>(), NullLogger<SearchIndexManager>.Instance);
        await manager.EnsureAsync(tenant.Id, Ct);
        await search.Client.RefreshAsync(alias, Ct);

        Assert.Equal([SearchIndexDefinition.IndexFor(search.IndexPrefix, tenant.Id, SearchIndexDefinition.SchemaVersion)],
            await search.Client.GetAliasTargetsAsync(alias, Ct));
        Assert.False(await search.Client.IndexExistsAsync(old, Ct));
        Assert.Equal("kept", Assert.Single((await search.SearchAsync(tenant, "upgrade")).Items).Id);

        // Versions survive: an older write is still refused after the upgrade.
        await search.Pipeline.GetRequiredService<SearchIndexConsumer>().ProcessAsync(Message(tenant, Upsert(6, "kept", "Stale")), Ct);
        await search.Client.RefreshAsync(alias, Ct);
        Assert.Empty((await search.SearchAsync(tenant, "stale")).Items);
    }

    private static SearchIndexMessage Message(Data.Tenancy.Tenant tenant, params SearchIndexMessageEntry[] entries) =>
        new(tenant.Id, tenant.Slug, entries);

    private static SearchIndexMessageEntry Upsert(long id, string documentId, string title) =>
        new(id, SearchOperation.Upsert, TestDocuments.Type, documentId, TestDocuments.Json(TestDocuments.Article(documentId, title, null)), null);
}
