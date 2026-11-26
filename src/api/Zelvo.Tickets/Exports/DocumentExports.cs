using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using Zelvo.Data;
using Zelvo.Data.Branding;
using Zelvo.Data.Localization;
using Zelvo.Data.Tenancy;
using Zelvo.Data.Tickets;
using Zelvo.Licensing;
using Zelvo.Tickets.Branding;

namespace Zelvo.Tickets.Exports;

/// <summary>Turns a self-contained HTML document into a PDF.</summary>
public interface IPdfRenderer
{
    /// <summary>The PDF of <paramref name="html"/>; throws <see cref="PdfRenderException"/> when it cannot be made.</summary>
    Task<byte[]> RenderAsync(string html, CancellationToken cancellationToken);
}

/// <summary>A PDF could not be made.</summary>
public sealed class PdfRenderException : Exception
{
    /// <summary>Creates the exception.</summary>
    public PdfRenderException()
    {
    }

    /// <summary>Creates the exception.</summary>
    public PdfRenderException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception.</summary>
    public PdfRenderException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Installation settings of PDF exports (configuration section <c>Pdf</c>).</summary>
public sealed class PdfOptions
{
    /// <summary>The Chromium executable (the worker image installs it; default <c>chromium</c> on the path).</summary>
    public string ChromiumPath { get; set; } = "chromium";

    /// <summary>Directory of bundled font files (woff2) the documents may use.</summary>
    public string? FontsDirectory { get; set; }

    /// <summary>Time one PDF may take.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>Reads the section.</summary>
    public static PdfOptions From(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var section = configuration.GetSection("Pdf");
        var options = new PdfOptions();
        options.ChromiumPath = section["ChromiumPath"] is { Length: > 0 } path ? path : options.ChromiumPath;
        options.FontsDirectory = section["FontsDirectory"] is { Length: > 0 } fonts ? fonts : null;
        options.Timeout = TimeSpan.TryParse(section["Timeout"], CultureInfo.InvariantCulture, out var timeout) && timeout > TimeSpan.Zero ? timeout : options.Timeout;
        return options;
    }
}

/// <summary>
/// Headless Chromium (product owner decision 2026-11-24): correct Arabic shaping and right to left,
/// the same look as the screens. The document is a local file whose policy allows no scripts,
/// every host name resolves to nothing and no profile is kept, so a document can reach nothing.
/// </summary>
public sealed class ChromiumPdfRenderer(PdfOptions options) : IPdfRenderer
{
    /// <inheritdoc />
    public async Task<byte[]> RenderAsync(string html, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(html);
        var work = Directory.CreateTempSubdirectory("zelvo-pdf-");
        try
        {
            var source = Path.Combine(work.FullName, "document.html");
            var target = Path.Combine(work.FullName, "document.pdf");
            await File.WriteAllTextAsync(source, html, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
            var start = new ProcessStartInfo(options.ChromiumPath)
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
            };
            foreach (var argument in new[]
            {
                "--headless=new", "--no-sandbox", "--disable-gpu", "--disable-dev-shm-usage", "--no-first-run", "--disable-extensions",
                "--disable-background-networking", "--disable-sync", "--mute-audio",
                "--host-resolver-rules=MAP * ~NOTFOUND", "--no-pdf-header-footer", $"--user-data-dir={Path.Combine(work.FullName, "profile")}",
                $"--print-to-pdf={target}", new Uri(source).AbsoluteUri,
            })
            {
                start.ArgumentList.Add(argument);
            }

            using var process = Process.Start(start) ?? throw new PdfRenderException("Chromium did not start.");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(options.Timeout);
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            _ = process.StandardOutput.ReadToEndAsync(timeout.Token);
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                process.Kill(entireProcessTree: true);
                throw new PdfRenderException("Chromium took too long to make the PDF.");
            }

            if (!File.Exists(target) || new FileInfo(target).Length == 0)
            {
                var error = await stderr.ConfigureAwait(false);
                throw new PdfRenderException($"Chromium made no PDF (exit {process.ExitCode}): {(error.Length > 300 ? error[..300] : error)}");
            }

            return await File.ReadAllBytesAsync(target, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or IOException)
        {
            throw new PdfRenderException($"Chromium could not be run: {exception.Message}", exception);
        }
        finally
        {
            try
            {
                work.Delete(recursive: true);
            }
            catch (IOException)
            {
                // A leftover temporary directory is harmless.
            }
        }
    }
}

/// <summary>Labels of the PDF documents in English and Arabic (no hard-coded text).</summary>
internal static class ExportTexts
{
    private static readonly Lazy<Dictionary<string, JsonElement>> Texts = new(() => new Dictionary<string, JsonElement>(StringComparer.Ordinal)
    {
        [LocalizedText.English] = Load(LocalizedText.English),
        [LocalizedText.Arabic] = Load(LocalizedText.Arabic),
    });

