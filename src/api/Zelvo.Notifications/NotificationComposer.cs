using Microsoft.EntityFrameworkCore;
using Zelvo.Data;
using Zelvo.Data.Identity;
using Zelvo.Data.Localization;
using Zelvo.Data.Notifications;
using Zelvo.Data.Sla;
using Zelvo.Tickets;

namespace Zelvo.Notifications;

/// <summary>
/// Writes notifications of the current tenant for a person: works out their language, calendar
/// and digits (their own choice, then the tenant default, then English, Gregorian and Western
/// digits: LANG-2, LANG-6, LANG-8), picks the tenant's template or Zelvo's default for that
/// language, and renders it. Used by the worker, and by the API for previews and test emails.
/// </summary>
public sealed class NotificationComposer(ZelvoDbContext db, NotificationOptions options)
{
    private TenantDefaults? _defaults;

    /// <summary>The tenant's own wording of <paramref name="eventType"/> in <paramref name="language"/>, each field falling back to Zelvo's default.</summary>
    public async Task<TemplateText> TemplateAsync(string eventType, string language, CancellationToken cancellationToken)
    {
        var row = await db.NotificationTemplates.AsNoTracking().FirstOrDefaultAsync(t => t.EventType == eventType, cancellationToken).ConfigureAwait(false);
        return Effective(row, eventType, language);
    }

    /// <summary>The wording of <paramref name="row"/> in <paramref name="language"/>, with Zelvo's default for missing fields.</summary>
    public static TemplateText Effective(NotificationTemplate? row, string eventType, string language)
    {
        var fallback = NotificationTexts.For(language).Templates[eventType];
        return row is null
            ? fallback
            : new TemplateText(
                row.Subject.Get(language) ?? fallback.Subject,
                row.Body.Get(language) ?? fallback.Body,
                row.InApp.Get(language) ?? fallback.InApp,
                row.Action.Get(language) ?? fallback.Action);
    }

    /// <summary>How <paramref name="user"/> reads notifications; <paramref name="language"/> overrides their language (previews).</summary>
    public async Task<RecipientFormat> FormatAsync(UserAccount user, CancellationToken cancellationToken, string? language = null)
    {
        ArgumentNullException.ThrowIfNull(user);
        var defaults = await DefaultsAsync(cancellationToken).ConfigureAwait(false);
        var chosen = language is LocalizedText.English or LocalizedText.Arabic ? language
            : user.PreferredLanguage is LocalizedText.English or LocalizedText.Arabic ? user.PreferredLanguage
            : defaults.Language;
        return new(chosen, user.Calendar ?? defaults.Calendar, user.Digits ?? defaults.Digits, defaults.TimeZone);
    }

    /// <summary>The tenant's names for the letterhead.</summary>
    public async Task<TenantLetterhead> LetterheadAsync(CancellationToken cancellationToken) =>
        (await DefaultsAsync(cancellationToken).ConfigureAwait(false)).Letterhead;

    /// <summary>
    /// Renders <paramref name="template"/> for <paramref name="user"/> with <paramref name="values"/>
    /// (the recipient's name and the tenant's name are added) and a link to <paramref name="linkPath"/>.
    /// </summary>
    public async Task<RenderedNotification> RenderAsync(
        TemplateText template, IReadOnlyDictionary<string, PlaceholderValue> values, UserAccount user, RecipientFormat format, string? linkPath,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(format);
        var letterhead = await LetterheadAsync(cancellationToken).ConfigureAwait(false);
        var all = new Dictionary<string, PlaceholderValue>(values, StringComparer.Ordinal)
        {
            [Placeholders.RecipientName] = PlaceholderValue.OfText(user.DisplayName ?? user.Email),
            [Placeholders.TenantName] = PlaceholderValue.OfText(letterhead.Name.HasAnyValue ? letterhead.Name : new LocalizedText(letterhead.Fallback, letterhead.Fallback)),
        };
        return NotificationRenderer.Render(template, all, format, letterhead, Links(format.Language, linkPath));
    }

    /// <summary>Links of an email in <paramref name="language"/>: the page at <paramref name="linkPath"/> and Account → Notifications.</summary>
    public NotificationLinks Links(string language, string? linkPath) =>
        new(linkPath is null ? null : new Uri(options.WebUrl, $"{language}/{linkPath.TrimStart('/')}"),
            new Uri(options.WebUrl, $"{language}/account#notifications"));

    private async Task<TenantDefaults> DefaultsAsync(CancellationToken cancellationToken)
    {
        if (_defaults is not null)
        {
            return _defaults;
        }

        var language = await TicketSettingKeys.ReadAsync(db, "localization.defaultLanguage", LocalizedText.English,
            v => v is LocalizedText.English or LocalizedText.Arabic, cancellationToken).ConfigureAwait(false);
        var calendar = await TicketSettingKeys.ReadAsync(db, "localization.defaultCalendar", CalendarSystem.Gregorian, Enum.IsDefined, cancellationToken)
            .ConfigureAwait(false);
        var digits = await TicketSettingKeys.ReadAsync(db, "localization.defaultDigits", DigitSystem.Western, Enum.IsDefined, cancellationToken)
            .ConfigureAwait(false);
        var zone = await db.BusinessCalendars.AsNoTracking().Where(c => c.IsDefault).Select(c => c.TimeZone)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false) ?? BusinessCalendar.DefaultTimeZone;
        var tenant = await db.Tenants.AsNoTracking().OrderBy(t => t.Id).Select(t => new { t.Name, t.Slug }).FirstAsync(cancellationToken).ConfigureAwait(false);
        _defaults = new(language, calendar, digits, TimeZone(zone), new TenantLetterhead(tenant.Name, tenant.Slug));
        return _defaults;
    }

    private static TimeZoneInfo TimeZone(string id)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (Exception exception) when (exception is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return TimeZoneInfo.Utc;
        }
    }

    private sealed record TenantDefaults(string Language, CalendarSystem Calendar, DigitSystem Digits, TimeZoneInfo TimeZone, TenantLetterhead Letterhead);
}
