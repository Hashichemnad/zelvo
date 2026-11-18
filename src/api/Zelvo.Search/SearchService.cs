using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using Zelvo.Data.Localization;
using Zelvo.Data.Tenancy;

namespace Zelvo.Search;

/// <summary>A search of the current tenant's index.</summary>
/// <param name="Text">What the user typed, in English, Arabic or both.</param>
/// <param name="Types">Document types to search; the caller decides which the user may see.</param>
/// <param name="Readers">Who the caller is (<see cref="SearchReaders.ForCaller"/>).</param>
/// <param name="Attributes">Exact attribute filters, for example <c>status</c> → <c>open</c>.</param>
/// <param name="Page">Page from 1.</param>
/// <param name="PageSize">Results per page.</param>
public sealed record SearchQuery(
    string Text,
    IReadOnlyCollection<string> Types,
    IReadOnlyList<string> Readers,
    IReadOnlyDictionary<string, string>? Attributes = null,
    int Page = 1,
    int PageSize = 25);

/// <summary>One result.</summary>
/// <param name="Type">Document type.</param>
/// <param name="Id">Record id within the type.</param>
/// <param name="Title">Title in English and Arabic.</param>
/// <param name="Url">Path of the record in the web app.</param>
/// <param name="UpdatedAt">When the record last changed.</param>
/// <param name="Score">Relevance; higher is better.</param>
/// <param name="Attributes">The document's attributes.</param>
/// <param name="Highlights">
/// Matching fragments per field (<c>title.en</c>, <c>title.ar</c>, <c>body.en</c>, <c>body.ar</c>,
/// <c>text</c>). They are HTML-escaped; matches are wrapped in <c>&lt;mark&gt;</c>.
/// </param>
public sealed record SearchHit(
    string Type,
    string Id,
    LocalizedText Title,
    string? Url,
    DateTimeOffset? UpdatedAt,
    double Score,
    IReadOnlyDictionary<string, string> Attributes,
    IReadOnlyDictionary<string, IReadOnlyList<string>> Highlights);

/// <summary>A page of results.</summary>
public sealed record SearchResults(IReadOnlyList<SearchHit> Items, long TotalCount);

/// <summary>
/// Searches the current tenant's index. The tenant comes from <see cref="ITenantContext"/>, never
/// from the query; every query also filters on the tenant id and the caller's readers.
/// </summary>
public sealed class SearchService(OpenSearchClient client, IOptions<SearchOptions> options, ITenantContext tenantContext)
{
    /// <summary>Largest <c>page × pageSize</c> OpenSearch pages through.</summary>
    public const int MaxResultWindow = 10_000;

    // Up to two words: all must match; more: three quarters of them.
    private const string MinimumShouldMatch = "2<75%";

    /// <summary>Runs the search; throws <see cref="OpenSearchException"/> when OpenSearch is unavailable.</summary>
    public async Task<SearchResults> SearchAsync(SearchQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var tenantId = tenantContext.TenantId ?? throw new InvalidOperationException("Search needs a tenant.");
        if (query.Types.Count == 0 || query.Readers.Count == 0 || string.IsNullOrWhiteSpace(query.Text))
        {
            return new([], 0);
        }

        var alias = SearchIndexDefinition.AliasFor(options.Value.IndexPrefix, tenantId);
        var response = await client.SearchAsync(alias, BuildRequest(tenantId, query), cancellationToken).ConfigureAwait(false);
        if (response is null)
        {
            return new([], 0);
        }

        var hits = response["hits"]?["hits"] as JsonArray ?? [];
        var total = response["hits"]?["total"]?["value"]?.GetValue<long>() ?? 0;
        return new([.. hits.OfType<JsonObject>().Select(ReadHit)], total);
    }