    public static string Get(string language, string key) =>
        Texts.Value[language == LocalizedText.Arabic ? LocalizedText.Arabic : LocalizedText.English].TryGetProperty(key, out var value) ? value.GetString() ?? key : key;

    public static string State(string language, TicketState state) =>
        Texts.Value[language == LocalizedText.Arabic ? LocalizedText.Arabic : LocalizedText.English].GetProperty("states").TryGetProperty(state.ToString(), out var value)
            ? value.GetString() ?? state.ToString()
            : state.ToString();

    private static JsonElement Load(string language)
    {
        using var stream = typeof(ExportTexts).Assembly.GetManifestResourceStream($"Zelvo.Tickets.Exports.Texts.{language}.json")
            ?? throw new InvalidOperationException($"The export texts '{language}' are missing.");
        using var json = JsonDocument.Parse(stream);
        return json.RootElement.Clone();
    }
}

/// <summary>How dates and digits are shown in a document (LANG-8).</summary>
/// <param name="Language">Language.</param>
/// <param name="Calendar">Gregorian or Hijri.</param>
/// <param name="Digits">Western or Arabic-Indic.</param>
/// <param name="TimeZone">Tenant time zone.</param>
public sealed record DocumentFormat(string Language, CalendarSystem Calendar, DigitSystem Digits, TimeZoneInfo TimeZone)
{
    /// <summary>A date and time in this format.</summary>
    public string Date(DateTimeOffset value)
    {
        var local = TimeZoneInfo.ConvertTime(value, TimeZone);
        var culture = (CultureInfo)CultureInfo.GetCultureInfo(Language == LocalizedText.Arabic ? "ar-AE" : "en-GB").Clone();
        if (Calendar == CalendarSystem.Hijri)
        {
            culture.DateTimeFormat.Calendar = new UmAlQuraCalendar();
        }

        var text = local.ToString("d MMMM yyyy, HH:mm", culture);
        text = Digits == DigitSystem.ArabicIndic
            ? string.Concat(text.Select(c => c is >= '0' and <= '9' ? (char)('٠' + (c - '0')) : c))
            : string.Concat(text.Select(c => c is >= '٠' and <= '٩' ? (char)('0' + (c - '٠')) : c));
        return Calendar == CalendarSystem.Hijri ? text + (Language == LocalizedText.Arabic ? " هـ" : " AH") : text;
    }
}

/// <summary>
/// The branded HTML of a ticket's PDF (module 11): the tenant's logo, product and organisation
/// names, brand colour and font, the ticket's facts, description and conversation (internal notes
/// only in the staff view), right to left for Arabic, and the "Powered by Zelvo" mark, which
/// branding cannot remove. Everything people wrote is HTML-encoded; images are embedded.
/// </summary>
public static class TicketDocument
{
    /// <summary>The ticket's facts as shown.</summary>
    /// <param name="Label">Label key of <c>Texts/*.json</c>.</param>
    /// <param name="Value">Value (people's text, encoded when written).</param>
    public sealed record Fact(string Label, string Value);

    /// <summary>One message of the conversation.</summary>
    public sealed record Message(string Author, bool ByStaff, bool Internal, DateTimeOffset At, string Language, string Body);

