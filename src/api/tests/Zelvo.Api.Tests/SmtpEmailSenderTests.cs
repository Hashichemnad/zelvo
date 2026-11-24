using System.Net.Http.Json;
using System.Text.Json.Nodes;
using DotNet.Testcontainers.Builders;
using Zelvo.Data.Localization;
using Zelvo.Notifications;

namespace Zelvo.Api.Tests;

/// <summary>The SMTP sender (task B5) against a real mail server: Mailpit, as in Compose.</summary>
public sealed class SmtpEmailSenderTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_Arabic_notification_arrives_as_text_and_HTML_with_its_language_and_marked_automatic()
    {
        await using var mailpit = new ContainerBuilder(Environment.GetEnvironmentVariable("ZELVO_TEST_MAILPIT_IMAGE") ?? "axllent/mailpit:v1.34")
            .WithPortBinding(1025, true)
            .WithPortBinding(8025, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(8025).ForPath("/readyz")))
            .Build();
        await mailpit.StartAsync(Ct);
        var options = new NotificationOptions
        {
            Smtp = new SmtpOptions { Host = mailpit.Hostname, Port = mailpit.GetMappedPublicPort(1025), Security = SmtpSecurity.None, From = "no-reply@zelvo.test" },
        };
        var template = NotificationTexts.For(LocalizedText.Arabic).Templates[NotificationEvents.IncidentReceived];
        var values = new Dictionary<string, PlaceholderValue>(StringComparer.Ordinal)
        {
            [Placeholders.TicketNumber] = PlaceholderValue.OfText("INC-000042"),
            [Placeholders.TicketTitle] = PlaceholderValue.OfText("الطابعة لا تعمل"),
            [Placeholders.TicketCreatedAt] = PlaceholderValue.OfDate(DateTimeOffset.UtcNow),
            [Placeholders.RecipientName] = PlaceholderValue.OfText("فاطمة الشامسي"),
            [Placeholders.TenantName] = PlaceholderValue.OfText(new LocalizedText("Demo Municipality", "البلدية التجريبية")),
        };
        var rendered = NotificationRenderer.Render(template, values,
            new RecipientFormat(LocalizedText.Arabic, CalendarSystem.Hijri, DigitSystem.ArabicIndic, TimeZoneInfo.FindSystemTimeZoneById("Asia/Dubai")),
            new TenantLetterhead(new LocalizedText("Demo Municipality", "البلدية التجريبية"), "demo"),
            new NotificationLinks(new Uri("http://localhost:3000/ar/my-incidents/1"), new Uri("http://localhost:3000/ar/account#notifications")));
        var sender = new SmtpEmailSender(options);

        var result = await sender.SendAsync(new EmailMessage("fatima@example.test", "البلدية التجريبية", rendered.Subject, rendered.Html, rendered.Text, "ar", "zelvo.test.1@zelvo.test"), Ct);

        Assert.True(result.Sent, result.Error);
        using var http = new HttpClient { BaseAddress = new Uri($"http://{mailpit.Hostname}:{mailpit.GetMappedPublicPort(8025)}") };
        var list = (await http.GetFromJsonAsync<JsonObject>("/api/v1/messages", Ct))!;
        var summary = Assert.Single(list["messages"]!.AsArray())!;
        Assert.Equal("بلاغ رقم INC-000042: تم استلام بلاغكم", (string?)summary["Subject"]);
        Assert.Equal("البلدية التجريبية", (string?)summary["From"]!["Name"]);
        var id = (string)summary["ID"]!;
        var message = (await http.GetFromJsonAsync<JsonObject>($"/api/v1/message/{id}", Ct))!;
        Assert.Contains("تحية طيبة، فاطمة الشامسي", (string?)message["Text"], StringComparison.Ordinal);
        Assert.Contains("dir=\"rtl\"", (string?)message["HTML"], StringComparison.Ordinal);
        Assert.Matches("هـ، [٠-٩]", (string?)message["Text"]); // The Hijri date in Arabic-Indic digits.
        var headers = (await http.GetFromJsonAsync<JsonObject>($"/api/v1/message/{id}/headers", Ct))!;
        Assert.Equal("auto-generated", (string?)headers["Auto-Submitted"]![0]);
        Assert.Equal("ar", (string?)headers["Content-Language"]![0]);
        Assert.Equal("<zelvo.test.1@zelvo.test>", (string?)headers["Message-Id"]?[0] ?? (string?)headers["Message-ID"]![0]);
    }

    [Fact]
    public async Task An_unreachable_server_is_tried_again_and_an_address_that_does_not_parse_is_not()
    {
        var sender = new SmtpEmailSender(new NotificationOptions
        {
            Smtp = new SmtpOptions { Host = "127.0.0.1", Port = 1, Security = SmtpSecurity.None, Timeout = TimeSpan.FromSeconds(2) },
        });
        var message = new EmailMessage("someone@example.test", "Zelvo", "Subject", "<p>Hi</p>", "Hi", "en", "zelvo.test.2@zelvo.test");

        var unreachable = await sender.SendAsync(message, Ct);
        Assert.Equal((false, false), (unreachable.Sent, unreachable.Permanent));

        var malformed = await sender.SendAsync(message with { To = "not an address" }, Ct);
        Assert.Equal((false, true), (malformed.Sent, malformed.Permanent));

        var unset = await new SmtpEmailSender(new NotificationOptions()).SendAsync(message, Ct);
        Assert.Equal((false, true), (unset.Sent, unset.Permanent));
        Assert.Contains("Notifications:Smtp:Host", unset.Error, StringComparison.Ordinal);
    }
}
