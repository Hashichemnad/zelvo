namespace Zelvo.Data.Localization;

/// <summary>
/// A user-facing value held in both English and Arabic (LANG-5). Catalog items, categories, forms,
/// knowledge articles, email templates and SLA names use this type. Either value may be missing
/// while an administrator is still translating; <see cref="MissingLanguages"/> reports which.
/// </summary>
/// <remarks>Stored as two columns, <c>&lt;field&gt;_en</c> and <c>&lt;field&gt;_ar</c>.</remarks>
public sealed record LocalizedText
{
    /// <summary>Language code for English.</summary>
    public const string English = "en";

    /// <summary>Language code for Arabic.</summary>
    public const string Arabic = "ar";

    /// <summary>Languages every bilingual value must hold, in display order.</summary>
    public static readonly IReadOnlyList<string> Languages = [English, Arabic];

    /// <summary>Creates a value; blank strings are stored as missing.</summary>
    public LocalizedText(string? en, string? ar)
    {
        En = Normalize(en);
        Ar = Normalize(ar);
    }

    /// <summary>English value, or <see langword="null"/> when missing.</summary>
    public string? En { get; init; }

    /// <summary>Arabic value, or <see langword="null"/> when missing.</summary>
    public string? Ar { get; init; }

    /// <summary>Language codes whose value is missing, in display order.</summary>
    public IReadOnlyList<string> MissingLanguages =>
        Languages.Where(language => Get(language) is null).ToArray();

    /// <summary>Both languages are present.</summary>
    public bool IsComplete => En is not null && Ar is not null;

    /// <summary>At least one language is present.</summary>
    public bool HasAnyValue => En is not null || Ar is not null;

    /// <summary>Value in <paramref name="language"/>, or <see langword="null"/> when missing.</summary>
    public string? Get(string language) => language switch
    {
        English => En,
        Arabic => Ar,
        _ => throw new ArgumentOutOfRangeException(nameof(language), language, "Supported languages are 'en' and 'ar'."),
    };

    /// <summary>
    /// Value in <paramref name="language"/>, falling back to the other language when it is missing,
    /// so a screen never shows an empty label.
    /// </summary>
    public string? GetOrFallback(string language) =>
        Get(language) ?? (language == Arabic ? En : Ar);

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