    /// <summary>The HTML document.</summary>
    public static string Html(
        BrandingValue branding, LocalizedText organisation, BrandingImage? logo, DocumentFormat format, TicketType type, string number, string title,
        IReadOnlyList<Fact> facts, string? description, string? resolutionNotes, IReadOnlyList<Message> messages, DateTimeOffset exportedAt, string? fontsDirectory)
    {
        ArgumentNullException.ThrowIfNull(branding);
        ArgumentNullException.ThrowIfNull(format);
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(messages);
        var language = format.Language;
        var rtl = language == LocalizedText.Arabic;
        string T(string key) => ExportTexts.Get(language, key);
        var colour = BrandingValue.IsColour(branding.LightColour) ? branding.LightColour : BrandingValue.DefaultLightColour;
        var font = branding.FontFamily;
        var html = new StringBuilder(8192);
        html.Append(CultureInfo.InvariantCulture, $$"""
            <!DOCTYPE html>
            <html lang="{{language}}" dir="{{(rtl ? "rtl" : "ltr")}}">
            <head>
            <meta charset="utf-8">
            <meta http-equiv="Content-Security-Policy" content="default-src 'none'; img-src data:; style-src 'unsafe-inline'; font-src file: data:">
            <title>{{E(number)}}</title>
            <style>
            {{FontFaces(font, fontsDirectory)}}
            @page { size: A4; margin: 18mm 16mm 20mm; }
            body { font-family: "{{font}}", "Noto Sans Arabic", "Noto Sans", Arial, sans-serif; color: #1a1f24; font-size: 11pt; line-height: 1.55; margin: 0; }
            header { display: flex; align-items: center; gap: 12pt; border-block-end: 3pt solid {{colour}}; padding-block-end: 8pt; margin-block-end: 14pt; }
            header img { max-height: 40pt; max-width: 160pt; }
            .product { font-size: 16pt; font-weight: 700; color: {{colour}}; }
            .organisation { color: #4a5560; }
            h1 { font-size: 15pt; margin: 0 0 8pt; }
            h2 { font-size: 12pt; color: {{colour}}; margin: 16pt 0 6pt; }
            .number { font-family: monospace; color: #4a5560; }
            table.facts { border-collapse: collapse; width: 100%; }
            table.facts th { text-align: start; width: 30%; padding: 3pt 6pt; background: #f1f4f7; font-weight: 600; }
            table.facts td { padding: 3pt 6pt; border-block-end: 0.5pt solid #d5dce3; }
            .text { white-space: pre-wrap; }
            .message { border: 0.5pt solid #d5dce3; border-radius: 4pt; padding: 6pt 8pt; margin-block-end: 6pt; break-inside: avoid; }
            .message.internal { border-color: #9a5b00; background: #fff8eb; }
            .meta { font-size: 9pt; color: #4a5560; margin-block-end: 2pt; }
            footer { margin-block-start: 18pt; padding-block-start: 6pt; border-block-start: 0.5pt solid #d5dce3; font-size: 9pt; color: #4a5560; display: flex; justify-content: space-between; }
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
            <p class="number" dir="ltr">{E(T(type == TicketType.ServiceRequest ? "serviceRequest" : "incident"))} {E(number)}</p>
            <h1 dir="auto">{E(title)}</h1>
            <table class="facts">
            """);
        foreach (var fact in facts)
        {
            html.Append(CultureInfo.InvariantCulture, $"""<tr><th>{E(T(fact.Label))}</th><td dir="auto">{E(fact.Value)}</td></tr>""").Append('\n');
        }

        html.Append("</table>\n");
        if (!string.IsNullOrWhiteSpace(description))
        {
            html.Append(CultureInfo.InvariantCulture, $"""<h2>{E(T("description"))}</h2><div class="text" dir="auto">{E(description)}</div>""");
        }

        if (!string.IsNullOrWhiteSpace(resolutionNotes))
        {
            html.Append(CultureInfo.InvariantCulture, $"""<h2>{E(T("resolutionNotes"))}</h2><div class="text" dir="auto">{E(resolutionNotes)}</div>""");
        }

        html.Append(CultureInfo.InvariantCulture, $"<h2>{E(T("conversation"))}</h2>\n");
        if (messages.Count == 0)
        {
            html.Append(CultureInfo.InvariantCulture, $"<p>{E(T("noConversation"))}</p>\n");
        }

        foreach (var message in messages)
        {
            var who = message.Internal ? T("internalNote") : message.ByStaff ? T("staff") : T("requesterLabel");
            html.Append(CultureInfo.InvariantCulture, $"""
                <div class="message{(message.Internal ? " internal" : string.Empty)}">
                <div class="meta"><bdi>{E(message.Author)}</bdi> · {E(who)} · {E(format.Date(message.At))}</div>
                <div class="text" lang="{(message.Language == LocalizedText.Arabic ? "ar" : "en")}" dir="auto">{E(message.Body)}</div>
                </div>
                """).Append('\n');
        }

        html.Append(CultureInfo.InvariantCulture, $"""
            <footer><span>{E(T("exported").Replace("{date}", format.Date(exportedAt), StringComparison.Ordinal))}</span><span>{E(T("poweredBy"))}</span></footer>
            </body>
            </html>
            """);
        return html.ToString();
    }

