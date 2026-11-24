using Zelvo.Data.Auditing;
using Zelvo.Data.Localization;
using Zelvo.Data.Tenancy;

namespace Zelvo.Data.Knowledge;

/// <summary>Where an article stands (module 7).</summary>
public enum ArticleState
{
    /// <summary>Being written; never published.</summary>
    Draft,

    /// <summary>Submitted and waiting for a reviewer; never published.</summary>
    InReview,

    /// <summary>A published version is live. Changes may be pending in a new version.</summary>
    Published,

    /// <summary>Taken out of use: not shown or found. Can be reopened as a draft.</summary>
    Retired,
}

/// <summary>Who may read a published article.</summary>
public enum ArticleVisibility
{
    /// <summary>Staff with <c>knowledge.read</c> only.</summary>
    Internal,

    /// <summary>The portal: signed-in users, and visitors when the tenant allows it.</summary>
    External,
}

/// <summary>Where one version of an article stands.</summary>
public enum ArticleVersionState
{
    /// <summary>Being written or changed.</summary>
    Draft,

    /// <summary>Submitted for review.</summary>
    InReview,

    /// <summary>The live version.</summary>
    Published,

    /// <summary>Was live, replaced by a newer published version.</summary>
    Superseded,

    /// <summary>Was live when the article was retired.</summary>
    Retired,
}

/// <summary>
/// A knowledge article (module 7). Its content lives in versions: the published version is what
/// readers see; changes are made in a working version that goes through review and replaces the
/// published one only when a reviewer other than its author approves it (product owner decision
/// 2026-11-24). Articles are retired rather than deleted once published, and numbers
/// (<c>KB-000123</c>) are never reused.
/// </summary>
public sealed class KbArticle : ITenantOwned, IAuditedEntity
{
    /// <summary>Maximum length of <see cref="Number"/>.</summary>
    public const int NumberMaxLength = 20;

    /// <summary>Unique id (UUID version 7).</summary>
    public Guid Id { get; init; } = Guid.CreateVersion7();

    /// <inheritdoc />
    public Guid TenantId { get; set; }

    /// <summary>Number shown to people, for example <c>KB-000123</c>.</summary>
    public required string Number { get; init; }

    /// <summary>Sequence behind <see cref="Number"/>, per tenant.</summary>
    public long Sequence { get; init; }

    /// <summary>Lifecycle state.</summary>
    public ArticleState State { get; set; } = ArticleState.Draft;

    /// <summary>Visibility of the published version (of the working version until first published).</summary>
    public ArticleVisibility Visibility { get; set; } = ArticleVisibility.Internal;

    /// <summary>Category of the published version (of the working version until first published).</summary>
    public Guid? CategoryId { get; set; }

    /// <summary>The live version.</summary>
    public Guid? PublishedVersionId { get; set; }

    /// <summary>The version being written or reviewed, if any.</summary>
    public Guid? WorkingVersionId { get; set; }

    /// <summary>Who created the article.</summary>
    public Guid? CreatedById { get; init; }

    /// <summary>The ticket the article was made from ("turn this ticket into an article").</summary>
    public Guid? SourceTicketId { get; init; }

    /// <summary>Views by distinct viewers per day.</summary>
    public long ViewCount { get; set; }

    /// <summary>"Was this helpful?" answered yes.</summary>
    public int HelpfulCount { get; set; }

    /// <summary>"Was this helpful?" answered no.</summary>
    public int NotHelpfulCount { get; set; }

    /// <summary>When it was created.</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>When it was last changed.</summary>
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>When a version was last published.</summary>
    public DateTimeOffset? PublishedAt { get; set; }

    /// <summary>When it was retired.</summary>
    public DateTimeOffset? RetiredAt { get; set; }

    // No concurrency token here: view and feedback counters change the row all the time.
    // Concurrent changes are caught on the version being changed (KbArticleVersion.Version).
}

/// <summary>
/// One version of an article: bilingual title, summary and Markdown body (LANG-5). An article
/// may be published with one language; the missing one is reported (product owner decision
/// 2026-11-24). Every published version is kept as history.
/// </summary>
public sealed class KbArticleVersion : ITenantOwned, IAuditedEntity
{
    /// <summary>Maximum length of each language of <see cref="Title"/>.</summary>
    public const int TitleMaxLength = 200;

    /// <summary>Maximum length of each language of <see cref="Summary"/>.</summary>
    public const int SummaryMaxLength = 500;

    /// <summary>Maximum length of each language of <see cref="Body"/> (characters of Markdown).</summary>
    public const int BodyMaxLength = 100_000;

