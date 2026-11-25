using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Zelvo.Data;
using Zelvo.Data.Branding;
using Zelvo.Data.Localization;
using Zelvo.Data.Tenancy;
using Zelvo.Tickets.Branding;
using Zelvo.Tickets.Exports;

namespace Zelvo.Tickets.Reports;

/// <summary>Labels of report PDFs and emails in English and Arabic (no hard-coded text).</summary>
public static class ReportTexts
{
    private static readonly Lazy<Dictionary<string, JsonElement>> Texts = new(() => new Dictionary<string, JsonElement>(StringComparer.Ordinal)
    {
        [LocalizedText.English] = Load(LocalizedText.English),
        [LocalizedText.Arabic] = Load(LocalizedText.Arabic),
    });

    /// <summary>The text of <paramref name="key"/> (dotted for nested keys) with its <c>{placeholders}</c> filled in.</summary>
    public static string Get(string language, string key, params (string Name, string Value)[] values)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(values);
        var element = Texts.Value[language == LocalizedText.Arabic ? LocalizedText.Arabic : LocalizedText.English];
        foreach (var part in key.Split('.'))
        {
            if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(part, out element))
            {
                return key;
            }
        }

        var text = element.GetString() ?? key;
        foreach (var (name, value) in values)
        {
            text = text.Replace("{" + name + "}", value, StringComparison.Ordinal);
        }

        return text;
    }

    /// <summary>Whether <paramref name="key"/> exists in both languages.</summary>
    public static bool Has(string key) => Get(LocalizedText.English, key) != key && Get(LocalizedText.Arabic, key) != key;

    private static JsonElement Load(string language)
    {
        using var stream = typeof(ReportTexts).Assembly.GetManifestResourceStream($"Zelvo.Tickets.Reports.Texts.{language}.json")
            ?? throw new InvalidOperationException($"The report texts '{language}' are missing.");
        using var json = JsonDocument.Parse(stream);
        return json.RootElement.Clone();
    }
}

/// <summary>Numbers and dates of a report in the reader's language, calendar and digits (LANG-8).</summary>
public sealed record ReportFormat(string Language, CalendarSystem Calendar, DigitSystem Digits, TimeZoneInfo TimeZone)
{
    /// <summary>A whole number.</summary>
    public string Number(double value, int decimals = 0) => Digit(value.ToString(decimals == 0 ? "#,0" : "#,0.0", CultureInfo.InvariantCulture));

    /// <summary>A percentage, or "no data".</summary>
    public string Percent(double? value) => value is { } v ? Number(v, 1) + " %" : ReportTexts.Get(Language, "noData");

    /// <summary>A day.</summary>
    public string Day(DateOnly day)
    {
        var culture = Culture();
        var text = day.ToDateTime(TimeOnly.MinValue).ToString("d MMMM yyyy", culture);
        return Digit(text) + (Calendar == CalendarSystem.Hijri ? (Language == LocalizedText.Arabic ? " هـ" : " AH") : string.Empty);
    }

    /// <summary>The first day of a bucket, short.</summary>
    public string Bucket(DateOnly day, ReportInterval interval)
    {
        var text = day.ToDateTime(TimeOnly.MinValue).ToString(interval == ReportInterval.Month ? "MMM yyyy" : "d MMM", Culture());
        return Digit(text);
    }

    /// <summary>A moment, with its time, in the tenant's time zone.</summary>
    public string Moment(DateTimeOffset at) => new DocumentFormat(Language, Calendar, Digits, TimeZone).Date(at);

    private CultureInfo Culture()
    {
        var culture = (CultureInfo)CultureInfo.GetCultureInfo(Language == LocalizedText.Arabic ? "ar-AE" : "en-GB").Clone();
        if (Calendar == CalendarSystem.Hijri)
        {
            culture.DateTimeFormat.Calendar = new UmAlQuraCalendar();
        }

        return culture;
    }