    // The branding's font from the bundled files, when the installation has them.
    internal static string FontFaces(string family, string? directory)
    {
        if (directory is null || !Directory.Exists(directory))
        {
            return string.Empty;
        }

        var key = BrandingFonts.Families.FirstOrDefault(f => f.Value == family).Key ?? BrandingFonts.Default;
        var css = new StringBuilder();
        foreach (var file in Directory.EnumerateFiles(directory, $"{key}-*.woff2").Order(StringComparer.Ordinal))
        {
            // Files are named <font>-<subset>-<weight>.woff2, as the worker image copies them.
            var parts = Path.GetFileNameWithoutExtension(file).Split('-');
            var weight = int.TryParse(parts[^1], out var w) ? w : 400;
            // Each subset covers its own letters, so Arabic and Latin text both use the font.
            var range = parts[^2] == "arabic" ? "U+0600-06FF, U+0750-077F, U+08A0-08FF, U+FB50-FDFF, U+FE70-FEFF" : "U+0000-024F, U+2000-206F, U+20AC";
            css.Append(CultureInfo.InvariantCulture, $"@font-face {{ font-family: \"{family}\"; font-weight: {weight}; unicode-range: {range}; src: url(\"{new Uri(file).AbsoluteUri}\") format(\"woff2\"); }}\n");
        }

        return css.ToString();
    }

    private static string E(string? text) => WebUtility.HtmlEncode(text ?? string.Empty);
}

/// <summary>
/// Worker job: makes waiting PDF exports (module 11) tenant by tenant with the tenant's published
/// branding, in the language asked for and with the dates of the person who asked; exports are
/// removed after a day. Several workers share the queue through a lease.
/// </summary>
public sealed partial class DocumentExportJob(IServiceScopeFactory scopes, IPdfRenderer renderer, PdfOptions options, ILogger<DocumentExportJob> logger, TimeProvider time)
    : BackgroundService
{
    /// <summary>How often the job runs.</summary>
    public static TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>How long exports are kept.</summary>
    public static TimeSpan Retention { get; } = TimeSpan.FromDays(1);

    private const int MaxAttempts = 3;

    /// <summary>Makes every waiting export now; returns how many were finished.</summary>
    public async Task<int> RunOnceAsync(CancellationToken cancellationToken)
    {
        var done = 0;
        foreach (var (tenantId, slug) in await TenantsAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await NextAsync(tenantId, slug, cancellationToken).ConfigureAwait(false) is { } id)
            {
                await MakeAsync(tenantId, slug, id, cancellationToken).ConfigureAwait(false);
                done++;
            }
        }

        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ZelvoDbContext>();
        var before = time.GetUtcNow() - Retention;
        await db.Database.SqlQuery<long>($"SELECT zelvo_purge_document_exports({before}) AS \"Value\"").SingleAsync(cancellationToken).ConfigureAwait(false);
        return done;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                LogFailed(logger, exception);
            }

            try
            {
                await Task.Delay(Interval, time, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task<List<(Guid, string)>> TenantsAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ZelvoDbContext>();
        await db.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand("SELECT id, slug FROM zelvo_export_tenants()", (NpgsqlConnection)db.Database.GetDbConnection());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var tenants = new List<(Guid, string)>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            tenants.Add((reader.GetGuid(0), reader.GetString(1)));
        }

        return tenants;
    }

