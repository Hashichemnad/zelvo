using System.ComponentModel.DataAnnotations;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http.HttpResults;
using Zelvo.Api.Conventions;
using Zelvo.Api.Identity;
using Zelvo.Api.Knowledge;
using Zelvo.Api.Settings;
using Zelvo.Api.Tenancy;
using Zelvo.Api.Voice;
using Zelvo.Tickets;

namespace Zelvo.Api.Problems;

/// <summary>A category of the fishbone template (people, process, technology…). Inactive ones stay on old analyses but cannot be chosen.</summary>
public sealed partial record FishboneCategory(string Key, SettingText Name, bool Active)
{
    /// <summary>Whether <paramref name="key"/> is a valid key.</summary>
    public static bool IsKey(string? key) => key is not null && KeyPattern().IsMatch(key);

    [GeneratedRegex("^[a-z0-9]([a-z0-9-]{0,48}[a-z0-9])?$")]
    private static partial Regex KeyPattern();
}

/// <summary>Problem management settings of a tenant (module 16, task H1), in Settings → Problems.</summary>
/// <param name="KnownErrorPrefix">Prefix of known error numbers (<c>KE-000123</c>).</param>
/// <param name="FishboneCategories">The fishbone template's categories, in order.</param>
public sealed record ProblemSettingsValue(string KnownErrorPrefix, IReadOnlyList<FishboneCategory> FishboneCategories)
{
    /// <summary>Most fishbone categories.</summary>
    public const int MaxCategories = 12;

    /// <summary>The defaults: the six categories of the classic Ishikawa template.</summary>
    public static ProblemSettingsValue Default { get; } = new("KE",
    [
        new("people", new("People", "الأفراد"), true),
        new("process", new("Process", "الإجراءات"), true),
        new("technology", new("Technology", "التقنية"), true),
        new("environment", new("Environment", "البيئة"), true),
        new("suppliers", new("Suppliers", "الموردون"), true),
        new("measurement", new("Measurement", "القياس"), true),
    ]);

    /// <summary>A valid prefix; 1–12 categories with unique keys and names, at least one active.</summary>
    public bool IsValid() =>
        KnowledgeSettings.IsPrefix(KnownErrorPrefix)
        && FishboneCategories is { Count: > 0 and <= MaxCategories }
        && FishboneCategories.All(c => c is not null && FishboneCategory.IsKey(c.Key) && c.Name is { IsValid: true })
        && FishboneCategories.Select(c => c.Key).Distinct(StringComparer.Ordinal).Count() == FishboneCategories.Count
        && FishboneCategories.Any(c => c.Active);
}

/// <summary>Settings → Problems.</summary>
internal static class ProblemSettings
{
    public static SettingDefinition<ProblemSettingsValue> Problems { get; } =
        new("problems.settings", ProblemSettingsValue.Default, Permissions.ProblemsManage, v => v.IsValid());

    public static RouteGroupBuilder MapProblemSettingsEndpoints(this RouteGroupBuilder group)
    {
        var settings = group.MapGroup("/settings/problems").WithTags("Settings").RequireTenant();
        settings.MapGet("/", GetAsync).WithName("GetProblemSettings")
            .WithSummary("Problem management settings: the known error number prefix and the fishbone template's categories. Needs problems.read.")
            .RequirePermission(Permissions.ProblemsRead).Produces<ProblemSettingsValue>();
        settings.MapPut("/", UpdateAsync).WithName("UpdateProblemSettings")
            .WithSummary("Change the problem management settings. Categories used in analyses can be made inactive but not removed. Needs problems.manage; audited.")
            .RequirePermission(Permissions.ProblemsManage).Produces<ProblemSettingsValue>().ProducesValidationProblem();
        return group;
    }

    internal static async Task<Ok<ProblemSettingsValue>> GetAsync(TenantSettingsStore store, CancellationToken cancellationToken) =>
        TypedResults.Ok(await store.GetAsync(Problems, cancellationToken));

    internal static async Task<Results<Ok<ProblemSettingsValue>, ValidationProblem>> UpdateAsync(
        UpdateProblemSettingsRequest request, TenantSettingsStore store, Data.ZelvoDbContext db, CancellationToken cancellationToken)
    {
        var current = await store.GetAsync(Problems, cancellationToken);
        var value = new ProblemSettingsValue(
            request.KnownErrorPrefix?.Trim() ?? current.KnownErrorPrefix,
            request.FishboneCategories is null
                ? current.FishboneCategories
                : [.. request.FishboneCategories.Select(c => new FishboneCategory(c.Key?.Trim() ?? "", new SettingText(Trim(c.Name?.En), Trim(c.Name?.Ar)), c.Active ?? true))]);
        if (!value.IsValid())
        {
            return UserEndpoints.Validation("fishboneCategories",
                "Give a prefix of 1–10 capital letters or digits starting with a letter, and 1–12 fishbone categories with unique keys (lower-case letters, digits and hyphens) and names, at least one active.");
        }

        var removed = current.FishboneCategories.Select(c => c.Key).Except(value.FishboneCategories.Select(c => c.Key), StringComparer.Ordinal).ToList();
        foreach (var key in removed)
        {
            if (await ProblemAnalysis.CategoryUsedAsync(db, key, cancellationToken))
            {
                return UserEndpoints.Validation("fishboneCategories", $"The category '{key}' is used in analyses; make it inactive instead.");
            }
        }

        await store.SetAsync(Problems, value, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Ok(value);
    }

    private static string? Trim(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>Changes to Settings → Problems.</summary>
public sealed class UpdateProblemSettingsRequest
{
    /// <summary>Known error number prefix.</summary>
    [StringLength(10)]
    public string? KnownErrorPrefix { get; init; }

    /// <summary>The fishbone template's categories, in order (replaces the list).</summary>
    [MaxLength(ProblemSettingsValue.MaxCategories)]
    public IReadOnlyList<FishboneCategoryInput>? FishboneCategories { get; init; }
}

/// <summary>A fishbone category.</summary>
public sealed class FishboneCategoryInput
{
    /// <summary>Key: lower-case letters, digits and hyphens.</summary>
    [StringLength(50)]
    public string? Key { get; init; }

    /// <summary>Name in English and Arabic.</summary>
    public TextInput? Name { get; init; }

    /// <summary>Whether it can be chosen.</summary>
    public bool? Active { get; init; }
}