    private string Digit(string text) => Digits == DigitSystem.ArabicIndic
        ? string.Concat(text.Select(c => c is >= '0' and <= '9' ? (char)('٠' + (c - '0')) : c))
        : string.Concat(text.Select(c => c is >= '٠' and <= '٩' ? (char)('0' + (c - '٠')) : c));
}

/// <summary>
/// The branded HTML of a report PDF (module 10 with module 11's look): logo, product and
/// organisation names, brand colour and font, the period and filter, key figures, bars drawn with
/// plain elements and every value written as text in tables, right to left for Arabic, and the
/// "Powered by Zelvo" mark, which branding cannot remove. Names are HTML-encoded and isolated.
/// </summary>
public static class ReportDocument
{
    /// <summary>The HTML of <paramref name="report"/> (one of the report records).</summary>
    public static string Html(
        ReportKind kind, object report, ReportFilter filter, ReportScope scope, BrandingValue branding, LocalizedText organisation, BrandingImage? logo,
        ReportFormat format, DateTimeOffset madeAt, string? fontsDirectory)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(branding);
        ArgumentNullException.ThrowIfNull(format);
        var language = format.Language;
        var rtl = language == LocalizedText.Arabic;
        var colour = BrandingValue.IsColour(branding.LightColour) ? branding.LightColour : BrandingValue.DefaultLightColour;
        var title = T(format, $"kinds.{kind}");
        var html = new StringBuilder(16384);
        html.Append(CultureInfo.InvariantCulture, $$"""
            <!DOCTYPE html>
            <html lang="{{language}}" dir="{{(rtl ? "rtl" : "ltr")}}">
            <head>
            <meta charset="utf-8">
            <meta http-equiv="Content-Security-Policy" content="default-src 'none'; img-src data:; style-src 'unsafe-inline'; font-src file: data:">
            <title>{{E(title)}}</title>
            <style>
            {{TicketDocument.FontFaces(branding.FontFamily, fontsDirectory)}}
            @page { size: A4; margin: 16mm 14mm 18mm; }
            body { font-family: "{{branding.FontFamily}}", "Noto Sans Arabic", "Noto Sans", Arial, sans-serif; color: #1a1f24; font-size: 10pt; line-height: 1.5; margin: 0; }
            header { display: flex; align-items: center; gap: 12pt; border-block-end: 3pt solid {{colour}}; padding-block-end: 8pt; margin-block-end: 12pt; }
            header img { max-height: 36pt; max-width: 150pt; }
            .product { font-size: 15pt; font-weight: 700; color: {{colour}}; }
            .organisation { color: #4a5560; }
            h1 { font-size: 16pt; margin: 0 0 4pt; }
            h2 { font-size: 12pt; color: {{colour}}; margin: 14pt 0 6pt; break-after: avoid; }
            .meta { color: #4a5560; margin: 0 0 2pt; }
            .tiles { display: flex; flex-wrap: wrap; gap: 6pt; margin-block: 10pt; }
            .tile { flex: 1 1 110pt; border: 0.5pt solid #d5dce3; border-radius: 4pt; padding: 6pt 8pt; break-inside: avoid; }
            .tile .label { font-size: 9pt; color: #4a5560; }
            .tile .value { font-size: 15pt; font-weight: 700; }
            .tile .note { font-size: 8.5pt; color: #4a5560; }
            table { border-collapse: collapse; width: 100%; break-inside: auto; }
            th { text-align: start; padding: 3pt 6pt; background: #f1f4f7; font-weight: 600; }
            td { padding: 3pt 6pt; border-block-end: 0.5pt solid #d5dce3; vertical-align: middle; }
            td.n, th.n { text-align: end; white-space: nowrap; }
            tr { break-inside: avoid; }
            .bar { height: 7pt; background: {{colour}}; border-radius: 2pt; min-width: 1pt; }
            .track { background: #eef2f5; border-radius: 2pt; width: 100%; }
            .cols { display: flex; gap: 14pt; }
            .cols > section { flex: 1 1 0; min-width: 0; }
            footer { margin-block-start: 16pt; padding-block-start: 6pt; border-block-start: 0.5pt solid #d5dce3; font-size: 8.5pt; color: #4a5560; display: flex; justify-content: space-between; }
            </style>
            </head>
            <body>
            <header>
            """);
        if (logo is not null)
        {
            html.Append(CultureInfo.InvariantCulture, $"""<img src="data:{logo.ContentType};base64,{Convert.ToBase64String(logo.Content)}" alt="{E(organisation.GetOrFallback(language) ?? string.Empty)}">""");
        }

