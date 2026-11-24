using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Zelvo.Api.Identity;
using Zelvo.Api.Settings;
using Zelvo.Data;
using Zelvo.Data.Knowledge;
using Zelvo.Data.Localization;
using Zelvo.Search;

namespace Zelvo.Api.Knowledge;

/// <summary>Tenant settings of the knowledge base (module 7), changed in Settings → Knowledge.</summary>
internal static partial class KnowledgeSettings
{
    /// <summary>
    /// Whether visitors who are not signed in can read and find external articles (product owner
    /// decision 2026-11-24: on by default, like a public help centre).
    /// </summary>
    public static SettingDefinition<bool> PublicAccess { get; } = new("knowledge.publicAccess", true, Permissions.SettingsManage);

    /// <summary>Prefix of article numbers, for example <c>KB</c> in <c>KB-000123</c>.</summary>
    public static SettingDefinition<string> NumberPrefix { get; } = new("knowledge.numberPrefix", "KB", Permissions.SettingsManage, IsPrefix);

    public static IReadOnlyList<SettingDefinition> All { get; } = [PublicAccess, NumberPrefix];

    /// <summary>1–10 capital letters or digits, starting with a letter.</summary>
    public static bool IsPrefix(string? prefix) => prefix is not null && PrefixPattern().IsMatch(prefix);