    /// <summary>Maximum length of <see cref="ChangeNote"/> and <see cref="ReviewComment"/>.</summary>
    public const int NoteMaxLength = 1000;

    /// <summary>Most keywords per version.</summary>
    public const int MaxKeywords = 20;

    /// <summary>Maximum length of a keyword.</summary>
    public const int KeywordMaxLength = 50;

    /// <summary>Unique id (UUID version 7).</summary>
    public Guid Id { get; init; } = Guid.CreateVersion7();

    /// <inheritdoc />
    public Guid TenantId { get; set; }

    /// <summary>The article.</summary>
    public Guid ArticleId { get; init; }

    /// <summary>1 for the first version, then counting up.</summary>
    public int Number { get; init; }

    /// <summary>Where the version stands.</summary>
    public ArticleVersionState State { get; set; } = ArticleVersionState.Draft;

    /// <summary>Title in English and Arabic (at least one).</summary>
    public required LocalizedText Title { get; set; }

    /// <summary>Short summary shown in lists and search results.</summary>
    public LocalizedText Summary { get; set; } = new(null, null);

    /// <summary>The article text in Markdown, in English and Arabic. Never rendered as raw HTML.</summary>
    [AuditLengthOnly]
    public LocalizedText Body { get; set; } = new(null, null);

    /// <summary>Search keywords, in either language.</summary>
    public string[] Keywords { get; set; } = [];

    /// <summary>Visibility this version asks for.</summary>
    public ArticleVisibility Visibility { get; set; } = ArticleVisibility.Internal;

    /// <summary>Category this version asks for.</summary>
    public Guid? CategoryId { get; set; }

    /// <summary>What changed, written by the author.</summary>
    public string? ChangeNote { get; set; }

    /// <summary>Who created the version.</summary>
    public Guid? CreatedById { get; init; }

    /// <summary>Who last changed its content.</summary>
    public Guid? LastEditedById { get; set; }

    /// <summary>Who submitted it for review.</summary>
    public Guid? SubmittedById { get; set; }

    /// <summary>When it was submitted.</summary>
    public DateTimeOffset? SubmittedAt { get; set; }

    /// <summary>Who approved or rejected it.</summary>
    public Guid? ReviewedById { get; set; }

    /// <summary>When it was approved or rejected.</summary>
    public DateTimeOffset? ReviewedAt { get; set; }

    /// <summary>The reviewer's comment (required when sending it back).</summary>
    public string? ReviewComment { get; set; }

    /// <summary>When it was published.</summary>
    public DateTimeOffset? PublishedAt { get; set; }

    /// <summary>When it was created.</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>When it was last changed.</summary>
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Optimistic concurrency token (PostgreSQL <c>xmin</c>).</summary>
    public uint Version { get; init; }
}

/// <summary>
/// An answer to "Was this helpful?": one per signed-in user, or per device for visitors
/// (identified by a random id the browser keeps, stored only as a hash). People can change it.
/// </summary>
public sealed class KbFeedback : ITenantOwned, IAuditedEntity
{
    /// <summary>Maximum length of <see cref="Comment"/>.</summary>
    public const int CommentMaxLength = 1000;

    /// <summary>Unique id (UUID version 7).</summary>
    public Guid Id { get; init; } = Guid.CreateVersion7();

    /// <inheritdoc />
    public Guid TenantId { get; set; }

    /// <summary>The article.</summary>
    public Guid ArticleId { get; init; }

    /// <summary>The published version that was read.</summary>
    public Guid? VersionId { get; set; }

    /// <summary>The signed-in user who answered.</summary>
    public Guid? UserId { get; init; }

    /// <summary>SHA-256 (hexadecimal) of a visitor's device id.</summary>
    public string? VisitorHash { get; init; }

    /// <summary>Yes or no.</summary>
    public bool Helpful { get; set; }

    /// <summary>Optional comment; may hold personal data, so it is masked in the audit log.</summary>
    [SensitiveData]
    public string? Comment { get; set; }

    /// <summary>When it was first given.</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>When it was last changed.</summary>
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>One viewer's view of an article on one day (UTC), so views are counted once per viewer and day.</summary>
public sealed class KbArticleView : ITenantOwned
{
    /// <inheritdoc />
    public Guid TenantId { get; set; }

    /// <summary>The article.</summary>
    public Guid ArticleId { get; init; }

    /// <summary>SHA-256 (hexadecimal) of the user id or the visitor's device id.</summary>
    public required string ViewerHash { get; init; }

    /// <summary>The day.</summary>
    public DateOnly Day { get; init; }
}
