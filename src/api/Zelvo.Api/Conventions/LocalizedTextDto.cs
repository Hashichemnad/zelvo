using System.ComponentModel.DataAnnotations;
using Zelvo.Data.Localization;

namespace Zelvo.Api.Conventions;

/// <summary>
/// A bilingual value in API responses (LANG-5): <c>{"en": "…", "ar": "…", "missing": ["ar"]}</c>.
/// <c>missing</c> lists the languages still to be translated.
/// </summary>
internal sealed record LocalizedTextDto(string? En, string? Ar, IReadOnlyList<string> Missing)
{
    public static LocalizedTextDto From(LocalizedText text) => new(text.En, text.Ar, text.MissingLanguages);
}

/// <summary>A bilingual value in API requests. At least one language is required.</summary>
public sealed class LocalizedTextInput : IValidatableObject
{
    /// <summary>English value.</summary>
    [StringLength(2000)]
    public string? En { get; init; }

    /// <summary>Arabic value.</summary>
    [StringLength(2000)]
    public string? Ar { get; init; }

    /// <summary>The value, with blank languages treated as missing.</summary>
    public LocalizedText ToValue() => new(En, Ar);

    /// <inheritdoc />
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (!ToValue().HasAnyValue)
        {
            yield return new ValidationResult(
                "A value in English or Arabic is required.", [nameof(En), nameof(Ar)]);
        }
    }
}
