using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Zelvo.Data.Localization;

namespace Zelvo.Notifications;

/// <summary>How a recipient reads text, numbers and dates (LANG-6, LANG-8).</summary>
/// <param name="Language"><c>en</c> or <c>ar</c>.</param>
/// <param name="Calendar">Gregorian or Hijri (Umm al-Qura).</param>
/// <param name="Digits">Western or Arabic-Indic.</param>
/// <param name="TimeZone">Time zone dates are shown in (the tenant's default calendar).</param>
public sealed record RecipientFormat(string Language, CalendarSystem Calendar, DigitSystem Digits, TimeZoneInfo TimeZone);

/// <summary>A notification written out for one recipient.</summary>
/// <param name="Subject">Email subject and in-app title.</param>
/// <param name="InApp">In-app text.</param>
/// <param name="Html">Email body as HTML (full document).</param>
/// <param name="Text">Email body as plain text.</param>
public sealed record RenderedNotification(string Subject, string InApp, string Html, string Text);

/// <summary>Where the email's links go.</summary>
/// <param name="Action">The ticket or article; <see langword="null"/> for no button.</param>
/// <param name="Preferences">Account → Notifications.</param>
public sealed record NotificationLinks(Uri? Action, Uri? Preferences, Uri? Secondary = null, string? SecondaryLabel = null);

/// <summary>
/// Writes a notification for one recipient: placeholders replaced with values formatted in the
/// recipient's language, calendar and digits; the email laid out with the organisation's name as
/// letterhead, right to left for Arabic, Latin values isolated (<c>&lt;bdi&gt;</c>) so they keep
/// their direction, and the "Powered by Zelvo" footer. Every value is HTML-encoded; templates have
/// no logic, so a template cannot run code or reach data beyond its placeholders.
/// </summary>
public static partial class NotificationRenderer
{
    /// <summary>Content id of the logo embedded in emails (task F1).</summary>
    public const string LogoContentId = "zelvo-logo";

    private const string TextColour = "#1f2937";
    private const string MutedColour = "#4b5563";

    /// <summary>Renders <paramref name="template"/> for a recipient.</summary>
    public static RenderedNotification Render(
        TemplateText template,
        IReadOnlyDictionary<string, PlaceholderValue> values,
        RecipientFormat format,
        TenantLetterhead letterhead,
        NotificationLinks links)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(format);
        ArgumentNullException.ThrowIfNull(letterhead);
        ArgumentNullException.ThrowIfNull(links);
        var texts = NotificationTexts.For(format.Language);
        string Value(string name) => values.TryGetValue(name, out var value) ? FormatValue(value, format, texts) : string.Empty;

