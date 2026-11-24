using System.Text.RegularExpressions;
using Zelvo.Data.Localization;
using Zelvo.Notifications;

namespace Zelvo.Api.Tests;

/// <summary>The notification renderer, template syntax and Zelvo's default wording (task B5; LANG-6, LANG-8, GOV-8).</summary>
public sealed class NotificationRendererTests
{
    private static readonly TimeZoneInfo Dubai = TimeZoneInfo.FindSystemTimeZoneById("Asia/Dubai");
    private static readonly DateTimeOffset Moment = new(2026, 11, 24, 6, 30, 0, TimeSpan.Zero); // 10:30 in Dubai.
    private static readonly TenantLetterhead Letterhead = new(new LocalizedText("Demo Municipality", "البلدية التجريبية"), "demo");
    private static readonly NotificationLinks Links = new(new Uri("https://support.example.ae/ar/my-incidents/1"), new Uri("https://support.example.ae/ar/account#notifications"));

    private static RecipientFormat Format(string language, CalendarSystem calendar = CalendarSystem.Gregorian, DigitSystem digits = DigitSystem.Western) =>
        new(language, calendar, digits, Dubai);

    [Fact]
    public void Dates_follow_the_recipients_language_calendar_and_digits_and_name_their_calendar()
    {
        Assert.Equal("24 November 2026, 10:30 AM", NotificationRenderer.FormatDate(Moment, Format("en"), NotificationTexts.For("en").Dates));
        Assert.Equal("24 نوفمبر 2026م، 10:30 ص", NotificationRenderer.FormatDate(Moment, Format("ar"), NotificationTexts.For("ar").Dates));
        Assert.Equal("٢٤ نوفمبر ٢٠٢٦م، ١٠:٣٠ ص", NotificationRenderer.FormatDate(Moment, Format("ar", digits: DigitSystem.ArabicIndic), NotificationTexts.For("ar").Dates));

        var hijri = NotificationRenderer.FormatDate(Moment, Format("ar", CalendarSystem.Hijri, DigitSystem.ArabicIndic), NotificationTexts.For("ar").Dates);
        Assert.Matches("^[٠-٩]{1,2} .+ ١٤٤[٧-٨]هـ، ١٠:٣٠ ص$", hijri);
        Assert.Contains(NotificationTexts.For("ar").Dates.HijriMonths, month => hijri.Contains(month, StringComparison.Ordinal));
        Assert.EndsWith(" AH, 10:30 AM", NotificationRenderer.FormatDate(Moment, Format("en", CalendarSystem.Hijri), NotificationTexts.For("en").Dates), StringComparison.Ordinal);
        Assert.Equal("4:05 PM", NotificationRenderer.FormatDate(new DateTimeOffset(2026, 1, 2, 12, 5, 0, TimeSpan.Zero), Format("en"), NotificationTexts.For("en").Dates)[^7..]);
    }

    [Fact]
    public void Numbers_and_percentages_use_the_recipients_digits()
    {
        var texts = NotificationTexts.For("ar");
        Assert.Equal("٧٥٪", NotificationRenderer.FormatValue(PlaceholderValue.OfPercent(75), Format("ar", digits: DigitSystem.ArabicIndic), texts));
        Assert.Equal("75%", NotificationRenderer.FormatValue(PlaceholderValue.OfPercent(75), Format("ar"), texts));
        Assert.Equal("الاستجابة", NotificationRenderer.FormatValue(PlaceholderValue.OfTerm("sla.target.response"), Format("ar"), texts));
        Assert.Equal("Desk", NotificationRenderer.FormatValue(PlaceholderValue.OfText(new LocalizedText("Desk", null)), Format("ar"), texts)); // Falls back to the other language.
        // References stay as written: INC-000123 is not a number to convert.
        Assert.Equal("INC-000123", NotificationRenderer.FormatValue(PlaceholderValue.OfText("INC-000123"), Format("ar", digits: DigitSystem.ArabicIndic), texts));
    }

