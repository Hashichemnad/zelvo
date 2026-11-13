using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Http.HttpResults;
using Zelvo.Api.Identity;
using Zelvo.Api.Tenancy;
using Zelvo.Data;
using Zelvo.Data.Localization;

namespace Zelvo.Api.Settings;

/// <summary>Tenant settings. Each area has its own typed endpoint; A3 adds language and region.</summary>
internal static class SettingsEndpoints
{
    public static RouteGroupBuilder MapSettingsEndpoints(this RouteGroupBuilder group)
    {
        var localization = group.MapGroup("/settings/localization").WithTags("Settings").RequireTenant();

        localization.MapGet("/", GetLocalizationAsync)
            .WithName("GetLocalizationSettings")
            .WithSummary("The tenant's default language, calendar and digits. Open to anonymous visitors, whose pages use them too.")
            .AllowAnonymousAccess();

        localization.MapPut("/", UpdateLocalizationAsync)
            .WithName("UpdateLocalizationSettings")
            .WithSummary("Change the tenant's default language (LANG-1), calendar and digits (LANG-8). Users' own choices still win. Needs settings.manage; audited.")
            .RequirePermission(Permissions.SettingsManage)
            .ProducesValidationProblem();

        return group;
    }

    internal static async Task<Ok<LocalizationSettingsResponse>> GetLocalizationAsync(
        TenantSettingsStore store, CancellationToken cancellationToken) =>
        TypedResults.Ok(await LocalizationSettingsResponse.LoadAsync(store, cancellationToken));

    internal static async Task<Results<Ok<LocalizationSettingsResponse>, ValidationProblem>> UpdateLocalizationAsync(
        UpdateLocalizationSettingsRequest request, TenantSettingsStore store, ZelvoDbContext db, CancellationToken cancellationToken)
    {
        if (!LocalizationSettings.IsLanguage(request.DefaultLanguage))
        {
            return UserEndpoints.Validation("defaultLanguage", "Use 'en' or 'ar'.");
        }

        await store.SetAsync(LocalizationSettings.DefaultLanguage, request.DefaultLanguage!, cancellationToken);
        await store.SetAsync(LocalizationSettings.DefaultCalendar, request.DefaultCalendar!.Value, cancellationToken);
        await store.SetAsync(LocalizationSettings.DefaultDigits, request.DefaultDigits!.Value, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Ok(await LocalizationSettingsResponse.LoadAsync(store, cancellationToken));
    }
}

/// <summary>The tenant's language and region defaults.</summary>
/// <param name="DefaultLanguage">en or ar; used when a user has not chosen a language.</param>
/// <param name="DefaultCalendar">Gregorian or Hijri (Umm al-Qura).</param>
/// <param name="DefaultDigits">Western or ArabicIndic.</param>
/// <param name="UpdatedAt">Last change; null while all are at their defaults.</param>
internal sealed record LocalizationSettingsResponse(
    string DefaultLanguage, CalendarSystem DefaultCalendar, DigitSystem DefaultDigits, DateTimeOffset? UpdatedAt)
{
    public static async Task<LocalizationSettingsResponse> LoadAsync(TenantSettingsStore store, CancellationToken cancellationToken)
    {
        var defaults = await LocalizationSettings.GetDefaultsAsync(store, cancellationToken).ConfigureAwait(false);
        var updatedAt = await store.LastChangedAsync(LocalizationSettings.All, cancellationToken).ConfigureAwait(false);
        return new(defaults.Language, defaults.Calendar, defaults.Digits, updatedAt);
    }
}

/// <summary>New language and region defaults.</summary>
public sealed class UpdateLocalizationSettingsRequest
{
    /// <summary>en or ar.</summary>
    [Required]
    public string? DefaultLanguage { get; init; }

    /// <summary>Gregorian or Hijri.</summary>
    [Required]
    public CalendarSystem? DefaultCalendar { get; init; }

    /// <summary>Western or ArabicIndic.</summary>
    [Required]
    public DigitSystem? DefaultDigits { get; init; }
}