    private async Task<Guid?> NextAsync(Guid tenantId, string slug, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>().SetTenant(tenantId, slug);
        var db = scope.ServiceProvider.GetRequiredService<ZelvoDbContext>();
        var now = time.GetUtcNow();
        var lease = now.AddMinutes(2);
        var ids = await db.Database.SqlQuery<Guid>($"""
            UPDATE document_exports SET lease_until = {lease}, attempts = attempts + 1
            WHERE id = (SELECT id FROM document_exports
                        WHERE status = 'Pending' AND (lease_until IS NULL OR lease_until < {now})
                        ORDER BY created_at LIMIT 1 FOR UPDATE SKIP LOCKED)
            RETURNING id AS "Value"
            """).ToListAsync(cancellationToken).ConfigureAwait(false);
        return ids.Count == 0 ? null : ids[0];
    }

    private async Task MakeAsync(Guid tenantId, string slug, Guid id, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>().SetTenant(tenantId, slug);
        var db = scope.ServiceProvider.GetRequiredService<ZelvoDbContext>();
        var export = await db.DocumentExports.SingleAsync(e => e.Id == id, cancellationToken).ConfigureAwait(false);
        try
        {
            var (html, name) = await HtmlAsync(db, export, cancellationToken).ConfigureAwait(false);
            var pdf = await renderer.RenderAsync(html, cancellationToken).ConfigureAwait(false);
            // The installation's hidden mark (module 12, task F6).
            export.Content = InstallationMark.MarkPdf(pdf, scope.ServiceProvider.GetService<IInstallationWatermark>()?.Current);
            export.FileName = name;
            export.Status = ExportStatus.Ready;
            export.Error = null;
        }
        catch (PdfRenderException exception)
        {
            LogExportFailed(logger, exception, id);
            export.Error = exception.Message.Length > DocumentExport.ErrorMaxLength ? exception.Message[..DocumentExport.ErrorMaxLength] : exception.Message;
            if (export.Attempts >= MaxAttempts)
            {
                export.Status = ExportStatus.Failed;
            }
        }

        // A failed attempt waits a little before the next one.
        export.LeaseUntil = export.Status == ExportStatus.Pending ? time.GetUtcNow().AddSeconds(30 * export.Attempts) : null;
        export.FinishedAt = export.Status == ExportStatus.Pending ? null : time.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The document of an export and its file name.</summary>
    internal async Task<(string Html, string FileName)> HtmlAsync(ZelvoDbContext db, DocumentExport export, CancellationToken cancellationToken)
    {
        if (export.Kind == ExportKind.Report)
        {
            return await ReportHtmlAsync(db, export, cancellationToken).ConfigureAwait(false);
        }

        var ticket = await db.Tickets.AsNoTracking().Include(t => t.Incident).Include(t => t.Requester).Include(t => t.Assignee)
            .Include(t => t.Category).Include(t => t.Group).SingleAsync(t => t.Id == export.EntityId, cancellationToken).ConfigureAwait(false);
        var person = await db.Users.AsNoTracking().SingleAsync(u => u.Id == export.RequestedById, cancellationToken).ConfigureAwait(false);
        var tenant = await db.Tenants.AsNoTracking().OrderBy(t => t.Id).Select(t => t.Name).FirstAsync(cancellationToken).ConfigureAwait(false);
        var defaultCalendar = await TicketSettingKeys.ReadAsync(db, "localization.defaultCalendar", CalendarSystem.Gregorian, Enum.IsDefined, cancellationToken).ConfigureAwait(false);
        var defaultDigits = await TicketSettingKeys.ReadAsync(db, "localization.defaultDigits", DigitSystem.Western, Enum.IsDefined, cancellationToken).ConfigureAwait(false);
        var zone = await db.BusinessCalendars.AsNoTracking().Where(c => c.IsDefault).Select(c => c.TimeZone).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false)
            ?? Data.Sla.BusinessCalendar.DefaultTimeZone;
        var format = new DocumentFormat(export.Language, person.Calendar ?? defaultCalendar, person.Digits ?? defaultDigits, Zone(zone));
        var branding = await BrandingReader.PublishedAsync(db, cancellationToken).ConfigureAwait(false);
        var logo = await BrandingReader.ImageAsync(db, branding, BrandingSlot.LogoLight, cancellationToken).ConfigureAwait(false);

        var language = export.Language;
        var facts = new List<TicketDocument.Fact>
        {
            new("state", ExportTexts.State(language, ticket.State)),
            new("requester", ticket.Requester?.DisplayName ?? ticket.Requester?.Email ?? string.Empty),
            new("created", format.Date(ticket.CreatedAt)),
        };
        if (ticket.Incident is { } incident)
        {
            var matrix = await TicketSettingKeys.ReadAsync(db, TicketSettingKeys.PriorityMatrix, PriorityMatrix.Default, m => m.IsValid(), cancellationToken).ConfigureAwait(false);
            facts.Insert(1, new("priority", incident.Priority is >= 1 and <= PriorityMatrix.PriorityLevels
                ? matrix.Priorities[incident.Priority - 1].ToText().GetOrFallback(language) ?? string.Empty : string.Empty));
        }

        if (export.StaffView)
        {
            if (ticket.Category is { } category) facts.Add(new("category", category.Name.GetOrFallback(language) ?? string.Empty));
            if (ticket.Group is { } group) facts.Add(new("group", group.Name.GetOrFallback(language) ?? string.Empty));
            if (ticket.Assignee is { } assignee) facts.Add(new("assignee", assignee.DisplayName ?? assignee.Email ?? string.Empty));
        }

        if (ticket.ResolvedAt is { } resolved)
        {
            facts.Add(new("resolved", format.Date(resolved)));
        }

        var comments = await db.TicketComments.AsNoTracking()
            .Where(c => c.TicketId == ticket.Id && (export.StaffView || c.Visibility == CommentVisibility.Public))
            .OrderBy(c => c.CreatedAt).ThenBy(c => c.Id).Take(500).ToListAsync(cancellationToken).ConfigureAwait(false);
        var authorIds = comments.Select(c => c.AuthorId).Distinct().ToList();
        var authors = await db.Users.AsNoTracking().Where(u => authorIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName ?? u.Email ?? string.Empty, cancellationToken)
            .ConfigureAwait(false);
        var messages = comments.Select(c => new TicketDocument.Message(authors.GetValueOrDefault(c.AuthorId) ?? string.Empty, c.ByStaff,
            c.Visibility == CommentVisibility.Internal, c.CreatedAt, c.Language, c.Body)).ToList();

        var html = TicketDocument.Html(branding, tenant, logo, format, ticket.Type, ticket.Number, ticket.Title, facts, ticket.Description,
            ticket.ResolutionNotes, messages, time.GetUtcNow(), options.FontsDirectory);
        return (html, $"{ticket.Number}-{language}.pdf");
    }

    // Task F2: a dashboard, built with the access the person has now (a lost permission shows less).
    private async Task<(string Html, string FileName)> ReportHtmlAsync(ZelvoDbContext db, DocumentExport export, CancellationToken cancellationToken)
    {
        var request = Reports.ReportRequest.FromJson(export.Parameters) ?? throw new PdfRenderException("The report export has no valid dashboard and filter.");
        var permissions = await Reports.ReportAccess.PermissionsOfAsync(db, export.RequestedById, cancellationToken).ConfigureAwait(false);
        var scope = await Reports.ReportAccess.ScopeAsync(db, export.RequestedById, permissions, cancellationToken).ConfigureAwait(false)
            ?? throw new PdfRenderException("The person who asked may no longer see reports.");
        return await new Reports.ReportRenderer(db, time).HtmlAsync(request, scope, export.RequestedById, export.Language, options.FontsDirectory, cancellationToken)
            .ConfigureAwait(false);
    }

    private static TimeZoneInfo Zone(string id)
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

    [LoggerMessage(Level = LogLevel.Error, Message = "Making PDF exports failed; trying again shortly.")]
    private static partial void LogFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "PDF export {Id} could not be made.")]
    private static partial void LogExportFailed(ILogger logger, Exception exception, Guid id);
}
