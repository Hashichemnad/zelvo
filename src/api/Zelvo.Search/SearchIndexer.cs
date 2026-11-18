using System.Text.Json;
using Zelvo.Data;
using Zelvo.Data.Search;

namespace Zelvo.Search;

/// <summary>
/// How modules update the search index: each call adds an outbox entry to the module's
/// <see cref="ZelvoDbContext"/>, saved with its next <c>SaveChanges</c>, in the same transaction as
/// the change. Nothing reaches OpenSearch if the transaction rolls back.
/// </summary>
public interface ISearchIndexer
{
    /// <summary>Adds or replaces a document. Throws <see cref="ArgumentException"/> when it is not valid.</summary>
    void Upsert(SearchDocument document);

    /// <summary>Removes a document.</summary>
    void Delete(string type, string id);
}

/// <inheritdoc />
public sealed class SearchIndexer(ZelvoDbContext db) : ISearchIndexer
{
    /// <inheritdoc />
    public void Upsert(SearchDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var problems = document.Validate();
        if (problems.Count > 0)
        {
            throw new ArgumentException($"Search document {document.Type}/{document.Id} is not valid: {string.Join(" ", problems)}", nameof(document));
        }

        db.SearchOutbox.Add(new SearchOutboxEntry
        {
            Operation = SearchOperation.Upsert,
            DocumentType = document.Type,
            DocumentId = document.Id,
            Document = JsonSerializer.Serialize(document, SearchJson.Options),
        });
    }

    /// <inheritdoc />
    public void Delete(string type, string id)
    {
        if (!SearchDocument.IsType(type))
        {
            throw new ArgumentException("Not a valid document type.", nameof(type));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        db.SearchOutbox.Add(new SearchOutboxEntry { Operation = SearchOperation.Delete, DocumentType = type, DocumentId = id });
    }
}

/// <summary>JSON settings for search documents and messages.</summary>
internal static class SearchJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web);
}
