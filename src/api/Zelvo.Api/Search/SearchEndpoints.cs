using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Http.HttpResults;
using Zelvo.Api.Conventions;
using Zelvo.Api.Identity;
using Zelvo.Api.Tenancy;
using Zelvo.Data.Auditing;
using Zelvo.Search;

namespace Zelvo.Api.Search;

/// <summary>
/// Search across the tenant's content in English and Arabic (LANG-9), and Settings → Search for
/// administrators (module 10): index status and rebuild.
/// </summary>
internal static class SearchEndpoints
{
    public static RouteGroupBuilder MapSearchEndpoints(this RouteGroupBuilder group)
    {
        var search = group.MapGroup("/search").WithTags("Search").RequireTenant();

        search.MapGet("/", SearchAsync)
            .WithName("Search")
            .WithSummary("Searches the tenant's content in English and Arabic, matching Arabic with or without diacritics and with different letter forms. Results are limited to document types and records the caller may see; visitors who are not signed in see public content only.")
            .AllowAnonymousAccess()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        search.MapGet("/status", StatusAsync)
            .WithName("GetSearchStatus")
            .WithSummary("Settings → Search: documents in the tenant's index, changes waiting to be indexed, and the last indexing error. Needs search.manage.")
            .RequirePermission(Permissions.SearchManage);

        search.MapPost("/rebuild", RebuildAsync)
            .WithName("RebuildSearchIndex")
            .WithSummary("Settings → Search: sends every searchable record of the tenant to the index again and removes documents whose records are gone. Searches keep working meanwhile. Needs search.manage; audited.")
            .RequirePermission(Permissions.SearchManage);

        return group;
    }

    internal static async Task<Results<Ok<PageResult<SearchHitResponse>>, ValidationProblem, ProblemHttpResult>> SearchAsync(
        [AsParameters] SearchRequestQuery request,
        [AsParameters] PageQuery paging,
        CurrentUser user,
        Data.ZelvoDbContext db,
        IEnumerable<SearchTypeRegistration> registrations,
        SearchService search,
        CancellationToken cancellationToken)
    {
        var visible = registrations
            .Where(r => r.RequiredPermission is null || user.Has(r.RequiredPermission))
            .Select(r => r.Type)
            .ToHashSet(StringComparer.Ordinal);

        var types = visible;
        if (!string.IsNullOrWhiteSpace(request.Types))
        {
            var asked = request.Types.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (asked.Length > 20 || asked.Any(t => !SearchDocument.IsType(t)))
            {
                return UserEndpoints.Validation("types", "A comma-separated list of document types.");
            }

            // Types the caller may not see are left out silently, like hidden records.
            types = asked.Where(visible.Contains).ToHashSet(StringComparer.Ordinal);
        }

        if (paging.Number * paging.Size > SearchService.MaxResultWindow)
        {
            return UserEndpoints.Validation("page", $"Results beyond the first {SearchService.MaxResultWindow} are not available; refine the search.");
        }

        // Task C2: services shown to groups, departments and locations; the caller is a reader of their
        // groups, department and location with the locations above it.
        var readers = (await Catalog.CatalogScope.OfAsync(db, user, cancellationToken)).Readers;
        try
        {
            var results = await search.SearchAsync(
                new SearchQuery(request.Q!, types, readers, null, paging.Number, paging.Size), cancellationToken);
            return TypedResults.Ok(new PageResult<SearchHitResponse>(
                [.. results.Items.Select(SearchHitResponse.From)], paging.Number, paging.Size, results.TotalCount));
        }
        catch (OpenSearchException)
        {
            return Unavailable();
        }
    }

    internal static async Task<Ok<SearchStatus>> StatusAsync(SearchStatusReader reader, CancellationToken cancellationToken) =>
        TypedResults.Ok(await reader.ReadAsync(cancellationToken));

    internal static async Task<Accepted<SearchRebuildResponse>> RebuildAsync(
        SearchRebuild rebuild, IAuditLog audit, CancellationToken cancellationToken)
    {
        var queued = await rebuild.RequestAsync(cancellationToken);
        await audit.RecordAsync(AuditCategory.Admin, "search.index.rebuild-requested", "search_index",
            details: new Dictionary<string, object?>(StringComparer.Ordinal) { ["documentsQueued"] = queued },
            cancellationToken: cancellationToken);
        return TypedResults.Accepted("/api/v1/search/status", new SearchRebuildResponse(queued));
    }

    private static ProblemHttpResult Unavailable() =>
        (ProblemHttpResult)ApiProblem.Create(StatusCodes.Status503ServiceUnavailable, "search-unavailable",
            "Search is not available right now. Try again shortly.");
}

/// <summary>Search query parameters.</summary>
public sealed class SearchRequestQuery
{
    /// <summary>What to search for, in English or Arabic.</summary>
    [Required]
    [StringLength(200, MinimumLength = 1)]
    public string? Q { get; init; }

    /// <summary>Document types to search, comma separated (for example <c>article,service</c>); all the caller may see when empty.</summary>
    [StringLength(1000)]
    public string? Types { get; init; }
}

/// <summary>One search result.</summary>
/// <param name="Type">Document type.</param>
/// <param name="Id">Record id within the type.</param>
/// <param name="Title">Title in English and Arabic.</param>
/// <param name="Url">Path of the record in the web app.</param>
/// <param name="UpdatedAt">When the record last changed.</param>
/// <param name="Score">Relevance.</param>
/// <param name="Attributes">The record's filterable values.</param>
/// <param name="Highlights">Matching fragments per field (<c>title.en</c>, <c>title.ar</c>, <c>body.en</c>, <c>body.ar</c>, <c>text</c>); HTML-escaped, matches in <c>&lt;mark&gt;</c>.</param>
internal sealed record SearchHitResponse(
    string Type, string Id, LocalizedTextDto Title, string? Url, DateTimeOffset? UpdatedAt, double Score,
    IReadOnlyDictionary<string, string> Attributes, IReadOnlyDictionary<string, IReadOnlyList<string>> Highlights)
{
    public static SearchHitResponse From(SearchHit hit) =>
        new(hit.Type, hit.Id, LocalizedTextDto.From(hit.Title), hit.Url, hit.UpdatedAt, hit.Score, hit.Attributes, hit.Highlights);
}

/// <summary>A queued rebuild.</summary>
/// <param name="DocumentsQueued">Documents sent to the index again.</param>
internal sealed record SearchRebuildResponse(int DocumentsQueued);
