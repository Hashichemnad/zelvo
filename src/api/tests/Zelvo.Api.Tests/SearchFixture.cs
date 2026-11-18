using System.Text.Json;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Zelvo.Data;
using Zelvo.Data.Search;
using Zelvo.Data.Tenancy;
using Zelvo.Search;

namespace Zelvo.Api.Tests;

/// <summary>Search tests share one OpenSearch, one RabbitMQ and one running indexing pipeline.</summary>
[CollectionDefinition(Name)]
public sealed class SearchTestGroup : ICollectionFixture<SearchFixture>
{
    public const string Name = "search";
}

/// <summary>
/// Real OpenSearch (Zelvo's image with the ICU plugin; CI builds <c>zelvo/opensearch:dev</c> from
/// deploy/opensearch first, or set <c>ZELVO_TEST_OPENSEARCH_IMAGE</c>) and RabbitMQ, plus the
/// worker's relay and indexer running against the test database as the application role.
/// </summary>
public sealed class SearchFixture(DatabaseFixture database) : IAsyncLifetime
{
    private const string RabbitUser = "zelvo";
    private const string RabbitPassword = "test-only-rabbit-password";

    private readonly IContainer _openSearch = new ContainerBuilder(
            Environment.GetEnvironmentVariable("ZELVO_TEST_OPENSEARCH_IMAGE") ?? "zelvo/opensearch:dev")
        .WithEnvironment("discovery.type", "single-node")
        .WithEnvironment("DISABLE_SECURITY_PLUGIN", "true")
        .WithEnvironment("DISABLE_INSTALL_DEMO_CONFIG", "true")
        .WithEnvironment("OPENSEARCH_JAVA_OPTS", "-Xms512m -Xmx512m")
        .WithEnvironment("cluster.routing.allocation.disk.watermark.low", "2gb")
        .WithEnvironment("cluster.routing.allocation.disk.watermark.high", "1gb")
        .WithEnvironment("cluster.routing.allocation.disk.watermark.flood_stage", "500mb")
        .WithPortBinding(9200, true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(9200).ForPath("/_cluster/health")))
        .Build();

    private readonly IContainer _rabbit = new ContainerBuilder("rabbitmq:4.4-alpine")
        .WithEnvironment("RABBITMQ_DEFAULT_USER", RabbitUser)
        .WithEnvironment("RABBITMQ_DEFAULT_PASS", RabbitPassword)
        .WithPortBinding(5672, true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("Server startup complete"))
        .Build();

    private ServiceProvider? _pipeline;

    /// <summary>Index names start with this, so test indices never mix with others.</summary>
    public string IndexPrefix { get; } = $"test{Guid.NewGuid():N}"[..16];

    public Uri OpenSearchUrl => new($"http://{_openSearch.Hostname}:{_openSearch.GetMappedPublicPort(9200)}/");

    public Uri RabbitUrl => new($"amqp://{RabbitUser}:{RabbitPassword}@{_rabbit.Hostname}:{_rabbit.GetMappedPublicPort(5672)}/");

    /// <summary>The worker's services (relay, indexer, index manager, OpenSearch client).</summary>
    public IServiceProvider Pipeline => _pipeline ?? throw new InvalidOperationException("Not started.");

    public OpenSearchClient Client => Pipeline.GetRequiredService<OpenSearchClient>();

    public async ValueTask InitializeAsync()
    {
        await Task.WhenAll(_openSearch.StartAsync(), _rabbit.StartAsync());
        _pipeline = BuildPipeline(Settings());
        foreach (var service in _pipeline.GetServices<IHostedService>())
        {
            await service.StartAsync(TestContext.Current.CancellationToken);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_pipeline is not null)
        {
            foreach (var service in _pipeline.GetServices<IHostedService>())
            {
                await service.StopAsync(CancellationToken.None);
            }

            await _pipeline.DisposeAsync();
        }

        await _openSearch.DisposeAsync();
        await _rabbit.DisposeAsync();
    }

    /// <summary>An API whose search uses this OpenSearch.</summary>
    public ApiFactory Api() => ApiFactory.ForDatabase(database)
        .WithSetting("Services:OpenSearch", OpenSearchUrl.ToString())
        .WithSetting("Search:IndexPrefix", IndexPrefix);

    /// <summary>Worker services that are not started (for tests that drive the relay and indexer themselves).</summary>
    public ServiceProvider BuildPipeline(Dictionary<string, string?> settings)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddZelvoData(database.AppConnectionString);
        services.AddZelvoSearchPipeline(configuration, RabbitUrl);
        return services.BuildServiceProvider();
    }

    public Dictionary<string, string?> Settings() => new(StringComparer.Ordinal)
    {
        ["Services:OpenSearch"] = OpenSearchUrl.ToString(),
        ["Search:IndexPrefix"] = IndexPrefix,
        ["Search:Pipeline:PollInterval"] = "00:00:00.100",
        ["Search:Pipeline:RetryDelay"] = "00:00:00.500",
    };

    public string AliasFor(Tenant tenant) => SearchIndexDefinition.AliasFor(IndexPrefix, tenant.Id);

    /// <summary>Saves documents of <paramref name="tenant"/> through the outbox, as a module would.</summary>
    public async Task IndexAsync(Tenant tenant, params SearchDocument[] documents)
    {
        await using var db = TestData.AppContext(database, tenant);
        var indexer = new SearchIndexer(db);
        foreach (var document in documents)
        {
            indexer.Upsert(document);
        }

        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>Deletes documents of <paramref name="tenant"/> through the outbox.</summary>
    public async Task DeleteAsync(Tenant tenant, string type, string id)
    {
        await using var db = TestData.AppContext(database, tenant);
        new SearchIndexer(db).Delete(type, id);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>Waits until the running pipeline has indexed (or refused) every outbox entry of the tenant.</summary>
    public async Task WaitUntilIndexedAsync(Tenant tenant)
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (true)
        {
            await using (var db = TestData.AppContext(database, tenant))
            {
                if (!await db.SearchOutbox.AnyAsync(e => e.FailedAt == null, TestContext.Current.CancellationToken))
                {
                    break;
                }
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Search outbox of tenant {tenant.Slug} was not indexed in time.");
            }

            await Task.Delay(100, TestContext.Current.CancellationToken);
        }

        await Client.RefreshAsync(AliasFor(tenant), TestContext.Current.CancellationToken);
    }

    /// <summary>Searches as the pipeline would be searched by the API, for a tenant.</summary>
    public Task<SearchResults> SearchAsync(Tenant tenant, string text, IReadOnlyCollection<string>? types = null, IReadOnlyList<string>? readers = null)
    {
        var tenantContext = new TenantContext();
        tenantContext.SetTenant(tenant.Id, tenant.Slug);
        var service = new SearchService(Client, Pipeline.GetRequiredService<Microsoft.Extensions.Options.IOptions<SearchOptions>>(), tenantContext);
        return service.SearchAsync(new SearchQuery(text, types ?? [TestDocuments.Type], readers ?? [SearchReaders.Public]), TestContext.Current.CancellationToken);
    }

    /// <summary>Writes an outbox entry directly (for malformed or out-of-order entries).</summary>
    public async Task<SearchOutboxEntry> AddOutboxEntryAsync(Tenant tenant, SearchOutboxEntry entry)
    {
        await using var db = TestData.AppContext(database, tenant);
        db.SearchOutbox.Add(entry);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return entry;
    }
}

/// <summary>Documents for search tests.</summary>
internal static class TestDocuments
{
    public const string Type = "test-article";

    public static SearchDocument Article(string id, string? en, string? ar, string? bodyEn = null, string? bodyAr = null, params string[] readers) => new()
    {
        Type = Type,
        Id = id,
        Title = new(en, ar),
        Body = bodyEn is null && bodyAr is null ? null : new(bodyEn, bodyAr),
        Readers = readers.Length == 0 ? [SearchReaders.Public] : readers,
        Url = $"/knowledge/{id}",
        UpdatedAt = DateTimeOffset.UtcNow,
    };

    public static string Json(SearchDocument document) => JsonSerializer.Serialize(document, SearchJson.Options);
}
