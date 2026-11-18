using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Zelvo.Data.Localization;

namespace Zelvo.Search;

/// <summary>
/// What a module puts in the search index for one record: bilingual title and body (LANG-5),
/// text in an unknown language (for example a requester's description), who may see it and a few
/// filterable attributes. Modules build it from their record and hand it to
/// <see cref="ISearchIndexer"/> in the same unit of work as the change.
/// </summary>
public sealed partial record SearchDocument
{
    /// <summary>Maximum length of <see cref="Type"/>.</summary>
    public const int TypeMaxLength = 50;

    /// <summary>Maximum length of <see cref="Id"/>.</summary>
    public const int IdMaxLength = 200;

    /// <summary>Maximum total length of the document's text, in characters.</summary>
    public const int TextMaxLength = 200_000;

    /// <summary>Maximum number of readers, keywords or attributes.</summary>
    public const int ListMaxCount = 100;

    /// <summary>Document type, for example <c>article</c>, <c>service</c> or <c>incident</c>.</summary>
    public required string Type { get; init; }

    /// <summary>Id of the record within its type.</summary>
    public required string Id { get; init; }

    /// <summary>Title in English and Arabic; at least one is required.</summary>
    public required LocalizedText Title { get; init; }

    /// <summary>Body in English and Arabic.</summary>
    public LocalizedText? Body { get; init; }

    /// <summary>Text whose language is not known; analysed as both Arabic and English.</summary>
    public IReadOnlyList<string> Text { get; init; } = [];

    /// <summary>Search keywords set by an administrator, in either language.</summary>
    public IReadOnlyList<string> Keywords { get; init; } = [];

    /// <summary>Who may find the document; see <see cref="SearchReaders"/>. At least one.</summary>
    public required IReadOnlyList<string> Readers { get; init; }

    /// <summary>Filterable values, for example <c>status</c> → <c>open</c>.</summary>
    public IReadOnlyDictionary<string, string> Attributes { get; init; } = new Dictionary<string, string>();

    /// <summary>Path of the record in the web app, relative, for example <c>/knowledge/123</c>.</summary>
    public string? Url { get; init; }

    /// <summary>When the record last changed.</summary>
    public DateTimeOffset? UpdatedAt { get; init; }

    /// <summary>Id of the document in the tenant's index.</summary>
    [JsonIgnore]
    public string IndexId => IndexIdOf(Type, Id);

    /// <summary>Id in the tenant's index of a document of <paramref name="type"/>.</summary>
    public static string IndexIdOf(string type, string id) => $"{type}:{id}";

    /// <summary>Whether <paramref name="type"/> is a valid document type: lower-case letters, digits and hyphens.</summary>
    public static bool IsType(string? type) => type is not null && TypePattern().IsMatch(type);

    /// <summary>Problems that make the document unusable; empty when it is valid.</summary>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();
        if (!IsType(Type))
        {
            problems.Add("Type must be 1-50 lower-case letters, digits or hyphens, starting with a letter.");
        }

        if (string.IsNullOrWhiteSpace(Id) || Id.Length > IdMaxLength)
        {
            problems.Add($"Id must be 1-{IdMaxLength} characters.");
        }

        if (Title is null || !Title.HasAnyValue)
        {
            problems.Add("Title needs an English or Arabic value.");
        }

        if (Readers is null || Readers.Count == 0 || Readers.Count > ListMaxCount || !Readers.All(SearchReaders.IsReader))
        {
            problems.Add("Readers must list 1-100 readers made with SearchReaders.");
        }

        if (Keywords.Count > ListMaxCount || Text.Count > ListMaxCount || Attributes.Count > ListMaxCount)
        {
            problems.Add("Keywords, text and attributes hold at most 100 entries each.");
        }

        if (Attributes.Any(a => !AttributePattern().IsMatch(a.Key) || a.Value is null || a.Value.Length > 200))
        {
            problems.Add("Attribute keys are camelCase (max 50); values at most 200 characters.");
        }

        if (Url is not null && (!Url.StartsWith('/') || Url.StartsWith("//", StringComparison.Ordinal) || Url.Length > 500))
        {
            problems.Add("Url must be a relative path starting with a single '/'.");
        }

        var length = (Title?.En?.Length ?? 0) + (Title?.Ar?.Length ?? 0) + (Body?.En?.Length ?? 0) + (Body?.Ar?.Length ?? 0)
            + Text.Sum(t => t?.Length ?? 0) + Keywords.Sum(k => k?.Length ?? 0);
        if (length > TextMaxLength)
        {
            problems.Add($"The document's text is longer than {TextMaxLength} characters.");
        }

        return problems;
    }

    [GeneratedRegex("^[a-z][a-z0-9-]{0,49}$")]
    private static partial Regex TypePattern();

    [GeneratedRegex("^[a-z][a-zA-Z0-9]{0,49}$")]
    private static partial Regex AttributePattern();
}