        var subject = Truncate(OneLine(Substitute(template.Subject, Value)), Data.Notifications.Notification.TitleMaxLength);
        var inApp = Truncate(OneLine(Substitute(template.InApp, Value)), Data.Notifications.Notification.TextMaxLength);
        var text = PlainText(template, Value, texts, letterhead.NameIn(format.Language), links);
        var html = Html(template, subject, Value, format.Language, texts, letterhead, links);
        return new(subject, inApp, html, text);
    }

    /// <summary>A value as the recipient reads it.</summary>
    public static string FormatValue(PlaceholderValue value, RecipientFormat format, LanguageTexts texts)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(format);
        ArgumentNullException.ThrowIfNull(texts);
        if (value.Text is not null)
        {
            return value.Text;
        }

        if (value.En is not null || value.Ar is not null)
        {
            return new LocalizedText(value.En, value.Ar).GetOrFallback(format.Language) ?? string.Empty;
        }

        if (value.Number is { } number)
        {
            return Digits(number.ToString(CultureInfo.InvariantCulture), format.Digits);
        }

        if (value.Percent is { } percent)
        {
            var sign = format.Digits == DigitSystem.ArabicIndic ? "٪" : "%";
            return Digits(percent.ToString(CultureInfo.InvariantCulture), format.Digits) + sign;
        }

        if (value.Date is { } date)
        {
            return FormatDate(date, format, texts.Dates);
        }

        return value.Term is { } term && texts.Terms.TryGetValue(term, out var word) ? word : string.Empty;
    }

    /// <summary>
    /// A moment in the recipient's calendar and digits, naming its calendar (GOV-8): for example
    /// "24 November 2026, 10:30 AM", "٢٤ نوفمبر ٢٠٢٦م، ١٠:٣٠ ص" or "٣ جمادى الآخرة ١٤٤٨هـ، ١٠:٣٠ ص".
    /// </summary>
    public static string FormatDate(DateTimeOffset date, RecipientFormat format, DateText names)
    {
        ArgumentNullException.ThrowIfNull(format);
        ArgumentNullException.ThrowIfNull(names);
        var local = TimeZoneInfo.ConvertTime(date, format.TimeZone).DateTime;
        int day, month, year;
        string monthName, suffix;
        var hijri = new UmAlQuraCalendar();
        if (format.Calendar == CalendarSystem.Hijri && local >= hijri.MinSupportedDateTime && local <= hijri.MaxSupportedDateTime)
        {
            day = hijri.GetDayOfMonth(local);
            month = hijri.GetMonth(local);
            year = hijri.GetYear(local);
            monthName = names.HijriMonths[month - 1];
            suffix = names.HijriSuffix;
        }
        else
        {
            (day, month, year) = (local.Day, local.Month, local.Year);
            monthName = names.Months[month - 1];
            suffix = names.GregorianSuffix;
        }

        var hour = local.Hour % 12 == 0 ? 12 : local.Hour % 12;
        var written = string.Create(CultureInfo.InvariantCulture,
            $"{day} {monthName} {year}{suffix}{names.Separator}{hour}:{local.Minute:00} {(local.Hour < 12 ? names.Am : names.Pm)}");
        return Digits(written, format.Digits);
    }

    /// <summary>Writes Western digits as Arabic-Indic ones when the recipient reads those.</summary>
    public static string Digits(string text, DigitSystem digits)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (digits != DigitSystem.ArabicIndic)
        {
            return text;
        }

        var builder = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            builder.Append(c is >= '0' and <= '9' ? (char)('٠' + (c - '0')) : c);
        }

        return builder.ToString();
    }

    private static string Substitute(string template, Func<string, string> value) =>
        TemplateSyntax.Placeholder().Replace(template, match => value(match.Groups[1].Value));

    private static string PlainText(TemplateText template, Func<string, string> value, LanguageTexts texts, string tenantName, NotificationLinks links)
    {
        var builder = new StringBuilder();
        builder.Append(tenantName).Append("\n\n");
        builder.Append(Normalize(Substitute(template.Body, value)).Trim());
        if (links.Action is { } action)
        {
            builder.Append("\n\n").Append(template.Action).Append(": ").Append(action.AbsoluteUri);
        }

        if (links.Secondary is { } secondary && links.SecondaryLabel is { } label)
        {
            builder.Append('\n').Append(label).Append(": ").Append(secondary.AbsoluteUri);
        }

        builder.Append("\n\n--\n").Append(texts.Layout.Automated);
        if (links.Preferences is { } preferences)
        {
            builder.Append('\n').Append(texts.Layout.Preferences).Append(": ").Append(preferences.AbsoluteUri);
        }

        builder.Append('\n').Append(texts.Layout.PoweredBy).Append('\n');
        return builder.ToString();
    }

    private static string Html(
        TemplateText template, string subject, Func<string, string> value, string language, LanguageTexts texts, TenantLetterhead letterhead, NotificationLinks links)
    {
        var rtl = language == LocalizedText.Arabic;
        var dir = rtl ? "rtl" : "ltr";
        // Task F1: the tenant's brand colour (checked for contrast on white when published).
        var Accent = letterhead.Accent;
        // Mail apps ignore logical CSS properties, so alignment follows the direction explicitly.
        var align = rtl ? "right" : "left";
        var font = rtl ? "Tahoma, 'IBM Plex Sans Arabic', Arial, sans-serif" : "Arial, Helvetica, sans-serif";
        var html = new StringBuilder(4096);
        html.Append(CultureInfo.InvariantCulture, $"""
            <!DOCTYPE html>
            <html lang="{language}" dir="{dir}">
            <head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1"><meta name="color-scheme" content="light"><title>{Encode(subject)}</title></head>
            <body style="margin:0;padding:0;background-color:#f3f4f6;">
            <table role="presentation" width="100%" cellpadding="0" cellspacing="0" style="background-color:#f3f4f6;"><tr><td align="center" style="padding:24px 12px;">
            <table role="presentation" width="100%" cellpadding="0" cellspacing="0" dir="{dir}" style="max-width:600px;background-color:#ffffff;border-radius:8px;font-family:{font};text-align:{align};">
            <tr><td style="padding:20px 24px;border-bottom:3px solid {Accent};">
            """);
        if (letterhead.HasLogo)
        {
            html.Append(CultureInfo.InvariantCulture, $"""<img src="cid:{LogoContentId}" alt="" height="48" style="display:block;max-height:48px;margin:0 0 8px;border:0;">""");
        }

        html.Append(CultureInfo.InvariantCulture, $"""<div style="font-size:20px;font-weight:bold;color:{Accent};">{Encode(letterhead.NameIn(language))}</div>""");
        if (letterhead.OtherName(language) is { } other)
        {
            var otherLanguage = rtl ? LocalizedText.English : LocalizedText.Arabic;
            html.Append(CultureInfo.InvariantCulture,
                $"""<div lang="{otherLanguage}" dir="{(rtl ? "ltr" : "rtl")}" style="font-size:14px;color:{MutedColour};text-align:{align};">{Encode(other)}</div>""");
        }

        html.Append(CultureInfo.InvariantCulture, $"""
            </td></tr>
            <tr><td style="padding:24px;font-size:16px;line-height:1.6;color:{TextColour};">
            """);
        foreach (var paragraph in Paragraphs(template.Body))
        {
            var content = TemplateSyntax.Placeholder().Replace(Encode(paragraph), match =>
                $"<bdi>{Encode(value(match.Groups[1].Value))}</bdi>");
            html.Append(CultureInfo.InvariantCulture, $"""<p style="margin:0 0 16px;">{LineBreaks(content)}</p>""").Append('\n');
        }

        if (links.Action is { } action)
        {
            var href = Encode(action.AbsoluteUri);
            html.Append(CultureInfo.InvariantCulture, $"""
                <p style="margin:24px 0;"><a href="{href}" style="display:inline-block;background-color:{Accent};color:#ffffff;text-decoration:none;padding:12px 20px;border-radius:6px;font-weight:bold;">{Encode(template.Action)}</a>{SecondaryButton(links, Accent)}</p>
                <p style="margin:0;font-size:13px;color:{MutedColour};">{Encode(texts.Layout.LinkHint)}<br><a href="{href}" dir="ltr" style="color:{Accent};word-break:break-all;">{href}</a></p>
                """);
        }

        html.Append(CultureInfo.InvariantCulture, $"""
            </td></tr>
            <tr><td style="padding:16px 24px;border-top:1px solid #e5e7eb;font-size:13px;line-height:1.6;color:{MutedColour};">
            {Encode(texts.Layout.Automated)}<br>
            """);
        if (links.Preferences is { } preferences)
        {
            html.Append(CultureInfo.InvariantCulture, $"""<a href="{Encode(preferences.AbsoluteUri)}" style="color:{Accent};">{Encode(texts.Layout.Preferences)}</a><br>""");
        }

        html.Append(CultureInfo.InvariantCulture, $"""
            {(letterhead.ProductIn(language) is { } product ? $"<span>{Encode(product)}</span> · " : string.Empty)}<span>{Encode(texts.Layout.PoweredBy)}</span>
            </td></tr>
            </table>
            </td></tr></table>
            </body>
            </html>
            """);
        return html.ToString();
    }

    /// <summary>A second button (for example Reject next to Approve), outlined.</summary>
    private static string SecondaryButton(NotificationLinks links, string accent) =>
        links.Secondary is { } secondary && links.SecondaryLabel is { } label
            ? $"""&#160;&#160;<a href="{Encode(secondary.AbsoluteUri)}" style="display:inline-block;background-color:#ffffff;color:{accent};text-decoration:none;padding:10px 18px;border:2px solid {accent};border-radius:6px;font-weight:bold;">{Encode(label)}</a>"""
            : string.Empty;

    private static IEnumerable<string> Paragraphs(string body) =>
        BlankLines().Split(Normalize(body)).Select(p => p.Trim('\n', ' ')).Where(p => p.Length > 0);

    private static string LineBreaks(string encoded) => encoded.Replace("\n", "<br>\n", StringComparison.Ordinal);

    private static string Encode(string text) => WebUtility.HtmlEncode(text);

    private static string Normalize(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    /// <summary>One line without control characters (subjects and in-app text).</summary>
    private static string OneLine(string text) => Whitespace().Replace(ControlCharacters().Replace(text, " "), " ").Trim();

    private static string Truncate(string text, int length) => text.Length <= length ? text : string.Concat(text.AsSpan(0, length - 1), "…");

    [GeneratedRegex(@"\n[ \t]*\n+")]
    private static partial Regex BlankLines();

    [GeneratedRegex(@"\p{Cc}")]
    private static partial Regex ControlCharacters();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex Whitespace();
}

