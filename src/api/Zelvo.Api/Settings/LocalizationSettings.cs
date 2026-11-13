using Zelvo.Api.Identity;
using Zelvo.Data.Identity;
using Zelvo.Data.Localization;

namespace Zelvo.Api.Settings;

/// <summary>
/// Tenant defaults for language (LANG-1), calendar and digits (LANG-8), and how they combine with
/// a user's own choices (LANG-2). Set by administrators in Settings → Language and region.
/// </summary>
internal static class LocalizationSettings
{
    public static SettingDefinition<string> DefaultLanguage { get; } =
        new("localization.defaultLanguage", LocalizedText.English, Permissions.SettingsManage, IsLanguage);

    public static SettingDefinition<CalendarSystem> DefaultCalendar { get; } =
        new("localization.defaultCalendar", CalendarSystem.Gregorian, Permissions.SettingsManage, Enum.IsDefined);

    public static SettingDefinition<DigitSystem> DefaultDigits { get; } =
        new("localization.defaultDigits", DigitSystem.Western, Permissions.SettingsManage, Enum.IsDefined);

    public static IReadOnlyList<SettingDefinition> All { get; } = [DefaultLanguage, DefaultCalendar, DefaultDigits];

    public static bool IsLanguage(string? value) => value is not null && LocalizedText.Languages.Contains(value, StringComparer.Ordinal);

    /// <summary>The tenant's defaults.</summary>
    public static async Task<DisplayDefaults> GetDefaultsAsync(TenantSettingsStore store, CancellationToken cancellationToken) =>
        new(await store.GetAsync(DefaultLanguage, cancellationToken).ConfigureAwait(false),
            await store.GetAsync(DefaultCalendar, cancellationToken).ConfigureAwait(false),
            await store.GetAsync(DefaultDigits, cancellationToken).ConfigureAwait(false));

    /// <summary>
    /// What a signed-in user sees (product owner decision 2026-11-13): their own choice, then the
    /// tenant default (whose own default is English, Gregorian and Western digits).
    /// </summary>
    public static DisplayDefaults Resolve(UserAccount user, DisplayDefaults tenant) =>
        new(IsLanguage(user.PreferredLanguage) ? user.PreferredLanguage! : tenant.Language,
            user.Calendar ?? tenant.Calendar,
            user.Digits ?? tenant.Digits);
}

/// <summary>Language, calendar and digits for showing text, dates and numbers.</summary>
/// <param name="Language">en or ar.</param>
/// <param name="Calendar">Gregorian or Hijri (Umm al-Qura).</param>
/// <param name="Digits">Western or ArabicIndic.</param>
internal sealed record DisplayDefaults(string Language, CalendarSystem Calendar, DigitSystem Digits);