    [GeneratedRegex("^[A-Z][A-Z0-9]{0,9}$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 200)]
    private static partial Regex PrefixPattern();
}

/// <summary>Who may do what with articles: the rules every endpoint applies.</summary>
internal static class KnowledgeAccess
{
    /// <summary>Holds any of the knowledge staff permissions (sees the staff list and drafts).</summary>
    public static bool IsStaff(CurrentUser user) =>
        user.Has(Permissions.KnowledgeRead) || user.Has(Permissions.KnowledgeWrite) || user.Has(Permissions.KnowledgePublish);

    /// <summary>Whether the caller may read the published version of <paramref name="article"/>.</summary>
    public static bool CanReadPublished(CurrentUser user, KbArticle article, bool publicAccess) =>
        article is { State: ArticleState.Published, PublishedVersionId: not null }
        && (article.Visibility == ArticleVisibility.External
            ? user.IsSignedIn || publicAccess
            : user.Has(Permissions.KnowledgeRead) || user.Has(Permissions.KnowledgePublish));

    /// <summary>
    /// Four-eyes rule (product owner decision 2026-11-24): the reviewer is neither who created the
    /// version nor who last changed it.
    /// </summary>
    public static bool MayReview(CurrentUser user, KbArticleVersion version) =>
        user.Has(Permissions.KnowledgePublish) && user.Account is { } account
        && version.CreatedById != account.Id && version.LastEditedById != account.Id;
}

/// <summary>Articles in the search index (A4), with readers that follow visibility and state.</summary>
internal static partial class KnowledgeSearch
{
    /// <summary>Document type.</summary>
    public const string Type = "article";

    /// <summary>Who finds what.</summary>
    /// <param name="ReadRoles">Roles with <c>knowledge.read</c> or <c>knowledge.publish</c>: published internal articles.</param>
    /// <param name="AuthorRoles">Roles with <c>knowledge.write</c> or <c>knowledge.publish</c>: articles not yet published.</param>
    /// <param name="PublicAccess">Visitors may find external articles.</param>
    public sealed record Audience(IReadOnlyList<string> ReadRoles, IReadOnlyList<string> AuthorRoles, bool PublicAccess);

    public static async Task<Audience> AudienceAsync(ZelvoDbContext db, TenantSettingsStore settings, CancellationToken cancellationToken)
    {
        var grants = await db.Roles.Select(r => new { r.Key, Permissions = r.Permissions.Select(p => p.Permission).ToList() })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var read = grants.Where(r => r.Permissions.Contains(Permissions.KnowledgeRead) || r.Permissions.Contains(Permissions.KnowledgePublish))
            .Select(r => r.Key).Order(StringComparer.Ordinal).ToList();
        var authors = grants.Where(r => r.Permissions.Contains(Permissions.KnowledgeWrite) || r.Permissions.Contains(Permissions.KnowledgePublish))
            .Select(r => r.Key).Order(StringComparer.Ordinal).ToList();
        return new Audience(read, authors, await settings.GetAsync(KnowledgeSettings.PublicAccess, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// The document of an article: its published version when there is one (pending changes are
    /// not searchable), otherwise its working version for authors and reviewers. Null when the
    /// article must not be found (retired).
    /// </summary>
    public static SearchDocument? Document(KbArticle article, KbArticleVersion? published, KbArticleVersion? working, Audience audience)
    {
        if (article.State == ArticleState.Retired)
        {
            return null;
        }

        var version = published ?? working;
        if (version is null)
        {
            return null;
        }

        IReadOnlyList<string> readers = published is not null
            ? article.Visibility == ArticleVisibility.External
                ? [audience.PublicAccess ? SearchReaders.Public : SearchReaders.SignedIn]
                : [.. audience.ReadRoles.Select(SearchReaders.Role)]
            : [.. audience.AuthorRoles.Select(SearchReaders.Role), .. version.CreatedById is { } author ? [SearchReaders.User(author)] : Array.Empty<string>()];
        if (readers.Count == 0)
        {
            return null;
        }

        return new SearchDocument
        {
            Type = Type,
            Id = article.Id.ToString(),
            Title = version.Title,
            Body = new LocalizedText(Join(version.Summary.En, PlainText(version.Body.En)), Join(version.Summary.Ar, PlainText(version.Body.Ar))),
            Keywords = [article.Number, .. version.Keywords],
            Readers = [.. readers.Distinct(StringComparer.Ordinal)],
            Attributes = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["number"] = article.Number,
                ["state"] = article.State.ToString(),
                ["visibility"] = article.Visibility.ToString(),
                ["category"] = article.CategoryId?.ToString() ?? string.Empty,
            },
            Url = published is not null && article.Visibility == ArticleVisibility.External ? $"/help/articles/{article.Id}" : $"/knowledge/{article.Id}",
            UpdatedAt = article.UpdatedAt,
        };
    }

    /// <summary>Stages the index update of <paramref name="article"/> (before <c>SaveChanges</c>).</summary>
    public static async Task IndexAsync(ZelvoDbContext db, ISearchIndexer indexer, Audience audience, KbArticle article, CancellationToken cancellationToken)
    {
        var ids = new[] { article.PublishedVersionId, article.WorkingVersionId }.Where(i => i is not null).Select(i => i!.Value).ToList();
        var versions = await db.KbArticleVersions.Where(v => ids.Contains(v.Id)).ToListAsync(cancellationToken).ConfigureAwait(false);
        var published = versions.FirstOrDefault(v => v.Id == article.PublishedVersionId);
        var working = versions.FirstOrDefault(v => v.Id == article.WorkingVersionId);

        // Versions staged in this request are not in the database yet.
        published ??= db.ChangeTracker.Entries<KbArticleVersion>().Select(e => e.Entity).FirstOrDefault(v => v.Id == article.PublishedVersionId);
        working ??= db.ChangeTracker.Entries<KbArticleVersion>().Select(e => e.Entity).FirstOrDefault(v => v.Id == article.WorkingVersionId);
        if (Document(article, published, working, audience) is { } document)
        {
            indexer.Upsert(document);
        }
        else
        {
            indexer.Delete(Type, article.Id.ToString());
        }
    }

    /// <summary>Markdown as plain words for the index: images, link targets and markup removed.</summary>
    public static string? PlainText(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return null;
        }

        var text = ImagePattern().Replace(markdown, "$1");
        text = LinkPattern().Replace(text, "$1");
        text = MarkupPattern().Replace(text, " ");
        return WhitespacePattern().Replace(text, " ").Trim();
    }

    private static string? Join(string? a, string? b) =>
        (a, b) switch
        {
            (null, null) => null,
            (null, _) => b,
            (_, null) => a,
            _ => a + "\n" + b,
        };

    [GeneratedRegex(@"!\[([^\]]*)\]\([^)]*\)", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 500)]
    private static partial Regex ImagePattern();

    [GeneratedRegex(@"\[([^\]]*)\]\([^)]*\)", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 500)]
    private static partial Regex LinkPattern();

    [GeneratedRegex(@"[#*_`>|~]+|^\s*[-+]\s|^\s*\d+\.\s", RegexOptions.Multiline | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 500)]
    private static partial Regex MarkupPattern();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 500)]
    private static partial Regex WhitespacePattern();
}

/// <summary>Every current article of the tenant, for rebuilding the search index.</summary>
internal sealed class KnowledgeSearchSource(ZelvoDbContext db, TenantSettingsStore settings) : ISearchDocumentSource
{
    /// <inheritdoc />
    public string Type => KnowledgeSearch.Type;