        html.Append(CultureInfo.InvariantCulture, $"""
            <div><div class="product">{E(branding.ProductNameIn(language))}</div><div class="organisation">{E(organisation.GetOrFallback(language) ?? string.Empty)}</div></div>
            </header>
            <h1>{E(title)}</h1>
            <p class="meta">{E(T(format, "period", ("from", format.Day(filter.From)), ("to", format.Day(filter.To))))}</p>
            <p class="meta">{E(T(format, "filters"))}: {E(FilterText(filter, scope, format))}</p>
            """);

        switch (report)
        {
            case OverviewReport o:
                Overview(html, o, format);
                break;
            case VolumesReport v:
                Tiles(html, (T(format, "created"), format.Number(v.Created), null), (T(format, "resolved"), format.Number(v.Resolved), null), (T(format, "closed"), format.Number(v.Closed), null));
                Series(html, format, T(format, "trend"), v.Interval, [T(format, "created"), T(format, "resolved"), T(format, "closed")],
                    [.. v.Series.Select(p => (p.Bucket, new double?[] { p.Created, p.Resolved, p.Closed }))], percent: false);
                Columns(html, () => Keys(html, format, T(format, "byType"), v.ByType, "types"), () => Keys(html, format, T(format, "byChannel"), v.ByChannel, "channels"));
                Columns(html, () => Keys(html, format, T(format, "byPriority"), v.ByPriority, "priority"), () => Named(html, format, T(format, "byGroup"), v.ByGroup));
                Named(html, format, T(format, "byCategory"), v.ByCategory);
                break;
            case BacklogReport b:
                Tiles(html, (T(format, "open"), format.Number(b.Open), null), (T(format, "unassigned"), format.Number(b.Unassigned), null));
                Series(html, format, T(format, "trend"), b.Interval, [T(format, "open")], [.. b.Trend.Select(p => (p.Bucket, new double?[] { p.Open }))], percent: false);
                Columns(html, () => Keys(html, format, T(format, "byState"), b.ByState, "states"), () => Keys(html, format, T(format, "byAge"), b.ByAge, "ages"));
                Columns(html, () => Keys(html, format, T(format, "byType"), b.ByType, "types"), () => Keys(html, format, T(format, "byPriority"), b.ByPriority, "priority"));
                Named(html, format, T(format, "byGroup"), b.ByGroup);
                break;
            case SlaReport s:
                Sla(html, s, format);
                break;
            case AgentsReport a:
                Agents(html, a, format);
                break;
            case HappinessReport h:
                Happiness(html, h, format);
                break;
            default:
                throw new ArgumentException("Unknown report.", nameof(report));
        }

