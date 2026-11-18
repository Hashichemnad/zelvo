using System.Text.Json.Nodes;

namespace Zelvo.Search;

/// <summary>
/// Settings, analysers and mapping of a tenant's search index (LANG-9). Changing anything here
/// needs a new <see cref="SchemaVersion"/>: the worker then builds a new index for each tenant from
/// the old one and switches the tenant's alias to it (see <see cref="SearchIndexManager"/>).
/// </summary>
/// <remarks>
/// Arabic text is normalised so that spelling variants match:
/// <list type="bullet">
/// <item>diacritics (tashkeel) and tatweel are ignored;</item>
/// <item>alef forms أ إ آ ٱ match ا, teh marbuta ة matches ه, alef maqsura ى matches ي, hamza on a carrier (ؤ ئ) matches the bare carrier;</item>
/// <item>Persian and Urdu letter forms (ی ک ہ) match their Arabic equivalents, presentation forms (ﻻ) match the plain letters;</item>
/// <item>Arabic-Indic digits match Western digits;</item>
/// <item>the Arabic analyser also removes common prefixes and suffixes (ال, ب, ة …), so word forms match.</item>
/// </list>
/// English text is folded (café = cafe), stemmed lightly (printers = printer) and possessives are removed.
/// </remarks>
public static class SearchIndexDefinition
{
    /// <summary>Version of the mapping and analysers below.</summary>
    public const int SchemaVersion = 1;

    /// <summary>Analyser for Arabic text: normalisation, stop words and light stemming.</summary>
    public const string ArabicAnalyzer = "zelvo_arabic";

    /// <summary>Analyser for English text: folding, stop words and light stemming.</summary>
    public const string EnglishAnalyzer = "zelvo_english";

    /// <summary>Analyser for text in either language: normalisation and folding, no stemming.</summary>
    public const string FoldedAnalyzer = "zelvo_folded";

    /// <summary>Index-time analyser for search-as-you-type on titles (word prefixes).</summary>
    public const string PrefixAnalyzer = "zelvo_prefix";

    /// <summary>The alias every read and write uses for <paramref name="tenantId"/>.</summary>
    public static string AliasFor(string prefix, Guid tenantId) => $"{prefix}-search-{tenantId:D}";

    /// <summary>The physical index behind the alias for one schema version.</summary>
    public static string IndexFor(string prefix, Guid tenantId, int schemaVersion) =>
        $"{AliasFor(prefix, tenantId)}-v{schemaVersion}";

    /// <summary>The schema version of a physical index name, or null.</summary>
    public static int? VersionOf(string indexName)
    {
        var at = indexName.LastIndexOf("-v", StringComparison.Ordinal);
        return at >= 0 && int.TryParse(indexName.AsSpan(at + 2), out var version) ? version : null;
    }

