using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Zelvo.Data;

namespace Zelvo.Api.Problems;

/// <summary>A step of the 5 Whys: the question and its answer.</summary>
/// <param name="Why">Why did it happen?</param>
/// <param name="Because">Because…</param>
public sealed record WhyStep(string Why, string? Because);

/// <summary>The 5 Whys (task H1): the problem statement, up to seven why / because steps and the conclusion.</summary>
public sealed record FiveWhys(string? Statement, IReadOnlyList<WhyStep> Steps, string? Conclusion)
{
    /// <summary>Most steps (five is the method's name, not a limit).</summary>
    public const int MaxSteps = 7;

    /// <summary>Longest text of one field.</summary>
    public const int TextMaxLength = 2000;

    /// <summary>Nothing written yet.</summary>
    public static FiveWhys Empty { get; } = new(null, [], null);
}

/// <summary>A cause on the fishbone, under one of the template's categories.</summary>
/// <param name="Category">Key of a category of the tenant's template.</param>
/// <param name="Text">The cause.</param>
/// <param name="Root">Marked as (part of) the root cause.</param>
public sealed record FishboneCause(string Category, string Text, bool Root);

/// <summary>The fishbone (Ishikawa) diagram (task H1): the effect and causes by category.</summary>
public sealed record Fishbone(string? Effect, IReadOnlyList<FishboneCause> Causes)
{
    /// <summary>Most causes.</summary>
    public const int MaxCauses = 60;

    /// <summary>Longest text of one cause or the effect.</summary>
    public const int TextMaxLength = 1000;

    /// <summary>Nothing written yet.</summary>
    public static Fishbone Empty { get; } = new(null, []);
}

/// <summary>Reading, checking and storing the root-cause tools as JSON.</summary>
internal static class ProblemAnalysis
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.Never };

    public static FiveWhys ReadFiveWhys(string json) => Read(json, FiveWhys.Empty) is { } value ? value with { Steps = value.Steps ?? [] } : FiveWhys.Empty;

    public static Fishbone ReadFishbone(string json) => Read(json, Fishbone.Empty) is { } value ? value with { Causes = value.Causes ?? [] } : Fishbone.Empty;

    public static string Write<T>(T value) => JsonSerializer.Serialize(value, Json);

    /// <summary>Trimmed copy, or the reason it is refused.</summary>
    public static (FiveWhys? Value, string? Error) Check(FiveWhys input)
    {
        var steps = (input.Steps ?? []).Select(s => new WhyStep(s?.Why?.Trim() ?? "", Trim(s?.Because))).ToList();
        if (steps.Count > FiveWhys.MaxSteps || steps.Any(s => s.Why.Length == 0))
        {
            return (null, $"Give up to {FiveWhys.MaxSteps} steps, each with its question.");
        }

        var value = new FiveWhys(Trim(input.Statement), steps, Trim(input.Conclusion));
        var texts = new[] { value.Statement, value.Conclusion }.Concat(steps.SelectMany(s => new[] { s.Why, s.Because }));
        return texts.Any(t => t?.Length > FiveWhys.TextMaxLength) ? (null, $"Each text is at most {FiveWhys.TextMaxLength} characters.") : (value, null);
    }

    /// <summary>Trimmed copy, or the reason it is refused; categories must be active keys of the template, or kept from before.</summary>
    public static (Fishbone? Value, string? Error) Check(Fishbone input, ProblemSettingsValue settings, Fishbone current)
    {
        var causes = (input.Causes ?? []).Select(c => new FishboneCause(c?.Category?.Trim() ?? "", c?.Text?.Trim() ?? "", c?.Root ?? false)).ToList();
        if (causes.Count > Fishbone.MaxCauses || causes.Any(c => c.Text.Length is 0 or > Fishbone.TextMaxLength))
        {
            return (null, $"Give up to {Fishbone.MaxCauses} causes of at most {Fishbone.TextMaxLength} characters.");
        }

        var allowed = settings.FishboneCategories.Where(c => c.Active).Select(c => c.Key).Concat(current.Causes.Select(c => c.Category)).ToHashSet(StringComparer.Ordinal);
        if (causes.Any(c => !allowed.Contains(c.Category)))
        {
            return (null, "Put each cause under an active category of the fishbone template.");
        }

        var effect = Trim(input.Effect);
        return effect?.Length > Fishbone.TextMaxLength ? (null, $"The effect is at most {Fishbone.TextMaxLength} characters.") : (new Fishbone(effect, causes), null);
    }

    /// <summary>Whether any analysis puts a cause under <paramref name="category"/>.</summary>
    public static Task<bool> CategoryUsedAsync(ZelvoDbContext db, string category, CancellationToken cancellationToken) =>
        db.Database.SqlQuery<bool>($"""
            SELECT EXISTS (SELECT 1 FROM problems, jsonb_array_elements(coalesce(fishbone -> 'causes', '[]'::jsonb)) c WHERE c ->> 'category' = {category}) AS "Value"
            """).SingleAsync(cancellationToken);

    private static T? Read<T>(string json, T fallback)
        where T : class
    {
        try
        {
            return json is null or "{}" ? fallback : JsonSerializer.Deserialize<T>(json, Json) ?? fallback;
        }
        catch (JsonException)
        {
            return fallback;
        }
    }

    private static string? Trim(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