        html.Append(CultureInfo.InvariantCulture, $"""
            <footer><span>{E(T(format, "exported", ("date", format.Moment(madeAt))))}</span><span>{E(T(format, "poweredBy"))}</span></footer>
            </body>
            </html>
            """);
        return html.ToString();
    }

    /// <summary>The filter in words.</summary>
    public static string FilterText(ReportFilter filter, ReportScope scope, ReportFormat format)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(format);
        var parts = new List<string>
        {
            filter.Types.Count == 0 ? T(format, "allRecords") : string.Join(", ", filter.Types.Select(t => T(format, $"types.{t}"))),
        };
        if (filter.Groups.Count > 0)
        {
            parts.Add(T(format, "groupsChosen", ("count", format.Number(filter.Groups.Count))));
        }

        if (filter.Categories.Count > 0)
        {
            parts.Add(T(format, "categoriesChosen", ("count", format.Number(filter.Categories.Count))));
        }

        if (scope.OwnFiguresOnly)
        {
            parts.Add(T(format, "ownFigures"));
        }
        else if (scope.Groups is not null)
        {
            parts.Add(T(format, "ledGroups"));
        }

        return string.Join(" · ", parts);
    }

    private static void Overview(StringBuilder html, OverviewReport o, ReportFormat format)
    {
        var target = T(format, "target", ("value", format.Number(o.Targets.SlaTargetPercent)));
        Tiles(html,
            (T(format, "created"), format.Number(o.Created), null),
            (T(format, "resolved"), format.Number(o.Resolved), null),
            (T(format, "open"), format.Number(o.Open), null),
            (T(format, "responseSla"), format.Percent(o.ResponsePercent), target),
            (T(format, "resolutionSla"), format.Percent(o.ResolutionPercent), target),
            (T(format, "happiness"), format.Percent(o.HappinessPercent), T(format, "target", ("value", format.Number(o.Targets.HappinessTargetPercent)))),
            (T(format, "medianResponse"), o.MedianResponseMinutes is { } m ? T(format, "minutes", ("value", format.Number(m, 1))) : T(format, "noData"), null));
    }

    private static void Sla(StringBuilder html, SlaReport s, ReportFormat format)
    {
        var target = T(format, "target", ("value", format.Number(s.TargetPercent)));
        Tiles(html, (T(format, "responseSla"), format.Percent(s.Response.Percent), target), (T(format, "resolutionSla"), format.Percent(s.Resolution.Percent), target));
        Series(html, format, T(format, "trend"), s.Interval, [T(format, "responseSla"), T(format, "resolutionSla")],
            [.. s.Trend.Select(p => (p.Bucket, new[] { p.ResponsePercent, p.ResolutionPercent }))], percent: true);
        foreach (var (heading, rows, labels) in new[]
        {
            (T(format, "byPriority"), s.ByPriority, "priority"),
            (T(format, "byType"), s.ByType, "types"),
            (T(format, "byGroup"), s.ByGroup, "named"),
            (T(format, "byPolicy"), s.ByPolicy, "named"),
        })
        {
            if (rows.Count == 0)
            {
                continue;
            }

            html.Append(CultureInfo.InvariantCulture, $"""
                <h2>{E(heading)}</h2>
                <table><thead><tr><th></th><th class="n">{E(T(format, "responseSla"))}</th><th class="n">{E(T(format, "met"))} / {E(T(format, "breached"))}</th><th class="n">{E(T(format, "resolutionSla"))}</th><th class="n">{E(T(format, "met"))} / {E(T(format, "breached"))}</th></tr></thead><tbody>
                """);
            foreach (var row in rows)
            {
                var name = labels == "named" ? NameOf(row.Name, row.Id, format) : KeyLabel(format, labels, row.Key);
                html.Append(CultureInfo.InvariantCulture, $"""
                    <tr><td><bdi>{E(name)}</bdi></td><td class="n">{E(format.Percent(row.Response.Percent))}</td><td class="n">{E(format.Number(row.Response.Met))} / {E(format.Number(row.Response.Breached))}</td><td class="n">{E(format.Percent(row.Resolution.Percent))}</td><td class="n">{E(format.Number(row.Resolution.Met))} / {E(format.Number(row.Resolution.Breached))}</td></tr>
                    """);
            }

            html.Append("</tbody></table>\n");
        }
    }

    private static void Agents(StringBuilder html, AgentsReport a, ReportFormat format)
    {
        html.Append(CultureInfo.InvariantCulture, $"""
            <table><thead><tr><th>{E(T(format, "agent"))}</th><th class="n">{E(T(format, "assigned"))}</th><th class="n">{E(T(format, "resolved"))}</th><th class="n">{E(T(format, "openNow"))}</th><th class="n">{E(T(format, "medianResponse"))}</th><th class="n">{E(T(format, "medianResolution"))}</th><th class="n">{E(T(format, "slaMet"))}</th><th class="n">{E(T(format, "reopened"))}</th><th class="n">{E(T(format, "happiness"))}</th></tr></thead><tbody>
            """);
        if (a.Agents.Count == 0)
        {
            html.Append(CultureInfo.InvariantCulture, $"""<tr><td colspan="9">{E(T(format, "noData"))}</td></tr>""");
        }

        foreach (var row in a.Agents)
        {
            html.Append(CultureInfo.InvariantCulture, $"""
                <tr><td><bdi>{E(row.Name)}</bdi></td><td class="n">{E(format.Number(row.Assigned))}</td><td class="n">{E(format.Number(row.Resolved))}</td><td class="n">{E(format.Number(row.OpenNow))}</td><td class="n">{E(row.MedianResponseMinutes is { } m ? T(format, "minutes", ("value", format.Number(m, 1))) : T(format, "noData"))}</td><td class="n">{E(row.MedianResolutionHours is { } h ? T(format, "hours", ("value", format.Number(h, 1))) : T(format, "noData"))}</td><td class="n">{E(format.Percent(row.SlaPercent))}</td><td class="n">{E(format.Percent(row.ReopenPercent))}</td><td class="n">{E(format.Percent(row.HappinessPercent))} ({E(format.Number(row.Ratings))})</td></tr>
                """);
        }

        html.Append("</tbody></table>\n");
    }

    private static void Happiness(StringBuilder html, HappinessReport h, ReportFormat format)
    {
        Tiles(html, (T(format, "happiness"), format.Percent(h.HappinessPercent), T(format, "target", ("value", format.Number(h.TargetPercent)))),
            (T(format, "ratings"), format.Number(h.Ratings), null));
        Series(html, format, T(format, "trend"), h.Interval, [T(format, "happiness")], [.. h.Trend.Select(p => (p.Bucket, new[] { p.HappinessPercent }))], percent: true);
        Keys(html, format, T(format, "scores"), [.. h.Scores.Select(s => new KeyCount(T(format, "score", ("score", format.Number(s.Score)), ("points", format.Number(s.Points))), s.Count))], null);
        foreach (var (heading, rows, labels) in new[]
        {
            (T(format, "byType"), h.ByType, "types"),
            (T(format, "byChannel"), h.ByChannel, "channels"),
            (T(format, "byGroup"), h.ByGroup, "named"),
            (T(format, "byService"), h.ByService, "named"),
        })
        {
            if (rows.Count == 0)
            {
                continue;
            }

            html.Append(CultureInfo.InvariantCulture, $"""
                <h2>{E(heading)}</h2>
                <table><thead><tr><th></th><th class="n">{E(T(format, "ratings"))}</th><th class="n">{E(T(format, "happiness"))}</th><th style="width:35%"></th></tr></thead><tbody>
                """);
            foreach (var row in rows)
            {
                var name = labels == "named" ? NameOf(row.Name, row.Id, format) : KeyLabel(format, labels, row.Key);
                html.Append(CultureInfo.InvariantCulture, $"""
                    <tr><td><bdi>{E(name)}</bdi></td><td class="n">{E(format.Number(row.Ratings))}</td><td class="n">{E(format.Percent(row.HappinessPercent))}</td><td>{Bar(row.HappinessPercent ?? 0, 100)}</td></tr>
                    """);
            }

            html.Append("</tbody></table>\n");
        }
    }

    private static void Tiles(StringBuilder html, params (string Label, string Value, string? Note)[] tiles)
    {
        html.Append("<div class=\"tiles\">");
        foreach (var (label, value, note) in tiles)
        {
            html.Append(CultureInfo.InvariantCulture, $"""<div class="tile"><div class="label">{E(label)}</div><div class="value">{E(value)}</div>{(note is null ? string.Empty : $"<div class=\"note\">{E(note)}</div>")}</div>""");
        }

        html.Append("</div>\n");
    }

    // A trend as a table with a bar for the first value.
    private static void Series(StringBuilder html, ReportFormat format, string heading, ReportInterval interval, string[] names, (DateOnly Bucket, double?[] Values)[] points, bool percent)
    {
        var max = percent ? 100 : Math.Max(1, points.SelectMany(p => p.Values).Select(v => v ?? 0).DefaultIfEmpty(0).Max());
        html.Append(CultureInfo.InvariantCulture, $"<h2>{E(heading)}</h2><table><thead><tr><th>{E(T(format, "bucket"))}</th>");
        foreach (var name in names)
        {
            html.Append(CultureInfo.InvariantCulture, $"<th class=\"n\">{E(name)}</th>");
        }

        html.Append("<th style=\"width:35%\"></th></tr></thead><tbody>\n");
        foreach (var (bucket, values) in points)
        {
            html.Append(CultureInfo.InvariantCulture, $"<tr><td>{E(format.Bucket(bucket, interval))}</td>");
            foreach (var value in values)
            {
                html.Append(CultureInfo.InvariantCulture, $"<td class=\"n\">{E(percent ? format.Percent(value) : format.Number(value ?? 0))}</td>");
            }

            html.Append(CultureInfo.InvariantCulture, $"<td>{Bar(values[0] ?? 0, max)}</td></tr>\n");
        }

        html.Append("</tbody></table>\n");
    }

    private static void Keys(StringBuilder html, ReportFormat format, string heading, IReadOnlyList<KeyCount> rows, string? labels)
    {
        var max = Math.Max(1, rows.Select(r => r.Count).DefaultIfEmpty(0).Max());
        html.Append(CultureInfo.InvariantCulture, $"<section><h2>{E(heading)}</h2><table><tbody>\n");
        if (rows.Count == 0)
        {
            html.Append(CultureInfo.InvariantCulture, $"<tr><td>{E(T(format, "noData"))}</td></tr>");
        }

        foreach (var row in rows)
        {
            var name = labels is null ? row.Key : KeyLabel(format, labels, row.Key);
            html.Append(CultureInfo.InvariantCulture, $"<tr><td><bdi>{E(name)}</bdi></td><td class=\"n\">{E(format.Number(row.Count))}</td><td style=\"width:40%\">{Bar(row.Count, max)}</td></tr>\n");
        }

        html.Append("</tbody></table></section>\n");
    }

    private static void Named(StringBuilder html, ReportFormat format, string heading, IReadOnlyList<NamedCount> rows) =>
        Keys(html, format, heading, [.. rows.Select(r => new KeyCount(NameOf(r.Name, r.Id, format), r.Count))], null);

    private static void Columns(StringBuilder html, Action first, Action second)
    {
        html.Append("<div class=\"cols\">");
        first();
        second();
        html.Append("</div>\n");
    }

    private static string KeyLabel(ReportFormat format, string labels, string key) => key == "none"
        ? T(format, "none")
        : labels == "priority" ? T(format, "priority", ("value", key)) : T(format, $"{labels}.{key}");

    private static string NameOf(ReportName? name, Guid? id, ReportFormat format) =>
        name?.In(format.Language) is { Length: > 0 } text ? text : id is null ? T(format, "none") : T(format, "removed");

    private static string Bar(double value, double max) =>
        string.Create(CultureInfo.InvariantCulture, $"<div class=\"track\"><div class=\"bar\" style=\"width:{Math.Clamp(value * 100 / max, 0, 100):0.#}%\"></div></div>");

    private static string T(ReportFormat format, string key, params (string Name, string Value)[] values) => ReportTexts.Get(format.Language, key, values);

    private static string E(string? text) => WebUtility.HtmlEncode(text ?? string.Empty);
}