/// <summary>The organisation's letterhead (GOV-8) with its branding (task F1).</summary>
/// <param name="Name">The tenant's bilingual name.</param>
/// <param name="Fallback">Shown when the tenant has no name (its slug).</param>
public sealed record TenantLetterhead(LocalizedText Name, string Fallback)
{
    /// <summary>Brand colour of headings, links and buttons (the published light colour).</summary>
    public string Accent { get; init; } = Data.Branding.BrandingValue.DefaultLightColour;

    /// <summary>The published product name, if the tenant set one.</summary>
    public LocalizedText ProductName { get; init; } = new(null, null);

    /// <summary>The published logo is embedded (content id <see cref="NotificationRenderer.LogoContentId"/>).</summary>
    public bool HasLogo { get; init; }

    /// <summary>The product name in <paramref name="language"/>, or null when the tenant set none.</summary>
    public string? ProductIn(string language) => ProductName.GetOrFallback(language);

    /// <summary>The name in <paramref name="language"/>, else the other language, else the fallback.</summary>
    public string NameIn(string language) => Name.GetOrFallback(language) ?? Fallback;

    /// <summary>The name in the other language when it differs (bilingual letterheads), else <see langword="null"/>.</summary>
    public string? OtherName(string language)
    {
        var other = Name.Get(language == LocalizedText.Arabic ? LocalizedText.English : LocalizedText.Arabic);
        return other is not null && Name.Get(language) is not null && !string.Equals(other, Name.Get(language), StringComparison.Ordinal) ? other : null;
    }
}