    internal static JsonObject BuildRequest(Guid tenantId, SearchQuery query)
    {
        var filters = new JsonArray(
            Term("tenantId", tenantId.ToString("D")),
            Terms("type", query.Types),
            Terms("readers", query.Readers));
        foreach (var (key, value) in query.Attributes ?? new Dictionary<string, string>())
        {
            filters.Add(Term($"attributes.{key}", value));
        }

        var text = query.Text.Trim();
        return new JsonObject
        {
            ["from"] = (query.Page - 1) * query.PageSize,
            ["size"] = query.PageSize,
            ["track_total_hits"] = true,
            ["_source"] = new JsonArray("type", "id", "title", "url", "updatedAt", "attributes"),
            ["query"] = new JsonObject
            {
                ["bool"] = new JsonObject
                {
                    ["filter"] = filters,
                    ["must"] = new JsonArray(new JsonObject
                    {
                        ["bool"] = new JsonObject
                        {
                            ["minimum_should_match"] = 1,
                            ["should"] = new JsonArray(
                                // Each field in its own language, stemmed: the best field wins.
                                new JsonObject
                                {
                                    ["multi_match"] = new JsonObject
                                    {
                                        ["query"] = text,
                                        ["type"] = "best_fields",
                                        ["fields"] = new JsonArray([.. SearchIndexDefinition.QueryFields.Select(f => (JsonNode)f)]),
                                        ["tie_breaker"] = 0.3,
                                        ["minimum_should_match"] = MinimumShouldMatch,
                                    },
                                },
                                // Words in both languages at once ("password المرور"): the folded
                                // fields share one analyser, so they are searched as one field.
                                new JsonObject
                                {
                                    ["multi_match"] = new JsonObject
                                    {
                                        ["query"] = text,
                                        ["type"] = "cross_fields",
                                        ["fields"] = new JsonArray([.. SearchIndexDefinition.FoldedFields.Select(f => (JsonNode)f)]),
                                        ["minimum_should_match"] = MinimumShouldMatch,
                                    },
                                }),
                        },
                    }),
                    // Whole phrases rank higher than scattered words.
                    ["should"] = new JsonArray(new JsonObject
                    {
                        ["multi_match"] = new JsonObject
                        {
                            ["query"] = text,
                            ["type"] = "phrase",
                            ["fields"] = new JsonArray("title.en.folded^3", "title.ar.folded^3", "body.en.folded", "body.ar.folded", "text"),
                        },
                    }),
                },
            },
            ["highlight"] = new JsonObject
            {
                ["encoder"] = "html",
                ["pre_tags"] = new JsonArray("<mark>"),
                ["post_tags"] = new JsonArray("</mark>"),
                ["require_field_match"] = false,
                ["fragment_size"] = 160,
                ["number_of_fragments"] = 2,
                ["fields"] = new JsonObject(SearchIndexDefinition.HighlightFields.Select(f =>
                    KeyValuePair.Create(f, (JsonNode?)new JsonObject()))),
            },
        };
    }

    private static SearchHit ReadHit(JsonObject hit)
    {
        var source = hit["_source"] as JsonObject ?? [];
        var attributes = new Dictionary<string, string>(StringComparer.Ordinal);
        if (source["attributes"] is JsonObject values)
        {
            foreach (var (key, value) in values)
            {
                if (value is JsonValue v && v.TryGetValue<string>(out var s))
                {
                    attributes[key] = s;
                }
            }
        }

        var highlights = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        if (hit["highlight"] is JsonObject fields)
        {
            foreach (var (field, fragments) in fields)
            {
                if (fragments is JsonArray list)
                {
                    highlights[field] = [.. list.Select(f => f!.GetValue<string>())];
                }
            }
        }

        return new SearchHit(
            source["type"]?.GetValue<string>() ?? string.Empty,
            source["id"]?.GetValue<string>() ?? string.Empty,
            new LocalizedText(source["title"]?["en"]?.GetValue<string>(), source["title"]?["ar"]?.GetValue<string>()),
            source["url"]?.GetValue<string>(),
            source["updatedAt"] is JsonValue updated && DateTimeOffset.TryParse(updated.GetValue<string>(), out var at) ? at : null,
            hit["_score"]?.GetValue<double>() ?? 0,
            attributes,
            highlights);
    }

    private static JsonObject Term(string field, string value) => new() { ["term"] = new JsonObject { [field] = value } };

    private static JsonObject Terms(string field, IEnumerable<string> values) =>
        new() { ["terms"] = new JsonObject { [field] = new JsonArray([.. values.Select(v => (JsonNode)v)]) } };
}