/// <summary>Builds a dashboard and its branded PDF document for one reader (API exports and scheduled reports).</summary>
public sealed class ReportRenderer(ZelvoDbContext db, TimeProvider time)
{
    /// <summary>The report of <paramref name="request"/> in <paramref name="scope"/>.</summary>
    public async Task<object> BuildAsync(ReportRequest request, ReportScope scope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var builder = new ReportBuilder(db);
        return request.Kind switch
        {
            ReportKind.Volumes => await builder.VolumesAsync(request.Filter, scope, cancellationToken).ConfigureAwait(false),
            ReportKind.Backlog => await builder.BacklogAsync(request.Filter, scope, time.GetUtcNow(), cancellationToken).ConfigureAwait(false),
            ReportKind.Sla => await builder.SlaAsync(request.Filter, scope, cancellationToken).ConfigureAwait(false),
            ReportKind.Agents => await builder.AgentsAsync(request.Filter, scope, cancellationToken).ConfigureAwait(false),
            ReportKind.Happiness => await builder.HappinessAsync(request.Filter, scope, cancellationToken).ConfigureAwait(false),
            _ => await builder.OverviewAsync(request.Filter, scope, cancellationToken).ConfigureAwait(false),
        };
    }

    /// <summary>The format of reader <paramref name="userId"/>: their calendar and digits, else the tenant's; the tenant's time zone.</summary>
    public async Task<ReportFormat> FormatAsync(Guid userId, string language, CancellationToken cancellationToken)
    {
        var person = await db.Users.AsNoTracking().Where(u => u.Id == userId).Select(u => new { u.Calendar, u.Digits }).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        var calendar = await TicketSettingKeys.ReadAsync(db, "localization.defaultCalendar", CalendarSystem.Gregorian, Enum.IsDefined, cancellationToken).ConfigureAwait(false);
        var digits = await TicketSettingKeys.ReadAsync(db, "localization.defaultDigits", DigitSystem.Western, Enum.IsDefined, cancellationToken).ConfigureAwait(false);
        var zone = await new ReportBuilder(db).TimeZoneAsync(cancellationToken).ConfigureAwait(false);
        return new ReportFormat(language == LocalizedText.Arabic ? LocalizedText.Arabic : LocalizedText.English, person?.Calendar ?? calendar, person?.Digits ?? digits, zone);
    }

