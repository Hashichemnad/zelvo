using Microsoft.AspNetCore.Http.HttpResults;
using Zelvo.Api.Settings;
using Zelvo.Api.Tenancy;
using Zelvo.Data;
using Zelvo.Data.Identity;
using Zelvo.Data.Localization;

namespace Zelvo.Api.Identity;

/// <summary>The signed-in user's own language, calendar and digits (LANG-2, LANG-8).</summary>
internal static class PreferenceEndpoints
{
    public static RouteGroupBuilder MapPreferenceEndpoints(this RouteGroupBuilder group)
    {
        var preferences = group.MapGroup("/me/preferences").WithTags("Identity").RequireTenant().RequireAuthorization();

        preferences.MapGet("/", GetAsync)
            .WithName("GetMyPreferences")
            .WithSummary("Your own language, calendar and digits (null: the tenant default) and what applies.");

        preferences.MapPut("/", UpdateAsync)
            .WithName("UpdateMyPreferences")
            .WithSummary("Change your own language, calendar and digits. Send null for any of them to use the tenant default. Audited.")
            .ProducesValidationProblem();

        return group;
    }

    internal static async Task<Ok<PreferencesResponse>> GetAsync(CurrentUser user, TenantSettingsStore store, CancellationToken cancellationToken) =>
        TypedResults.Ok(await PreferencesResponse.LoadAsync(user.Account!, store, cancellationToken));

    internal static async Task<Results<Ok<PreferencesResponse>, ValidationProblem>> UpdateAsync(
        UpdatePreferencesRequest request, CurrentUser user, TenantSettingsStore store, ZelvoDbContext db, CancellationToken cancellationToken)
    {
        if (request.Language is not null && !LocalizationSettings.IsLanguage(request.Language))
        {
            return UserEndpoints.Validation("language", "Use 'en', 'ar' or null for the tenant default.");
        }

        if ((request.Calendar is { } calendar && !Enum.IsDefined(calendar)) || (request.Digits is { } digits && !Enum.IsDefined(digits)))
        {
            return UserEndpoints.Validation("calendar", "Unknown calendar or digits.");
        }

        var account = user.Account!;
        if (account.PreferredLanguage != request.Language || account.Calendar != request.Calendar || account.Digits != request.Digits)
        {
            account.PreferredLanguage = request.Language;
            account.Calendar = request.Calendar;
            account.Digits = request.Digits;
            account.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
        }

        return TypedResults.Ok(await PreferencesResponse.LoadAsync(account, store, cancellationToken));
    }
}

/// <summary>A user's display preferences.</summary>
/// <param name="Language">Own language (en or ar), or null for the tenant default.</param>
/// <param name="Calendar">Own calendar, or null for the tenant default.</param>
/// <param name="Digits">Own digits, or null for the tenant default.</param>
/// <param name="Effective">What applies: own choices, then the tenant defaults.</param>
internal sealed record PreferencesResponse(string? Language, CalendarSystem? Calendar, DigitSystem? Digits, DisplayDefaults Effective)
{
    public static async Task<PreferencesResponse> LoadAsync(UserAccount account, TenantSettingsStore store, CancellationToken cancellationToken)
    {
        var defaults = await LocalizationSettings.GetDefaultsAsync(store, cancellationToken).ConfigureAwait(false);
        return new(account.PreferredLanguage, account.Calendar, account.Digits, LocalizationSettings.Resolve(account, defaults));
    }
}

/// <summary>New display preferences. Every field is replaced; null means "use the tenant default".</summary>
public sealed class UpdatePreferencesRequest
{
    /// <summary>en, ar or null.</summary>
    public string? Language { get; init; }

    /// <summary>Gregorian, Hijri or null.</summary>
    public CalendarSystem? Calendar { get; init; }

    /// <summary>Western, ArabicIndic or null.</summary>
    public DigitSystem? Digits { get; init; }
}