/// <summary>The <c>{{placeholder}}</c> syntax of templates.</summary>
public static partial class TemplateSyntax
{
    /// <summary>A placeholder: <c>{{name}}</c> or <c>{{ name }}</c>, names of dotted lower camel case segments.</summary>
    [GeneratedRegex(@"\{\{\s*([a-z][a-zA-Z0-9]*(?:\.[a-z][a-zA-Z0-9]*)*)\s*\}\}")]
    public static partial Regex Placeholder();

    [GeneratedRegex(@"\{\{|\}\}")]
    private static partial Regex Braces();

    /// <summary>
    /// Problems with <paramref name="text"/>: placeholders not in <paramref name="allowed"/>, and
    /// <c>{{</c> or <c>}}</c> that are not part of a placeholder. Empty when it is fine.
    /// </summary>
    public static IReadOnlyList<string> Problems(string? text, IReadOnlyCollection<string> allowed)
    {
        ArgumentNullException.ThrowIfNull(allowed);
        if (string.IsNullOrEmpty(text))
        {
            return [];
        }

        var problems = new List<string>();
        foreach (Match match in Placeholder().Matches(text))
        {
            var name = match.Groups[1].Value;
            if (!allowed.Contains(name))
            {
                problems.Add($"Unknown placeholder {{{{{name}}}}}.");
            }
        }

        if (Braces().IsMatch(Placeholder().Replace(text, string.Empty)))
        {
            problems.Add("Use {{ and }} only around a placeholder name.");
        }

        return problems;
    }
}
