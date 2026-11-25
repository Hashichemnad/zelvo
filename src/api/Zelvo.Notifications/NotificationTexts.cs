using System.Collections.Frozen;
using System.Text.Json;
using Zelvo.Data.Localization;

namespace Zelvo.Notifications;

/// <summary>The wording of one notification in one language.</summary>
/// <param name="Subject">Subject line and in-app title.</param>
/// <param name="Body">Email body.</param>
/// <param name="InApp">In-app text.</param>
/// <param name="Action">Label of the link.</param>
public sealed record TemplateText(string Subject, string Body, string InApp, string Action);

/// <summary>Fixed wording of the email layout in one language.</summary>
/// <param name="PoweredBy">"Powered by Zelvo" (approved Arabic: "بتقنية زيلفو").</param>
/// <param name="Automated">Note that the email is automatic.</param>
/// <param name="Preferences">Link to Account → Notifications.</param>
/// <param name="LinkHint">Shown above the link's address for mail apps that do not show buttons.</param>
public sealed record LayoutText(string PoweredBy, string Automated, string Preferences, string LinkHint);

/// <summary>Names used to write dates in one language.</summary>
/// <param name="Months">Gregorian months.</param>
/// <param name="HijriMonths">Hijri (Umm al-Qura) months.</param>
/// <param name="Am">Before noon.</param>
/// <param name="Pm">After noon.</param>
/// <param name="GregorianSuffix">After a Gregorian year ("م" in Arabic).</param>
/// <param name="HijriSuffix">After a Hijri year ("هـ" in Arabic, " AH" in English).</param>
/// <param name="Separator">Between the date and the time.</param>
public sealed record DateText(
    IReadOnlyList<string> Months, IReadOnlyList<string> HijriMonths, string Am, string Pm, string GregorianSuffix, string HijriSuffix, string Separator);

/// <summary>Everything Zelvo itself writes in notifications, in one language.</summary>
/// <param name="Layout">Email layout.</param>
/// <param name="Dates">Date names.</param>
/// <param name="Terms">Fixed terms used as placeholder values.</param>
/// <param name="Templates">Default wording of each event.</param>
/// <param name="Samples">Sample placeholder values for previews in Settings.</param>
public sealed record LanguageTexts(
    LayoutText Layout, DateText Dates, IReadOnlyDictionary<string, string> Terms, IReadOnlyDictionary<string, TemplateText> Templates,
    IReadOnlyDictionary<string, string> Samples);

/// <summary>
/// Zelvo's own notification wording: the default templates and the email layout, from the
/// translation files <c>Texts/en.json</c> and <c>Texts/ar.json</c> (no text in code). Arabic follows
/// <c>docs/guidelines/arabic-formal-correspondence.md</c> (GOV-8).
/// </summary>
public static class NotificationTexts
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static readonly FrozenDictionary<string, LanguageTexts> Languages = LocalizedText.Languages
        .ToFrozenDictionary(language => language, Load, StringComparer.Ordinal);

    /// <summary>The texts of <paramref name="language"/> (<c>en</c> or <c>ar</c>).</summary>
    public static LanguageTexts For(string language) =>
        Languages.TryGetValue(language, out var texts) ? texts : Languages[LocalizedText.English];

    /// <summary>Zelvo's default wording of <paramref name="eventType"/> in both languages.</summary>
    public static (TemplateText En, TemplateText Ar) Default(string eventType) =>
        (For(LocalizedText.English).Templates[eventType], For(LocalizedText.Arabic).Templates[eventType]);

    private static LanguageTexts Load(string language)
    {
        var name = $"Zelvo.Notifications.Texts.{language}.json";
        using var stream = typeof(NotificationTexts).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"The notification texts '{name}' are missing.");
        var texts = JsonSerializer.Deserialize<LanguageTexts>(stream, Json)
            ?? throw new InvalidOperationException($"The notification texts '{name}' are empty.");
        if (texts.Dates.Months.Count != 12 || texts.Dates.HijriMonths.Count != 12)
        {
            throw new InvalidOperationException($"The notification texts '{name}' need 12 Gregorian and 12 Hijri months.");
        }

        foreach (var definition in NotificationEvents.All.Select(e => e.Type).Append(NotificationEvents.TestEmail))
        {
            if (!texts.Templates.ContainsKey(definition))
            {
                throw new InvalidOperationException($"The notification texts '{name}' have no template for '{definition}'.");
            }
        }

        return texts with
        {
            Terms = texts.Terms.ToFrozenDictionary(StringComparer.Ordinal),
            Templates = texts.Templates.ToFrozenDictionary(StringComparer.Ordinal),
            Samples = texts.Samples.ToFrozenDictionary(StringComparer.Ordinal),
        };
    }
}

/// <summary>Sample values for previews of templates in Settings → Notifications.</summary>
public static class NotificationSamples
{
    /// <summary>A value for every placeholder; texts in both languages from the translation files.</summary>
    public static IReadOnlyDictionary<string, PlaceholderValue> Values(DateTimeOffset now, PlaceholderValue priority)
    {
        var en = NotificationTexts.For(Zelvo.Data.Localization.LocalizedText.English).Samples;
        var ar = NotificationTexts.For(Zelvo.Data.Localization.LocalizedText.Arabic).Samples;
        PlaceholderValue Both(string key) => new() { En = en.GetValueOrDefault(key), Ar = ar.GetValueOrDefault(key) };
        return new Dictionary<string, PlaceholderValue>(StringComparer.Ordinal)
        {
            [Placeholders.TicketNumber] = PlaceholderValue.OfText("INC-000123"),
            [Placeholders.TicketTitle] = Both(Placeholders.TicketTitle),
            [Placeholders.TicketCreatedAt] = PlaceholderValue.OfDate(now.AddMinutes(-5)),
            [Placeholders.TicketResolutionNotes] = Both(Placeholders.TicketResolutionNotes),
            [Placeholders.TicketPriority] = priority,
            [Placeholders.TicketGroup] = Both(Placeholders.TicketGroup),
            [Placeholders.RequesterName] = Both(Placeholders.RequesterName),
            [Placeholders.UpdateText] = Both(Placeholders.UpdateText),
            [Placeholders.UpdatePostedAt] = PlaceholderValue.OfDate(now),
            [Placeholders.SlaTarget] = PlaceholderValue.OfTerm("sla.target.response"),
            [Placeholders.SlaPercent] = PlaceholderValue.OfPercent(75),
            [Placeholders.SlaDueAt] = PlaceholderValue.OfDate(now.AddHours(2)),
            [Placeholders.ArticleNumber] = PlaceholderValue.OfText("KB-000042"),
            [Placeholders.ArticleTitle] = Both(Placeholders.ArticleTitle),
            [Placeholders.AuthorName] = Both(Placeholders.AuthorName),
            [Placeholders.TicketType] = PlaceholderValue.OfTerm("ticket.type.Complaint"),
            [Placeholders.ReviewState] = PlaceholderValue.OfTerm("review.Accepted"),
            [Placeholders.ReviewFeedback] = Both(Placeholders.ReviewFeedback),
            [Placeholders.ServiceName] = Both(Placeholders.ServiceName),
            [Placeholders.ApprovalLevel] = Both(Placeholders.ApprovalLevel),
            [Placeholders.ApprovalReason] = Both(Placeholders.ApprovalReason),
            [Placeholders.TaskName] = Both(Placeholders.TaskName),
            [Placeholders.TaskDueAt] = PlaceholderValue.OfDate(now.AddDays(1)),
            [Placeholders.OfferExpiresAt] = PlaceholderValue.OfDate(now.AddDays(14)),
        };
    }
}
