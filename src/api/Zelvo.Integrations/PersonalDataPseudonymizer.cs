using System.Text;
using System.Text.RegularExpressions;

namespace Zelvo.Integrations;

/// <summary>
/// Reversible masking for text sent to an AI provider (module 9: "personal data masked before
/// anything is sent"). Personal data is replaced by numbered placeholders such as
/// <c>[EMAIL_1]</c> or <c>[PERSON_2]</c>; the same value always gets the same placeholder within
/// one instance, so the model can still tell people apart. <see cref="Restore"/> puts the real
/// values back into the model's answer on Zelvo's side, so they never leave the installation.
/// <para>
/// Found by pattern: email addresses, Emirates ID numbers, IBANs, phone, card and account
/// numbers (as <see cref="PersonalDataMasker"/>). Names cannot be found reliably in free text,
/// so callers pass the personal values they know (the requester's name, for example) to
/// <see cref="AddKnownValue"/>. Bearer tokens and Zelvo keys are removed for good
/// (<see cref="PersonalDataMasker.Mask"/>) and never restored.
/// </para>
/// </summary>
public sealed partial class PersonalDataPseudonymizer
{
    /// <summary>Kind of a known value: a person's name.</summary>
    public const string Person = "PERSON";

    /// <summary>Shortest known value that is masked (shorter ones would match inside other words).</summary>
    public const int MinKnownValueLength = 3;

    private readonly Dictionary<string, string> _placeholderOf = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _valueOf = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _counters = new(StringComparer.Ordinal);
    private readonly List<(string Value, string Kind)> _known = [];

    /// <summary>How many different values were replaced so far.</summary>
    public int MaskedCount => _valueOf.Count;

    /// <summary>
    /// Adds a value that must not reach the provider, for example a person's name or a customer
    /// number, with the placeholder kind (upper-case letters, default <see cref="Person"/>).
    /// </summary>
    public void AddKnownValue(string? value, string kind = Person)
    {
        ArgumentNullException.ThrowIfNull(kind);
        if (!KindPattern().IsMatch(kind))
        {
            throw new ArgumentException("Kinds are upper-case letters and underscores.", nameof(kind));
        }

        var trimmed = value?.Trim();
        if (trimmed is null || trimmed.Length < MinKnownValueLength || _known.Any(k => string.Equals(k.Value, trimmed, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        _known.Add((trimmed, kind));

        // A full name is also masked word by word ("Fatima Al Mansoori" → "Fatima", "Mansoori").
        if (kind == Person)
        {
            foreach (var part in trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (part.Length >= MinKnownValueLength && !NameParticles.Contains(part)
                    && !_known.Any(k => string.Equals(k.Value, part, StringComparison.OrdinalIgnoreCase)))
                {
                    _known.Add((part, kind));
                }
            }
        }
    }

    /// <summary>Replaces personal data in <paramref name="text"/> with placeholders.</summary>
    public string Mask(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        // Text that already looks like a placeholder is changed, so that it cannot be swapped for a real value by Restore.
        var masked = PlaceholderPattern().Replace(text, m => "(" + m.Value[1..^1] + ")");

        masked = PersonalDataMasker.BearerPattern().Replace(masked, "Bearer " + PersonalDataMasker.Mask);
        masked = PersonalDataMasker.ApiKeyPattern().Replace(masked, PersonalDataMasker.Mask);
        masked = PersonalDataMasker.EmailPattern().Replace(masked, m => PlaceholderFor(m.Value, "EMAIL"));
        masked = PersonalDataMasker.EmiratesIdPattern().Replace(masked, m => PlaceholderFor(m.Value, "EMIRATES_ID"));
        masked = PersonalDataMasker.IbanPattern().Replace(masked, m => PlaceholderFor(m.Value, "IBAN"));
        var ids = PersonalDataMasker.UuidPattern().Matches(masked).Select(m => (m.Index, End: m.Index + m.Length)).ToList();
        masked = PersonalDataMasker.LongNumberPattern().Replace(masked, m =>
            !ids.Any(id => m.Index < id.End && m.Index + m.Length > id.Index) && PersonalDataMasker.IsPersonalNumber(m.Value)
                ? PlaceholderFor(m.Value, m.Value.TrimStart().StartsWith('+') ? "PHONE" : "NUMBER")
                : m.Value);

        // Known values last, so that a name inside an email address goes with the address.
        // Longest first, so that a full name wins over its parts.
        foreach (var (value, kind) in _known.OrderByDescending(k => k.Value.Length))
        {
            masked = ReplaceWord(masked, value, kind);
        }

        return masked;
    }

    /// <summary>Puts the real values back for the placeholders this instance made; other text is left as it is.</summary>
    public string Restore(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return _valueOf.Count == 0
            ? text
            : PlaceholderPattern().Replace(text, m => _valueOf.TryGetValue(m.Value, out var value) ? value : m.Value);
    }

    private string ReplaceWord(string text, string value, string kind)
    {
        var builder = new StringBuilder(text.Length);
        var start = 0;
        while (start < text.Length)
        {
            var index = text.IndexOf(value, start, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                break;
            }

            var end = index + value.Length;
            var wholeWord = (index == 0 || !char.IsLetterOrDigit(text[index - 1])) && (end == text.Length || !char.IsLetterOrDigit(text[end]));
            builder.Append(text, start, index - start);
            builder.Append(wholeWord ? PlaceholderFor(text[index..end], kind) : text[index..end]);
            start = end;
        }

        builder.Append(text, start, text.Length - start);
        return builder.ToString();
    }

    private string PlaceholderFor(string value, string kind)
    {
        if (_placeholderOf.TryGetValue(value, out var existing))
        {
            return existing;
        }

        var number = _counters.GetValueOrDefault(kind) + 1;
        _counters[kind] = number;
        var placeholder = $"[{kind}_{number}]";
        _placeholderOf[value] = placeholder;
        _valueOf[placeholder] = value;
        return placeholder;
    }

    // Parts of names that are not personal on their own.
    private static readonly HashSet<string> NameParticles = new(StringComparer.OrdinalIgnoreCase)
    {
        "Al", "El", "Bin", "Bint", "Ibn", "Abu", "Umm", "Von", "Van", "Der", "Mr", "Mrs", "Ms", "Dr",
        "آل", "بن", "بنت", "ابن", "أبو", "ابو", "أم",
    };

    [GeneratedRegex(@"\[[A-Z][A-Z_]*_\d+\]", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 200)]
    private static partial Regex PlaceholderPattern();

    [GeneratedRegex("^[A-Z][A-Z_]*$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 200)]
    private static partial Regex KindPattern();
}