    /// <summary>The branded HTML of the report and its file name.</summary>
    public async Task<(string Html, string FileName)> HtmlAsync(
        ReportRequest request, ReportScope scope, Guid readerId, string language, string? fontsDirectory, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var report = await BuildAsync(request, scope, cancellationToken).ConfigureAwait(false);
        var format = await FormatAsync(readerId, language, cancellationToken).ConfigureAwait(false);
        var organisation = await db.Tenants.AsNoTracking().OrderBy(t => t.Id).Select(t => t.Name).FirstAsync(cancellationToken).ConfigureAwait(false);
        var branding = await BrandingReader.PublishedAsync(db, cancellationToken).ConfigureAwait(false);
        var logo = await BrandingReader.ImageAsync(db, branding, BrandingSlot.LogoLight, cancellationToken).ConfigureAwait(false);
        var html = ReportDocument.Html(request.Kind, report, request.Filter, scope, branding, organisation, logo, format, time.GetUtcNow(), fontsDirectory);
        var name = string.Create(CultureInfo.InvariantCulture, $"{request.Kind.ToString().ToLowerInvariant()}-{request.Filter.From:yyyy-MM-dd}-{request.Filter.To:yyyy-MM-dd}-{format.Language}.pdf");
        return (html, name);
    }
}
