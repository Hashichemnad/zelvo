using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Zelvo.Data.Localization;

namespace Zelvo.Ai.Features;

/// <summary>Small text helpers shared by the AI features (task E5).</summary>
public static class AiText
{
    /// <summary>Whether <paramref name="text"/> contains Arabic letters.</summary>
    public static bool ContainsArabic(string? text) => text?.Any(c => c is >= '؀' and <= 'ۿ' or >= 'ݐ' and <= 'ݿ') ?? false;

    /// <summary>The language a text is mostly written in: <c>ar</c> when it has more Arabic than Latin letters.</summary>
    public static string LanguageOf(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return LocalizedText.English;
        }

        var arabic = text.Count(c => c is >= '؀' and <= 'ۿ');
        var latin = text.Count(c => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z');
        return arabic > latin ? LocalizedText.Arabic : LocalizedText.English;
    }

    /// <summary>The name of a language for instructions to the model.</summary>
    public static string LanguageName(string language) =>
        language == LocalizedText.Arabic ? "Arabic (formal Modern Standard Arabic)" : "English";

    /// <summary>
    /// Words for comparing texts: lower case, Arabic diacritics and tatweel removed, alef, teh
    /// marbuta, alef maqsura and hamza forms unified, Arabic-Indic digits as Western digits, and
    /// words of one or two letters dropped.
    /// </summary>
    public static HashSet<string> Words(string? text)
    {
        var words = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(text))
        {
            return words;
        }

        var builder = new StringBuilder(text.Length);
        foreach (var c in text.ToLowerInvariant())
        {
            switch (c)
            {
                case >= 'ً' and <= 'ٟ' or 'ٰ' or 'ـ':
                    continue;
                case 'أ' or 'إ' or 'آ' or 'ٱ':
                    builder.Append('ا');
                    break;
                case 'ة':
                    builder.Append('ه');
                    break;
                case 'ى':
                    builder.Append('ي');
                    break;
                case 'ؤ' or 'ئ':
                    builder.Append('ء');
                    break;
                case >= '٠' and <= '٩':
                    builder.Append((char)('0' + (c - '٠')));
                    break;
                default:
                    builder.Append(char.IsLetterOrDigit(c) ? c : ' ');
                    break;
            }
        }

        foreach (var word in builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var stem = word.Length > 4 && word.StartsWith("ال", StringComparison.Ordinal) ? word[2..] : word;
            if (stem.Length > 2 && !StopWords.Contains(stem))
            {
                words.Add(stem);
            }
        }

        return words;
    }

    /// <summary>How much two sets of words overlap (Jaccard index, 0–1).</summary>
    public static double Overlap(IReadOnlySet<string> a, IReadOnlySet<string> b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        if (a.Count == 0 || b.Count == 0)
        {
            return 0;
        }

        var shared = a.Count(b.Contains);
        return (double)shared / (a.Count + b.Count - shared);
    }

    /// <summary>SHA-256 of a text, lower-case hex.</summary>
    public static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    /// <summary>Cuts <paramref name="text"/> to <paramref name="max"/> characters.</summary>
    public static string Cut(string text, int max) => text.Length <= max ? text : string.Concat(text.AsSpan(0, max - 1), "…");

    /// <summary>A string property of a JSON answer, trimmed; null when missing or empty.</summary>
    public static string? Text(JsonElement json, string name) =>
        json.ValueKind == JsonValueKind.Object && json.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
        && value.GetString()?.Trim() is { Length: > 0 } text ? text : null;

    /// <summary>An integer property of a JSON answer (also given as a string); null when missing.</summary>
    public static int? Number(JsonElement json, string name)
    {
        if (json.ValueKind != JsonValueKind.Object || !json.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetDouble(out var d) => (int)Math.Round(d),
            JsonValueKind.String when int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) => i,
            _ => null,
        };
    }

    /// <summary>A boolean property of a JSON answer; false when missing.</summary>
    public static bool Bool(JsonElement json, string name) =>
        json.ValueKind == JsonValueKind.Object && json.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    /// <summary>The strings of an array property of a JSON answer.</summary>
    public static IReadOnlyList<string> Strings(JsonElement json, string name) =>
        json.ValueKind == JsonValueKind.Object && json.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
            ? [.. value.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!.Trim()).Where(s => s.Length > 0)]
            : [];

    /// <summary>A short reference for a list item given to the model, for example <c>c3</c>.</summary>
    public static string Ref(char prefix, int index) => prefix + (index + 1).ToString(CultureInfo.InvariantCulture);

    // Common words that say nothing about the problem, in English and Arabic.
    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "the", "and", "for", "with", "not", "can", "cannot", "has", "have", "are", "was", "this", "that", "from", "our", "your",
        "you", "but", "all", "any", "please", "hello", "thanks", "thank", "since", "when", "into", "out", "its", "it's", "does", "doesn't",
        "من", "في", "على", "الى", "الي", "عن", "مع", "هذا", "هذه", "ذلك", "التي", "الذي", "لا", "لم", "لن", "ما", "او", "ان", "كان", "يوجد",
        "شكرا", "مرحبا", "السلام", "عليكم", "لو", "سمحت", "الرجاء", "ارجو",
    };
}