    /// <inheritdoc />
    public async IAsyncEnumerable<SearchDocument> ReadAllAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var audience = await KnowledgeSearch.AudienceAsync(db, settings, cancellationToken).ConfigureAwait(false);
        var articles = await db.KbArticles.AsNoTracking().Where(a => a.State != ArticleState.Retired).OrderBy(a => a.Id)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var chunk in articles.Chunk(200))
        {
            var ids = chunk.SelectMany(a => new[] { a.PublishedVersionId, a.WorkingVersionId }).Where(i => i is not null).Select(i => i!.Value).ToList();
            var versions = await db.KbArticleVersions.AsNoTracking().Where(v => ids.Contains(v.Id)).ToDictionaryAsync(v => v.Id, cancellationToken).ConfigureAwait(false);
            foreach (var article in chunk)
            {
                var document = KnowledgeSearch.Document(article,
                    article.PublishedVersionId is { } p ? versions.GetValueOrDefault(p) : null,
                    article.WorkingVersionId is { } w ? versions.GetValueOrDefault(w) : null, audience);
                if (document is not null)
                {
                    yield return document;
                }
            }
        }
    }
}

/// <summary>Published articles that miss a language (LANG-5), for Settings → Missing translations.</summary>
internal sealed class KnowledgeMissingTranslations : IMissingTranslationSource
{
    public async Task<IReadOnlyList<MissingTranslation>> FindAsync(ZelvoDbContext db, CancellationToken cancellationToken)
    {
        var rows = await db.KbArticles.AsNoTracking()
            .Where(a => a.State == ArticleState.Published)
            .Join(db.KbArticleVersions.AsNoTracking(), a => a.PublishedVersionId, v => v.Id, (a, v) => new { a.Number, v.Title, v.Body })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var missing = new List<MissingTranslation>();
        foreach (var row in rows.OrderBy(r => r.Number, StringComparer.Ordinal))
        {
            if (!row.Title.IsComplete)
            {
                missing.Add(new MissingTranslation("kb_articles", row.Number, "title", row.Title.MissingLanguages));
            }

            if (row.Body.HasAnyValue && !row.Body.IsComplete)
            {
                missing.Add(new MissingTranslation("kb_articles", row.Number, "body", row.Body.MissingLanguages));
            }
        }

        return missing;
    }
}

/// <summary>Hashes of who viewed or answered, so device ids and user ids are not stored as such.</summary>
internal static class ViewerKeys
{
    /// <summary>Longest device id a browser may send.</summary>
    public const int MaxVisitorIdLength = 64;

    public static string ForUser(Guid tenantId, Guid userId) => Hash($"user|{tenantId:N}|{userId:N}");

    public static string ForVisitor(Guid tenantId, string visitorId) => Hash($"visitor|{tenantId:N}|{visitorId}");

    /// <summary>A device id: 16–64 letters, digits and hyphens (browsers send a random UUID).</summary>
    public static bool IsVisitorId(string? visitorId) =>
        visitorId is { Length: >= 16 and <= MaxVisitorIdLength } && visitorId.All(c => char.IsAsciiLetterOrDigit(c) || c == '-');

    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
