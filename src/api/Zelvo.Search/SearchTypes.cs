namespace Zelvo.Search;

/// <summary>
/// A document type modules put in the index. Registered with
/// <see cref="SearchServiceCollectionExtensions.AddSearchType"/>; searches only return registered
/// types, and only those whose permission the caller holds.
/// </summary>
/// <param name="Type">Document type, for example <c>article</c>.</param>
/// <param name="RequiredPermission">Permission needed to find any document of the type; null when the document's readers decide alone.</param>
public sealed record SearchTypeRegistration(string Type, string? RequiredPermission);

/// <summary>
/// Gives every current document of one type, so an administrator can rebuild the index from the
/// database. Modules register one per document type (scoped; the tenant is set).
/// </summary>
public interface ISearchDocumentSource
{
    /// <summary>The document type it gives.</summary>
    string Type { get; }

    /// <summary>All current documents of the tenant.</summary>
    IAsyncEnumerable<SearchDocument> ReadAllAsync(CancellationToken cancellationToken);
}
