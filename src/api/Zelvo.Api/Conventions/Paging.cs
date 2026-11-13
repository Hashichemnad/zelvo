using System.ComponentModel.DataAnnotations;

namespace Zelvo.Api.Conventions;

/// <summary>
/// Paging query parameters shared by every list endpoint: <c>?page=1&amp;pageSize=25</c>.
/// Pages start at 1; at most <see cref="MaxPageSize"/> items per page.
/// </summary>
/// <remarks>Public so the request validation generator includes it; properties are nullable so
/// that omitting them uses the defaults.</remarks>
public sealed class PageQuery
{
    /// <summary>Items per page when not given.</summary>
    public const int DefaultPageSize = 25;

    /// <summary>Largest allowed page size.</summary>
    public const int MaxPageSize = 100;

    /// <summary>Page number, starting at 1.</summary>
    [Range(1, 100_000)]
    public int? Page { get; init; }

    /// <summary>Items per page, 1 to 100.</summary>
    [Range(1, MaxPageSize)]
    public int? PageSize { get; init; }

    /// <summary>Page number with the default applied.</summary>
    internal int Number => Page ?? 1;

    /// <summary>Page size with the default applied.</summary>
    internal int Size => PageSize ?? DefaultPageSize;

    /// <summary>Rows to skip.</summary>
    internal int Skip => (Number - 1) * Size;
}

/// <summary>One page of a list.</summary>
/// <param name="Items">Items on this page.</param>
/// <param name="Page">Page number, starting at 1.</param>
/// <param name="PageSize">Requested items per page.</param>
/// <param name="TotalCount">Items across all pages.</param>
internal sealed record PageResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, long TotalCount);
