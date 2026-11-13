using System.Text.Json;
using System.Text.Json.Serialization;

namespace Zelvo.Data.Auditing;

/// <summary>JSON settings for audit change and metadata documents.</summary>
internal static class AuditJson
{
    /// <summary>Placeholder stored instead of a sensitive value.</summary>
    public const string Masked = "[masked]";

    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static string? Serialize<T>(T? value) =>
        value is null ? null : JsonSerializer.Serialize(value, Options);
}
