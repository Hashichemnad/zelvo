using System.Text.Json;
using System.Text.Json.Serialization;
using Zelvo.Data.Localization;

namespace Zelvo.Notifications;

/// <summary>
/// A placeholder value captured with the change that caused a notification. Exactly one field is
/// set. The worker formats it for each recipient: bilingual values in their language, numbers in
/// their digits, dates in their calendar (LANG-6, LANG-8). Templates never contain formatted values.
/// </summary>
public sealed record PlaceholderValue
{
    /// <summary>Text shown as is (ticket numbers, titles as the requester wrote them, names).</summary>
    public string? Text { get; init; }

    /// <summary>English of a bilingual value.</summary>
    public string? En { get; init; }

    /// <summary>Arabic of a bilingual value.</summary>
    public string? Ar { get; init; }

    /// <summary>A whole number.</summary>
    public long? Number { get; init; }

    /// <summary>A percentage (75 for 75 %).</summary>
    public int? Percent { get; init; }

    /// <summary>A moment, shown in the tenant's time zone.</summary>
    public DateTimeOffset? Date { get; init; }

    /// <summary>A fixed term translated by Zelvo, for example <c>sla.target.response</c>.</summary>
    public string? Term { get; init; }

    /// <summary>Plain text.</summary>
    public static PlaceholderValue OfText(string? text) => new() { Text = text ?? string.Empty };

    /// <summary>A bilingual value.</summary>
    public static PlaceholderValue OfText(LocalizedText? text) => new() { En = text?.En, Ar = text?.Ar };

    /// <summary>A moment.</summary>
    public static PlaceholderValue OfDate(DateTimeOffset? date) => date is null ? OfText((string?)null) : new() { Date = date };

    /// <summary>A percentage.</summary>
    public static PlaceholderValue OfPercent(int percent) => new() { Percent = percent };

    /// <summary>A fixed term.</summary>
    public static PlaceholderValue OfTerm(string term) => new() { Term = term };
}

/// <summary>Reads and writes <see cref="Data.Notifications.Notification.Data"/>.</summary>
public static class NotificationData
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>The values as a JSON object.</summary>
    public static string Serialize(IReadOnlyDictionary<string, PlaceholderValue> values) => JsonSerializer.Serialize(values, Json);

    /// <summary>The values of a stored notification; empty when the JSON is not an object of values.</summary>
    public static IReadOnlyDictionary<string, PlaceholderValue> Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new Dictionary<string, PlaceholderValue>(StringComparer.Ordinal);
        }

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, PlaceholderValue>>(json, Json) is { } values
                ? new Dictionary<string, PlaceholderValue>(values, StringComparer.Ordinal)
                : new Dictionary<string, PlaceholderValue>(StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            return new Dictionary<string, PlaceholderValue>(StringComparer.Ordinal);
        }
    }
}
