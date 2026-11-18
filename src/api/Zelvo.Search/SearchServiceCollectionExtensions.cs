using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Zelvo.Search;

/// <summary>Registers search for the API (querying, producing) and the worker (pipeline).</summary>
public static class SearchServiceCollectionExtensions
{
    /// <summary>
    /// What both hosts need: options, the OpenSearch client, the producer side
    /// (<see cref="ISearchIndexer"/>, <see cref="SearchRebuild"/>) and the query side.
    /// Needs <c>AddZelvoData</c>.
    /// </summary>
    public static IServiceCollection AddZelvoSearch(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        services.AddOptions<SearchOptions>()
            .Configure(o =>
            {
                var section = configuration.GetSection(SearchOptions.Section);
                if (configuration["Services:OpenSearch"] is { Length: > 0 } address)
                {
                    o.OpenSearch = new Uri(address);
                }

                o.IndexPrefix = section["IndexPrefix"] ?? o.IndexPrefix;
                o.Replicas = Int(section["Replicas"]) ?? o.Replicas;
                o.RequestTimeout = Time(section["RequestTimeout"]) ?? o.RequestTimeout;
            })
            .Validate(o => SearchDocument.IsType(o.IndexPrefix) && o.Replicas is >= 0 and <= 5, "Search:IndexPrefix must be lower-case letters, digits or hyphens; Search:Replicas 0-5.");
        services.TryAddSingleton<OpenSearchClient>();
        services.TryAddSingleton<SearchIndexManager>();
        services.TryAddScoped<ISearchIndexer, SearchIndexer>();
        services.TryAddScoped<SearchRebuild>();
        services.TryAddScoped<SearchService>();
        services.TryAddScoped<SearchStatusReader>();
        return services;
    }

    /// <summary>Makes a document type searchable; see <see cref="SearchTypeRegistration"/>.</summary>
    public static IServiceCollection AddSearchType(this IServiceCollection services, string type, string? requiredPermission)
    {
        if (!SearchDocument.IsType(type))
        {
            throw new ArgumentException("Not a valid document type.", nameof(type));
        }

        services.AddSingleton(new SearchTypeRegistration(type, requiredPermission));
        return services;
    }

    /// <summary>The worker side: outbox relay and indexer, connected to RabbitMQ at <paramref name="rabbitMq"/>.</summary>
    public static IServiceCollection AddZelvoSearchPipeline(this IServiceCollection services, IConfiguration configuration, Uri rabbitMq)
    {
        services.AddZelvoSearch(configuration);
        services.AddOptions<SearchPipelineOptions>().Configure(o =>
        {
            var section = configuration.GetSection(SearchPipelineOptions.Section);
            o.PollInterval = Time(section["PollInterval"]) ?? o.PollInterval;
            o.RepublishAfter = Time(section["RepublishAfter"]) ?? o.RepublishAfter;
            o.RetryDelay = Time(section["RetryDelay"]) ?? o.RetryDelay;
            o.BatchSize = Int(section["BatchSize"]) is { } size and > 0 and <= 1000 ? size : o.BatchSize;
            o.Prefetch = (ushort)(Int(section["Prefetch"]) is { } prefetch and > 0 and <= 100 ? prefetch : o.Prefetch);
        });
        services.TryAddSingleton(_ => new SearchRabbitConnection(rabbitMq, "zelvo-worker-search"));
        services.TryAddSingleton<SearchOutboxRelay>();
        services.TryAddSingleton<SearchIndexConsumer>();
        services.AddHostedService(sp => sp.GetRequiredService<SearchOutboxRelay>());
        services.AddHostedService(sp => sp.GetRequiredService<SearchIndexConsumer>());
        return services;
    }

    private static int? Int(string? value) =>
        int.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var number) ? number : null;

    private static TimeSpan? Time(string? value) =>
        TimeSpan.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out var time) ? time : null;
}