    /// <summary>The request body that creates an index of the current version.</summary>
    public static JsonObject CreateIndexBody(int replicas)
    {
        // Letters that the built-in Arabic normaliser does not cover.
        var letterMappings = new JsonArray(
            "ٱ => ا",   // alef wasla
            "ی => ي",   // Farsi yeh
            "ې => ي",
            "ک => ك",   // keheh
            "ڪ => ك",
            "ہ => ه",   // heh goal
            "ە => ه",
            "ھ => ه",   // heh doachashmee
            "ۀ => ه",
            "ؤ => و",   // hamza on waw
            "ئ => ي");  // hamza on yeh

        return new JsonObject
        {
            ["settings"] = new JsonObject
            {
                ["index"] = new JsonObject
                {
                    ["number_of_shards"] = 1,
                    ["number_of_replicas"] = replicas,
                    // Deletes are remembered for an hour, so a late, older update cannot bring
                    // a deleted document back (versions are checked against the tombstone).
                    ["gc_deletes"] = "1h",
                    ["max_ngram_diff"] = 20,
                },
                ["analysis"] = new JsonObject
                {
                    ["char_filter"] = new JsonObject
                    {
                        ["zelvo_letters"] = new JsonObject { ["type"] = "mapping", ["mappings"] = letterMappings },
                    },
                    ["filter"] = new JsonObject
                    {
                        ["zelvo_arabic_stop"] = new JsonObject { ["type"] = "stop", ["stopwords"] = "_arabic_" },
                        ["zelvo_arabic_stem"] = new JsonObject { ["type"] = "arabic_stem" },
                        ["zelvo_english_stop"] = new JsonObject { ["type"] = "stop", ["stopwords"] = "_english_" },
                        ["zelvo_english_stem"] = new JsonObject { ["type"] = "stemmer", ["language"] = "light_english" },
                        ["zelvo_english_possessive"] = new JsonObject { ["type"] = "stemmer", ["language"] = "possessive_english" },
                        ["zelvo_prefixes"] = new JsonObject { ["type"] = "edge_ngram", ["min_gram"] = 1, ["max_gram"] = 20 },
                    },
                    ["analyzer"] = new JsonObject
                    {
                        [ArabicAnalyzer] = Analyzer("icu_tokenizer", "icu_folding", "arabic_normalization", "zelvo_arabic_stop", "zelvo_arabic_stem"),
                        [EnglishAnalyzer] = Analyzer("standard", "zelvo_english_possessive", "icu_folding", "arabic_normalization", "zelvo_english_stop", "zelvo_english_stem"),
                        [FoldedAnalyzer] = Analyzer("icu_tokenizer", "icu_folding", "arabic_normalization"),
                        [PrefixAnalyzer] = Analyzer("icu_tokenizer", "icu_folding", "arabic_normalization", "zelvo_prefixes"),
                    },
                },
            },
            ["mappings"] = new JsonObject
            {
                ["dynamic"] = "strict",
                ["properties"] = new JsonObject
                {
                    ["tenantId"] = Keyword(),
                    ["type"] = Keyword(),
                    ["id"] = Keyword(),
                    ["readers"] = Keyword(),
                    ["outboxId"] = new JsonObject { ["type"] = "long" },
                    ["updatedAt"] = new JsonObject { ["type"] = "date" },
                    ["url"] = new JsonObject { ["type"] = "keyword", ["index"] = false },
                    ["attributes"] = new JsonObject { ["type"] = "flat_object" },
                    ["title"] = Bilingual(withPrefix: true),
                    ["body"] = Bilingual(withPrefix: false),
                    ["text"] = AnyLanguage(),
                    ["keywords"] = AnyLanguage(),
                },
            },
        };
    }

    /// <summary>Fields a free-text query searches, with their boosts.</summary>
    internal static IReadOnlyList<string> QueryFields { get; } =
    [
        "title.en^4", "title.ar^4", "title.en.folded^5", "title.ar.folded^5", "title.en.prefix", "title.ar.prefix",
        "keywords^3", "keywords.ar^2", "keywords.en^2",
        "body.en", "body.ar", "body.en.folded^1.5", "body.ar.folded^1.5",
        "text", "text.ar", "text.en",
    ];

    /// <summary>Fields analysed with <see cref="FoldedAnalyzer"/>, searched together for mixed-language queries.</summary>
    internal static IReadOnlyList<string> FoldedFields { get; } =
        ["title.en.folded^3", "title.ar.folded^3", "keywords^2", "body.en.folded", "body.ar.folded", "text"];

    /// <summary>Fields shown as highlighted snippets.</summary>
    internal static IReadOnlyList<string> HighlightFields { get; } = ["title.en", "title.ar", "body.en", "body.ar", "text"];

    private static JsonObject Analyzer(string tokenizer, params string[] filters) => new()
    {
        ["type"] = "custom",
        ["char_filter"] = new JsonArray("zelvo_letters"),
        ["tokenizer"] = tokenizer,
        ["filter"] = new JsonArray([.. filters.Select(f => (JsonNode)f)]),
    };

    private static JsonObject Keyword() => new() { ["type"] = "keyword" };

    private static JsonObject Text(string analyzer, JsonObject? fields = null)
    {
        var text = new JsonObject { ["type"] = "text", ["analyzer"] = analyzer };
        if (fields is not null)
        {
            text["fields"] = fields;
        }

        return text;
    }

    private static JsonObject LanguageField(string analyzer, bool withPrefix)
    {
        var fields = new JsonObject { ["folded"] = Text(FoldedAnalyzer) };
        if (withPrefix)
        {
            var prefix = Text(PrefixAnalyzer);
            prefix["search_analyzer"] = FoldedAnalyzer;
            fields["prefix"] = prefix;
        }

        return Text(analyzer, fields);
    }

    private static JsonObject Bilingual(bool withPrefix) => new()
    {
        ["properties"] = new JsonObject
        {
            ["en"] = LanguageField(EnglishAnalyzer, withPrefix),
            ["ar"] = LanguageField(ArabicAnalyzer, withPrefix),
        },
    };

    private static JsonObject AnyLanguage() =>
        Text(FoldedAnalyzer, new JsonObject { ["ar"] = Text(ArabicAnalyzer), ["en"] = Text(EnglishAnalyzer) });
}
