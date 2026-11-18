using Zelvo.Data.Tenancy;

namespace Zelvo.Data.Search;

/// <summary>What a search outbox entry asks the indexer to do.</summary>
public enum SearchOperation
{
    /// <summary>Add or replace a document.</summary>
    Upsert,

    /// <summary>Remove a document.</summary>
    Delete,

    /// <summary>
    /// End of a rebuild: remove every document of the tenant last written by an entry older than
    /// <see cref="SearchOutboxEntry.PurgeBefore"/>, that is, documents the rebuild did not send again.
    /// </summary>
    Purge,
}

/// <summary>
/// A pending change to the tenant's search index, written in the same transaction as the change
/// it describes (transactional outbox). The worker publishes entries to RabbitMQ and deletes each
/// one once OpenSearch has it. The id orders changes: it is the document version in OpenSearch,
/// so an older change never overwrites a newer one.
/// </summary>
public sealed class SearchOutboxEntry : ITenantOwned
{
    /// <summary>Maximum length of <see cref="DocumentType"/>.</summary>
    public const int DocumentTypeMaxLength = 50;

    /// <summary>Maximum length of <see cref="DocumentId"/>.</summary>
    public const int DocumentIdMaxLength = 200;

    /// <summary>Maximum length of <see cref="LastError"/>.</summary>
    public const int ErrorMaxLength = 2000;

    /// <summary>Sequence number, assigned by the database; also the document version.</summary>
    public long Id { get; init; }

    /// <inheritdoc />
    public Guid TenantId { get; set; }

    /// <summary>What to do.</summary>
    public SearchOperation Operation { get; init; }

    /// <summary>Document type, for example <c>article</c>. Null for <see cref="SearchOperation.Purge"/>.</summary>
    public string? DocumentType { get; init; }

    /// <summary>Document id within its type. Null for <see cref="SearchOperation.Purge"/>.</summary>
    public string? DocumentId { get; init; }

    /// <summary>The document as JSON, for <see cref="SearchOperation.Upsert"/>.</summary>
    public string? Document { get; init; }

    /// <summary>For <see cref="SearchOperation.Purge"/>: the first entry id of the rebuild.</summary>
    public long? PurgeBefore { get; init; }

    /// <summary>When the entry was written.</summary>
    public DateTimeOffset EnqueuedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>When the worker last published it to RabbitMQ; republished if it stays too long.</summary>
    public DateTimeOffset? PublishedAt { get; set; }

    /// <summary>How many times indexing it failed.</summary>
    public int Attempts { get; set; }

    /// <summary>When it was given up on (the document was refused); a rebuild sends it again.</summary>
    public DateTimeOffset? FailedAt { get; set; }

    /// <summary>The last indexing error, for administrators. Never holds document content.</summary>
    public string? LastError { get; set; }
}