    [Fact]
    public void Values_are_encoded_isolated_and_laid_out_right_to_left_with_the_letterhead_and_the_Zelvo_footer()
    {
        var template = new TemplateText("بلاغ رقم {{ticket.number}}", "تحية طيبة، {{recipient.name}}\n\nبعنوان «{{ticket.title}}»\nسطر ثانٍ", "«{{ticket.title}}»", "عرض البلاغ");
        var values = new Dictionary<string, PlaceholderValue>(StringComparer.Ordinal)
        {
            [Placeholders.TicketNumber] = PlaceholderValue.OfText("INC-000123"),
            [Placeholders.TicketTitle] = PlaceholderValue.OfText("<script>alert(1)</script>\r\nBcc: x@evil.test"),
            [Placeholders.RecipientName] = PlaceholderValue.OfText("فاطمة"),
        };

        var rendered = NotificationRenderer.Render(template, values, Format("ar"), Letterhead, Links);

        Assert.Equal("بلاغ رقم INC-000123", rendered.Subject);
        Assert.DoesNotContain('\n', rendered.InApp);
        Assert.DoesNotContain("<script>", rendered.Html, StringComparison.Ordinal);
        Assert.Contains("<bdi>&lt;script&gt;alert(1)&lt;/script&gt;", rendered.Html, StringComparison.Ordinal);
        Assert.Contains("<bdi>INC-000123</bdi>", NotificationRenderer.Render(template with { Body = "{{ticket.number}}" }, values, Format("ar"), Letterhead, Links).Html, StringComparison.Ordinal);
        Assert.Contains("<html lang=\"ar\" dir=\"rtl\">", rendered.Html, StringComparison.Ordinal);
        Assert.Contains("text-align:right", rendered.Html, StringComparison.Ordinal);
        Assert.Contains("البلدية التجريبية", rendered.Html, StringComparison.Ordinal);
        Assert.Contains("Demo Municipality", rendered.Html, StringComparison.Ordinal); // Bilingual letterhead.
        Assert.Contains("بتقنية زيلفو", rendered.Html, StringComparison.Ordinal);
        Assert.Contains("بتقنية زيلفو", rendered.Text, StringComparison.Ordinal);
        Assert.Contains("بعنوان &#171;", rendered.Html, StringComparison.Ordinal); // « encoded as a character reference.
        Assert.Contains("<br>", rendered.Html, StringComparison.Ordinal); // Single line breaks inside a paragraph.
        Assert.Contains("href=\"https://support.example.ae/ar/my-incidents/1\"", rendered.Html, StringComparison.Ordinal);
        Assert.StartsWith("البلدية التجريبية\n\nتحية طيبة، فاطمة", rendered.Text, StringComparison.Ordinal);

        var english = NotificationRenderer.Render(template, values, Format("en"), Letterhead, Links);
        Assert.Contains("<html lang=\"en\" dir=\"ltr\">", english.Html, StringComparison.Ordinal);
        Assert.Contains("Powered by Zelvo", english.Html, StringComparison.Ordinal);
    }

    [Fact]
    public void Templates_may_use_only_the_events_placeholders()
    {
        string[] allowed = ["ticket.number", "recipient.name"];
        Assert.Empty(TemplateSyntax.Problems("{{ticket.number}} and {{ recipient.name }}, {single} braces are fine", allowed));
        Assert.Equal(["Unknown placeholder {{ticket.secret}}."], TemplateSyntax.Problems("{{ticket.secret}}", allowed));
        Assert.Single(TemplateSyntax.Problems("{{ticket.number}", allowed));
        Assert.Single(TemplateSyntax.Problems("{{ Ticket Number }}", allowed));
    }

    [Theory]
    [InlineData("en")]
    [InlineData("ar")]
    public void Every_event_has_default_wording_that_uses_only_its_placeholders(string language)
    {
        var texts = NotificationTexts.For(language);
        foreach (var definition in NotificationEvents.All)
        {
            var template = texts.Templates[definition.Type];
            foreach (var field in new[] { template.Subject, template.Body, template.InApp, template.Action })
            {
                Assert.False(string.IsNullOrWhiteSpace(field), $"{language} {definition.Type}");
                Assert.Empty(TemplateSyntax.Problems(field, [.. definition.AllPlaceholders]));
            }
        }

        Assert.Empty(TemplateSyntax.Problems(texts.Templates[NotificationEvents.TestEmail].Body, [.. NotificationEvents.Common]));
        Assert.Equal(12, texts.Dates.Months.Count);
    }

    [Fact]
    public void Arabic_default_wording_follows_the_formal_correspondence_guidelines()
    {
        // docs/guidelines/arabic-formal-correspondence.md (GOV-8): no exclamation marks or emojis, no
        // tatweel, Arabic punctuation, "المتعامل" rather than "العميل", no dialect, approved terms.
        var arabic = NotificationTexts.For("ar");
        var all = string.Join("\n", arabic.Templates.Values.SelectMany(t => new[] { t.Subject, t.Body, t.InApp, t.Action }));
        Assert.DoesNotContain('!', all);
        Assert.DoesNotContain('ـ', all);
        Assert.DoesNotMatch(new Regex(@"\p{So}"), all);
        Assert.DoesNotContain(',', all.Replace("{{", "", StringComparison.Ordinal));
        Assert.DoesNotContain("العميل", all, StringComparison.Ordinal);
        foreach (var dialect in new[] { "لو سمحت", "الحين", "شلون", "وايد" })
        {
            Assert.DoesNotContain(dialect, all, StringComparison.Ordinal);
        }

        Assert.Equal("بتقنية زيلفو", arabic.Layout.PoweredBy);
        Assert.Contains("مع خالص التحية،", arabic.Templates[NotificationEvents.IncidentReceived].Body, StringComparison.Ordinal);
    }
}
